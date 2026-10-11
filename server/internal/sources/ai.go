package sources

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"strings"
	"sync"
	"time"
)

// IdentifyResult AI 识别结果：日文原名 / 通行中文名（简体）/ 英文或罗马字名。
type IdentifyResult struct {
	Jp        string
	Cn        string
	En        string
	Confident bool
}

// AiClient AI 匹配客户端（OpenAI 兼容 /chat/completions）。
// 用途：①识别作品（jp/cn/en）②智能选择（候选>12 先按 bigram Jaccard 截断）
// ③翻译简介/标题 → 简体中文 ④补写简介。每个用途进程内缓存（同一系列不重复问）。
type AiClient struct {
	base string
	key  string
	mod  string
	http *http.Client
}

// ---- 进程内缓存（对齐 C# static ConcurrentDictionary：同一系列批量里不重复问 AI）----

var aiCacheMu sync.Mutex
var identifyCache = map[string]*IdentifyResult{} // nil = 失败缓存
var titleJpCache = map[string]string{}
var synopsisCache = map[string]string{}
var transSumCache = map[string]string{} // 简介翻译缓存（key=原文）

// ResetAICaches 清空进程内缓存（测试隔离用）。
func ResetAICaches() {
	aiCacheMu.Lock()
	identifyCache = map[string]*IdentifyResult{}
	titleJpCache = map[string]string{}
	synopsisCache = map[string]string{}
	transSumCache = map[string]string{}
	aiCacheMu.Unlock()
}

// NewAiClient 创建 AI 客户端。baseURL 自动补 /v1。
func NewAiClient(baseURL, apiKey, model string, p ProxyConfig, timeout time.Duration) (*AiClient, error) {
	root := strings.TrimRight(baseURL, "/")
	if root != "" && !strings.HasSuffix(root, "/v1") && !strings.HasSuffix(root, "/v1/") {
		root += "/v1"
	}
	if model == "" {
		model = "deepseek-chat"
	}
	hc, err := NewHTTPClient(p, timeout, "KomgaScraper/1.0", "")
	if err != nil {
		return nil, err
	}
	return &AiClient{
		base: root, key: apiKey, mod: model, http: hc,
	}, nil
}

type aiChatResp struct {
	Choices []struct {
		Message struct {
			Content string `json:"content"`
		} `json:"message"`
	} `json:"choices"`
}

// chat 发送 /chat/completions；网络异常重试一次（历史事故：批量匹配 AI 偶发超时）。
func (c *AiClient) chat(ctx context.Context, temperature float64, system, user string) (string, error) {
	payload := map[string]any{
		"model":       c.mod,
		"temperature": temperature,
		"messages": []map[string]string{
			{"role": "system", "content": system},
			{"role": "user", "content": user},
		},
	}
	body, _ := json.Marshal(payload)
	for attempt := 0; ; attempt++ {
		req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.base+"/chat/completions", bytes.NewReader(body))
		if err != nil {
			return "", err
		}
		req.Header.Set("Content-Type", "application/json")
		if c.key != "" {
			req.Header.Set("Authorization", "Bearer "+c.key)
		}
		resp, err := c.http.Do(req)
		if err != nil {
			if attempt < 1 {
				select {
				case <-ctx.Done():
					return "", ctx.Err()
				case <-time.After(1500 * time.Millisecond):
				}
				continue
			}
			return "", fmt.Errorf("AI 请求失败: %w", err)
		}
		data, readErr := io.ReadAll(io.LimitReader(resp.Body, 1<<22))
		resp.Body.Close()
		if resp.StatusCode >= 300 {
			if resp.StatusCode >= 500 && attempt < 1 {
				io.Copy(io.Discard, resp.Body)
				resp.Body.Close()
				select {
				case <-ctx.Done():
					return "", ctx.Err()
				case <-time.After(1500 * time.Millisecond):
				}
				continue
			}
			return "", fmt.Errorf("AI 接口 %d: %s", resp.StatusCode, truncateRunes(string(data), 200))
		}
		if readErr != nil {
			return "", readErr
		}
		var cr aiChatResp
		if err := json.Unmarshal(data, &cr); err != nil {
			return "", err
		}
		if len(cr.Choices) == 0 {
			return "", fmt.Errorf("AI 响应无 choices")
		}
		return cr.Choices[0].Message.Content, nil
	}
}

func truncateRunes(s string, n int) string {
	r := []rune(s)
	if len(r) <= n {
		return s
	}
	return string(r[:n])
}

// CleanTranslation 清理模型输出的 ```代码块```、整体引号、"译文："前缀等。
func CleanTranslation(s string) string {
	t := strings.TrimSpace(s)
	if strings.HasPrefix(t, "```") {
		if nl := strings.IndexByte(t, '\n'); nl >= 0 {
			t = t[nl+1:]
		} else {
			t = ""
		}
		t = strings.TrimSuffix(strings.TrimSpace(t), "```")
		t = strings.TrimSpace(t)
	}
	if len(t) >= 2 {
		first, last := t[0], t[len(t)-1]
		if (first == '"' && last == '"') || (first == '\'' && last == '\'') ||
			(strings.HasPrefix(t, "\u201c") && strings.HasSuffix(t, "\u201d")) {
			t = strings.TrimSpace(t[1 : len(t)-1])
		}
	}
	for _, prefix := range []string{"译文：", "译文:", "翻译：", "翻译:", "中文简介：", "中文："} {
		if strings.HasPrefix(t, prefix) {
			t = strings.TrimSpace(t[len(prefix):])
			break
		}
	}
	return t
}

var stripQuoteMarks = strings.NewReplacer("《", "", "》", "", "「", "", "」", "", "『", "", "』", "")

// IdentifySeries 问 AI「这是哪部作品」；失败/无法识别返回 nil（缓存住，同一系列不重复问）。
func (c *AiClient) IdentifySeries(ctx context.Context, rawTitle string, candidateTerms []string) (*IdentifyResult, error) {
	key := strings.TrimSpace(rawTitle)
	if key == "" {
		return nil, fmt.Errorf("空标题")
	}
	aiCacheMu.Lock()
	if v, ok := identifyCache[key]; ok {
		aiCacheMu.Unlock()
		if v == nil {
			return nil, fmt.Errorf("AI 识别失败（缓存）")
		}
		return v, nil
	}
	aiCacheMu.Unlock()

	var termList string
	var terms []string
	for _, t := range candidateTerms {
		t = strings.TrimSpace(t)
		if t != "" {
			terms = append(terms, t)
		}
		if len(terms) >= 5 {
			break
		}
	}
	if len(terms) > 0 {
		termList = "从中提取的可能标题：" + strings.Join(terms, " / ") + "\n\n"
	}
	prompt := "本地漫画文件夹的原始名称：" + key + "\n" + termList +
		"请判断这是哪部漫画/轻小说，输出它的通行名字：\n" +
		"- jp：日文原名（官方标题，保留原有括号/符号写法）\n" +
		"- cn：最通行的简体中文译名（没有通行译名就留空串）\n" +
		"- en：官方英文或罗马字名（没有就留空串）\n" +
		"注意区分重名作品、续作、外传、总集篇、画集；如果无法确定是哪部作品，confident 填 false，" +
		"但仍有把握的字段照填。只输出 JSON：" +
		`{"jp":"...","cn":"...","en":"...","confident":true}`
	text, err := c.chat(ctx, 0, "你是资深漫画/轻小说资料库助手，熟悉作品在 Bangumi、BookWalker 与中文圈的通行命名。", prompt)
	if err != nil {
		aiCacheMu.Lock()
		identifyCache[key] = nil
		aiCacheMu.Unlock()
		return nil, err
	}
	var result *IdentifyResult
	cleaned := CleanTranslation(text)
	start := strings.IndexByte(cleaned, '{')
	end := strings.LastIndexByte(cleaned, '}')
	if start >= 0 && end > start {
		var dto struct {
			Jp        string `json:"jp"`
			Cn        string `json:"cn"`
			En        string `json:"en"`
			Confident bool   `json:"confident"`
		}
		if json.Unmarshal([]byte(cleaned[start:end+1]), &dto) == nil {
			jp := strings.TrimSpace(stripQuoteMarks.Replace(dto.Jp))
			cn := strings.TrimSpace(stripQuoteMarks.Replace(dto.Cn))
			en := strings.TrimSpace(dto.En)
			if jp != "" || cn != "" || en != "" {
				result = &IdentifyResult{Jp: jp, Cn: cn, En: en, Confident: dto.Confident}
			}
		}
	}
	aiCacheMu.Lock()
	identifyCache[key] = result
	aiCacheMu.Unlock()
	if result == nil {
		return nil, fmt.Errorf("AI 无法识别该作品")
	}
	return result, nil
}

// PickBest 从候选中挑最佳匹配；返回 nil 表示 AI 认为都不匹配。
func (c *AiClient) PickBest(ctx context.Context, seriesTitle string, candidates []Candidate) (*Candidate, error) {
	if len(candidates) == 0 {
		return nil, fmt.Errorf("无候选")
	}
	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("漫画/轻小说系列《%s》在 Bangumi 的搜索候选如下（编号从 1 开始）：\n", seriesTitle))
	for i, cc := range candidates {
		sb.WriteString(fmt.Sprintf("%d. 《%s》 原:%s 作者:%s 别名:%s\n", i+1, cc.Title, cc.OriTitle, cc.Author, cc.Aliases))
	}
	sb.WriteString("请判断哪个候选与目标系列是同一部作品（注意区分续作/番外/画集/不同译名）。" +
		"本地漫画库只收录日本漫画：候选若为韩国漫画（谚文标题、Naver/Lezhin/Kakao 等韩国平台连载）、" +
		"美漫或国产漫画，一律视为不匹配（输出 -1）。" +
		`只输出 JSON：{"index": 编号}；若都不匹配则输出 {"index": -1}。不要输出其他内容。`)
	text, err := c.chat(ctx, 0, "你是漫画元数据匹配助手，严格输出 JSON。", sb.String())
	if err != nil {
		return nil, err
	}
	m := aiIndexRe.FindStringSubmatch(text)
	if m == nil {
		return nil, nil
	}
	var idx int
	fmt.Sscanf(m[1], "%d", &idx)
	if idx < 1 || idx > len(candidates) {
		return nil, nil
	}
	return &candidates[idx-1], nil
}

var aiIndexRe = regexp.MustCompile(`"?index"?\s*:\s*(-?\d+)`)

// TranslateTitleToJapanese 中文标题 → 日文原名（供 BookWalker 目录匹配）；失败返回空串。
func (c *AiClient) TranslateTitleToJapanese(ctx context.Context, title string) (string, error) {
	key := strings.TrimSpace(title)
	if key == "" || !ContainsHan(key) {
		return "", fmt.Errorf("无需翻译")
	}
	aiCacheMu.Lock()
	if v, ok := titleJpCache[key]; ok {
		aiCacheMu.Unlock()
		if v == "" {
			return "", fmt.Errorf("AI 翻译失败（缓存）")
		}
		return v, nil
	}
	aiCacheMu.Unlock()
	jp, err := c.chat(ctx, 0, "你是漫画/轻小说资料整理助手。",
		"把下面这部漫画/轻小说的中文标题还原成日文原名。只输出日文标题本身，不要解释、不要书名号、不要引号：\n"+key)
	jp = strings.TrimSpace(stripQuoteMarks.Replace(CleanTranslation(jp)))
	aiCacheMu.Lock()
	if err != nil || jp == "" || jp == key {
		titleJpCache[key] = ""
		aiCacheMu.Unlock()
		if err != nil {
			return "", err
		}
		return "", fmt.Errorf("AI 未给出译名")
	}
	titleJpCache[key] = jp
	aiCacheMu.Unlock()
	return jp, nil
}

// GenerateSynopsis AI 补写简介（80~160 字简体中文）；AI 不认识时返回空串。
func (c *AiClient) GenerateSynopsis(ctx context.Context, seriesTitle, originalTitle, author string) (string, error) {
	key := strings.TrimSpace(seriesTitle)
	if key == "" {
		return "", fmt.Errorf("空标题")
	}
	aiCacheMu.Lock()
	if v, ok := synopsisCache[key]; ok {
		aiCacheMu.Unlock()
		if v == "" {
			return "", fmt.Errorf("AI 无法确认作品（缓存）")
		}
		return v, nil
	}
	aiCacheMu.Unlock()
	prompt := "请为下面这部漫画/轻小说写一段简体中文的作品简介：\n" +
		"作品名：" + key + "\n" +
		(qual("原名：", originalTitle)) + (qual("作者：", author)) +
		"要求：80~160 字，概括作品核心设定与看点，不要剧透结局，" +
		"写成可直接放在漫画站上的简介文案；全文必须是纯简体中文，不得夹杂日文假名或繁体字。\n" +
		"如果你不确定这是哪部作品（怕写错内容），只输出：UNKNOWN"
	text, err := c.chat(ctx, 0.2, "你是漫画资料站的编辑，简介基于真实作品内容，绝不能编造。", prompt)
	if err != nil {
		return "", err
	}
	out := strings.TrimSpace(CleanTranslation(text))
	if out == "" || strings.Contains(strings.ToUpper(out), "UNKNOWN") || len([]rune(out)) < 20 {
		aiCacheMu.Lock()
		synopsisCache[key] = ""
		aiCacheMu.Unlock()
		return "", fmt.Errorf("AI 无法确认作品，未补写简介")
	}
	aiCacheMu.Lock()
	synopsisCache[key] = out
	aiCacheMu.Unlock()
	return out, nil
}

func qual(label, v string) string {
	if strings.TrimSpace(v) == "" {
		return ""
	}
	return label + v + "\n"
}

// ContainsHan 文本里是否含汉字。
func ContainsHan(s string) bool {
	for _, ch := range s {
		if ch >= '\u4e00' && ch <= '\u9fff' {
			return true
		}
	}
	return false
}

// NeedsChineseTranslation 判断文本是否需要翻译成中文（日文/韩文/英文为主/汉字不足一半 → 是）。
func NeedsChineseTranslation(text string) bool {
	if strings.TrimSpace(text) == "" {
		return false
	}
	han, kana, hangul, latin, other := 0, 0, 0, 0, 0
	for _, ch := range text {
		switch {
		case ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r':
			continue
		case ch >= '\u3040' && ch <= '\u30ff':
			kana++
		case ch >= '\uac00' && ch <= '\ud7a3':
			hangul++
		case ch >= '\u4e00' && ch <= '\u9fff':
			han++
		case ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z':
			latin++
		case ch >= '0' && ch <= '9':
			// 数字/标点不计
		default:
			other++
		}
	}
	if kana > 0 || hangul > 0 {
		return true
	}
	letters := han + latin + other
	if letters == 0 {
		return false
	}
	if latin*2 > letters {
		return true
	}
	return han*2 < letters
}

// TranslateToChinese 把非中文简介翻译成简体中文；已是中文或空文本原样返回（不调用接口）。
func (c *AiClient) TranslateToChinese(ctx context.Context, text string) (string, error) {
	if !NeedsChineseTranslation(text) {
		return text, nil
	}
	input := strings.TrimSpace(text)
	if len([]rune(input)) > 3000 {
		input = string([]rune(input)[:3000])
	}
	aiCacheMu.Lock()
	if v, ok := transSumCache[input]; ok {
		aiCacheMu.Unlock()
		return v, nil
	}
	aiCacheMu.Unlock()
	out, err := c.chat(ctx, 0, "你是漫画/轻小说资料翻译，负责把外文简介翻译成自然准确的简体中文。",
		"请把下面这段作品简介完整翻译成简体中文，严格要求：\n"+
			"1. 全文译成简体中文，不允许保留任何日文假名、谚文或未翻译的日文词句；\n"+
			"2. 人名/作品名等专有名词用通行简体译名，没有通行译名的写成汉字；\n"+
			"3. 不使用繁体字，统一输出简体；\n"+
			"4. 保留原有分段和书名号；只输出译文本身，不要解释、不要前缀、不要加引号：\n\n"+input)
	if err != nil {
		return "", err
	}
	translated := CleanTranslation(out)
	if translated == "" {
		translated = text
	}
	aiCacheMu.Lock()
	transSumCache[input] = translated
	aiCacheMu.Unlock()
	return translated, nil
}
