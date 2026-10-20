// ehentai.go：E-Hentai（表站 e-hentai.org，免登录）客户端。
//
// 两条腿：
//  1. 搜索 → HTML 抓取（/  ?f_search=...&advsearch=1&f_sfu=on&f_sft=on&f_sfl=on），拿 gid/token；
//  2. 元数据 → JSON API（POST api.e-hentai.org/api.php，method=gdata，namespace=1）拿全字段。
//
// 实测（见 docs/research/source-probes.md）：gdata 免鉴权；rating/posted/filecount/filesize 都是字符串
// （画廊被删时会退化成裸数字，故用 ehStr 宽容接收）；一次最多 25 个 gid；tags 自带 "namespace:value" 前缀。
// 表站在墙内直连不通，必须经代理。
package sources

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"html"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

// EhSource E-Hentai 源标识（MatchOutcome/ScrapeOutcome 用）。
const EhSource = "EH"

const (
	ehDefaultBase = "https://e-hentai.org"
	ehGDataURL    = "https://api.e-hentai.org/api.php"
	// EhMaxBatch gdata 一次最多查询 25 个 gid（服务端硬限制）。
	EhMaxBatch = 25
	// EhDefaultPaceMs 表站礼貌间隔：两次请求起始的最小间隔（EH 对爬取比较敏感）。
	EhDefaultPaceMs = 1100
	// ehTimeout 单次请求超时。故意压到 15s：墙内直连必然超时，别让整库刮削被拖死。
	ehTimeout = 15 * time.Second
	ehMaxBody = 8 << 20
	ehUA      = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
	// ehCircuitFails / ehCircuitCooldown：连接层连续失败到阈值就熔断一段时间。
	// 一个客户端 = 一次刮削任务（api 层每任务构建一次），所以熔断等于「本次任务不再碰 EH」，
	// 避免墙内直连时每个系列都白等 15s（4 次请求/系列 × N 系列会拖到几十分钟）。
	ehCircuitFails    = 2
	ehCircuitCooldown = 3 * time.Minute
)

// EhHit 搜索结果条目（gdata 未调用前的轻量信息）。
type EhHit struct {
	GID      int
	Token    string
	URL      string
	Title    string // 原始标题（含 [汉化组] 等方括号段）
	Category string
	Thumb    string
	Posted   string
	Pages    string
	Uploader string
}

// EhGallery gdata 归一后的画廊详情。
type EhGallery struct {
	GID       int
	Token     string
	URL       string
	Title     string // 英文/罗马字标题
	TitleJpn  string // 日文原名（无则空）
	Category  string
	Thumb     string
	Uploader  string
	Posted    string // unix 秒（字符串，别当数字）
	FileCount string
	Filesize  string
	Rating    string
	Tags      []string // 原始 "namespace:value"
}

// EhClient E-Hentai 客户端：严格串行 + 瞬态 HTTP 错误退避重试（最多 3 次）。
type EhClient struct {
	base    string // 站点根（测试可指向 mock）
	api     string // gdata 端点
	http    *http.Client
	log     Logger
	proxied bool // 是否配了代理（决定连接失败时的提示文案）

	paceMu sync.Mutex
	pace   time.Time

	// 连接层熔断状态（见 ehCircuitFails）。
	circMu    sync.Mutex
	connFails int
	openUntil time.Time

	// MinRequestIntervalMs 两次请求起始的最小间隔；0 = 不节流（mock 测试用），负数 = 用默认值。
	MinRequestIntervalMs int
}

// NewEhClient 创建客户端：baseURL 为空用官方表站；minIntervalMs 传负数用默认节流。
func NewEhClient(p ProxyConfig, baseURL string, log Logger, minIntervalMs int) (*EhClient, error) {
	hc, err := NewHTTPClient(p, ehTimeout, ehUA, "")
	if err != nil {
		return nil, err
	}
	base := strings.TrimRight(strings.TrimSpace(baseURL), "/")
	if base == "" {
		base = ehDefaultBase
	}
	api := ehGDataURL
	// 测试 / 自建反代场景：非默认站点时，gdata 走同一主机的 /api.php。
	if base != ehDefaultBase && base != "http://e-hentai.org" {
		api = base + "/api.php"
	}
	if minIntervalMs < 0 {
		minIntervalMs = EhDefaultPaceMs
	}
	return &EhClient{
		base:                 base,
		api:                  api,
		http:                 hc,
		log:                  log,
		proxied:              p.IsSet(),
		pace:                 time.Now().Add(time.Duration(minIntervalMs) * time.Millisecond),
		MinRequestIntervalMs: minIntervalMs,
	}, nil
}

// Base 站点根（日志/调试用）。
func (c *EhClient) Base() string { return c.base }

// GDataURL gdata 端点（日志/调试用）。
func (c *EhClient) GDataURL() string { return c.api }

func (c *EhClient) scheduleStart() {
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

// ehTransientStatus EH 的瞬态状态码：509 是「带宽超限」，要退避更久。
func ehTransientStatus(status int) bool {
	switch status {
	case 429, 500, 502, 503, 504, 509, 520, 521, 522, 523, 524:
		return true
	}
	return false
}

func ehRetryDelay(attempt int, last error) time.Duration {
	msg := ""
	if last != nil {
		msg = last.Error()
	}
	if strings.Contains(msg, "509") {
		return time.Duration(attempt) * 5 * time.Second
	}
	return time.Duration(attempt) * 1500 * time.Millisecond
}

// reachHint 连接失败时给用户一句可执行的提示（别只丢一个 dial timeout）。
func (c *EhClient) reachHint() string {
	if c.proxied {
		return "已配置代理但仍连不上，请检查代理是否可用"
	}
	return "e-hentai.org 在中国大陆直连不通，请在「设置 → 代理」里配置后重试"
}

// circuitError 熔断中则返回错误（不发任何请求）。
func (c *EhClient) circuitError() error {
	c.circMu.Lock()
	until := c.openUntil
	c.circMu.Unlock()
	if until.IsZero() || time.Now().After(until) {
		return nil
	}
	return fmt.Errorf("E-Hentai 源已熔断至 %s（连续 %d 次请求失败，本次任务不再重试）：%s",
		until.Format("15:04:05"), ehCircuitFails, c.reachHint())
}

// noteFail 记录一次 HTTP 层失败（连接层或重试耗尽），达到阈值即熔断。
// 只统计「请求本身失败」：gdata 逻辑错误（画廊已删除等）不算，那说明站点是通的。
func (c *EhClient) noteFail() {
	c.circMu.Lock()
	c.connFails++
	opened := false
	if c.connFails >= ehCircuitFails && time.Now().After(c.openUntil) {
		c.openUntil = time.Now().Add(ehCircuitCooldown)
		opened = true
	}
	until, fails := c.openUntil, c.connFails
	c.circMu.Unlock()
	if opened && c.log != nil {
		c.log.Warn(fmt.Sprintf("E-Hentai 连续失败 %d 次，本次任务熔断至 %s", fails, until.Format("15:04:05")))
	}
}

// noteOK 成功一次即清零熔断计数。
func (c *EhClient) noteOK() {
	c.circMu.Lock()
	c.connFails = 0
	c.openUntil = time.Time{}
	c.circMu.Unlock()
}

// doText GET 一个页面，返回正文与最终 URL（跟随跳转后，用于补 token）。
//
// 重试策略（重要）：只有「瞬态 HTTP 状态码」才重试（最多 3 次）；
// 连接层错误（墙内直连 e-hentai.org 必然是 dial timeout）**不重试**——
// 否则每个系列都要白等 3 个超时，把整库刮削拖死。调用方另有一层「本次任务内熔断」。
func (c *EhClient) doText(ctx context.Context, u string) (string, string, error) {
	if err := c.circuitError(); err != nil {
		return "", "", err
	}
	var last error
	for attempt := 1; attempt <= 3; attempt++ {
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
		if err != nil {
			return "", "", err
		}
		req.Header.Set("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")
		req.Header.Set("Accept-Language", "en-US,en;q=0.9")
		c.scheduleStart()
		resp, err := c.http.Do(req)
		if err == nil {
			final := u
			if resp.Request != nil && resp.Request.URL != nil {
				final = resp.Request.URL.String()
			}
			switch {
			case ehTransientStatus(resp.StatusCode):
				io.Copy(io.Discard, io.LimitReader(resp.Body, 1024))
				resp.Body.Close()
				last = fmt.Errorf("E-Hentai 请求失败 %d：%s", resp.StatusCode, u)
			case resp.StatusCode >= 300:
				io.Copy(io.Discard, io.LimitReader(resp.Body, 1024))
				resp.Body.Close()
				c.noteFail()
				return "", "", fmt.Errorf("E-Hentai 请求失败 %d：%s", resp.StatusCode, u)
			default:
				data, readErr := io.ReadAll(io.LimitReader(resp.Body, ehMaxBody))
				resp.Body.Close()
				if readErr != nil {
					last = readErr
				} else {
					c.noteOK()
					return decodeHTML(data), final, nil
				}
			}
		} else {
			// 连接层错误：直接返回，不重试（见上方注释）。
			c.noteFail()
			return "", "", fmt.Errorf("E-Hentai 连接失败（%s）：%w", c.reachHint(), err)
		}
		if attempt < 3 {
			if c.log != nil {
				c.log.Warn(fmt.Sprintf("E-Hentai 请求重试 %d/3：%v", attempt, last))
			}
			select {
			case <-ctx.Done():
				return "", "", ctx.Err()
			case <-time.After(ehRetryDelay(attempt, last)):
			}
		}
	}
	if last == nil {
		last = fmt.Errorf("E-Hentai 请求失败：%s", u)
	}
	c.noteFail()
	return "", "", last
}

// postJSON POST JSON 并读回响应体（同样的重试策略）。
func (c *EhClient) postJSON(ctx context.Context, u string, payload any) ([]byte, error) {
	if err := c.circuitError(); err != nil {
		return nil, err
	}
	buf, err := json.Marshal(payload)
	if err != nil {
		return nil, err
	}
	var last error
	for attempt := 1; attempt <= 3; attempt++ {
		req, err := http.NewRequestWithContext(ctx, http.MethodPost, u, bytes.NewReader(buf))
		if err != nil {
			return nil, err
		}
		req.Header.Set("Content-Type", "application/json")
		req.Header.Set("Accept", "application/json")
		c.scheduleStart()
		resp, err := c.http.Do(req)
		if err == nil {
			data, readErr := io.ReadAll(io.LimitReader(resp.Body, ehMaxBody))
			resp.Body.Close()
			switch {
			case resp.StatusCode == http.StatusOK && readErr == nil:
				c.noteOK()
				return data, nil
			case ehTransientStatus(resp.StatusCode):
				last = fmt.Errorf("E-Hentai 元数据接口失败 %d", resp.StatusCode)
			case resp.StatusCode != http.StatusOK:
				c.noteFail()
				return nil, fmt.Errorf("E-Hentai 元数据接口失败 %d：%s", resp.StatusCode, ehSnippet(data))
			case readErr != nil:
				last = readErr
			}
		} else {
			c.noteFail()
			return nil, fmt.Errorf("E-Hentai 元数据接口连接失败（%s）：%w", c.reachHint(), err)
		}
		if attempt < 3 {
			if c.log != nil {
				c.log.Warn(fmt.Sprintf("E-Hentai 元数据接口重试 %d/3：%v", attempt, last))
			}
			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			case <-time.After(ehRetryDelay(attempt, last)):
			}
		}
	}
	if last == nil {
		last = fmt.Errorf("E-Hentai 元数据接口失败：%s", u)
	}
	c.noteFail()
	return nil, last
}

func ehSnippet(b []byte) string {
	s := strings.TrimSpace(string(b))
	if len(s) > 200 {
		s = s[:200] + "…"
	}
	return s
}

// Search 关键词搜索（表站，第一页）；返回 ≤limit 条。
// 走 f_search + advsearch=1 + f_sfu/f_sft/f_sfl=on。
// 也支持标签语法（"artist:asanagi"）与引号短语，见 SearchTag。
func (c *EhClient) Search(ctx context.Context, term string, limit int) ([]EhHit, error) {
	term = strings.TrimSpace(term)
	if term == "" {
		return nil, nil
	}
	if limit <= 0 || limit > EhMaxBatch {
		limit = EhMaxBatch
	}
	q := url.Values{}
	q.Set("f_search", term)
	q.Set("advsearch", "1")
	q.Set("f_sfu", "on")
	q.Set("f_sft", "on")
	q.Set("f_sfl", "on")
	page, _, err := c.doText(ctx, c.base+"/?"+q.Encode())
	if err != nil {
		return nil, err
	}
	hits := parseEhSearch(page)
	if len(hits) == 0 {
		// 把「不是没搜到，而是被拦/需要确认」的情况说清楚，别让用户以为是标题问题。
		switch {
		case strings.Contains(page, "Content Warning"):
			return nil, fmt.Errorf("E-Hentai 返回了内容警告页：需要先在浏览器打开 e-hentai.org 确认一次（或在站点设置里关闭该提示）")
		case strings.Contains(page, "temporarily banned") || strings.Contains(page, "Your IP address has been"):
			return nil, fmt.Errorf("E-Hentai 提示 IP 被临时限制：请降低频率或更换代理出口")
		}
	}
	if len(hits) > limit {
		hits = hits[:limit]
	}
	return hits, nil
}

// Candidates 搜索并转成跨源候选（Title=清洗后标题，Aliases=原始标题，Author=首段方括号）。
func (c *EhClient) Candidates(ctx context.Context, term string, limit int) ([]Candidate, error) {
	hits, err := c.Search(ctx, term, limit)
	if err != nil {
		return nil, err
	}
	return ehHitsToCandidates(hits), nil
}

// SearchTag 标签语法搜索：f_search="artist:asanagi" / "parody:azur lane" / "language:chinese"。
// 🟢 实测（2026-10-20）：远优于纯文本搜索——`artist:asanagi` 只回该作者的画廊，
// 而纯文本 `asanagi` 首条是无关的 Misaya 作品。同人志自动匹配靠它兜精度。
func (c *EhClient) SearchTag(ctx context.Context, ns, tag string, limit int) ([]EhHit, error) {
	ns = strings.TrimSpace(ns)
	tag = strings.TrimSpace(tag)
	if ns == "" || tag == "" {
		return nil, nil
	}
	return c.Search(ctx, ns+":"+tag, limit)
}

// CandidatesTag 标签语法搜索 + 转候选。
func (c *EhClient) CandidatesTag(ctx context.Context, ns, tag string, limit int) ([]Candidate, error) {
	hits, err := c.SearchTag(ctx, ns, tag, limit)
	if err != nil {
		return nil, err
	}
	return ehHitsToCandidates(hits), nil
}

func ehHitsToCandidates(hits []EhHit) []Candidate {
	out := make([]Candidate, 0, len(hits))
	for _, h := range hits {
		out = append(out, Candidate{
			Title:    cleanEhTitle(h.Title),
			Aliases:  h.Title,
			Author:   ehAuthorOf(h.Title),
			CoverURL: h.Thumb,
			Source:   EhSource,
			EhID:     EhID(h.GID, h.Token),
		})
	}
	return out
}

type ehGDataResp struct {
	GMetadata []ehGDataItem `json:"gmetadata"`
	Error     string        `json:"error"`
}

// ehStr 宽容字符串：gdata 大多数时候给字符串，但画廊被删/被 expunge 时会退化成裸数字
// （🟢 实测 filesize 返回 0），所以这些字段统一用 ehStr 接收。
type ehStr string

func (s *ehStr) UnmarshalJSON(b []byte) error {
	t := strings.TrimSpace(string(b))
	switch {
	case t == "", t == "null":
		*s = ""
	case strings.HasPrefix(t, `"`):
		var v string
		if err := json.Unmarshal(b, &v); err != nil {
			return err
		}
		*s = ehStr(v)
	default:
		*s = ehStr(t)
	}
	return nil
}

func (s ehStr) String() string { return strings.TrimSpace(string(s)) }

type ehGDataItem struct {
	GID       int      `json:"gid"`
	Token     ehStr    `json:"token"`
	Title     ehStr    `json:"title"`
	TitleJpn  ehStr    `json:"title_jpn"`
	Category  ehStr    `json:"category"`
	Thumb     ehStr    `json:"thumb"`
	Uploader  ehStr    `json:"uploader"`
	Posted    ehStr    `json:"posted"`
	FileCount ehStr    `json:"filecount"`
	Filesize  ehStr    `json:"filesize"`
	Rating    ehStr    `json:"rating"`
	Tags      []string `json:"tags"`
	Error     ehStr    `json:"error"`
}

// GetGallery 取单个画廊元数据（id 形如 "123/abcdef" 或完整画廊链接）。
func (c *EhClient) GetGallery(ctx context.Context, id string) (*EhGallery, error) {
	gid, token, ok := ParseEhID(id)
	if !ok {
		return nil, fmt.Errorf("E-Hentai 画廊标识无效：%q（应为 gid/token 或画廊链接）", id)
	}
	if token == "" {
		t, err := c.resolveToken(ctx, gid)
		if err != nil {
			return nil, err
		}
		token = t
	}
	payload := map[string]any{
		"method":    "gdata",
		"gidlist":   []any{[]any{gid, token}},
		"namespace": 1,
	}
	raw, err := c.postJSON(ctx, c.api, payload)
	if err != nil {
		return nil, err
	}
	var resp ehGDataResp
	if err := json.Unmarshal(raw, &resp); err != nil {
		return nil, fmt.Errorf("解析 E-Hentai 元数据失败：%w", err)
	}
	if resp.Error != "" {
		return nil, fmt.Errorf("E-Hentai 返回错误：%s", ehErrText(resp.Error))
	}
	if len(resp.GMetadata) == 0 {
		return nil, fmt.Errorf("E-Hentai 未返回该画廊元数据（gid %d，可能已删除或 token 失效）", gid)
	}
	it := resp.GMetadata[0]
	if it.Error.String() != "" {
		return nil, fmt.Errorf("E-Hentai 返回错误：%s", ehErrText(it.Error.String()))
	}
	if it.Token.String() != "" {
		token = it.Token.String()
	}
	return &EhGallery{
		GID:       gid,
		Token:     token,
		URL:       fmt.Sprintf("%s/g/%d/%s/", c.base, gid, token),
		Title:     html.UnescapeString(it.Title.String()),
		TitleJpn:  html.UnescapeString(it.TitleJpn.String()),
		Category:  strings.ToLower(it.Category.String()),
		Thumb:     it.Thumb.String(),
		Uploader:  it.Uploader.String(),
		Posted:    it.Posted.String(),
		FileCount: it.FileCount.String(),
		Filesize:  it.Filesize.String(),
		Rating:    it.Rating.String(),
		Tags:      it.Tags,
	}, nil
}

// resolveToken 只给了 gid 时，抓画廊页并从跳转后 URL / 正文里补出 token。
func (c *EhClient) resolveToken(ctx context.Context, gid int) (string, error) {
	page, final, err := c.doText(ctx, fmt.Sprintf("%s/g/%d/", c.base, gid))
	if err != nil {
		return "", err
	}
	for _, s := range []string{final, page} {
		if m := ehGalleryTokenRe.FindStringSubmatch(s); m != nil {
			return m[2], nil
		}
	}
	return "", fmt.Errorf("无法解析 E-Hentai 画廊 token（gid %d）：画廊可能已删除，或该内容需要登录（本项目只支持表站）", gid)
}

// CoverURL 封面地址（EH 的 gdata thumb 已经是 CDN 直链；空则返回 ""）。
func (c *EhClient) CoverURL(g *EhGallery) string {
	if g == nil {
		return ""
	}
	u := strings.TrimSpace(g.Thumb)
	if strings.HasPrefix(u, "http://") || strings.HasPrefix(u, "https://") {
		return u
	}
	return ""
}

// ---- ID / 链接解析 ----

// EhID 组装 "gid/token"。
func EhID(gid int, token string) string {
	if token == "" {
		return strconv.Itoa(gid)
	}
	return strconv.Itoa(gid) + "/" + strings.TrimSpace(token)
}

var (
	ehTokenRe        = regexp.MustCompile(`^[0-9a-fA-F]+$`)
	ehGalleryURLRe   = regexp.MustCompile(`(?i)^(?:https?:)?(?://)?(?:www\.)?(?:e-hentai\.org|exhentai\.org|e-hentai\.com)/g/(\d+)(?:/([0-9a-f]+))?`)
	ehGalleryTokenRe = regexp.MustCompile(`(?i)/g/(\d+)/([0-9a-f]+)`)
	ehRowLinkRe      = regexp.MustCompile(`(?is)<a\s+href="(https?://[^"]*/g/(\d+)/([0-9a-f]+)/?)"[^>]*>\s*<div class="glink">(.*?)</div>`)
	ehCategoryRe     = regexp.MustCompile(`(?is)class="cn[^"]*"[^>]*>\s*([^<]+?)\s*<`)
	ehPagesRe        = regexp.MustCompile(`(?is)>\s*(\d+)\s+pages?\s*<`)
	ehUploaderRe     = regexp.MustCompile(`(?is)<a href="https?://[^"]*/uploader/[^"]*">\s*([^<]+?)\s*</a>`)
	ehThumbRe        = regexp.MustCompile(`(?is)<img[^>]+(?:src|data-src)="(https?://[^"]+/w/[^"]+)"`)
	ehBracketRe      = regexp.MustCompile(`\[[^\]]*\]`)
	ehEventRe        = regexp.MustCompile(`(?i)\(\s*(?:c\d{2,3}|comiket[^)]*|reitaisai[^)]*|例大祭[^)]*|sunshine creation[^)]*|hakurei[^)]*)\s*\)`)
	ehSpaceRe        = regexp.MustCompile(`\s+`)
	ehAuthorPrefixRe = regexp.MustCompile(`^\s*\[([^\]]+)\]`)
)

// ParseEhLink 从画廊链接里解出 gid/token（支持 http(s)://、协议相对、裸域名；表站与里站域名）。
func ParseEhLink(u string) (int, string, bool) {
	s := strings.TrimSpace(u)
	if s == "" {
		return 0, "", false
	}
	m := ehGalleryURLRe.FindStringSubmatch(s)
	if m == nil {
		return 0, "", false
	}
	gid, err := strconv.Atoi(m[1])
	if err != nil || gid <= 0 {
		return 0, "", false
	}
	return gid, m[2], true
}

// IsEhGalleryLink 是否是 E-Hentai / ExHentai 的画廊链接（只认带域名的形态，避免把纯数字当画廊）。
func IsEhGalleryLink(u string) bool {
	_, _, ok := ParseEhLink(u)
	return ok
}

// ParseEhID 解析内部 id（"123/abcdef"、"123"，也容忍直接贴完整链接）。
func ParseEhID(id string) (int, string, bool) {
	s := strings.TrimSpace(id)
	if s == "" {
		return 0, "", false
	}
	if gid, token, ok := ParseEhLink(s); ok {
		return gid, token, true
	}
	s = strings.Trim(s, "/")
	parts := strings.Split(s, "/")
	if len(parts) == 0 || len(parts) > 2 {
		return 0, "", false
	}
	gid, err := strconv.Atoi(strings.TrimSpace(parts[0]))
	if err != nil || gid <= 0 {
		return 0, "", false
	}
	token := ""
	if len(parts) == 2 {
		token = strings.TrimSpace(parts[1])
		if token != "" && !ehTokenRe.MatchString(token) {
			return 0, "", false
		}
	}
	return gid, token, true
}

// SplitEhTags 把 "namespace:value" 列表按命名空间分组（命名空间小写；无冒号的归到 ""）。
func SplitEhTags(tags []string) map[string][]string {
	out := make(map[string][]string, 12)
	for _, t := range tags {
		t = strings.TrimSpace(t)
		if t == "" {
			continue
		}
		ns := ""
		val := t
		if i := strings.Index(t, ":"); i > 0 {
			ns = strings.ToLower(strings.TrimSpace(t[:i]))
			val = strings.TrimSpace(t[i+1:])
		}
		if val == "" {
			continue
		}
		out[ns] = append(out[ns], val)
	}
	return out
}

// ---- 搜索页解析 ----

func parseEhSearch(page string) []EhHit {
	rows := strings.Split(page, "<tr")
	out := make([]EhHit, 0, 25)
	seen := make(map[int]bool, 25)
	for _, row := range rows {
		m := ehRowLinkRe.FindStringSubmatch(row)
		if m == nil {
			continue
		}
		gid, err := strconv.Atoi(m[2])
		if err != nil || gid <= 0 || seen[gid] {
			continue
		}
		seen[gid] = true
		link := m[1]
		if !strings.HasSuffix(link, "/") {
			link += "/"
		}
		h := EhHit{
			GID:   gid,
			Token: m[3],
			URL:   link,
			Title: ehSpaceRe.ReplaceAllString(html.UnescapeString(strings.TrimSpace(m[4])), " "),
		}
		if cm := ehCategoryRe.FindStringSubmatch(row); cm != nil {
			h.Category = strings.ToLower(strings.TrimSpace(html.UnescapeString(cm[1])))
		}
		if pm := ehPagesRe.FindStringSubmatch(row); pm != nil {
			h.Pages = pm[1]
		}
		if um := ehUploaderRe.FindStringSubmatch(row); um != nil {
			h.Uploader = html.UnescapeString(strings.TrimSpace(um[1]))
		}
		if tm := ehThumbRe.FindStringSubmatch(row); tm != nil {
			h.Thumb = tm[1]
		}
		out = append(out, h)
	}
	return out
}

// cleanEhTitle 剥掉方括号段与会场标记，得到「核心标题」——用于和文件名派生的查询词比对。
//
// 例："(C100) [kibun tenkan (Eru Takagaki)] untitled (THE iDOLM@STER: Shiny Colors)"
//
//	→ "untitled (THE iDOLM@STER: Shiny Colors)"
func cleanEhTitle(s string) string {
	s = html.UnescapeString(strings.TrimSpace(s))
	s = ehBracketRe.ReplaceAllString(s, " ")
	s = ehEventRe.ReplaceAllString(s, " ")
	s = strings.Join(strings.Fields(s), " ")
	return strings.Trim(s, " -_|·")
}

// ehAuthorOf 取标题首个方括号段当作者/社团（EH 约定 [circle (artist)]）。
func ehAuthorOf(title string) string {
	m := ehAuthorPrefixRe.FindStringSubmatch(strings.TrimSpace(title))
	if m == nil {
		return ""
	}
	return strings.TrimSpace(m[1])
}

// ehErrText 把 gdata 的英文错误串翻成人话（保留原文便于排查）。
func ehErrText(s string) string {
	s = strings.TrimSpace(s)
	switch {
	case s == "":
		return "未知错误"
	case strings.Contains(s, "Invalid JSON"), strings.Contains(s, "invalid"):
		return "请求格式被拒绝（" + s + "）"
	case strings.Contains(s, "Key missing"):
		return "缺少 token（" + s + "）"
	}
	return s
}
