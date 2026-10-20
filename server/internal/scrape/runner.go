package scrape

import (
	"context"
	"log/slog"
	"net/http"
	"strings"

	"komgabangumi/internal/bangumi"
	"komgabangumi/internal/komga"
)

// bangumiClient 别名（保持 runner 字段类型简洁）。
type bangumiClient = bangumi.Client

// bangumiSearchCandidate 别名。
type bangumiSearchCandidate = bangumi.SearchCandidate

// Runner 任务级编排：对一批系列执行 匹配 → 刮削 → 失败进收藏夹。
type Runner struct {
	Komga          *komga.Client
	Bangumi        *bangumiClient
	Log            *slog.Logger
	CollectionName string // "待手动匹配"
	DryRun         bool
	FetchVolumes   bool
	WithCovers     bool
	Concurrency    int // worker pool 并发数（来自全局设置）

	// ---- v2 ----
	Sources          SourceClients
	MatchType        string // 漫画|小说（Bangumi platform 过滤）
	TranslateSummary bool
	AIWriteSummary   bool
	AIIdentify       bool
	AISmartPick      bool
	BwWriteTags      bool
	Whitelist        []string
	Blacklist        []string
	CoverHTTP        *http.Client
	CoverReferer     string
	State            StateWriter
	OverrideMatch    bool // 覆盖匹配：忽略旧链接、搜索词只用文件夹名、命中即 ReplaceMatch、成功移出收藏夹
	ForceRefresh     bool // 已有 Btv 链接也重刮
}

// SeriesItem 单系列处理结果（任务条目）。
type SeriesItem struct {
	SeriesID   string            `json:"seriesId"`
	SeriesName string            `json:"seriesName"`
	Status     string            `json:"status"` // ok|failed|manual|skipped
	Message    string            `json:"message,omitempty"`
	SubjectID  int               `json:"subjectId,omitempty"`
	MatchTitle string            `json:"matchTitle,omitempty"`
	Source     string            `json:"source,omitempty"`
	Changed    map[string][2]any `json:"changed,omitempty"`
}

// NewService 构建单系列刮削器（含全部 v2 开关；jobs/api 包复用）。
func (r *Runner) NewService() *Service {
	return r.newService()
}

// newService 构建单系列刮削器（含全部 v2 开关）。
func (r *Runner) newService() *Service {
	manual := NewManualMatchCollection(r.Komga, r.CollectionName, r.Log)
	return &Service{
		Komga: r.Komga, Bangumi: r.Bangumi, Log: r.Log, Manual: manual,
		DryRun: r.DryRun, FetchVolumes: r.FetchVolumes, WithCovers: r.WithCovers,
		AI: r.Sources.AI, BW: r.Sources.BW, Mox: r.Sources.Mox,
		MangaDex: r.Sources.MangaDex, Baike: r.Sources.Baike, EH: r.Sources.EH,
		State: r.State, TranslateSummary: r.TranslateSummary, AIWriteSummary: r.AIWriteSummary,
		AIIdentify: r.AIIdentify, AISmartPick: r.AISmartPick,
		BwWriteTags: r.BwWriteTags, Whitelist: r.Whitelist, Blacklist: r.Blacklist,
		CoverHTTP: r.CoverHTTP, CoverReferer: r.CoverReferer,
	}
}

// hasSourceLink 系列是否已有任一来源链接（skip-scraped 判定：列表元数据 links，零额外请求）。
func hasSourceLink(series *komga.SeriesDto) (string, int) {
	if series.Metadata == nil {
		return "", 0
	}
	for _, l := range series.Metadata.Links {
		if IsBtvLink(l.URL) {
			return "Btv", btvSubjectID(l.URL)
		}
		if IsSourceLink(l.URL) {
			return "other", 0
		}
	}
	return "", 0
}

// ProcessSeries 对单系列执行匹配 + 刮削（在任务 worker goroutine 中调用）。
func (r *Runner) ProcessSeries(ctx context.Context, series *komga.SeriesDto) SeriesItem {
	seriesName := series.Name
	if series.Metadata != nil && series.Metadata.Title != "" {
		seriesName = series.Metadata.Title
	}
	item := SeriesItem{SeriesID: series.ID, SeriesName: seriesName}

	svc := r.newService()
	// ---- 跳过已刮（overrideMatch/forceRefresh 除外）：列表 links 判断，零额外请求 ----
	if !r.OverrideMatch {
		if linkKind, subjectID := hasSourceLink(series); linkKind != "" {
			if r.ForceRefresh && linkKind == "Btv" {
				r.Log.Info("《" + seriesName + "》已存在 Btv 链接，强制刷新中…")
				res := svc.ScrapeSeriesOpts(series, subjectID, false)
				if res.Success {
					item.Status = "ok"
					item.SubjectID = subjectID
					item.MatchTitle = res.MatchTitle
					item.Source = SourceBgm
					item.Changed = collectChanges(res.Changes)
				} else {
					item.Status = "failed"
					item.Message = res.Error
				}
				return item
			}
			r.Log.Info("《" + seriesName + "》已存在来源链接（" + linkKind + "），跳过")
			item.Status = "skipped"
			item.Message = "已存在来源链接"
			return item
		}
	}

	// ---- 匹配：overrideMatch 时搜索词只用文件夹名（元数据标题可能已被旧匹配污染）----
	searchName := seriesName
	if r.OverrideMatch {
		searchName = series.Name // 文件夹名是用户的原始命名
	}

	var outcome *MatchOutcome
	var matchErr error
	if r.Sources.AI == nil && r.Sources.BW == nil && r.Sources.Mox == nil &&
		r.Sources.MangaDex == nil && r.Sources.Baike == nil && r.Sources.EH == nil {
		// v1 纯 Bangumi 链（无任何 v2 源配置时行为与 v1 完全一致）
		search := func(keyword string) ([]bangumiSearchCandidate, error) {
			return r.Bangumi.SearchSubjects(keyword, r.matchType(), 0)
		}
		precise, term, err := MatchSeries(search, searchName)
		if err != nil {
			matchErr = err
			if term != "" {
				matchErr = errOf(err.Error() + "（词:\"" + term + "\"）")
			}
		} else {
			outcome = &MatchOutcome{Source: SourceBgm, SubjectID: precise.ID,
				MatchTitle: orDefault(precise.Title, precise.OriTitle)}
		}
	} else {
		outcome, matchErr = svc.RunChain(ctx, series, searchName, r.matchType())
	}

	if outcome == nil {
		item.Message = matchErr.Error()
		if r.DryRun {
			r.Log.Info("[Dry-run] 将把《" + seriesName + "》加入 \"" + r.CollectionName + "\" 收藏夹")
		} else if !r.OverrideMatch {
			manual := NewManualMatchCollection(r.Komga, r.CollectionName, r.Log)
			if manual.AddSeries(series.ID, seriesName) {
				item.Status = "manual"
			} else {
				item.Status = "failed"
			}
		} else {
			item.Status = "failed" // 覆盖匹配：没匹配到的保持原样、不进收藏夹
		}
		if item.Status == "" {
			item.Status = "manual"
		}
		r.Log.Warn("《" + seriesName + "》" + item.Message)
		return item
	}

	// 尊重外部取消：刮削前检查
	if err := ctx.Err(); err != nil {
		item.Status = "failed"
		item.Message = "任务已取消"
		return item
	}

	replace := r.OverrideMatch // 覆盖匹配命中即 ReplaceMatch；纠正匹配走 API 层
	res := svc.ScrapeOutcome(series, outcome, replace)
	if !res.Success {
		item.Status = "failed"
		item.Message = res.Error
		item.MatchTitle = outcome.MatchTitle
		item.Source = outcome.Source
		if !r.DryRun && !strings.Contains(item.Message, "已取消") && !r.OverrideMatch {
			manual := NewManualMatchCollection(r.Komga, r.CollectionName, r.Log)
			manual.AddSeries(series.ID, seriesName)
		}
		return item
	}

	item.Status = "ok"
	item.Source = outcome.Source
	item.MatchTitle = outcome.MatchTitle
	if outcome.Source == SourceBgm {
		item.SubjectID = outcome.SubjectID
	}
	item.Changed = collectChanges(res.Changes)
	return item
}

func (r *Runner) matchType() string {
	if r.MatchType == "" {
		return "漫画"
	}
	return r.MatchType
}

// collectChanges 汇总字段差异（系列级 + 卷级合并）。
func collectChanges(changes []Change) map[string][2]any {
	changed := map[string][2]any{}
	for _, ch := range changes {
		for k, v := range ch.FieldDiff {
			key := k
			if ch.Kind == ChangeBookMeta && ch.BookID != "" {
				key = "books." + ch.BookID + "." + k
			}
			changed[key] = v
		}
	}
	return changed
}

// CollectChanges 汇总字段差异（jobs 包 / api 包共用入口）。
func CollectChanges(changes []Change) map[string][2]any { return collectChanges(changes) }
