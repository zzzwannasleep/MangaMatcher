package scrape

import (
	"context"
	"strings"

	"komgabangumi/internal/bangumi"
	"komgabangumi/internal/komga"
	"komgabangumi/internal/sources"
	"komgabangumi/internal/text"
)

// Source 标签常量（候选面板/链路）。
const (
	SourceBgm      = "Bgm"
	SourceMox      = "Mox"
	SourceBaike    = "Baike"
	SourceMangaDex = "MangaDex"
)

// IsBtvLink Bangumi 站链接判定（bangumi.tv / bgm.tv / chii.in 及子域）。
func IsBtvLink(u string) bool {
	host := strings.ToLower(strings.SplitN(strings.TrimPrefix(strings.TrimPrefix(u, "https://"), "http://"), "/", 2)[0])
	return host == "bangumi.tv" || strings.HasSuffix(host, ".bangumi.tv") ||
		host == "bgm.tv" || strings.HasSuffix(host, ".bgm.tv") ||
		host == "chii.in" || strings.HasSuffix(host, ".chii.in")
}

// btvSubjectID 从 Btv 链接解析 subject id。
func btvSubjectID(u string) int {
	idx := strings.LastIndex(u, "/subject/")
	if idx < 0 {
		return 0
	}
	num := 0
	for _, ch := range u[idx+len("/subject/"):] {
		if ch < '0' || ch > '9' {
			break
		}
		num = num*10 + int(ch-'0')
	}
	return num
}

// MatchOutcome 单系列匹配结果（链路产物）。
type MatchOutcome struct {
	Source     string // Bgm|Bw|Mox|MangaDex|Baike
	SubjectID  int    // Bgm 用
	BwID       string
	MoxID      string
	MdID       string
	BaikeTitle string // 百科无 id 定位，用词条标题重查
	MatchTitle string
}

// RunChain 匹配回退链（SPEC-v2 第 7 节，顺序对齐桌面版 BatchMatcher.PreciseMatchAsync）：
//
//	Bangumi精确 → BookWalker索引 → AI识别(拿不到时问AI作品名 jp/cn/en → 三名各重搜 Bangumi/BW/Mox)
//	→ AI智能选择(多候选) → Mox → AI中文译日文再查BW → MangaDex → 百度百科 → 失败
//
// 单源失败不中断整链；AI 结果进程内缓存；候选>12 先按相似度截断再给 AI。
// matchType：Bangumi platform 过滤（漫画/小说）。
func (s *Service) RunChain(ctx context.Context, series *komga.SeriesDto, seriesName, matchType string) (*MatchOutcome, error) {
	terms := text.ExtractSeriesTitles(seriesName, 4)
	if len(terms) == 0 || terms[0] == "" {
		return nil, errOf("无法从标题解析搜索词")
	}
	searchTerm := terms[0]

	// ---- 1. Bangumi 精确搜索（候选词逐个试）----
	var searchResults []bangumi.SearchCandidate
	var precise *sources.Candidate
	for _, term := range terms {
		r, err := s.Bangumi.SearchSubjects(term, matchType, 0)
		if err != nil {
			s.Log.Warn("《" + seriesName + "》\"" + term + "\" 搜索失败: " + err.Error())
			continue
		}
		for _, it := range r {
			dup := false
			for _, x := range searchResults {
				if x.ID == it.ID {
					dup = true
					break
				}
			}
			if !dup {
				searchResults = append(searchResults, it)
			}
		}
		if p := sources.FindPreciseMatch(toCandListFromBgm(r), text.ExtractAndNormalizeTitle(term)); p != nil {
			precise = p
			searchTerm = term
			break
		}
	}
	if precise != nil {
		display := sources.FirstNonEmpty(precise.Title, precise.OriTitle, "未知匹配标题")
		s.Log.Info("《" + seriesName + "》精确匹配（词:\"" + searchTerm + "\"）:《" + display + "》 subject=" + itoa(precise.BgmID) + "，刮削中…")
		return &MatchOutcome{Source: SourceBgm, SubjectID: precise.BgmID, MatchTitle: display}, nil
	}

	// ---- 2. BookWalker 本地索引精确匹配（每个候选词都试一遍）----
	var bwHit *sources.BwSeries
	if s.BW != nil {
		bwHit = s.BW.FindExact(terms[0])
		if bwHit == nil {
			for _, t := range terms[1:] {
				bwHit = s.BW.FindExact(t)
				if bwHit != nil {
					break
				}
			}
		}
	}
	if bwHit != nil {
		s.Log.Info("《" + seriesName + "》Bangumi 无精确匹配，BookWalker 命中:《" + bwHit.Title + "》，刮削中…")
		return &MatchOutcome{Source: sources.BwSource, BwID: bwHit.ID, MatchTitle: bwHit.Title}, nil
	}

	// ---- 3. AI 识别 → 三名各重搜 Bangumi/BW/Mox → AI 智能选择 ----
	var identify *sources.IdentifyResult
	if s.AI != nil && s.AIIdentify {
		id2, err := s.AI.IdentifySeries(ctx, seriesName, terms)
		if err != nil {
			s.Log.Warn("《" + seriesName + "》AI 识别失败: " + err.Error())
		} else {
			identify = id2
		}
	}
	if identify != nil {
		var aiTerms []string
		if identify.Jp != "" {
			aiTerms = append(aiTerms, identify.Jp)
		}
		if identify.En != "" {
			aiTerms = append(aiTerms, identify.En)
		}
		if identify.Cn != "" && !containsStr(aiTerms, identify.Cn) {
			aiTerms = append(aiTerms, identify.Cn)
		}
		s.Log.Info("《" + seriesName + "》AI 识别为《" + identify.Jp + "》" +
			tailStr(identify.Cn != "", "（《"+identify.Cn+"》）") +
			tailStr(identify.En != "", " 英文 "+identify.En) +
			tailStr(!identify.Confident, "（AI 不太确定）") + "，按原名重搜…")

		// 3a. Bangumi 按原名重搜 → 精确匹配 → AI 从候选里挑（候选>12 截断）
		var aiPrecise *sources.Candidate
		var aiCands []sources.Candidate
		for _, t := range aiTerms {
			r, err := s.Bangumi.SearchSubjects(t, matchType, 0)
			if err != nil {
				s.Log.Warn("《" + seriesName + "》AI 原名 \"" + t + "\" 搜索失败: " + err.Error())
				continue
			}
			for _, it := range r {
				dup := false
				for _, x := range aiCands {
					if x.BgmID == it.ID {
						dup = true
						break
					}
				}
				if !dup {
					aiCands = append(aiCands, bgmToCandidate(it))
				}
			}
			if p := sources.FindPreciseMatch(toCandListFromBgm(r), text.ExtractAndNormalizeTitle(t)); p != nil {
				aiPrecise = p
				break
			}
		}
		if aiPrecise == nil && len(aiCands) > 1 && s.AI != nil && s.AISmartPick {
			ranked := sources.RankCandidates(aiCands, aiTerms[0], 12)
			s.Log.Info("《" + seriesName + "》原名重搜候选 " + itoa(len(ranked)) + " 个，AI 选择中…")
			if pick, err := s.AI.PickBest(ctx, seriesName, ranked); err != nil {
				s.Log.Warn("《" + seriesName + "》AI 选择失败: " + err.Error())
			} else if pick != nil {
				aiPrecise = pick
			}
		}
		if aiPrecise != nil {
			display := sources.FirstNonEmpty(aiPrecise.Title, aiPrecise.OriTitle, "未知匹配标题")
			s.Log.Info("《" + seriesName + "》AI 原名重搜命中:《" + display + "》 subject=" + itoa(aiPrecise.BgmID) + "，刮削中…")
			return &MatchOutcome{Source: SourceBgm, SubjectID: aiPrecise.BgmID, MatchTitle: display}, nil
		}

		// 3b. BookWalker 用日文/英文原名查（BW 目录只有日英标题的那条正路）
		if s.BW != nil {
			for _, t := range aiTerms {
				bwAi := s.BW.FindExact(t)
				if bwAi == nil {
					continue
				}
				s.Log.Info("《" + seriesName + "》AI 原名「" + t + "」BookWalker 命中:《" + bwAi.Title + "》，刮削中…")
				return &MatchOutcome{Source: sources.BwSource, BwID: bwAi.ID, MatchTitle: bwAi.Title}, nil
			}
		}

		// 3c. Mox 用 AI 给的中文名查（识别出的通行译名往往比文件名里的更准）
		if s.Mox != nil {
			moxTerms := []string{}
			if identify.Cn != "" {
				moxTerms = append(moxTerms, identify.Cn)
			}
			moxTerms = append(moxTerms, terms[0])
			for i, t := range moxTerms {
				if i >= 2 {
					break
				}
				if out := s.tryMoxSearch(ctx, seriesName, t, searchTerm); out != nil {
					return out, nil
				}
			}
		}
	}

	// ---- 4. AI 没配置或认不出时的老兜底：Mox 直搜 ----
	if s.Mox != nil && (s.AI == nil || identify == nil) {
		for i, t := range terms {
			if i >= 2 {
				break
			}
			if out := s.tryMoxSearch(ctx, seriesName, t, searchTerm); out != nil {
				return out, nil
			}
		}
	}

	// ---- 5. AI 把中文标题译成日文原名 → 再查 BookWalker ----
	if s.BW != nil && s.AI != nil && (s.AI == nil || identify == nil) {
		for i, t := range terms {
			if i >= 2 {
				break
			}
			jp, err := s.AI.TranslateTitleToJapanese(ctx, t)
			if err != nil || jp == "" {
				continue
			}
			bwJp := s.BW.FindExact(jp)
			if bwJp == nil {
				continue
			}
			s.Log.Info("《" + seriesName + "》\"" + t + "\" 译成日文「" + jp + "」后 BookWalker 命中:《" + bwJp.Title + "》，刮削中…")
			return &MatchOutcome{Source: sources.BwSource, BwID: bwJp.ID, MatchTitle: bwJp.Title}, nil
		}
	}

	// ---- 6. MangaDex（英文圈图源，免密钥）----
	if s.MangaDex != nil {
		for i, t := range terms {
			if i >= 2 {
				break
			}
			mdList, err := s.MangaDex.Search(ctx, t, 8)
			if err != nil {
				s.Log.Warn("《" + seriesName + "》MangaDex 搜索失败（\"" + t + "\"）: " + err.Error())
				continue
			}
			if len(mdList) == 0 {
				continue
			}
			// 客户端兜底：只收日漫
			var filtered []*sources.MangaDexSeries
			for _, m := range mdList {
				if strings.EqualFold(m.OriginalLanguage, "ja") {
					filtered = append(filtered, m)
				}
			}
			if len(filtered) == 0 {
				continue
			}
			var cands []sources.Candidate
			for _, m := range filtered {
				cands = append(cands, sources.Candidate{BgmID: 0, Title: m.Title, OriTitle: m.OriTitle,
					Aliases: strings.Join(m.AltTitles[:minInt(len(m.AltTitles), 4)], " / "),
					CoverURL: m.CoverURL, Source: SourceMangaDex, MdID: m.ID})
			}
			mdPick := sources.FindPreciseMatch(cands, text.ExtractAndNormalizeTitle(t))
			if mdPick == nil {
				continue
			}
			for _, m := range filtered {
				if m.ID == mdPick.MdID {
					s.Log.Info("《" + seriesName + "》MangaDex 命中:《" + m.Title + "》，刮削中…")
					return &MatchOutcome{Source: SourceMangaDex, MdID: m.ID, MatchTitle: m.Title}, nil
				}
			}
			break // 搜到了（哪怕没刮成）就不再换词
		}
	}

	// ---- 7. 百度百科（词条卡片）—— 冷门漫画最终兜底 ----
	if s.Baike != nil {
		var bkTerms []string
		if identify != nil && identify.Cn != "" {
			bkTerms = append(bkTerms, identify.Cn)
		}
		if identify != nil && identify.Jp != "" {
			bkTerms = append(bkTerms, identify.Jp)
		}
		bkTerms = append(bkTerms, terms...)
		seen := map[string]bool{}
		var uniqTerms []string
		for _, t := range bkTerms {
			if !seen[t] {
				seen[t] = true
				uniqTerms = append(uniqTerms, t)
			}
			if len(uniqTerms) >= 4 {
				break
			}
		}
		for _, t := range uniqTerms {
			card, err := s.Baike.SearchCard(ctx, t)
			if err != nil {
				s.Log.Warn("《" + seriesName + "》百度百科搜索失败（\"" + t + "\"）: " + err.Error())
				continue
			}
			if card == nil {
				continue
			}
			if !card.LooksLikeMangaWork() {
				s.Log.Warn("《" + seriesName + "》百度百科《" + card.Title + "》非漫画作品词条（人物/影视/小说，概述图不是漫画封面），跳过")
				continue
			}
			bkCands := []sources.Candidate{{BgmID: 0, Title: card.Title, OriTitle: card.Field("原版名称"),
				Author: card.Field("作者"), Source: SourceBaike}}
			if sources.FindPreciseMatch(bkCands, text.ExtractAndNormalizeTitle(t)) == nil {
				continue
			}
			s.Log.Info("《" + seriesName + "》百度百科命中:《" + card.Title + "》，刮削中…")
			return &MatchOutcome{Source: SourceBaike, BaikeTitle: card.Title, MatchTitle: card.Title}, nil
		}
	}

	errMsg := "搜索无结果"
	if len(searchResults) > 0 {
		errMsg = "无精确匹配（标题/原名/别名）"
	}
	s.Log.Warn("《" + seriesName + "》(词:\"" + searchTerm + "\") " + errMsg)
	return nil, errOf(errMsg + "（词:\"" + searchTerm + "\"）")
}

// tryMoxSearch Mox 搜索 + 精确/AI 选择 + 韩漫过滤（命中返回结果）。
func (s *Service) tryMoxSearch(ctx context.Context, seriesName, term, searchTerm string) *MatchOutcome {
	ml, err := s.Mox.Search(ctx, term, 8)
	if err != nil {
		s.Log.Warn("《" + seriesName + "》Mox 搜索失败（\"" + term + "\"）: " + err.Error())
		return nil
	}
	if len(ml) == 0 {
		return nil
	}
	var filtered []sources.MoxSearchResult
	for _, m := range ml {
		if !sources.LooksKoreanMox(m.Title) {
			filtered = append(filtered, m)
		}
	}
	if len(filtered) == 0 {
		return nil
	}
	var cands []sources.Candidate
	for _, m := range filtered {
		cands = append(cands, sources.Candidate{BgmID: 0, Title: m.Title, OriTitle: m.Title,
			Author: m.Author, CoverURL: m.CoverURL, Source: SourceMox, MoxID: m.ID})
	}
	pick := sources.FindPreciseMatch(cands, text.ExtractAndNormalizeTitle(searchTerm))
	if pick == nil && s.AI != nil && s.AISmartPick && len(cands) > 1 {
		if p, err := s.AI.PickBest(ctx, seriesName, cands); err == nil && p != nil {
			pick = p
		} else if err != nil {
			s.Log.Warn("《" + seriesName + "》Mox 候选 AI 选择失败: " + err.Error())
		}
	}
	if pick == nil || pick.MoxID == "" {
		return nil
	}
	moxDetail, err := s.Mox.GetSeries(ctx, pick.MoxID)
	if err != nil {
		s.Log.Warn("《" + seriesName + "》Mox 刮削失败: " + err.Error())
		return nil
	}
	if sources.LooksKoreanMox(moxDetail.Title) || sources.LooksKoreanMox(moxDetail.OriTitle) {
		s.Log.Warn("《" + seriesName + "》Mox 命中的《" + moxDetail.Title + "》疑似韩漫，跳过（只匹配日漫）")
		return nil
	}
	s.Log.Info("《" + seriesName + "》Mox 命中:《" + moxDetail.Title + "》，刮削中…")
	return &MatchOutcome{Source: SourceMox, MoxID: moxDetail.ID, MatchTitle: moxDetail.Title}
}

// bgmToCandidate Bangumi 搜索候选 → 跨源候选。
func bgmToCandidate(c bangumi.SearchCandidate) sources.Candidate {
	return sources.Candidate{BgmID: c.ID, Title: c.Title, OriTitle: c.OriTitle, Author: c.Author,
		Aliases: c.Aliases, CoverURL: c.CoverURL, Source: SourceBgm}
}

func toCandListFromBgm(list []bangumi.SearchCandidate) []sources.Candidate {
	out := make([]sources.Candidate, 0, len(list))
	for _, c := range list {
		out = append(out, bgmToCandidate(c))
	}
	return out
}

func tailStr(cond bool, s string) string {
	if cond {
		return s
	}
	return ""
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

// ScrapeOutcome 对已选定的匹配结果执行刮削（链路终点 / 纠正匹配入口）。
func (s *Service) ScrapeOutcome(series *komga.SeriesDto, out *MatchOutcome, replace bool) *SeriesResult {
	switch out.Source {
	case SourceBgm:
		return s.ScrapeSeriesOpts(series, out.SubjectID, replace)
	case sources.BwSource:
		bw := s.BW.FindByID(out.BwID)
		if bw == nil {
			return &SeriesResult{Error: "BookWalker 条目不存在: " + out.BwID}
		}
		return s.scrapeFromBookWalker(series, bw, replace)
	case SourceMox:
		mox, err := s.Mox.GetSeries(context.Background(), out.MoxID)
		if err != nil {
			return &SeriesResult{Error: "获取 Mox 详情失败: " + err.Error()}
		}
		return s.scrapeFromMox(series, mox, replace)
	case SourceMangaDex:
		md, err := s.MangaDex.Get(context.Background(), out.MdID)
		if err != nil {
			return &SeriesResult{Error: "获取 MangaDex 详情失败: " + err.Error()}
		}
		if md == nil {
			return &SeriesResult{Error: "MangaDex 条目不存在: " + out.MdID}
		}
		return s.scrapeFromMangaDex(series, md, replace)
	case SourceBaike:
		card, err := s.Baike.SearchCard(context.Background(), out.BaikeTitle)
		if err != nil {
			return &SeriesResult{Error: "获取百度百科词条失败: " + err.Error()}
		}
		if card == nil {
			return &SeriesResult{Error: "百度百科词条不存在: " + out.BaikeTitle}
		}
		return s.scrapeFromBaike(series, card, replace)
	}
	return &SeriesResult{Error: "未知匹配来源: " + out.Source}
}
