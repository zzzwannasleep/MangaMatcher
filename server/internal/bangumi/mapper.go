package bangumi

import (
	"encoding/json"
	"math"
	"regexp"
	"strconv"
	"strings"

	"komgabangumi/internal/komga"
	"komgabangumi/internal/text"
)

// SubjectMapper：Bangumi 条目 → Komga 系列元数据映射，移植自 SubjectMapper.cs。

// ParseInfobox 从 infobox 提取指定 key 的值；数组以 、 连接（对齐脚本 parseInfobox）。
func ParseInfobox(infobox []InfoboxItemDto, targetKey string) string {
	for _, item := range infobox {
		if item.Key != targetKey || len(item.Value) == 0 {
			continue
		}
		return infoboxValueToString(item.Value)
	}
	return ""
}

func infoboxValueToString(raw json.RawMessage) string {
	var s string
	if err := json.Unmarshal(raw, &s); err == nil {
		return s
	}
	var arr []json.RawMessage
	if err := json.Unmarshal(raw, &arr); err == nil {
		var parts []string
		for _, e := range arr {
			var es string
			if err := json.Unmarshal(e, &es); err == nil {
				parts = append(parts, es)
				continue
			}
			var obj map[string]any
			if err := json.Unmarshal(e, &obj); err == nil {
				if v, ok := obj["v"].(string); ok {
					parts = append(parts, v)
				}
			}
		}
		return strings.Join(parts, "、")
	}
	var obj map[string]any
	if err := json.Unmarshal(raw, &obj); err == nil {
		if v, ok := obj["v"].(string); ok {
			return v
		}
		if v, ok := obj["v"]; ok {
			b, _ := json.Marshal(v)
			return string(b)
		}
	}
	return ""
}

// ExtractAliases 提取别名（别名/別名/版本名，含嵌套），以 " / " 连接（对齐脚本 extractAliases）。
func ExtractAliases(infobox []InfoboxItemDto) string {
	var items []string
	seen := map[string]struct{}{}
	add := func(s string) {
		if strings.TrimSpace(s) == "" {
			return
		}
		s = strings.TrimSpace(s)
		if _, ok := seen[s]; ok {
			return
		}
		seen[s] = struct{}{}
		items = append(items, s)
	}
	isAliasKey := func(k string) bool { return k == "别名" || k == "別名" || k == "版本名" }

	for _, item := range infobox {
		if isAliasKey(item.Key) && len(item.Value) > 0 {
			var s string
			if err := json.Unmarshal(item.Value, &s); err == nil {
				add(s)
			} else {
				var arr []json.RawMessage
				if err := json.Unmarshal(item.Value, &arr); err == nil {
					for _, e := range arr {
						var es string
						if err := json.Unmarshal(e, &es); err == nil {
							add(es)
						}
					}
				}
			}
		}
		// 数组内的嵌套对象条目 {"k":"别名","v":...}
		var arr []json.RawMessage
		if err := json.Unmarshal(item.Value, &arr); err == nil {
			for _, e := range arr {
				var obj map[string]json.RawMessage
				if err := json.Unmarshal(e, &obj); err != nil {
					continue
				}
				var k string
				if err := json.Unmarshal(obj["k"], &k); err != nil || !isAliasKey(k) {
					continue
				}
				var sv string
				if err := json.Unmarshal(obj["v"], &sv); err == nil {
					add(sv)
				}
			}
		}
	}
	return strings.Join(items, " / ")
}

var (
	reTrailingBracket = regexp.MustCompile(`[《【（\[\(][^》】）\]\)]*[》】）\]\)]\s*$`)
	reAuthorBracket   = regexp.MustCompile(`[《【（\[\(（][^》】）\]\)）]*[》】）\]\)）]`)
	reAuthorSplit     = regexp.MustCompile(`[/／、_→・:×&,，]`)
)

// FirstAuthor 搜索候选作者：取第一个名字并去掉尾部括注。
func FirstAuthor(authorValue string) string {
	first := reAuthorSplit.Split(authorValue, -1)[0]
	first = strings.TrimSpace(reTrailingBracket.ReplaceAllString(first, ""))
	if first == "" {
		return strings.TrimSpace(authorValue)
	}
	return first
}

// MapAgeRating ageRating 组合判定（对齐脚本 mapBangumiAgeRating）。
func MapAgeRating(apiAgeRating *int, nsfw *bool, tags []string) *int {
	if apiAgeRating != nil {
		switch *apiAgeRating {
		case 1:
			v := 15
			return &v
		case 2:
			v := 18
			return &v
		case 0:
			v := 0
			return &v
		default:
			return nil
		}
	}
	if nsfw != nil && *nsfw {
		v := 18
		return &v
	}
	for _, t := range tags {
		for _, a := range text.AdultTags {
			if t == a {
				v := 18
				return &v
			}
		}
	}
	return nil
}

// CleanMultiline summary 清洗：换行统一、逐行去首尾空白（含全角空格）。
func CleanMultiline(s string) string {
	if s == "" {
		return ""
	}
	s = strings.ReplaceAll(s, "\r\n", "\n")
	s = strings.ReplaceAll(s, "\r", "\n")
	lines := strings.Split(s, "\n")
	for i, l := range lines {
		lines[i] = strings.Trim(l, " \t\u3000")
	}
	return strings.TrimSpace(strings.Join(lines, "\n"))
}

// DetectLanguage 按题材标签判作品产地语言：韩漫 → ko，国漫/港漫 → zh，默认 → ja。
func DetectLanguage(tags []string) string {
	for _, t := range tags {
		for _, k := range text.KoreanTags {
			if strings.EqualFold(t, k) {
				return "ko"
			}
		}
	}
	for _, t := range tags {
		for _, k := range text.ChineseOriginTags {
			if t == k {
				return "zh"
			}
		}
	}
	return "ja"
}

// buildTags 标签构建：白名单过滤 + count 排序 + 动态阈值 + 评分标签 + 状态标签清理。
func buildTags(btv *SubjectDto, komgaSeriesName string) []string {
	var tags []string
	if len(btv.Tags) > 0 {
		type nt struct {
			name  string
			count int
		}
		var raw []nt
		for _, t := range btv.Tags {
			if text.IsValidTag(t.Name) {
				raw = append(raw, nt{t.Name, t.Count})
			}
		}
		// count 降序（稳定）
		for i := 1; i < len(raw); i++ {
			for j := i; j > 0 && raw[j].count > raw[j-1].count; j-- {
				raw[j], raw[j-1] = raw[j-1], raw[j]
			}
		}
		if len(raw) > 0 {
			maxTagCount := raw[0].count
			if maxTagCount < 1 {
				maxTagCount = 1
			}
			threshold := 3
			switch {
			case maxTagCount > 200:
				threshold = 35
			case maxTagCount > 125:
				threshold = 25
			case maxTagCount > 60:
				threshold = 15
			case maxTagCount > 30:
				threshold = 10
			case maxTagCount > 10:
				threshold = 5
			}
			var finalTags []string
			for _, t := range raw {
				if t.count >= threshold {
					finalTags = append(finalTags, t.name)
				}
			}
			if len(finalTags) < 10 {
				finalTags = nil
				for i, t := range raw {
					if i >= 10 {
						break
					}
					finalTags = append(finalTags, t.name)
				}
			}
			tags = append(tags, finalTags...)
		}
	}

	// 文件夹名出版社/来源关键词追加
	seriesNameS2T := text.T2S(komgaSeriesName)
	for _, k := range text.PublisherKeywords {
		if strings.Contains(seriesNameS2T, k) {
			dup := false
			for _, t := range tags {
				if t == k {
					dup = true
					break
				}
			}
			if !dup {
				tags = append(tags, k)
			}
			break
		}
	}

	// 评分标签
	if btv.Rating != nil && btv.Rating.Score != nil && *btv.Rating.Score > 0 {
		tags = append(tags, strconv.Itoa(int(math.Round(*btv.Rating.Score)))+"分")
	}

	// 状态标签清理：完结/已完结 只保留一个
	hasFinished := false
	keepTag := ""
	for _, t := range tags {
		if t == "已完结" || t == "完结" {
			hasFinished = true
			if t == "已完结" {
				keepTag = "已完结"
			} else if keepTag == "" {
				keepTag = "完结"
			}
		}
	}
	if hasFinished {
		var kept []string
		for _, t := range tags {
			isStatus := false
			for _, s := range text.StatusTags {
				if t == s {
					isStatus = true
					break
				}
			}
			if isStatus && t != keepTag {
				continue
			}
			kept = append(kept, t)
		}
		tags = kept
	}

	return tags
}

func fmtInt(n int) string { return strconv.Itoa(n) }

// buildStatus 状态判定（对齐脚本）：infobox 状态/连载状态/刊行状态 → 状态标签 → 结束/卷数推断。
func buildStatus(btv *SubjectDto, tags []string, infobox []InfoboxItemDto) string {
	statusVal := ParseInfobox(infobox, "状态")
	if statusVal == "" {
		statusVal = ParseInfobox(infobox, "连载状态")
	}
	if statusVal == "" {
		statusVal = ParseInfobox(infobox, "刊行状态")
	}
	if statusVal == "" {
		for _, t := range tags {
			for _, s := range text.StatusTags {
				if t == s {
					statusVal = t
					break
				}
			}
			if statusVal != "" {
				break
			}
		}
	}

	var status string
	if statusVal != "" {
		s := text.T2S(strings.ToLower(statusVal))
		switch {
		case strings.Contains(s, "休刊") || strings.Contains(s, "停刊") ||
			strings.Contains(s, "停止连载") || strings.Contains(s, "长期休载"):
			status = "HIATUS"
		case strings.Contains(s, "完结") || strings.Contains(s, "已完结") || strings.Contains(s, "腰斩"):
			status = "ENDED"
		case strings.Contains(s, "连载"):
			status = "ONGOING"
		}
	}

	// 显式「连载中」优先；无显式状态时，infobox 有「结束/完结」或 volumes>0 → 推断完结。
	if status == "ONGOING" {
		return status
	}
	if ParseInfobox(infobox, "结束") != "" || ParseInfobox(infobox, "完结") != "" ||
		(btv.Volumes != nil && *btv.Volumes > 0) {
		status = "ENDED"
	}
	return status
}

// buildAuthors infobox 角色映射 → Komga 作者列表。
func buildAuthors(btv *SubjectDto) []komga.AuthorDto {
	infobox := btv.Infobox
	var authors []komga.AuthorDto
	for _, ar := range text.AuthorRoles {
		val := ParseInfobox(infobox, ar.Key)
		if val == "" {
			continue
		}
		cleaned := reAuthorBracket.ReplaceAllString(val, "")
		for _, name := range reAuthorSplit.Split(cleaned, -1) {
			trimmed := strings.TrimSpace(name)
			if trimmed == "" {
				continue
			}
			dup := false
			for _, a := range authors {
				if a.Name == trimmed && a.Role == ar.Role {
					dup = true
					break
				}
			}
			if !dup {
				authors = append(authors, komga.AuthorDto{Name: text.T2S(trimmed), Role: ar.Role})
			}
		}
	}

	hasWriter, hasPenciller := false, false
	for _, a := range authors {
		if a.Role == "writer" {
			hasWriter = true
		}
		if a.Role == "penciller" {
			hasPenciller = true
		}
	}
	if !hasWriter {
		var toAdd []komga.AuthorDto
		for _, p := range authors {
			if p.Role != "penciller" {
				continue
			}
			dup := false
			for _, a := range authors {
				if a.Name == p.Name && a.Role == "writer" {
					dup = true
					break
				}
			}
			if !dup {
				toAdd = append(toAdd, komga.AuthorDto{Name: p.Name, Role: "writer"})
			}
		}
		authors = append(authors, toAdd...)
	}
	if !hasPenciller && btv.Platform == "漫画" {
		var toAdd []komga.AuthorDto
		for _, w := range authors {
			if w.Role != "writer" {
				continue
			}
			dup := false
			for _, a := range authors {
				if a.Name == w.Name && a.Role == "penciller" {
					dup = true
					break
				}
			}
			if !dup {
				toAdd = append(toAdd, komga.AuthorDto{Name: w.Name, Role: "penciller"})
			}
		}
		authors = append(authors, toAdd...)
	}
	return authors
}

// BuildSeriesMeta 构建系列元数据（未合并 Komga 现有数据；合并由 MergeWithExisting 完成）。
func BuildSeriesMeta(btv *SubjectDto, komgaSeries *komga.SeriesDto) *komga.SeriesMetadataDto {
	f := false
	meta := &komga.SeriesMetadataDto{
		Status:              "",
		StatusLock:          &f,
		Title:               "",
		TitleLock:           &f,
		TitleSort:           "",
		TitleSortLock:       &f,
		Tags:                []string{},
		TagsLock:            &f,
		Links:               []komga.LinkDto{{Label: "Btv", URL: LegacyBase + "/subject/" + itoa(btv.ID)}},
		LinksLock:           &f,
		Publisher:           "",
		PublisherLock:       &f,
		Summary:             "",
		SummaryLock:         &f,
		AlternateTitles:     []komga.AlternateTitleDto{},
		Authors:             []komga.AuthorDto{},
		AuthorsLock:         &f,
		AgeRating:           nil,
		AgeRatingLock:       &f,
	}

	// title：中文名优先，繁→简；无中文名时若原名是纯中文（含繁体）才转简，
	// 日文原名绝不过繁简转换（新字体汉字会被误转，如 進撃の巨人）
	var title string
	switch {
	case btv.NameCN != "":
		title = text.T2S(btv.NameCN)
	case text.IsHanOnly(btv.Name):
		title = text.T2S(btv.Name)
	default:
		title = btv.Name
	}
	title = text.StripCopySuffix(title) // 副本后缀（" (1)"）是污染不是标题
	meta.Title = title
	meta.TitleSort = title
	if komgaSeries.Metadata != nil && text.HasCopySuffix(komgaSeries.Metadata.Title) {
		meta.TitleLock = &f // 旧标题带副本后缀 → 中文名这次必须写入顶替
	}
	if btv.Name != "" && btv.Name != meta.Title {
		meta.AlternateTitles = append(meta.AlternateTitles,
			komga.AlternateTitleDto{Label: "原名", Title: text.Capitalize(btv.Name)})
	}

	// summary：逐行 trim（含全角空格）；纯中文（含繁体）→ 繁→简统一；日文原样
	rawSummary := CleanMultiline(btv.Summary)
	if text.IsHanOnly(rawSummary) {
		meta.Summary = text.T2S(rawSummary)
	} else {
		meta.Summary = rawSummary
	}

	infobox := btv.Infobox

	// status 先算：totalBookCount 的写入以完结状态为前提
	var tagList []string
	meta.Status = buildStatus(btv, tagList, infobox)

	// tags：白名单过滤 + count 排序 + 动态阈值（language/阅读方向判定依赖 tags）
	meta.Tags = buildTags(btv, komgaSeries.Name)
	tagList = meta.Tags

	// totalBookCount：仅完结时写（连载中的 volumes 是「已出版卷数」而非全卷数）
	if meta.Status == "ENDED" {
		if btv.Volumes != nil && *btv.Volumes > 0 {
			meta.TotalBookCount = btv.Volumes
		}
	}
	meta.TotalBookCountLock = &f

	// 语言 + 阅读方向
	meta.Language = DetectLanguage(tagList)
	meta.LanguageLock = &f
	if btv.Platform == "漫画" {
		ltr := false
		for _, t := range tagList {
			for _, w := range text.WebtoonTags {
				if strings.EqualFold(t, w) {
					ltr = true
				}
			}
			for _, w := range text.ChineseOriginTags {
				if t == w {
					ltr = true
				}
			}
			for _, w := range text.KoreanTags {
				if strings.EqualFold(t, w) {
					ltr = true
				}
			}
		}
		if ltr {
			meta.ReadingDirection = "LEFT_TO_RIGHT"
		} else {
			meta.ReadingDirection = "RIGHT_TO_LEFT"
		}
		meta.ReadingDirectionLock = &f
	}

	// 系列首发日：仅完整 yyyy-MM-dd 写入
	if len(btv.Date) >= 10 {
		meta.ReleaseDate = btv.Date
	}
	meta.ReleaseDateLock = &f

	// genres：保留现有 + platform（去重）
	var genres []string
	if komgaSeries.Metadata != nil {
		genres = append(genres, komgaSeries.Metadata.Genres...)
	}
	if btv.Platform != "" && !containsFold(genres, btv.Platform) {
		genres = append(genres, btv.Platform)
	}
	meta.Genres = genres

	meta.AgeRating = MapAgeRating(btv.AgeRating, btv.Nsfw, tagList)

	// publisher：infobox 出版社 → 连载杂志 → 制作；文件夹名出版社关键词兜底
	publisherVal := ParseInfobox(infobox, "出版社")
	if publisherVal == "" {
		publisherVal = ParseInfobox(infobox, "连载杂志")
	}
	if publisherVal == "" {
		publisherVal = ParseInfobox(infobox, "制作")
	}
	seriesNameS2T := text.T2S(komgaSeries.Name)
	var matchedKeyword string
	for _, k := range text.PublisherKeywords {
		if strings.Contains(seriesNameS2T, k) {
			matchedKeyword = k
			break
		}
	}
	if publisherVal != "" {
		meta.Publisher = text.T2S(strings.TrimSpace(rePublisherSplit.Split(publisherVal, -1)[0]))
	} else if matchedKeyword != "" && meta.Publisher == "" {
		meta.Publisher = matchedKeyword
	}

	// authors：infobox 角色映射
	meta.Authors = buildAuthors(btv)

	// 别名 → alternateTitles
	aliasStr := ExtractAliases(infobox)
	if aliasStr != "" {
		for _, alias := range strings.Split(aliasStr, " / ") {
			if alias == "" {
				continue
			}
			aliasLower := strings.ToLower(alias)
			titleLower := strings.ToLower(meta.Title)
			oriLower := strings.ToLower(btv.Name)
			if aliasLower == titleLower || aliasLower == oriLower {
				continue
			}
			dup := false
			for _, at := range meta.AlternateTitles {
				if strings.ToLower(at.Title) == aliasLower {
					dup = true
					break
				}
			}
			if !dup {
				meta.AlternateTitles = append(meta.AlternateTitles,
					komga.AlternateTitleDto{Label: "别名", Title: text.Capitalize(alias)})
			}
		}
	}

	return meta
}

var rePublisherSplit = regexp.MustCompile(`[/／、_→×&,，]`)

func itoa(n int) string { return strconv.Itoa(n) }

func containsFold(list []string, v string) bool {
	for _, s := range list {
		if strings.EqualFold(s, v) {
			return true
		}
	}
	return false
}

// MergeWithExisting 与 Komga 现有元数据合并：links 合并（新链接优先）、tags 合并+t2s+等价归一+去重、
// alternateTitles 合并去重（原名优先）；锁字段跳过。
func MergeWithExisting(meta *komga.SeriesMetadataDto, komgaMeta *komga.SeriesMetadataDto) *komga.SeriesMetadataDto {
	if komgaMeta == nil {
		return meta
	}

	// links：新链接排前面（同 label 以新为准），再并入旧链接里 label 不冲突的部分
	var combinedLinks []komga.LinkDto
	combinedLinks = append(combinedLinks, meta.Links...)
	for _, l := range komgaMeta.Links {
		combinedLinks = append(combinedLinks, l)
	}
	var links []komga.LinkDto
	seenLabel := map[string]struct{}{}
	for _, l := range combinedLinks {
		if l.Label == "" {
			continue
		}
		key := strings.ToLower(l.Label)
		if _, ok := seenLabel[key]; ok {
			continue
		}
		seenLabel[key] = struct{}{}
		links = append(links, l)
	}
	meta.Links = links

	// tags
	var combinedTags []string
	combinedTags = append(combinedTags, komgaMeta.Tags...)
	combinedTags = append(combinedTags, meta.Tags...)
	seenTag := map[string]struct{}{}
	var tags []string
	for _, t := range combinedTags {
		t = text.NormalizeEqualLabel(text.T2S(t))
		if t == "" {
			continue
		}
		if _, ok := seenTag[t]; ok {
			continue
		}
		seenTag[t] = struct{}{}
		tags = append(tags, t)
	}
	meta.Tags = tags

	// alternateTitles
	type altKey struct {
		title string
	}
	var alts []komga.AlternateTitleDto
	alts = append(alts, komgaMeta.AlternateTitles...)
	alts = append(alts, meta.AlternateTitles...)
	// 排序：原名(0) < 其他(1) < 别名(2)，稳定
	for i := 1; i < len(alts); i++ {
		for j := i; j > 0 && altOrder(alts[j].Label) < altOrder(alts[j-1].Label); j-- {
			alts[j], alts[j-1] = alts[j-1], alts[j]
		}
	}
	seenAlt := map[altKey]struct{}{}
	var mergedAlts []komga.AlternateTitleDto
	for _, t := range alts {
		if t.Title == "" {
			continue
		}
		k := altKey{strings.ToLower(t.Title)}
		if _, ok := seenAlt[k]; ok {
			continue
		}
		seenAlt[k] = struct{}{}
		mergedAlts = append(mergedAlts, t)
	}
	meta.AlternateTitles = mergedAlts

	// 锁字段：删除对应值
	if komgaMeta.StatusLock != nil && *komgaMeta.StatusLock {
		meta.Status, meta.StatusLock = "", nil
	}
	if komgaMeta.TitleLock != nil && *komgaMeta.TitleLock {
		meta.Title, meta.TitleLock = "", nil
	}
	if komgaMeta.TitleSortLock != nil && *komgaMeta.TitleSortLock {
		meta.TitleSort, meta.TitleSortLock = "", nil
	}
	if komgaMeta.SummaryLock != nil && *komgaMeta.SummaryLock {
		meta.Summary, meta.SummaryLock = "", nil
	}
	if komgaMeta.PublisherLock != nil && *komgaMeta.PublisherLock {
		meta.Publisher, meta.PublisherLock = "", nil
	}
	if komgaMeta.AgeRatingLock != nil && *komgaMeta.AgeRatingLock {
		meta.AgeRating, meta.AgeRatingLock = nil, nil
	}
	if komgaMeta.TagsLock != nil && *komgaMeta.TagsLock {
		meta.Tags, meta.TagsLock = nil, nil
	}
	if komgaMeta.LinksLock != nil && *komgaMeta.LinksLock {
		meta.Links, meta.LinksLock = nil, nil
	}
	if komgaMeta.AlternateTitlesLock != nil && *komgaMeta.AlternateTitlesLock {
		meta.AlternateTitles, meta.AlternateTitlesLock = nil, nil
	}
	if komgaMeta.AuthorsLock != nil && *komgaMeta.AuthorsLock {
		meta.Authors, meta.AuthorsLock = nil, nil
	}
	if komgaMeta.TotalBookCountLock != nil && *komgaMeta.TotalBookCountLock {
		meta.TotalBookCount, meta.TotalBookCountLock = nil, nil
	}
	if komgaMeta.LanguageLock != nil && *komgaMeta.LanguageLock {
		meta.Language, meta.LanguageLock = "", nil
	}
	if komgaMeta.ReadingDirectionLock != nil && *komgaMeta.ReadingDirectionLock {
		meta.ReadingDirection, meta.ReadingDirectionLock = "", nil
	}
	if komgaMeta.ReleaseDateLock != nil && *komgaMeta.ReleaseDateLock {
		meta.ReleaseDate, meta.ReleaseDateLock = "", nil
	}

	return meta
}

func altOrder(label string) int {
	switch label {
	case "原名":
		return 0
	case "别名":
		return 2
	default:
		return 1
	}
}

// GetSeriesCoverURLs 系列封面 URL 组（image 最优先，其次 large/medium/common）。
func GetSeriesCoverURLs(btv *SubjectDto) []string {
	var urls []string
	if btv.Image != "" {
		urls = append(urls, btv.Image)
	}
	if btv.Images != nil {
		for _, u := range []string{btv.Images.Large, btv.Images.Medium, btv.Images.Common} {
			if u != "" {
				urls = append(urls, u)
			}
		}
	}
	seen := map[string]struct{}{}
	out := urls[:0]
	for _, u := range urls {
		if _, ok := seen[u]; ok {
			continue
		}
		seen[u] = struct{}{}
		out = append(out, u)
	}
	return out
}