package sources

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// BaikeCardField 百度百科词条基本信息卡字段。
type BaikeCardField struct {
	Name  string // 字段显示名（已去空格，如「作画」「其他出版社」）
	Value string // 字段值（多值合并为顿号串）
}

// BaikeCard 百度百科词条卡片。
type BaikeCard struct {
	LemmaID  int64
	Title    string // 词条标题（中文通行名）
	Desc     string // 一句话描述
	Abstract string // 词条摘要（本身是中文，可直接当简介）
	ImageURL string // 概述图 URL（bkimg.cdn.bcebos.com；可能为空）
	EntryURL string // 词条页 URL（已转 https）
	Fields   []BaikeCardField
}

// Field 按字段名取值，没有返回空串。
func (c *BaikeCard) Field(name string) string {
	for _, f := range c.Fields {
		if f.Name == name {
			return f.Value
		}
	}
	return ""
}

// LooksLikeMangaWork 是否为「漫画作品」词条（保守判定：宁缺毋错——
// 人物词条概述图是本人照片、影视词条是电影海报、文学词条是小说封面，都不能当封面用）。
func (c *BaikeCard) LooksLikeMangaWork() bool {
	mangaFields := []string{"出版社", "作画", "原版名称", "连载杂志", "连载期间", "丛书", "单行本册数", "其他出版社"}
	for _, f := range c.Fields {
		for _, m := range mangaFields {
			if f.Name == m {
				return true
			}
		}
	}
	personFields := []string{"职业", "国籍", "出生日期", "逝世日期", "出生地", "毕业院校", "经纪公司", "民族", "出生名"}
	for _, f := range c.Fields {
		for _, m := range personFields {
			if f.Name == m {
				return false
			}
		}
	}
	mediaFields := []string{"出品公司", "制片地区", "导演", "主演", "上映日期", "票房", "发行公司", "首播电视台", "编剧", "文学体裁", "创作年代", "出品人", "制片人"}
	for _, f := range c.Fields {
		for _, m := range mediaFields {
			if f.Name == m {
				return false
			}
		}
	}
	switch c.Field("类型") {
	case "电影", "电视剧", "动画", "游戏":
		return false
	}
	// 字段稀疏时用摘要兜底：人物词条含「漫画家」类称呼；作品词条描述通常含「作画」
	body := c.Desc + c.Abstract
	for _, w := range []string{"漫画家", "插画家", "绘师", "声优", "小说家", "作家"} {
		if strings.Contains(body, w) && !strings.Contains(body, "作画") {
			return false
		}
	}
	return true
}

// BaikeClient 百度百科客户端 —— 冷门漫画的最终兜底源（国内直连不走代理）。
type BaikeClient struct {
	base string
	http *http.Client
}

// NewBaikeClient 创建客户端。
func NewBaikeClient(baseURL string) (*BaikeClient, error) {
	hc, err := NewHTTPClient(ProxyConfig{}, 20*time.Second,
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36", "")
	if err != nil {
		return nil, err
	}
	if baseURL == "" {
		baseURL = "https://baike.baidu.com"
	}
	return &BaikeClient{base: strings.TrimRight(baseURL, "/"), http: hc}, nil
}

type baikeResp struct {
	ID         flexInt64 `json:"id"`
	NewLemmaID flexInt64 `json:"newLemmaId"`
	Title      string    `json:"title"`
	Desc       string    `json:"desc"`
	Abstract   string    `json:"abstract"`
	Image      string    `json:"image"`
	URL        string    `json:"url"`
	Card       []struct {
		Name  string   `json:"name"`
		Value []string `json:"value"`
	} `json:"card"`
}

// flexInt64 百科 id / newLemmaId 有时回数字、有时回字符串（甚至空串）—— 宽容解析。
// 实测报错：`json: cannot unmarshal string into Go struct field ... of type int64`，
// 字段类型不匹配会让整条百科兜底源解析失败（一次任务 3 个系列因此失去兜底）。
type flexInt64 int64

func (f *flexInt64) UnmarshalJSON(b []byte) error {
	s := strings.Trim(strings.TrimSpace(string(b)), `"`)
	if s == "" || s == "null" {
		*f = 0
		return nil
	}
	if v, err := strconv.ParseInt(s, 10, 64); err == nil {
		*f = flexInt64(v)
		return nil
	}
	if v, err := strconv.ParseFloat(s, 64); err == nil {
		*f = flexInt64(int64(v))
		return nil
	}
	*f = 0 // 其他形态不炸整条解析
	return nil
}

// SearchCard 按关键词取词条卡片；未命中返回 nil，网络错误抛出。
func (c *BaikeClient) SearchCard(ctx context.Context, keyword string) (*BaikeCard, error) {
	kw := strings.TrimSpace(keyword)
	if kw == "" {
		return nil, nil
	}
	u := fmt.Sprintf("%s/api/openapi/BaikeLemmaCardApi?scope=103&format=json&appid=379020&bk_key=%s&bk_length=600",
		c.base, urlQueryEscape(kw))
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "application/json")
	req.Header.Set("Referer", c.base+"/")
	resp, err := c.http.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, 2<<20))
	if err != nil {
		return nil, err
	}
	if resp.StatusCode >= 300 {
		return nil, fmt.Errorf("百度百科搜索失败 %d: %s", resp.StatusCode, kw)
	}
	var r baikeResp
	if err := json.Unmarshal(data, &r); err != nil {
		return nil, err
	}
	title := strings.TrimSpace(r.Title)
	if title == "" {
		return nil, nil // 未命中：接口只回 errno/errMsg，没有 title
	}
	// 注意：不做 T2S —— 百科正文是简体，但「原版名称/作画」等卡片值是日文原文，原样保留
	card := &BaikeCard{
		LemmaID:  int64(r.NewLemmaID),
		Title:    title,
		Desc:     strings.TrimSpace(r.Desc),
		Abstract: strings.TrimSpace(r.Abstract),
		ImageURL: strings.TrimSpace(r.Image),
		EntryURL: toHTTPS(strings.TrimSpace(r.URL)),
	}
	for _, f := range r.Card {
		name := strings.ReplaceAll(strings.TrimSpace(f.Name), " ", "")
		var vals []string
		for _, v := range f.Value {
			if strings.TrimSpace(v) != "" {
				vals = append(vals, strings.TrimSpace(v))
			}
		}
		card.Fields = append(card.Fields, BaikeCardField{Name: name, Value: joinStr(vals, "、")})
	}
	return card, nil
}

// DownloadImage 下载词条图：三段回退——原 URL → 去掉 x-bce-process 参数的裸图 → 再失败才抛。
func (c *BaikeClient) DownloadImage(ctx context.Context, u string) ([]byte, error) {
	blob, err := c.downloadOnce(ctx, u)
	if err == nil || !strings.Contains(u, "x-bce-process=") {
		return blob, err
	}
	return c.downloadOnce(ctx, StripProcessParam(u))
}

func (c *BaikeClient) downloadOnce(ctx context.Context, u string) ([]byte, error) {
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
		return nil, fmt.Errorf("下载百度百科图片失败 %d: %s", resp.StatusCode, u)
	}
	return io.ReadAll(resp.Body)
}

// StripProcessParam 剥掉 URL 里的 x-bce-process 处理参数（裸图直链）。
func StripProcessParam(u string) string {
	q := strings.Index(u, "?x-bce-process=")
	if q < 0 {
		return u
	}
	amp := strings.IndexByte(u[q:], '&')
	if amp > 0 {
		return u[:q] + u[q+amp:]
	}
	return u[:q]
}

// JpegCoverURL 概述图直链（bkimg 的 f_auto 由 Accept 协商，URL 原样返回——历史事故：改成 jpg 反而 400）。
func JpegCoverURL(imageURL string) string { return imageURL }

func toHTTPS(u string) string {
	if strings.HasPrefix(u, "http://") {
		return "https://" + u[len("http://"):]
	}
	return u
}

func joinStr(list []string, sep string) string {
	return strings.Join(list, sep)
}

func urlQueryEscape(s string) string {
	var b strings.Builder
	for i := 0; i < len(s); i++ {
		ch := s[i]
		if (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') ||
			ch == '-' || ch == '_' || ch == '.' || ch == '~' {
			b.WriteByte(ch)
		} else {
			fmt.Fprintf(&b, "%%%02X", ch)
		}
	}
	return b.String()
}
