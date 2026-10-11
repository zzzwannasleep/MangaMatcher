package api

import (
	"encoding/json"
	"log/slog"
	"mime"
	"os"
	"strings"
	"testing"
)

func testLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelWarn}))
}

// ---------- a. 登录成功/失败 ----------

func TestLoginSuccessAndFailure(t *testing.T) {
	env := newTestEnv(t)

	// 成功
	resp, data := env.do("POST", "/api/auth/login", "", map[string]string{"username": "admin", "password": "admin123"})
	if resp.StatusCode != 200 {
		t.Fatalf("正确密码应 200，得到 %d %s", resp.StatusCode, data)
	}
	if !strings.Contains(string(data), `"token"`) {
		t.Fatalf("响应缺少 token: %s", data)
	}
	// 失败：错误密码
	resp, data = env.do("POST", "/api/auth/login", "", map[string]string{"username": "admin", "password": "wrong"})
	if resp.StatusCode != 401 {
		t.Fatalf("错误密码应 401，得到 %d %s", resp.StatusCode, data)
	}
	if !strings.Contains(string(data), "error") {
		t.Fatalf("错误响应缺少 error 字段: %s", data)
	}
}

// ---------- b. master 访问未授权库的 series → 403 ----------

func TestMasterForbiddenOnUnauthorizedLibrary(t *testing.T) {
	env := newTestEnv(t)
	env.komga.seedSeries("s2", "lib2", "[另一部作品]")
	env.komga.libraries = append(env.komga.libraries, map[string]any{"id": "lib2", "name": "Lib2", "root": "/d2"})

	// admin 创建 master（未授权任何库）
	_, data := env.do("POST", "/api/users", env.adminToken, map[string]any{
		"username": "m1", "password": "pass1234", "role": "master", "libraries": []string{},
	})
	masterToken := env.login("m1", "pass1234")
	_ = data

	resp, data := env.do("GET", "/api/libraries/lib2/series?page=0&size=50", masterToken, nil)
	if resp.StatusCode != 403 {
		t.Fatalf("master 访问未授权库应 403，得到 %d %s", resp.StatusCode, data)
	}
	resp, data = env.do("GET", "/api/series/s2", masterToken, nil)
	if resp.StatusCode != 403 {
		t.Fatalf("master 访问未授权库系列应 403，得到 %d %s", resp.StatusCode, data)
	}
	// master 刮未授权库 → 403
	resp, data = env.do("POST", "/api/jobs", masterToken, map[string]any{"libraryId": "lib2", "seriesIds": []string{}, "dryRun": true})
	if resp.StatusCode != 403 {
		t.Fatalf("master 刮未授权库应 403，得到 %d %s", resp.StatusCode, data)
	}
}

// ---------- c. admin 建号并给 master 分配库后 master 能看见该库 ----------

func TestAdminAssignLibraryToMaster(t *testing.T) {
	env := newTestEnv(t)

	resp, data := env.do("POST", "/api/users", env.adminToken, map[string]any{
		"username": "m2", "password": "pass1234", "role": "master", "libraries": []string{"lib1"},
	})
	if resp.StatusCode != 200 {
		t.Fatalf("创建 master 失败: %d %s", resp.StatusCode, data)
	}
	masterToken := env.login("m2", "pass1234")

	// master 只见被分配的库
	resp, data = env.do("GET", "/api/libraries", masterToken, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("GET /api/libraries 失败: %d %s", resp.StatusCode, data)
	}
	if !strings.Contains(string(data), `"lib1"`) {
		t.Fatalf("master 应看见 lib1: %s", data)
	}
	if strings.Contains(string(data), `"lib2"`) {
		t.Fatalf("master 不应看见未分配的 lib2: %s", data)
	}
	// master 对已授权库可以访问系列
	resp, data = env.do("GET", "/api/libraries/lib1/series?page=0&size=50", masterToken, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("master 访问已授权库应 200: %d %s", resp.StatusCode, data)
	}
}

// ---------- d. dry-run 任务产出 changed 差异且 mock Komga 未收到任何 PATCH ----------

func TestDryRunProducesDiffWithoutPatch(t *testing.T) {
	env := newTestEnv(t)
	job := env.submitJob(t, env.adminToken, "lib1", []string{"s1"}, true)

	if job.Status != "done" {
		t.Fatalf("任务状态应为 done: %+v", job)
	}
	if len(job.Items) != 1 || job.Items[0].Status != "ok" {
		t.Fatalf("dry-run 条目异常: %+v", job.Items)
	}
	item := job.Items[0]
	if item.SubjectID != 600001 {
		t.Fatalf("应匹配到 subject 600001: %d", item.SubjectID)
	}
	if item.MatchTitle != "碧藍之海" {
		t.Fatalf("匹配标题应为 碧藍之海: %s", item.MatchTitle)
	}
	// changed 差异：title/status/totalBookCount/tags 等
	ch := item.Changed
	if ch == nil {
		t.Fatalf("dry-run 必须产出 changed 差异")
	}
	for _, key := range []string{"title", "status", "totalBookCount", "tags", "authors"} {
		if _, ok := ch[key]; !ok {
			t.Fatalf("changed 缺少字段 %s: %v", key, ch)
		}
	}
	if ch["title"][1] != "碧蓝之海" {
		t.Fatalf("title 新值应为 碧蓝之海: %v", ch["title"])
	}
	if ch["status"][1] != "ENDED" {
		t.Fatalf("status 新值应为 ENDED: %v", ch["status"])
	}
	// mock Komga 未收到任何 PATCH / 上传 / 收藏夹写入
	if patches := env.komga.requests("PATCH", ""); len(patches) > 0 {
		t.Fatalf("dry-run 不得发出 PATCH，收到 %d 个: %+v", len(patches), patches[0])
	}
	if posts := env.komga.requests("POST", "/api/v1/series/s1/thumbnails"); len(posts) > 0 {
		t.Fatalf("dry-run 不得上传封面")
	}
	if colls := env.komga.requests("POST", "/api/v1/collections"); len(colls) > 0 {
		t.Fatalf("dry-run 不得创建收藏夹")
	}
}

// ---------- e. 真实执行任务后 mock 收到的 PATCH 体字段正确 + 封面 multipart 收到 ----------

func TestRealRunSendsCorrectPatchesAndCover(t *testing.T) {
	env := newTestEnv(t)
	job := env.submitJob(t, env.adminToken, "lib1", []string{"s1"}, false)

	if job.Status != "done" {
		t.Fatalf("任务状态应为 done: %+v", job)
	}
	if len(job.Items) != 1 || job.Items[0].Status != "ok" {
		t.Fatalf("执行条目异常: %+v", job.Items)
	}

	// ---- 系列 PATCH 体 ----
	seriesPatches := env.komga.requests("PATCH", "/api/v1/series/s1/metadata")
	if len(seriesPatches) == 0 {
		t.Fatalf("未收到系列元数据 PATCH")
	}
	body := env.komga.jsonBody(t, seriesPatches[0])
	if body["title"] != "碧蓝之海" {
		t.Fatalf("PATCH title 应为 碧蓝之海: %v", body["title"])
	}
	if body["status"] != "ENDED" {
		t.Fatalf("PATCH status 应为 ENDED: %v", body["status"])
	}
	if v, ok := body["totalBookCount"].(float64); !ok || v != 16 {
		t.Fatalf("PATCH totalBookCount 应为 16: %v", body["totalBookCount"])
	}
	tags := body["tags"].([]any)
	tagSet := map[string]bool{}
	for _, tg := range tags {
		tagSet[tg.(string)] = true
	}
	for _, want := range []string{"搞笑", "校园", "青春", "9分", "MACHINE"} {
		if !tagSet[want] {
			t.Fatalf("PATCH tags 缺少 %s: %v", want, tags)
		}
	}
	authors := body["authors"].([]any)
	foundWriter := false
	foundPenciller := false
	for _, a := range authors {
		m := a.(map[string]any)
		if m["name"] == "井上坚二" && m["role"] == "writer" {
			foundWriter = true
		}
		if m["name"] == "吉冈公威" && m["role"] == "penciller" {
			foundPenciller = true
		}
	}
	if !foundWriter || !foundPenciller {
		t.Fatalf("PATCH authors 缺少 writer/penciller: %v", authors)
	}

	// ---- 书 PATCH 体 ----
	bookPatches := env.komga.requests("PATCH", "/api/v1/books/s1-b1/metadata")
	if len(bookPatches) == 0 {
		t.Fatalf("未收到书元数据 PATCH")
	}
	bbody := env.komga.jsonBody(t, bookPatches[0])
	if bbody["title"] != "卷 01" {
		t.Fatalf("书 PATCH title 应为 卷 01: %v", bbody["title"])
	}
	if bbody["number"] != "01" {
		t.Fatalf("书 PATCH number 应为 01: %v", bbody["number"])
	}
	if bbody["isbn"] != "9784063883237" {
		t.Fatalf("书 PATCH isbn 应为 9784063883237: %v", bbody["isbn"])
	}
	if bbody["releaseDate"] != "2014-05-09" {
		t.Fatalf("书 PATCH releaseDate 应为 2014-05-09: %v", bbody["releaseDate"])
	}
	if s, ok := bbody["summary"].(string); !ok || !strings.Contains(s, "第1卷") {
		t.Fatalf("书 PATCH summary 应含 第1卷: %v", bbody["summary"])
	}

	// ---- 封面 multipart 上传 ----
	coverPosts := env.komga.requests("POST", "/api/v1/series/s1/thumbnails")
	if len(coverPosts) == 0 {
		t.Fatalf("未收到系列封面上传")
	}
	cover := coverPosts[0]
	ct := cover.Header.Get("Content-Type")
	mediaType, _, _ := mime.ParseMediaType(ct)
	if mediaType != "multipart/form-data" {
		t.Fatalf("封面上传应为 multipart/form-data: %s", ct)
	}
	if len(cover.Body) == 0 {
		t.Fatalf("封面上传体为空")
	}
	if !strings.Contains(string(cover.Body), "series_cover_s1.jpg") {
		t.Fatalf("multipart 应含文件名 series_cover_s1.jpg")
	}
	// 书封面也收到
	if len(env.komga.requests("POST", "/api/v1/books/s1-b1/thumbnails")) == 0 {
		t.Fatalf("未收到书封面上传")
	}

	// ---- 缩略图代理带 Accept: image/jpeg ----
	resp, data := env.do("GET", "/api/series/s1/thumbnail", env.adminToken, nil)
	if resp.StatusCode != 200 || !strings.Contains(resp.Header.Get("Content-Type"), "image/jpeg") {
		t.Fatalf("封面代理应 200 image/jpeg: %d %s", resp.StatusCode, string(data))
	}
	thumbGets := env.komga.requests("GET", "/api/v1/series/s1/thumbnail")
	if len(thumbGets) == 0 || thumbGets[len(thumbGets)-1].Header.Get("Accept") != "image/jpeg" {
		t.Fatalf("代理请求头必须带 Accept: image/jpeg")
	}
}

// ---------- f. 刮削失败系列进入"待手动匹配"收藏夹（POST /collections 被调用） ----------

func TestFailedSeriesGoesToManualCollection(t *testing.T) {
	env := newTestEnv(t)
	// s2：搜索无任何结果 → 匹配失败
	env.komga.seedSeries("s2", "lib1", "[彻底查无此书的作品名]")

	job := env.submitJob(t, env.adminToken, "lib1", []string{"s2"}, false)
	if job.Status != "done" {
		t.Fatalf("任务状态应为 done: %+v", job)
	}
	if len(job.Items) != 1 || job.Items[0].Status != "manual" {
		t.Fatalf("匹配失败系列应进 manual: %+v", job.Items)
	}

	colls := env.komga.requests("POST", "/api/v1/collections")
	if len(colls) == 0 {
		t.Fatalf("失败系列必须触发 POST /collections")
	}
	body := env.komga.jsonBody(t, colls[0])
	if body["name"] != "待手动匹配" {
		t.Fatalf("收藏夹名应为 待手动匹配: %v", body["name"])
	}
	ids, _ := body["seriesIds"].([]any)
	if len(ids) != 1 || ids[0] != "s2" {
		t.Fatalf("收藏夹成员应为 [s2]: %v", body["seriesIds"])
	}
}

// ---------- g. admin 不能删除自己/最后一个 admin ----------

func TestAdminCannotDeleteSelfOrLastAdmin(t *testing.T) {
	env := newTestEnv(t)

	// 查出自己的 id
	resp, data := env.do("GET", "/api/me", env.adminToken, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("GET /api/me 失败: %d %s", resp.StatusCode, data)
	}
	var me struct {
		ID string `json:"id"`
	}
	json.Unmarshal(data, &me)

	// 删除自己 → 400
	resp, data = env.do("DELETE", "/api/users/"+me.ID, env.adminToken, nil)
	if resp.StatusCode != 400 {
		t.Fatalf("admin 删除自己应 400: %d %s", resp.StatusCode, data)
	}
	// 降级最后一个 admin → 400
	resp, data = env.do("PATCH", "/api/users/"+me.ID, env.adminToken, map[string]any{"role": "master"})
	if resp.StatusCode != 400 {
		t.Fatalf("降级最后一个 admin 应 400: %d %s", resp.StatusCode, data)
	}
	// 再建一个 admin 后，第二个 admin 可以删第一个……反之 self 保护仍生效
	resp, data = env.do("POST", "/api/users", env.adminToken, map[string]any{
		"username": "admin2", "password": "pass1234", "role": "admin", "libraries": []string{},
	})
	if resp.StatusCode != 200 {
		t.Fatalf("创建第二个 admin 失败: %d %s", resp.StatusCode, data)
	}
	token2 := env.login("admin2", "pass1234")
	resp, data = env.do("DELETE", "/api/users/"+me.ID, token2, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("admin2 应能删除 admin1: %d %s", resp.StatusCode, data)
	}
}
