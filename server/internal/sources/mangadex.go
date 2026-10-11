package sources

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// MangaDexSeries MangaDex 结果条目（GET /manga 展开后的归一化模型）。
// 标题挑选：zh-Hans > zh-Hant > 原文语言 > ja/en 首个；简介取 description（en/ja 齐全）。
type MangaDexSeries struct {
	ID               string
	Title            string
	OriTitle         string
	AltTitles        []string
	Description      string
	CoverURL         string
	Status           string
	OriginalLanguage string
	Tags             []string
}

// MangaDexClient 英文圈图源（免密钥公开 API，api.mangadex.org）。
type MangaDexClient struct {
	base  string
	cover string
	http  *http.Client
}

// NewMangaDexClient 创建客户端。
func NewMangaDexClient(p ProxyConfig, baseURL string, japaneseOnly bool) (*MangaDexClient, error) {
	hc, err := NewHTTPClient(p, 20*time.Second, "KomgaScraper/1.0 (metadata scraper)", "")
	if err != nil {
		return nil, err
	}
	if baseURL == "" {
		baseURL = "https://api.mangadex.org"
	}
	return &MangaDexClient{base: strings.TrimRight(baseURL, "/"), cover: "https://uploads.mangadex.org/covers", http: hc}, nil
}

type mdResponse struct {
	Result string     `json:"result"`
	Data   []mdManga  `json:"data"`
}

type mdManga struct {
	ID            string        `json:"id"`
	Type          string        `json:"type"`
	Attributes    *mdAttributes `json:"attributes"`
	Relationships []mdRelation  `json:"relationships"`
}

type mdAttributes struct {
	Title            map[string]string      `json:"title"`
	AltTitles        []map[string]string    `json:"altTitles"`
	Description      map[string]string      `json:"description"`
	OriginalLanguage string                 `json:"originalLanguage"`
	Status           string                 `json:"status"`
	Tags             []mdTag                `json:"tags"`
}

type mdRelation struct {
	ID         string          `json:"id"`
	Type       string          `json:"type"`
	Attributes *mdCoverAttrs   `json:"attributes"`
}

type mdCoverAttrs struct {
	FileName string `json:"fileName"`
}

type mdTag struct {
	Attributes *struct {
		Name map[string]string `json:"name"`
	} `json:"attributes"`
}

// Search 按关键词搜索（title 匹配主标题与别名）。
func (c *MangaDexClient) Search(ctx context.Context, keyword string, limit int) ([]*MangaDexSeries, error) {
	if strings.TrimSpace(keyword) == "" {
		return nil, nil
	}
	if limit < 1 {
		limit = 1
	}
	if limit > 100 {
		limit = 100
	}
	u := fmt.Sprintf("%s/manga?title=%s&limit=%d&includes%%5B%%5D=cover_art&contentRating%%5B%%5D=safe&contentRating%%5B%%5D=suggestive&contentRating%%5B%%5D=erotica",
		c.base, url.QueryEscape(keyword), limit)
	return c.search(ctx, u)
}

// Get 按 id 取单部详情。
func (c *MangaDexClient) Get(ctx context.Context, id string) (*MangaDexSeries, error) {
	if strings.TrimSpace(id) == "" {
		return nil, nil
	}
	u := fmt.Sprintf("%s/manga/%s?includes%%5B%%5D=cover_art", c.base, url.PathEscape(id))
	resp, err := c.http.Get(u)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		io.Copy(io.Discard, io.LimitReader(resp.Body, 1024))
		return nil, fmt.Errorf("MangaDex 详情失败 %d: %s", resp.StatusCode, id)
	}
	var parsed mdResponse
	if err := json.NewDecoder(io.LimitReader(resp.Body, 8<<20)).Decode(&parsed); err != nil {
		return nil, err
	}
	if len(parsed.Data) == 0 {
		return nil, nil
	}
	return toMDSeries(&parsed.Data[0]), nil
}

// DownloadCover 下载封面（uploads CDN 公开直连）。
func (c *MangaDexClient) DownloadCover(ctx context.Context, u string) ([]byte, error) {
	resp, err := c.http.Get(u)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		io.Copy(io.Discard, io.LimitReader(resp.Body, 1024))
		return nil, fmt.Errorf("下载 MangaDex 封面失败 %d: %s", resp.StatusCode, u)
	}
	return io.ReadAll(resp.Body)
}

func (c *MangaDexClient) search(ctx context.Context, u string) ([]*MangaDexSeries, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return nil, err
	}
	resp, err := c.http.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		io.Copy(io.Discard, io.LimitReader(resp.Body, 1024))
		return nil, fmt.Errorf("MangaDex 搜索失败 %d: %s", resp.StatusCode, u)
	}
	var parsed mdResponse
	if err := json.NewDecoder(io.LimitReader(resp.Body, 8<<20)).Decode(&parsed); err != nil {
		return nil, err
	}
	var out []*MangaDexSeries
	for i := range parsed.Data {
		if s := toMDSeries(&parsed.Data[i]); s.ID != "" {
			out = append(out, s)
		}
	}
	return out, nil
}

func pickDict(dict map[string]string, keys ...string) string {
	for _, k := range keys {
		if v, ok := dict[k]; ok && strings.TrimSpace(v) != "" {
			return v
		}
	}
	return ""
}

func orDefault(a, b string) string {
	if strings.TrimSpace(a) != "" {
		return a
	}
	return b
}

func toMDSeries(d *mdManga) *MangaDexSeries {
	attr := d.Attributes
	if attr == nil {
		return &MangaDexSeries{}
	}
	titleDict := attr.Title
	if titleDict == nil {
		titleDict = map[string]string{}
	}
	oriLang := attr.OriginalLanguage
	title := pickDict(titleDict, "zh-Hans", "zh-Hant", oriLang)
	if title == "" {
		title = pickDict(titleDict, "ja", "en")
	}
	if title == "" {
		for _, v := range titleDict {
			if strings.TrimSpace(v) != "" {
				title = v
				break
			}
		}
	}
	ori := pickDict(titleDict, orDefault(oriLang, "ja"))
	if ori == "" {
		ori = pickDict(titleDict, "ja", "en")
	}

	// 别名：所有 altTitles 的值去重（排除已选标题/原名），最多 8 条
	var alts []string
	seen := map[string]bool{}
	for _, alt := range attr.AltTitles {
		for _, v := range alt {
			v = strings.TrimSpace(v)
			if v == "" || seen[strings.ToLower(v)] || v == title || v == ori {
				continue
			}
			seen[strings.ToLower(v)] = true
			if len(alts) < 8 {
				alts = append(alts, v)
			}
		}
	}

	desc := attr.Description
	if desc == nil {
		desc = map[string]string{}
	}
	description := pickDict(desc, "en", "ja")
	if description == "" {
		for _, v := range desc {
			if strings.TrimSpace(v) != "" {
				description = v
				break
			}
		}
	}

	// 封面：cover_art 关系 → uploads.mangadex.org/covers/{id}/{file}
	coverURL := ""
	for _, r := range d.Relationships {
		if r.Type == "cover_art" && r.Attributes != nil && r.Attributes.FileName != "" {
			coverURL = fmt.Sprintf("%s/%s/%s", "https://uploads.mangadex.org/covers", d.ID, r.Attributes.FileName)
			break
		}
	}

	// 标签：name.en（英文圈标签）
	var tags []string
	seenTag := map[string]bool{}
	for _, t := range attr.Tags {
		if t.Attributes == nil {
			continue
		}
		n := pickDict(t.Attributes.Name, "en")
		if n == "" || seenTag[strings.ToLower(n)] {
			continue
		}
		seenTag[strings.ToLower(n)] = true
		tags = append(tags, n)
	}

	return &MangaDexSeries{ID: d.ID, Title: title, OriTitle: ori, AltTitles: alts,
		Description: description, CoverURL: coverURL, Status: attr.Status,
		OriginalLanguage: oriLang, Tags: tags}
}
