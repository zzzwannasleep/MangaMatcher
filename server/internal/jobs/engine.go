// Package jobs：刮削任务引擎。goroutine worker pool（并发数可配、对 Bangumi ≥350ms 限速、
// 429/5xx 退避重试在 bangumi.Client 内），任务与进度保存在内存 map + 互斥锁，支持 cancel。
package jobs

import (
	"context"
	"fmt"
	"log/slog"
	"sort"
	"strconv"
	"sync"
	"time"

	"komgabangumi/internal/komga"
	"komgabangumi/internal/scrape"
)

// Item 任务条目状态（SPEC：ok|failed|skipped|manual）。
type Item = scrape.SeriesItem

// JobOptions 任务选项（SPEC-v2 第 6 节）。
type JobOptions struct {
	DryRun        bool
	ForceRefresh  bool   // 已有 Btv 链接的系列是否重刮
	OverrideMatch bool   // 覆盖匹配：忽略旧链接、搜索词只用文件夹名、命中即 ReplaceMatch
	Kind          string // scrape（默认）| tag（版本标记打标）
}

// Job 任务。
type Job struct {
	ID         string     `json:"id"`
	Kind       string     `json:"kind"` // scrape|tag
	LibraryID  string     `json:"libraryId"`
	Total      int        `json:"total"`
	Done       int        `json:"done"`
	OK         int        `json:"ok"`
	Failed     int        `json:"failed"`
	Status     string     `json:"status"` // running|done|canceled
	DryRun     bool       `json:"dryRun"`
	StartedAt  time.Time  `json:"startedAt"`
	FinishedAt *time.Time `json:"finishedAt,omitempty"`
	Items      []Item     `json:"items,omitempty"`

	cancel context.CancelFunc
	log    *slog.Logger
}

// Engine 任务引擎。
type Engine struct {
	mu           sync.Mutex
	jobs         map[string]*Job
	seq          int
	log          *slog.Logger
	komgaFactory func() *komga.Client                   // 按最新设置构建 Komga 客户端
	newRunner    func(dryRun bool) *scrape.Runner       // 由 api 层注入（含最新设置）
	OnDone       func(jobID string, seriesIDs []string) // 任务结束钩子（缓存回源等；api 层注入）
}

// NewEngine 创建引擎。
func NewEngine(komgaFactory func() *komga.Client, log *slog.Logger, newRunner func(dryRun bool) *scrape.Runner) *Engine {
	return &Engine{jobs: map[string]*Job{}, log: log, komgaFactory: komgaFactory, newRunner: newRunner}
}

// Submit 创建并启动任务。seriesIDs 为空 = 全库。
// 返回 job id。master 的 libraryId 权限校验在 api 层完成。
func (e *Engine) Submit(libraryID string, seriesIDs []string, opts JobOptions) string {
	e.mu.Lock()
	e.seq++
	id := "job-" + time.Now().Format("20060102-150405") + "-" + itoa(e.seq)
	ctx, cancel := context.WithCancel(context.Background())
	kind := opts.Kind
	if kind == "" {
		kind = "scrape"
	}
	job := &Job{
		ID: id, Kind: kind, LibraryID: libraryID, Status: "running",
		DryRun: opts.DryRun, StartedAt: time.Now(), cancel: cancel, log: e.log,
	}
	e.jobs[id] = job
	e.mu.Unlock()

	go e.run(ctx, job, seriesIDs, opts)
	return id
}

// SubmitApply 纠正/手动匹配的单系列任务（ReplaceMatch 语义）。
// source/id 为候选定位（bangumi=id、bookwalker/mox/mangadex=bwID/moxID/mdID、baike=词条标题）。
func (e *Engine) SubmitApply(series *komga.SeriesDto, source, id string, dryRun bool,
	newRunner func(dryRun bool) *scrape.Runner, sc scrape.SourceClients) string {
	e.mu.Lock()
	e.seq++
	jobID := "job-" + time.Now().Format("20060102-150405") + "-" + itoa(e.seq)
	ctx, cancel := context.WithCancel(context.Background())
	job := &Job{ID: jobID, Kind: "scrape", LibraryID: series.LibraryID, Status: "running",
		Total: 1, DryRun: dryRun, StartedAt: time.Now(), cancel: cancel, log: e.log}
	e.jobs[jobID] = job
	e.mu.Unlock()

	runner := newRunner(dryRun)
	runner.Sources = sc
	svc := runner.NewService()
	go func() {
		// API source 名（SPEC：bangumi|bookwalker|mox|mangadex|baike|ehentai）→ 内部标签
		apiSource := source
		switch source {
		case "bangumi":
			apiSource = "Bgm"
		case "bookwalker":
			apiSource = "Bw"
		case "mox":
			apiSource = "Mox"
		case "mangadex":
			apiSource = "MangaDex"
		case "baike":
			apiSource = "Baike"
		case "ehentai", "EH":
			apiSource = "EH"
		}
		outcome := &scrape.MatchOutcome{Source: apiSource, MatchTitle: id}
		switch source {
		case "bangumi":
			fmt.Sscanf(id, "%d", &outcome.SubjectID)
			outcome.MatchTitle = "subject " + id
		case "bookwalker":
			outcome.BwID = id
			if bw := sc.BW.FindByID(id); bw != nil {
				outcome.MatchTitle = bw.Title
			}
		case "mox":
			outcome.MoxID = id
		case "mangadex":
			outcome.MdID = id
		case "baike":
			outcome.BaikeTitle = id
			outcome.MatchTitle = id
		case "ehentai", "EH":
			// id = "gid/token"（候选/链接解析产出的 EhID 形状）
			outcome.EhID = id
		}
		res := svc.ScrapeOutcome(series, outcome, true) // ReplaceMatch：旧链接剔除、锁解锁重写、全部卷重抓
		item := Item{SeriesID: series.ID, SeriesName: series.Name,
			Status: "ok", MatchTitle: outcome.MatchTitle, Source: source}
		if !res.Success {
			item.Status = "failed"
			item.Message = res.Error
		} else {
			item.Changed = scrape.CollectChanges(res.Changes)
		}
		e.appendItem(job, item)
		e.finish(ctx, job)
	}()
	return jobID
}

func (e *Engine) run(ctx context.Context, job *Job, seriesIDs []string, opts JobOptions) {
	runner := e.newRunner(job.DryRun)
	client := e.komgaFactory()

	// 目标系列列表
	var series []komga.SeriesDto
	var err error
	if len(seriesIDs) > 0 {
		for _, sid := range seriesIDs {
			s, err := client.GetSeries(sid)
			if err != nil {
				e.appendItem(job, Item{SeriesID: sid, SeriesName: sid, Status: "failed",
					Message: "获取系列失败: " + err.Error()})
				continue
			}
			series = append(series, *s)
		}
	} else {
		series, err = client.ListSeriesInLibrary(job.LibraryID)
		if err != nil {
			e.failJob(job, "获取库内系列失败: "+err.Error())
			return
		}
	}

	e.mu.Lock()
	job.Total = len(series)
	e.mu.Unlock()

	// 打标任务：不动简介/链接，逐个同步处理（文件夹标记 → 标签+标题后缀、出版社词、MACHINE）
	if opts.Kind == "tag" {
		for _, s := range series {
			if ctx.Err() != nil {
				break
			}
			tagger := &scrape.FolderTagger{Komga: client, Log: scrape.SlogLogger{L: runner.Log},
				Whitelist: runner.Whitelist, Blacklist: runner.Blacklist}
			var preview string
			tagger.Observe = func(ch scrape.Change) {
				preview = ch.Desc
				if ch.Detail != "" {
					preview += "：" + ch.Detail
				}
			}
			n := tagger.Tag([]komga.SeriesDto{s}, job.DryRun)
			item := Item{SeriesID: s.ID, SeriesName: s.Name}
			if n > 0 {
				item.Status = "ok"
				item.Message = preview
			} else {
				item.Status = "skipped"
				item.Message = "无标记可打（文件夹名无版本/出版社词，或标签已齐）"
			}
			e.appendItem(job, item)
		}
		e.finish(ctx, job)
		return
	}

	// v2 选项下发到 runner
	runner.ForceRefresh = opts.ForceRefresh
	runner.OverrideMatch = opts.OverrideMatch

	// worker pool
	concurrency := runner.Concurrency
	if concurrency < 1 {
		concurrency = 1
	}
	sem := make(chan struct{}, concurrency)
	var wg sync.WaitGroup

	for _, s := range series {
		select {
		case <-ctx.Done():
		case sem <- struct{}{}:
		}
		if ctx.Err() != nil {
			break
		}
		wg.Add(1)
		go func(series komga.SeriesDto) {
			defer wg.Done()
			defer func() { <-sem }()
			item := runner.ProcessSeries(ctx, &series)
			e.appendItem(job, item)
		}(s)
	}
	wg.Wait()

	e.finish(ctx, job)
}

func (e *Engine) finish(ctx context.Context, job *Job) {

	e.mu.Lock()
	if job.Status == "running" {
		now := time.Now()
		job.FinishedAt = &now
		if ctx.Err() != nil {
			job.Status = "canceled"
		} else {
			job.Status = "done"
		}
	}
	ids := make([]string, 0, len(job.Items))
	for _, it := range job.Items {
		if it.SeriesID != "" && it.SeriesID != it.Message {
			ids = append(ids, it.SeriesID)
		}
	}
	e.mu.Unlock()
	e.log.Info("任务 " + job.ID + " 结束：status=" + job.Status +
		" ok=" + itoa(job.OK) + " failed=" + itoa(job.Failed))
	if e.OnDone != nil {
		e.OnDone(job.ID, ids)
	}
}

func boolStr(b bool) string {
	if b {
		return "是"
	}
	return "否"
}

func (e *Engine) failJob(job *Job, msg string) {
	e.mu.Lock()
	defer e.mu.Unlock()
	now := time.Now()
	job.FinishedAt = &now
	job.Status = "done"
	job.Items = append(job.Items, Item{Status: "failed", Message: msg})
	e.log.Error("任务 " + job.ID + " 失败: " + msg)
}

// appendItem 追加条目并推进计数（调用方持有 e.mu 或自身 mu；内部统一走 e.mu）。
func (e *Engine) appendItem(job *Job, item Item) {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.appendItemLocked(job, item)
}

func (e *Engine) appendItemLocked(job *Job, item Item) {
	job.Items = append(job.Items, item)
	job.Done++
	switch item.Status {
	case "ok":
		job.OK++
	case "failed", "manual":
		job.Failed++
	}
}

// Get 任务详情（深拷贝避免竞态）。
func (e *Engine) Get(id string) (*Job, bool) {
	e.mu.Lock()
	defer e.mu.Unlock()
	j, ok := e.jobs[id]
	if !ok {
		return nil, false
	}
	cp := *j
	cp.Items = append([]Item(nil), j.Items...)
	cp.cancel = nil
	return &cp, true
}

// List 任务列表（按开始时间倒序）。
func (e *Engine) List() []*Job {
	e.mu.Lock()
	defer e.mu.Unlock()
	out := make([]*Job, 0, len(e.jobs))
	for _, j := range e.jobs {
		cp := *j
		cp.Items = nil
		cp.cancel = nil
		out = append(out, &cp)
	}
	sort.Slice(out, func(i, k int) bool { return out[i].StartedAt.After(out[k].StartedAt) })
	return out
}

// Cancel 取消任务。
func (e *Engine) Cancel(id string) bool {
	e.mu.Lock()
	defer e.mu.Unlock()
	j, ok := e.jobs[id]
	if !ok || j.Status != "running" {
		return false
	}
	j.Status = "canceled"
	j.cancel()
	return true
}

func itoa(n int) string {
	return strconv.Itoa(n)
}
