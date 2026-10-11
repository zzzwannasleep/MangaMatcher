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
	"komgabangumi/internal/scrape"
	"komgabangumi/internal/sources"
	"komgabangumi/internal/store"
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
}

// NewServer 创建 API 服务器（内部创建任务引擎，Runner 由最新设置构建）。
func NewServer(st *store.Store, am *auth.Manager, log *slog.Logger) *Server {
	s := &Server{store: st, auth: am, log: log, dataDir: "./data"}
	s.jobs = jobs.NewEngine(s.komgaClient, log, s.newRunner)
	return s
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
	writeJSON(w, 200, map[string]any{"ok": true, "version": Version})
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

// newRunner 任务引擎的 Runner 工厂（应用最新设置：五源客户端 + 代理 + 状态库）。
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

	// ---- 五源客户端 ----
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

	return &scrape.Runner{
		Komga:          s.komgaClient(),
		Bangumi:        bgmClient,
		Log:            s.log,
		CollectionName: collectionName,
		DryRun:         dryRun,
		FetchVolumes:   st.FetchVolumes,
		WithCovers:     st.WithCovers,
		Concurrency:    st.Concurrency,
		MatchType:      st.MatchType,
		Sources:        sc,
		TranslateSummary: st.AI.TranslateSummary,
		AIWriteSummary:   st.AI.WriteSummary,
		AIIdentify:       st.AI.Identify,
		AISmartPick:      st.AI.SmartPick,
		BwWriteTags:      st.BookWalker.WriteExtras,
		Whitelist:        st.VersionTagWhitelist,
		Blacklist:        st.VersionTagBlacklist,
		CoverHTTP:        coverHTTP,
		State:            &storeStateAdapter{st: s.store},
	}
}

const coverUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"

// ---- 测试注入点（默认空 = 官方站；集成测试指向 httptest mock）----
var (
	moxBaseOverride       = ""
	moxPaceOverrideMs     = 500
	mangaDexBaseOverride  = ""
	baikeBaseOverride     = ""
)

// SetSourceBaseOverrides 运行时注入源基址（env 种子/自建反代用；空串 = 官方站）。
// moxPaceMs<=0 时用默认 500。
func SetSourceBaseOverrides(moxBase, mangaDexBase, baikeBase string, moxPaceMs int) {
	if moxBase != "" {
		moxBaseOverride = moxBase
	}
	if mangaDexBase != "" {
		mangaDexBaseOverride = mangaDexBase
	}
	if baikeBase != "" {
		baikeBaseOverride = baikeBase
	}
	if moxPaceMs > 0 {
		moxPaceOverrideMs = moxPaceMs
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
type storeStateAdapter struct{ st *store.Store }

// SetCover 记封面状态。
func (a *storeStateAdapter) SetCover(seriesID string, ok bool) {
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