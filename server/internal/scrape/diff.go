package scrape

import (
	"strconv"
	"strings"

	"komgabangumi/internal/komga"
)

// diffSeriesMeta 系列元数据差异：{字段: [旧值, 新值]}，只列本次真的要写的字段
// （与 CleanMeta 一致：null / 空串 / 0 都不写，所以也不出现在差异里）。
func diffSeriesMeta(old, meta *komga.SeriesMetadataDto) map[string][2]any {
	diff := map[string][2]any{}
	addStr := func(name, oldV, newV string) {
		if newV == "" || oldV == newV {
			return
		}
		diff[name] = [2]any{oldV, newV}
	}
	addStr("title", oldTitle(old), meta.Title)
	addStr("titleSort", oldTitleSort(old), meta.TitleSort)
	addStr("status", oldStatus(old), meta.Status)
	addStr("summary", oldSummary(old), meta.Summary)
	addStr("publisher", oldPublisher(old), meta.Publisher)
	addStr("language", oldLanguage(old), meta.Language)
	addStr("readingDirection", oldReadingDirection(old), meta.ReadingDirection)
	addStr("releaseDate", oldReleaseDate(old), meta.ReleaseDate)

	if old == nil {
		old = &komga.SeriesMetadataDto{}
	}
	if meta.AgeRating != nil && (old.AgeRating == nil || *meta.AgeRating != *old.AgeRating) {
		var oldV any
		if old.AgeRating != nil {
			oldV = *old.AgeRating
		}
		diff["ageRating"] = [2]any{oldV, *meta.AgeRating}
	}
	if meta.TotalBookCount != nil && *meta.TotalBookCount > 0 &&
		(old.TotalBookCount == nil || *meta.TotalBookCount != *old.TotalBookCount) {
		var oldV any
		if old.TotalBookCount != nil {
			oldV = *old.TotalBookCount
		}
		diff["totalBookCount"] = [2]any{oldV, *meta.TotalBookCount}
	}

	// 数组字段：新列表与旧列表不同才记（整个列表作为新值）
	if meta.Tags != nil && !strSliceEqual(old.Tags, meta.Tags) {
		diff["tags"] = [2]any{toAnySlice(old.Tags), toAnySlice(meta.Tags)}
	}
	if meta.Genres != nil && !strSliceEqualFold(old.Genres, meta.Genres) {
		diff["genres"] = [2]any{toAnySlice(old.Genres), toAnySlice(meta.Genres)}
	}
	if meta.Authors != nil && len(meta.Authors) > 0 && len(old.Authors) == 0 {
		diff["authors"] = [2]any{toAnySlice(nil), authorsToAny(meta.Authors)}
	}
	if meta.Links != nil {
		var added []komga.LinkDto
		for _, l := range meta.Links {
			found := false
			for _, x := range old.Links {
				if x.URL == l.URL {
					found = true
					break
				}
			}
			if !found {
				added = append(added, l)
			}
		}
		if len(added) > 0 {
			diff["links"] = [2]any{toAnySlice(nil), linksToAny(meta.Links)}
		}
	}
	if meta.AlternateTitles != nil && len(meta.AlternateTitles) > 0 && len(old.AlternateTitles) == 0 {
		diff["alternateTitles"] = [2]any{toAnySlice(nil), altTitlesToAny(meta.AlternateTitles)}
	}
	return diff
}

// diffBookMeta 单册元数据差异。
func diffBookMeta(old, meta *komga.BookMetadataDto) map[string][2]any {
	diff := map[string][2]any{}
	if old == nil {
		old = &komga.BookMetadataDto{}
	}
	addStr := func(name, oldV, newV string) {
		if newV == "" || oldV == newV {
			return
		}
		diff[name] = [2]any{oldV, newV}
	}
	addStr("title", old.Title, meta.Title)
	addStr("summary", old.Summary, meta.Summary)
	addStr("releaseDate", old.ReleaseDate, meta.ReleaseDate)
	addStr("isbn", old.Isbn, meta.Isbn)
	addStr("number", old.Number, meta.Number)
	if len(meta.Authors) > 0 && len(old.Authors) == 0 {
		diff["authors"] = [2]any{toAnySlice(nil), authorsToAny(meta.Authors)}
	}
	return diff
}

func oldTitle(m *komga.SeriesMetadataDto) string {
	if m != nil {
		return m.Title
	}
	return ""
}
func oldTitleSort(m *komga.SeriesMetadataDto) string {
	if m != nil {
		return m.TitleSort
	}
	return ""
}
func oldStatus(m *komga.SeriesMetadataDto) string {
	if m != nil {
		return m.Status
	}
	return ""
}
func oldSummary(m *komga.SeriesMetadataDto) string {
	if m != nil {
		return m.Summary
	}
	return ""
}
func oldPublisher(m *komga.SeriesMetadataDto) string {
	if m != nil {
		return m.Publisher
	}
	return ""
}
func oldLanguage(m *komga.SeriesMetadataDto) string {
	if m != nil {
		return m.Language
	}
	return ""
}
func oldReadingDirection(m *komga.SeriesMetadataDto) string {
	if m != nil {
		return m.ReadingDirection
	}
	return ""
}
func oldReleaseDate(m *komga.SeriesMetadataDto) string {
	if m != nil {
		return m.ReleaseDate
	}
	return ""
}

func strSliceEqual(a, b []string) bool {
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

func strSliceEqualFold(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if !equalFoldASCII(a[i], b[i]) {
			return false
		}
	}
	return true
}

func equalFoldASCII(a, b string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := 0; i < len(a); i++ {
		ca, cb := a[i], b[i]
		if ca >= 'A' && ca <= 'Z' {
			ca += 32
		}
		if cb >= 'A' && cb <= 'Z' {
			cb += 32
		}
		if ca != cb {
			return false
		}
	}
	return true
}

func toAnySlice(s []string) []any {
	out := make([]any, 0, len(s))
	for _, v := range s {
		out = append(out, v)
	}
	return out
}

func authorsToAny(a []komga.AuthorDto) []any {
	out := make([]any, 0, len(a))
	for _, v := range a {
		out = append(out, map[string]any{"name": v.Name, "role": v.Role})
	}
	return out
}

func linksToAny(l []komga.LinkDto) []any {
	out := make([]any, 0, len(l))
	for _, v := range l {
		out = append(out, map[string]any{"label": v.Label, "url": v.URL})
	}
	return out
}

func altTitlesToAny(a []komga.AlternateTitleDto) []any {
	out := make([]any, 0, len(a))
	for _, v := range a {
		out = append(out, map[string]any{"label": v.Label, "title": v.Title})
	}
	return out
}

// summarizeMeta 日志用元数据摘要（对齐 SummarizeMeta）。
func summarizeMeta(m *komga.SeriesMetadataDto) string {
	parts := []string{"title=" + m.Title}
	if s := m.Summary; s != "" {
		r := []rune(s)
		if len(r) > 40 {
			s = string(r[:40]) + "…"
		}
		parts = append(parts, "summary="+s)
	} else {
		parts = append(parts, "summary=")
	}
	parts = append(parts, "publisher="+m.Publisher, "status="+m.Status)
	if m.AgeRating != nil {
		parts = append(parts, "ageRating="+itoa(*m.AgeRating))
	} else {
		parts = append(parts, "ageRating=null")
	}
	if m.TotalBookCount != nil {
		parts = append(parts, "totalBookCount="+itoa(*m.TotalBookCount))
	} else {
		parts = append(parts, "totalBookCount=null")
	}
	var authorNames []string
	for _, a := range m.Authors {
		authorNames = append(authorNames, a.Name+"("+a.Role+")")
	}
	parts = append(parts, "authors=["+joinStr(authorNames, ";")+"]")
	if len(m.Tags) > 15 {
		parts = append(parts, "tags=["+joinStr(m.Tags[:15], ",")+",…]")
	} else {
		parts = append(parts, "tags=["+joinStr(m.Tags, ",")+"]")
	}
	parts = append(parts, "genres=["+joinStr(m.Genres, ",")+"]")
	rd := m.ReadingDirection
	if rd == "" {
		rd = "-"
	}
	parts = append(parts, "readingDirection="+rd)
	rd2 := m.ReleaseDate
	if rd2 == "" {
		rd2 = "-"
	}
	parts = append(parts, "releaseDate="+rd2)
	var labels []string
	for _, l := range m.Links {
		labels = append(labels, l.Label)
	}
	parts = append(parts, "links=["+joinStr(labels, ";")+"]")
	parts = append(parts, "altTitles="+itoa(len(m.AlternateTitles)))
	return joinStr(parts, " ")
}

func itoa(n int) string { return strconv.Itoa(n) }

func joinStr(list []string, sep string) string { return strings.Join(list, sep) }
