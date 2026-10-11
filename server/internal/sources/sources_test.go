package sources

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	_ "modernc.org/sqlite"
)

// ============================================================
// 五源解析各 1：BW 索引 / Mox / MangaDex / 百科 / AI
// ============================================================

// buildBwTestSqlite 造一个最小官方目录库（表结构对齐 BookWalkerService 的 SQL）。
func buildBwTestSqlite(t *testing.T, path string) {
	t.Helper()
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatalf("打开测试 sqlite 失败: %v", err)
	}
	defer db.Close()
	stmts := []string{
		`CREATE TABLE publishers (id TEXT PRIMARY KEY, display_name TEXT)`,
		`CREATE TABLE labels (id TEXT PRIMARY KEY, name TEXT, publisher_id TEXT)`,
		`CREATE TABLE series_external_ids (series_id TEXT, external_id TEXT, type INT)`,
		`CREATE TABLE series (id TEXT PRIMARY KEY, title TEXT, alt_titles TEXT, description TEXT, image_id TEXT, type INT)`,
		`CREATE TABLE products (id TEXT PRIMARY KEY, series_id TEXT, parent_content_id TEXT, label_id TEXT, image_id TEXT, display_order INT, title TEXT, on_sale_at TEXT, description TEXT)`,
		`CREATE TABLE contributors (id TEXT PRIMARY KEY, name TEXT, name_alt TEXT)`,
		`CREATE TABLE product_contributors (product_id TEXT, contributor_id TEXT, role INT)`,
		`CREATE TABLE series_tags (series_id TEXT, tag_id TEXT)`,
		`CREATE TABLE tags (id TEXT PRIMARY KEY, namespace INT, name TEXT)`,
		`CREATE TABLE images (id TEXT PRIMARY KEY, name TEXT)`,
		`CREATE TABLE product_external_ids (product_id TEXT, external_id TEXT, type INT)`,
	}
	for _, q := range stmts {
		if _, err := db.Exec(q); err != nil {
			t.Fatalf("建表失败: %v", err)
		}
	}
	seed := []string{
		`INSERT INTO publishers VALUES ('p1','讲谈社')`,
		`INSERT INTO labels VALUES ('l1','KADOKAWA MANGA','p1')`,
		`INSERT INTO series_external_ids VALUES ('s1','3990',1)`,
		`INSERT INTO series VALUES ('s1','Grand Blue','["ぐらんぶる","Grand Blue 碧藍之海"]','<p>潜水与饮酒的<b>青春</b>喜剧。</p>','img1',1)`,
		`INSERT INTO products VALUES ('pv1','s1',NULL,'l1','img1',1,'Vol.1','2016-04-07T00:00:00Z','<p>第1卷。伊织入学。</p>')`,
		`INSERT INTO products VALUES ('pv2','s1',NULL,'l1','img2',2,'Vol.2','2016-09-07T00:00:00Z','第2卷。')`,
		`INSERT INTO contributors VALUES ('c1','井上堅二','')`,
		`INSERT INTO contributors VALUES ('c2','吉岡公威','')`,
		`INSERT INTO product_contributors VALUES ('pv1','c1',1)`,
		`INSERT INTO product_contributors VALUES ('pv1','c2',2)`,
		`INSERT INTO series_tags VALUES ('s1','t1')`,
		`INSERT INTO series_tags VALUES ('s1','t2')`,
		`INSERT INTO series_tags VALUES ('s1','t3')`,
		`INSERT INTO tags VALUES ('t1',1,'Shounen')`,
		`INSERT INTO tags VALUES ('t2',2,'Comedy')`,
		`INSERT INTO tags VALUES ('t3',3,'complete')`,
		`INSERT INTO images VALUES ('img1','cover_s1.jpg')`,
		`INSERT INTO images VALUES ('img2','cover_pv2.jpg')`,
		`INSERT INTO product_external_ids VALUES ('pv1','9784063883237',3)`,
		`INSERT INTO product_external_ids VALUES ('pv2','9784063885231',3)`,
	}
	for _, q := range seed {
		if _, err := db.Exec(q); err != nil {
			t.Fatalf("种子数据失败: %v", err)
		}
	}
}

// TestBWIndexBuildLoadMatch：sqlite → v2 索引 → 精确匹配/别名/模糊/候选。
func TestBWIndexBuildLoadMatch(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "bkwk-db.sqlite")
	indexPath := filepath.Join(dir, "bkwk-index.json.gz")
	buildBwTestSqlite(t, dbPath)

	if err := BuildBookWalkerIndexFromSqlite(dbPath, indexPath); err != nil {
		t.Fatalf("建索引失败: %v", err)
	}
	ix, err := LoadBookWalkerIndex(indexPath)
	if err != nil {
		t.Fatalf("加载索引失败: %v", err)
	}
	if ix.Count() != 1 || ix.VolumeTotal() != 2 {
		t.Fatalf("索引计数错误: count=%d vol=%d", ix.Count(), ix.VolumeTotal())
	}
	if ix.FormatVersion() != CurrentFormatVersion {
		t.Fatalf("格式版本应为 2，得到 %d", ix.FormatVersion())
	}

	// 主标题精确命中（含规范化大小写）
	if bw := ix.FindExact("grand blue"); bw == nil || bw.ID != "s1" {
		t.Fatalf("主标题精确匹配失败: %+v", bw)
	}
	// 别名命中
	if bw := ix.FindExact("ぐらんぶる"); bw == nil {
		t.Fatal("别名精确匹配失败")
	}
	// 模糊
	if hits := ix.SearchFuzzy("Grand", 5); len(hits) != 1 {
		t.Fatalf("模糊搜索应命中 1 条，得到 %d", len(hits))
	}
	bw := ix.FindExact("Grand Blue")
	// 系列级字段
	if bw.Publisher != "讲谈社" || bw.Imprint != "KADOKAWA MANGA" {
		t.Fatalf("出版社/品牌错误: %q / %q", bw.Publisher, bw.Imprint)
	}
	if !bw.Complete || bw.VolumeCount != 2 || !bw.IsComic() {
		t.Fatalf("complete/卷数/类型错误: %+v", bw)
	}
	if len(bw.Genres) != 1 || bw.Genres[0] != "Shounen" || len(bw.Themes) != 1 || bw.Themes[0] != "Comedy" {
		t.Fatalf("题材标签错误: %v %v", bw.Genres, bw.Themes)
	}
	if !strings.Contains(bw.Description, "潜水") || strings.Contains(bw.Description, "<") {
		t.Fatalf("简介应去 HTML 标签: %q", bw.Description)
	}
	if len(bw.Authors) != 2 || bw.Authors[0].Name != "井上堅二" || bw.Authors[0].Role != 1 {
		t.Fatalf("作者错误: %+v", bw.Authors)
	}
	if bw.SeriesURL != "https://bookwalker.jp/series/3990/" {
		t.Fatalf("系列链接错误: %s", bw.SeriesURL)
	}
	// 每卷级字段：卷 2 有独立封面（image_id 不同）；卷 1 独立简介
	v1, v2 := bw.Volumes[0], bw.Volumes[1]
	if v1.Order != 1 || v1.ReleaseDate != "2016-04-07" || v1.Isbn != "9784063883237" {
		t.Fatalf("卷1字段错误: %+v", v1)
	}
	if v1.CoverName != "" || v1.Summary == "" || !strings.Contains(v1.Summary, "伊织") {
		t.Fatalf("卷1封面/简介错误: cover=%q summary=%q", v1.CoverName, v1.Summary)
	}
	if v2.CoverName != "cover_pv2.jpg" {
		t.Fatalf("卷2独立封面错误: %q", v2.CoverName)
	}
	// 候选转换
	cand := ix.ToCandidate(bw)
	if cand.Source != BwSource || cand.BwID != "s1" || cand.CoverURL != "https://c.bookwalker.jp/cover_s1.jpg" {
		t.Fatalf("候选转换错误: %+v", cand)
	}
	// 封面候选 CDN 兜底
	urls := BuildCoverURLs("x.jpg")
	if len(urls) != 2 || urls[1] != "https://d2snwnmzyr8jue.cloudfront.net/x.jpg" {
		t.Fatalf("封面 URL 组错误: %v", urls)
	}
}

// TestBWImportMagicDetection：按文件头识别 zst/sqlite。
func TestBWImportMagicDetection(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "a.sqlite"), []byte("SQLite format 3\x00"+strings.Repeat("x", 100)), 0o644); err != nil {
		t.Fatal(err)
	}
	if !IsSqliteFile(filepath.Join(dir, "a.sqlite")) {
		t.Fatal("sqlite 魔数识别失败")
	}
	zst := []byte{0x28, 0xB5, 0x2F, 0xFD}
	if err := os.WriteFile(filepath.Join(dir, "b.zst"), append(zst, 1, 2, 3), 0o644); err != nil {
		t.Fatal(err)
	}
	if !IsZstdFile(filepath.Join(dir, "b.zst")) {
		t.Fatal("zstd 魔数识别失败")
	}
	if IsSqliteFile(filepath.Join(dir, "b.zst")) || IsZstdFile(filepath.Join(dir, "a.sqlite")) {
		t.Fatal("魔数互判错误")
	}
}

// ---------- Mox ----------

// mockMoxServer Mox 站内 mock（Go httptest 版）。
type mockMoxServer struct {
	detailHits atomic.Int32
	first520   bool
	searchHits atomic.Int32
	frameHits  atomic.Int32
	searchBody atomic.Value // string
}

func (m *mockMoxServer) handler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/data_list.php":
			m.searchHits.Add(1)
			m.searchBody.Store(r.URL.Query().Get("s"))
			w.Header().Set("Content-Type", "text/html; charset=utf-8")
			term := r.URL.Query().Get("s")
			fmt.Fprintf(w, "<html><script>x</script>\ndatainfo-B=漫画,123,%s,井上堅二,2014-05-09,https://img.mxomo.com/cover/123.jpg?sign=a,b\ndatainfo-B=漫画,456,%s外傳,其他人,2016-01-01,https://img.mxomo.com/cover/456.jpg\n</html>", term, term)
		case strings.HasPrefix(r.URL.Path, "/b/"):
			m.detailHits.Add(1)
			if m.first520 && m.detailHits.Load() == 1 {
				w.WriteHeader(520)
				w.Write([]byte("cloudflare origin error"))
				return
			}
			w.Header().Set("Content-Type", "text/html; charset=utf-8")
			w.Write([]byte(`<html><body>` +
				`<img id="logo_r18" src="/r18.png">` +
				`<div class="name_main">ぐらんぶる</div>` +
				`<div class="name_subt">(Grand Blue) 狀態：連載</div>` +
				`<div class="name_subt">分類：喜劇 校園　版本：東立　最後出版：2020</div>` +
				`<div id="div_desctext">大學生伊織加入潛水社團，<br>圍繞潛水與飲酒的青春故事。【電子版】</div>` +
				`<a href="https://bookof.moe/s/AUT/1">井上堅二</a>` +
				`<a href="https://bookof.moe/s/AUT/2">吉岡公威</a>` +
				`<script>window.iframe_action.location.href="/data_vol.php?id=123"</script>` +
				`</body></html>`))
		case r.URL.Path == "/data_vol.php":
			m.frameHits.Add(1)
			w.Header().Set("Content-Type", "text/html; charset=utf-8")
			w.Write([]byte("datainfo-V=1, Vol.01, 漫画, x, /v/123_1.jpg, y\n" +
				"datainfo-V=2, Vol.02, 漫画, x, https://img.mxomo.com/v/123_2.jpg, y"))
		default:
			w.WriteHeader(404)
		}
	}
}

// TestMox520BackoffAndParse：520 一次 → 退避重试成功 + 详情解析字段全集。
func TestMox520BackoffAndParse(t *testing.T) {
	srv := httptest.NewServer((&mockMoxServer{first520: true}).handler())
	defer srv.Close()
	mc, err := NewMoxClient(ProxyConfig{}, srv.URL, nil, 0)
	if err != nil {
		t.Fatal(err)
	}
	start := time.Now()
	mox, err := mc.GetSeries(context.Background(), "123")
	if err != nil {
		t.Fatalf("520 后重试应成功: %v", err)
	}
	if time.Since(start) < time.Second {
		t.Fatalf("520 退避应有延迟（1.5s），实际 %v", time.Since(start))
	}
	if mox.Title != "ぐらんぶる" { // 主标题保留繁体原样（原名）
		t.Fatalf("原名错误: %q", mox.Title)
	}
	if mox.OriTitle != "ぐらんぶる" {
		t.Fatalf("OriTitle 错误: %q", mox.OriTitle)
	}
	if mox.EngName != "Grand Blue" {
		t.Fatalf("英文名错误: %q", mox.EngName)
	}
	if mox.Status != "ONGOING" {
		t.Fatalf("状态错误: %q", mox.Status)
	}
	if !mox.IsAdult {
		t.Fatal("r18 图标存在且未隐藏 → 应判定成人")
	}
	if mox.Summary != "大学生伊织加入潜水社团，围绕潜水与饮酒的青春故事。" {
		t.Fatalf("简介错误: %q", mox.Summary)
	}
	if len(mox.Authors) != 2 || mox.Authors[0] != "井上坚二" {
		t.Fatalf("作者错误（应 T2S 转简体）: %v", mox.Authors)
	}
	if mox.Publisher != "东立" {
		t.Fatalf("出版社错误（应 T2S 转简体）: %q", mox.Publisher)
	}
	if len(mox.Tags) != 2 || mox.Tags[0] != "喜剧" || mox.Tags[1] != "校园" {
		t.Fatalf("题材错误: %v", mox.Tags)
	}
	if mox.CoverFrameURL != srv.URL+"/data_vol.php?id=123" {
		t.Fatalf("封面帧页错误: %q", mox.CoverFrameURL)
	}
	// 每卷封面：相对路径补基址 + 去重
	covers, err := mc.GetVolumeCoverURLs(context.Background(), mox.CoverFrameURL)
	if err != nil || len(covers) != 2 {
		t.Fatalf("封面解析错误: %v %v", covers, err)
	}
	if covers[0] != srv.URL+"/v/123_1.jpg" || covers[1] != "https://img.mxomo.com/v/123_2.jpg" {
		t.Fatalf("封面 URL 错误: %v", covers)
	}
}

// TestMoxSearchParse：搜索行解析（S2T 查询 / T2S 结果 / 行内封面含逗号）。
func TestMoxSearchParse(t *testing.T) {
	mm := &mockMoxServer{}
	srv := httptest.NewServer(mm.handler())
	defer srv.Close()
	mc, _ := NewMoxClient(ProxyConfig{}, srv.URL, nil, 0)
	// 繁体词查询
	res, err := mc.Search(context.Background(), "碧藍之海", 8)
	if err != nil {
		t.Fatal(err)
	}
	if got := mS(mm.searchBody.Load()); got != "碧藍之海" {
		t.Fatalf("查询词应保持繁体（S2T 后仍含繁体词表）：%q", got)
	}
	if len(res) != 2 || res[0].ID != "123" || res[0].Title != "碧蓝之海" || res[0].Author != "井上坚二" {
		t.Fatalf("搜索解析错误（结果应已 T2S 转简体）: %+v", res)
	}
	if res[1].Title != "碧蓝之海外传" {
		t.Fatalf("搜索第二行错误: %+v", res[1])
	}
	if res[0].CoverURL != "https://img.mxomo.com/cover/123.jpg?sign=a,b" {
		t.Fatalf("封面 URL（sign 含逗号）错误: %q", res[0].CoverURL)
	}
}

func mS(v any) string {
	s, _ := v.(string)
	return s
}

// ---------- MangaDex ----------

func TestMangaDexParse(t *testing.T) {
	body := `{"result":"ok","data":[{"id":"a1b2","type":"manga","attributes":{
		"title":{"ja":"ぐらんぶる","zh-Hans":"碧蓝之海","en":"Grand Blue"},
		"altTitles":[{"en":"Grand Blue"},{"ko":"그랜드블루"}],
		"description":{"en":"Dive and drink.","ja":"潜水と酒。"},
		"originalLanguage":"ja","status":"ongoing",
		"tags":[{"attributes":{"name":{"en":"Comedy"}}}]
		},"relationships":[{"id":"c1","type":"cover_art","attributes":{"fileName":"cover.jpg"}}]}]}`
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/manga" && r.URL.Query().Get("title") == "碧蓝之海" {
			w.Write([]byte(body))
			return
		}
		w.WriteHeader(404)
	}))
	defer srv.Close()
	md, err := NewMangaDexClient(ProxyConfig{}, srv.URL, true)
	if err != nil {
		t.Fatal(err)
	}
	list, err := md.Search(context.Background(), "碧蓝之海", 8)
	if err != nil || len(list) != 1 {
		t.Fatalf("搜索失败: %v %v", list, err)
	}
	m := list[0]
	if m.Title != "碧蓝之海" { // zh-Hans 优先
		t.Fatalf("标题应取 zh-Hans: %q", m.Title)
	}
	if m.OriTitle != "ぐらんぶる" {
		t.Fatalf("原名应取 ja: %q", m.OriTitle)
	}
	if m.Description != "Dive and drink." {
		t.Fatalf("简介应取 en: %q", m.Description)
	}
	if m.CoverURL != "https://uploads.mangadex.org/covers/a1b2/cover.jpg" {
		t.Fatalf("封面 URL 错误: %q", m.CoverURL)
	}
	if len(m.AltTitles) != 2 || len(m.Tags) != 1 || m.Tags[0] != "Comedy" {
		t.Fatalf("别名/标签错误: %v %v", m.AltTitles, m.Tags)
	}
}

// ---------- 百度百科 ----------

func TestBaikeParse(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/openapi/BaikeLemmaCardApi" {
			if r.Header.Get("Referer") == "" {
				t.Error("百科请求应带 Referer（bkimg 防盗链对齐）")
			}
			if r.URL.Query().Get("bk_key") == "碧蓝之海" {
				w.Write([]byte(`{"id":1,"newLemmaId":9527,"title":"碧蓝之海","desc":"井上坚二作画的日本漫画",
				"abstract":"潜水与饮酒的青春喜剧。","image":"https://bkimg.cdn.bcebos.com/pic/x?x-bce-process=image/f_auto",
				"url":"http://baike.baidu.com/item/x",
				"card":[{"name":"中文名","value":["碧蓝之海"]},{"name":"原版名称","value":["ぐらんぶる"]},
				{"name":"作画","value":["吉冈公威"]},{"name":"出版社","value":["讲谈社"]},{"name":"类型","value":["喜剧","校园"]}]}`))
			} else {
				w.Write([]byte(`{"errno":-1,"errMsg":"词条不存在"}`))
			}
			return
		}
		w.WriteHeader(404)
	}))
	defer srv.Close()
	bk, err := NewBaikeClient(srv.URL)
	if err != nil {
		t.Fatal(err)
	}
	card, err := bk.SearchCard(context.Background(), "碧蓝之海")
	if err != nil || card == nil {
		t.Fatalf("词条解析失败: %v", err)
	}
	if card.Title != "碧蓝之海" || card.LemmaID != 9527 {
		t.Fatalf("标题/id 错误: %+v", card)
	}
	if card.Field("原版名称") != "ぐらんぶる" {
		t.Fatalf("原版名称应保留日文原样（不做 T2S）: %q", card.Field("原版名称"))
	}
	if card.Field("出版社") != "讲谈社" {
		t.Fatalf("出版社错误: %q", card.Field("出版社"))
	}
	if !card.LooksLikeMangaWork() {
		t.Fatal("含 作画/出版社 字段应判定为漫画作品词条")
	}
	if card.EntryURL != "https://baike.baidu.com/item/x" {
		t.Fatalf("词条页应转 https: %q", card.EntryURL)
	}
	// 人物词条 → 非漫画作品
	person := &BaikeCard{Title: "某漫画家", Fields: []BaikeCardField{{Name: "职业", Value: "漫画家"}}}
	if person.LooksLikeMangaWork() {
		t.Fatal("人物词条不应判定为漫画作品")
	}
	// StripProcessParam（与 C# 同语义：保留后续参数，前缀的 & 留给调用方容忍）
	if got := StripProcessParam("https://bkimg.cdn.bcebos.com/pic/x?x-bce-process=image/f_auto&token=1"); got != "https://bkimg.cdn.bcebos.com/pic/x&token=1" {
		t.Fatalf("剥参数失败: %q", got)
	}
	if got := StripProcessParam("https://bkimg.cdn.bcebos.com/pic/x?x-bce-process=image/f_auto"); got != "https://bkimg.cdn.bcebos.com/pic/x" {
		t.Fatalf("剥参数失败（无后续参数）: %q", got)
	}
}

// ---------- AI ----------

func TestAIClientParseAndCache(t *testing.T) {
	ResetAICaches()
	var calls atomic.Int32
	var mode atomic.Value // string：identify|pick|translate|synopsis
	mode.Store("identify")
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		var body struct {
			Messages []struct {
				Role    string `json:"role"`
				Content string `json:"content"`
			} `json:"messages"`
		}
		json.NewDecoder(r.Body).Decode(&body)
		user := ""
		for _, m := range body.Messages {
			if m.Role == "user" {
				user = m.Content
			}
		}
		if r.Header.Get("Authorization") != "Bearer test-key" {
			t.Errorf("AI 请求应带 Bearer key")
		}
		if !strings.HasSuffix(r.URL.Path, "/chat/completions") {
			t.Errorf("应请求 /chat/completions，得到 %s", r.URL.Path)
		}
		switch {
		case strings.Contains(user, "请判断这是哪部"):
			fenced := "```json\\n{\\\"jp\\\":\\\"ぐらんぶる\\\",\\\"cn\\\":\\\"碧蓝之海\\\",\\\"en\\\":\\\"Grand Blue\\\",\\\"confident\\\":true}\\n```"
			w.Write([]byte(`{"choices":[{"message":{"content":"` + fenced + `"}}]}`))
		case strings.Contains(user, "编号从 1 开始"):
			w.Write([]byte(`{"choices":[{"message":{"content":"{\"index\":2}"}}]}`))
		case strings.Contains(user, "完整翻译成简体中文"):
			w.Write([]byte(`{"choices":[{"message":{"content":"译文：潜水与饮酒的青春喜剧。"}}]}`))
		default:
			w.Write([]byte(`{"choices":[{"message":{"content":"UNKNOWN"}}]}`))
		}
	}))
	defer srv.Close()
	ai, err := NewAiClient(srv.URL, "test-key", "mock", ProxyConfig{}, 5*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	// 识别：```json 代码块解析
	r1, err := ai.IdentifySeries(context.Background(), "未知名", []string{"未知名"})
	if err != nil || r1.Jp != "ぐらんぶる" || r1.Cn != "碧蓝之海" || !r1.Confident {
		t.Fatalf("识别解析错误: %+v %v", r1, err)
	}
	// 缓存命中：第二次不再调接口（calls 不变）
	_, err = ai.IdentifySeries(context.Background(), "未知名", []string{"未知名"})
	if err != nil {
		t.Fatalf("缓存命中应成功: %v", err)
	}
	if calls.Load() != 1 {
		t.Fatalf("AI 应只被调用一次（进程内缓存），实际 %d", calls.Load())
	}
	// 选择：index 2
	pick, err := ai.PickBest(context.Background(), "目标", []Candidate{
		{Title: "候选A"}, {Title: "候选B"}, {Title: "候选C"},
	})
	if err != nil || pick == nil || pick.Title != "候选B" {
		t.Fatalf("智能选择错误: %+v %v", pick, err)
	}
	// 翻译：剥前缀
	out, err := ai.TranslateToChinese(context.Background(), "ぐらんぶるは潜 water story")
	if err != nil || out != "潜水与饮酒的青春喜剧。" {
		t.Fatalf("翻译错误: %q %v", out, err)
	}
	// NeedsChineseTranslation 判定
	if NeedsChineseTranslation("潜水与饮酒的青春喜剧。") {
		t.Fatal("纯中文不需要翻译")
	}
	if !NeedsChineseTranslation("潜水とお酒の話。") {
		t.Fatal("含假名需要翻译")
	}
	if !NeedsChineseTranslation("Dive and drink story.") {
		t.Fatal("英文为主需要翻译")
	}
	if NeedsChineseTranslation("") {
		t.Fatal("空文本不需要翻译")
	}
	// 补写：UNKNOWN → 空
	syn, err := ai.GenerateSynopsis(context.Background(), "不认识的作品", "", "")
	if err == nil || syn != "" {
		t.Fatalf("UNKNOWN 应转失败: %q %v", syn, err)
	}
}

// ---------- 代理 Transport ----------

func TestProxyTransportHTTP(t *testing.T) {
	// httptest 服务器当 HTTP 代理：应收到绝对 URI 请求
	var hit atomic.Bool
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Host != "" { // 代理请求 URL 为绝对 URI
			hit.Store(true)
		}
		w.Write([]byte("proxied"))
	}))
	defer srv.Close()
	host, port := splitHostPort(srv.URL)
	hc, err := NewHTTPClient(ProxyConfig{Protocol: "http", Host: host, Port: atoiT(port)}, 5*time.Second, "t", "")
	if err != nil {
		t.Fatal(err)
	}
	resp, err := hc.Get("http://target.example.com/data")
	if err != nil {
		t.Fatal(err)
	}
	body, _ := ioReadAllTest(resp.Body)
	resp.Body.Close()
	if !hit.Load() || string(body) != "proxied" {
		t.Fatalf("HTTP 代理未生效: hit=%v body=%s", hit.Load(), body)
	}
}

func TestProxyTransportSOCKS5(t *testing.T) {
	// 最小 SOCKS5 服务器（无鉴权 + CONNECT）
	ln, err := netListen()
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			go serveSocks5(conn)
		}
	}()

	// 目标站（真实 HTTP 通过 socks 隧道）
	target := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("via-socks5"))
	}))
	defer target.Close()
	THost, TPort := splitHostPort(target.URL)

	hc, err := NewHTTPClient(ProxyConfig{Protocol: "socks5", Host: "127.0.0.1", Port: portOf(ln)}, 5*time.Second, "t", "")
	if err != nil {
		t.Fatal(err)
	}
	resp, err := hc.Get("http://" + THost + ":" + TPort + "/x")
	if err != nil {
		t.Fatalf("SOCKS5 隧道失败: %v", err)
	}
	body, _ := ioReadAllTest(resp.Body)
	resp.Body.Close()
	if string(body) != "via-socks5" {
		t.Fatalf("SOCKS5 响应错误: %s", body)
	}
}

func TestProxyConfigValidate(t *testing.T) {
	if msg := (ProxyConfig{}).Validate(); msg != "" {
		t.Fatalf("空配置应合法: %s", msg)
	}
	if msg := (ProxyConfig{Protocol: "socks5", Host: "1.2.3.4", Port: 70000}).Validate(); msg == "" {
		t.Fatal("端口越界应报错")
	}
	if msg := (ProxyConfig{Protocol: "socks5", Host: "http://x", Port: 1080}).Validate(); msg == "" {
		t.Fatal("主机带 scheme 应报错")
	}
}
