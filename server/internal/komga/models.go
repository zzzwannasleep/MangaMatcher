// Package komga：Komga REST API 客户端与 DTO，移植自 KomgaScraper.Core/Komga。
package komga

// GetTitle nil 安全取值（v2 刮削层统一走 getter）。
func (m *SeriesMetadataDto) GetTitle() string {
	if m == nil {
		return ""
	}
	return m.Title
}

// GetSummary nil 安全取值。
func (m *SeriesMetadataDto) GetSummary() string {
	if m == nil {
		return ""
	}
	return m.Summary
}

// GetStatus nil 安全取值。
func (m *SeriesMetadataDto) GetStatus() string {
	if m == nil {
		return ""
	}
	return m.Status
}

// GetPublisher nil 安全取值。
func (m *SeriesMetadataDto) GetPublisher() string {
	if m == nil {
		return ""
	}
	return m.Publisher
}

// GetReleaseDate nil 安全取值。
func (m *SeriesMetadataDto) GetReleaseDate() string {
	if m == nil {
		return ""
	}
	return m.ReleaseDate
}

// GetTotalBookCount nil 安全取值。
func (m *SeriesMetadataDto) GetTotalBookCount() int {
	if m == nil || m.TotalBookCount == nil {
		return 0
	}
	return *m.TotalBookCount
}

// GetAgeRating nil 安全取值。
func (m *SeriesMetadataDto) GetAgeRating() int {
	if m == nil || m.AgeRating == nil {
		return 0
	}
	return *m.AgeRating
}

// GetStatusLock nil 安全取值。
func (m *SeriesMetadataDto) GetStatusLock() bool { return m != nil && m.StatusLock != nil && *m.StatusLock }

// GetTitleLock nil 安全取值。
func (m *SeriesMetadataDto) GetTitleLock() bool { return m != nil && m.TitleLock != nil && *m.TitleLock }

// GetTagsLock nil 安全取值。
func (m *SeriesMetadataDto) GetTagsLock() bool { return m != nil && m.TagsLock != nil && *m.TagsLock }

// GetGenresLock nil 安全取值。
func (m *SeriesMetadataDto) GetGenresLock() bool { return m != nil && m.GenresLock != nil && *m.GenresLock }

// GetReadingDirectionLock nil 安全取值。
func (m *SeriesMetadataDto) GetReadingDirectionLock() bool {
	return m != nil && m.ReadingDirectionLock != nil && *m.ReadingDirectionLock
}

// GetReleaseDateLock nil 安全取值。
func (m *SeriesMetadataDto) GetReleaseDateLock() bool { return m != nil && m.ReleaseDateLock != nil && *m.ReleaseDateLock }

// GetAlternateTitlesLock nil 安全取值。
func (m *SeriesMetadataDto) GetAlternateTitlesLock() bool {
	return m != nil && m.AlternateTitlesLock != nil && *m.AlternateTitlesLock
}

// LibraryDto Komga 图书馆。
type LibraryDto struct {
	ID   string `json:"id"`
	Name string `json:"name"`
	Root string `json:"root,omitempty"`
}

// AuthorDto 作者。
type AuthorDto struct {
	Name string `json:"name"`
	Role string `json:"role"`
}

// LinkDto 链接。
type LinkDto struct {
	Label string `json:"label"`
	URL   string `json:"url"`
}

// AlternateTitleDto 备用标题。
type AlternateTitleDto struct {
	Label string `json:"label"`
	Title string `json:"title"`
}

// SeriesMetadataDto 系列元数据（含 Komga 的锁字段）。
type SeriesMetadataDto struct {
	Status        string              `json:"status,omitempty"`
	Title         string              `json:"title,omitempty"`
	TitleSort     string              `json:"titleSort,omitempty"`
	Summary       string              `json:"summary,omitempty"`
	ReadingDirection string           `json:"readingDirection,omitempty"`
	Publisher     string              `json:"publisher,omitempty"`
	AgeRating     *int                `json:"ageRating,omitempty"`
	Language      string              `json:"language,omitempty"`
	Genres        []string            `json:"genres,omitempty"`
	Tags          []string            `json:"tags,omitempty"`
	TotalBookCount *int               `json:"totalBookCount,omitempty"`
	AlternateTitles []AlternateTitleDto `json:"alternateTitles,omitempty"`
	Authors       []AuthorDto         `json:"authors,omitempty"`
	ReleaseDate   string              `json:"releaseDate,omitempty"`
	Links         []LinkDto           `json:"links,omitempty"`

	StatusLock          *bool `json:"statusLock,omitempty"`
	TitleLock           *bool `json:"titleLock,omitempty"`
	TitleSortLock       *bool `json:"titleSortLock,omitempty"`
	SummaryLock         *bool `json:"summaryLock,omitempty"`
	PublisherLock       *bool `json:"publisherLock,omitempty"`
	AgeRatingLock       *bool `json:"ageRatingLock,omitempty"`
	GenresLock          *bool `json:"genresLock,omitempty"`
	TagsLock            *bool `json:"tagsLock,omitempty"`
	TotalBookCountLock  *bool `json:"totalBookCountLock,omitempty"`
	AlternateTitlesLock *bool `json:"alternateTitlesLock,omitempty"`
	AuthorsLock         *bool `json:"authorsLock,omitempty"`
	LinksLock           *bool `json:"linksLock,omitempty"`
	LanguageLock        *bool `json:"languageLock,omitempty"`
	ReadingDirectionLock *bool `json:"readingDirectionLock,omitempty"`
	ReleaseDateLock     *bool `json:"releaseDateLock,omitempty"`
}

// BooksMetadataDto 系列书籍聚合元数据。
type BooksMetadataDto struct {
	ReleaseDate string      `json:"releaseDate,omitempty"`
	Summary     string      `json:"summary,omitempty"`
	Authors     []AuthorDto `json:"authors,omitempty"`
	Tags        []string    `json:"tags,omitempty"`
}

// SeriesDto 系列。
type SeriesDto struct {
	ID            string             `json:"id"`
	Name          string             `json:"name"`
	LibraryID     string             `json:"libraryId"`
	URL           string             `json:"url,omitempty"`
	Oneshot       bool               `json:"oneshot,omitempty"`
	Metadata      *SeriesMetadataDto `json:"metadata,omitempty"`
	BooksMetadata *BooksMetadataDto  `json:"booksMetadata,omitempty"`
	BooksCount    int                `json:"booksCount,omitempty"`
	Deleted       bool               `json:"deleted,omitempty"`
}

// BookMetadataDto 单册元数据。
type BookMetadataDto struct {
	Title       string      `json:"title,omitempty"`
	TitleSort   string      `json:"titleSort,omitempty"`
	Summary     string      `json:"summary,omitempty"`
	Number      string      `json:"number,omitempty"`
	NumberSort  *float64    `json:"numberSort,omitempty"`
	ReleaseDate string      `json:"releaseDate,omitempty"`
	Isbn        string      `json:"isbn,omitempty"`
	Authors     []AuthorDto `json:"authors,omitempty"`
	Tags        []string    `json:"tags,omitempty"`
	Links       []LinkDto   `json:"links,omitempty"`

	TitleLock      *bool `json:"titleLock,omitempty"`
	TitleSortLock  *bool `json:"titleSortLock,omitempty"`
	SummaryLock    *bool `json:"summaryLock,omitempty"`
	NumberLock     *bool `json:"numberLock,omitempty"`
	NumberSortLock *bool `json:"numberSortLock,omitempty"`
	ReleaseDateLock *bool `json:"releaseDateLock,omitempty"`
	IsbnLock       *bool `json:"isbnLock,omitempty"`
	AuthorsLock    *bool `json:"authorsLock,omitempty"`
	TagsLock       *bool `json:"tagsLock,omitempty"`
	LinksLock      *bool `json:"linksLock,omitempty"`
}

// BookDto 单册。
type BookDto struct {
	ID       string           `json:"id"`
	Name     string           `json:"name"`
	Number   int              `json:"number,omitempty"`
	SeriesID string           `json:"seriesId"`
	URL      string           `json:"url,omitempty"`
	FileSize int64            `json:"fileSize,omitempty"`
	Deleted  bool             `json:"deleted,omitempty"`
	Metadata *BookMetadataDto `json:"metadata,omitempty"`
	Oneshot  bool             `json:"oneshot,omitempty"`
}

// Page Komga 分页结构。
type Page[T any] struct {
	Content         []T `json:"content"`
	TotalElements   int64 `json:"totalElements"`
	NumberOfElements int  `json:"numberOfElements,omitempty"`
}

// ThumbnailDto 缩略图。
type ThumbnailDto struct {
	ID       string `json:"id"`
	Type     string `json:"type,omitempty"`
	Selected bool   `json:"selected,omitempty"`
	FileSize int64  `json:"fileSize,omitempty"`
}

// CollectionDto 收藏夹。
type CollectionDto struct {
	ID        string   `json:"id"`
	Name      string   `json:"name"`
	SeriesIds []string `json:"seriesIds,omitempty"`
}

// condition 查询条件构造（仅实现用到的 is / isFalse）。
type opValue struct {
	Operator string `json:"operator"`
	Value    string `json:"value"`
}

// SeriesInLibraryQuery series.list 的查询条件：库内 + 未删除。
func SeriesInLibraryQuery(libraryID string) map[string]any {
	return map[string]any{
		"condition": map[string]any{
			"allOf": []any{
				map[string]any{"libraryId": opValue{Operator: "is", Value: libraryID}},
				map[string]any{"deleted": opValue{Operator: "isFalse"}},
			},
		},
	}
}

// BooksInSeriesQuery books.list 的查询条件：系列内 + 未删除。
func BooksInSeriesQuery(seriesID string) map[string]any {
	return map[string]any{
		"condition": map[string]any{
			"allOf": []any{
				map[string]any{"seriesId": opValue{Operator: "is", Value: seriesID}},
				map[string]any{"deleted": opValue{Operator: "isFalse"}},
			},
		},
	}
}
