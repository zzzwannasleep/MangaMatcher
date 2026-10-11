package api

import (
	"encoding/json"
	"strings"
)

// ProxySettings 代理配置（SPEC：HTTP/HTTPS/SOCKS4/4a/5 + 账密）。
type ProxySettings struct {
	Protocol string `json:"protocol"`
	Host     string `json:"host"`
	Port     int    `json:"port"`
	Username string `json:"username"`
	Password string `json:"password"` // GET 打码 ****；PATCH 传 ****/空 = 不改
}

// AISettings AI（OpenAI 兼容）配置。
type AISettings struct {
	Enabled          bool   `json:"enabled"`
	BaseURL          string `json:"baseURL"`
	APIKey           string `json:"apiKey"` // 打码
	Model            string `json:"model"`
	Identify         bool   `json:"identify"`         // 识别作品（搜不到时问 AI 日/中/英三名再重搜）
	SmartPick        bool   `json:"smartPick"`        // 多候选时 AI 智能选择
	TranslateSummary bool   `json:"translateSummary"` // 非中文简介→简体中文
	WriteSummary     bool   `json:"writeSummary"`     // 三源无简介时 AI 补写
}

// BookWalkerSettings BookWalker 目录库配置。
type BookWalkerSettings struct {
	DownloadURL string `json:"downloadURL"`
	WriteExtras bool   `json:"writeExtras"` // 类型/题材/出版品牌/阅读方向 写入 Komga
}

// CacheSettings 本地库缓存配置（打开即用不重拉；后台自动对齐 Komga）。
type CacheSettings struct {
	Enabled        bool `json:"enabled"`
	RefreshMinutes int  `json:"refreshMinutes"` // 自动对齐间隔（分钟；0=关闭自动刷新）
}

// UpdateSettings 自动更新检查配置（Docker 版：检查新版本 → 提示引导更新）。
type UpdateSettings struct {
	AutoCheck bool `json:"autoCheck"` // 启动后自动检查（每 24h 一次）；关掉仍可手动检查
}

// Settings 全局设置（SPEC-v2 第 1 节 + v1 已有字段）。
// 秘钥字段 GET 返回 "****" 打码；PATCH 传 "****" 或空表示不修改。
type Settings struct {
	KomgaURL             string `json:"komgaUrl"`
	KomgaAPIKey          string `json:"komgaApiKey"`
	KomgaUser            string `json:"komgaUser,omitempty"`
	KomgaPassword        string `json:"komgaPassword,omitempty"`
	BangumiToken         string `json:"bangumiToken"`
	BangumiAPIBase       string `json:"bangumiApiBase,omitempty"`
	BangumiMinIntervalMs int    `json:"bangumiMinIntervalMs,omitempty"`
	Concurrency          int    `json:"concurrency"`
	DryRunDefault        bool   `json:"dryRunDefault"`
	ManualCollection     bool   `json:"manualCollection"`
	FetchVolumes         bool   `json:"fetchVolumes"`
	WithCovers           bool   `json:"withCovers"`

	// ---- v2 ----
	MatchType           string             `json:"matchType"` // 漫画|小说
	Proxy               ProxySettings      `json:"proxy"`
	AI                  AISettings         `json:"ai"`
	BaikeEnabled        bool               `json:"baikeEnabled"`
	MangaDexEnabled     bool               `json:"mangaDexEnabled"`
	BookWalker          BookWalkerSettings `json:"bookwalker"`
	BatchForceRefresh   bool               `json:"batchForceRefresh"`
	VersionTagWhitelist []string           `json:"versionTagWhitelist"`
	VersionTagBlacklist []string           `json:"versionTagBlacklist"`
	ComicsDir           string             `json:"comicsDir"`
	Cache               CacheSettings      `json:"cache"`
	Update              UpdateSettings     `json:"update"`

	// 内部：DATA_DIR（不进 JSON）
	DataDir string `json:"-"`
}

const mask = "****"

// LoadSettings 从 DB 读设置（缺省值兜底）。
func (s *Server) LoadSettings() Settings {
	st := Settings{
		KomgaURL:             "http://localhost:25600",
		Concurrency:          3,
		DryRunDefault:        true,
		ManualCollection:     true,
		FetchVolumes:         true,
		WithCovers:           true,
		BangumiMinIntervalMs: 350,
		MatchType:            "漫画",
		BaikeEnabled:         true,
		MangaDexEnabled:      true,
		Cache:                CacheSettings{Enabled: true, RefreshMinutes: 60},
		Update:               UpdateSettings{AutoCheck: true},
		BookWalker: BookWalkerSettings{
			DownloadURL: "https://data.bookwalker.com/bkwk-db.sqlite.zst",
			WriteExtras: true,
		},
		AI: AISettings{BaseURL: "https://api.deepseek.com", Model: "deepseek-chat", Identify: true, SmartPick: true},
		DataDir: s.dataDir,
	}
	if v, ok := s.store.GetSetting("settings"); ok {
		var saved Settings
		if err := json.Unmarshal([]byte(v), &saved); err == nil {
			if saved.KomgaURL != "" {
				st.KomgaURL = saved.KomgaURL
			}
			st.KomgaAPIKey = saved.KomgaAPIKey
			st.KomgaUser = saved.KomgaUser
			st.KomgaPassword = saved.KomgaPassword
			st.BangumiToken = saved.BangumiToken
			st.BangumiAPIBase = saved.BangumiAPIBase
			if saved.Concurrency > 0 {
				st.Concurrency = saved.Concurrency
			}
			st.DryRunDefault = saved.DryRunDefault
			st.ManualCollection = saved.ManualCollection
			st.FetchVolumes = saved.FetchVolumes
			st.WithCovers = saved.WithCovers
			if saved.BangumiMinIntervalMs > 0 {
				st.BangumiMinIntervalMs = saved.BangumiMinIntervalMs
			}
			// v2
			if saved.MatchType != "" {
				st.MatchType = saved.MatchType
			}
			st.Proxy = saved.Proxy
			st.AI = saved.AI
			if st.AI.BaseURL == "" {
				st.AI.BaseURL = "https://api.deepseek.com"
			}
			if st.AI.Model == "" {
				st.AI.Model = "deepseek-chat"
			}
			st.BaikeEnabled = saved.BaikeEnabled
			st.MangaDexEnabled = saved.MangaDexEnabled
			// 缺省 true（老设置无该字段时 json.Unmarshal 得 false，这里在"没有任何 v2 字段"时保持默认）
			if raw, ok := rawHasKey(v, "baikeEnabled"); !ok {
				st.BaikeEnabled = true
			} else {
				st.BaikeEnabled = raw
			}
			if raw, ok := rawHasKey(v, "mangaDexEnabled"); !ok {
				st.MangaDexEnabled = true
			} else {
				st.MangaDexEnabled = raw
			}
			if saved.BookWalker.DownloadURL != "" {
				st.BookWalker.DownloadURL = saved.BookWalker.DownloadURL
			}
			if rawHasKeyAny(v, "bookwalker") {
				st.BookWalker.WriteExtras = saved.BookWalker.WriteExtras
			}
			st.BatchForceRefresh = saved.BatchForceRefresh
			st.VersionTagWhitelist = saved.VersionTagWhitelist
			st.VersionTagBlacklist = saved.VersionTagBlacklist
			st.ComicsDir = saved.ComicsDir
			// 缓存：老设置无该字段 → 保持默认（开 + 60min）
			if rawHasKeyAny(v, "cache") {
				st.Cache.Enabled = saved.Cache.Enabled
				if saved.Cache.RefreshMinutes >= 0 {
					st.Cache.RefreshMinutes = saved.Cache.RefreshMinutes
				}
			}
			if rawHasKeyAny(v, "update") {
				st.Update.AutoCheck = saved.Update.AutoCheck
			}
		}
	} else {
		// 尚无整体设置 blob：回退读取 main.go 的 env 种子（SeedSettingIfAbsent 写入的独立键）。
		if v, ok := s.store.GetSetting("komgaUrl"); ok && v != "" {
			st.KomgaURL = v
		}
		if v, ok := s.store.GetSetting("komgaApiKey"); ok && v != "" {
			st.KomgaAPIKey = v
		}
		if v, ok := s.store.GetSetting("bangumiToken"); ok && v != "" {
			st.BangumiToken = v
		}
		if v, ok := s.store.GetSetting("bangumiApiBase"); ok && v != "" {
			st.BangumiAPIBase = v
		}
	}
	return st
}

// rawHasKey 读设置 blob 的 bool 字段（区分「没存过」与「存了 false」）。
func rawHasKey(blob, key string) (bool, bool) {
	var m map[string]json.RawMessage
	if err := json.Unmarshal([]byte(blob), &m); err != nil {
		return false, false
	}
	raw, ok := m[key]
	if !ok {
		return false, false
	}
	var b bool
	if err := json.Unmarshal(raw, &b); err != nil {
		return false, true
	}
	return b, true
}

// rawHasKeyAny 判断 blob 是否含某键。
func rawHasKeyAny(blob, key string) bool {
	var m map[string]json.RawMessage
	if err := json.Unmarshal([]byte(blob), &m); err != nil {
		return false
	}
	_, ok := m[key]
	return ok
}

// SaveSettings 保存设置。
func (s *Server) SaveSettings(st Settings) error {
	b, err := json.Marshal(st)
	if err != nil {
		return err
	}
	return s.store.SetSetting("settings", string(b))
}

// Masked 返回给前端的打码视图。
func (st Settings) Masked() Settings {
	cp := st
	if cp.KomgaAPIKey != "" {
		cp.KomgaAPIKey = mask
	}
	if cp.KomgaPassword != "" {
		cp.KomgaPassword = mask
	}
	if cp.BangumiToken != "" {
		cp.BangumiToken = mask
	}
	if cp.AI.APIKey != "" {
		cp.AI.APIKey = mask
	}
	if cp.Proxy.Password != "" {
		cp.Proxy.Password = mask
	}
	cp.DataDir = ""
	return cp
}

// ApplyPatch 合并 PATCH（打码/空值表示不修改；嵌套对象 proxy/ai/bookwalker 逐字段合并）。
func (st Settings) ApplyPatch(body json.RawMessage) (Settings, error) {
	var patch map[string]json.RawMessage
	if err := json.Unmarshal(body, &patch); err != nil {
		return st, err
	}
	cp := st
	getStr := func(k string) (string, bool) {
		raw, ok := patch[k]
		if !ok {
			return "", false
		}
		var s string
		if err := json.Unmarshal(raw, &s); err != nil {
			return "", false
		}
		return s, true
	}
	getBool := func(k string) (bool, bool) { return rawHasKey(string(mustMarshal(patch)), k) }

	if v, ok := getStr("komgaUrl"); ok && v != "" {
		cp.KomgaURL = v
	}
	if v, ok := getStr("komgaApiKey"); ok && v != "" && v != mask {
		cp.KomgaAPIKey = v
	}
	if v, ok := getStr("komgaUser"); ok {
		cp.KomgaUser = v
	}
	if v, ok := getStr("komgaPassword"); ok && v != "" && v != mask {
		cp.KomgaPassword = v
	}
	if v, ok := getStr("bangumiToken"); ok && v != "" && v != mask {
		cp.BangumiToken = v
	}
	if v, ok := getStr("bangumiApiBase"); ok {
		cp.BangumiAPIBase = v
	}
	if v, ok := patch["bangumiMinIntervalMs"]; ok {
		var f float64
		if err := json.Unmarshal(v, &f); err == nil && f >= 0 {
			cp.BangumiMinIntervalMs = int(f)
		}
	}
	if v, ok := patch["concurrency"]; ok {
		var f float64
		if err := json.Unmarshal(v, &f); err == nil && f >= 1 {
			c := int(f)
			if c > 16 {
				c = 16
			}
			cp.Concurrency = c
		}
	}
	if b, ok := getBool("dryRunDefault"); ok {
		cp.DryRunDefault = b
	}
	if b, ok := getBool("manualCollection"); ok {
		cp.ManualCollection = b
	}
	if b, ok := getBool("fetchVolumes"); ok {
		cp.FetchVolumes = b
	}
	if b, ok := getBool("withCovers"); ok {
		cp.WithCovers = b
	}

	// ---- v2 ----
	if v, ok := getStr("matchType"); ok && (v == "漫画" || v == "小说") {
		cp.MatchType = v
	}
	if raw, ok := patch["proxy"]; ok {
		var p map[string]any
		if json.Unmarshal(raw, &p) == nil {
			if v, ok := p["protocol"].(string); ok {
				cp.Proxy.Protocol = v
			}
			if v, ok := p["host"].(string); ok {
				cp.Proxy.Host = strings.TrimSpace(v)
			}
			if v, ok := p["port"].(float64); ok {
				cp.Proxy.Port = int(v)
			}
			if v, ok := p["username"].(string); ok {
				cp.Proxy.Username = v
			}
			if v, ok := p["password"].(string); ok && v != "" && v != mask {
				cp.Proxy.Password = v
			}
		}
	}
	if raw, ok := patch["ai"]; ok {
		var a map[string]any
		if json.Unmarshal(raw, &a) == nil {
			if v, ok := a["enabled"].(bool); ok {
				cp.AI.Enabled = v
			}
			if v, ok := a["baseURL"].(string); ok && v != "" {
				cp.AI.BaseURL = v
			}
			if v, ok := a["apiKey"].(string); ok && v != "" && v != mask {
				cp.AI.APIKey = v
			}
			if v, ok := a["model"].(string); ok && v != "" {
				cp.AI.Model = v
			}
			if v, ok := a["identify"].(bool); ok {
				cp.AI.Identify = v
			}
			if v, ok := a["smartPick"].(bool); ok {
				cp.AI.SmartPick = v
			}
			if v, ok := a["translateSummary"].(bool); ok {
				cp.AI.TranslateSummary = v
			}
			if v, ok := a["writeSummary"].(bool); ok {
				cp.AI.WriteSummary = v
			}
		}
	}
	if b, ok := getBool("baikeEnabled"); ok {
		cp.BaikeEnabled = b
	}
	if b, ok := getBool("mangaDexEnabled"); ok {
		cp.MangaDexEnabled = b
	}
	if raw, ok := patch["bookwalker"]; ok {
		var b map[string]any
		if json.Unmarshal(raw, &b) == nil {
			if v, ok := b["downloadURL"].(string); ok && v != "" {
				cp.BookWalker.DownloadURL = v
			}
			if v, ok := b["writeExtras"].(bool); ok {
				cp.BookWalker.WriteExtras = v
			}
		}
	}
	if b, ok := getBool("batchForceRefresh"); ok {
		cp.BatchForceRefresh = b
	}
	if raw, ok := patch["versionTagWhitelist"]; ok {
		var list []string
		if json.Unmarshal(raw, &list) == nil {
			cp.VersionTagWhitelist = cleanWordList(list)
		}
	}
	if raw, ok := patch["versionTagBlacklist"]; ok {
		var list []string
		if json.Unmarshal(raw, &list) == nil {
			cp.VersionTagBlacklist = cleanWordList(list)
		}
	}
	if v, ok := getStr("comicsDir"); ok {
		cp.ComicsDir = v
	}
	if raw, ok := patch["cache"]; ok {
		var cm map[string]any
		if json.Unmarshal(raw, &cm) == nil {
			if b, ok := cm["enabled"].(bool); ok {
				cp.Cache.Enabled = b
			}
			if f, ok := cm["refreshMinutes"].(float64); ok && f >= 0 {
				n := int(f)
				if n > 1440 {
					n = 1440
				}
				cp.Cache.RefreshMinutes = n
			}
		}
	}
	if raw, ok := patch["update"]; ok {
		var um map[string]any
		if json.Unmarshal(raw, &um) == nil {
			if b, ok := um["autoCheck"].(bool); ok {
				cp.Update.AutoCheck = b
			}
		}
	}
	return cp, nil
}

// cleanWordList 清洗黑白名单（去空、去重、保序）。
func cleanWordList(list []string) []string {
	seen := map[string]bool{}
	var out []string
	for _, w := range list {
		w = strings.TrimSpace(w)
		if w == "" || seen[w] {
			continue
		}
		seen[w] = true
		out = append(out, w)
	}
	return out
}

func mustMarshal(v any) []byte {
	b, _ := json.Marshal(v)
	return b
}
