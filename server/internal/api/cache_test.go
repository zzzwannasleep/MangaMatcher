package api

// ============================================================
// 本地库缓存集成测试：打开即用 / 零重复拉取 / 自动对齐 / 失效链
// ============================================================

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"komgabangumi/internal/auth"
	"komgabangumi/internal/store"
)

// cacheStatus 读缓存状态。
func cacheStatus(t *testing.T, e *testEnv) map[string]any {
	t.Helper()
	resp, data := e.do("GET", "/api/cache/status", e.adminToken, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("GET /api/cache/status: %d %s", resp.StatusCode, data)
	}
	var out map[string]any
	json.Unmarshal(data, &out)
	return out
}

// warmCache 手动预热并等待就绪。
func warmCache(t *testing.T, e *testEnv) {
	t.Helper()
	e.do("POST", "/api/cache/refresh", e.adminToken, nil)
	deadline := time.Now().Add(5 * time.Second)
	for {
		st := cacheStatus(t, e)
		if st["ready"] == true && st["seriesCount"].(float64) >= 1 {
			return
		}
		if time.Now().After(deadline) {
			t.Fatalf("缓存 5s 内未就绪: %v", st)
		}
		time.Sleep(50 * time.Millisecond)
	}
}

// TestCacheColdStartWarmRead 冷启动 → 预热 → 列表/库列表走内存（Komga 请求计数零增长）。
func TestCacheColdStartWarmRead(t *testing.T) {
	e := newTestEnv(t)

	// 冷态：首个列表请求回源 Komga（兼容旧路径）
	resp, data := e.do("GET", "/api/libraries/lib1/series?page=0&size=50", e.adminToken, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("冷态列表失败: %d %s", resp.StatusCode, data)
	}

	// 预热后：热读零 Komga 请求
	warmCache(t, e)
	before := len(e.komga.requests("", ""))
	for i := 0; i < 2; i++ {
		resp, data := e.do("GET", "/api/libraries/lib1/series?page=0&size=50", e.adminToken, nil)
		if resp.StatusCode != 200 {
			t.Fatalf("热读列表失败: %d %s", resp.StatusCode, data)
		}
	}
	if resp, data := e.do("GET", "/api/libraries", e.adminToken, nil); resp.StatusCode != 200 {
		t.Fatalf("热读库列表失败: %d %s", resp.StatusCode, data)
	}
	after := len(e.komga.requests("", ""))
	if after != before {
		t.Fatalf("热读仍打 Komga：%d → %d（缓存未生效）", before, after)
	}

	// 列表条数与 mock 一致（s1 一条）
	_, data = e.do("GET", "/api/libraries/lib1/series?page=0&size=50", e.adminToken, nil)
	var page struct {
		TotalElements int `json:"totalElements"`
	}
	json.Unmarshal(data, &page)
	if page.TotalElements != 1 {
		t.Fatalf("缓存列表 totalElements=%d，期望 1", page.TotalElements)
	}
}

// TestCacheRestartRestore 重启（新 Server 同一 SQLite）→ 不刷新直接可读（打开即用）。
func TestCacheRestartRestore(t *testing.T) {
	e := newTestEnv(t)
	warmCache(t, e)

	// 模拟重启：新 Server 实例 + 同一 dataDir（同一 SQLite 文件），不预热
	e2 := reopenServer(t, e)
	e2.server.cache.Load() // Start() 的恢复步骤（测试不启动刷新循环）
	before := len(e2.komga.requests("GET", "/api/v1/libraries"))
	if resp, data := e2.do("GET", "/api/libraries", e2.adminToken, nil); resp.StatusCode != 200 {
		t.Fatalf("重启后库列表失败: %d %s", resp.StatusCode, data)
	} else if !strings.Contains(string(data), "Comics") {
		t.Fatalf("重启后库列表缺数据: %s", data)
	}
	after := len(e2.komga.requests("GET", "/api/v1/libraries"))
	if after != before {
		t.Fatalf("重启后库列表仍打 Komga：%d → %d", before, after)
	}
	// 列表也可用
	resp, data := e2.do("GET", "/api/libraries/lib1/series?page=0&size=50", e2.adminToken, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("重启后列表失败: %d %s", resp.StatusCode, data)
	}
	var page struct {
		Content []json.RawMessage `json:"content"`
	}
	json.Unmarshal(data, &page)
	if len(page.Content) != 1 {
		t.Fatalf("重启后缓存列表条数=%d，期望 1", len(page.Content))
	}
}

// TestCacheInvalidationOnEdit 编辑器保存元数据 → 缓存内标题更新（单系列回源）。
func TestCacheInvalidationOnEdit(t *testing.T) {
	e := newTestEnv(t)
	warmCache(t, e)

	resp, data := e.do("PATCH", "/api/series/s1/metadata-raw", e.adminToken,
		map[string]any{"title": "新标题测试"})
	if resp.StatusCode != 200 {
		t.Fatalf("PATCH 元数据失败: %d %s", resp.StatusCode, data)
	}
	deadline := time.Now().Add(5 * time.Second)
	for {
		_, data := e.do("GET", "/api/libraries/lib1/series?page=0&size=50", e.adminToken, nil)
		if strings.Contains(string(data), "新标题测试") {
			return
		}
		if time.Now().After(deadline) {
			t.Fatalf("5s 内缓存未反映编辑器变更")
		}
		time.Sleep(50 * time.Millisecond)
	}
}

// TestCacheResetOnKomgaUrlChange Komga URL 变更 → 缓存清空重建（不串库）。
func TestCacheResetOnKomgaUrlChange(t *testing.T) {
	e := newTestEnv(t)
	warmCache(t, e)

	// 读当前 komgaUrl，补一个尾斜杠触发"连接变更"分支（同一 mock）
	_, sdata := e.do("GET", "/api/settings", e.adminToken, nil)
	var saved struct {
		KomgaURL string `json:"komgaUrl"`
	}
	json.Unmarshal(sdata, &saved)
	if saved.KomgaURL == "" {
		t.Fatal("设置里读不到 komgaUrl")
	}
	e.patchSettings(map[string]any{"komgaUrl": saved.KomgaURL + "/"})

	deadline := time.Now().Add(5 * time.Second)
	for {
		st := cacheStatus(t, e)
		if st["seriesCount"].(float64) >= 1 {
			return // 重建完成
		}
		if time.Now().After(deadline) {
			t.Fatalf("URL 变更后缓存未重建: %v", st)
		}
		time.Sleep(50 * time.Millisecond)
	}
}

// TestCoverETag304 封面磁盘缓存 + ETag/304。
func TestCoverETag304(t *testing.T) {
	e := newTestEnv(t)
	warmCache(t, e) // 权限校验走缓存库归属，封面请求本身不再打 GetSeries

	// 首次：200 + ETag
	req, _ := http.NewRequest("GET", e.api.URL+"/api/series/s1/thumbnail", nil)
	req.Header.Set("Authorization", "Bearer "+e.adminToken)
	resp1, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	etag := resp1.Header.Get("ETag")
	body1 := readAll(t, resp1)
	resp1.Body.Close()
	if resp1.StatusCode != 200 || len(body1) == 0 || etag == "" {
		t.Fatalf("首次封面响应异常: %d etag=%q len=%d", resp1.StatusCode, etag, len(body1))
	}

	// mock 端水位（首次封面已产生 1 次 /thumbnail 请求）
	before := len(e.komga.requests("GET", "/api/v1/series/"))

	// 第二次带 If-None-Match：304 无 body
	req2, _ := http.NewRequest("GET", e.api.URL+"/api/series/s1/thumbnail", nil)
	req2.Header.Set("Authorization", "Bearer "+e.adminToken)
	req2.Header.Set("If-None-Match", etag)
	resp2, err := http.DefaultClient.Do(req2)
	if err != nil {
		t.Fatal(err)
	}
	b2 := readAll(t, resp2)
	resp2.Body.Close()
	if resp2.StatusCode != http.StatusNotModified || len(b2) != 0 {
		t.Fatalf("期望 304，得到 %d len=%d", resp2.StatusCode, len(b2))
	}

	// 第三次不带 If-None-Match：200 且来自磁盘缓存（Komga 水位零增长）
	req3, _ := http.NewRequest("GET", e.api.URL+"/api/series/s1/thumbnail", nil)
	req3.Header.Set("Authorization", "Bearer "+e.adminToken)
	resp3, err := http.DefaultClient.Do(req3)
	if err != nil {
		t.Fatal(err)
	}
	body3 := readAll(t, resp3)
	resp3.Body.Close()
	if resp3.StatusCode != 200 || string(body3) != string(body1) {
		t.Fatalf("磁盘缓存回读不一致")
	}
	after := len(e.komga.requests("GET", "/api/v1/series/"))
	if after != before {
		t.Fatalf("后续封面请求仍打到 Komga：%d → %d（磁盘缓存未生效）", before, after)
	}
}

// reopenServer 用同一 dataDir 新建 Server（模拟重启；复用同一 mock Komga）。
func reopenServer(t *testing.T, e *testEnv) *testEnv {
	t.Helper()
	st, err := store.Open(e.dataDir)
	if err != nil {
		t.Fatalf("重开存储失败: %v", err)
	}
	secret, _ := store.EnsureJWTSecret(e.dataDir)
	am := auth.NewManager(secret)
	srv := NewServer(st, am, testLogger())
	srv.SetDataDir(e.dataDir)
	apiSrv := httptest.NewServer(srv.Router())
	t.Cleanup(func() {
		apiSrv.Close()
		st.Close()
	})
	return &testEnv{t: t, komga: e.komga, bangumi: e.bangumi, api: apiSrv,
		server: srv, store: st, dataDir: e.dataDir, adminToken: e.adminToken}
}

func readAll(t *testing.T, resp *http.Response) []byte {
	t.Helper()
	data := make([]byte, 0, 1024)
	buf := make([]byte, 4096)
	for {
		n, err := resp.Body.Read(buf)
		data = append(data, buf[:n]...)
		if err != nil {
			break
		}
	}
	return data
}
