package api

// v2_eh_test.go — E-Hentai（表站 e-hentai.org，免登录）源接线的**独立验证**（task-6）。
//
// 作者≠验证者：本文件只做黑盒/集成验证，不改任何实现文件；断言全部基于
//   HTTP 层可观测行为（候选 wire 字段、详情 wire 字段、mock Komga 收到的原始 PATCH 体、任务条目状态）。
//
// 三条铁律：
//  1. **绝不出网**：EH 指向本地 httptest；Mox/MangaDex/百科顶到 127.0.0.1:1（连接立刻被拒）；
//     Bangumi 走 newTestEnv 的本地 mock；BW 索引不存在 → sc.BW 为 nil。
//  2. `ehBaseOverride` / `ehPaceOverrideMs`（以及其它源基址）用完必须 t.Cleanup 还原，
//     否则会污染同包其它测试（v2_test.go 的 stubEhSource 是同一模式）。
//  3. 12 条上限断言用 scrape.EhTagLimit（全仓唯一定义），不写字面量 12。

import (
	"encoding/json"
	"fmt"
	"html"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"komgabangumi/internal/ehtags"
	"komgabangumi/internal/scrape"
	"komgabangumi/internal/sources"
)

// ============================================================
// 本地假表站（Go httptest 版；浏览器联调的零依赖版是 tools/mock-ehentai.js）
// ============================================================

// ehTagFixture 合成 EhTagTranslation 词库（结构照 db.text.json：data[].namespace + data[].data[tag]={name}）。
// 关键点：artist:asanagi → 朝凪（task-6 要求的「艺术家:朝凪」样例）；
// female:ok 的 name 与 tag 相同 → ehtags.Translate 视为「未翻译」返回 false（对抗性用例）。
const ehTagFixture = `{
  "repo": "EhTagTranslation/Database", "version": 7,
  "data": [
    {"namespace":"artist","frontMatters":{"name":"艺术家"},"data":{
      "asanagi":{"name":"朝凪"},"artist1":{"name":"画师一"},"artist2":{"name":"画师二"}}},
    {"namespace":"group","frontMatters":{"name":"团队"},"data":{"group1":{"name":"社团一"}}},
    {"namespace":"parody","frontMatters":{"name":"原作"},"data":{
      "azur lane":{"name":"碧蓝航线"},"parody1":{"name":"原作一"}}},
    {"namespace":"character","frontMatters":{"name":"角色"},"data":{
      "char1":{"name":"角色一"},"char2":{"name":"角色二"}}},
    {"namespace":"female","frontMatters":{"name":"女性"},"data":{
      "fem1":{"name":"女性一"},"fem2":{"name":"女性二"},"fem3":{"name":"女性三"},"ok":{"name":"ok"}}},
    {"namespace":"male","frontMatters":{"name":"男性"},"data":{
      "male1":{"name":"男性一"},"male2":{"name":"男性二"}}},
    {"namespace":"mixed","frontMatters":{"name":"混合"},"data":{"mixed1":{"name":"混合一"}}},
    {"namespace":"other","frontMatters":{"name":"其他"},"data":{
      "other1":{"name":"其他一"},"other2":{"name":"其他二"}}},
    {"namespace":"language","frontMatters":{"name":"语言"},"data":{
      "chinese":{"name":"中文"},"japanese":{"name":"日文"}}},
    {"namespace":"category","frontMatters":{"name":"分类"},"data":{"doujinshi":{"name":"同人志"}}}
  ]
}`

// useEhTagFixture 装载合成词库（ehtags 是进程内单例；本包其它测试不用它，故安全）。
func useEhTagFixture(t *testing.T) {
	t.Helper()
	p := filepath.Join(t.TempDir(), "db.text.json")
	if err := os.WriteFile(p, []byte(ehTagFixture), 0o644); err != nil {
		t.Fatalf("写合成 EH 词库失败: %v", err)
	}
	if err := ehtags.Use(p); err != nil {
		t.Fatalf("装载合成 EH 词库失败: %v", err)
	}
	if !ehtags.Ready() {
		t.Fatal("合成 EH 词库未就绪")
	}
	t.Cleanup(func() { _ = ehtags.Use(filepath.Join(t.TempDir(), "not-exists.json")) })
}

type ehReqSnapshot struct {
	Method  string
	Path    string
	Query   string
	Headers http.Header
}

// mockEhV2 本地假表站：/ 搜索页、/api.php gdata、/g/<gid>/ 画廊页（补 token）、/w/ 图床。
type mockEhV2 struct {
	mu       sync.Mutex
	base     string
	searches []string
	gdata    []map[string]any
	reqs     []ehReqSnapshot

	gid   int
	token string

	echoTitle    bool // 搜索标题回显 f_search（默认 true → 任意系列名都能被精确匹配）
	galleryTitle string
	titleJpn     string
	category     string
	uploader     string
	posted       any
	filecount    any
	filesize     any
	rating       any
	tags         []string
	thumbPath    string

	status      int    // 非 0：所有请求返回该状态码（模拟 500）
	gdataErr    string // 非空：gdata 返回 {"error":...}
	gdataNone   bool   // gdata 返回 {"gmetadata":[]}
	noTokenPage bool   // /g/<gid>/ 页面不带规范链接 → 无法补 token
}

func newMockEhV2() *mockEhV2 {
	return &mockEhV2{
		gid: 4264304, token: "aabbccdd01",
		echoTitle:    true,
		galleryTitle: "Mesu Kyoushi 3",
		titleJpn:     "雌教師3",
		category:     "Doujinshi",
		uploader:     "asanagi",
		posted:       "1600000000",
		filecount:    "24",
		filesize:     "123456789",
		rating:       "4.32",
		thumbPath:    "/w/00/cover.jpg",
	}
}

// startMockEhV2 起服务。
// 注意顺序：httptest.NewUnstartedServer 时 srv.URL 还是空串，必须 Start() 之后再取 URL；
// 踩过一次：先取 URL 得到 ""，ehBaseOverride="" 等于「用官方表站」，测试会真的去打 e-hentai.org。
func startMockEhV2(t *testing.T) *mockEhV2 {
	t.Helper()
	m := newMockEhV2()
	srv := httptest.NewUnstartedServer(m.handler())
	srv.Start()
	t.Cleanup(srv.Close)
	m.mu.Lock()
	m.base = srv.URL
	m.mu.Unlock()
	if !strings.HasPrefix(m.base, "http://127.0.0.1:") {
		t.Fatalf("假表站基址异常: %q", m.base)
	}
	return m
}

func (m *mockEhV2) searchPage(term string) string {
	m.mu.Lock()
	m.searches = append(m.searches, term)
	title := m.galleryTitle
	if m.echoTitle {
		title = "[TestCircle (Asanagi)] " + term
	}
	thumb := ""
	if m.thumbPath != "" {
		thumb = `<img style="height:360px" alt="thumb" src="` + m.base + m.thumbPath + `">`
	}
	base, gid, token, up := m.base, m.gid, m.token, m.uploader
	m.mu.Unlock()
	return fmt.Sprintf(`<!DOCTYPE html><html><head><title>E-Hentai Galleries</title></head><body>
<table class="itg"><tr>
<td class="gl1c glcat"><div class="cn ct2">Doujinshi</div></td>
<td class="gl2c"><div class="glthumb" id="it%d"><div>%s</div></div></td>
<td class="gl3c glname"><a href="%s/g/%d/%s/"><div class="glink">%s</div></a></td>
<td class="gl4c glhide"><div><a href="%s/uploader/%s">%s</a></div><div>24 pages</div></td>
</tr></table></body></html>`, gid, thumb, base, gid, token, html.EscapeString(title), base, up, up)
}

func (m *mockEhV2) gdataJSON(gid int, token string) string {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.gdataErr != "" {
		b, _ := json.Marshal(m.gdataErr)
		return `{"error":` + string(b) + `}`
	}
	if m.gdataNone {
		return `{"gmetadata":[]}`
	}
	j := func(v any) string { b, _ := json.Marshal(v); return string(b) }
	tags := m.tags
	if tags == nil {
		tags = []string{}
	}
	tb, _ := json.Marshal(tags)
	thumb := ""
	if m.thumbPath != "" {
		thumb = m.base + m.thumbPath
	}
	return fmt.Sprintf(`{"gmetadata":[{"gid":%d,"token":%s,"title":%s,"title_jpn":%s,"category":%s,`+
		`"thumb":%s,"uploader":%s,"posted":%s,"filecount":%s,"filesize":%s,"rating":%s,"tags":%s}]}`,
		gid, j(token), j(m.galleryTitle), j(m.titleJpn), j(m.category), j(thumb), j(m.uploader),
		j(m.posted), j(m.filecount), j(m.filesize), j(m.rating), string(tb))
}

func (m *mockEhV2) handler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		m.mu.Lock()
		m.reqs = append(m.reqs, ehReqSnapshot{Method: r.Method, Path: r.URL.Path,
			Query: r.URL.RawQuery, Headers: r.Header.Clone()})
		status := m.status
		m.mu.Unlock()
		if status != 0 {
			w.WriteHeader(status)
			return
		}
		switch {
		case r.Method == http.MethodPost && r.URL.Path == "/api.php":
			raw, _ := io.ReadAll(r.Body)
			gid, token := 0, ""
			var body map[string]any
			if json.Unmarshal(raw, &body) == nil {
				m.mu.Lock()
				m.gdata = append(m.gdata, body)
				m.mu.Unlock()
				if list, ok := body["gidlist"].([]any); ok && len(list) > 0 {
					if pair, ok := list[0].([]any); ok && len(pair) == 2 {
						if f, ok := pair[0].(float64); ok {
							gid = int(f)
						}
						token, _ = pair[1].(string)
					}
				}
			}
			w.Header().Set("Content-Type", "application/json; charset=utf-8")
			io.WriteString(w, m.gdataJSON(gid, token))
		case r.URL.Path == "/":
			w.Header().Set("Content-Type", "text/html; charset=utf-8")
			io.WriteString(w, m.searchPage(r.URL.Query().Get("f_search")))
		case strings.HasPrefix(r.URL.Path, "/w/"):
			w.Header().Set("Content-Type", "image/jpeg")
			w.Write(ehMockJPEG)
		case strings.HasPrefix(r.URL.Path, "/g/"):
			gid := strings.Trim(strings.TrimPrefix(r.URL.Path, "/g/"), "/")
			m.mu.Lock()
			token, noLink, base := m.token, m.noTokenPage, m.base
			m.mu.Unlock()
			w.Header().Set("Content-Type", "text/html; charset=utf-8")
			if noLink {
				io.WriteString(w, "<!DOCTYPE html><html><body><p>no canonical link</p></body></html>")
				return
			}
			fmt.Fprintf(w, `<!DOCTYPE html><html><body><a href="%s/g/%s/%s/">gallery</a></body></html>`, base, gid, token)
		default:
			w.WriteHeader(404)
		}
	}
}

var ehMockJPEG = []byte{0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46, 0x00, 0x01}

// lastBaikeMock 记录最近一次 ehEnv 创建的百科 mock（本包串行运行，够用）。
// 用途：G 断言「EH 失败后链路确实继续走到了百科」——只看 job 状态无法区分「走到百科但没命中」和「根本没走到」。
var lastBaikeMock *mockBaikeV2

// ============================================================
// 测试环境：所有源都顶到本地，EH → 假表站
// ============================================================

// setEhTestOverrides 把 EH 指向 mock、其它源指向本地 mock（绝不出网）。
// 必须在 newTestEnv 之后调用（newTestEnv 内部已 stubEhSource；t.Cleanup 逆序执行 → 本函数先还原）。
func setEhTestOverrides(t *testing.T, ehBase, moxBase, mdBase, baikeBase string) {
	t.Helper()
	// 硬闸门：四个基址都必须显式指向本地。空串 = 用官方站点（EH 空串 = https://e-hentai.org）→ 出网 = 测试 bug。
	for name, v := range map[string]string{"EH": ehBase, "Mox": moxBase, "MangaDex": mdBase, "百科": baikeBase} {
		if v == "" || !strings.Contains(v, "127.0.0.1") {
			t.Fatalf("%s 基址必须指向本地 mock，实际 %q（空串/官方域名会让测试出网）", name, v)
		}
	}
	oldEhBase, oldEhPace := ehBaseOverride, ehPaceOverrideMs
	oldMox, oldMD, oldBaike, oldMoxPace := moxBaseOverride, mangaDexBaseOverride, baikeBaseOverride, moxPaceOverrideMs
	ehBaseOverride, ehPaceOverrideMs = ehBase, 1 // 1ms：默认 1100ms 节流会让套件变慢
	moxBaseOverride, mangaDexBaseOverride, baikeBaseOverride, moxPaceOverrideMs = moxBase, mdBase, baikeBase, 1
	t.Cleanup(func() {
		ehBaseOverride, ehPaceOverrideMs = oldEhBase, oldEhPace
		moxBaseOverride, mangaDexBaseOverride, baikeBaseOverride, moxPaceOverrideMs = oldMox, oldMD, oldBaike, oldMoxPace
	})
}

// ehEnv 建一个「只有 EH 一个可能命中的源」的环境：Bangumi 永远空、AI/MangaDex 关闭、// BW 索引不存在、Mox 本地空结果（本地 mock 而不是死端口 —— 死端口会触发 Mox 客户端 2s/3s 重试，白等 5 秒）。
func ehEnv(t *testing.T, withCovers bool, baikeEnabled bool) (*testEnv, *mockEhV2) {
	t.Helper()
	eh := startMockEhV2(t)
	moxSrv := httptest.NewServer((&mockMoxV2{empty: true}).handler())
	t.Cleanup(moxSrv.Close)
	mdSrv := httptest.NewServer((&mockMangaDexV2{empty: true}).handler())
	t.Cleanup(mdSrv.Close)
	bkMock := &mockBaikeV2{}
	bkSrv := httptest.NewServer(bkMock.handler())
	t.Cleanup(bkSrv.Close)
	testBaseStore[bkSrv.URL] = "baike"
	t.Cleanup(func() { delete(testBaseStore, bkSrv.URL) })
	lastBaikeMock = bkMock // 供 G 断言「EH 挂了之后链真的走到了百科」

	env := newTestEnv(t)
	setEhTestOverrides(t, eh.base, moxSrv.URL, mdSrv.URL, bkSrv.URL)
	env.bangumi.mu.Lock()
	env.bangumi.emptyAll = true
	env.bangumi.mu.Unlock()
	env.patchSettings(map[string]any{
		"dryRunDefault":   false,
		"withCovers":      withCovers,
		"concurrency":     1,
		"ai":              map[string]any{"enabled": false},
		"mangaDexEnabled": false,
		"baikeEnabled":    baikeEnabled,
		"ehEnabled":       true,
	})
	return env, eh
}

// seedEhOnlySeries 把 mock Komga 清成「只有一个目标系列 + 一册」。
// 必要性：mock 的 POST /books/list 忽略 seriesId 过滤，多系列时 books[0] 会落到别的系列上。
func seedEhOnlySeries(t *testing.T, env *testEnv, id, name string, meta map[string]any) {
	t.Helper()
	env.komga.mu.Lock()
	defer env.komga.mu.Unlock()
	env.komga.series = map[string]map[string]any{}
	env.komga.books = map[string]map[string]any{}
	env.komga.seedSeries(id, "lib1", name)
	s := env.komga.series[id]
	s["url"] = "/data/" + name
	m := s["metadata"].(map[string]any)
	for k, v := range meta {
		m[k] = v
	}
	delete(env.komga.books, id+"-b2")
	env.komga.reqs = nil
}

func ehSeriesMeta(t *testing.T, env *testEnv, id string) map[string]any {
	t.Helper()
	env.komga.mu.Lock()
	defer env.komga.mu.Unlock()
	s := env.komga.series[id]
	if s == nil {
		t.Fatalf("mock Komga 里没有系列 %s", id)
	}
	m, _ := s["metadata"].(map[string]any)
	return m
}

func ehBookMeta(t *testing.T, env *testEnv, id string) map[string]any {
	t.Helper()
	env.komga.mu.Lock()
	defer env.komga.mu.Unlock()
	b := env.komga.books[id]
	if b == nil {
		t.Fatalf("mock Komga 里没有书 %s", id)
	}
	m, _ := b["metadata"].(map[string]any)
	return m
}

// ehPatches 取 mock Komga 收到的写请求（PATCH/PUT/DELETE = 真写；POST /list 是读，不算）。
func ehPatches(env *testEnv) []capturedRequest {
	var out []capturedRequest
	for _, r := range env.komga.requests("", "") {
		switch r.Method {
		case "PATCH", "PUT", "DELETE":
			out = append(out, r)
		}
	}
	return out
}

// ehJobView 本文件专用任务视图（jobView 没有 Source 字段，不改别人的文件）。
type ehJobView struct {
	ID     string `json:"id"`
	Status string `json:"status"`
	DryRun bool   `json:"dryRun"`
	Total  int    `json:"total"`
	OK     int    `json:"ok"`
	Failed int    `json:"failed"`
	Items  []struct {
		SeriesID   string           `json:"seriesId"`
		Status     string           `json:"status"`
		Message    string           `json:"message"`
		Source     string           `json:"source"`
		MatchTitle string           `json:"matchTitle"`
		Changed    map[string][]any `json:"changed"`
	} `json:"items"`
}

func ehSubmitJob(t *testing.T, env *testEnv, seriesIDs []string, dryRun bool) ehJobView {
	t.Helper()
	resp, data := env.do("POST", "/api/jobs", env.adminToken, map[string]any{
		"libraryId": "lib1", "seriesIds": seriesIDs, "dryRun": dryRun})
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
	deadline := time.Now().Add(120 * time.Second)
	for {
		resp, data := env.do("GET", "/api/jobs/"+created.ID, env.adminToken, nil)
		if resp.StatusCode != 200 {
			t.Fatalf("查询任务失败: %d %s", resp.StatusCode, data)
		}
		var job ehJobView
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

// ehDetail 调 GET /api/match/detail?source=ehentai&id=…（id 里的 "/" 必须转义）。
func ehDetail(t *testing.T, env *testEnv, id string) (int, map[string]any, string) {
	t.Helper()
	resp, data := env.do("GET", "/api/match/detail?source=ehentai&id="+url.QueryEscape(id), env.adminToken, nil)
	var body map[string]any
	json.Unmarshal(data, &body)
	msg, _ := body["error"].(string)
	return resp.StatusCode, body, msg
}

func ehStrings(v any) []string {
	list, _ := v.([]any)
	out := make([]string, 0, len(list))
	for _, x := range list {
		s, _ := x.(string)
		out = append(out, s)
	}
	return out
}

// 画廊默认元数据（真实形状）：一个画廊 = 一册，标题是英文/罗马字 + 日文原名。
func ehGalleryTags(tags ...string) []string { return tags }

// ============================================================
// A. 候选搜索：wire 字段 Source="EH"、EhID="gid/token"
// ============================================================

func TestV2EhCandidatesWire(t *testing.T) {
	env, eh := ehEnv(t, false, false)
	seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] 朝凪测试画廊", nil)
	useEhTagFixture(t)

	const q = "朝凪测试画廊"
	resp, data := env.do("GET", "/api/series/eh1/match/candidates?q="+url.QueryEscape(q), env.adminToken, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("候选接口应 200，实际 %d %s", resp.StatusCode, data)
	}
	var out struct {
		Groups map[string][]struct {
			ID       string `json:"id"`
			Title    string `json:"title"`
			OriTitle string `json:"oriTitle"`
			Author   string `json:"author"`
			Aliases  string `json:"aliases"`
			CoverURL string `json:"coverUrl"`
			Source   string `json:"source"`
		} `json:"groups"`
	}
	if err := json.Unmarshal(data, &out); err != nil {
		t.Fatalf("解析候选响应失败: %v (%s)", err, data)
	}
	list, ok := out.Groups["ehentai"]
	if !ok || len(list) == 0 {
		t.Fatalf("候选分组应有 ehentai（前端 Tab key），实际 groups=%v", out.Groups)
	}
	got := list[0]
	if got.Source != sources.EhSource {
		t.Errorf("wire source 应为 %q，实际 %q", sources.EhSource, got.Source)
	}
	if want := sources.EhID(eh.gid, eh.token); got.ID != want {
		t.Errorf("wire id 应为 %q（gid/token），实际 %q", want, got.ID)
	}
	if got.Title != q {
		t.Errorf("候选标题应回显搜索词 %q，实际 %q", q, got.Title)
	}
	if got.Author != "TestCircle (Asanagi)" {
		t.Errorf("候选作者应取首个方括号段，实际 %q", got.Author)
	}
	if got.CoverURL != eh.base+eh.thumbPath {
		t.Errorf("候选封面应为缩略图 %q，实际 %q", eh.base+eh.thumbPath, got.CoverURL)
	}
	if !strings.Contains(got.Aliases, "TestCircle (Asanagi)") {
		t.Errorf("候选别名应为原始标题，实际 %q", got.Aliases)
	}
	// 冻结事实：搜索走 GET {base}/?f_search=…&advsearch=1&f_sfu=on&f_sft=on&f_sfl=on
	eh.mu.Lock()
	reqs := append([]ehReqSnapshot(nil), eh.reqs...)
	searches := append([]string(nil), eh.searches...)
	eh.mu.Unlock()
	if len(searches) == 0 {
		t.Fatal("假表站没收到任何搜索请求")
	}
	searchQ := ""
	for _, r := range reqs {
		if r.Path == "/" {
			searchQ = r.Query
			break
		}
	}
	for _, want := range []string{"advsearch=1", "f_sfu=on", "f_sft=on", "f_sfl=on"} {
		if !strings.Contains(searchQ, want) {
			t.Errorf("搜索 query 应含 %q，实际 %q", want, searchQ)
		}
	}
	t.Logf("候选命中：id=%s title=%q author=%q cover=%s", got.ID, got.Title, got.Author, got.CoverURL)
	t.Logf("假表站收到搜索 query：%s", searchQ)
}

// ============================================================
// B. 详情映射：中文标签 / links / releaseDate / language
// ============================================================

func TestV2EhChainMapsMetadata(t *testing.T) {
	env, eh := ehEnv(t, false, false)
	useEhTagFixture(t)
	seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)

	eh.mu.Lock()
	eh.tags = ehGalleryTags("artist:asanagi", "artist:nobody", "parody:azur lane", "female:ok", "language:chinese")
	eh.mu.Unlock()

	job := ehSubmitJob(t, env, []string{"eh1"}, false)
	if job.Status != "done" || len(job.Items) != 1 {
		t.Fatalf("任务应 done 且 1 条：%+v", job)
	}
	item := job.Items[0]
	if item.Status != "ok" {
		t.Fatalf("EH 命中后条目应 ok，实际 %q（message=%q）", item.Status, item.Message)
	}
	if item.Source != "EH" {
		t.Errorf("任务条目 source 应为 EH，实际 %q", item.Source)
	}

	meta := ehSeriesMeta(t, env, "eh1")
	tags := ehStrings(meta["tags"])
	wantTags := []string{"艺术家:朝凪", "艺术家:nobody", "原作:碧蓝航线", "女性:ok", "语言:中文", "MACHINE"}
	if strings.Join(tags, "|") != strings.Join(wantTags, "|") {
		t.Errorf("中文标签映射不符\n got=%v\nwant=%v", tags, wantTags)
	}
	if meta["language"] != "zh" {
		t.Errorf("language:chinese 应映射为 zh，实际 %v", meta["language"])
	}
	if meta["releaseDate"] != "2020-09-13" {
		t.Errorf("posted=1600000000 应映射为 2020-09-13，实际 %v", meta["releaseDate"])
	}
	if meta["title"] != "Mesu Kyoushi 3" {
		t.Errorf("系列标题应取 gdata title，实际 %v", meta["title"])
	}
	links, _ := meta["links"].([]any)
	found := false
	for _, l := range links {
		lm, _ := l.(map[string]any)
		if lm["label"] == "EH" && lm["url"] == eh.base+"/g/4264304/aabbccdd01/" {
			found = true
		}
	}
	if !found {
		t.Errorf("links 应追加 {label:EH, url:画廊页}，实际 %v", links)
	}
	alt, _ := meta["alternateTitles"].([]any)
	if len(alt) == 0 {
		t.Errorf("title_jpn 应写入 alternateTitles，实际 %v", meta["alternateTitles"])
	} else if am, _ := alt[0].(map[string]any); !strings.Contains(fmt.Sprint(am["title"]), "雌教師3") {
		t.Errorf("alternateTitles 应含日文原名，实际 %v", alt[0])
	}

	// 画师标签 → 书级作者（role=Artist）；未收录的 artist:nobody 按「回落原始 tag 名」也成为一个作者
	book := ehBookMeta(t, env, "eh1-b1")
	authors, _ := book["authors"].([]any)
	wantAuthors := []struct{ name, role string }{{"朝凪", "Artist"}, {"nobody", "Artist"}}
	if len(authors) != len(wantAuthors) {
		t.Fatalf("artist 标签应写 %d 个书级作者（含未收录回落），实际 %v", len(wantAuthors), authors)
	}
	for i, w := range wantAuthors {
		am, _ := authors[i].(map[string]any)
		if am["name"] != w.name || am["role"] != w.role {
			t.Errorf("第 %d 个书级作者应为 {%s %s}，实际 %v", i, w.name, w.role, authors[i])
		}
	}

	// 表站纯度（运行时证伪）：所有请求不得带 Cookie，也不得出现 exhentai
	eh.mu.Lock()
	reqs := append([]ehReqSnapshot(nil), eh.reqs...)
	eh.mu.Unlock()
	for _, r := range reqs {
		if c := r.Headers.Get("Cookie"); c != "" {
			t.Errorf("EH 请求带了 Cookie（表站必须免登录）：%s %s Cookie=%q", r.Method, r.Path, c)
		}
		if strings.Contains(strings.ToLower(r.Path+"?"+r.Query), "exhentai") {
			t.Errorf("EH 请求打到了里站：%s %s?%s", r.Method, r.Path, r.Query)
		}
	}
	t.Logf("系列元数据：title=%v language=%v releaseDate=%v tags=%v", meta["title"], meta["language"], meta["releaseDate"], tags)
	t.Logf("links=%v", links)
	t.Logf("EH 请求数=%d（全部无 Cookie）", len(reqs))
}

// ============================================================
// C. 12 条上限 + 命名空间优先级截断
// ============================================================

// ehCapTags 17 条标签，**故意按命名空间逆序给**（category→…→artist），命名空间内部保持自然顺序。
// 这样「写库结果是否按 artist>group>parody>character>female>male>mixed>other>language>category」
// 就不是原始顺序的巧合，而是优先级真的生效。
func ehCapTags() []string {
	return []string{
		"category:doujinshi",
		"language:chinese", "language:japanese",
		"other:other1", "other:other2",
		"mixed:mixed1",
		"male:male1", "male:male2",
		"female:fem1", "female:fem2", "female:fem3",
		"character:char1", "character:char2",
		"parody:parody1",
		"group:group1",
		"artist:artist1", "artist:artist2",
	}
}

// ehCapWant 17 条候选 → 按优先级取满 12 条（female 有 3 条，所以 mixed 之后就到上限，
// other/language/category 整段被截掉 —— 这是「优先级」的必然结果，不是丢数据）。
var ehCapWant = []string{
	"艺术家:画师一", "艺术家:画师二", "团队:社团一", "原作:原作一", "角色:角色一", "角色:角色二",
	"女性:女性一", "女性:女性二", "女性:女性三", "男性:男性一", "男性:男性二", "混合:混合一",
}

// ehCapCut 超上限、整段被截掉的标签（other 及之后的命名空间）。
var ehCapCut = []string{"其他:其他一", "其他:其他二", "语言:中文", "语言:日文", "分类:同人志"}

func TestV2EhTagCap12Priority(t *testing.T) {
	if scrape.EhTagLimit != 12 {
		t.Fatalf("上限常量应为 12，实际 %d", scrape.EhTagLimit)
	}
	env, eh := ehEnv(t, false, false)
	useEhTagFixture(t)
	seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)
	eh.mu.Lock()
	eh.tags = ehCapTags() // 17 条 > 12
	eh.mu.Unlock()

	job := ehSubmitJob(t, env, []string{"eh1"}, false)
	if job.Items[0].Status != "ok" {
		t.Fatalf("条目应 ok：%+v", job.Items[0])
	}
	tags := ehStrings(ehSeriesMeta(t, env, "eh1")["tags"])
	if len(tags) != len(ehCapWant)+1 { // +MACHINE
		t.Fatalf("标签数应为 %d（12 EH + MACHINE），实际 %d：%v", len(ehCapWant)+1, len(tags), tags)
	}
	if strings.Join(tags[:len(ehCapWant)], "|") != strings.Join(ehCapWant, "|") {
		t.Errorf("前 12 条应按优先级截断\n got=%v\nwant=%v", tags[:len(ehCapWant)], ehCapWant)
	}
	if tags[len(ehCapWant)] != "MACHINE" {
		t.Errorf("末尾应为 MACHINE，实际 %q", tags[len(ehCapWant)])
	}
	for _, cut := range ehCapCut {
		if contains(tags, cut) {
			t.Errorf("超上限的标签 %q 不该写库：%v", cut, tags)
		}
	}
	// 上限只管标签：language 字段仍应写 zh
	if m := ehSeriesMeta(t, env, "eh1"); m["language"] != "zh" {
		t.Errorf("language 字段不该被标签上限影响，实际 %v", m["language"])
	}
	t.Logf("17 条候选（命名空间逆序给）→ 写库 %d 条：%v", len(tags), tags)
}

// C-对抗：系列已有 30 条标签时，上限仍是 12，且已有标签不被删、不占额度。
func TestV2EhTagCapWith30ExistingTags(t *testing.T) {
	env, eh := ehEnv(t, false, false)
	useEhTagFixture(t)
	existing := make([]string, 0, 30)
	for i := 1; i <= 30; i++ {
		existing = append(existing, fmt.Sprintf("旧标签%02d", i))
	}
	seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3",
		map[string]any{"tags": existing})
	eh.mu.Lock()
	eh.tags = ehCapTags()
	eh.mu.Unlock()

	job := ehSubmitJob(t, env, []string{"eh1"}, false)
	if job.Items[0].Status != "ok" {
		t.Fatalf("条目应 ok：%+v", job.Items[0])
	}
	tags := ehStrings(ehSeriesMeta(t, env, "eh1")["tags"])
	if len(tags) != 30+len(ehCapWant)+1 {
		t.Fatalf("应为 30 旧 + 12 EH + MACHINE = %d，实际 %d：%v", 30+len(ehCapWant)+1, len(tags), tags)
	}
	for i, want := range existing {
		if tags[i] != want {
			t.Fatalf("已有标签第 %d 条被改动：got=%q want=%q（全量 %v）", i, tags[i], want, tags)
		}
	}
	if got := tags[30 : 30+len(ehCapWant)]; strings.Join(got, "|") != strings.Join(ehCapWant, "|") {
		t.Errorf("EH 标签应接在已有标签之后且保持优先级\n got=%v\nwant=%v", got, ehCapWant)
	}
	if tags[len(tags)-1] != "MACHINE" {
		t.Errorf("末尾应为 MACHINE，实际 %q", tags[len(tags)-1])
	}
	ehCount := 0
	for _, tg := range tags {
		if strings.Contains(tg, ":") {
			ehCount++
		}
	}
	if ehCount != scrape.EhTagLimit {
		t.Errorf("EH 标签应恰好 %d 条，实际 %d", scrape.EhTagLimit, ehCount)
	}
	t.Logf("30 条已有标签 + 17 条候选 → 写库 %d 条（EH %d 条）", len(tags), ehCount)
}

// ============================================================
// D. 作者落书级 authors(role=Artist)，series 级 PATCH 不含 authors
// ============================================================

func TestV2EhAuthorsBookLevelOnly(t *testing.T) {
	env, eh := ehEnv(t, false, false)
	useEhTagFixture(t)
	seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)
	eh.mu.Lock()
	eh.tags = ehGalleryTags("artist:asanagi", "group:group1", "artist:artist2", "parody:azur lane")
	eh.mu.Unlock()

	job := ehSubmitJob(t, env, []string{"eh1"}, false)
	if job.Items[0].Status != "ok" {
		t.Fatalf("条目应 ok：%+v", job.Items[0])
	}

	// series 级 PATCH：不得含 authors（Komga series metadata 无该字段，会静默忽略）
	var seriesPatch map[string]any
	bookPatches := 0
	for _, r := range ehPatches(env) {
		if strings.HasSuffix(r.Path, "/series/eh1/metadata") {
			if err := json.Unmarshal(r.Body, &seriesPatch); err != nil {
				t.Fatalf("解析 series PATCH 体失败: %v", err)
			}
		}
		if strings.HasSuffix(r.Path, "/books/eh1-b1/metadata") {
			bookPatches++
		}
	}
	if seriesPatch == nil {
		t.Fatal("没抓到 series 级 PATCH")
	}
	if _, has := seriesPatch["authors"]; has {
		t.Errorf("series 级 PATCH 不该含 authors 字段，实际体=%v", seriesPatch)
	}
	if bookPatches != 1 {
		t.Errorf("应恰好 1 次书级 PATCH，实际 %d", bookPatches)
	}
	book := ehBookMeta(t, env, "eh1-b1")
	authors, _ := book["authors"].([]any)
	want := []struct{ name, role string }{{"朝凪", "Artist"}, {"画师二", "Artist"}, {"社团一", "Artist"}}
	if len(authors) != len(want) {
		t.Fatalf("书级作者应为 %d 个（artist 在前、group 在后），实际 %v", len(want), authors)
	}
	for i, w := range want {
		am, _ := authors[i].(map[string]any)
		if am["name"] != w.name || am["role"] != w.role {
			t.Errorf("第 %d 个作者应为 {%s %s}，实际 %v", i, w.name, w.role, authors[i])
		}
	}
	// series metadata 里 authors 必须仍是初始空数组（没被 series PATCH 写脏）
	sm := ehSeriesMeta(t, env, "eh1")
	if v, ok := sm["authors"]; ok {
		if arr, _ := v.([]any); len(arr) != 0 {
			t.Errorf("series metadata.authors 不该被写，实际 %v", arr)
		}
	}
	t.Logf("series PATCH 体=%v", seriesPatch)
	t.Logf("书级 authors=%v", authors)
}

// ============================================================
// E. dryRun=true 零写请求；dryRun=false 只 PATCH 一次
// ============================================================

func TestV2EhDryRunZeroWrites(t *testing.T) {
	env, eh := ehEnv(t, false, false)
	useEhTagFixture(t)
	seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)
	// 只用 parody：不产生 artist/group → 无书级 PATCH，PATCH 总数可精确断言
	eh.mu.Lock()
	eh.tags = ehGalleryTags("parody:parody1", "language:chinese")
	eh.mu.Unlock()

	// ---- dryRun=true ----
	job := ehSubmitJob(t, env, []string{"eh1"}, true)
	if job.Status != "done" || len(job.Items) != 1 || job.Items[0].Status != "ok" {
		t.Fatalf("dry-run 任务应 done/ok：%+v", job)
	}
	if got := len(ehPatches(env)); got != 0 {
		t.Fatalf("dryRun=true 必须零写请求，实际 %d 条：%+v", got, ehPatches(env))
	}
	if len(job.Items[0].Changed) == 0 {
		t.Errorf("dry-run 应给出将改动的字段（items[0].changed 为空）：%+v", job.Items[0])
	}
	metaAfterDry := ehSeriesMeta(t, env, "eh1")
	if metaAfterDry["title"] != "" || len(ehStrings(metaAfterDry["tags"])) != 0 {
		t.Errorf("dry-run 不得改动 mock Komga：%v", metaAfterDry)
	}
	t.Logf("dryRun=true：PATCH/PUT/DELETE 请求数=0；changed=%v", job.Items[0].Changed)

	// ---- dryRun=false ----
	env.komga.mu.Lock()
	env.komga.reqs = nil
	env.komga.mu.Unlock()
	job = ehSubmitJob(t, env, []string{"eh1"}, false)
	if job.Items[0].Status != "ok" {
		t.Fatalf("真实模式条目应 ok：%+v", job.Items[0])
	}
	patches := ehPatches(env)
	if len(patches) != 1 {
		t.Fatalf("dryRun=false 应只 PATCH 一次，实际 %d 条：%+v", len(patches), patches)
	}
	if patches[0].Path != "/api/v1/series/eh1/metadata" {
		t.Errorf("唯一 PATCH 应是系列元数据，实际 %s", patches[0].Path)
	}
	tags := ehStrings(ehSeriesMeta(t, env, "eh1")["tags"])
	if strings.Join(tags, "|") != "原作:原作一|语言:中文|MACHINE" {
		t.Errorf("真实模式写入标签不符：%v", tags)
	}
	t.Logf("dryRun=false：PATCH 数=%d path=%s tags=%v", len(patches), patches[0].Path, tags)
}

// ============================================================
// F. tagsLock：EH 标签不写，但 MACHINE 仍打
// ============================================================

func TestV2EhTagsLockStillMachine(t *testing.T) {
	env, eh := ehEnv(t, false, false)
	useEhTagFixture(t)
	seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3",
		map[string]any{"tags": []string{"旧标签A"}, "tagsLock": true})
	eh.mu.Lock()
	eh.tags = ehGalleryTags("artist:asanagi", "parody:azur lane")
	eh.mu.Unlock()

	job := ehSubmitJob(t, env, []string{"eh1"}, false)
	if job.Items[0].Status != "ok" {
		t.Fatalf("条目应 ok：%+v", job.Items[0])
	}
	tags := ehStrings(ehSeriesMeta(t, env, "eh1")["tags"])
	if !contains(tags, "MACHINE") {
		t.Errorf("tagsLock 时 MACHINE 仍应写入，实际 %v", tags)
	}
	if !contains(tags, "旧标签A") {
		t.Errorf("tagsLock 时已有标签不该丢，实际 %v", tags)
	}
	for _, bad := range []string{"艺术家:朝凪", "原作:碧蓝航线"} {
		if contains(tags, bad) {
			t.Errorf("tagsLock 时 EH 标签 %q 不该写入，实际 %v", bad, tags)
		}
	}
	t.Logf("tagsLock=true 后写库标签=%v", tags)
}

// ============================================================
// G. 失败即继续：EH 500 / 连接失败 → 链继续到百科 → 待手动匹配，任务不失败
// ============================================================

func TestV2EhFailureDoesNotBreakChain(t *testing.T) {
	t.Run("EH 返回 500 → 继续走百科 → 待手动匹配", func(t *testing.T) {
		env, eh := ehEnv(t, false, true) // 百科开启：本地 mock 对「Mesu Kyoushi 3」返回 errno -1（查无此词条）
		useEhTagFixture(t)
		seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)
		eh.mu.Lock()
		eh.status = 500 // 瞬态状态码：走重试而非熔断（每个请求重试 3 次，共 ~4.5s）
		eh.mu.Unlock()

		start := time.Now()
		job := ehSubmitJob(t, env, []string{"eh1"}, false)
		elapsed := time.Since(start)
		if job.Status != "done" {
			t.Fatalf("EH 500 不该让任务失败，job.Status=%q", job.Status)
		}
		if len(job.Items) != 1 || job.Items[0].Status != "manual" {
			t.Fatalf("应落到 manual（待手动匹配），实际 %+v", job.Items)
		}
		eh.mu.Lock()
		reqs := append([]ehReqSnapshot(nil), eh.reqs...)
		eh.mu.Unlock()
		searchN := 0
		for _, r := range reqs {
			if r.Path == "/" {
				searchN++
			}
		}
		if searchN == 0 {
			t.Fatalf("假表站没收到搜索请求（EH 分支没被走到？共 %d 个请求）", len(reqs))
		}
		lastBaikeMock.mu.Lock()
		cardN := lastBaikeMock.cardHits
		lastBaikeMock.mu.Unlock()
		if cardN == 0 {
			t.Error("EH 失败后应继续问百科（BaikeLemmaCardApi 未被调用）")
		}
		if len(ehPatches(env)) != 0 {
			t.Errorf("EH 失败后不该有任何写请求，实际 %+v", ehPatches(env))
		}
		t.Logf("EH 500：搜索请求 %d 次，百科卡片请求 %d 次，耗时 %s，任务 status=%s item=%s message=%q",
			searchN, cardN, elapsed.Round(time.Millisecond), job.Status, job.Items[0].Status, job.Items[0].Message)
	})

	t.Run("EH 连接失败（等价墙内直连/超时）→ 熔断短路后仍继续走百科", func(t *testing.T) {
		env, _ := ehEnv(t, false, true)
		useEhTagFixture(t)
		seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)
		// 覆盖成死端口：连接立刻被拒（连接层错误不重试，连续 2 次后熔断短路）
		ehBaseOverride = "http://127.0.0.1:1"

		start := time.Now()
		job := ehSubmitJob(t, env, []string{"eh1"}, false)
		elapsed := time.Since(start)
		if job.Status != "done" {
			t.Fatalf("EH 连不上不该让任务失败，job.Status=%q", job.Status)
		}
		if len(job.Items) != 1 || job.Items[0].Status != "manual" {
			t.Fatalf("应落到 manual，实际 %+v", job.Items)
		}
		lastBaikeMock.mu.Lock()
		cardN := lastBaikeMock.cardHits
		lastBaikeMock.mu.Unlock()
		if cardN == 0 {
			t.Error("EH 连不上后应继续问百科（BaikeLemmaCardApi 未被调用）")
		}
		if elapsed > 20*time.Second {
			t.Errorf("连接失败应快速短路（熔断），实际耗时 %s", elapsed)
		}
		t.Logf("EH 连接失败：耗时 %s，百科卡片请求 %d 次，任务 status=%s item=%s message=%q",
			elapsed.Round(time.Millisecond), cardN, job.Status, job.Items[0].Status, job.Items[0].Message)
	})
}

// ============================================================
// 对抗性边界：空 tags / 空 title / 只有 gid / gdata error / filesize 裸数字
// ============================================================

func TestV2EhAdversarialBoundaries(t *testing.T) {
	// ---- 空 tags：链路仍能命中并写库（只写 MACHINE）----
	t.Run("空 tags", func(t *testing.T) {
		env, eh := ehEnv(t, false, false)
		useEhTagFixture(t)
		seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)
		eh.mu.Lock()
		eh.tags = []string{}
		eh.mu.Unlock()

		job := ehSubmitJob(t, env, []string{"eh1"}, false)
		if job.Items[0].Status != "ok" {
			t.Fatalf("空 tags 不该让刮削失败：%+v", job.Items[0])
		}
		tags := ehStrings(ehSeriesMeta(t, env, "eh1")["tags"])
		if strings.Join(tags, "|") != "MACHINE" {
			t.Errorf("空 tags 时只应写 MACHINE，实际 %v", tags)
		}
		if len(ehPatches(env)) != 1 {
			t.Errorf("空 tags 仍应只有 1 次 PATCH（无书级作者），实际 %+v", ehPatches(env))
		}
		t.Logf("空 tags → 写库 tags=%v", tags)
	})

	// ---- 空 title：不写标题，但其余字段照写；任务不失败 ----
	t.Run("空 title", func(t *testing.T) {
		env, eh := ehEnv(t, false, false)
		useEhTagFixture(t)
		seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)
		eh.mu.Lock()
		eh.galleryTitle = ""
		eh.tags = ehGalleryTags("parody:parody1")
		eh.mu.Unlock()

		job := ehSubmitJob(t, env, []string{"eh1"}, false)
		if job.Items[0].Status != "ok" {
			t.Fatalf("空 title 不该让刮削失败：%+v", job.Items[0])
		}
		meta := ehSeriesMeta(t, env, "eh1")
		if meta["title"] != "" {
			t.Errorf("gdata title 为空时不该写标题，实际 %v", meta["title"])
		}
		if meta["releaseDate"] != "2020-09-13" {
			t.Errorf("空 title 不该影响 releaseDate，实际 %v", meta["releaseDate"])
		}
		t.Logf("空 title → title=%q releaseDate=%v tags=%v", meta["title"], meta["releaseDate"], ehStrings(meta["tags"]))
	})

	// ---- 只有 gid 没 token：画廊页能补出 token（详情仍 200）----
	t.Run("只有 gid 没 token（可补 token）", func(t *testing.T) {
		env, eh := ehEnv(t, false, false)
		useEhTagFixture(t)
		eh.mu.Lock()
		eh.tags = ehGalleryTags("artist:asanagi")
		eh.mu.Unlock()

		code, body, msg := ehDetail(t, env, fmt.Sprint(eh.gid))
		if code != 200 {
			t.Fatalf("只给 gid 应能补 token 并取到详情，实际 %d msg=%s", code, msg)
		}
		if body["title"] != "Mesu Kyoushi 3" {
			t.Errorf("详情标题不符：%v", body["title"])
		}
		if body["id"] != fmt.Sprint(eh.gid) {
			t.Errorf("详情 id 应原样回显 gid，实际 %v", body["id"])
		}
		t.Logf("只给 gid：code=%d title=%v url=%v tags=%v", code, body["title"], body["url"], body["tags"])
	})

	// ---- 只有 gid 没 token 且画廊页无规范链接：应明确报错，不能静默成功 ----
	t.Run("只有 gid 没 token（补不出 token）", func(t *testing.T) {
		env, eh := ehEnv(t, false, false)
		useEhTagFixture(t)
		eh.mu.Lock()
		eh.noTokenPage = true
		eh.mu.Unlock()

		code, _, msg := ehDetail(t, env, fmt.Sprint(eh.gid))
		if code == 200 {
			t.Fatal("补不出 token 时不该返回 200")
		}
		if !strings.Contains(msg, "token") {
			t.Errorf("错误信息应说明 token 解析失败，实际 %q（code=%d）", msg, code)
		}
		t.Logf("补不出 token：code=%d msg=%s", code, msg)
	})

	// ---- gdata 返回 {"error":...}：详情应 502 且带原文 ----
	t.Run("gdata error", func(t *testing.T) {
		env, eh := ehEnv(t, false, false)
		useEhTagFixture(t)
		eh.mu.Lock()
		eh.gdataErr = "Gallery not found"
		eh.mu.Unlock()

		code, _, msg := ehDetail(t, env, sources.EhID(eh.gid, eh.token))
		if code != 502 {
			t.Errorf("gdata error 应 502，实际 %d", code)
		}
		if !strings.Contains(msg, "Gallery not found") {
			t.Errorf("错误信息应保留 gdata 原文，实际 %q", msg)
		}
		t.Logf("gdata error：code=%d msg=%s", code, msg)
	})

	// ---- gmetadata 空数组：详情应报「未返回该画廊元数据」----
	t.Run("gmetadata 空数组", func(t *testing.T) {
		env, eh := ehEnv(t, false, false)
		useEhTagFixture(t)
		eh.mu.Lock()
		eh.gdataNone = true
		eh.mu.Unlock()

		code, _, msg := ehDetail(t, env, sources.EhID(eh.gid, eh.token))
		if code != 502 {
			t.Errorf("空 gmetadata 应 502，实际 %d", code)
		}
		if !strings.Contains(msg, "未返回") {
			t.Errorf("错误信息应说明未返回元数据，实际 %q", msg)
		}
		t.Logf("空 gmetadata：code=%d msg=%s", code, msg)
	})

	// ---- filesize/rating/filecount 为裸数字（真实站 expunge 后 filesize=0）----
	t.Run("filesize 裸数字 0", func(t *testing.T) {
		env, eh := ehEnv(t, false, false)
		useEhTagFixture(t)
		eh.mu.Lock()
		eh.filesize = 0 // 裸数字，不是字符串
		eh.rating = 0
		eh.filecount = 0
		eh.mu.Unlock()

		code, body, msg := ehDetail(t, env, sources.EhID(eh.gid, eh.token))
		if code != 200 {
			t.Fatalf("裸数字字段应被宽容解析（200），实际 %d msg=%s", code, msg)
		}
		summary, _ := body["summary"].(string)
		if !strings.Contains(summary, "大小：0") || !strings.Contains(summary, "页数：0") {
			t.Errorf("裸数字应解析成 \"0\"，实际 summary=%q", summary)
		}
		t.Logf("filesize=0（裸数字）→ summary=%q", summary)
	})

	// ---- 空 tags 的详情：tags/tagsRaw 都应为空且不 panic ----
	t.Run("空 tags 的详情", func(t *testing.T) {
		env, eh := ehEnv(t, false, false)
		useEhTagFixture(t)
		eh.mu.Lock()
		eh.tags = []string{}
		eh.mu.Unlock()

		code, body, msg := ehDetail(t, env, sources.EhID(eh.gid, eh.token))
		if code != 200 {
			t.Fatalf("空 tags 详情应 200，实际 %d msg=%s", code, msg)
		}
		if _, has := body["tags"]; has {
			t.Errorf("空 tags 应 omitempty 不出现，实际 %v", body["tags"])
		}
		if s, _ := body["summary"].(string); strings.Contains(s, "标签：") {
			t.Errorf("无标签时摘要不该有「标签：」，实际 %q", s)
		}
		t.Logf("空 tags 详情：code=%d summary=%q", code, body["summary"])
	})
}

// ============================================================
// 对抗性：翻译未收录 / 译文等于原文 → 回落原始 tag，绝不写英文原文以外的东西
// ============================================================

func TestV2EhTranslationFallback(t *testing.T) {
	env, eh := ehEnv(t, false, false)
	useEhTagFixture(t)
	seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)
	eh.mu.Lock()
	// nobody：词库未收录；ok：词库 name 与 tag 相同（ehtags 视为未翻译）
	eh.tags = ehGalleryTags("artist:asanagi", "artist:nobody", "female:ok")
	eh.mu.Unlock()

	job := ehSubmitJob(t, env, []string{"eh1"}, false)
	if job.Items[0].Status != "ok" {
		t.Fatalf("条目应 ok：%+v", job.Items[0])
	}
	tags := ehStrings(ehSeriesMeta(t, env, "eh1")["tags"])
	want := []string{"艺术家:朝凪", "艺术家:nobody", "女性:ok", "MACHINE"}
	if strings.Join(tags, "|") != strings.Join(want, "|") {
		t.Errorf("未收录应回落原始 tag 名（命名空间仍译中文）\n got=%v\nwant=%v", tags, want)
	}
	t.Logf("翻译回落：%v", tags)
}

// ============================================================
// 对抗性：确认没把里站（exhentai）/ cookie 逻辑带进来
// ============================================================

func TestV2EhTableSiteOnly(t *testing.T) {
	// ① 默认基址必须是表站，不是里站
	c, err := sources.NewEhClient(sources.ProxyConfig{}, "", nil, 0)
	if err != nil {
		t.Fatalf("创建默认 EH 客户端失败: %v", err)
	}
	if c.Base() != "https://e-hentai.org" {
		t.Errorf("默认站点应为表站 https://e-hentai.org，实际 %q", c.Base())
	}
	if c.GDataURL() != "https://api.e-hentai.org/api.php" {
		t.Errorf("默认 gdata 端点应为主站 api.php，实际 %q", c.GDataURL())
	}

	// ② 跑一遍完整链路，检查假表站收到的每个请求都不带 Cookie、不打里站
	env, eh := ehEnv(t, false, false)
	useEhTagFixture(t)
	seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)
	eh.mu.Lock()
	eh.tags = ehGalleryTags("artist:asanagi")
	eh.mu.Unlock()
	job := ehSubmitJob(t, env, []string{"eh1"}, false)
	if job.Items[0].Status != "ok" {
		t.Fatalf("条目应 ok：%+v", job.Items[0])
	}

	eh.mu.Lock()
	reqs := append([]ehReqSnapshot(nil), eh.reqs...)
	eh.mu.Unlock()
	if len(reqs) == 0 {
		t.Fatal("没抓到任何 EH 请求")
	}
	authish := []string{"cookie", "authorization", "ipb_member_id", "ipb_pass_hash", "igneous"}
	for _, r := range reqs {
		for _, h := range authish {
			if v := r.Headers.Get(h); v != "" {
				t.Errorf("表站请求不该带 %s：%s %s → %q", h, r.Method, r.Path, v)
			}
		}
		if strings.Contains(strings.ToLower(r.Headers.Get("Host")+r.Path+r.Query), "exhentai") {
			t.Errorf("请求打到了里站：%s %s?%s", r.Method, r.Path, r.Query)
		}
	}
	t.Logf("默认 base=%s gdata=%s；实测 %d 个 EH 请求，全部无 Cookie/无里站", c.Base(), c.GDataURL(), len(reqs))

	// ③ 粘贴里站（exhentai）链接：只做「链接解析」，数据仍然只走表站。
	// 这是运行时证伪，不是读代码：假表站必须收到请求，且假表站收到的请求里不得出现 exhentai。
	eh.mu.Lock()
	before := len(eh.reqs)
	eh.mu.Unlock()
	resp, data := env.do("POST", "/api/match/resolve", env.adminToken, map[string]any{
		"url": "https://exhentai.org/g/" + fmt.Sprint(eh.gid) + "/" + eh.token + "/",
	})
	if resp.StatusCode != 200 {
		t.Fatalf("里站链接应被解析成 gid/token 并走表站取详情，实际 %d %s", resp.StatusCode, data)
	}
	var resolved map[string]any
	json.Unmarshal(data, &resolved)
	if resolved["source"] != "ehentai" {
		t.Errorf("详情 source 应归一成 ehentai，实际 %v", resolved["source"])
	}
	eh.mu.Lock()
	got := append([]ehReqSnapshot(nil), eh.reqs[before:]...)
	eh.mu.Unlock()
	if len(got) == 0 {
		t.Fatal("里站链接没有产生任何表站请求（说明它根本没走表站？）")
	}
	gdataCalls := 0
	for _, r := range got {
		if strings.Contains(strings.ToLower(r.Path+"?"+r.Query), "exhentai") {
			t.Errorf("里站链接把请求带到了里站：%s %s?%s", r.Method, r.Path, r.Query)
		}
		if r.Path == "/api.php" {
			gdataCalls++
		}
	}
	t.Logf("里站链接 → source=%v id=%v；表站假站收到 %d 个请求（其中 gdata %d 次），零里站请求",
		resolved["source"], resolved["id"], len(got), gdataCalls)
}

// ============================================================
// 补充（非 task-6 A–G 必选项）：封面链路真的会上传
// ============================================================

func TestV2EhCoverUploaded(t *testing.T) {
	env, eh := ehEnv(t, true, false) // withCovers=true
	useEhTagFixture(t)
	seedEhOnlySeries(t, env, "eh1", "[TestCircle (Asanagi)] Mesu Kyoushi 3", nil)
	eh.mu.Lock()
	eh.tags = ehGalleryTags("parody:parody1")
	eh.mu.Unlock()

	job := ehSubmitJob(t, env, []string{"eh1"}, false)
	if job.Items[0].Status != "ok" {
		t.Fatalf("条目应 ok：%+v", job.Items[0])
	}
	uploaded := len(env.komga.requests("POST", "/api/v1/series/eh1/thumbnails"))
	if uploaded != 1 {
		t.Errorf("应上传 1 次系列封面，实际 %d", uploaded)
	}
	served := 0
	eh.mu.Lock()
	for _, r := range eh.reqs {
		if strings.HasPrefix(r.Path, "/w/") {
			served++
		}
	}
	eh.mu.Unlock()
	if served == 0 {
		t.Error("假表站图床没被访问（封面下载没发生）")
	}
	t.Logf("封面：图床被访问 %d 次，Komga 上传 %d 次", served, uploaded)
}

func contains(list []string, v string) bool {
	for _, s := range list {
		if s == v {
			return true
		}
	}
	return false
}
