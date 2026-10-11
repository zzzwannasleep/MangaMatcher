package api

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	"komgabangumi/internal/auth"
	"komgabangumi/internal/bangumi"
	"komgabangumi/internal/komga"
	"komgabangumi/internal/scrape"
	"komgabangumi/internal/sources"
	"komgabangumi/internal/update"
)

// ============================================================
// v2 处理器：状态筛选 / 全库搜索 / 编辑器 / 纠正匹配 / 手动匹配收藏夹 /
// Mylar 导出 / BookWalker 索引管理 / 日志
// ============================================================

// ---------- 2. 刮削状态与筛选 ----------

// stateFilter 合法的 filter 值集合。
var stateFilters = map[string]bool{
	"all": true, "unscraped": true, "scraped": true,
	"no_cover": true, "no_summary": true, "incomplete": true, "complete": true,
}

// handleLibrarySeries GET /api/libraries/{id}/series?page&size&filter&q（v2 增加筛选/搜索/state）。
func (s *Server) handleLibrarySeries(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	libID := r.PathValue("id")
	if !s.canAccessLibrary(c, libID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	page := atoiDefault(r.URL.Query().Get("page"), 0)
	size := atoiDefault(r.URL.Query().Get("size"), 50)
	filter := r.URL.Query().Get("filter")
	if filter != "" && !stateFilters[filter] {
		writeError(w, 400, "无效的 filter："+filter)
		return
	}
	q := strings.TrimSpace(r.URL.Query().Get("q"))
	var data *komga.Page[komga.SeriesDto]
	// 缓存启用且已就绪 → 内存筛选+裁剪（零 Komga 请求）；否则直连 Komga（兼容旧路径/测试）
	if s.cacheEnabled() && s.cache.Ready() {
		all := s.cache.SeriesInLib(libID)
		if q != "" {
			var kept []komga.SeriesDto
			for _, series := range all {
				if matchSeriesQuery(series, q) {
					kept = append(kept, series)
				}
			}
			all = kept
		}
		if filter != "" && filter != "all" {
			var kept []komga.SeriesDto
			for _, series := range all {
				if s.stateMatches(series, filter) {
					kept = append(kept, series)
				}
			}
			all = kept
		}
		start, size0 := page*size, size
		if start > len(all) {
			start = len(all)
		}
		end := start + size0
		if end > len(all) {
			end = len(all)
		}
		data = &komga.Page[komga.SeriesDto]{
			Content: all[start:end], TotalElements: int64(len(all)),
			NumberOfElements: end - start,
		}
	} else {
		client := s.komgaClient()
		var err error
		data, err = client.ListSeriesPaged(libID, page, size)
		if err != nil {
			writeError(w, 502, "获取库内系列失败: "+err.Error())
			return
		}
		// filter/q 过滤（对当前页内容做内存筛选；status/状态判定不在此处发额外请求）
		if filter != "" || q != "" {
			var kept []komga.SeriesDto
			for _, series := range data.Content {
				if q != "" && !matchSeriesQuery(series, q) {
					continue
				}
				if filter != "" && filter != "all" && !s.stateMatches(series, filter) {
					continue
				}
				kept = append(kept, series)
			}
			data.Content = kept
			data.TotalElements = int64(len(kept))
			data.NumberOfElements = len(kept)
		}
	}
	if data.Content == nil {
		data.Content = []komga.SeriesDto{}
	}
	// state 注入（已有记录的系列带 hasCover/hasSummary；未记录的省略字段=前端显示 …）
	ids := make([]string, 0, len(data.Content))
	for _, series := range data.Content {
		ids = append(ids, series.ID)
	}
	states, _ := s.store.ListScrapeStates(ids)
	resp := struct {
		Content         []any  `json:"content"`
		TotalElements   int64  `json:"totalElements"`
		NumberOfElements int   `json:"numberOfElements,omitempty"`
	}{Content: []any{}, TotalElements: data.TotalElements, NumberOfElements: data.NumberOfElements}
	for _, series := range data.Content {
		item := map[string]any{"id": series.ID, "name": series.Name, "libraryId": series.LibraryID,
			"url": series.URL, "oneshot": series.Oneshot, "booksCount": series.BooksCount}
		if series.Metadata != nil {
			item["metadata"] = series.Metadata
		}
		if st, ok := states[series.ID]; ok {
			item["state"] = map[string]any{"hasCover": st.HasCover, "hasSummary": st.HasSummary}
		}
		resp.Content = append(resp.Content, item)
	}
	writeJSON(w, 200, resp)
}

// matchSeriesQuery 名称/原名 LIKE 匹配（大小写不敏感子串）。
func matchSeriesQuery(series komga.SeriesDto, q string) bool {
	lower := strings.ToLower(q)
	if strings.Contains(strings.ToLower(series.Name), lower) {
		return true
	}
	if series.Metadata != nil && strings.Contains(strings.ToLower(series.Metadata.Title), lower) {
		return true
	}
	return false
}

// stateMatches 状态判定：已有 state 记录用记录；没有的从列表 DTO 现算（零额外请求）。
func (s *Server) stateMatches(series komga.SeriesDto, filter string) bool {
	hasCover, hasSummary := false, false
	if st, err := s.store.GetScrapeState(series.ID); err == nil && st != nil {
		hasCover, hasSummary = st.HasCover, st.HasSummary
	} else if series.Metadata != nil {
		// 列表数据兜底：简介 = metadata.summary 非空；封面无从判定 → 不计（不算已刮）
		hasSummary = strings.TrimSpace(series.Metadata.Summary) != ""
	}
	hasLink := false
	if series.Metadata != nil {
		for _, l := range series.Metadata.Links {
			if scrape.IsSourceLink(l.URL) {
				hasLink = true
				break
			}
		}
	}
	switch filter {
	case "unscraped":
		return !hasLink
	case "scraped":
		return hasLink
	case "no_cover":
		return !hasCover
	case "no_summary":
		return !hasSummary
	case "incomplete":
		return !hasCover || !hasSummary
	case "complete":
		return hasCover && hasSummary
	}
	return true
}

// ---------- 3. 全库搜索 ----------

// handleSearch GET /api/search?q=&size=50（master 限授权库）。
func (s *Server) handleSearch(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	q := strings.TrimSpace(r.URL.Query().Get("q"))
	size := atoiDefault(r.URL.Query().Get("size"), 50)
	if q == "" {
		writeJSON(w, 200, []any{})
		return
	}
	type searchItem struct {
		ID          string               `json:"id"`
		Name        string               `json:"name"`
		LibraryID   string               `json:"libraryId"`
		LibraryName string               `json:"libraryName"`
		BooksCount  int                  `json:"booksCount"`
		Metadata    *komga.SeriesMetadataDto `json:"metadata,omitempty"`
		State       map[string]any       `json:"state,omitempty"`
	}
	var out []searchItem
	// 缓存启用且就绪 → 内存检索（零 Komga 请求）
	if s.cacheEnabled() && s.cache.Ready() {
		libs := s.cache.Libraries()
		libNames := map[string]string{}
		for _, l := range libs {
			libNames[l.ID] = l.Name
		}
		for _, lib := range libs {
			if c.Role != "admin" && !s.canAccessLibrary(c, lib.ID) {
				continue
			}
			for _, series := range s.cache.SeriesInLib(lib.ID) {
				if !matchSeriesQuery(series, q) {
					continue
				}
				out = append(out, searchItem{ID: series.ID, Name: series.Name, LibraryID: lib.ID,
					LibraryName: libNames[lib.ID], BooksCount: series.BooksCount, Metadata: series.Metadata})
				if len(out) >= size {
					break
				}
			}
			if len(out) >= size {
				break
			}
		}
	} else {
		client := s.komgaClient()
		libs, err := client.GetLibraries()
		if err != nil {
			writeError(w, 502, "获取库列表失败: "+err.Error())
			return
		}
		for _, lib := range libs {
			if c.Role != "admin" && !s.canAccessLibrary(c, lib.ID) {
				continue
			}
			data, err := client.ListSeriesPaged(lib.ID, 0, 2000)
			if err != nil {
				continue
			}
			for _, series := range data.Content {
				if !matchSeriesQuery(series, q) {
					continue
				}
				out = append(out, searchItem{ID: series.ID, Name: series.Name, LibraryID: lib.ID,
					LibraryName: lib.Name, BooksCount: series.BooksCount, Metadata: series.Metadata})
				if len(out) >= size {
					break
				}
			}
			if len(out) >= size {
				break
			}
		}
	}
	if out == nil {
		out = []searchItem{}
	}
	writeJSON(w, 200, out)
}

// ---------- 4. 系列详情与编辑器 ----------

// handleSeries GET /api/series/{id} → {series, books, state}。
func (s *Server) handleSeries(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	id := r.PathValue("id")
	client := s.komgaClient()
	// 缓存命中 → 系列详情零请求；书籍列表仍直连（详情页低频）
	var series *komga.SeriesDto
	if s.cacheEnabled() {
		if cs, ok := s.cache.GetSeries(id); ok {
			series = cs
		}
	}
	if series == nil {
		got, err := client.GetSeries(id)
		if err != nil {
			writeError(w, statusOf(err), "获取系列失败: "+err.Error())
			return
		}
		series = got
	}
	if !s.canAccessLibrary(c, series.LibraryID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	books, err := client.ListBooksInSeries(id)
	if err != nil {
		writeError(w, 502, "获取系列书籍失败: "+err.Error())
		return
	}
	if books == nil {
		books = []komga.BookDto{}
	}
	// {...series, books, state}
	m, _ := json.Marshal(series)
	var out map[string]any
	json.Unmarshal(m, &out)
	out["books"] = books
	if st, err := s.store.GetScrapeState(id); err == nil && st != nil {
		out["state"] = map[string]any{"hasCover": st.HasCover, "hasSummary": st.HasSummary}
	}
	writeJSON(w, 200, out)
}

// handleSeriesMetadataRaw PATCH /api/series/{id}/metadata-raw：任意 Komga SeriesMetadataUpdateDto 子集
// （前端只提交改动字段；后端自动补 MACHINE 标签；锁字段原样回传不会翻转——锁由编辑器 PATCH 构建器保证）。
func (s *Server) handleSeriesMetadataRaw(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	id := r.PathValue("id")
	client := s.komgaClient()
	series, err := client.GetSeries(id)
	if err != nil {
		writeError(w, statusOf(err), "获取系列失败: "+err.Error())
		return
	}
	if !s.canAccessLibrary(c, series.LibraryID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	body, err := readBody(r)
	if err != nil {
		writeError(w, 400, "请求体格式错误")
		return
	}
	var patch map[string]any
	if err := json.Unmarshal(body, &patch); err != nil || len(patch) == 0 {
		writeError(w, 400, "PATCH 体为空或格式错误")
		return
	}
	// MACHINE 自动补（tags 被提交且缺 MACHINE 时追加；没提交 tags 且现有 tags 缺 MACHINE 时补）
	ensureMachineInPatch(patch, series.Metadata)
	if err := client.PatchSeriesMetadataRaw(id, patch); err != nil {
		writeError(w, statusOf(err), "更新系列元数据失败: "+err.Error())
		return
	}
	s.cache.InvalidateSeries(id) // 缓存单系列回源（元数据已变）
	s.log.Info("编辑器更新系列元数据 " + series.Name + "（字段: " + strings.Join(patchKeys(patch), ",") + "）")
	writeJSON(w, 200, map[string]any{"ok": true})
}

// ensureMachineInPatch 编辑器保存 = 程序写入 → MACHINE 必打。
func ensureMachineInPatch(patch map[string]any, current *komga.SeriesMetadataDto) {
	machine := "MACHINE"
	if tagsAny, ok := patch["tags"]; ok {
		if list, ok := tagsAny.([]any); ok {
			has := false
			for _, t := range list {
				if s, ok := t.(string); ok && s == machine {
					has = true
					break
				}
			}
			if !has {
				patch["tags"] = append(list, machine)
			}
		}
		return
	}
	// 没提交 tags：现有 tags 缺 MACHINE → 补一条（幂等：已有则不动）
	has := false
	if current != nil {
		for _, t := range current.Tags {
			if t == machine {
				has = true
				break
			}
		}
	}
	if !has {
		tags := []string{}
		if current != nil {
			tags = append(tags, current.Tags...)
		}
		patch["tags"] = append(tags, machine)
	}
}

func patchKeys(patch map[string]any) []string {
	var keys []string
	for k := range patch {
		keys = append(keys, k)
	}
	return keys
}

// handleBookMetadataRaw PATCH /api/books/{id}/metadata-raw。
func (s *Server) handleBookMetadataRaw(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	body, err := readBody(r)
	if err != nil {
		writeError(w, 400, "请求体格式错误")
		return
	}
	var patch map[string]any
	if err := json.Unmarshal(body, &patch); err != nil || len(patch) == 0 {
		writeError(w, 400, "PATCH 体为空或格式错误")
		return
	}
	if err := s.komgaClient().PatchBookMetadataRaw(id, patch); err != nil {
		writeError(w, statusOf(err), "更新卷元数据失败: "+err.Error())
		return
	}
	s.log.Info("编辑器更新卷元数据 " + id + "（字段: " + strings.Join(patchKeys(patch), ",") + "）")
	writeJSON(w, 200, map[string]any{"ok": true})
}

// handleSeriesCover POST /api/series/{id}/cover（multipart image → selected=true 上传）。
func (s *Server) handleSeriesCover(w http.ResponseWriter, r *http.Request) {
	s.handleCoverCommon(w, r, true)
}

// handleBookCover POST /api/books/{id}/cover。
func (s *Server) handleBookCover(w http.ResponseWriter, r *http.Request) {
	s.handleCoverCommon(w, r, false)
}

func (s *Server) handleCoverCommon(w http.ResponseWriter, r *http.Request, isSeries bool) {
	c := auth.FromContext(r.Context())
	id := r.PathValue("id")
	client := s.komgaClient()
	if isSeries {
		series, err := client.GetSeries(id)
		if err != nil {
			writeError(w, statusOf(err), "获取系列失败: "+err.Error())
			return
		}
		if !s.canAccessLibrary(c, series.LibraryID) {
			writeError(w, 403, "没有该库的访问权限")
			return
		}
	}
	if err := r.ParseMultipartForm(32 << 20); err != nil {
		writeError(w, 400, "multipart 表单解析失败: "+err.Error())
		return
	}
	file, _, err := r.FormFile("image")
	if err != nil {
		file, _, err = r.FormFile("file")
		if err != nil {
			writeError(w, 400, "缺少 image 文件字段")
			return
		}
	}
	defer file.Close()
	blob, err := io.ReadAll(file)
	if err != nil {
		writeError(w, 400, "读取文件失败: "+err.Error())
		return
	}
	name := "cover_upload_" + id + ".jpg"
	if isSeries {
		err = client.UploadSeriesCover(id, blob, name)
	} else {
		err = client.UploadBookCover(id, blob, name)
	}
	if err != nil {
		writeError(w, statusOf(err), "上传封面失败: "+err.Error())
		return
	}
	// 缓存失效：封面磁盘缓存删除 + 系列数据回源（booksCount/updatedAt 等可能变化）
	if isSeries {
		s.dropCover("series", id)
		s.cache.InvalidateSeries(id)
	} else {
		s.dropCover("book", id)
	}
	writeJSON(w, 200, map[string]any{"ok": true})
}

// handleSeriesRescrapeVolumes POST /api/series/{id}/rescrapevolumes {dryRun} → {ok, changed:[...]}。
// 重刮每卷（按卷号对齐；遵循 Dry-run）——同步执行小任务。
func (s *Server) handleSeriesRescrapeVolumes(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	id := r.PathValue("id")
	client := s.komgaClient()
	series, err := client.GetSeries(id)
	if err != nil {
		writeError(w, statusOf(err), "获取系列失败: "+err.Error())
		return
	}
	if !s.canAccessLibrary(c, series.LibraryID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	dryRun := s.LoadSettings().DryRunDefault
	if r.Body != nil {
		var raw map[string]any
		if err := json.NewDecoder(r.Body).Decode(&raw); err == nil {
			if v, ok := raw["dryRun"].(bool); ok {
				dryRun = v
			}
		}
	}
	// 已有 Btv 链接 → 重刮（沿用既有 subject id）；没有 → 报错
	_, subjectID := hasSourceLinkOf(series)
	if subjectID <= 0 {
		writeError(w, 400, "该系列尚未刮削（无 Btv 链接），请先执行刮削或纠正匹配")
		return
	}
	svc := s.newRunner(dryRun).NewService()
	res := svc.ScrapeSeriesOpts(series, subjectID, false)
	if !res.Success {
		writeError(w, 500, "重刮每卷失败: "+res.Error)
		return
	}
	var changed []map[string]any
	for _, ch := range res.Changes {
		changed = append(changed, map[string]any{
			"kind": ch.Kind, "target": ch.Target, "description": ch.Desc, "detail": ch.Detail,
		})
	}
	if changed == nil {
		changed = []map[string]any{}
	}
	writeJSON(w, 200, map[string]any{"ok": true, "changed": changed})
}

// hasSourceLinkOf 系列已刮判定（供 rescrape 等非 runner 场景）。
func hasSourceLinkOf(series *komga.SeriesDto) (string, int) {
	if series.Metadata == nil {
		return "", 0
	}
	for _, l := range series.Metadata.Links {
		if scrape.IsBtvLink(l.URL) {
			return "Btv", btvIDOf(l.URL)
		}
	}
	return "", 0
}

func btvIDOf(u string) int {
	m := btvIDRe.FindStringSubmatch(u)
	if m == nil {
		return 0
	}
	n := 0
	for _, ch := range m[1] {
		n = n*10 + int(ch-'0')
	}
	return n
}

var btvIDRe = regexp.MustCompile(`/subject/(\d+)`)

// ---------- 5. 纠正匹配 / 手动匹配 ----------

// candidateWire 候选的前端 wire 格式（统一 id 字段；sources.Candidate 无 json tag 是内部结构）。
type candidateWire struct {
	ID       string `json:"id"`
	Title    string `json:"title"`
	OriTitle string `json:"oriTitle"`
	Author   string `json:"author"`
	Aliases  string `json:"aliases"`
	CoverURL string `json:"coverUrl"`
	Source   string `json:"source"`
}

func candidateToWire(c sources.Candidate) candidateWire {
	id := ""
	switch c.Source {
	case "Bgm":
		id = fmt.Sprint(c.BgmID)
	case "Bw":
		id = c.BwID
	case "Mox":
		id = c.MoxID
	case "MangaDex":
		id = c.MdID
	case "Baike":
		id = c.BaikeID
	}
	return candidateWire{ID: id, Title: c.Title, OriTitle: c.OriTitle,
		Author: c.Author, Aliases: c.Aliases, CoverURL: c.CoverURL, Source: c.Source}
}

// handleMatchCandidates GET /api/series/{id}/match/candidates?q= → 按源分组候选。
func (s *Server) handleMatchCandidates(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	id := r.PathValue("id")
	client := s.komgaClient()
	series, err := client.GetSeries(id)
	if err != nil {
		writeError(w, statusOf(err), "获取系列失败: "+err.Error())
		return
	}
	if !s.canAccessLibrary(c, series.LibraryID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	q := strings.TrimSpace(r.URL.Query().Get("q"))
	if q == "" {
		seriesName := series.Name
		if series.Metadata != nil && series.Metadata.Title != "" {
			seriesName = series.Metadata.Title
		}
		q = seriesName
	}
	st := s.LoadSettings()
	sc, err := s.buildSources(st)
	if err != nil {
		writeError(w, 500, "构建五源客户端失败: "+err.Error())
		return
	}
	ctx := r.Context()
	groups := map[string]any{}
	toWireList := func(list []sources.Candidate) []candidateWire {
		out := make([]candidateWire, 0, len(list))
		for _, c := range list {
			out = append(out, candidateToWire(c))
		}
		return out
	}
	// Bangumi
	if bgm := s.bangumiClient(); bgm != nil {
		var list []sources.Candidate
		for _, term := range []string{q} {
			r2, err := bgm.SearchSubjects(term, st.MatchType, 8)
			if err != nil {
				continue
			}
			for _, cand := range r2 {
				list = append(list, sources.Candidate{BgmID: cand.ID, Title: cand.Title,
					OriTitle: cand.OriTitle, Author: cand.Author, Aliases: cand.Aliases,
					CoverURL: cand.CoverURL, Source: "Bgm"})
			}
		}
		if len(list) > 0 {
			groups["bangumi"] = toWireList(list)
		}
	}
	// BookWalker
	if sc.BW != nil {
		if hits := sc.BW.SearchFuzzy(q, 8); len(hits) > 0 {
			var list []sources.Candidate
			for _, bw := range hits {
				list = append(list, sc.BW.ToCandidate(bw))
			}
			groups["bookwalker"] = toWireList(list)
		}
	}
	// Mox
	if sc.Mox != nil {
		if hits, err := sc.Mox.Search(ctx, q, 8); err == nil && len(hits) > 0 {
			var list []sources.Candidate
			for _, m := range hits {
				list = append(list, sources.Candidate{Title: m.Title, Author: m.Author,
					CoverURL: m.CoverURL, Source: "Mox", MoxID: m.ID})
			}
			groups["mox"] = toWireList(list)
		}
	}
	// MangaDex
	if sc.MangaDex != nil {
		if hits, err := sc.MangaDex.Search(ctx, q, 8); err == nil && len(hits) > 0 {
			var list []sources.Candidate
			for _, m := range hits {
				list = append(list, sources.Candidate{Title: m.Title, OriTitle: m.OriTitle,
					CoverURL: m.CoverURL, Source: "MangaDex", MdID: m.ID})
			}
			groups["mangadex"] = toWireList(list)
		}
	}
	// 百科（单词条）
	if sc.Baike != nil {
		if card, err := sc.Baike.SearchCard(ctx, q); err == nil && card != nil {
			groups["baike"] = toWireList([]sources.Candidate{{
				Title: card.Title, OriTitle: card.Field("原版名称"),
				Author: card.Field("作者"), Source: "Baike", BaikeID: fmt.Sprint(card.LemmaID)}})
		}
	}
	writeJSON(w, 200, map[string]any{"groups": groups})
}

// matchDetail 来源详情（预览区共用 wire 结构）。
type matchDetail struct {
	Source   string `json:"source"`
	ID       string `json:"id"`
	Title    string `json:"title"`
	OriTitle string `json:"oriTitle"`
	Author   string `json:"author"`
	Aliases  string `json:"aliases"`
	Summary  string `json:"summary"`
	CoverURL string `json:"coverUrl"`
}

// fetchMatchDetail 按来源拉取详情（handleMatchDetail / handleMatchResolve 共用）。
// 返回 (详情, HTTP 状态码, 错误)。
func (s *Server) fetchMatchDetail(source, id string) (*matchDetail, int, error) {
	ctx := context.Background()
	st := s.LoadSettings()
	sc, err := s.buildSources(st)
	if err != nil {
		return nil, 500, fmt.Errorf("构建五源客户端失败: %w", err)
	}
	d := &matchDetail{Source: source, ID: id}
	switch source {
	case "bangumi":
		bgm := s.bangumiClient()
		var sid int
		fmt.Sscanf(id, "%d", &sid)
		subject, err := bgm.GetSubject(sid)
		if err != nil {
			return nil, 502, fmt.Errorf("获取 Bangumi 详情失败: %w", err)
		}
		d.Title, d.OriTitle = subject.NameCN, subject.Name
		if d.Title == "" {
			d.Title = subject.Name
		}
		author := bangumi.ParseInfobox(subject.Infobox, "作画")
		if author == "" {
			author = bangumi.ParseInfobox(subject.Infobox, "作者")
		}
		if author != "" {
			d.Author = bangumi.FirstAuthor(author)
		}
		d.Aliases = subject.Aliases()
		d.Summary = subject.Summary
		if subject.Image != "" {
			d.CoverURL = subject.Image
		} else if subject.Images != nil {
			d.CoverURL = subject.Images.Medium
		}
	case "bookwalker":
		if sc.BW == nil {
			return nil, 400, fmt.Errorf("BookWalker 索引未构建")
		}
		bw := sc.BW.FindByID(id)
		if bw == nil {
			return nil, 404, fmt.Errorf("BookWalker 条目不存在")
		}
		d.Title, d.OriTitle, d.Summary = bw.Title, bw.Title, sources.StripHtml(bw.Description)
		for _, a := range bw.Authors {
			if d.Author != "" {
				break
			}
			d.Author = a.Name
		}
		d.Aliases = strings.Join(bw.AltTitles, " / ")
		if len(bw.CoverURLs) > 0 {
			d.CoverURL = bw.CoverURLs[0]
		}
	case "mox":
		if sc.Mox == nil {
			return nil, 400, fmt.Errorf("Mox 客户端未配置")
		}
		mox, err := sc.Mox.GetSeries(ctx, id)
		if err != nil {
			return nil, 502, fmt.Errorf("获取 Mox 详情失败: %w", err)
		}
		d.Title, d.OriTitle, d.Summary, d.CoverURL = mox.Title, mox.OriTitle, mox.Summary, ""
		if len(mox.Authors) > 0 {
			d.Author = mox.Authors[0]
		}
		if mox.EngName != "" {
			d.Aliases = mox.EngName
		}
	case "mangadex":
		if sc.MangaDex == nil {
			return nil, 400, fmt.Errorf("MangaDex 未启用")
		}
		md, err := sc.MangaDex.Get(ctx, id)
		if err != nil {
			return nil, 502, fmt.Errorf("获取 MangaDex 详情失败: %w", err)
		}
		if md == nil {
			return nil, 404, fmt.Errorf("MangaDex 条目不存在")
		}
		d.Title, d.OriTitle, d.Summary, d.CoverURL = md.Title, md.OriTitle, md.Description, md.CoverURL
		d.Aliases = strings.Join(md.AltTitles, " / ")
	case "baike":
		if sc.Baike == nil {
			return nil, 400, fmt.Errorf("百度百科未启用")
		}
		card, err := sc.Baike.SearchCard(ctx, id)
		if err != nil {
			return nil, 502, fmt.Errorf("获取百度百科词条失败: %w", err)
		}
		if card == nil {
			return nil, 404, fmt.Errorf("百度百科词条不存在")
		}
		d.Title, d.Summary, d.CoverURL = card.Title, card.Abstract, card.ImageURL
		d.OriTitle = card.Field("原版名称")
		d.Author = card.Field("作者")
		if d.Author == "" {
			d.Author = card.Field("作画")
		}
	default:
		return nil, 400, fmt.Errorf("未知来源: %s", source)
	}
	return d, 200, nil
}

// handleMatchDetail GET /api/match/detail?source=&id= → 详情区完整简介。
func (s *Server) handleMatchDetail(w http.ResponseWriter, r *http.Request) {
	d, code, err := s.fetchMatchDetail(r.URL.Query().Get("source"), r.URL.Query().Get("id"))
	if err != nil {
		writeError(w, code, err.Error())
		return
	}
	writeJSON(w, 200, d)
}

// baikeItemNameRe 百度百科 /item/ 词条名（可带 /<lemmaID> 尾段）。
var baikeItemNameRe = regexp.MustCompile(`baike\.baidu\.com/item/([^/?#]+)`)

// handleMatchResolve POST /api/match/resolve {url} → 识别链接来源并返回预览详情。
// 支持：Bangumi 链接（bgm.tv/bangumi.tv/subject/N）、纯 Bangumi ID、百度百科词条链接。
func (s *Server) handleMatchResolve(w http.ResponseWriter, r *http.Request) {
	var body struct {
		URL string `json:"url"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeError(w, 400, "请求体格式错误")
		return
	}
	raw := strings.TrimSpace(body.URL)
	if raw == "" {
		writeError(w, 400, "缺少 url 字段")
		return
	}
	var source, id string
	switch {
	case isDigits(raw):
		source, id = "bangumi", raw
	case btvIDRe.MatchString(raw):
		source, id = "bangumi", extractBgmID(raw)
	case baikeItemNameRe.MatchString(raw):
		m := baikeItemNameRe.FindStringSubmatch(raw)
		name, err := url.PathUnescape(m[1])
		if err != nil || strings.TrimSpace(name) == "" {
			writeError(w, 400, "无法从百度百科链接解析词条名: "+m[1])
			return
		}
		// 词条 id 即词条标题（与候选/应用链路一致：baike 源以词条名为键）
		source, id = "baike", strings.TrimSpace(name)
	default:
		writeError(w, 400, "无法识别的链接：支持 Bangumi 条目链接/ID 或百度百科词条链接")
		return
	}
	d, code, err := s.fetchMatchDetail(source, id)
	if err != nil {
		writeError(w, code, err.Error())
		return
	}
	s.log.Info("匹配预览解析：" + raw + " → " + source + " " + id)
	writeJSON(w, 200, d)
}

// handleMatchApply POST /api/series/{id}/match/apply → ReplaceMatch 语义（返回 jobId）。
func (s *Server) handleMatchApply(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	id := r.PathValue("id")
	client := s.komgaClient()
	series, err := client.GetSeries(id)
	if err != nil {
		writeError(w, statusOf(err), "获取系列失败: "+err.Error())
		return
	}
	if !s.canAccessLibrary(c, series.LibraryID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	var body struct {
		Source string `json:"source"`
		ID     string `json:"id"`
		Manual string `json:"manual"`
		DryRun *bool  `json:"dryRun"` // 指针：缺省才用 dryRunDefault；显式 false 必须执行
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeError(w, 400, "请求体格式错误")
		return
	}
	// 手动输入 BGM 链接/ID → 解析出 Bangumi ID 直接应用
	if body.Manual != "" && body.Source == "" {
		body.Source = "bangumi"
		body.ID = extractBgmID(body.Manual)
		if body.ID == "" {
			writeError(w, 400, "无法从手动输入解析 Bangumi ID："+body.Manual)
			return
		}
	}
	if body.Source == "" {
		writeError(w, 400, "缺少 source 或 manual 字段")
		return
	}
	st := s.LoadSettings()
	sc, err := s.buildSources(st)
	if err != nil {
		writeError(w, 500, "构建五源客户端失败: "+err.Error())
		return
	}
	// 单系列任务：后台执行，前端跳任务详情。dryRun 缺省才用 dryRunDefault，显式值必须尊重。
	dryRun := st.DryRunDefault
	if body.DryRun != nil {
		dryRun = *body.DryRun
	}
	jobID := s.jobs.SubmitApply(series, body.Source, body.ID, dryRun, func(dryRun bool) *scrape.Runner {
		return s.newRunner(dryRun)
	}, sc)
	s.log.Info("纠正匹配任务 " + jobID + "：《" + series.Name + "》 ← " + body.Source + " " + body.ID +
		"（dry-run=" + boolCn(dryRun) + "）")
	writeJSON(w, 200, map[string]string{"jobId": jobID})
}

// extractBgmID 从链接或纯 ID 提取 Bangumi subject id。
func extractBgmID(s string) string {
	s = strings.TrimSpace(s)
	if m := btvIDRe.FindStringSubmatch(s); m != nil {
		return m[1]
	}
	if isDigits(s) {
		return s
	}
	return ""
}

func isDigits(s string) bool {
	if s == "" {
		return false
	}
	for _, c := range s {
		if c < '0' || c > '9' {
			return false
		}
	}
	return true
}

// handleManualCollection GET /api/manual-collection → 「待手动匹配」收藏夹内容。
func (s *Server) handleManualCollection(w http.ResponseWriter, r *http.Request) {
	client := s.komgaClient()
	const name = "待手动匹配"
	collections, err := client.GetCollections()
	if err != nil {
		writeError(w, 502, "获取收藏夹失败: "+err.Error())
		return
	}
	var col *komga.CollectionDto
	for i := range collections {
		if collections[i].Name == name {
			col = &collections[i]
			break
		}
	}
	if col == nil || len(col.SeriesIds) == 0 {
		writeJSON(w, 200, map[string]any{"name": name, "series": []any{}})
		return
	}
	type seriesItem struct {
		ID     string `json:"id"`
		Name   string `json:"name"`
		LibraryID string `json:"libraryId"`
	}
	var list []seriesItem
	for _, sid := range col.SeriesIds {
		series, err := client.GetSeries(sid)
		if err != nil {
			continue
		}
		name2 := series.Name
		if series.Metadata != nil && series.Metadata.Title != "" {
			name2 = series.Metadata.Title
		}
		list = append(list, seriesItem{ID: series.ID, Name: name2, LibraryID: series.LibraryID})
	}
	if list == nil {
		list = []seriesItem{}
	}
	writeJSON(w, 200, map[string]any{"name": name, "series": list})
}

// ---------- 8. Mylar 导出 ----------

// handleMylarZip GET /api/libraries/{id}/mylar?withCovers=1 → application/zip 下载。
func (s *Server) handleMylarZip(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	libID := r.PathValue("id")
	if !s.canAccessLibrary(c, libID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	withCovers := atoiDefault(r.URL.Query().Get("withCovers"), 0) == 1
	exp := &scrape.MylarExporter{Komga: s.komgaClient(), Log: scrape.SlogLogger{L: s.log}}
	client := s.komgaClient()
	libRoot := s.libraryRoot(client, libID)
	seriesList, err := client.ListSeriesInLibrary(libID)
	if err != nil {
		writeError(w, 502, "获取库内系列失败: "+err.Error())
		return
	}
	entries, written := exp.BuildEntries(seriesList, libRoot, withCovers)
	if written == 0 {
		writeError(w, 400, "没有可导出的系列（书籍数异常或缺少目录信息）")
		return
	}
	zipData, err := scrape.BuildZip(entries)
	if err != nil {
		writeError(w, 500, "打包 ZIP 失败: "+err.Error())
		return
	}
	s.log.Info("Mylar ZIP 导出完成：库 " + libID + "，共 " + itoa(written) + " 个 series.json")
	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Disposition", "attachment; filename=mylar-"+libID+".zip")
	w.WriteHeader(200)
	w.Write(zipData)
}

// handleMylarExport POST /api/libraries/{id}/mylar/export {withCovers} → 直写 comicsDir。
func (s *Server) handleMylarExport(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	libID := r.PathValue("id")
	if !s.canAccessLibrary(c, libID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	var body struct {
		WithCovers bool `json:"withCovers"`
	}
	if r.Body != nil {
		var raw map[string]any
		if err := json.NewDecoder(r.Body).Decode(&raw); err == nil {
			if v, ok := raw["withCovers"].(bool); ok {
				body.WithCovers = v
			}
		}
	}
	st := s.LoadSettings()
	if strings.TrimSpace(st.ComicsDir) == "" {
		writeError(w, 400, "未配置 comicsDir（容器内路径）——设置里填好后再用直写导出")
		return
	}
	client := s.komgaClient()
	libRoot := s.libraryRoot(client, libID)
	seriesList, err := client.ListSeriesInLibrary(libID)
	if err != nil {
		writeError(w, 502, "获取库内系列失败: "+err.Error())
		return
	}
	exp := &scrape.MylarExporter{Komga: client, Log: scrape.SlogLogger{L: s.log}}
	entries, written := exp.BuildEntries(seriesList, libRoot, body.WithCovers)
	if written == 0 {
		writeError(w, 400, "没有可导出的系列（书籍数异常或缺少目录信息）")
		return
	}
	n, err := scrape.WriteDirect(entries, st.ComicsDir)
	if err != nil {
		writeError(w, 500, "直写 comicsDir 失败: "+err.Error())
		return
	}
	s.log.Info("Mylar 直写完成：库 " + libID + " → " + st.ComicsDir + "，共 " + itoa(n) + " 个文件")
	writeJSON(w, 200, map[string]any{"written": n})
}

// libraryRoot 查库根目录（列表里匹配 root；拿不到返回空串）。
func (s *Server) libraryRoot(client *komga.Client, libID string) string {
	libs, err := client.GetLibraries()
	if err != nil {
		return ""
	}
	for _, l := range libs {
		if l.ID == libID {
			return l.Root
		}
	}
	return ""
}

// ---------- 1.x BookWalker 索引管理 ----------

// bwJob BW 后台索引构建状态。
type bwJob struct {
	mu        sync.Mutex
	building  bool
	lastError string
}

var bwState = &bwJob{}

// handleBWDownload POST /api/settings/bw/download → 202 后台下载 → 解压 → 建索引。
func (s *Server) handleBWDownload(w http.ResponseWriter, r *http.Request) {
	bwState.mu.Lock()
	if bwState.building {
		bwState.mu.Unlock()
		writeJSON(w, 202, map[string]any{"ok": true, "message": "已有索引构建在进行中"})
		return
	}
	bwState.building = true
	bwState.lastError = ""
	bwState.mu.Unlock()

	st := s.LoadSettings()
	url := st.BookWalker.DownloadURL
	if url == "" {
		url = sources.DefaultBWDownloadURL
	}
	dataDir := s.dataDir
	log := s.log
	svc := sources.NewBookWalkerService(scrape.SlogLogger{L: log})
	go func() {
		defer func() {
			bwState.mu.Lock()
			bwState.building = false
			bwState.mu.Unlock()
		}()
		ctx, cancel := contextWithTimeout(30 * time.Minute)
		defer cancel()
		_, err := svc.BuildIndex(ctx, url,
			filepath.Join(dataDir, "bkwk-db.sqlite"),
			filepath.Join(dataDir, "bkwk-index.json.gz"),
			sources.ProxyConfig{Protocol: st.Proxy.Protocol, Host: st.Proxy.Host, Port: st.Proxy.Port,
				Username: st.Proxy.Username, Password: st.Proxy.Password},
			func(step, total int, msg string) { log.Info("[BookWalker] " + msg) })
		if err != nil {
			bwState.mu.Lock()
			bwState.lastError = err.Error()
			bwState.mu.Unlock()
			log.Error("BookWalker 索引构建失败: " + err.Error())
		}
	}()
	writeJSON(w, 202, map[string]any{"ok": true})
}

// handleBWImport POST /api/settings/bw/import（multipart file）→ 建索引（同步）。
func (s *Server) handleBWImport(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseMultipartForm(64 << 20); err != nil {
		writeError(w, 400, "multipart 表单解析失败: "+err.Error())
		return
	}
	file, header, err := r.FormFile("file")
	if err != nil {
		writeError(w, 400, "缺少 file 文件字段")
		return
	}
	defer file.Close()
	dataDir := s.dataDir
	tmpPath := filepath.Join(dataDir, "bw-import-"+header.Filename)
	out, err := os.Create(tmpPath)
	if err != nil {
		writeError(w, 500, "落盘临时文件失败: "+err.Error())
		return
	}
	_, copyErr := io.Copy(out, file)
	out.Close()
	if copyErr != nil {
		writeError(w, 500, "写入临时文件失败: "+copyErr.Error())
		return
	}
	svc := sources.NewBookWalkerService(scrape.SlogLogger{L: s.log})
	dbPath := filepath.Join(dataDir, "bkwk-db.sqlite")
	indexPath := filepath.Join(dataDir, "bkwk-index.json.gz")
	if _, err := svc.ImportLocal(tmpPath, dbPath, indexPath, func(step, total int, msg string) {
		s.log.Info("[BookWalker] " + msg)
	}); err != nil {
		os.Remove(tmpPath)
		writeError(w, 400, err.Error())
		return
	}
	os.Remove(tmpPath)
	idx, err := sources.LoadBookWalkerIndex(indexPath)
	if err != nil {
		writeError(w, 500, "索引加载失败: "+err.Error())
		return
	}
	s.log.Info("BookWalker 索引导入完成：系列 " + itoa(idx.Count()) + "，卷 " + itoa(idx.VolumeTotal()))
	writeJSON(w, 200, map[string]any{"ok": true,
		"seriesCount": idx.Count(), "volumeCount": idx.VolumeTotal()})
}

// handleBWStatus GET /api/settings/bw/status。
func (s *Server) handleBWStatus(w http.ResponseWriter, r *http.Request) {
	bwState.mu.Lock()
	building, lastError := bwState.building, bwState.lastError
	bwState.mu.Unlock()
	out := map[string]any{"indexed": false, "building": building,
		"seriesCount": 0, "volumeCount": 0, "formatVersion": 2, "lastError": lastError}
	if idxPath := s.bwIndexPath(); idxPath != "" {
		if idx, err := sources.LoadBookWalkerIndex(idxPath); err == nil {
			out["indexed"] = true
			out["seriesCount"] = idx.Count()
			out["volumeCount"] = idx.VolumeTotal()
			out["formatVersion"] = idx.FormatVersion()
		}
	}
	writeJSON(w, 200, out)
}

// ---------- 9. 日志 ----------

// handleLogs GET /api/logs?lines=500 → text/plain 最近日志（admin only）。
func (s *Server) handleLogs(w http.ResponseWriter, r *http.Request) {
	lines := atoiDefault(r.URL.Query().Get("lines"), 500)
	path := s.todayLogPath()
	data, err := os.ReadFile(path)
	if err != nil {
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		w.WriteHeader(200)
		w.Write([]byte(""))
		return
	}
	// 取最近 N 行
	all := strings.Split(strings.TrimRight(string(data), "\n"), "\n")
	if len(all) > lines {
		all = all[len(all)-lines:]
	}
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.WriteHeader(200)
	w.Write([]byte(strings.Join(all, "\n")))
}

// todayLogPath 今日日志文件（DATA_DIR/logs/log-YYYYMMDD.log）。
func (s *Server) todayLogPath() string {
	return filepath.Join(s.dataDir, "logs", "log-"+time.Now().Format("20060102")+".log")
}

// contextWithTimeout 包装（30 分钟上限）。
func contextWithTimeout(d time.Duration) (context.Context, context.CancelFunc) {
	return context.WithTimeout(context.Background(), d)
}

// ---------- 10. 本地库缓存 ----------

// handleCacheStatus GET /api/cache/status → 缓存概况（设置页「本地缓存」卡片）。
func (s *Server) handleCacheStatus(w http.ResponseWriter, r *http.Request) {
	st := s.LoadSettings()
	last := s.cache.LastSyncAt()
	writeJSON(w, 200, map[string]any{
		"enabled":       st.Cache.Enabled,
		"refreshMinutes": st.Cache.RefreshMinutes,
		"ready":         s.cache.Ready(),
		"refreshing":    s.cache.IsRefreshing(),
		"seriesCount":   s.cache.SeriesCount(),
		"libraryCount":  s.cache.LibraryCount(),
		"lastSync":      last,
		"ageMinutes":    int(time.Since(last).Minutes()),
	})
}

// handleCacheRefresh POST /api/cache/refresh → 立即全量对齐（异步执行，立即返回状态）。
func (s *Server) handleCacheRefresh(w http.ResponseWriter, r *http.Request) {
	if s.cache.IsRefreshing() {
		writeJSON(w, 200, map[string]any{"ok": true, "already": true})
		return
	}
	go func() {
		if err := s.cache.RefreshAll(); err != nil {
			s.log.Warn("手动刷新库缓存失败: " + err.Error())
		}
	}()
	writeJSON(w, 200, map[string]any{"ok": true})
}

// ---------- 11. 自动更新检查 ----------

// handleUpdateStatus GET /api/update/status → 最近一次检查结果（无网络调用）。
func (s *Server) handleUpdateStatus(w http.ResponseWriter, r *http.Request) {
	st := s.LoadSettings()
	last := s.updater.Last()
	out := map[string]any{
		"current":   update.Current(),
		"autoCheck": st.Update.AutoCheck,
		"checking":  s.updater.Checking(),
	}
	if last != nil {
		out["latest"] = last.Latest
		out["hasUpdate"] = last.HasUpdate
		out["releaseUrl"] = last.ReleaseURL
		out["checkedAt"] = last.CheckedAt
		out["error"] = last.Error
	}
	writeJSON(w, 200, out)
}

// handleUpdateCheck POST /api/update/check → 立即检查（同步，最多 ~20s）。
func (s *Server) handleUpdateCheck(w http.ResponseWriter, r *http.Request) {
	st, err := s.updater.Check()
	if err != nil || st == nil {
		if st == nil {
			writeError(w, 502, "检查更新失败")
			return
		}
		writeJSON(w, 200, st) // 带错误信息的快照也返回（前端展示失败原因）
		return
	}
	writeJSON(w, 200, st)
}

// ---------- 源客户端构建（候选/详情端点共用） ----------

// buildSources 按设置构建五源客户端（不含 BW 索引加载失败时的降级：返回 nil 索引）。
func (s *Server) buildSources(st Settings) (scrape.SourceClients, error) {
	sc := scrape.SourceClients{}
	pc := sources.ProxyConfig{Protocol: st.Proxy.Protocol, Host: st.Proxy.Host, Port: st.Proxy.Port,
		Username: st.Proxy.Username, Password: st.Proxy.Password}
	if st.AI.Enabled && strings.TrimSpace(st.AI.BaseURL) != "" {
		ai, err := sources.NewAiClient(st.AI.BaseURL, st.AI.APIKey, st.AI.Model, pc, 60*time.Second)
		if err != nil {
			return sc, err
		}
		sc.AI = ai
	}
	if idxPath := s.bwIndexPath(); idxPath != "" {
		if idx, err := sources.LoadBookWalkerIndex(idxPath); err == nil {
			sc.BW = idx
		}
	}
	if mox, err := sources.NewMoxClient(pc, moxBaseOverride, scrape.SlogLogger{L: s.log}, moxPaceOverrideMs); err == nil {
		sc.Mox = mox
	}
	if st.MangaDexEnabled {
		if md, err := sources.NewMangaDexClient(pc, mangaDexBaseOverride, true); err == nil {
			sc.MangaDex = md
		}
	}
	if st.BaikeEnabled {
		if bk, err := sources.NewBaikeClient(baikeBaseOverride); err == nil {
			sc.Baike = bk
		}
	}
	return sc, nil
}

var _ = fmt.Sprint

// itoa 数字转字符串（api 包辅助）。
func itoa(n int) string { return strconv.Itoa(n) }
