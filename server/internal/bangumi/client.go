package bangumi

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync"
	"time"

	"komgabangumi/internal/text"
)

const (
	// DefaultAPIBase Bangumi v0 API 默认基址。
	DefaultAPIBase = "https://api.bgm.tv"
	// LegacyBase 旧站（用于拼条目链接）。
	LegacyBase = "https://bangumi.tv"
	// DefaultMinIntervalMs 两次 Bangumi API 请求的最小间隔（≥350ms/请求，硬性要求）。
	DefaultMinIntervalMs = 350
)

// APIError Bangumi API 错误。
type APIError struct {
	Status  int
	Message string
}

func (e *APIError) Error() string { return e.Message }

// Client Bangumi v0 API 客户端。
// 限速：并发批量刮削时所有系列共享同一个节拍（≥350ms/请求）。
// 重试：429/5xx/网络错误按退避重试（500ms→2s→4s，最多 3 次）。
type Client struct {
	http         *http.Client
	apiBase      string
	accessToken  string
	minInterval  time.Duration
	paceMu       sync.Mutex
	nextStart    time.Time
}

// New 创建客户端。apiBase 为空用官方基址（测试可指向 mock）。
func New(accessToken, apiBase string, minIntervalMs int) *Client {
	if apiBase == "" {
		apiBase = DefaultAPIBase
	}
	if minIntervalMs < 0 {
		minIntervalMs = 0
	}
	return &Client{
		http:        &http.Client{Timeout: 30 * time.Second},
		apiBase:     strings.TrimRight(apiBase, "/"),
		accessToken: accessToken,
		minInterval: time.Duration(minIntervalMs) * time.Millisecond,
	}
}

// scheduleStart 把请求的"起始时刻"按最小间隔排开（对齐 C# ScheduleStartAsync）。
func (c *Client) scheduleStart() {
	if c.minInterval <= 0 {
		return
	}
	c.paceMu.Lock()
	now := time.Now()
	start := now
	if c.nextStart.After(now) {
		start = c.nextStart
	}
	c.nextStart = start.Add(c.minInterval)
	c.paceMu.Unlock()
	if d := time.Until(start); d > 0 {
		time.Sleep(d)
	}
}

func (c *Client) do(method, path string, payload any) ([]byte, error) {
	var body []byte
	if payload != nil {
		b, err := json.Marshal(payload)
		if err != nil {
			return nil, err
		}
		body = b
	}
	backoff := []time.Duration{500 * time.Millisecond, 2 * time.Second, 4 * time.Second}
	var lastErr error
	for attempt := 0; ; attempt++ {
		c.scheduleStart()
		req, err := http.NewRequest(method, c.apiBase+path, bytes.NewReader(body))
		if err != nil {
			return nil, err
		}
		req.Header.Set("Accept", "application/json")
		req.Header.Set("User-Agent", "KomgaBangumi/1.0 (docker)")
		if c.accessToken != "" {
			req.Header.Set("Authorization", "Bearer "+c.accessToken)
		}
		if body != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		resp, err := c.http.Do(req)
		if err != nil {
			lastErr = err
			if attempt < len(backoff) {
				time.Sleep(backoff[attempt])
				continue
			}
			return nil, fmt.Errorf("Bangumi 请求网络异常: %w", err)
		}
		data, readErr := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
		resp.Body.Close()
		status := resp.StatusCode
		if status == 429 || status >= 500 {
			lastErr = &APIError{Status: status, Message: fmt.Sprintf("Bangumi API %d: %s", status, truncate(string(data), 200))}
			if attempt < len(backoff) {
				time.Sleep(backoff[attempt])
				continue
			}
			return nil, lastErr
		}
		if status == http.StatusUnauthorized {
			return nil, &APIError{Status: 401, Message: "Bangumi 认证失败(401)：Access Token 已失效，请在设置中更新"}
		}
		if status >= 300 {
			return nil, &APIError{Status: status,
				Message: fmt.Sprintf("Bangumi API %d: %s", status, truncate(string(data), 200))}
		}
		return data, readErr
	}
}

func truncate(s string, n int) string {
	r := []rune(s)
	if len(r) <= n {
		return s
	}
	return string(r[:n])
}

// SearchSubjects 按名称搜索（type=1 书籍，含 NSFW），过滤 platform 与韩漫，返回归一化候选。
func (c *Client) SearchSubjects(keyword, matchType string, limit int) ([]SearchCandidate, error) {
	payload := map[string]any{
		"keyword": keyword,
		"sort":    "match",
		"filter":  map[string]any{"type": []int{1}, "nsfw": true},
	}
	data, err := c.do(http.MethodPost, "/v0/search/subjects?limit=20", payload)
	if err != nil {
		return nil, fmt.Errorf("Bangumi 搜索失败: %w", err)
	}
	var search SearchResponseDto
	if err := json.Unmarshal(data, &search); err != nil {
		return nil, err
	}
	var out []SearchCandidate
	for _, item := range search.Data {
		if matchType != "" && item.Platform != matchType {
			continue
		}
		cand := toCandidate(item)
		// 日漫过滤：谚文标题/韩漫标签/韩国平台连载的条目直接剔除（本地库只收录日漫）
		aliases := ExtractAliases(item.Infobox)
		magazine := ParseInfobox(item.Infobox, "连载杂志")
		var tagNames []string
		for _, t := range item.Tags {
			tagNames = append(tagNames, t.Name)
		}
		if text.LooksKorean(cand.Title, cand.OriTitle, aliases, tagNames, magazine) {
			continue
		}
		out = append(out, cand)
	}
	if limit > 0 && len(out) > limit {
		out = out[:limit]
	}
	return out, nil
}

func toCandidate(item SearchItemDto) SearchCandidate {
	authorName := "未知作者"
	aliases := ""
	if item.Infobox != nil {
		author := ParseInfobox(item.Infobox, "作画")
		if author == "" {
			author = ParseInfobox(item.Infobox, "作者")
		}
		if author == "" {
			author = ParseInfobox(item.Infobox, "原作")
		}
		if author == "" {
			author = ParseInfobox(item.Infobox, "脚本")
		}
		if author != "" {
			authorName = FirstAuthor(author)
		}
		aliases = ExtractAliases(item.Infobox)
	}
	cover := item.Image
	if cover == "" && item.Images != nil {
		cover = item.Images.Medium
		if cover == "" {
			cover = item.Images.Common
		}
		if cover == "" {
			cover = item.Images.Small
		}
	}
	title := item.NameCN
	if title == "" {
		title = item.Name
	}
	return SearchCandidate{
		ID:       item.ID,
		Title:    title,
		OriTitle: item.Name,
		Author:   authorName,
		Aliases:  aliases,
		CoverURL: cover,
		Source:   "Bgm",
	}
}

// GetSubject 条目详情。
func (c *Client) GetSubject(subjectID int) (*SubjectDto, error) {
	data, err := c.do(http.MethodGet, fmt.Sprintf("/v0/subjects/%d", subjectID), nil)
	if err != nil {
		return nil, err
	}
	var s SubjectDto
	if err := json.Unmarshal(data, &s); err != nil {
		return nil, err
	}
	return &s, nil
}

// GetRelatedSubjects 关联条目（单行本）。
func (c *Client) GetRelatedSubjects(subjectID int) ([]RelatedSubjectDto, error) {
	data, err := c.do(http.MethodGet, fmt.Sprintf("/v0/subjects/%d/subjects", subjectID), nil)
	if err != nil {
		return nil, err
	}
	var list []RelatedSubjectDto
	if err := json.Unmarshal(data, &list); err != nil {
		return nil, err
	}
	return list, nil
}

// SetHTTPClient 注入自定义 HTTP 客户端（v2：让封面下载走设置里的代理，与 Bangumi API 同路）。
func (c *Client) SetHTTPClient(h *http.Client) {
	if h != nil {
		c.http = h
	}
}

// DownloadImage 下载封面图片（CDN，不参与 API 节流；但复用 429 退避不做——CDN 无鉴权）。
func (c *Client) DownloadImage(url string) ([]byte, error) {
	req, err := http.NewRequest(http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36")
	resp, err := c.http.Do(req)
	if err != nil {
		return nil, fmt.Errorf("下载封面失败: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		return nil, fmt.Errorf("下载封面失败 %d: %s", resp.StatusCode, url)
	}
	return io.ReadAll(resp.Body)
}