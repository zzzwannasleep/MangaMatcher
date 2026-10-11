// Package bangumi：Bangumi (bgm.tv) v0 API 客户端与映射，移植自 Sources/BangumiClient.cs、
// BangumiModels.cs、SubjectMapper.cs。
package bangumi

import "encoding/json"

// TagDto 标签（含使用人数）。
type TagDto struct {
	Name  string `json:"name"`
	Count int    `json:"count"`
}

// RatingDto 评分。
type RatingDto struct {
	Score *float64 `json:"score"`
}

// ImagesDto 图片组。
type ImagesDto struct {
	Large  string `json:"large,omitempty"`
	Medium string `json:"medium,omitempty"`
	Common string `json:"common,omitempty"`
	Small  string `json:"small,omitempty"`
	Grid   string `json:"grid,omitempty"`
}

// InfoboxItemDto infobox 条目：value 多态（string / {"v":..} / [...]），由 ParseInfobox 解析。
type InfoboxItemDto struct {
	Key   string          `json:"key"`
	Value json.RawMessage `json:"value,omitempty"`
}

// SearchItemDto 搜索结果条目（/v0/search/subjects）。
type SearchItemDto struct {
	ID       int              `json:"id"`
	Name     string           `json:"name,omitempty"`
	NameCN   string           `json:"name_cn,omitempty"`
	Platform string           `json:"platform,omitempty"`
	Image    string           `json:"image,omitempty"`
	Images   *ImagesDto       `json:"images,omitempty"`
	Infobox  []InfoboxItemDto `json:"infobox,omitempty"`
	Tags     []TagDto         `json:"tags,omitempty"`
	Nsfw     *bool            `json:"nsfw,omitempty"`
}

// SearchResponseDto 搜索响应。
type SearchResponseDto struct {
	Data []SearchItemDto `json:"data"`
}

// SubjectDto 条目详情（/v0/subjects/{id}）。
type SubjectDto struct {
	ID             int              `json:"id"`
	Name           string           `json:"name,omitempty"`
	NameCN         string           `json:"name_cn,omitempty"`
	Platform       string           `json:"platform,omitempty"`
	Summary        string           `json:"summary,omitempty"`
	Volumes        *int             `json:"volumes,omitempty"`
	Eps            *int             `json:"eps,omitempty"`
	TotalEpisodes  *int             `json:"total_episodes,omitempty"`
	Image          string           `json:"image,omitempty"`
	Images         *ImagesDto       `json:"images,omitempty"`
	Date           string           `json:"date,omitempty"`
	Nsfw           *bool            `json:"nsfw,omitempty"`
	AgeRating      *int             `json:"age_rating,omitempty"`
	Tags           []TagDto         `json:"tags,omitempty"`
	Rating         *RatingDto       `json:"rating,omitempty"`
	Infobox        []InfoboxItemDto `json:"infobox,omitempty"`
}

// RelatedSubjectDto 关联条目（/v0/subjects/{id}/subjects，用于抓单行本）。
type RelatedSubjectDto struct {
	ID       int        `json:"id"`
	Name     string     `json:"name,omitempty"`
	NameCN   string     `json:"name_cn,omitempty"`
	Relation string     `json:"relation,omitempty"`
	Image    string     `json:"image,omitempty"`
	Images   *ImagesDto `json:"images,omitempty"`
}

// SearchCandidate 归一化搜索候选（对应脚本 fetchBookByName 的返回项）。
type SearchCandidate struct {
	ID       int    // Bangumi subject id（未知源为 0）
	Title    string // 中文名优先
	OriTitle string // 原名
	Author   string
	Aliases  string // " / " 连接
	CoverURL string
	Source   string // "Bgm"
}

// Aliases 提取别名（详情区展示用）。
func (s *SubjectDto) Aliases() string { return ExtractAliases(s.Infobox) }

// VolumeMate 单行本信息（对应脚本 volumeMates）。
type VolumeMate struct {
	Num         string
	Summary     string
	ReleaseDate string
	Isbn        string
	CoverURLs   []string
}