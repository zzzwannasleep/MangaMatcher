package api

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"mime"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"komgabangumi/internal/auth"
	"komgabangumi/internal/store"
)

// ============================================================
// Mock Komga / Mock Bangumi —— 对齐 tools/mock-komga.js / mock-bangumi.js
// （桌面版 Core 层端到端测试的同一批场景基准）
// ============================================================

// capturedRequest 记录 mock 收到的请求。
type capturedRequest struct {
	Method string
	Path   string
	Query  string
	Body   []byte
	Header http.Header
}

// mockKomga 模拟 Komga API 服务器。
type mockKomga struct {
	mu        sync.Mutex
	libraries []map[string]any
	series    map[string]map[string]any
	books     map[string]map[string]any
	reqs      []capturedRequest
	url       string
}

func newMockKomga() *mockKomga {
	return &mockKomga{
		libraries: []map[string]any{{"id": "lib1", "name": "Comics", "root": "/data"}},
		series:    map[string]map[string]any{},
		books:     map[string]map[string]any{},
	}
}

// seedSeries 注入系列（metadata 全空，对齐 js mock 的 s1 初始状态）。
func (m *mockKomga) seedSeries(id, libraryID, name string) {
	m.series[id] = map[string]any{
		"id": id, "name": name, "libraryId": libraryID,
		"url": "/data/Grand Blue", "oneshot": false, "booksCount": 2, "deleted": false,
		"metadata": map[string]any{
			"status": "", "title": "", "summary": "", "publisher": "", "ageRating": nil,
			"genres": []any{}, "tags": []any{}, "links": []any{}, "alternateTitles": []any{}, "authors": []any{},
			"totalBookCount": nil,
		},
		"booksMetadata": map[string]any{"releaseDate": nil, "summary": nil, "authors": nil, "tags": nil},
	}
	m.books[id+"-b1"] = map[string]any{
		"id": id + "-b1", "name": name + " Vol.01", "seriesId": id, "number": 1, "oneshot": false,
		"metadata": map[string]any{"title": nil, "number": nil, "numberSort": 1, "summary": nil,
			"releaseDate": nil, "isbn": nil, "authors": []any{}, "tags": []any{}, "links": []any{}},
	}
	m.books[id+"-b2"] = map[string]any{
		"id": id + "-b2", "name": name + " Vol.02", "seriesId": id, "number": 2, "oneshot": false,
		"metadata": map[string]any{"title": nil, "number": nil, "numberSort": 2, "summary": nil,
			"releaseDate": nil, "isbn": nil, "authors": []any{}, "tags": []any{}, "links": []any{}},
	}
}

// requests 过滤已捕获的请求（prefix 为空 = 全部）。
func (m *mockKomga) requests(method, prefix string) []capturedRequest {
	m.mu.Lock()
	defer m.mu.Unlock()
	var out []capturedRequest
	for _, r := range m.reqs {
		if method != "" && r.Method != method {
			continue
		}
		if prefix != "" && !strings.HasPrefix(r.Path, prefix) {
			continue
		}
		out = append(out, r)
	}
	return out
}

func (m *mockKomga) jsonBody(t *testing.T, r capturedRequest) map[string]any {
	t.Helper()
	var body map[string]any
	if len(r.Body) > 0 {
		if err := json.Unmarshal(r.Body, &body); err != nil {
			t.Fatalf("解析 PATCH 体失败: %v (body=%s)", err, r.Body)
		}
	}
	return body
}

func (m *mockKomga) handler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		m.mu.Lock()
		m.reqs = append(m.reqs, capturedRequest{Method: r.Method, Path: r.URL.Path,
			Query: r.URL.RawQuery, Body: body, Header: r.Header})
		m.mu.Unlock()

		w.Header().Set("Content-Type", "application/json")
		p := r.URL.Path

		// 系统信息（test-komga 用）
		if r.Method == "GET" && p == "/api/v1/system/info" {
			json.NewEncoder(w).Encode(map[string]any{"version": "1.21.0"})
			return
		}
		if r.Method == "GET" && p == "/api/v1/libraries" {
			json.NewEncoder(w).Encode(m.libraries)
			return
		}
		// 系列搜索/列表（支持按库过滤，对齐 js mock 行为基准）
		if r.Method == "POST" && p == "/api/v1/series/list" {
			libID := ""
			var q struct {
				Condition struct {
					AllOf []struct {
						LibraryID struct {
							Operator string `json:"operator"`
							Value    string `json:"value"`
						} `json:"libraryId"`
					} `json:"allOf"`
				} `json:"condition"`
			}
			if err := json.Unmarshal(body, &q); err == nil {
				for _, c := range q.Condition.AllOf {
					if c.LibraryID.Value != "" {
						libID = c.LibraryID.Value
					}
				}
			}
			var content []map[string]any
			for _, s := range m.series {
				if libID == "" || s["libraryId"] == libID {
					content = append(content, s)
				}
			}
			if content == nil {
				content = []map[string]any{}
			}
			json.NewEncoder(w).Encode(map[string]any{"content": content,
				"totalElements": len(content), "numberOfElements": len(content)})
			return
		}
		if r.Method == "GET" && strings.HasPrefix(p, "/api/v1/series/") && strings.HasSuffix(p, "/metadata") {
			sid := strings.TrimSuffix(strings.TrimPrefix(p, "/api/v1/series/"), "/metadata")
			s := m.series[sid]
			if s == nil {
				w.WriteHeader(404)
				return
			}
			json.NewEncoder(w).Encode(s["metadata"])
			return
		}
		if r.Method == "PATCH" && strings.HasPrefix(p, "/api/v1/series/") && strings.HasSuffix(p, "/metadata") {
			sid := strings.TrimSuffix(strings.TrimPrefix(p, "/api/v1/series/"), "/metadata")
			s := m.series[sid]
			if s == nil {
				w.WriteHeader(404)
				return
			}
			var patch map[string]any
			json.Unmarshal(body, &patch)
			meta := s["metadata"].(map[string]any)
			for k, v := range patch {
				meta[k] = v
			}
			w.WriteHeader(204)
			return
		}
		if r.Method == "GET" && strings.HasPrefix(p, "/api/v1/series/") && strings.HasSuffix(p, "/thumbnail") {
			// 历史事故基准：Accept 不是 image/jpeg → 406
			if r.Header.Get("Accept") != "image/jpeg" {
				w.WriteHeader(406)
				return
			}
			w.Header().Set("Content-Type", "image/jpeg")
			w.Write([]byte{0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 4})
			return
		}
		if r.Method == "GET" && strings.HasPrefix(p, "/api/v1/series/") && strings.HasSuffix(p, "/thumbnails") {
			w.Write([]byte("[]"))
			return
		}
		if r.Method == "POST" && strings.HasPrefix(p, "/api/v1/series/") && strings.HasSuffix(p, "/thumbnails") {
			w.WriteHeader(204)
			return
		}
		if r.Method == "GET" && p == "/api/v1/series" {
			var content []map[string]any
			for _, s := range m.series {
				content = append(content, s)
			}
			json.NewEncoder(w).Encode(map[string]any{"content": content, "totalElements": len(content)})
			return
		}
		if r.Method == "POST" && p == "/api/v1/books/list" {
			var content []map[string]any
			for _, b := range m.books {
				if b["deleted"] != true {
					content = append(content, b)
				}
			}
			json.NewEncoder(w).Encode(map[string]any{"content": content,
				"totalElements": len(content), "numberOfElements": len(content)})
			return
		}
		if r.Method == "PATCH" && strings.HasPrefix(p, "/api/v1/books/") && strings.HasSuffix(p, "/metadata") {
			bid := strings.TrimSuffix(strings.TrimPrefix(p, "/api/v1/books/"), "/metadata")
			b := m.books[bid]
			if b == nil {
				w.WriteHeader(404)
				return
			}
			var patch map[string]any
			json.Unmarshal(body, &patch)
			meta := b["metadata"].(map[string]any)
			for k, v := range patch {
				meta[k] = v
			}
			w.WriteHeader(204)
			return
		}
		if r.Method == "GET" && strings.HasPrefix(p, "/api/v1/books/") && strings.HasSuffix(p, "/thumbnails") {
			w.Write([]byte("[]"))
			return
		}
		if r.Method == "POST" && strings.HasPrefix(p, "/api/v1/books/") && strings.HasSuffix(p, "/thumbnails") {
			w.WriteHeader(204)
			return
		}
		if r.Method == "GET" && p == "/api/v1/collections" {
			json.NewEncoder(w).Encode(map[string]any{"content": []any{}, "totalElements": 0})
			return
		}
		if r.Method == "POST" && p == "/api/v1/collections" {
			var c map[string]any
			json.Unmarshal(body, &c)
			c["id"] = "c1"
			w.Write([]byte(mustJSON(c)))
			return
		}
		if r.Method == "PATCH" && strings.HasPrefix(p, "/api/v1/collections/") {
			w.WriteHeader(204)
			return
		}
		if r.Method == "GET" && strings.HasPrefix(p, "/api/v1/series/") && !strings.HasSuffix(p, "/metadata") &&
			!strings.HasSuffix(p, "/thumbnails") && !strings.HasSuffix(p, "/thumbnail") {
			sid := strings.TrimPrefix(p, "/api/v1/series/")
			s := m.series[sid]
			if s == nil {
				w.WriteHeader(404)
				json.NewEncoder(w).Encode(map[string]any{"error": "not found"})
				return
			}
			w.Write([]byte(mustJSON(s)))
			return
		}
		w.WriteHeader(404)
		json.NewEncoder(w).Encode(map[string]any{"error": "no route: " + r.Method + " " + p})
	}
}

func mustJSON(v any) string {
	b, _ := json.Marshal(v)
	return string(b)
}

// mockBangumi 模拟 Bangumi v0 API。
type mockBangumi struct {
	mu        sync.Mutex
	url       string
	searches  []string
	emptyAll  bool   // v2 测试开关：搜索永远空（回退链打到百科）
	detailIDs map[int]bool
}

var imgJPEG = append([]byte{0xff, 0xd8, 0xff, 0xe0}, bytes.Repeat([]byte{7}, 200)...)

func subjectJSON() map[string]any {
	return map[string]any{
		"id": 600001, "name": "ぐらんぶる", "name_cn": "碧藍之海", "platform": "漫画",
		"summary": "  大学生・伊志田空美与表哥北原伊织一起加入大学社团「うみほの会」。\r\n  围绕潜水与饮酒的青春故事。  ",
		"volumes": 16, "eps": nil, "total_episodes": nil,
		"image": "", "images": map[string]any{
			"large": "", "medium": "", "common": "", "small": "",
		},
		"date": "2014-05-09", "nsfw": false, "age_rating": 0,
		"tags": []any{
			map[string]any{"name": "搞笑", "count": 1200},
			map[string]any{"name": "校园", "count": 900},
			map[string]any{"name": "青春", "count": 700},
			map[string]any{"name": "水下探险", "count": 300},
			map[string]any{"name": "井上堅二", "count": 200},
			map[string]any{"name": "軽小说", "count": 5},
			map[string]any{"name": "连载中", "count": 50},
		},
		"rating": map[string]any{"score": 8.6},
		"infobox": []any{
			map[string]any{"key": "中文名", "value": "碧蓝之海"},
			map[string]any{"key": "作者", "value": []any{map[string]any{"v": "井上堅二"}, map[string]any{"v": "吉岡公威"}}},
			map[string]any{"key": "作画", "value": "吉岡公威"},
			map[string]any{"key": "出版社", "value": "講談社"},
			map[string]any{"key": "连载杂志", "value": "good!アフタヌーン"},
			map[string]any{"key": "別名", "value": []any{map[string]any{"v": "Grand Blue"}, map[string]any{"v": "Grand Blue 碧藍之海"}}},
			map[string]any{"key": "状态", "value": "完结"},
			map[string]any{"key": "发售日", "value": "2014年5月9日"},
		},
	}
}

func (m *mockBangumi) handler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		p := r.URL.Path
		w.Header().Set("Content-Type", "application/json")

		if r.Method == "GET" && strings.HasPrefix(p, "/img/") {
			w.Header().Set("Content-Type", "image/jpeg")
			w.Write(imgJPEG)
			return
		}
		if r.Method == "POST" && p == "/v0/search/subjects" {
			body, _ := io.ReadAll(r.Body)
			var q struct {
				Keyword string `json:"keyword"`
			}
			json.Unmarshal(body, &q)
			m.mu.Lock()
			m.searches = append(m.searches, q.Keyword)
			empty := m.emptyAll
			m.mu.Unlock()
			if !empty && (strings.Contains(q.Keyword, "碧蓝") || strings.Contains(q.Keyword, "ぐらんぶる") ||
				strings.EqualFold(q.Keyword, "Grand Blue")) {
				subj := subjectJSON()
				// 图片 URL 指向本 mock
				images := subj["images"].(map[string]any)
				subj["image"] = m.url + "/img/main.jpg"
				images["large"] = m.url + "/img/large.jpg"
				images["medium"] = m.url + "/img/medium.jpg"
				images["common"] = m.url + "/img/common.jpg"
				json.NewEncoder(w).Encode(map[string]any{"data": []any{subj}})
			} else {
				json.NewEncoder(w).Encode(map[string]any{"data": []any{}})
			}
			return
		}
		if r.Method == "GET" && p == "/v0/subjects/600001" {
			subj := subjectJSON()
			subj["image"] = m.url + "/img/main.jpg"
			images := subj["images"].(map[string]any)
			images["large"] = m.url + "/img/large.jpg"
			images["medium"] = m.url + "/img/medium.jpg"
			images["common"] = m.url + "/img/common.jpg"
			w.Write([]byte(mustJSON(subj)))
			return
		}
		if r.Method == "GET" && p == "/v0/subjects/600001/subjects" {
			volumes := []any{
				map[string]any{
					"id": 600101, "name": "ぐらんぶる (1)", "name_cn": "碧蓝之海 1", "relation": "单行本",
					"image": m.url + "/img/vol1.jpg",
					"images": map[string]any{"large": m.url + "/img/vol1.jpg", "medium": nil, "common": nil},
				},
				map[string]any{
					"id": 600102, "name": "ぐらんぶる (2)", "name_cn": "碧蓝之海 2", "relation": "单行本",
					"image": m.url + "/img/vol2.jpg",
					"images": map[string]any{"large": m.url + "/img/vol2.jpg", "medium": nil, "common": nil},
				},
				map[string]any{
					"id": 600103, "name": "another work", "name_cn": "其他作品", "relation": "主线故事",
					"image": nil, "images": map[string]any{},
				},
			}
			w.Write([]byte(mustJSON(volumes)))
			return
		}
		if r.Method == "GET" && p == "/v0/subjects/600101" {
			w.Write([]byte(mustJSON(map[string]any{
				"id": 600101, "name": "ぐらんぶる (1)", "name_cn": "碧蓝之海 1",
				"summary": "  第1卷。  伊织大学生活开始。 ",
				"infobox": []any{
					map[string]any{"key": "发售日", "value": "2014年5月9日"},
					map[string]any{"key": "ISBN", "value": "9784063883237"},
				},
			})))
			return
		}
		if r.Method == "GET" && p == "/v0/subjects/600102" {
			w.Write([]byte(mustJSON(map[string]any{
				"id": 600102, "name": "ぐらんぶる (2)", "name_cn": "碧蓝之海 2",
				"summary": "第2卷。",
				"infobox": []any{
					map[string]any{"key": "发售日", "value": "2014年11月21日"},
					map[string]any{"key": "ISBN", "value": "9784063885231"},
				},
			})))
			return
		}
		w.WriteHeader(404)
		json.NewEncoder(w).Encode(map[string]any{"error": "no route " + r.Method + " " + p})
	}
}

// ============================================================
// 测试环境
// ============================================================

type testEnv struct {
	t          *testing.T
	komga      *mockKomga
	bangumi    *mockBangumi
	api        *httptest.Server
	adminToken string
	server     *Server       // 底层 Server（v2 注入 override/数据目录用）
	store      *store.Store  // v2 状态库直写用
	dataDir    string
}

func newTestEnv(t *testing.T) *testEnv {
	t.Helper()
	// 绝不出网：E-Hentai 指向本地死端口（墙内直连表站会等到超时，拖死测试）。
	stubEhSource(t)
	km := newMockKomga()
	km.seedSeries("s1", "lib1", "[碧蓝之海][井上堅二×吉岡公威][Vol.01-Vol.05]")

	bm := &mockBangumi{}
	komgaSrv := httptest.NewServer(km.handler())
	bangumiSrv := httptest.NewServer(bm.handler())
	bm.url = bangumiSrv.URL

	dataDir := t.TempDir()
	st, err := store.Open(dataDir)
	if err != nil {
		t.Fatalf("打开存储失败: %v", err)
	}
	hash, _ := auth.HashPassword("admin123")
	if _, err := st.CreateUser("admin", hash, "admin", nil); err != nil {
		t.Fatalf("创建 admin 失败: %v", err)
	}
	secret, _ := store.EnsureJWTSecret(dataDir)
	am := auth.NewManager(secret)
	log := testLogger()
	srv := NewServer(st, am, log)
	srv.SetDataDir(dataDir)
	apiSrv := httptest.NewServer(srv.Router())

	env := &testEnv{t: t, komga: km, bangumi: bm, api: apiSrv, server: srv, store: st, dataDir: dataDir}
	env.adminToken = env.login("admin", "admin123")

	// 设置指向 mock（关闭限速，测试用）
	env.patchSettings(map[string]any{
		"komgaUrl":              komgaSrv.URL,
		"bangumiApiBase":        bangumiSrv.URL,
		"bangumiMinIntervalMs":  0,
		"concurrency":           2,
		"dryRunDefault":         false,
		"manualCollection":      true,
		"fetchVolumes":          true,
		"withCovers":            true,
	})

	t.Cleanup(func() {
		apiSrv.Close()
		bangumiSrv.Close()
		komgaSrv.Close()
		st.Close()
	})
	return env
}

func (e *testEnv) do(method, path, token string, body any) (*http.Response, []byte) {
	e.t.Helper()
	var rd io.Reader
	if body != nil {
		rd = strings.NewReader(mustJSON(body))
	}
	req, _ := http.NewRequest(method, e.api.URL+path, rd)
	req.Header.Set("Content-Type", "application/json")
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		e.t.Fatalf("%s %s 失败: %v", method, path, err)
	}
	defer resp.Body.Close()
	data, _ := io.ReadAll(resp.Body)
	// 返回的 Response Body 已读完，用 data 代替
	resp.Body = io.NopCloser(bytes.NewReader(data))
	return resp, data
}

func (e *testEnv) login(user, pass string) string {
	e.t.Helper()
	resp, data := e.do("POST", "/api/auth/login", "", map[string]string{"username": user, "password": pass})
	if resp.StatusCode != 200 {
		e.t.Fatalf("登录 %s 失败: %d %s", user, resp.StatusCode, data)
	}
	var out struct {
		Token string `json:"token"`
	}
	json.Unmarshal(data, &out)
	return out.Token
}

func (e *testEnv) patchSettings(patch map[string]any) {
	e.t.Helper()
	resp, data := e.do("PATCH", "/api/settings", e.adminToken, patch)
	if resp.StatusCode != 200 {
		e.t.Fatalf("PATCH /api/settings 失败: %d %s", resp.StatusCode, data)
	}
}

type jobView struct {
	ID     string `json:"id"`
	Status string `json:"status"`
	Total  int    `json:"total"`
	Done   int    `json:"done"`
	OK     int    `json:"ok"`
	Failed int    `json:"failed"`
	Items  []struct {
		SeriesID   string            `json:"seriesId"`
		SeriesName string            `json:"seriesName"`
		Status     string            `json:"status"`
		Message    string            `json:"message"`
		SubjectID  int               `json:"subjectId"`
		MatchTitle string            `json:"matchTitle"`
		Changed    map[string][2]any `json:"changed"`
	} `json:"items"`
}

// submitJob 创建任务并等待结束。
func (e *testEnv) submitJob(t *testing.T, token, libraryID string, seriesIDs []string, dryRun bool) jobView {
	t.Helper()
	payload := map[string]any{"libraryId": libraryID, "seriesIds": seriesIDs, "dryRun": dryRun}
	resp, data := e.do("POST", "/api/jobs", token, payload)
	if resp.StatusCode != 200 {
		t.Fatalf("创建任务失败: %d %s", resp.StatusCode, data)
	}
	var created struct {
		ID string `json:"id"`
	}
	json.Unmarshal(data, &created)
	if created.ID == "" {
		t.Fatalf("任务创建返回为空: %s", data)
	}

	deadline := time.Now().Add(60 * time.Second)
	for {
		resp, data := e.do("GET", "/api/jobs/"+created.ID, token, nil)
		if resp.StatusCode != 200 {
			t.Fatalf("查询任务失败: %d %s", resp.StatusCode, data)
		}
		var job jobView
		json.Unmarshal(data, &job)
		if job.Status != "running" {
			return job
		}
		if time.Now().After(deadline) {
			t.Fatalf("任务超时未结束: %s", data)
		}
		time.Sleep(50 * time.Millisecond)
	}
}

func mustJSONString(v any) string { return mustJSON(v) }

var _ = fmt.Sprintf
var _ = mime.FormatMediaType