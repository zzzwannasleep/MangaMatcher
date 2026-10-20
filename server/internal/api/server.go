// Package api：REST API 路由与处理器（net/http 1.22+ method 路由）+ SPA fallback。
package api

import (
	"encoding/json"
	"io/fs"
	"log/slog"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"strings"
	"time"

	"komgabangumi/internal/auth"
	"komgabangumi/internal/bangumi"
	"komgabangumi/internal/jobs"
	"komgabangumi/internal/komga"
	"komgabangumi/internal/komgacache"
	"komgabangumi/internal/scrape"
	"komgabangumi/internal/sources"
	"komgabangumi/internal/store"
	"komgabangumi/internal/update"
	"komgabangumi/internal/webdist"
)

// Version 后端版本号。
const Version = "2.0.0"

// Server API 服务器。
type Server struct {
	store     *store.Store
	auth      *auth.Manager
	jobs      *jobs.Engine
	log       *slog.Logger
	komgaBase string // 测试注入：绕过设置的 Komga 客户端工厂（为空时按设置构建）
	dataDir   string // BW 索引 / 日志 / 直写目录的基准
	cache     *komgacache.Cache
	updater   *update.Checker
}

// NewServer 创建 API 服务器（内部创建任务引擎，Runner 由最新设置构建）。
func NewServer(st *store.Store, am *auth.Manager, log *slog.Logger) *Server {
	s := &Server{store: st, auth: am, log: log, dataDir: "./data"}
	s.jobs = jobs.NewEngine(s.komgaClient, log, s.newRunner)
	s.cache = komgacache.New(st.DB(), s.komgaFactory, log)
	s.jobs.OnDone = s.onJobDone
	s.updater = update.NewChecker("", nil)
	return s
}

// Start 启动库缓存自动刷新 + 更新检查循环（main.go 在 SetDataDir 后调用；集成测试不调用）。
func (s *Server) Start() {
	s.cache.Start(s.cacheInterval)
	go s.updateLoop()
}

// updateLoop 更新检查：启动 10s 后首查，之后每 24h 一次（设置 update.autoCheck 热更新）。
// 检查走设置里配置的统一代理（GitHub 直连不通的环境靠它）。
func (s *Server) updateLoop() {
	first := time.NewTimer(10 * time.Second)
	defer first.Stop()
	for range first.C {
		s.checkUpdateSoon()
		// 之后 24h 循环
		ticker := time.NewTicker(24 * time.Hour)
		defer ticker.Stop()
		for range ticker.C {
			s.checkUpdateSoon()
		}
	}
}

// checkUpdateSoon 按设置构建代理 HTTP 客户端后检查一次。
func (s *Server) checkUpdateSoon() {
	st := s.LoadSettings()
	if !st.Update.AutoCheck {
		return
	}
	pc := sources.ProxyConfig{Protocol: st.Proxy.Protocol, Host: st.Proxy.Host, Port: st.Proxy.Port,
		Username: st.Proxy.Username, Password: st.Proxy.Password}
	var hc *http.Client
	if pc.IsSet() {
		if c, err := sources.NewHTTPClient(pc, 20*time.Second, "KomgaBangumi-Docker", ""); err == nil {
			hc = c
		}
	}
	s.updater.SetClient(hc)
	_, err := s.updater.Check()
	if err != nil {
		s.log.Warn("更新检查失败: " + err.Error())
	}
}

// cacheInterval 缓存自动刷新间隔（设置热更新；<=0 = 关闭自动刷新）。
func (s *Server) cacheInterval() time.Duration {
	st := s.LoadSettings()
	if !st.Cache.Enabled {
		return 0
	}
	if st.Cache.RefreshMinutes <= 0 {
		return 0
	}
	return time.Duration(st.Cache.RefreshMinutes) * time.Minute
}

// cacheEnabled 缓存开关（读路径门控）。
func (s *Server) cacheEnabled() bool {
	return s.LoadSettings().Cache.Enabled
}

// komgaFactory Komga 客户端工厂（缓存与 komgaClient 共用；测试注入 komgaBase 时同样生效）。
func (s *Server) komgaFactory() *komga.Client { return s.komgaClient() }

// onJobDone 任务完成 → 受影响系列回源刷新缓存（异步，不阻塞任务收尾）。
func (s *Server) onJobDone(_ string, seriesIDs []string) {
	for _, id := range seriesIDs {
		s.cache.InvalidateSeries(id)
	}
}

// coversDir 封面磁盘缓存目录（DATA_DIR/covers）。
func (s *Server) coversDir() string { return filepath.Join(s.dataDir, "covers") }

// coverPath 封面缓存文件路径（kind: series|book）。
func (s *Server) coverPath(kind, id string) string {
	return filepath.Join(s.coversDir(), kind+"-"+id+".bin")
}

// SetDataDir 设置数据目录（BW 索引 / 日志 / Mylar 基准；main.go 与测试注入）。
func (s *Server) SetDataDir(d string) { s.dataDir = d }

// Router 组装路由。
func (s *Server) Router() http.Handler {
	mux := http.NewServeMux()

	// 免认证
	mux.HandleFunc("GET /api/health", s.handleHealth)
	mux.HandleFunc("POST /api/auth/login", s.handleLogin)

	// 认证
	authed := http.NewServeMux()
	authed.HandleFunc("GET /api/me", s.handleMe)

	// 用户管理（仅 admin）
	authed.Handle("GET /api/users", s.admin(s.handleUsersList))
	authed.Handle("POST /api/users", s.admin(s.handleUsersCreate))
	authed.Handle("GET /api/users/name-index", s.admin(s.handleUsersNameIndex))
	authed.Handle("PATCH /api/users/{id}", s.admin(s.handleUsersPatch))
	authed.Handle("DELETE /api/users/{id}", s.admin(s.handleUsersDelete))

	// 库与系列（Komga 代理 + 权限过滤）
	authed.HandleFunc("GET /api/libraries", s.handleLibraries)
	authed.HandleFunc("GET /api/libraries/{id}/series", s.handleLibrarySeries)
	authed.HandleFunc("GET /api/libraries/{id}/mylar", s.handleMylarZip)
	authed.Handle("POST /api/libraries/{id}/mylar/export", s.admin(s.handleMylarExport))
	authed.HandleFunc("GET /api/series/{id}", s.handleSeries)
	authed.HandleFunc("GET /api/series/{id}/thumbnail", s.handleSeriesThumbnail)
	authed.HandleFunc("PATCH /api/series/{id}/metadata-raw", s.handleSeriesMetadataRaw)
	authed.HandleFunc("POST /api/series/{id}/cover", s.handleSeriesCover)
	authed.HandleFunc("POST /api/series/{id}/rescrapevolumes", s.handleSeriesRescrapeVolumes)
	authed.HandleFunc("POST /api/books/{id}/metadata-raw", s.handleBookMetadataRaw)
	authed.HandleFunc("POST /api/books/{id}/cover", s.handleBookCover)
	authed.HandleFunc("GET /api/books/{id}/thumbnail", s.handleBookThumbnail)
	authed.HandleFunc("GET /api/series/{id}/match/candidates", s.handleMatchCandidates)
	authed.HandleFunc("GET /api/match/detail", s.handleMatchDetail)
	authed.HandleFunc("POST /api/match/resolve", s.handleMatchResolve)
	authed.HandleFunc("POST /api/series/{id}/match/apply", s.handleMatchApply)
	authed.HandleFunc("GET /api/manual-collection", s.handleManualCollection)
	authed.HandleFunc("GET /api/search", s.handleSearch)

	// 刮削任务
	authed.HandleFunc("POST /api/jobs", s.handleJobsCreate)
	authed.HandleFunc("GET /api/jobs", s.handleJobsList)
	authed.HandleFunc("GET /api/jobs/{id}", s.handleJobGet)
	authed.HandleFunc("POST /api/jobs/{id}/cancel", s.handleJobCancel)

	// 设置（仅 admin）
	authed.Handle("GET /api/settings", s.admin(s.handleSettingsGet))
	authed.Handle("PATCH /api/settings", s.admin(s.handleSettingsPatch))
	authed.Handle("POST /api/settings/test-komga", s.admin(s.handleTestKomga))
	authed.Handle("POST /api/settings/bw/download", s.admin(s.handleBWDownload))
	authed.Handle("POST /api/settings/bw/import", s.admin(s.handleBWImport))
	authed.Handle("GET /api/settings/bw/status", s.admin(s.handleBWStatus))

	// 日志（admin only）
	authed.Handle("GET /api/logs", s.admin(s.handleLogs))

	// 本地库缓存（admin only）
	authed.Handle("GET /api/cache/status", s.admin(s.handleCacheStatus))
	authed.Handle("POST /api/cache/refresh", s.admin(s.handleCacheRefresh))

	// 自动更新检查（admin only）
	authed.Handle("GET /api/update/status", s.admin(s.handleUpdateStatus))
	authed.Handle("POST /api/update/check", s.admin(s.handleUpdateCheck))

	mux.Handle("/api/", s.auth.Middleware(authed))

	// SPA fallback：非 /api 路径全部走嵌入前端
	dist := webdist.FS()
	mux.HandleFunc("/", spaHandler(dist))

	return logRequests(s.log, mux)
}

// admin admin 门禁包装（接 handler func）。
func (s *Server) admin(next func(http.ResponseWriter, *http.Request)) http.Handler {
	return auth.RequireAdmin(http.HandlerFunc(next))
}

// logRequests 请求日志（slog，中文可读）。
func logRequests(log *slog.Logger, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		next.ServeHTTP(w, r)
	})
}

// spaHandler 静态文件服务 + index.html fallback。
func spaHandler(dist fs.FS) http.HandlerFunc {
	fileServer := http.FileServer(http.FS(dist))
	return func(w http.ResponseWriter, r *http.Request) {
		p := strings.TrimPrefix(path.Clean(r.URL.Path), "/")
		if p == "" {
			p = "index.html"
		}
		if _, err := fs.Stat(dist, p); err != nil {
			// SPA fallback：未命中的路径回退到 index.html
			r.URL.Path = "/"
		}
		fileServer.ServeHTTP(w, r)
	}
}

// writeJSON 统一 JSON 输出。
func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(v)
}

// writeError 统一错误格式 { "error": "中文可读消息" }。
func writeError(w http.ResponseWriter, status int, msg string) {
	writeJSON(w, status, map[string]string{"error": msg})
}

// handleHealth GET /api/health（免认证）。
func (s *Server) handleHealth(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, 200, map[string]any{"ok": true, "version": buildVersionOr()})
}

// buildVersionOr 优先返回构建注入的版本（Docker 镜像 = 实际版本号），否则退回 API 契约常量。
func buildVersionOr() string {
	if v := update.Current(); v != "" && v != "dev" {
		return v
	}
	return Version
}

// komgaClient 按当前设置构建 Komga 客户端（测试可覆盖）。
func (s *Server) komgaClient() *komga.Client {
	if s.komgaBase != "" {
		return komga.New(s.komgaBase, "", "", "")
	}
	st := s.LoadSettings()
	return komga.New(st.KomgaURL, st.KomgaAPIKey, st.KomgaUser, st.KomgaPassword)
}

// bangumiClient 按当前设置构建 Bangumi 客户端（测试注入基址见 LoadSettings）。
func (s *Server) bangumiClient() *bangumi.Client {
	st := s.LoadSettings()
	return bangumi.New(st.BangumiToken, st.BangumiAPIBase, st.BangumiMinIntervalMs)
}

// newRunner 任务引擎的 Runner 工厂（应用最新设置：各源客户端 + 代理 + 状态库）。
func (s *Server) newRunner(dryRun bool) *scrape.Runner {
	st := s.LoadSettings()
	collectionName := ""
	if st.ManualCollection {
		collectionName = "待手动匹配"
	}

	// ---- 代理：Bangumi API + 封面下载 + BW 下载共用；Komga 永远直连 ----
	pc := sources.ProxyConfig{Protocol: st.Proxy.Protocol, Host: st.Proxy.Host, Port: st.Proxy.Port,
		Username: st.Proxy.Username, Password: st.Proxy.Password}
	var coverHTTP *http.Client
	if pc.IsSet() {
		if hc, err := sources.NewHTTPClient(pc, 40*time.Second, coverUA, ""); err == nil {
			coverHTTP = hc
		}
	}

	// ---- 源客户端 ----
	sc := scrape.SourceClients{}
	bgmClient := s.bangumiClient()
	if coverHTTP != nil {
		bgmClient.SetHTTPClient(coverHTTP) // 同一 Transport 同时服务 Bangumi API 与封面下载
	}
	if st.AI.Enabled && strings.TrimSpace(st.AI.BaseURL) != "" {
		if ai, err := sources.NewAiClient(st.AI.BaseURL, st.AI.APIKey, st.AI.Model, pc, 60*time.Second); err == nil {
			sc.AI = ai
		} else {
			s.log.Warn("AI 客户端创建失败: " + err.Error())
		}
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
	if st.EhEnabled {
		if eh, err := sources.NewEhClient(pc, ehBaseOverride, scrape.SlogLogger{L: s.log}, ehPaceOverrideMs); err == nil {
			sc.EH = eh
		} else {
			s.log.Warn("E-Hentai 客户端创建失败: " + err.Error())
		}
	}

	return &scrape.Runner{
		Komga:            s.komgaClient(),
		Bangumi:          bgmClient,
		Log:              s.log,
		CollectionName:   collectionName,
		DryRun:           dryRun,
		FetchVolumes:     st.FetchVolumes,
		WithCovers:       st.WithCovers,
		Concurrency:      st.Concurrency,
		MatchType:        st.MatchType,
		Sources:          sc,
		TranslateSummary: st.AI.TranslateSummary,
		AIWriteSummary:   st.AI.WriteSummary,
		AIIdentify:       st.AI.Identify,
		AISmartPick:      st.AI.SmartPick,
		BwWriteTags:      st.BookWalker.WriteExtras,
		Whitelist:        st.VersionTagWhitelist,
		Blacklist:        st.VersionTagBlacklist,
		CoverHTTP:        coverHTTP,
		State:            &storeStateAdapter{st: s.store, s: s},
	}
}

const coverUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"

// ---- 测试注入点（默认空 = 官方站；集成测试指向 httptest mock）----
var (
	moxBaseOverride      = ""
	moxPaceOverrideMs    = 500
	mangaDexBaseOverride = ""
	baikeBaseOverride    = ""
	ehBaseOverride       = "https://e-hentai.org" // E-Hentai 表站
	ehPaceOverrideMs     = 1100                   // 表站节流：≥1s/请求
)

// SetSourceBaseOverrides 运行时注入源基址（env 种子/自建反代用；空串 = 官方站）。
// moxPaceMs<=0 时用默认 500；ehPaceMs<=0 时用默认 1100。
func SetSourceBaseOverrides(moxBase, mangaDexBase, baikeBase, ehBase string, moxPaceMs, ehPaceMs int) {
	if moxBase != "" {
		moxBaseOverride = moxBase
	}
	if mangaDexBase != "" {
		mangaDexBaseOverride = mangaDexBase
	}
	if baikeBase != "" {
		baikeBaseOverride = baikeBase
	}
	if ehBase != "" {
		ehBaseOverride = ehBase
	}
	if moxPaceMs > 0 {
		moxPaceOverrideMs = moxPaceMs
	}
	if ehPaceMs > 0 {
		ehPaceOverrideMs = ehPaceMs
	}
}

// bwIndexPath BW 索引路径（DATA_DIR/bkwk-index.json.gz）。
func (s *Server) bwIndexPath() string {
	p := filepath.Join(s.dataDir, "bkwk-index.json.gz")
	if _, err := os.Stat(p); err != nil {
		return ""
	}
	return p
}

// storeStateAdapter store 刮削状态 → scrape.StateWriter 适配。
type storeStateAdapter struct {
	st *store.Store
	s  *Server // 封面缓存失效需要（与 store 同生命周期）
}

// SetCover 记封面状态（封面刚被任务写入 → 同步失效封面磁盘缓存 + 系列缓存回源）。
func (a *storeStateAdapter) SetCover(seriesID string, ok bool) {
	if ok {
		a.s.dropCover("series", seriesID)
		a.s.cache.InvalidateSeries(seriesID)
	}
	existing, _ := a.st.GetScrapeState(seriesID)
	st := store.ScrapeState{SeriesID: seriesID, HasCover: ok}
	if existing != nil {
		st.HasSummary = existing.HasSummary
		st.Links = existing.Links
	}
	_ = a.st.SetScrapeState(st)
}

// SetSummary 记简介状态。
func (a *storeStateAdapter) SetSummary(seriesID string, ok bool) {
	existing, _ := a.st.GetScrapeState(seriesID)
	st := store.ScrapeState{SeriesID: seriesID, HasSummary: ok}
	if existing != nil {
		st.HasCover = existing.HasCover
		st.Links = existing.Links
	}
	_ = a.st.SetScrapeState(st)
}

// userLibraries 用户的库授权：admin 全库（"*"），master 返回被分配的库 id。
func (s *Server) userLibraries(c *auth.Claims) []string {
	if c.Role == "admin" {
		return []string{"*"}
	}
	libs, _ := s.store.GetLibraries(c.UserID)
	return libs
}

// canAccessLibrary 权限判断。
func (s *Server) canAccessLibrary(c *auth.Claims, libraryID string) bool {
	if c == nil {
		return false
	}
	if c.Role == "admin" {
		return true
	}
	libs, _ := s.store.GetLibraries(c.UserID)
	for _, l := range libs {
		if l == libraryID {
			return true
		}
	}
	return false
}
