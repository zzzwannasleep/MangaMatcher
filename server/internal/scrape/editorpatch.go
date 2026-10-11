package scrape

import (
	"komgabangumi/internal/komga"
	"komgabangumi/internal/text"
)

// 编辑器 PATCH 体构建（移植 SeriesEditorPatch.cs，纯逻辑可测）：
// ① 只发用户改过的字段；② 锁字段原样回传避免翻转；③ MACHINE 标签必打。

// SeriesEditInput 编辑器保存时收集的字段。nil = 用户没动（不写入 PATCH），
// 非 nil = 用户明确要的值（与当前值相同则跳过）。空串对文本字段 = 想清空。
type SeriesEditInput struct {
	Title             string                   `json:"title,omitempty"`
	TitleSort         string                   `json:"titleSort,omitempty"`
	Status            string                   `json:"status,omitempty"`
	ReadingDirection  string                   `json:"readingDirection,omitempty"`
	Publisher         string                   `json:"publisher,omitempty"`
	Language          string                   `json:"language,omitempty"`
	ReleaseDate       string                   `json:"releaseDate,omitempty"`
	Summary           string                   `json:"summary,omitempty"`
	AgeRating         *int                     `json:"ageRating,omitempty"`
	TotalBookCount    *int                     `json:"totalBookCount,omitempty"`
	Tags              []string                 `json:"tags,omitempty"`
	Genres            []string                 `json:"genres,omitempty"`
	Authors           []komga.AuthorDto        `json:"authors,omitempty"`
	AlternateTitles   []komga.AlternateTitleDto `json:"alternateTitles,omitempty"`
	Links             []komga.LinkDto          `json:"links,omitempty"`
	// 设定标记：以下字段是否被用户改过（区分「没提交」与「提交了空串=清空」）
	TitleSet            *bool `json:"-"`
	TitleSortSet        *bool `json:"-"`
	StatusSet           *bool `json:"-"`
	ReadingDirectionSet *bool `json:"-"`
	PublisherSet        *bool `json:"-"`
	LanguageSet         *bool `json:"-"`
	ReleaseDateSet      *bool `json:"-"`
	SummarySet          *bool `json:"-"`
}

// BookEditInput 单卷编辑输入（封面字节由前端直接上传，不进 PATCH 体）。
type BookEditInput struct {
	Title       string `json:"title,omitempty"`
	Number      string `json:"number,omitempty"`
	ReleaseDate string `json:"releaseDate,omitempty"`
	Isbn        string `json:"isbn,omitempty"`
	Summary     string `json:"summary,omitempty"`

	TitleSet       *bool `json:"-"`
	NumberSet      *bool `json:"-"`
	ReleaseDateSet *bool `json:"-"`
	IsbnSet        *bool `json:"-"`
	SummarySet     *bool `json:"-"`
}

func setIf(set *bool, newV, cur string, body map[string]any, key string) {
	if set == nil || !*set {
		return
	}
	if newV == cur {
		return
	}
	body[key] = newV
}

// BuildSeriesPatch 构建系列 PATCH 体：
// 只发用户改过的字段；锁字段原样回传（PATCH 不会翻转锁状态）；MACHINE 标签必打。
func BuildSeriesPatch(current *komga.SeriesMetadataDto, input SeriesEditInput) map[string]any {
	if current == nil {
		current = &komga.SeriesMetadataDto{}
	}
	body := map[string]any{}
	setIf(input.TitleSet, input.Title, current.Title, body, "title")
	setIf(input.TitleSortSet, input.TitleSort, current.TitleSort, body, "titleSort")
	setIf(input.StatusSet, input.Status, current.Status, body, "status")
	setIf(input.ReadingDirectionSet, input.ReadingDirection, current.ReadingDirection, body, "readingDirection")
	setIf(input.PublisherSet, input.Publisher, current.Publisher, body, "publisher")
	setIf(input.LanguageSet, input.Language, current.Language, body, "language")
	setIf(input.ReleaseDateSet, input.ReleaseDate, current.ReleaseDate, body, "releaseDate")
	setIf(input.SummarySet, input.Summary, current.Summary, body, "summary")
	if input.AgeRating != nil && (current.AgeRating == nil || *input.AgeRating != *current.AgeRating) {
		body["ageRating"] = *input.AgeRating
	}
	if input.TotalBookCount != nil && *input.TotalBookCount > 0 &&
		(current.TotalBookCount == nil || *input.TotalBookCount != *current.TotalBookCount) {
		body["totalBookCount"] = *input.TotalBookCount
	}
	if input.Genres != nil && !sameList(input.Genres, current.Genres) {
		body["genres"] = input.Genres
	}
	if input.Authors != nil && !sameAuthors(input.Authors, current.Authors) {
		body["authors"] = input.Authors
	}
	if input.AlternateTitles != nil && !sameAlt(input.AlternateTitles, current.AlternateTitles) {
		body["alternateTitles"] = input.AlternateTitles
	}
	if input.Links != nil && !sameLinks(input.Links, current.Links) {
		body["links"] = input.Links
	}

	// 标签：编辑器保存 = 程序写入 → MACHINE 必打（TagsLock 时也随 PATCH 发）
	if input.Tags != nil {
		finalTags := append([]string{}, input.Tags...)
		if !containsStr(finalTags, text.MachineTag) {
			finalTags = append(finalTags, text.MachineTag)
		}
		if !sameList(finalTags, current.Tags) {
			body["tags"] = finalTags
		}
	} else if !containsStr(current.Tags, text.MachineTag) {
		t := append([]string{}, current.Tags...)
		t = append(t, text.MachineTag)
		body["tags"] = t
	}

	// 锁：当前值原样回传（PATCH 不会翻转锁状态）
	for name, lock := range map[string]*bool{
		"statusLock": current.StatusLock, "titleLock": current.TitleLock,
		"titleSortLock": current.TitleSortLock, "summaryLock": current.SummaryLock,
		"publisherLock": current.PublisherLock, "ageRatingLock": current.AgeRatingLock,
		"genresLock": current.GenresLock, "tagsLock": current.TagsLock,
		"totalBookCountLock": current.TotalBookCountLock, "alternateTitlesLock": current.AlternateTitlesLock,
		"authorsLock": current.AuthorsLock, "linksLock": current.LinksLock,
		"languageLock": current.LanguageLock, "readingDirectionLock": current.ReadingDirectionLock,
		"releaseDateLock": current.ReleaseDateLock,
	} {
		if lock != nil {
			body[name] = *lock
		}
	}
	return body
}

// BuildBookPatch 构建单卷 PATCH 体（只发改动字段 + 锁字段回传）。
func BuildBookPatch(current *komga.BookMetadataDto, input BookEditInput) map[string]any {
	if current == nil {
		current = &komga.BookMetadataDto{}
	}
	body := map[string]any{}
	setIf(input.TitleSet, input.Title, current.Title, body, "title")
	setIf(input.NumberSet, input.Number, current.Number, body, "number")
	setIf(input.ReleaseDateSet, input.ReleaseDate, current.ReleaseDate, body, "releaseDate")
	setIf(input.IsbnSet, input.Isbn, current.Isbn, body, "isbn")
	setIf(input.SummarySet, input.Summary, current.Summary, body, "summary")
	for name, lock := range map[string]*bool{
		"titleLock": current.TitleLock, "titleSortLock": current.TitleSortLock,
		"summaryLock": current.SummaryLock, "numberLock": current.NumberLock,
		"numberSortLock": current.NumberSortLock, "releaseDateLock": current.ReleaseDateLock,
		"isbnLock": current.IsbnLock, "authorsLock": current.AuthorsLock,
		"tagsLock": current.TagsLock, "linksLock": current.LinksLock,
	} {
		if lock != nil {
			body[name] = *lock
		}
	}
	return body
}

func sameList(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func sameAuthors(a, b []komga.AuthorDto) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i].Name != b[i].Name || a[i].Role != b[i].Role {
			return false
		}
	}
	return true
}

func sameAlt(a, b []komga.AlternateTitleDto) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i].Title != b[i].Title || a[i].Label != b[i].Label {
			return false
		}
	}
	return true
}

func sameLinks(a, b []komga.LinkDto) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i].URL != b[i].URL || a[i].Label != b[i].Label {
			return false
		}
	}
	return true
}
