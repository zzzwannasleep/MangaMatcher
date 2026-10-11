package sources

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"strings"
	"sync"
	"time"

	"komgabangumi/internal/text"
)

// MoxSeries Mox（bookof.moe）系列详情：数据本身就是中文（繁体站，已统一转简体）。
type MoxSeries struct {
	ID            string // BoF 内部 id（bookof.moe/b/{id}.htm）
	Title         string // 主标题（简体）
	OriTitle      string // 原名（繁体）
	EngName       string // 英文名（可空）
	Summary       string // 简介（简体）
	Authors       []string
	Status        string // ONGOING/ENDED/HIATUS/ABANDONED
	Tags          []string
	Publisher     string
	IsAdult       bool
	CoverFrameURL string // data_vol.php 帧页 URL（解析每卷封面用，可空）
}

// BoFURL 详情页链接。
func (m *MoxSeries) BoFURL() string { return "https://bookof.moe/b/" + m.ID + ".htm" }

// MoxURL 阅读页链接。
func (m *MoxSeries) MoxURL() string { return "https://kox.moe/c/" + m.ID + ".htm" }

// MoxSearchResult Mox 搜索结果（datainfo-B 行自带封面 URL）。
type MoxSearchResult struct {
	ID       string
	Title    string
	Author   string
	CoverURL string
}

// MoxClient Mox（bookof.moe）中文漫画库客户端。
// 站内请求严格串行（500ms 礼貌间隔）+ 520/521/522/523/524/429/502/503/504 退避重试（最多 3 次）。
type MoxClient struct {
	base   string
	http   *http.Client
	log    Logger
	paceMu sync.Mutex
	pace   time.Time
	// MinRequestIntervalMs 对 bookof.moe 的礼貌节流（两次请求起始的最小间隔）。mock 测试设 0。
	MinRequestIntervalMs int
}

// Logger 客户端日志接口（scrape 包的 slog 适配）。
type Logger interface {
	Warn(msg string)
	Info(msg string)
	Error(msg string)
}

// NewMoxClient 创建 Mox 客户端（baseURL 为空用官方站；测试指向 mock）。
func NewMoxClient(p ProxyConfig, baseURL string, log Logger, minIntervalMs int) (*MoxClient, error) {
	hc, err := NewHTTPClient(p, 30*time.Second,
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36", "")
	if err != nil {
		return nil, err
	}
	if baseURL == "" {
		baseURL = "https://bookof.moe"
	}
	if minIntervalMs < 0 {
		minIntervalMs = 500
	}
	return &MoxClient{base: strings.TrimRight(baseURL, "/"), http: hc, log: log,
		pace: time.Now().Add(time.Duration(minIntervalMs) * time.Millisecond), MinRequestIntervalMs: minIntervalMs}, nil
}

func (c *MoxClient) scheduleStart() {
	if c.MinRequestIntervalMs <= 0 {
		return
	}
	c.paceMu.Lock()
	now := time.Now()
	start := now
	if c.pace.After(now) {
		start = c.pace
	}
	c.pace = start.Add(time.Duration(c.MinRequestIntervalMs) * time.Millisecond)
	c.paceMu.Unlock()
	if d := time.Until(start); d > 0 {
		time.Sleep(d)
	}
}

// GetText 站内请求：严格串行 + 瞬态错误退避重试（最多 3 次）；404 等确定性失败不重试。
func (c *MoxClient) GetText(ctx context.Context, url string) (string, error) {
	var last error
	for attempt := 1; attempt <= 3; attempt++ {
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
		if err != nil {
			return "", err
		}
		c.scheduleStart()
		resp, err := c.http.Do(req)
		if err == nil {
			if isTransientStatus(resp.StatusCode) {
				io.Copy(io.Discard, io.LimitReader(resp.Body, 1024))
				resp.Body.Close()
				last = fmt.Errorf("Mox 请求失败 %d: %s", resp.StatusCode, url)
			} else if resp.StatusCode >= 300 {
				io.Copy(io.Discard, io.LimitReader(resp.Body, 1024))
				resp.Body.Close()
				return "", fmt.Errorf("Mox 请求失败 %d: %s", resp.StatusCode, url)
			} else {
				data, readErr := io.ReadAll(io.LimitReader(resp.Body, 4<<20))
				resp.Body.Close()
				if readErr != nil {
					last = readErr
				} else {
					return decodeHTML(data), nil
				}
			}
		} else {
			last = err
		}
		if attempt < 3 {
			delay := time.Duration(attempt) * 1500 * time.Millisecond
			if c.log != nil {
				c.log.Warn(fmt.Sprintf("Mox 请求失败，%.0fs 后重试（第 %d 次）: %v", delay.Seconds(), attempt, last))
			}
			select {
			case <-ctx.Done():
				return "", ctx.Err()
			case <-time.After(delay):
			}
		}
	}
	if last == nil {
		last = fmt.Errorf("Mox 请求失败: %s", url)
	}
	return "", last
}

func isTransientStatus(status int) bool {
	switch status {
	case 429, 502, 503, 504, 520, 521, 522, 523, 524:
		return true
	}
	return false
}

// decodeHTML 老站编码可能是 Big5/GBK：先按 UTF-8 解，出现替换符再降级。
func decodeHTML(b []byte) string {
	s := string(b)
	if !strings.ContainsRune(s, '\uFFFD') {
		return s
	}
	if gb, err := decodeCharset(b, "GBK"); err == nil && !strings.ContainsRune(gb, '\uFFFD') {
		return gb
	}
	if bg, err := decodeCharset(b, "big5"); err == nil && !strings.ContainsRune(bg, '\uFFFD') {
		return bg
	}
	return s
}

// Search 按名称搜索（站内是繁体 → 先 S2T 再查）。结果标题已转回简体。
func (c *MoxClient) Search(ctx context.Context, seriesName string, limit int) ([]MoxSearchResult, error) {
	if limit <= 0 {
		limit = 8
	}
	term := text.S2T(strings.TrimSpace(seriesName))
	html, err := c.GetText(ctx, c.base+"/data_list.php?s="+urlEscape(term)+"&p=1")
	if err != nil {
		return nil, err
	}
	var results []MoxSearchResult
	seen := map[string]bool{}
	for _, chunk := range strings.Split(html, "<script>") {
		for _, line := range strings.Split(chunk, "\n") {
			t := strings.TrimSpace(line)
			if !strings.HasPrefix(t, "datainfo-B=") {
				continue
			}
			body := t[len("datainfo-B="):]
			segs := strings.Split(body, ",")
			if len(segs) < 4 {
				continue
			}
			id := strings.TrimSpace(segs[1])
			title := text.T2S(strings.TrimSpace(segs[2]))
			author := ""
			if len(segs) > 3 {
				author = text.T2S(strings.TrimSpace(segs[3]))
			}
			if id == "" || title == "" || seen[id] {
				continue
			}
			seen[id] = true
			// 第 6 段起是封面 URL（sign 参数可能含逗号 → 从第 5 个逗号后整段截取再抽 URL）
			cover := ""
			if idx := indexOfNthComma(body, 5); idx >= 0 {
				if m := coverURLRe.FindString(body[idx:]); m != "" {
					cover = m
				}
			}
			if author == "" {
				author = "未知作者"
			}
			results = append(results, MoxSearchResult{ID: id, Title: title, Author: author, CoverURL: cover})
			if len(results) >= limit {
				return results, nil
			}
		}
	}
	return results, nil
}

// GetSeries 抓取 BoF 详情页并解析；失败抛错误由调用方兜底。
func (c *MoxClient) GetSeries(ctx context.Context, moxID string) (*MoxSeries, error) {
	url := moxID
	if !strings.HasPrefix(strings.ToLower(moxID), "http") {
		url = c.base + "/b/" + moxID + ".htm"
	}
	idFromURL := moxID
	if m := moxURLIDRe.FindStringSubmatch(url); m != nil {
		idFromURL = m[1]
	}
	html, err := c.GetText(ctx, url)
	if err != nil {
		return nil, err
	}

	// 封面帧页（window.iframe_action.location.href = "..."）
	frameURL := ""
	if m := frameURLRe.FindStringSubmatch(html); m != nil {
		p := strings.TrimSpace(m[1])
		frameURL = p
		if !strings.HasPrefix(strings.ToLower(p), "http") {
			frameURL = c.base + prefixSlash(p)
		}
	}

	// r18 图标存在且未隐藏 → 成人
	isAdult := false
	if m := r18ImgRe.FindStringSubmatch(html); m != nil {
		tag := m[1]
		isAdult = !strings.Contains(strings.ToLower(tag), "display") ||
			!strings.Contains(strings.ReplaceAll(tag, " ", ""), "display:none")
	}

	oriTitle := rawTextOf(html, nameMainRe)
	if oriTitle == "" {
		return nil, fmt.Errorf("Mox 详情页缺少主标题（.name_main），页面结构可能已变化")
	}

	// name_subt：第一个常是 "(英文名) …"，第二个（或第一个）含 狀態/分類/版本 信息
	var subt []string
	for _, m := range nameSubtRe.FindAllStringSubmatch(html, -1) {
		if s := textOf(m[1]); s != "" {
			subt = append(subt, s)
		}
	}
	engName, infoText := "", ""
	if len(subt) > 0 {
		if em := engInfoRe.FindStringSubmatch(subt[0]); em != nil {
			engName = strings.TrimSpace(em[1])
			infoText = strings.TrimSpace(em[2])
		} else {
			infoText = strings.TrimSpace(subt[0])
		}
		if len(subt) > 1 && infoText == "" {
			infoText = strings.TrimSpace(subt[1])
		} else if len(subt) > 1 && strings.Contains(subt[1], "：") {
			if infoText != "" {
				infoText += " " + strings.TrimSpace(subt[1])
			} else {
				infoText = strings.TrimSpace(subt[1])
			}
		}
	}

	// 简介是纯文本（<br> 分行），尾部常带整段【…】备注 → 去掉
	summary := ""
	if m := descRe.FindStringSubmatch(html); m != nil {
		summary = text.T2S(cleanMultiline(trailingDescNoteRe.ReplaceAllString(stripHTML(m[1]), "")))
	}

	var authors []string
	seen := map[string]bool{}
	for _, m := range authorLinkRe.FindAllStringSubmatch(html, -1) {
		a := text.T2S(strings.TrimSpace(bracketSuffixRe.ReplaceAllString(strings.TrimSpace(m[1]), "")))
		if a != "" && !seen[a] {
			seen[a] = true
			authors = append(authors, a)
		}
	}

	status := ""
	if len(subt) > 0 || infoText != "" {
		// T2S 词表会把 狀態 转成 混合形「状態」（態 未收录）→ labels 覆盖 简/繁/混合 三种写法
		if v := infoField(infoText, []string{"狀態：", "状态：", "状態："},
			[]string{"分類：", "分类：", "最後出版：", "最后出版：", " ", "\t"}); v != "" {
			switch v {
			case "连载", "連載":
				status = "ONGOING"
			case "完结", "完結":
				status = "ENDED"
			case "停更", "休刊":
				status = "HIATUS"
			case "弃坑", "放棄":
				status = "ABANDONED"
			}
		}
	}

	var tags []string
	if raw := infoFieldUntil(infoText, []string{"分類：", "分类："},
		[]string{"版本：", "最後出版：", "最后出版：", "状態：", "状态：", "狀態："}); raw != "" {
		seenT := map[string]bool{}
		for _, f := range strings.FieldsFunc(raw, func(r rune) bool { return r == ' ' || r == '\t' || r == '\u3000' }) {
			t := text.T2S(strings.TrimSpace(f))
			if t != "" && !seenT[t] {
				seenT[t] = true
				tags = append(tags, t)
			}
		}
	}
	publisher := ""
	if v := infoField(infoText, []string{"版本："}, []string{"最後出版：", "最后出版：", " ", "\t"}); v != "" {
		publisher = text.T2S(v)
	}

	return &MoxSeries{ID: idFromURL, Title: text.T2S(oriTitle), OriTitle: oriTitle,
		EngName: text.T2S(engName), Summary: summary, Authors: authors, Status: status,
		Tags: tags, Publisher: publisher, IsAdult: isAdult, CoverFrameURL: frameURL}, nil
}

// GetVolumeCoverURLs 抓取封面帧页，解析 datainfo-V 行得到按卷序排列的封面 URL。
func (c *MoxClient) GetVolumeCoverURLs(ctx context.Context, frameURL string) ([]string, error) {
	if strings.TrimSpace(frameURL) == "" {
		return nil, nil
	}
	html, err := c.GetText(ctx, frameURL)
	if err != nil {
		return nil, err
	}
	var covers []string
	for _, line := range strings.Split(html, "\n") {
		t := strings.TrimSpace(line)
		if !strings.HasPrefix(t, "datainfo-V=") {
			continue
		}
		segs := strings.Split(t[len("datainfo-V="):], ",")
		// datainfo-V=ID,标题,类型,未知,封面URL,未知
		if len(segs) < 5 {
			continue
		}
		u := strings.TrimSpace(segs[4])
		if u == "" {
			continue
		}
		if !strings.HasPrefix(strings.ToLower(u), "http") {
			u = c.base + prefixSlash(u)
		}
		dup := false
		for _, x := range covers {
			if x == u {
				dup = true
				break
			}
		}
		if !dup {
			covers = append(covers, u)
		}
	}
	return covers, nil
}

func prefixSlash(p string) string {
	if strings.HasPrefix(p, "/") {
		return p
	}
	return "/" + p
}

func indexOfNthComma(s string, nth int) int {
	idx := -1
	for i := 0; i < nth; i++ {
		next := strings.IndexByte(s[idx+1:], ',')
		if next < 0 {
			return -1
		}
		idx += 1 + next
	}
	return idx
}

var (
	frameURLRe      = regexp.MustCompile(`window\.iframe_action\.location\.href\s*=\s*"([^"]+)"`)
	r18ImgRe        = regexp.MustCompile(`(<img[^>]*id="logo_r18"[^>]*>)`)
	nameMainRe      = regexp.MustCompile(`class="name_main"[^>]*>([^<]+)<`)
	nameSubtRe      = regexp.MustCompile(`class="name_subt"[^>]*>(.*?)<(?:/|\w)`)
	descRe          = regexp.MustCompile(`id="div_desctext"[^>]*>(.*?)</div>`)
	authorLinkRe    = regexp.MustCompile(`(?is)<a[^>]*href="https?://bookof\.moe/s/AUT[^"]*"[^>]*>(.*?)</a>`)
	trailingDescNoteRe = regexp.MustCompile(`\s*【[^】]*】\s*$`)
	bracketSuffixRe = regexp.MustCompile(`[《【（\[\(（][^》】）\]\)）]*[》】）\]\)）]\s*$`)
	coverURLRe      = regexp.MustCompile(`https?://[^\s"]+`)
	moxURLIDRe      = regexp.MustCompile(`bookof\.moe/b/(.*?)\.htm`)
	engInfoRe       = regexp.MustCompile(`\(([^)]+)\)\s*(.*)`)
	htmlTagRe       = regexp.MustCompile(`<[^>]+>`)
)

// infoField 取 infoText 里 label 后到任一 stop 标记/首个空白为止的值（对齐 C# 正则语义）。
// stop 列表里 " " 与 "\t" 表示「遇到空白就停」（值不含空白时使用）。
func infoField(s string, labels, stops []string) string {
	for _, label := range labels {
		i := strings.Index(s, label)
		if i < 0 {
			continue
		}
		rest := s[i+len(label):]
		end := len(rest)
		for _, stop := range stops {
			if j := strings.Index(rest, stop); j >= 0 && j < end {
				end = j
			}
		}
		return strings.TrimSpace(rest[:end])
	}
	return ""
}

// infoFieldUntil 取 label 后到下一 stop 标记为止的整段（值可含空白，分类列表用）。
func infoFieldUntil(s string, labels, stops []string) string {
	for _, label := range labels {
		i := strings.Index(s, label)
		if i < 0 {
			continue
		}
		rest := s[i+len(label):]
		end := len(rest)
		for _, stop := range stops {
			if j := strings.Index(rest, stop); j >= 0 && j < end {
				end = j
			}
		}
		return strings.TrimSpace(rest[:end])
	}
	return ""
}

func stripHTML(s string) string { return htmlTagRe.ReplaceAllString(s, "") }

// textOf 取 HTML 内文 → 去标签 → 转简体 → 清空白。
func textOf(s string) string {
	return text.T2S(cleanMultiline(stripHTML(s)))
}

// rawTextOf 不转简体（保留繁体原样，用于「原名」）。
func rawTextOf(html string, re *regexp.Regexp) string {
	m := re.FindStringSubmatch(html)
	if m == nil {
		return ""
	}
	return strings.TrimSpace(cleanMultiline(stripHTML(m[1])))
}

func cleanMultiline(s string) string {
	s = strings.ReplaceAll(s, "\r\n", "\n")
	s = strings.ReplaceAll(s, "\r", "\n")
	var lines []string
	for _, l := range strings.Split(s, "\n") {
		lines = append(lines, strings.Trim(l, " \t\u3000"))
	}
	return strings.TrimSpace(strings.Join(lines, "\n"))
}

func urlEscape(s string) string {
	var b strings.Builder
	for _, r := range []byte(s) {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '-' || r == '_' || r == '.' || r == '~' {
			b.WriteByte(r)
		} else {
			fmt.Fprintf(&b, "%%%02X", r)
		}
	}
	return b.String()
}
