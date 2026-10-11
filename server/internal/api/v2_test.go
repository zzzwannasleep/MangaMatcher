package api

import (
	"archive/zip"
	"bytes"
	"compress/gzip"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"komgabangumi/internal/sources"
	"komgabangumi/internal/store"
)

// ============================================================
// v2 集成测试：回退链 / 跳过已刮 / overrideMatch / 编辑器 / Mylar / 打标 / 状态筛选
// （五源解析与 520 退避在 internal/sources 的单测；BW 跨源补空在 internal/scrape）
// ============================================================

// ---------- v2 源 mock（Go httptest 版，浏览器联调另有 tools/mock-*.js） ----------

type mockAIServer struct {
	mu     sync.Mutex
	calls  int
	script func(user string) string
}

func (m *mockAIServer) handler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		m.mu.Lock()
		m.calls++
		m.mu.Unlock()
		body, _ := io.ReadAll(r.Body)
		var req struct {
			Messages []struct {
				Role    string `json:"role"`
				Content string `json:"content"`
			} `json:"messages"`
		}
		json.Unmarshal(body, &req)
		user := ""
		for _, msg := range req.Messages {
			if msg.Role == "user" {
				user += msg.Content + "\n"
			}
		}
		var reply string
		if m.script != nil {
			reply = m.script(user)
		} else {
			reply = defaultAIScript(user)
		}
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]any{
			"choices": []any{map[string]any{"message": map[string]any{"role": "assistant", "content": reply}}},
		})
	}
}

// defaultAIScript 按用途返回罐头（识别/选择/翻译/补写）。
func defaultAIScript(user string) string {
	switch {
	case strings.Contains(user, "请判断这是哪部"):
		return `{"jp":"ぐらんぶる","cn":"碧蓝之海","en":"Grand Blue","confident":true}`
	case strings.Contains(user, "编号从 1 开始"):
		return `{"index":1}`
	case strings.Contains(user, "还原成日文原名"):
		return "ぐらんぶる"
	case strings.Contains(user, "完整翻译成简体中文"):
		return "潜水与饮酒的青春喜剧。"
	default:
		return "UNKNOWN"
	}
}

type mockMoxV2 struct {
	mu         sync.Mutex
	searchHits int
	detailHits int
	empty      bool // 搜索永远空（回退链打不到 Mox）
}

func (m *mockMoxV2) handler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		switch {
		case r.URL.Path == "/data_list.php":
			m.mu.Lock()
			m.searchHits++
			empty := m.empty
			m.mu.Unlock()
			if empty {
				w.Write([]byte("<html>nomatch</html>"))
				return
			}
			term := r.URL.Query().Get("s")
			fmt.Fprintf(w, "datainfo-B=漫画,777,%s,作者甲,2014-05-09,https://img.mxomo.com/cover/777.jpg", term)
		case strings.HasPrefix(r.URL.Path, "/b/"):
			m.mu.Lock()
			m.detailHits++
			m.mu.Unlock()
			w.Write([]byte(`<html><div class="name_main">ぐらんぶる</div>` +
				`<div class="name_subt">(Grand Blue) 狀態：連載</div>` +
				`<div id="div_desctext">潛水社的故事。</div>` +
				`<script>window.iframe_action.location.href="/data_vol.php?id=777"</script></html>`))
		case r.URL.Path == "/data_vol.php":
			w.Write([]byte("datainfo-V=1, v1, 漫画, x, https://img.mxomo.com/v/777_1.jpg, y"))
		default:
			w.WriteHeader(404)
		}
	}
}

type mockMangaDexV2 struct {
	mu         sync.Mutex
	searchHits int
	empty      bool
}

func (m *mockMangaDexV2) handler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/manga" {
			m.mu.Lock()
			m.searchHits++
			empty := m.empty
			m.mu.Unlock()
			if empty {
				w.Write([]byte(`{"result":"ok","data":[]}`))
				return
			}
			w.Write([]byte(`{"result":"ok","data":[{"id":"md-1","type":"manga","attributes":{
				"title":{"ja":"ぐらんぶる","zh-Hans":"碧蓝之海"},"altTitles":[],
				"description":{"en":"Dive and drink."},"originalLanguage":"ja","status":"ongoing","tags":[]}],
				"relationships":[{"id":"c1","type":"cover_art","attributes":{"fileName":"c.jpg"}}]}`))
			return
		}
		w.WriteHeader(404)
	}
}

type mockBaikeV2 struct {
	mu        sync.Mutex
	cardHits  int
	imageHits int
}

func (m *mockBaikeV2) handler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/api/openapi/BaikeLemmaCardApi":
			m.mu.Lock()
			m.cardHits++
			m.mu.Unlock()
			if strings.Contains(r.URL.Query().Get("bk_key"), "碧蓝") {
				w.Header().Set("Content-Type", "application/json")
				w.Write([]byte(`{"newLemmaId":9527,"title":"碧蓝之海","desc":"井上坚二、吉冈公威作画的日本漫画",
				"abstract":"围绕潜水与饮酒的青春喜剧故事。",
				"image":"` + testBase(m) + `/image/cover.jpg","url":"http://baike.baidu.com/item/x",
				"card":[{"name":"原版名称","value":["ぐらんぶる"]},{"name":"作画","value":["吉冈公威"]},
				{"name":"作者","value":["井上坚二"]},{"name":"出版社","value":["讲谈社"]},{"name":"类型","value":["喜剧","校园"]}]}`))
				return
			}
			w.Write([]byte(`{"errno":-1}`))
		case strings.HasPrefix(r.URL.Path, "/image/"):
			m.mu.Lock()
			m.imageHits++
			m.mu.Unlock()
			w.Header().Set("Content-Type", "image/jpeg")
			w.Write([]byte{0xff, 0xd8, 0xff, 0xe0, 8, 8, 8, 8})
		default:
			w.WriteHeader(404)
		}
	}
}

// testBaseStore mock 基址登记（baike mock 图 URL 需绝对地址）。
var testBaseStore = map[string]string{}

// testBase 拿 baike mock 自身基址。
func testBase(m *mockBaikeV2) string {
	for k, v := range testBaseStore {
		if v == "baike" {
			return k
		}
	}
	_ = m
	return "http://127.0.0.1"
}

// gzWriteAll gzip 压缩。
func gzWriteAll(out *bytes.Buffer, data []byte) {
	gz := gzip.NewWriter(out)
	gz.Write(data)
	gz.Close()
}

// writeFileBytes 落盘。
func writeFileBytes(path string, data []byte) error {
	return os.WriteFile(path, data, 0o644)
}

// readFileZip 读 ZIP 条目。
func readFileZip(f *zip.File) ([]byte, error) {
	rc, err := f.Open()
	if err != nil {
		return nil, err
	}
	defer rc.Close()
	return io.ReadAll(rc)
}

// writeBWIndex 在 dataDir 写一份 gzip v2 索引（直接落盘 JSON）。
func writeBWIndex(t *testing.T, dataDir string, entries []map[string]any) {
	t.Helper()
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	if err := enc.Encode(map[string]any{"v": 2, "n": len(entries), "e": entries}); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(dataDir, "bkwk-index.json.gz")
	// gzip 压缩写文件
	out := &bytes.Buffer{}
	gzWriteAll(out, buf.Bytes())
	if err := writeFileBytes(path, out.Bytes()); err != nil {
		t.Fatal(err)
	}
}

// restoreOverride 测试结束后恢复源基址（并行安全：本套件串行运行）。
func setSourceOverrides(t *testing.T, moxBase, mdBase, baikeBase string, moxPace int) {
	t.Helper()
	oldMox, oldMD, oldBaike, oldPace := moxBaseOverride, mangaDexBaseOverride, baikeBaseOverride, moxPaceOverrideMs
	moxBaseOverride, mangaDexBaseOverride, baikeBaseOverride, moxPaceOverrideMs = moxBase, mdBase, baikeBase, moxPace
	t.Cleanup(func() {
		moxBaseOverride, mangaDexBaseOverride, baikeBaseOverride, moxPaceOverrideMs = oldMox, oldMD, oldBaike, oldPace
	})
}

func v2EnvSettings(e *testEnv, aiURL string) {
	patch := map[string]any{
		"dryRunDefault": false,
		"ai": map[string]any{
			"enabled": aiURL != "", "baseURL": orDef(aiURL, "http://127.0.0.1:1"),
			"apiKey": "k", "model": "mock", "identify": true, "smartPick": true,
		},
	}
	if aiURL == "" {
		patch["ai"] = map[string]any{"enabled": false}
	}
	e.patchSettings(patch)
}

func orDef(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

func waitJob(t *testing.T, e *testEnv, jobID string) jobView {
	t.Helper()
	deadline := time.Now().Add(60 * time.Second)
	for {
		resp, data := e.do("GET", "/api/jobs/"+jobID, e.adminToken, nil)
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

// ---------- 7. 回退链顺序：Bangumi 空 → BW 无 → AI 识别 → 三名重搜空 → Mox 空 → MangaDex 空 → 百科命中 ----------

func TestV2ChainFallbackToBaike(t *testing.T) {
	aiMock := &mockAIServer{}
	aiSrv := httptest.NewServer(aiMock.handler())
	defer aiSrv.Close()
	moxMock := &mockMoxV2{empty: true}
	moxSrv := httptest.NewServer(moxMock.handler())
	defer moxSrv.Close()
	mdMock := &mockMangaDexV2{empty: true}
	mdSrv := httptest.NewServer(mdMock.handler())
	defer mdSrv.Close()
	bkMock := &mockBaikeV2{}
	bkSrv := httptest.NewServer(bkMock.handler())
	defer bkSrv.Close()
	testBaseStore[bkSrv.URL] = "baike"
	defer delete(testBaseStore, bkSrv.URL)
	setSourceOverrides(t, moxSrv.URL, mdSrv.URL, bkSrv.URL, 0)

	env := newTestEnv(t)
	env.bangumi.mu.Lock()
	env.bangumi.emptyAll = true // Bangumi 搜索永远空（含 AI 原名重搜）
	env.bangumi.mu.Unlock()
	// BW 索引：不含目标系列（确保 BW 回退无命中）
	writeBWIndex(t, env.dataDir, []map[string]any{{
		"id": "CNT_other", "t": "偽物語", "a": []string{"伪物语"}, "d": "other series",
		"v": 3, "ty": 1,
	}})
	v2EnvSettings(env, aiSrv.URL)

	// BW 索引存在 → runner 能加载
	job := env.submitJob(t, env.adminToken, "lib1", []string{"s1"}, false)
	if len(job.Items) != 1 || job.Items[0].Status != "ok" {
		t.Fatalf("回退链应百科命中：job=%+v", job.Items)
	}
	if job.Items[0].MatchTitle != "碧蓝之海" {
		t.Fatalf("命中标题应为百科词条标题: %+v", job.Items[0])
	}

	// 链路顺序证据：Bangumi 搜索 → AI 识别 → Mox 搜索 → MangaDex 搜索 → 百科卡片
	env.bangumi.mu.Lock()
	bgmSearches := len(env.bangumi.searches)
	env.bangumi.mu.Unlock()
	if bgmSearches < 4 { // 首词 + AI 三名重搜
		t.Fatalf("Bangumi 应至少被搜 4 次（首词+AI 三名），实际 %d", bgmSearches)
	}
	moxMock.mu.Lock()
	moxHits := moxMock.searchHits
	moxMock.mu.Unlock()
	if moxHits < 1 {
		t.Fatal("Mox 应被搜索过（AI 中文名重搜）")
	}
	mdMock.mu.Lock()
	mdHits := mdMock.searchHits
	mdMock.mu.Unlock()
	if mdHits < 1 {
		t.Fatal("MangaDex 应被搜索过")
	}
	bkMock.mu.Lock()
	bkHits := bkMock.cardHits
	bkMock.mu.Unlock()
	if bkHits < 1 {
		t.Fatal("百科应被查询过")
	}

	// PATCH 正确字段：title/summary/publisher/作者/链接/标签（含 MACHINE）/阅读方向
	meta := env.komga.series["s1"]["metadata"].(map[string]any)
	if meta["title"] != "碧蓝之海" {
		t.Fatalf("标题应来自百科词条: %v", meta["title"])
	}
	if meta["summary"] != "围绕潜水与饮酒的青春喜剧故事。" {
		t.Fatalf("简介应来自百科摘要: %v", meta["summary"])
	}
	if meta["publisher"] != "讲谈社" {
		t.Fatalf("出版社应来自百科卡片: %v", meta["publisher"])
	}
	hasBaikeLink := false
	for _, l := range meta["links"].([]any) {
		lm := l.(map[string]any)
		if lm["label"] == "Baike" {
			hasBaikeLink = true
		}
		if strings.Contains(strings.ToLower(fmt.Sprint(lm["url"])), "bgm.tv") {
			t.Fatalf("不应残留旧 Btv 链接: %v", lm)
		}
	}
	if !hasBaikeLink {
		t.Fatalf("应有 Baike 链接: %v", meta["links"])
	}
	hasMachine := false
	for _, tg := range meta["tags"].([]any) {
		if tg == "MACHINE" {
			hasMachine = true
		}
	}
	if !hasMachine {
		t.Fatalf("应有 MACHINE 标签: %v", meta["tags"])
	}
	if meta["readingDirection"] != "RIGHT_TO_LEFT" {
		t.Fatalf("日本漫画应为从右往左: %v", meta["readingDirection"])
	}
}

// ---------- 6. 跳过已刮：列表 links 判断零额外请求 ----------

func TestV2SkipScrapedZeroRequests(t *testing.T) {
	env := newTestEnv(t)
	// s1 已有 Btv 链接（覆盖 mockKomga 的 seed 元数据）
	env.komga.mu.Lock()
	s1 := env.komga.series["s1"]
	meta := s1["metadata"].(map[string]any)
	meta["links"] = []any{map[string]any{"label": "Btv", "url": "https://bgm.tv/subject/600001"}}
	env.komga.series["s1"] = s1
	env.komga.reqs = nil
	env.komga.mu.Unlock()

	job := env.submitJob(t, env.adminToken, "lib1", []string{"s1"}, false)
	if len(job.Items) != 1 || job.Items[0].Status != "skipped" {
		t.Fatalf("已有 Btv 链接应跳过: %+v", job.Items)
	}
	env.bangumi.mu.Lock()
	bgmSearches := len(env.bangumi.searches)
	env.bangumi.mu.Unlock()
	if bgmSearches != 0 {
		t.Fatalf("跳过已刮应零 Bangumi 请求，实际 %d", bgmSearches)
	}
	for _, r := range env.komga.requests("", "/api/v1/books/list") {
		_ = r
		t.Fatal("跳过已刮不应触发 books/list")
	}
	// forceRefresh=true 才重刮
	env.komga.mu.Lock()
	env.komga.reqs = nil
	env.komga.mu.Unlock()
	_, data := env.do("POST", "/api/jobs", env.adminToken, map[string]any{
		"libraryId": "lib1", "seriesIds": []string{"s1"}, "dryRun": false, "forceRefresh": true})
	var created struct {
		ID string `json:"id"`
	}
	json.Unmarshal(data, &created)
	job = waitJob(t, env, created.ID)
	if job.Items[0].Status != "ok" {
		t.Fatalf("forceRefresh 应重刮: %+v", job.Items)
	}
}

// ---------- 6. overrideMatch 语义：搜索词只用文件夹名 + ReplaceMatch ----------

func TestV2OverrideMatch(t *testing.T) {
	env := newTestEnv(t)
	// s1 已被错误匹配污染：旧 Btv 链接 + 旧标题
	env.komga.mu.Lock()
	s1 := env.komga.series["s1"]
	meta := s1["metadata"].(map[string]any)
	meta["title"] = "完全错误的旧标题"
	meta["links"] = []any{map[string]any{"label": "Btv", "url": "https://bangumi.tv/subject/888888"}}
	env.komga.reqs = nil
	env.komga.mu.Unlock()

	_, data := env.do("POST", "/api/jobs", env.adminToken, map[string]any{
		"libraryId": "lib1", "seriesIds": []string{"s1"}, "dryRun": false, "overrideMatch": true})
	var created struct {
		ID string `json:"id"`
	}
	json.Unmarshal(data, &created)
	job := waitJob(t, env, created.ID)
	if len(job.Items) != 1 || job.Items[0].Status != "ok" {
		t.Fatalf("覆盖匹配应命中并重刮: %+v", job.Items)
	}
	// 搜索词只用文件夹名（不含被污染的元数据标题）
	env.bangumi.mu.Lock()
	searched := append([]string{}, env.bangumi.searches...)
	env.bangumi.mu.Unlock()
	for _, term := range searched {
		if strings.Contains(term, "完全错误的旧标题") {
			t.Fatalf("覆盖匹配不得用污染标题搜索: %v", searched)
		}
	}
	// ReplaceMatch：PATCH 里新 Btv 链接（600001）顶替旧链接（888888）
	// 语义对齐桌面版：旧匹配链接在 BuildSeriesMeta 的新 links 里被 label 去重顶替 + replace 时剔除来源链接。
	env.komga.mu.Lock()
	metaNow := env.komga.series["s1"]["metadata"].(map[string]any)
	linksNow := metaNow["links"].([]any)
	env.komga.mu.Unlock()
	hasNew, hasOld := false, false
	for _, l := range linksNow {
		lm := l.(map[string]any)
		switch {
		case strings.Contains(fmt.Sprint(lm["url"]), "600001"):
			hasNew = true
		case strings.Contains(fmt.Sprint(lm["url"]), "888888"):
			hasOld = true
		}
	}
	if !hasNew || hasOld {
		t.Fatalf("ReplaceMatch 应替换旧链接: %v", linksNow)
	}
	if title := fmt.Sprint(metaNow["title"]); !strings.Contains(title, "碧蓝") && !strings.Contains(title, "碧藍") {
		t.Fatalf("标题应被新匹配覆盖: %v", metaNow["title"])
	}
}

// ---------- 4. 编辑器 PATCH 只含改动字段 + MACHINE 自动补 ----------

func TestV2EditorPatchChangedFieldsOnly(t *testing.T) {
	env := newTestEnv(t)
	env.komga.mu.Lock()
	env.komga.reqs = nil
	env.komga.mu.Unlock()

	resp, data := env.do("PATCH", "/api/series/s1/metadata-raw", env.adminToken, map[string]any{
		"summary": "新简介",
	})
	if resp.StatusCode != 200 {
		t.Fatalf("编辑器 PATCH 应成功: %d %s", resp.StatusCode, data)
	}
	reqs := env.komga.requests("PATCH", "/api/v1/series/s1/metadata")
	if len(reqs) != 1 {
		t.Fatalf("应恰好一次 PATCH: %d", len(reqs))
	}
	body := env.komga.jsonBody(t, reqs[0])
	if body["summary"] != "新简介" {
		t.Fatalf("改动字段应原样提交: %v", body)
	}
	if _, has := body["title"]; has {
		t.Fatalf("未改动的字段不得提交: %v", body)
	}
	tags := body["tags"].([]any)
	last := tags[len(tags)-1]
	if last != "MACHINE" {
		t.Fatalf("MACHINE 应自动补: %v", tags)
	}
	if len(tags) != 1 {
		t.Fatalf("空 tags 基础上只应补 MACHINE: %v", tags)
	}
	// 锁字段原样回传（不翻转）
	env.komga.mu.Lock()
	env.komga.reqs = nil
	env.komga.mu.Unlock()
	resp, data = env.do("PATCH", "/api/series/s1/metadata-raw", env.adminToken, map[string]any{
		"title": "改标题", "titleLock": true,
	})
	if resp.StatusCode != 200 {
		t.Fatalf("带锁 PATCH 应成功: %d %s", resp.StatusCode, data)
	}
	body = env.komga.jsonBody(t, env.komga.requests("PATCH", "/api/v1/series/s1/metadata")[0])
	if body["titleLock"] != true || body["title"] != "改标题" {
		t.Fatalf("锁字段应原样回传: %v", body)
	}
	// 卷级 PATCH 直通
	resp, data = env.do("POST", "/api/books/s1-b1/metadata-raw", env.adminToken, map[string]any{
		"isbn": "9780000000001", "isbnLock": false,
	})
	if resp.StatusCode != 200 {
		t.Fatalf("卷级 PATCH 应成功: %d %s", resp.StatusCode, data)
	}
	body = env.komga.jsonBody(t, env.komga.requests("PATCH", "/api/v1/books/s1-b1/metadata")[0])
	if body["isbn"] != "9780000000001" || body["isbnLock"] != false {
		t.Fatalf("卷 PATCH 体错误: %v", body)
	}
}

// ---------- 8. Mylar ZIP 导出：字节有效 + series.json 合法 ----------

func TestV2MylarZip(t *testing.T) {
	env := newTestEnv(t)
	resp, data := env.do("GET", "/api/libraries/lib1/mylar?withCovers=1", env.adminToken, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("Mylar ZIP 应 200: %d %s", resp.StatusCode, data)
	}
	if ct := resp.Header.Get("Content-Type"); ct != "application/zip" {
		t.Fatalf("Content-Type 应为 application/zip: %s", ct)
	}
	zr, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		t.Fatalf("ZIP 字节无效: %v", err)
	}
	var seriesJSON, cover []byte
	for _, f := range zr.File {
		switch {
		case strings.HasSuffix(f.Name, "/series.json") || f.Name == "series.json":
			seriesJSON, _ = readFileZip(f)
		case strings.HasSuffix(f.Name, "cover.jpg"):
			cover, _ = readFileZip(f)
		}
	}
	if seriesJSON == nil {
		t.Fatalf("ZIP 内应有 series.json: files=%v", zr.File)
	}
	if cover == nil {
		t.Fatal("withCovers=1 应含 cover.jpg")
	}
	var parsed struct {
		Version  string `json:"version"`
		Metadata struct {
			Type      string `json:"type"`
			Name      string `json:"name"`
			Status    string `json:"status"`
			Authors   []any  `json:"authors"`
			Links     []any  `json:"links"`
			AgeRating any    `json:"age_rating"`
		} `json:"metadata"`
	}
	if err := json.Unmarshal(seriesJSON, &parsed); err != nil {
		t.Fatalf("series.json 不合法: %v\n%s", err, seriesJSON)
	}
	if parsed.Version != "1.0.2" || parsed.Metadata.Type != "comicSeries" {
		t.Fatalf("series.json 结构错误: %+v", parsed)
	}
	if !strings.Contains(parsed.Metadata.Name, "碧蓝之海") {
		t.Fatalf("metadata.name 应来自系列: %q", parsed.Metadata.Name)
	}
	// 状态映射：Komga 空 → Continuing
	if parsed.Metadata.Status != "Continuing" {
		t.Fatalf("状态映射错误: %q", parsed.Metadata.Status)
	}
}

// ---------- 6. kind=tag 打标：dry-run 零写入；真实模式写标题+标签 ----------

func TestV2TagJobDryRun(t *testing.T) {
	env := newTestEnv(t)
	// 换一个带版本标记的系列：文件夹带「爱藏版」、元数据标题被旧刮削抹平（桌面版一键修复场景）
	env.komga.seedSeries("tag1", "lib1", "[碧蓝之海][爱藏版]")
	env.komga.mu.Lock()
	env.komga.series["tag1"]["url"] = "/data/[碧蓝之海][爱藏版]"
	env.komga.series["tag1"]["metadata"].(map[string]any)["title"] = "碧蓝之海"
	env.komga.reqs = nil
	env.komga.mu.Unlock()

	_, data := env.do("POST", "/api/jobs", env.adminToken, map[string]any{
		"libraryId": "lib1", "seriesIds": []string{"tag1"}, "dryRun": true, "kind": "tag"})
	var created struct {
		ID string `json:"id"`
	}
	json.Unmarshal(data, &created)
	job := waitJob(t, env, created.ID)
	if job.Status != "done" || len(job.Items) == 0 {
		t.Fatalf("打标任务应完成: %s", data)
	}
	for _, r := range env.komga.requests("PATCH", "/api/v1/series/tag1/metadata") {
		t.Fatalf("Dry-run 零写入，却收到 PATCH: %v", r.Body)
	}
	// 预览条目
	if !strings.Contains(jobViewString(job), "将") {
		t.Fatalf("dry-run 条目应为「将…」预览: %s", jobViewString(job))
	}

	// 真实模式：tags + title 后缀 + MACHINE + publisher 填充（出版社词命中时）
	env.komga.mu.Lock()
	env.komga.reqs = nil
	env.komga.mu.Unlock()
	_, data = env.do("POST", "/api/jobs", env.adminToken, map[string]any{
		"libraryId": "lib1", "seriesIds": []string{"tag1"}, "dryRun": false, "kind": "tag"})
	json.Unmarshal(data, &created)
	job = waitJob(t, env, created.ID)
	patches := env.komga.requests("PATCH", "/api/v1/series/tag1/metadata")
	if len(patches) == 0 {
		t.Fatal("真实打标应有 PATCH")
	}
	body := env.komga.jsonBody(t, patches[0])
	t.Logf("tag patch: %s", mustJSON(body))
	tags := body["tags"].([]any)
	if tags[len(tags)-1] != "MACHINE" {
		t.Fatalf("打标应追加 MACHINE: %v", tags)
	}
	hasMarker := false
	for _, tg := range tags {
		if tg == "爱藏版" {
			hasMarker = true
		}
	}
	if !hasMarker {
		t.Fatalf("版本标记应落标签: %v", tags)
	}
	if title, ok := body["title"].(string); !ok || !strings.Contains(title, "爱藏版") {
		t.Fatalf("标题应有版本后缀: %v", body["title"])
	}
	// kind 徽章字段
	if _, data = env.do("GET", "/api/jobs/"+created.ID, env.adminToken, nil); true {
		var jobFull struct {
			Kind string `json:"kind"`
		}
		json.Unmarshal(data, &jobFull)
		if jobFull.Kind != "tag" {
			t.Fatalf("任务 kind 应为 tag: %s", data)
		}
	}
}

func jobViewString(j jobView) string {
	var sb strings.Builder
	for _, it := range j.Items {
		sb.WriteString(it.Status + " " + it.Message)
	}
	return sb.String()
}

// ---------- 2. 刮削状态与筛选（6 项） ----------

func TestV2StateFilters(t *testing.T) {
	env := newTestEnv(t)
	// 准备三个系列：s1 完整（封面+简介+链接）、s2 缺封面、s3 未刮
	env.komga.seedSeries("s2", "lib1", "[雾都孤儿]")
	env.komga.seedSeries("s3", "lib1", "[无主之地]")
	env.komga.mu.Lock()
	// s1/s2 都带链接（已刮）
	for _, sid := range []string{"s1", "s2"} {
		meta := env.komga.series[sid]["metadata"].(map[string]any)
		meta["links"] = []any{map[string]any{"label": "Btv", "url": "https://bgm.tv/subject/600001"}}
	}
	// s1 简介非空
	env.komga.series["s1"]["metadata"].(map[string]any)["summary"] = "有简介"
	env.komga.mu.Unlock()
	// 状态库：s1 封面+简介；s2 仅简介
	mustSetState(t, env, "s1", true, true)
	mustSetState(t, env, "s2", false, true)

	cases := map[string][]string{
		"all":        {"s1", "s2", "s3"},
		"scraped":    {"s1", "s2"},
		"unscraped":  {"s3"},
		"no_cover":   {"s2", "s3"},
		"no_summary": {"s3"},
		"incomplete": {"s2", "s3"},
		"complete":   {"s1"},
	}
	for filter, want := range cases {
		resp, data := env.do("GET", "/api/libraries/lib1/series?page=0&size=50&filter="+filter, env.adminToken, nil)
		if resp.StatusCode != 200 {
			t.Fatalf("filter=%s 应 200: %d %s", filter, resp.StatusCode, data)
		}
		var page struct {
			Content []struct {
				ID    string         `json:"id"`
				State map[string]any `json:"state"`
			} `json:"content"`
		}
		json.Unmarshal(data, &page)
		got := map[string]bool{}
		for _, it := range page.Content {
			got[it.ID] = true
		}
		if len(got) != len(want) {
			t.Fatalf("filter=%s 应命中 %v，得到 %v", filter, want, got)
		}
		for _, id := range want {
			if !got[id] {
				t.Fatalf("filter=%s 缺 %s: %v", filter, id, got)
			}
		}
	}
	// state 注入：有记录的带 hasCover/hasSummary；未记录的不带（前端显示 …）
	var page struct {
		Content []struct {
			ID    string         `json:"id"`
			State map[string]any `json:"state"`
		} `json:"content"`
	}
	_, data := env.do("GET", "/api/libraries/lib1/series?page=0&size=50", env.adminToken, nil)
	json.Unmarshal(data, &page)
	for _, it := range page.Content {
		switch it.ID {
		case "s1":
			if it.State["hasCover"] != true || it.State["hasSummary"] != true {
				t.Fatalf("s1 state 错误: %v", it.State)
			}
		case "s3":
			if it.State != nil {
				t.Fatalf("s3 无记录不应带 state: %v", it.State)
			}
		}
	}
}

func mustSetState(t *testing.T, e *testEnv, seriesID string, cover, summary bool) {
	t.Helper()
	st := store.ScrapeState{SeriesID: seriesID, HasCover: cover, HasSummary: summary}
	if err := e.store.SetScrapeState(st); err != nil {
		t.Fatal(err)
	}
}

// ---------- 5. 纠正匹配：手动 BGM 链接 → apply → ReplaceMatch ----------

func TestV2MatchApplyManual(t *testing.T) {
	env := newTestEnv(t)
	resp, data := env.do("POST", "/api/series/s1/match/apply", env.adminToken, map[string]any{
		"manual": "https://bgm.tv/subject/600001", "dryRun": false,
	})
	if resp.StatusCode != 200 {
		t.Fatalf("apply 应成功: %d %s", resp.StatusCode, data)
	}
	var out struct {
		JobID string `json:"jobId"`
	}
	json.Unmarshal(data, &out)
	if out.JobID == "" {
		t.Fatalf("应返回 jobId: %s", data)
	}
	job := waitJob(t, env, out.JobID)
	t.Logf("apply job: %s", mustJSON(job))
	if len(job.Items) != 1 || job.Items[0].Status != "ok" {
		t.Fatalf("纠正匹配任务应成功: %s", mustJSON(job))
	}
	env.komga.mu.Lock()
	meta := env.komga.series["s1"]["metadata"].(map[string]any)
	env.komga.mu.Unlock()
	if meta["summary"] == "" {
		t.Fatal("纠正匹配后应有简介")
	}
}

// ---------- 1.x BW 状态端点：索引落盘后 status 可见 ----------

func TestV2BWStatusAndImport(t *testing.T) {
	env := newTestEnv(t)
	resp, data := env.do("GET", "/api/settings/bw/status", env.adminToken, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("BW status 应 200: %d %s", resp.StatusCode, data)
	}
	var st struct {
		Indexed       bool `json:"indexed"`
		SeriesCount   int  `json:"seriesCount"`
		VolumeCount   int  `json:"volumeCount"`
		FormatVersion int  `json:"formatVersion"`
	}
	json.Unmarshal(data, &st)
	if st.Indexed {
		t.Fatalf("空数据目录应未索引: %s", data)
	}
	// 写入索引后可见
	writeBWIndex(t, env.dataDir, []map[string]any{{
		"id": "CNT_1", "t": "Test Series", "v": 2, "ty": 1,
		"vol": []map[string]any{{"o": 1, "d": "2020-01-01", "i": "9780000000001"}},
	}})
	resp, data = env.do("GET", "/api/settings/bw/status", env.adminToken, nil)
	json.Unmarshal(data, &st)
	if !st.Indexed || st.SeriesCount != 1 || st.VolumeCount != 1 || st.FormatVersion != 2 {
		t.Fatalf("BW status 应反映索引: %s", data)
	}
}

var _ = sources.BuildCoverURLs
var _ = http.DefaultClient
