package scrape

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"strconv"
	"strings"
	"time"

	"komgabangumi/internal/bangumi"
	"komgabangumi/internal/ehtags"
	"komgabangumi/internal/komga"
	"komgabangumi/internal/sources"
	"komgabangumi/internal/text"
)

// StateWriter 刮削状态库接口（api 层以 store 适配注入；nil = 不记录）。
type StateWriter interface {
	SetCover(seriesID string, ok bool)
	SetSummary(seriesID string, ok bool)
}

// Logger 源客户端日志接口别名（slog 适配）。
type Logger = sources.Logger

// SlogLogger slog.Logger → sources.Logger 适配器。
type SlogLogger struct{ L *slog.Logger }

// Warn 警告日志。
func (s SlogLogger) Warn(msg string) { s.L.Warn(msg) }

// Info 信息日志。
func (s SlogLogger) Info(msg string) { s.L.Info(msg) }

// Error 错误日志。
func (s SlogLogger) Error(msg string) { s.L.Error(msg) }

// SourceClients 六源客户端集合（nil = 该源未启用/未配置）。
type SourceClients struct {
	AI       *sources.AiClient
	BW       *sources.BookWalkerIndex
	Mox      *sources.MoxClient
	MangaDex *sources.MangaDexClient
	Baike    *sources.BaikeClient
	EH       *sources.EhClient
}

// WithSources 注入六源客户端（链式配置）。
func (s *Service) WithSources(sc SourceClients) *Service {
	s.AI, s.BW, s.Mox, s.MangaDex, s.Baike, s.EH = sc.AI, sc.BW, sc.Mox, sc.MangaDex, sc.Baike, sc.EH
	return s
}

// translateIfNeeded 简介中文化三步：已是中文 → 繁→简统一；非中文 → AI 翻译 → 译文再繁→简兜底。
func (s *Service) translateIfNeeded(t, label string) string {
	if strings.TrimSpace(t) == "" {
		return t
	}
	if !sources.NeedsChineseTranslation(t) {
		if text.IsHanOnly(t) {
			return text.T2S(t)
		}
		return t
	}
	if s.AI == nil || !s.TranslateSummary {
		return t
	}
	out, err := s.AI.TranslateToChinese(context.Background(), t)
	if err != nil {
		s.Log.Warn("《" + label + "》简介翻译失败，保留原文: " + err.Error())
		return t
	}
	if strings.TrimSpace(out) != "" && out != t {
		if !text.IsHanOnly(out) {
			s.Log.Warn("《" + label + "》AI 译文仍含非中文内容（日文残留），建议人工检查")
		} else {
			out = text.T2S(out)
		}
		return out
	}
	return t
}

// translateTitleIfNeeded 标题中文化：纯中文 → 繁→简；非中文 → AI 翻译（复用简介翻译开关）。
func (s *Service) translateTitleIfNeeded(title, label string) string {
	if strings.TrimSpace(title) == "" {
		return title
	}
	if text.IsHanOnly(title) {
		return text.T2S(title)
	}
	if s.AI == nil || !s.TranslateSummary {
		return title
	}
	out, err := s.AI.TranslateToChinese(context.Background(), title)
	if err != nil {
		return title
	}
	if strings.TrimSpace(out) == "" || out == title {
		return title
	}
	if !text.IsHanOnly(out) {
		s.Log.Warn("《" + label + "》AI 标题译文仍含非中文内容，保留原标题：" + title)
		return title
	}
	return text.T2S(out)
}

// fillAiSynopsis 各源都没有简介时让 AI 补写；返回空串表示没补上（Dry-run 不调用）。
func (s *Service) fillAiSynopsis(seriesName, originalTitle, author string, komgaMeta *komga.SeriesMetadataDto, dryRun bool) string {
	if s.AI == nil || !s.AIWriteSummary || strings.TrimSpace(komgaMeta.GetSummary()) != "" {
		return ""
	}
	if dryRun {
		s.Log.Info("[Dry-run] 《" + seriesName + "》各源均无简介，真实模式将用 AI 补写简介")
		return ""
	}
	s.Log.Info("《" + seriesName + "》各源均无简介，AI 补写中…")
	synopsis, err := s.AI.GenerateSynopsis(context.Background(), seriesName, originalTitle, author)
	if err != nil || strings.TrimSpace(synopsis) == "" {
		s.Log.Info("《" + seriesName + "》AI 无法确认这部作品，未补写简介（宁缺毋滥）")
		return ""
	}
	s.Log.Info(fmt.Sprintf("《%s》AI 简介已生成（%d 字）", seriesName, len([]rune(synopsis))))
	if text.IsHanOnly(synopsis) {
		return text.T2S(synopsis)
	}
	return synopsis
}

// downloadImage 封面下载（带代理；百科图走 bkimg 三段回退）。
func (s *Service) downloadImage(u string) ([]byte, error) {
	if strings.Contains(strings.ToLower(u), "bkimg") && s.Baike != nil {
		return s.Baike.DownloadImage(context.Background(), u)
	}
	client := s.CoverHTTP
	if client == nil {
		client = http.DefaultClient
	}
	req, err := http.NewRequest(http.MethodGet, u, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36")
	referer := s.CoverReferer
	if strings.Contains(strings.ToLower(u), "bookwalker") {
		referer = "https://bookwalker.jp/" // BW CDN 按来源站校验
	}
	// EH 图床（ehgt.org）与画廊页同样按 Referer 校验，统一带表站地址
	if lowU := strings.ToLower(u); strings.Contains(lowU, "ehgt.org") ||
		strings.Contains(lowU, "e-hentai.org") || strings.Contains(lowU, "exhentai.org") {
		referer = "https://e-hentai.org/"
	}
	if referer != "" {
		req.Header.Set("Referer", referer)
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("下载封面失败: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		return nil, fmt.Errorf("下载封面失败 %d: %s", resp.StatusCode, u)
	}
	return io.ReadAll(resp.Body)
}

// ClearedForReplace 纠正匹配（ReplaceMatch）的数据视图：旧匹配写入的来源字段全部清空，
// 保留 tags/genres/阅读方向/发售日/总卷数（用户侧或语言无关的数据）；links 剔除旧来源。
func ClearedForReplace(src *komga.SeriesMetadataDto) *komga.SeriesMetadataDto {
	if src == nil {
		return nil
	}
	return &komga.SeriesMetadataDto{
		Tags:                 src.Tags,
		Genres:               src.Genres,
		TagsLock:             src.TagsLock,
		GenresLock:           src.GenresLock,
		TotalBookCount:       src.TotalBookCount,
		TotalBookCountLock:   src.TotalBookCountLock,
		ReadingDirection:     src.ReadingDirection,
		ReadingDirectionLock: src.ReadingDirectionLock,
		ReleaseDate:          src.ReleaseDate,
		ReleaseDateLock:      src.ReleaseDateLock,
		Language:             src.Language,
		LanguageLock:         src.LanguageLock,
	}
}

// IsSourceLink 是否旧匹配来源链接（纠正匹配时剔除，让新链接顶替；api 包也用）。
func IsSourceLink(u string) bool {
	lower := strings.ToLower(u)
	for _, host := range []string{"bangumi.tv", "bgm.tv", "chii.in", "bookwalker", "bookof.moe", "kox.moe", "mangadex.org", "baike.baidu.com", "e-hentai.org", "exhentai.org", "ehgt.org"} {
		if strings.Contains(lower, host) {
			return true
		}
	}
	return false
}

// volumesCompatible 两边的「卷」是否可能是同一套：差太多（>25% 且不少于 2 册余量）放弃对齐。
func volumesCompatible(srcCount, bookCount int) bool {
	if srcCount <= 0 || bookCount <= 0 {
		return false
	}
	diff := srcCount - bookCount
	if diff < 0 {
		diff = -diff
	}
	allow := bookCount / 4
	if allow < 2 {
		allow = 2
	}
	return diff <= allow
}

// mergeBookWalkerVolumeMates BW 每卷数据合并进 Bangumi volumeMates：
// 只补空缺的 ISBN/发售日/单卷封面（语言无关），不覆盖中文简介。防串卷失败返回原列表与 0。
func (s *Service) mergeBookWalkerVolumeMates(volumeMates []bangumi.VolumeMate, bw *sources.BwSeries, seriesName string, bookCount int) ([]bangumi.VolumeMate, int) {
	if !volumesCompatible(len(bw.Volumes), bookCount) {
		s.Log.Info(fmt.Sprintf("《%s》BookWalker 卷数（%d）与本地册数（%d）差距过大，跳过每卷补空", seriesName, len(bw.Volumes), bookCount))
		return volumeMates, 0
	}
	added, filled := 0, 0
	for _, v := range bw.Volumes {
		if v.Order <= 0 {
			continue
		}
		num := itoa(v.Order)
		idx := -1
		for i := range volumeMates {
			if volumeMates[i].Num == num {
				idx = i
				break
			}
		}
		if idx < 0 {
			if len(volumeMates) >= 500 {
				break
			}
			volumeMates = append(volumeMates, bangumi.VolumeMate{Num: num,
				ReleaseDate: v.ReleaseDate, Isbn: v.Isbn, CoverURLs: sources.BuildCoverURLs(v.CoverName)})
			added++
		} else {
			if volumeMates[idx].ReleaseDate == "" && v.ReleaseDate != "" {
				volumeMates[idx].ReleaseDate = v.ReleaseDate
				filled++
			}
			if volumeMates[idx].Isbn == "" && v.Isbn != "" {
				volumeMates[idx].Isbn = v.Isbn
				filled++
			}
			if len(volumeMates[idx].CoverURLs) == 0 && v.CoverName != "" {
				volumeMates[idx].CoverURLs = sources.BuildCoverURLs(v.CoverName)
			}
		}
	}
	if added+filled > 0 {
		s.Log.Info(fmt.Sprintf("《%s》BookWalker 补充 %d 卷、补空 %d 项（ISBN/发售日/封面）", seriesName, added, filled))
	}
	return volumeMates, added + filled
}

// scrapeFromBookWalker 用 BookWalker 本地目录数据刮削系列（回退源）。
func (s *Service) scrapeFromBookWalker(series *komga.SeriesDto, bw *sources.BwSeries, replace bool) *SeriesResult {
	seriesID := series.ID
	seriesName := series.Name
	if series.Metadata != nil && series.Metadata.Title != "" {
		seriesName = series.Metadata.Title
	}
	res := &SeriesResult{}
	sink := &changeSink{}
	komgaMeta := series.Metadata
	if replace && komgaMeta != nil {
		komgaMeta = ClearedForReplace(komgaMeta)
	}
	s.Log.Info(fmt.Sprintf("刮削 《%s》 ← BookWalker 《%s》%s（目录含 %d 卷数据）",
		seriesName, bw.Title, dryTag(s.DryRun), len(bw.Volumes)))

	meta := &komga.SeriesMetadataDto{}
	hasAny := false
	linkAdded := false

	// ---- 标题 / 排序标题 ----
	if komgaMeta.GetTitle() == "" || text.HasCopySuffix(komgaMeta.GetTitle()) {
		bwTitle := text.StripCopySuffix(s.translateTitleIfNeeded(bw.Title, seriesName))
		meta.Title = bwTitle
		meta.TitleSort = bwTitle
		hasAny = true
	}

	// ---- 简介（BW 是日文/英文 + HTML → 去标签，按需翻译成中文）----
	if komgaMeta.GetSummary() == "" && strings.TrimSpace(bw.Description) != "" {
		meta.Summary = s.translateIfNeeded(sources.StripHtml(bw.Description), seriesName)
		hasAny = true
	}
	// 目录里也没有简介 → AI 直接补写
	if strings.TrimSpace(meta.Summary) == "" && komgaMeta.GetSummary() == "" {
		author := ""
		if len(bw.Authors) > 0 {
			author = bw.Authors[0].Name
		}
		meta.Summary = s.fillAiSynopsis(seriesName, bw.Title, author, komgaMeta, s.DryRun)
		if meta.Summary != "" {
			hasAny = true
		}
	}

	// ---- 出版社 / 出版品牌 ----
	if komgaMeta.GetPublisher() == "" && bw.Publisher != "" {
		meta.Publisher = bw.Publisher
		hasAny = true
	}
	// ---- 成人分级 ----
	if bw.IsAdult && komgaMeta.GetAgeRating() <= 0 {
		v := 18
		meta.AgeRating = &v
		hasAny = true
	}
	// ---- 总卷数 ----
	if bw.VolumeCount > 0 && komgaMeta.GetTotalBookCount() <= 0 {
		meta.TotalBookCount = &bw.VolumeCount
		hasAny = true
	}
	// ---- 状态：complete 标签才是完结；有卷数但未标 complete → 连载中 ----
	if komgaMeta.GetStatus() == "" && !komgaMeta.GetStatusLock() {
		status := ""
		if bw.Complete {
			status = "ENDED"
		} else if bw.VolumeCount > 0 {
			status = "ONGOING"
		}
		if status != "" {
			meta.Status = status
			hasAny = true
		}
	}
	// ---- 作者 ----
	if len(komgaMeta.Authors) == 0 && len(bw.Authors) > 0 {
		for _, a := range bw.Authors {
			role := "writer"
			if a.Role != 1 {
				role = "penciller"
			}
			meta.Authors = append(meta.Authors, komga.AuthorDto{Name: a.Name, Role: role})
		}
		hasAny = true
	}
	// ---- 别名 → alternateTitles ----
	if !komgaMeta.GetAlternateTitlesLock() {
		merged := append([]komga.AlternateTitleDto{}, komgaMeta.AlternateTitles...)
		seen := map[string]bool{}
		for _, t := range merged {
			seen[strings.ToLower(t.Title)] = true
		}
		added := 0
		for _, alt := range bw.AltTitles {
			v := strings.TrimSpace(alt)
			if v == "" || strings.EqualFold(v, bw.Title) || seen[strings.ToLower(v)] {
				continue
			}
			seen[strings.ToLower(v)] = true
			merged = append(merged, komga.AlternateTitleDto{Label: "别名", Title: v})
			added++
			if added >= 8 {
				break
			}
		}
		if added > 0 {
			meta.AlternateTitles = merged
			hasAny = true
		}
	}

	// ---- 类型 / 题材标签 + 阅读方向 ----
	if s.BwWriteTags {
		if len(bw.Genres) > 0 && !komgaMeta.GetGenresLock() {
			genres := append([]string{}, komgaMeta.Genres...)
			added := 0
			for _, g := range bw.Genres {
				if !containsFoldStr(genres, g) {
					genres = append(genres, g)
					added++
				}
			}
			if added > 0 {
				meta.Genres = genres
				hasAny = true
			}
		}
		newTags := buildBwTags(bw, komgaMeta.Tags)
		if len(newTags) > 0 && !komgaMeta.GetTagsLock() {
			tags := append([]string{}, komgaMeta.Tags...)
			tags = append(tags, newTags...)
			meta.Tags = dedup(tags)
			hasAny = true
		}
		// 漫画 → 从右往左阅读（Komga 默认 LEFT_TO_RIGHT）
		if bw.IsComic() && !komgaMeta.GetReadingDirectionLock() &&
			strings.EqualFold(orStr(komgaMeta.ReadingDirection, "LEFT_TO_RIGHT"), "LEFT_TO_RIGHT") {
			meta.ReadingDirection = "RIGHT_TO_LEFT"
			hasAny = true
		}
	}

	// 程序打标
	s.applyScrapeMarksV2(meta, series, komgaMeta)

	// ---- 系列首发日：目录里最早一卷的发售日 ----
	firstDate := ""
	for _, v := range bw.Volumes {
		if len(v.ReleaseDate) == 10 && (firstDate == "" || v.ReleaseDate < firstDate) {
			firstDate = v.ReleaseDate
		}
	}
	if firstDate != "" && komgaMeta.GetReleaseDate() == "" && !komgaMeta.GetReleaseDateLock() {
		meta.ReleaseDate = firstDate
		hasAny = true
	}

	// ---- 链接：追加 Bw（没有变化就不动 links）----
	if bw.SeriesURL != "" {
		links := append([]komga.LinkDto{}, komgaMeta.Links...)
		hasBw := false
		for _, l := range links {
			if strings.Contains(strings.ToLower(l.URL), "bookwalker") {
				hasBw = true
				break
			}
		}
		if !hasBw {
			links = append(links, komga.LinkDto{Label: "Bw", URL: bw.SeriesURL})
			meta.Links = links
			linkAdded = true
		}
	}

	if !hasAny && !linkAdded {
		s.Log.Info("《" + seriesName + "》BookWalker 系列字段均已存在，无需更新")
	} else if s.DryRun {
		s.Log.Info("[Dry-run] PATCH /series/" + seriesID + "/metadata（BookWalker）：" + summarizeMeta(meta))
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: "将更新系列元数据（BookWalker）", Detail: summarizeMeta(meta), FieldDiff: diffSeriesMeta(komgaMeta, meta)})
	} else {
		if err := s.Komga.PatchSeriesMetadata(seriesID, meta); err != nil {
			res.Error = "BookWalker 系列元数据 PATCH 失败: " + err.Error()
			s.Log.Error("《" + seriesName + "》BookWalker 系列元数据 PATCH 失败: " + err.Error())
			return res
		}
		s.Log.Info("《" + seriesName + "》系列信息已更新（BookWalker）")
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: "已更新系列元数据（BookWalker）", Detail: summarizeMeta(meta), FieldDiff: diffSeriesMeta(komgaMeta, meta)})
	}
	if !s.DryRun {
		s.clearStaleSeries(seriesID, meta, komgaMeta, seriesName)
		if strings.TrimSpace(orStr(meta.Summary, komgaMeta.GetSummary())) != "" {
			s.markSummary(seriesID)
		}
		s.removeManual(seriesID, seriesName)
	}

	// ---- 卷级数据 + 封面 ----
	books, err := s.Komga.ListBooksInSeries(seriesID)
	if err != nil {
		res.Error = "获取系列书籍失败: " + err.Error()
		return res
	}
	if s.FetchVolumes && len(bw.Volumes) > 0 {
		if s.DryRun {
			s.Log.Info(fmt.Sprintf("[Dry-run] 将用 BookWalker 补齐 %d 卷信息（发售日/ISBN/简介）", len(books)))
		} else {
			s.updateBookWalkerVolumes(series, books, bw, seriesName, sink)
		}
	}
	if s.WithCovers {
		if len(bw.CoverURLs) > 0 {
			s.updateSeriesCover(seriesID, seriesName, bw.CoverURLs, sink)
		} else if s.DryRun {
			s.Log.Info("[Dry-run]《" + seriesName + "》BookWalker 无封面，真实模式将清空旧封面")
		} else {
			s.clearSeriesCover(seriesID, seriesName)
		}
	}
	s.ensureMachineTag(seriesID, seriesName, komgaMeta, meta)

	res.Success = true
	res.MatchTitle = bw.Title
	res.Changes = sink.items
	return res
}

// updateBookWalkerVolumes BW 每卷数据 → Komga 书元数据（只补发售日/ISBN/简介/封面）。
func (s *Service) updateBookWalkerVolumes(series *komga.SeriesDto, books []komga.BookDto, bw *sources.BwSeries, seriesName string, sink *changeSink) {
	if len(books) == 0 {
		return
	}
	if !volumesCompatible(len(bw.Volumes), len(books)) {
		s.Log.Info(fmt.Sprintf("《%s》BookWalker 卷数（%d）与本地册数（%d）差距过大，跳过每卷补齐", seriesName, len(bw.Volumes), len(books)))
		return
	}
	byOrder := map[int]sources.BwVolume{}
	for _, v := range bw.Volumes {
		if v.Order > 0 {
			if _, ok := byOrder[v.Order]; !ok {
				byOrder[v.Order] = v
			}
		}
	}
	updated := 0
	for i := range books {
		book := books[i]
		volNum := text.ExtractBookVolumeNumber(book.Name, bookMetaTitle(book), bookMetaNumber(book))
		var v *sources.BwVolume
		if volNum > 0 {
			if vv, ok := byOrder[volNum]; ok {
				v = &vv
			}
		}
		if v == nil && len(books) == len(bw.Volumes) && i < len(bw.Volumes) {
			v = &bw.Volumes[i]
		}
		if v == nil || v.IsEmpty() {
			continue
		}
		volDisplay := pad2(orInt(volNum, v.Order))
		bookMeta := &komga.BookMetadataDto{}
		if strings.TrimSpace(v.Summary) != "" {
			bookMeta.Summary = s.translateIfNeeded(sources.StripHtml(v.Summary), seriesName+" 卷 "+volDisplay)
		}
		if v.ReleaseDate != "" {
			bookMeta.ReleaseDate = v.ReleaseDate
		}
		if v.Isbn != "" {
			bookMeta.Isbn = v.Isbn
		}
		if komga.HasChanges(bookMeta) {
			diff := diffBookMeta(book.Metadata, bookMeta)
			if s.DryRun {
				sink.observe(Change{Kind: ChangeBookMeta, SeriesID: book.SeriesID, BookID: book.ID,
					Target: "《" + seriesName + "》卷 " + volDisplay, Desc: "将更新卷信息（BookWalker）", FieldDiff: diff})
			} else {
				if err := s.Komga.PatchBookMetadata(book.ID, bookMeta); err != nil {
					s.Log.Error("处理《" + seriesName + "》卷 " + volDisplay + " (book " + book.ID + ") 出错: " + err.Error())
				} else {
					updated++
					sink.observe(Change{Kind: ChangeBookMeta, SeriesID: book.SeriesID, BookID: book.ID,
						Target: "《" + seriesName + "》卷 " + volDisplay, Desc: "已更新卷信息（BookWalker）", FieldDiff: diff})
				}
			}
		}
		if s.WithCovers && v.CoverName != "" {
			s.updateBookCover(&book, seriesName, volDisplay, sources.BuildCoverURLs(v.CoverName), sink)
		}
	}
	if updated > 0 {
		s.Log.Info(fmt.Sprintf("《%s》BookWalker 补齐 %d 卷信息（发售日/ISBN/简介）", seriesName, updated))
	}
}

// buildBwTags BW 题材标签 + 出版品牌 + 漫画/小说 类型标记（跳过已存在的）。
func buildBwTags(bw *sources.BwSeries, existing []string) []string {
	var tags []string
	add := func(raw string) {
		v := strings.TrimSpace(raw)
		if v == "" || len([]rune(v)) > 40 {
			return
		}
		if containsFoldStr(existing, v) || containsFoldStr(tags, v) {
			return
		}
		tags = append(tags, v)
	}
	for _, t := range bw.Themes {
		add(t)
	}
	if bw.Imprint != "" && !strings.EqualFold(bw.Imprint, bw.Publisher) {
		add(bw.Imprint)
	}
	if bw.IsComic() {
		add("漫画")
	} else if bw.IsNovel() {
		add("小说")
	}
	return tags
}

// scrapeFromMox 用 Mox（bookof.moe 中文漫画库）刮削系列。
func (s *Service) scrapeFromMox(series *komga.SeriesDto, mox *sources.MoxSeries, replace bool) *SeriesResult {
	seriesID := series.ID
	seriesName := series.Name
	if series.Metadata != nil && series.Metadata.Title != "" {
		seriesName = series.Metadata.Title
	}
	res := &SeriesResult{}
	sink := &changeSink{}
	komgaMeta := series.Metadata
	if replace && komgaMeta != nil {
		komgaMeta = ClearedForReplace(komgaMeta)
	}
	s.Log.Info(fmt.Sprintf("刮削 《%s》 ← Mox(bookof.moe) 《%s》%s", seriesName, mox.Title, dryTag(s.DryRun)))

	meta := &komga.SeriesMetadataDto{}
	hasAny := false

	if (komgaMeta.GetTitle() == "" || text.HasCopySuffix(komgaMeta.GetTitle())) && mox.Title != "" {
		t := text.StripCopySuffix(mox.Title)
		meta.Title, meta.TitleSort = t, t
		hasAny = true
	}
	if komgaMeta.GetSummary() == "" && mox.Summary != "" {
		meta.Summary = mox.Summary
		hasAny = true
	}
	if strings.TrimSpace(meta.Summary) == "" && komgaMeta.GetSummary() == "" {
		author := ""
		if len(mox.Authors) > 0 {
			author = mox.Authors[0]
		}
		meta.Summary = s.fillAiSynopsis(seriesName, mox.OriTitle, author, komgaMeta, s.DryRun)
		if meta.Summary != "" {
			hasAny = true
		}
	}
	if komgaMeta.GetPublisher() == "" && mox.Publisher != "" {
		meta.Publisher = mox.Publisher
		hasAny = true
	}
	if mox.IsAdult && komgaMeta.GetAgeRating() <= 0 {
		v := 18
		meta.AgeRating = &v
		hasAny = true
	}
	if komgaMeta.GetStatus() == "" && !komgaMeta.GetStatusLock() && mox.Status != "" {
		meta.Status = mox.Status
		hasAny = true
	}
	if len(komgaMeta.Authors) == 0 && len(mox.Authors) > 0 {
		for _, a := range mox.Authors {
			meta.Authors = append(meta.Authors, komga.AuthorDto{Name: a, Role: "writer"})
		}
		hasAny = true
	}
	if !komgaMeta.GetAlternateTitlesLock() {
		merged := append([]komga.AlternateTitleDto{}, komgaMeta.AlternateTitles...)
		seen := map[string]bool{}
		for _, t := range merged {
			seen[strings.ToLower(t.Title)] = true
		}
		addAlt := func(label, value string) {
			v := strings.TrimSpace(value)
			if v == "" || len([]rune(v)) > 80 || strings.EqualFold(v, mox.Title) {
				return
			}
			if label == "别名" && strings.EqualFold(v, mox.OriTitle) {
				return
			}
			if seen[strings.ToLower(v)] {
				return
			}
			seen[strings.ToLower(v)] = true
			merged = append(merged, komga.AlternateTitleDto{Label: label, Title: text.Capitalize(v)})
		}
		addAlt("原名", mox.OriTitle)
		addAlt("别名", mox.EngName)
		if len(merged) > len(komgaMeta.AlternateTitles) {
			meta.AlternateTitles = merged
			hasAny = true
		}
	}
	if s.BwWriteTags && len(mox.Tags) > 0 && !komgaMeta.GetTagsLock() {
		tags := append([]string{}, komgaMeta.Tags...)
		added := 0
		for i, t := range mox.Tags {
			if i >= 12 {
				break
			}
			if !containsFoldStr(tags, t) {
				tags = append(tags, t)
				added++
			}
		}
		if added > 0 {
			meta.Tags = dedup(tags)
			hasAny = true
		}
	}

	s.applyScrapeMarksV2(meta, series, komgaMeta)

	// ---- 链接：Bof（详情页）+ Mox（kox.moe 阅读页）----
	links := append([]komga.LinkDto{}, komgaMeta.Links...)
	linkAdded := false
	hasBoF, hasKox := false, false
	for _, l := range links {
		if strings.Contains(strings.ToLower(l.URL), "bookof.moe") {
			hasBoF = true
		}
		if strings.Contains(strings.ToLower(l.URL), "kox.moe") {
			hasKox = true
		}
	}
	if !hasBoF {
		links = append(links, komga.LinkDto{Label: "Bof", URL: mox.BoFURL()})
		linkAdded = true
	}
	if !hasKox {
		links = append(links, komga.LinkDto{Label: "Mox", URL: mox.MoxURL()})
		linkAdded = true
	}
	if linkAdded {
		meta.Links = links
	}

	// ---- 封面：帧页 → 每卷 URL（第 1 张同时作系列封面）----
	var coverURLs []string
	if s.WithCovers && mox.CoverFrameURL != "" {
		if urls, err := s.Mox.GetVolumeCoverURLs(context.Background(), mox.CoverFrameURL); err == nil {
			coverURLs = urls
		} else {
			s.Log.Warn("《" + seriesName + "》Mox 封面列表获取失败: " + err.Error())
		}
		if len(coverURLs) == 0 {
			s.Log.Warn("《" + seriesName + "》Mox 封面帧页没有解析出封面")
		}
	}

	if !hasAny && !linkAdded {
		s.Log.Info("《" + seriesName + "》Mox 系列字段均已存在，无需更新")
	} else if s.DryRun {
		s.Log.Info("[Dry-run] PATCH /series/" + seriesID + "/metadata（Mox）：" + summarizeMeta(meta))
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: "将更新系列元数据（Mox）", Detail: summarizeMeta(meta), FieldDiff: diffSeriesMeta(komgaMeta, meta)})
		if len(coverURLs) > 0 {
			sink.observe(Change{Kind: ChangeSeriesCover, SeriesID: seriesID, Target: "《" + seriesName + "》",
				Desc: fmt.Sprintf("将上传系列封面 + 最多 %d 张卷封面（Mox）", len(coverURLs))})
		}
	} else {
		if err := s.Komga.PatchSeriesMetadata(seriesID, meta); err != nil {
			res.Error = "Mox 系列元数据 PATCH 失败: " + err.Error()
			return res
		}
		s.Log.Info("《" + seriesName + "》系列信息已更新（Mox）")
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: "已更新系列元数据（Mox）", Detail: summarizeMeta(meta), FieldDiff: diffSeriesMeta(komgaMeta, meta)})
	}
	if !s.DryRun {
		s.clearStaleSeries(seriesID, meta, komgaMeta, seriesName)
		if strings.TrimSpace(orStr(meta.Summary, komgaMeta.GetSummary())) != "" {
			s.markSummary(seriesID)
		}
		s.removeManual(seriesID, seriesName)
	}

	// ---- 封面上传（严格串行：bookof.moe 不能并发）----
	if s.WithCovers && !s.DryRun {
		if len(coverURLs) > 0 {
			if !series.Oneshot {
				s.updateSeriesCover(seriesID, seriesName, coverURLs[:1], sink)
			}
			books, err := s.Komga.ListBooksInSeries(seriesID)
			if err == nil {
				if series.Oneshot {
					if len(books) > 0 {
						s.updateBookCover(&books[0], seriesName, "", coverURLs, sink)
					}
				} else {
					s.updateMoxVolumeCovers(books, seriesName, coverURLs, sink)
				}
			}
		} else {
			s.clearSeriesCover(seriesID, seriesName)
		}
	}
	s.ensureMachineTag(seriesID, seriesName, komgaMeta, meta)

	res.Success = true
	res.MatchTitle = mox.Title
	res.Changes = sink.items
	return res
}

// EH 标签命名空间写入优先级（越靠前信息量越高；最多写 12 条）。
var ehTagNSOrder = []string{"artist", "group", "parody", "character", "female", "male", "mixed", "other", "language", "category"}

// splitEHRawTag EH 原始标签 "ns:tag" → (ns, tag)，ns 统一小写（无命名空间返回空）。
func splitEHRawTag(raw string) (string, string) {
	ns, tag, ok := strings.Cut(strings.TrimSpace(raw), ":")
	if !ok {
		return "", ""
	}
	return strings.ToLower(strings.TrimSpace(ns)), strings.TrimSpace(tag)
}

// EhTagLimit E-Hentai 标签写入上限（预览与写库共用，改这一处即可）。
const EhTagLimit = 12

// ehTagNames 按命名空间优先级把 "ns:tag" 转成 "中文命名空间:中文标签"（翻译未命中回落原始 tag 名）。
func ehTagNames(rawTags []string, limit int) []string {
	byNS := map[string][]string{}
	for _, raw := range rawTags {
		ns, tag := splitEHRawTag(raw)
		if ns == "" || tag == "" {
			continue
		}
		byNS[ns] = append(byNS[ns], tag)
	}
	out := make([]string, 0, limit)
	for _, ns := range ehTagNSOrder {
		for _, tag := range byNS[ns] {
			if len(out) >= limit {
				return out
			}
			name, ok := ehtags.Translate(ns, tag)
			if !ok || strings.TrimSpace(name) == "" {
				name = tag
			}
			out = append(out, ehtags.Label(ns)+":"+name)
		}
	}
	return out
}

// EhDisplayTags 供 API 预览复用：与写库同源的「翻译 + 命名空间优先级 + 上限」标签列表。
func EhDisplayTags(rawTags []string, limit int) []string { return ehTagNames(rawTags, limit) }

// ehArtistAuthors artist/group 标签 → book 级作者（role=Artist；画师在前、社团在后，最多 6 个）。
func ehArtistAuthors(rawTags []string) []komga.AuthorDto {
	var out []komga.AuthorDto
	seen := map[string]bool{}
	for _, ns := range []string{"artist", "group"} {
		for _, raw := range rawTags {
			tns, tag := splitEHRawTag(raw)
			if tns != ns || tag == "" {
				continue
			}
			name, ok := ehtags.Translate(ns, tag)
			if !ok || strings.TrimSpace(name) == "" {
				name = tag
			}
			key := strings.ToLower(name)
			if seen[key] || len(out) >= 6 {
				continue
			}
			seen[key] = true
			out = append(out, komga.AuthorDto{Name: name, Role: "Artist"})
		}
	}
	return out
}

// ehLanguageCode language:xxx → ISO 码（只认四种常见语言）。
func ehLanguageCode(rawTags []string) string {
	for _, raw := range rawTags {
		ns, tag := splitEHRawTag(raw)
		if ns != "language" {
			continue
		}
		switch strings.ToLower(tag) {
		case "chinese":
			return "zh"
		case "japanese":
			return "ja"
		case "english":
			return "en"
		case "korean":
			return "ko"
		}
	}
	return ""
}

// ehPostedDate posted（unix 秒字符串）→ YYYY-MM-DD。
func ehPostedDate(posted string) string {
	sec, err := strconv.ParseInt(strings.TrimSpace(posted), 10, 64)
	if err != nil || sec <= 0 {
		return ""
	}
	return time.Unix(sec, 0).UTC().Format("2006-01-02")
}

// scrapeFromEH 用 E-Hentai 表站画廊数据刮削系列（一个画廊 = 一册；只补空缺，不覆盖用户已有数据）。
// 与 scrapeFromMox 同构：读 komgaMeta → 组装 meta → Dry-run 出 diff / 真实模式 PATCH → sink.observe。
func (s *Service) scrapeFromEH(series *komga.SeriesDto, g *sources.EhGallery, replace bool) *SeriesResult {
	if s.EH == nil {
		return &SeriesResult{Error: "E-Hentai 源未启用"}
	}
	seriesID := series.ID
	seriesName := series.Name
	if series.Metadata != nil && series.Metadata.Title != "" {
		seriesName = series.Metadata.Title
	}
	res := &SeriesResult{}
	sink := &changeSink{}
	komgaMeta := series.Metadata
	if replace && komgaMeta != nil {
		komgaMeta = ClearedForReplace(komgaMeta)
	}
	s.Log.Info(fmt.Sprintf("刮削 《%s》 ← E-Hentai 《%s》%s", seriesName, g.Title, dryTag(s.DryRun)))

	meta := &komga.SeriesMetadataDto{}
	hasAny := false
	linkAdded := false

	// ---- 标题：当前为空或纠正匹配（replace 已由 ClearedForReplace 清空旧值）才写 ----
	if (komgaMeta.GetTitle() == "" || replace) && strings.TrimSpace(g.Title) != "" {
		if t := text.StripCopySuffix(strings.TrimSpace(g.Title)); t != "" {
			meta.Title, meta.TitleSort = t, t
			hasAny = true
		}
	}
	// ---- 日文标题 → alternateTitles（原名；去重、空值跳过）----
	if jpn := strings.TrimSpace(g.TitleJpn); jpn != "" && !komgaMeta.GetAlternateTitlesLock() {
		merged := append([]komga.AlternateTitleDto{}, komgaMeta.AlternateTitles...)
		seen := map[string]bool{}
		for _, t := range merged {
			seen[strings.ToLower(t.Title)] = true
		}
		if len([]rune(jpn)) <= 80 && !seen[strings.ToLower(jpn)] && !strings.EqualFold(jpn, g.Title) {
			merged = append(merged, komga.AlternateTitleDto{Label: "原名", Title: text.Capitalize(jpn)})
			meta.AlternateTitles = merged
			hasAny = true
		}
	}
	// ---- 发售日：posted（unix 秒）→ YYYY-MM-DD，仅旧值为空且未锁时写 ----
	if komgaMeta.GetReleaseDate() == "" && !komgaMeta.GetReleaseDateLock() {
		if d := ehPostedDate(g.Posted); d != "" {
			meta.ReleaseDate = d
			hasAny = true
		}
	}
	// ---- 语言：language:chinese/japanese/english/korean → zh/ja/en/ko，仅旧值为空且未锁时写 ----
	curLang := ""
	if komgaMeta != nil {
		curLang = komgaMeta.Language
	}
	langLocked := komgaMeta != nil && komgaMeta.LanguageLock != nil && *komgaMeta.LanguageLock
	if curLang == "" && !langLocked {
		if lang := ehLanguageCode(g.Tags); lang != "" {
			meta.Language = lang
			hasAny = true
		}
	}
	// ---- 标签：命名空间优先级 + 中文翻译，最多 12 条（不含 MACHINE、不含库中已有）；tagsLock 时不写 ----
	if !komgaMeta.GetTagsLock() {
		tags := append([]string{}, komgaMeta.Tags...)
		added := 0
		for _, t := range ehTagNames(g.Tags, EhTagLimit) {
			if added >= EhTagLimit {
				break
			}
			if !containsFoldStr(tags, t) {
				tags = append(tags, t)
				added++
			}
		}
		if added > 0 {
			tags = dedup(tags)
			if !containsStr(tags, text.MachineTag) {
				tags = append(tags, text.MachineTag)
			}
			meta.Tags = tags
			hasAny = true
		}
	}

	s.applyScrapeMarksV2(meta, series, komgaMeta)

	// ---- 链接：EH 画廊页（host 已存在则不重复加）----
	links := append([]komga.LinkDto{}, komgaMeta.Links...)
	hasEH := false
	for _, l := range links {
		low := strings.ToLower(l.URL)
		if strings.Contains(low, "e-hentai.org") || strings.Contains(low, "exhentai.org") {
			hasEH = true
			break
		}
	}
	if g.URL != "" && !hasEH {
		links = append(links, komga.LinkDto{Label: "EH", URL: g.URL})
		meta.Links = links
		linkAdded = true
	}

	if !hasAny && !linkAdded {
		s.Log.Info("《" + seriesName + "》E-Hentai 系列字段均已存在，无需更新")
	} else if s.DryRun {
		s.Log.Info("[Dry-run] PATCH /series/" + seriesID + "/metadata（E-Hentai）：" + summarizeMeta(meta))
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: "将更新系列元数据（E-Hentai）", Detail: summarizeMeta(meta), FieldDiff: diffSeriesMeta(komgaMeta, meta)})
	} else {
		if err := s.Komga.PatchSeriesMetadata(seriesID, meta); err != nil {
			res.Error = "E-Hentai 系列元数据 PATCH 失败: " + err.Error()
			s.Log.Error("《" + seriesName + "》E-Hentai 系列元数据 PATCH 失败: " + err.Error())
			return res
		}
		s.Log.Info("《" + seriesName + "》系列信息已更新（E-Hentai）")
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: "已更新系列元数据（E-Hentai）", Detail: summarizeMeta(meta), FieldDiff: diffSeriesMeta(komgaMeta, meta)})
	}
	// EH 无简介字段 → 刻意不调 clearStaleSeries（否则会把旧简介当脏数据清掉）
	if !s.DryRun {
		s.removeManual(seriesID, seriesName)
	}

	// ---- 画师 / 社团 → book 级 Authors（series 级无 authors 字段；一册一个画廊，取该系列第一册）----
	if authors := ehArtistAuthors(g.Tags); len(authors) > 0 {
		books, err := s.Komga.ListBooksInSeries(seriesID)
		if err != nil {
			s.Log.Warn("《" + seriesName + "》E-Hentai 画师/社团写入失败（获取系列书籍失败）: " + err.Error())
		} else if len(books) > 0 {
			book := books[0]
			volDisplay := pad2(orInt(text.ExtractBookVolumeNumber(book.Name, bookMetaTitle(book), bookMetaNumber(book)), 1))
			hasAuthors := book.Metadata != nil && len(book.Metadata.Authors) > 0
			authorsLocked := book.Metadata != nil && book.Metadata.AuthorsLock != nil && *book.Metadata.AuthorsLock
			if !hasAuthors && !authorsLocked {
				bookMeta := &komga.BookMetadataDto{Authors: authors}
				diff := diffBookMeta(book.Metadata, bookMeta)
				if s.DryRun {
					sink.observe(Change{Kind: ChangeBookMeta, SeriesID: seriesID, BookID: book.ID,
						Target: "《" + seriesName + "》卷 " + volDisplay, Desc: "将更新卷作者（E-Hentai）", FieldDiff: diff})
				} else if err := s.Komga.PatchBookMetadata(book.ID, bookMeta); err != nil {
					s.Log.Error("《" + seriesName + "》E-Hentai 卷作者写入失败: " + err.Error())
				} else {
					s.Log.Info("《" + seriesName + "》卷作者已更新（E-Hentai）")
					sink.observe(Change{Kind: ChangeBookMeta, SeriesID: seriesID, BookID: book.ID,
						Target: "《" + seriesName + "》卷 " + volDisplay, Desc: "已更新卷作者（E-Hentai）", FieldDiff: diff})
				}
			}
		}
	}

	// ---- 封面：画廊缩略图（ehgt.org；Referer 走 downloadImage 的 EH 规则，413 由 updateSeriesCover 降级）----
	if s.WithCovers {
		cover := s.EH.CoverURL(g)
		if s.DryRun {
			if cover != "" {
				s.Log.Info("[Dry-run] 将上传系列封面（E-Hentai）")
			} else {
				s.Log.Info("[Dry-run]《" + seriesName + "》E-Hentai 无封面，真实模式将清空旧封面")
			}
		} else if cover != "" {
			s.updateSeriesCover(seriesID, seriesName, []string{cover}, sink)
		} else {
			s.clearSeriesCover(seriesID, seriesName)
		}
	}
	s.ensureMachineTag(seriesID, seriesName, komgaMeta, meta)

	res.Success = true
	res.MatchTitle = g.Title
	res.Changes = sink.items
	return res
}

// updateMoxVolumeCovers Mox 每卷封面按顺序对齐本地书（防串卷：差距过大只保留系列封面）。
func (s *Service) updateMoxVolumeCovers(books []komga.BookDto, seriesName string, coverURLs []string, sink *changeSink) {
	if len(books) == 0 {
		return
	}
	if !volumesCompatible(len(coverURLs), len(books)) {
		s.Log.Info(fmt.Sprintf("《%s》Mox 封面数（%d）与本地册数（%d）差距过大，跳过卷封面", seriesName, len(coverURLs), len(books)))
		return
	}
	for i := range books {
		if i >= len(coverURLs) {
			break
		}
		book := books[i]
		volNum := text.ExtractBookVolumeNumber(book.Name, bookMetaTitle(book), bookMetaNumber(book))
		volDisplay := pad2(orInt(volNum, i+1))
		s.updateBookCover(&book, seriesName, volDisplay, coverURLs[i:i+1], sink)
	}
}

// scrapeFromMangaDex 用 MangaDex 条目刮削系列。
func (s *Service) scrapeFromMangaDex(series *komga.SeriesDto, md *sources.MangaDexSeries, replace bool) *SeriesResult {
	seriesID := series.ID
	seriesName := series.Name
	if series.Metadata != nil && series.Metadata.Title != "" {
		seriesName = series.Metadata.Title
	}
	res := &SeriesResult{}
	sink := &changeSink{}
	komgaMeta := series.Metadata
	if replace && komgaMeta != nil {
		komgaMeta = ClearedForReplace(komgaMeta)
	}
	s.Log.Info(fmt.Sprintf("刮削 《%s》 ← MangaDex 《%s》%s", seriesName, md.Title, dryTag(s.DryRun)))

	meta := &komga.SeriesMetadataDto{}
	hasAny := false

	if (komgaMeta.GetTitle() == "" || text.HasCopySuffix(komgaMeta.GetTitle())) && md.Title != "" {
		t := text.StripCopySuffix(s.translateTitleIfNeeded(md.Title, seriesName))
		meta.Title, meta.TitleSort = t, t
		hasAny = true
	}
	if komgaMeta.GetSummary() == "" && md.Description != "" {
		meta.Summary = s.translateIfNeeded(md.Description, seriesName)
		hasAny = true
	}
	if !komgaMeta.GetAlternateTitlesLock() && len(md.AltTitles) > 0 {
		merged := append([]komga.AlternateTitleDto{}, komgaMeta.AlternateTitles...)
		seen := map[string]bool{}
		for _, t := range merged {
			seen[strings.ToLower(t.Title)] = true
		}
		added := 0
		for _, alt := range md.AltTitles {
			if len([]rune(alt)) > 80 || seen[strings.ToLower(alt)] {
				continue
			}
			seen[strings.ToLower(alt)] = true
			merged = append(merged, komga.AlternateTitleDto{Label: "别名", Title: text.Capitalize(alt)})
			added++
			if added >= 5 {
				break
			}
		}
		if added > 0 {
			meta.AlternateTitles = merged
			hasAny = true
		}
	}
	if s.BwWriteTags && len(md.Tags) > 0 && !komgaMeta.GetGenresLock() {
		genres := append([]string{}, komgaMeta.Genres...)
		added := 0
		for i, g := range md.Tags {
			if i >= 8 {
				break
			}
			if !containsFoldStr(genres, g) {
				genres = append(genres, g)
				added++
			}
		}
		if added > 0 {
			meta.Genres = genres
			hasAny = true
		}
	}
	// 阅读方向：日文原作 → 从右往左
	if strings.EqualFold(md.OriginalLanguage, "ja") && !komgaMeta.GetReadingDirectionLock() &&
		strings.EqualFold(orStr(komgaMeta.ReadingDirection, "LEFT_TO_RIGHT"), "LEFT_TO_RIGHT") {
		meta.ReadingDirection = "RIGHT_TO_LEFT"
		hasAny = true
	}

	s.applyScrapeMarksV2(meta, series, komgaMeta)

	// ---- 链接：MangaDex（条目页）----
	links := append([]komga.LinkDto{}, komgaMeta.Links...)
	linkAdded := false
	hasMD := false
	for _, l := range links {
		if strings.Contains(strings.ToLower(l.URL), "mangadex.org") {
			hasMD = true
			break
		}
	}
	if md.ID != "" && !hasMD {
		links = append(links, komga.LinkDto{Label: "MangaDex", URL: "https://mangadex.org/title/" + md.ID})
		meta.Links = links
		linkAdded = true
	}

	if !hasAny && !linkAdded {
		s.Log.Info("《" + seriesName + "》MangaDex 字段均已存在，无需更新")
	} else if s.DryRun {
		s.Log.Info("[Dry-run] PATCH /series/" + seriesID + "/metadata（MangaDex）：" + summarizeMeta(meta))
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: "将更新系列元数据（MangaDex）", Detail: summarizeMeta(meta), FieldDiff: diffSeriesMeta(komgaMeta, meta)})
	} else {
		if err := s.Komga.PatchSeriesMetadata(seriesID, meta); err != nil {
			res.Error = "MangaDex 系列元数据 PATCH 失败: " + err.Error()
			return res
		}
		s.Log.Info("《" + seriesName + "》系列信息已更新（MangaDex）")
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: "已更新系列元数据（MangaDex）", Detail: summarizeMeta(meta), FieldDiff: diffSeriesMeta(komgaMeta, meta)})
	}
	if !s.DryRun {
		s.clearStaleSeries(seriesID, meta, komgaMeta, seriesName)
		if strings.TrimSpace(orStr(meta.Summary, komgaMeta.GetSummary())) != "" {
			s.markSummary(seriesID)
		}
		s.removeManual(seriesID, seriesName)
	}

	// ---- 封面：uploads CDN 直出 ----
	if s.WithCovers && md.CoverURL != "" {
		if s.DryRun {
			s.Log.Info("[Dry-run] 将上传系列封面（MangaDex）")
		} else if series.Oneshot {
			books, err := s.Komga.ListBooksInSeries(seriesID)
			if err == nil && len(books) > 0 {
				s.updateBookCover(&books[0], seriesName, "", []string{md.CoverURL}, sink)
			}
		} else {
			s.updateSeriesCover(seriesID, seriesName, []string{md.CoverURL}, sink)
		}
	} else if s.WithCovers && md.CoverURL == "" && !s.DryRun {
		s.clearSeriesCover(seriesID, seriesName)
	}
	s.ensureMachineTag(seriesID, seriesName, komgaMeta, meta)

	res.Success = true
	res.MatchTitle = md.Title
	res.Changes = sink.items
	return res
}

// scrapeFromBaike 用百度百科词条卡片刮削系列（最终兜底源）。
func (s *Service) scrapeFromBaike(series *komga.SeriesDto, card *sources.BaikeCard, replace bool) *SeriesResult {
	seriesID := series.ID
	seriesName := series.Name
	if series.Metadata != nil && series.Metadata.Title != "" {
		seriesName = series.Metadata.Title
	}
	res := &SeriesResult{}
	sink := &changeSink{}
	komgaMeta := series.Metadata
	if replace && komgaMeta != nil {
		komgaMeta = ClearedForReplace(komgaMeta)
	}
	s.Log.Info(fmt.Sprintf("刮削 《%s》 ← 百度百科 《%s》%s", seriesName, card.Title, dryTag(s.DryRun)))

	meta := &komga.SeriesMetadataDto{}
	hasAny := false

	if (komgaMeta.GetTitle() == "" || text.HasCopySuffix(komgaMeta.GetTitle())) && card.Title != "" {
		t := text.StripCopySuffix(card.Title)
		meta.Title, meta.TitleSort = t, t
		hasAny = true
	}
	if komgaMeta.GetSummary() == "" && card.Abstract != "" {
		meta.Summary = card.Abstract
		hasAny = true
	}
	press := card.Field("出版社")
	if press == "" {
		press = card.Field("其他出版社")
	}
	if komgaMeta.GetPublisher() == "" && press != "" {
		meta.Publisher = press
		hasAny = true
	}
	if len(komgaMeta.Authors) == 0 {
		var authors []komga.AuthorDto
		if w := card.Field("作者"); w != "" {
			authors = append(authors, komga.AuthorDto{Name: w, Role: "writer"})
		}
		if a := card.Field("作画"); a != "" {
			dup := false
			for _, x := range authors {
				if x.Name == a {
					dup = true
				}
			}
			if !dup {
				authors = append(authors, komga.AuthorDto{Name: a, Role: "penciller"})
			}
		}
		if len(authors) > 0 {
			meta.Authors = authors
			hasAny = true
		}
	}
	if !komgaMeta.GetAlternateTitlesLock() {
		ori := card.Field("原版名称")
		if ori != "" && len([]rune(ori)) <= 80 && !strings.EqualFold(ori, card.Title) {
			merged := append([]komga.AlternateTitleDto{}, komgaMeta.AlternateTitles...)
			dup := false
			for _, t := range merged {
				if strings.EqualFold(t.Title, ori) {
					dup = true
				}
			}
			if !dup {
				merged = append(merged, komga.AlternateTitleDto{Label: "原名", Title: text.Capitalize(ori)})
				meta.AlternateTitles = merged
				hasAny = true
			}
		}
	}
	if s.BwWriteTags && !komgaMeta.GetGenresLock() {
		typeRaw := card.Field("类型")
		if typeRaw != "" {
			genres := append([]string{}, komgaMeta.Genres...)
			added := 0
			for _, g := range strings.FieldsFunc(typeRaw, func(r rune) bool { return r == '，' || r == '、' || r == ' ' || r == ',' }) {
				if g != "" && !containsFoldStr(genres, g) {
					genres = append(genres, g)
					added++
				}
			}
			if added > 0 {
				meta.Genres = genres
				hasAny = true
			}
		}
	}
	// 阅读方向：日本漫画 → 从右往左（描述/摘要提到「漫画」才认定，避免误伤非漫画词条）
	if (strings.Contains(card.Desc, "漫画") || strings.Contains(card.Abstract, "漫画")) &&
		!komgaMeta.GetReadingDirectionLock() &&
		strings.EqualFold(orStr(komgaMeta.ReadingDirection, "LEFT_TO_RIGHT"), "LEFT_TO_RIGHT") {
		meta.ReadingDirection = "RIGHT_TO_LEFT"
		hasAny = true
	}

	s.applyScrapeMarksV2(meta, series, komgaMeta)

	// ---- 链接：Baike（词条页）----
	links := append([]komga.LinkDto{}, komgaMeta.Links...)
	linkAdded := false
	hasBk := false
	for _, l := range links {
		if strings.Contains(strings.ToLower(l.URL), "baike.baidu.com") {
			hasBk = true
			break
		}
	}
	if card.EntryURL != "" && !hasBk {
		links = append(links, komga.LinkDto{Label: "Baike", URL: card.EntryURL})
		meta.Links = links
		linkAdded = true
	}

	if !hasAny && !linkAdded {
		s.Log.Info("《" + seriesName + "》百度百科字段均已存在，无需更新")
	} else if s.DryRun {
		s.Log.Info("[Dry-run] PATCH /series/" + seriesID + "/metadata（百度百科）：" + summarizeMeta(meta))
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: "将更新系列元数据（百度百科）", Detail: summarizeMeta(meta), FieldDiff: diffSeriesMeta(komgaMeta, meta)})
	} else {
		if err := s.Komga.PatchSeriesMetadata(seriesID, meta); err != nil {
			res.Error = "百度百科系列元数据 PATCH 失败: " + err.Error()
			return res
		}
		s.Log.Info("《" + seriesName + "》系列信息已更新（百度百科）")
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: "已更新系列元数据（百度百科）", Detail: summarizeMeta(meta), FieldDiff: diffSeriesMeta(komgaMeta, meta)})
	}
	if !s.DryRun {
		s.clearStaleSeries(seriesID, meta, komgaMeta, seriesName)
		if strings.TrimSpace(orStr(meta.Summary, komgaMeta.GetSummary())) != "" {
			s.markSummary(seriesID)
		}
		s.removeManual(seriesID, seriesName)
	}

	// ---- 封面：词条概述图（该源只在其它源全落空时用）----
	if s.WithCovers && card.ImageURL != "" {
		jpg := sources.JpegCoverURL(card.ImageURL)
		if s.DryRun {
			s.Log.Info("[Dry-run] 将上传系列封面（百度百科概述图）")
		} else if series.Oneshot {
			books, err := s.Komga.ListBooksInSeries(seriesID)
			if err == nil && len(books) > 0 {
				s.updateBookCover(&books[0], seriesName, "", []string{jpg}, sink)
			}
		} else {
			s.updateSeriesCover(seriesID, seriesName, []string{jpg}, sink)
		}
	} else if s.WithCovers && card.ImageURL == "" && !s.DryRun {
		s.clearSeriesCover(seriesID, seriesName)
	}
	s.ensureMachineTag(seriesID, seriesName, komgaMeta, meta)

	res.Success = true
	res.MatchTitle = card.Title
	res.Changes = sink.items
	return res
}

// markSummary 简介有值 → 记入状态库。
func (s *Service) markSummary(seriesID string) {
	if s.State != nil {
		s.State.SetSummary(seriesID, true)
	}
}

// markSummaryIf 最终简介有值才记入状态库。
func (s *Service) markSummaryIf(seriesID, finalSummary string) {
	if strings.TrimSpace(finalSummary) != "" {
		s.markSummary(seriesID)
	}
}

// splitAliases 把 " / " 连接的别名字符串拆开。
func splitAliases(aliasStr string) []string {
	var out []string
	for _, a := range strings.Split(aliasStr, " / ") {
		a = strings.TrimSpace(a)
		if a != "" {
			out = append(out, a)
		}
	}
	return out
}

// removeManual 匹配成功后从手动匹配收藏夹移除（真实模式）。
func (s *Service) removeManual(seriesID, seriesName string) {
	if s.DryRun {
		return
	}
	s.Manual.EnsureExists("")
	s.Manual.RemoveSeries(seriesID, seriesName)
}

// ensureMachineTag 打标兜底：本次没走字段 PATCH 但动过库时补 MACHINE 标签（幂等）。
func (s *Service) ensureMachineTag(seriesID, seriesName string, komgaMeta, meta *komga.SeriesMetadataDto) {
	if s.DryRun {
		return
	}
	tags := meta.Tags
	if tags == nil {
		if komgaMeta != nil {
			tags = komgaMeta.Tags
		}
	}
	if containsStr(tags, text.MachineTag) {
		return
	}
	newTags := append(append([]string{}, tags...), text.MachineTag)
	if err := s.Komga.PatchSeriesMetadata(seriesID, &komga.SeriesMetadataDto{Tags: newTags}); err != nil {
		s.Log.Error("《" + seriesName + "》补打 MACHINE 标签失败: " + err.Error())
	} else {
		s.Log.Info("《" + seriesName + "》本次写入封面/卷数据，已补打 MACHINE 标签")
	}
}

// applyScrapeMarksV2 程序打标（v2：黑白名单生效）。
func (s *Service) applyScrapeMarksV2(meta *komga.SeriesMetadataDto, series *komga.SeriesDto, komgaMeta *komga.SeriesMetadataDto) {
	folder := text.FolderName(series.URL, series.Oneshot, series.Name)
	markers := text.Detect(folder, s.Whitelist, s.Blacklist)
	blacklisted := text.DetectBlacklisted(folder, s.Whitelist, s.Blacklist)

	var tags []string
	if meta.Tags != nil {
		tags = append(tags, meta.Tags...)
	} else if komgaMeta != nil {
		tags = append(tags, komgaMeta.Tags...)
	}
	before := len(tags)
	tagsLocked := komgaMeta != nil && komgaMeta.TagsLock != nil && *komgaMeta.TagsLock
	if len(s.Blacklist) > 0 {
		var kept []string
		removed := 0
		for _, t := range tags {
			if t != text.MachineTag && text.IsBlacklisted(t, s.Blacklist) {
				removed++
				continue
			}
			kept = append(kept, t)
		}
		tags = kept
		if removed > 0 {
			s.Log.Info(fmt.Sprintf("《%s》丢弃黑名单标签 -%d", series.Name, removed))
		}
	}
	if !tagsLocked {
		for _, t := range text.TagForms(markers) {
			if !containsStr(tags, t) {
				tags = append(tags, t)
			}
		}
		if pub := text.PublisherKeyword(folder); pub != "" && !containsStr(tags, pub) {
			tags = append(tags, pub)
		}
	}
	if !containsStr(tags, text.MachineTag) {
		tags = append(tags, text.MachineTag)
	}
	if len(tags) != before {
		meta.Tags = tags
	}

	if meta.Title != "" {
		t := meta.Title
		if len(blacklisted) > 0 {
			t, _ = text.StripSuffixes(t, blacklisted)
		}
		marked, changed := text.ApplySuffixes(t, markers)
		if changed {
			meta.Title = marked
		}
	}
}

// containsFoldStr 大小写不敏感包含判定。
func containsFoldStr(list []string, v string) bool {
	for _, s := range list {
		if strings.EqualFold(s, v) {
			return true
		}
	}
	return false
}

// dedup 保序去重（丢空串）。
func dedup(list []string) []string {
	seen := map[string]bool{}
	out := make([]string, 0, len(list))
	for _, t := range list {
		if t == "" || seen[t] {
			continue
		}
		seen[t] = true
		out = append(out, t)
	}
	return out
}

func orStr(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

func orInt(a, b int) int {
	if a > 0 {
		return a
	}
	return b
}
