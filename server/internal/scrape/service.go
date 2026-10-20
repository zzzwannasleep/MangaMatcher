package scrape

import (
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"regexp"
	"strconv"
	"strings"

	"komgabangumi/internal/bangumi"
	"komgabangumi/internal/komga"
	"komgabangumi/internal/sources"
	"komgabangumi/internal/text"
)

func errOf(msg string) error { return errors.New(msg) }

var volumePrefixRegex = regexp.MustCompile(`(?i)^vol(ume)?s?\s*\d+|^(?:巻|卷|册|第)\s*\d+`)

// changeSink 一次刮削内的改动收集器（每次调用独立，天然并发安全）。
type changeSink struct {
	items []Change
}

func (c *changeSink) observe(ch Change) { c.items = append(c.items, ch) }

// Service 单系列刮削编排（v1 Bangumi 主链：搜索 → 匹配 → 元数据 PATCH → 封面上传 → 失败进收藏夹）。
// v2 扩展：多源回退（BW/Mox/MangaDex/E-Hentai/百科）、AI 翻译/补写/识别、刮削状态、纠正匹配、黑白名单。
type Service struct {
	Komga        *komga.Client
	Bangumi      *bangumi.Client
	Log          *slog.Logger
	Manual       *ManualMatchCollection
	DryRun       bool // Dry-run：只产出 changed 差异，不发任何 PATCH/上传
	FetchVolumes bool // 抓单行本详情（每卷 summary/发售日/ISBN）
	WithCovers   bool // 上传封面（meta+cover = All）

	// ---- v2 扩展 ----
	AI               *sources.AiClient
	BW               *sources.BookWalkerIndex
	Mox              *sources.MoxClient
	EH               *sources.EhClient // E-Hentai 表站（同人志/画集兜底）
	MangaDex         *sources.MangaDexClient
	Baike            *sources.BaikeClient
	State            StateWriter  // 刮削状态（封面/简介）
	TranslateSummary bool         // 非中文简介/标题 → AI 翻译
	AIWriteSummary   bool         // 三源无简介时 AI 补写
	AIIdentify       bool         // AI 识别作品（回退链第 3 环）
	AISmartPick      bool         // 多候选时 AI 智能选择
	BwWriteTags      bool         // BW 类型/题材/阅读方向写入
	Whitelist        []string     // 版本标记白名单
	Blacklist        []string     // 版本标记黑名单
	CoverHTTP        *http.Client // 带代理的封面下载客户端（nil=直连）
	CoverReferer     string       // 封面下载 Referer（BW 用 bookwalker.jp）
}

// SeriesResult 单系列刮削结果。
type SeriesResult struct {
	Success    bool
	Skipped    bool
	MatchTitle string
	SubjectID  int
	Error      string
	Changes    []Change
}

// ScrapeSeries 单系列刮削全流程（v1 Bangumi 主链；v2 增加 AI 翻译/补写/状态/BW 跨源补空）。
func (s *Service) ScrapeSeries(series *komga.SeriesDto, subjectID int) *SeriesResult {
	return s.ScrapeSeriesOpts(series, subjectID, false)
}

// ScrapeSeriesOpts 带 ReplaceMatch 语义的刮削（纠正/覆盖匹配用）：
// 旧 Btv/Bw/Bof/Mox/MangaDex/Baike 链接剔除、锁字段解锁重写、全部卷强制重抓。
func (s *Service) ScrapeSeriesOpts(series *komga.SeriesDto, subjectID int, replace bool) *SeriesResult {
	seriesID := series.ID
	seriesName := series.Name
	if series.Metadata != nil && series.Metadata.Title != "" {
		seriesName = series.Metadata.Title
	}
	res := &SeriesResult{}
	sink := &changeSink{}

	subject, err := s.Bangumi.GetSubject(subjectID)
	if err != nil {
		res.Error = "获取 Bangumi 条目失败: " + err.Error()
		s.Log.Error(fmt.Sprintf("刮削《%s》失败: %s", seriesName, res.Error))
		return res
	}
	s.Log.Info(fmt.Sprintf("刮削《%s》← Bangumi subject %d《%s》%s",
		seriesName, subjectID, orDefault(subject.NameCN, subject.Name), dryTag(s.DryRun)))

	// ---- 0. 系列元数据（纯内存组装）----
	var komgaMeta *komga.SeriesMetadataDto
	if series.Metadata != nil {
		komgaMeta = series.Metadata
	} else {
		komgaMeta = s.Komga.GetSeriesMetadata(seriesID)
	}
	if replace && komgaMeta != nil {
		// 纠正/覆盖匹配：旧匹配写入的来源字段全部让位（ClearedForReplace 已丢 links/锁/值），
		// 新匹配的链接（meta.Links）在 Merge 时自动顶替，不会被旧链接污染。
		komgaMeta = ClearedForReplace(komgaMeta)
	}
	meta := bangumi.BuildSeriesMeta(subject, series)
	meta = bangumi.MergeWithExisting(meta, komgaMeta)

	// 标题/简介中文化（繁→简统一；启用 AI 翻译时非中文由 AI 处理）
	if text.IsHanOnly(meta.Title) {
		meta.Title = text.StripCopySuffix(text.T2S(meta.Title))
		if meta.Title != "" {
			meta.TitleSort = meta.Title
		}
	} else if meta.Title != "" {
		meta.Title = text.StripCopySuffix(s.translateTitleIfNeeded(meta.Title, seriesName))
		if meta.Title != "" {
			meta.TitleSort = meta.Title
		}
	}
	if text.IsHanOnly(meta.Summary) {
		meta.Summary = text.T2S(meta.Summary)
	} else {
		meta.Summary = s.translateIfNeeded(meta.Summary, seriesName)
	}

	// 合并后仍无简介 → AI 直接补写（Bangumi 条目缺简介/翻译未启用等场景）
	if strings.TrimSpace(meta.Summary) == "" {
		meta.Summary = s.fillAiSynopsis(seriesName, subject.Name,
			bangumi.ParseInfobox(subject.Infobox, "作者"), komgaMeta, s.DryRun)
	}

	// 程序打标：MACHINE 标签 + 文件夹名版本/格式标记（爱藏版/电子版/汉化组…→ 标题后缀）
	s.applyScrapeMarksV2(meta, series, komgaMeta)

	// ---- 0.5 材料收集（零写入）：卷表 → 每卷详情 → 封面候选 ----
	books, err := s.Komga.ListBooksInSeries(seriesID)
	if err != nil {
		res.Error = "获取系列书籍失败: " + err.Error()
		return res
	}
	bookAuthors := meta.Authors
	updateAuthors := len(bookAuthors) > 0 && ifUpdateBook(books, bookAuthors)
	volumeMates, err := s.fetchVolumeMates(subjectID, books, s.FetchVolumes)
	if err != nil {
		s.Log.Warn("获取单行本关联失败: " + err.Error())
		volumeMates = nil
	}
	if replace && s.FetchVolumes {
		// 纠正匹配：旧匹配写过的卷简介/ISBN/发售日可能是错的，全部有卷号的书强制重抓详情
		need := map[int]bool{}
		for _, b := range books {
			if n := text.ExtractBookVolumeNumber(b.Name, bookMetaTitle(b), bookMetaNumber(b)); n > 0 {
				need[n] = true
			}
		}
		s.Log.Info(fmt.Sprintf("纠正匹配：将重抓 %d 卷的详情（简介/ISBN/发售日）", len(need)))
		volumeMates, err = s.fetchVolumeMatesWithNeed(subjectID, books, need)
		if err != nil {
			s.Log.Warn("纠正匹配重抓单行本关联失败: " + err.Error())
			volumeMates = nil
		}
	}

	// ---- 3.5 BookWalker 跨源补空：只补 Bangumi 缺的 ISBN/发售日/单卷封面（语言无关）----
	if s.BW != nil && s.FetchVolumes && !series.Oneshot {
		bwTitles := []string{seriesName, series.Name, subject.Name, subject.NameCN}
		bwTitles = append(bwTitles, splitAliases(bangumi.ExtractAliases(subject.Infobox))...)
		var uniq []string
		seen := map[string]bool{}
		for _, t := range bwTitles {
			t = strings.TrimSpace(t)
			if t != "" && !seen[t] {
				seen[t] = true
				uniq = append(uniq, t)
			}
			if len(uniq) >= 12 {
				break
			}
		}
		if bwHit := s.BW.FindExact(uniq...); bwHit != nil {
			volumeMates, _ = s.mergeBookWalkerVolumeMates(volumeMates, bwHit, seriesName, len(books))
			// Bangumi 判完结但缺总卷数 → 用 BookWalker 目录的卷数补齐（只补完结系列）
			if meta.Status == "ENDED" && meta.GetTotalBookCount() <= 0 && bwHit.VolumeCount > 0 && !s.DryRun {
				meta.TotalBookCount = &bwHit.VolumeCount
				s.Log.Info(fmt.Sprintf("《%s》总卷数补齐（BookWalker）：%d", seriesName, bwHit.VolumeCount))
			}
		}
	}

	coverURLs := bangumi.GetSeriesCoverURLs(subject)

	// ---- 1. 系列元数据 ----
	seriesDiff := diffSeriesMeta(komgaMeta, meta)
	if s.DryRun {
		s.Log.Info("[Dry-run] PATCH /series/" + seriesID + "/metadata：" + summarizeMeta(meta))
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID,
			Target: "《" + seriesName + "》", Desc: "将更新系列元数据（Bangumi）",
			Detail: summarizeMeta(meta), FieldDiff: seriesDiff})
	} else {
		if err := s.Komga.PatchSeriesMetadata(seriesID, meta); err != nil {
			res.Error = "更新系列元数据失败: " + err.Error()
			s.Log.Error(fmt.Sprintf("《%s》系列元数据 PATCH 失败: %s", seriesName, err.Error()))
			return res
		}
		s.Log.Info("《" + seriesName + "》系列信息已更新")
		sink.observe(Change{Kind: ChangeSeriesMeta, SeriesID: seriesID,
			Target: "《" + seriesName + "》", Desc: "已更新系列元数据（Bangumi）",
			Detail: summarizeMeta(meta), FieldDiff: seriesDiff})
		// 匹配源无简介 → 清空旧简介（脏数据不留）；最终简介有值 → 记入状态库
		s.clearStaleSeries(seriesID, meta, komgaMeta, seriesName)
		s.markSummaryIf(seriesID, orStr(meta.Summary, komgaMeta.GetSummary()))
	}

	// ---- 2. 手动匹配收藏夹移除（匹配成功即出收藏夹）----
	if !s.DryRun {
		s.Manual.EnsureExists("")
		s.Manual.RemoveSeries(seriesID, seriesName)
	}

	// ---- 3. 书籍更新 ----
	s.updateBooks(series, books, seriesName, bookAuthors, volumeMates, updateAuthors, sink)

	// ---- 4. 封面 ----
	if s.WithCovers {
		if len(coverURLs) == 0 {
			if s.DryRun {
				s.Log.Info("[Dry-run]《" + seriesName + "》匹配源无封面，真实模式将清空旧封面")
			} else {
				s.clearSeriesCover(seriesID, seriesName)
			}
		} else {
			s.updateSeriesCover(seriesID, seriesName, coverURLs, sink)
		}
	}

	res.Success = true
	res.SubjectID = subjectID
	res.MatchTitle = orDefault(subject.NameCN, subject.Name)
	res.Changes = sink.items
	return res
}

func dryTag(dry bool) string {
	if dry {
		return " [Dry-run]"
	}
	return ""
}

func orDefault(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

// applyScrapeMarks 程序打标：MACHINE 标签（必打，无视 tags 锁）+ 版本/格式标记（标题后缀 + 标签）。
func (s *Service) applyScrapeMarks(meta *komga.SeriesMetadataDto, series *komga.SeriesDto, komgaMeta *komga.SeriesMetadataDto) {
	folder := text.FolderName(series.URL, series.Oneshot, series.Name)
	markers := text.Detect(folder, nil, nil)

	var tags []string
	if meta.Tags != nil {
		tags = append(tags, meta.Tags...)
	} else if komgaMeta != nil {
		tags = append(tags, komgaMeta.Tags...)
	}
	before := len(tags)
	tagsLocked := komgaMeta != nil && komgaMeta.TagsLock != nil && *komgaMeta.TagsLock
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
		marked, changed := text.ApplySuffixes(meta.Title, markers)
		if changed {
			meta.Title = marked
		}
	}
}

// clearStaleSeries 匹配源无简介时清空旧简介（脏数据不留）。
func (s *Service) clearStaleSeries(seriesID string, meta, komgaMeta *komga.SeriesMetadataDto, seriesName string) {
	if strings.TrimSpace(meta.Summary) == "" && komgaMeta != nil && strings.TrimSpace(komgaMeta.Summary) != "" {
		if err := s.Komga.PatchSeriesMetadataRaw(seriesID, map[string]any{"summary": ""}); err != nil {
			s.Log.Warn("《" + seriesName + "》旧简介清空失败: " + err.Error())
		} else {
			s.Log.Info("《" + seriesName + "》匹配源无简介，旧简介已清空")
		}
	}
}

// clearSeriesCover 匹配源无任何封面时清空旧封面（USER_UPLOADED 全删）。
func (s *Service) clearSeriesCover(seriesID, seriesName string) {
	thumbs, err := s.Komga.GetSeriesThumbnails(seriesID)
	if err != nil {
		s.Log.Warn("《" + seriesName + "》旧封面清理失败: " + err.Error())
		return
	}
	for _, t := range thumbs {
		if t.Type == "USER_UPLOADED" {
			if err := s.Komga.DeleteSeriesThumbnail(seriesID, t.ID); err != nil {
				s.Log.Error("《" + seriesName + "》旧封面删除失败 (ID: " + t.ID + "): " + err.Error())
			}
		}
	}
	s.Log.Info("《" + seriesName + "》匹配源无封面，旧封面已清空（Komga 将用首页自动生成图）")
}

// fetchVolumeMates 单行本抓取（对齐脚本 volumeMatesFetcher + 卷号解析关键坑修复）。
func (s *Service) fetchVolumeMates(subjectID int, books []komga.BookDto, fetchDetail bool) ([]bangumi.VolumeMate, error) {
	return s.fetchVolumeMatesWithNeed(subjectID, books, volumeNumsNeedUpdate(books))
}

// fetchVolumeMatesWithNeed 指定需要重抓详情的卷号集合。
func (s *Service) fetchVolumeMatesWithNeed(subjectID int, books []komga.BookDto, needUpdate map[int]bool) ([]bangumi.VolumeMate, error) {
	related, err := s.Bangumi.GetRelatedSubjects(subjectID)
	if err != nil {
		return nil, err
	}

	// 单行本名 → 卷号。关键坑：Bangumi 很多系列的单行本条目 name_cn 是空字符串（不是 null！），
	// 必须先挑非空名字段再解析；两个名字都解析不出时才按列表序号兜底并告警。
	effName := func(r bangumi.RelatedSubjectDto) string {
		if strings.TrimSpace(r.NameCN) != "" {
			return r.NameCN
		}
		if strings.TrimSpace(r.Name) != "" {
			return r.Name
		}
		return ""
	}
	volNumOf := func(r bangumi.RelatedSubjectDto) (int, bool) {
		if n, ok := text.ExtractVolumeNumber(effName(r)); ok {
			return n, true
		}
		if strings.TrimSpace(r.NameCN) != "" && r.Name != "" {
			if n, ok := text.ExtractVolumeNumber(r.Name); ok {
				return n, true
			}
		}
		return 0, false
	}

	// 过滤 单行本 关联，按卷号升序（稳定，同卷号按 subject id）
	var volumes []bangumi.RelatedSubjectDto
	for _, r := range related {
		if r.Relation == "单行本" {
			volumes = append(volumes, r)
		}
	}
	for i := 1; i < len(volumes); i++ {
		for j := i; j > 0; j-- {
			ni, _ := volNumOf(volumes[j])
			nj, _ := volNumOf(volumes[j-1])
			if ni < nj || (ni == nj && volumes[j].ID < volumes[j-1].ID) {
				volumes[j], volumes[j-1] = volumes[j-1], volumes[j]
			} else {
				break
			}
		}
	}
	if len(volumes) == 0 {
		s.Log.Info("Bangumi 无 单行本 关联条目")
		return nil, nil
	}

	mates := make([]bangumi.VolumeMate, 0, len(volumes))
	for idx, vol := range volumes {
		var coverURLs []string
		if vol.Image != "" {
			coverURLs = append(coverURLs, vol.Image)
		}
		if vol.Images != nil {
			for _, u := range []string{vol.Images.Large, vol.Images.Medium, vol.Images.Common} {
				if u != "" && !containsStr(coverURLs, u) {
					coverURLs = append(coverURLs, u)
				}
			}
		}
		num, ok := volNumOf(vol)
		if !ok {
			num = idx + 1
			s.Log.Warn("Bangumi 单行本《" + effName(vol) + "》解析不出卷号，按列表序号 " + strconv.Itoa(idx+1) + " 处理")
		}
		mate := bangumi.VolumeMate{Num: strconv.Itoa(num), CoverURLs: coverURLs}
		if needUpdate[num] && s.FetchVolumes {
			if detail, err := s.Bangumi.GetSubject(vol.ID); err == nil {
				mate.Summary = bangumi.CleanMultiline(detail.Summary)
				dateStr := bangumi.ParseInfobox(detail.Infobox, "发售日")
				if dateStr == "" {
					dateStr = bangumi.ParseInfobox(detail.Infobox, "放送开始")
				}
				mate.ReleaseDate = text.NormalizeDate(dateStr)
				mate.Isbn = bangumi.ParseInfobox(detail.Infobox, "ISBN")
			} else {
				s.Log.Warn("获取单行本详情失败 (" + strconv.Itoa(vol.ID) + "): " + err.Error())
			}
		}
		mates = append(mates, mate)
	}
	return mates, nil
}

// updateBooks 书籍更新（对齐脚本 updateKomgaBookAll）。
func (s *Service) updateBooks(series *komga.SeriesDto, books []komga.BookDto, seriesName string,
	bookAuthors []komga.AuthorDto, volumeMates []bangumi.VolumeMate, updateAuthors bool, sink *changeSink) {
	if len(books) == 0 {
		return
	}
	if len(books) >= 500 {
		s.Log.Warn(fmt.Sprintf("系列《%s》书籍数 (%d) 达到 500 上限，跳过书籍处理", seriesName, len(books)))
		return
	}
	bookUpdateNeeded := updateAuthors || len(volumeMates) > 0
	if !bookUpdateNeeded {
		return
	}

	for i := range books {
		book := books[i]
		volNum := text.ExtractBookVolumeNumber(book.Name, bookMetaTitle(book), bookMetaNumber(book))
		volDisplay := pad2(volNum)

		var mate *bangumi.VolumeMate
		if series.Oneshot {
			if len(volumeMates) > 0 {
				mate = &volumeMates[0]
			}
		} else {
			mate = findMate(volumeMates, volNum)
		}
		// 兜底：卷号解析不出但两边「卷」数量一致 → 按 numberSort 序对齐
		if mate == nil && volNum <= 0 && !series.Oneshot &&
			len(books) == len(volumeMates) && i < len(volumeMates) {
			mate = &volumeMates[i]
			if n, err := strconv.Atoi(mate.Num); err == nil {
				volDisplay = pad2(n)
			}
		}

		if updateAuthors {
			bookMeta := &komga.BookMetadataDto{Authors: bookAuthors}
			nameMatches := text.VolumeTitleRegex.MatchString(book.Name) ||
				text.VolumeTitleRegex.MatchString(orDefault(bookMetaTitle(book), ""))
			if nameMatches {
				bookMeta.Title = "卷 " + volDisplay
				bookMeta.Number = volDisplay
			}
			if mate != nil {
				if mate.Summary != "" {
					bookMeta.Summary = mate.Summary
				}
				if mate.ReleaseDate != "" {
					bookMeta.ReleaseDate = mate.ReleaseDate
				}
				if mate.Isbn != "" {
					bookMeta.Isbn = mate.Isbn
				}
			} else {
				// 匹配源没有该卷的数据 → 旧简介/ISBN/发售日多半是上次错误匹配留下的，直接清空
				stale := map[string]any{}
				if md := book.Metadata; md != nil {
					if strings.TrimSpace(md.Summary) != "" {
						stale["summary"] = ""
					}
					if strings.TrimSpace(md.ReleaseDate) != "" {
						stale["releaseDate"] = ""
					}
					if strings.TrimSpace(md.Isbn) != "" {
						stale["isbn"] = ""
					}
				}
				if len(stale) > 0 && !s.DryRun {
					if err := s.Komga.PatchBookMetadataRaw(book.ID, stale); err != nil {
						s.Log.Error("《" + seriesName + "》卷 " + volDisplay + " 旧卷信息清空失败: " + err.Error())
					} else {
						s.Log.Info("《" + seriesName + "》卷 " + volDisplay + "：匹配源无该卷数据，旧简介/ISBN/发售日已清空")
					}
				}
			}

			if komga.HasChanges(bookMeta) {
				diff := diffBookMeta(book.Metadata, bookMeta)
				if s.DryRun {
					sink.observe(Change{Kind: ChangeBookMeta, SeriesID: book.SeriesID, BookID: book.ID,
						Target: "《" + seriesName + "》卷 " + volDisplay, Desc: "将更新卷信息（Bangumi）",
						FieldDiff: diff})
				} else {
					if err := s.Komga.PatchBookMetadata(book.ID, bookMeta); err != nil {
						s.Log.Error("处理《" + seriesName + "》卷 " + volDisplay + " (book " + book.ID + ") 出错: " + err.Error())
					} else {
						s.Log.Info("《" + seriesName + "》第 " + strconv.Itoa(book.Number) + " 卷信息已更新")
						sink.observe(Change{Kind: ChangeBookMeta, SeriesID: book.SeriesID, BookID: book.ID,
							Target: "《" + seriesName + "》卷 " + volDisplay, Desc: "已更新卷信息（Bangumi）",
							FieldDiff: diff})
					}
				}
			}
		}

		// 单卷封面
		if s.WithCovers && mate != nil && len(mate.CoverURLs) > 0 {
			s.updateBookCover(&book, seriesName, volDisplay, mate.CoverURLs, sink)
		}
	}
}

func findMate(mates []bangumi.VolumeMate, volNum int) *bangumi.VolumeMate {
	if len(mates) == 0 || volNum <= 0 {
		return nil
	}
	for i := range mates {
		if n, err := strconv.Atoi(mates[i].Num); err == nil && n == volNum {
			return &mates[i]
		}
	}
	return nil
}

func bookMetaTitle(b komga.BookDto) string {
	if b.Metadata != nil {
		return b.Metadata.Title
	}
	return ""
}

func bookMetaNumber(b komga.BookDto) string {
	if b.Metadata != nil {
		return b.Metadata.Number
	}
	return ""
}

func pad2(n int) string {
	s := strconv.Itoa(n)
	if len(s) < 2 {
		return "0" + s
	}
	return s
}

// updateSeriesCover 系列封面上传（清理旧的非选中 USER_UPLOADED → 下载候选 → multipart 上传）。
func (s *Service) updateSeriesCover(seriesID, seriesName string, orderedURLs []string, sink *changeSink) {
	if s.DryRun {
		s.Log.Info(fmt.Sprintf("[Dry-run] 将清理并上传系列封面《%s》（%d 个候选 URL）", seriesName, len(orderedURLs)))
		sink.observe(Change{Kind: ChangeSeriesCover, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: fmt.Sprintf("将清理并上传系列封面（%d 个候选）", len(orderedURLs))})
		return
	}

	// 清理非选中的 USER_UPLOADED 旧封面
	if thumbs, err := s.Komga.GetSeriesThumbnails(seriesID); err == nil {
		for _, t := range thumbs {
			if t.Type == "USER_UPLOADED" && !t.Selected {
				if err := s.Komga.DeleteSeriesThumbnail(seriesID, t.ID); err != nil {
					s.Log.Error("《" + seriesName + "》系列封面清理失败 (ID: " + t.ID + "): " + err.Error())
				}
			}
		}
	}

	for i, u := range orderedURLs {
		sizeLabel := []string{"首选", "中尺寸", "通用尺寸", "小尺寸"}[min(i, 3)]
		blob, err := s.downloadImage(u)
		if err != nil {
			s.Log.Warn("《" + seriesName + "》" + sizeLabel + " 封面下载失败，换下一个尺寸: " + err.Error())
			continue
		}
		if len(blob) == 0 {
			s.Log.Warn("《" + seriesName + "》下载 " + sizeLabel + " 封面失败或为空")
			continue
		}
		if err := s.Komga.UploadSeriesCover(seriesID, blob, "series_cover_"+seriesID+".jpg"); err != nil {
			var apiErr *komga.APIError
			if errors.As(err, &apiErr) && apiErr.Status == 413 {
				s.Log.Warn("《" + seriesName + "》" + sizeLabel + " 封面过大(413)，尝试更小尺寸…")
				continue
			}
			s.Log.Error("《" + seriesName + "》系列封面 (" + sizeLabel + ") 更新失败: " + err.Error())
			return
		}
		s.Log.Info(fmt.Sprintf("《%s》系列封面 (%s, %dKB) 已更新", seriesName, sizeLabel, len(blob)/1024))
		sink.observe(Change{Kind: ChangeSeriesCover, SeriesID: seriesID, Target: "《" + seriesName + "》",
			Desc: fmt.Sprintf("已上传系列封面（%s，%dKB）", sizeLabel, len(blob)/1024)})
		if s.State != nil {
			s.State.SetCover(seriesID, true)
		}
		return
	}
}

// updateBookCover 单册封面上传。
func (s *Service) updateBookCover(book *komga.BookDto, seriesName, volDisplay string, orderedURLs []string, sink *changeSink) {
	if len(orderedURLs) == 0 {
		return
	}
	if s.DryRun {
		sink.observe(Change{Kind: ChangeBookCover, SeriesID: book.SeriesID, BookID: book.ID,
			Target: "《" + seriesName + "》卷 " + volDisplay,
			Desc:   fmt.Sprintf("将上传卷封面（%d 个候选）", len(orderedURLs))})
		return
	}
	for i, u := range orderedURLs {
		sizeLabel := []string{"首选", "中等", "通用", "较小"}[min(i, 3)]
		blob, err := s.downloadImage(u)
		if err != nil {
			s.Log.Warn("《" + seriesName + "》卷 " + volDisplay + " " + sizeLabel + " 封面下载失败，换下一个尺寸: " + err.Error())
			continue
		}
		if len(blob) == 0 {
			continue
		}
		// 已存在相同大小的封面则跳过（同一张图不重复上传）
		if existing, err := s.Komga.GetBookThumbnails(book.ID); err == nil {
			same := false
			for _, t := range existing {
				if t.FileSize == int64(len(blob)) {
					same = true
					break
				}
			}
			if same {
				s.Log.Info("《" + seriesName + "》卷 " + volDisplay + " 封面已存在相同大小，跳过")
				return
			}
		}
		if err := s.Komga.UploadBookCover(book.ID, blob, "book_cover_"+book.ID+".jpg"); err != nil {
			var apiErr *komga.APIError
			if errors.As(err, &apiErr) && apiErr.Status == 413 {
				s.Log.Warn("《" + seriesName + "》卷 " + volDisplay + " " + sizeLabel + " 封面过大(413)，尝试更小…")
				continue
			}
			s.Log.Error("《" + seriesName + "》卷 " + volDisplay + " 封面 (" + sizeLabel + ") 更新失败: " + err.Error())
			return
		}
		s.Log.Info(fmt.Sprintf("《%s》卷 %s 封面 (%s, %dKB) 已更新", seriesName, volDisplay, sizeLabel, len(blob)/1024))
		sink.observe(Change{Kind: ChangeBookCover, SeriesID: book.SeriesID, BookID: book.ID,
			Target: "《" + seriesName + "》卷 " + volDisplay,
			Desc:   fmt.Sprintf("已上传卷封面（%s，%dKB）", sizeLabel, len(blob)/1024)})
		return
	}
}

// ifUpdateBook 启发式（对齐脚本 ifUpdateBook）。
func ifUpdateBook(books []komga.BookDto, bookAuthors []komga.AuthorDto) bool {
	if len(bookAuthors) == 0 || len(books) == 0 {
		return false
	}
	meta := books[len(books)-1].Metadata
	if meta == nil || meta.Title == "" || meta.Summary == "" || meta.ReleaseDate == "" || meta.Isbn == "" {
		return true
	}
	title := meta.Title
	if len([]rune(title)) > 16 {
		return true
	}
	if strings.ContainsAny(title, "[]") {
		return true
	}
	lower := strings.ToLower(title)
	if strings.Contains(lower, ".zip") || strings.Contains(lower, ".cbz") {
		return true
	}
	if !volumePrefixRegex.MatchString(title) {
		return true
	}
	if len(meta.Authors) == 0 {
		return true
	}
	return false
}

// volumeNumsNeedUpdate 启发式（对齐脚本 getVolumeNumsNeedUpdate）。
func volumeNumsNeedUpdate(books []komga.BookDto) map[int]bool {
	need := map[int]bool{}
	for _, book := range books {
		volNum := text.ExtractBookVolumeNumber(book.Name, bookMetaTitle(book), bookMetaNumber(book))
		if volNum <= 0 {
			continue
		}
		meta := book.Metadata
		if meta == nil || meta.Title == "" || meta.Summary == "" || meta.ReleaseDate == "" || meta.Isbn == "" {
			need[volNum] = true
		}
	}
	return need
}

func containsStr(list []string, v string) bool {
	for _, s := range list {
		if s == v {
			return true
		}
	}
	return false
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}
