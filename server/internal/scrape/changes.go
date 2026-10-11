// Package scrape：刮削管线，移植自 Scraping/ScrapeService.cs、BatchMatcher.cs、
// ManualMatchCollection.cs、ScrapeChange.cs（仅 v1 Bangumi 主链）。
package scrape

// ChangeKind 一次库写入动作的种类（Dry-run 预览面板的行类型）。
type ChangeKind string

const (
	ChangeSeriesMeta  ChangeKind = "seriesMeta"
	ChangeSeriesCover ChangeKind = "seriesCover"
	ChangeBookMeta    ChangeKind = "bookMeta"
	ChangeBookCover   ChangeKind = "bookCover"
	ChangeCollection  ChangeKind = "collection"
)

// Change 单条「将对库做什么 / 对库做了什么」的改动记录。
type Change struct {
	Kind      ChangeKind        `json:"kind"`
	SeriesID  string            `json:"seriesId"`
	Target    string            `json:"target"`
	Desc      string            `json:"description"`
	Detail    string            `json:"detail,omitempty"`
	BookID    string            `json:"bookId,omitempty"`
	FieldDiff map[string][2]any `json:"-"`
}

// ChangeKindText 类型的中文短标签（预览面板"类型"列）。
func ChangeKindText(kind ChangeKind) string {
	switch kind {
	case ChangeSeriesMeta:
		return "系列信息"
	case ChangeSeriesCover:
		return "系列封面"
	case ChangeBookMeta:
		return "卷信息"
	case ChangeBookCover:
		return "卷封面"
	case ChangeCollection:
		return "收藏夹"
	default:
		return "改动"
	}
}
