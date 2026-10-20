package sources

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"
)

// ehSearchHTML 真实搜索页结构的最小化 fixture（照 .scratch/eh/search.html 的 DOM 形状截取）：
// 每行 <tr> 里先有缩略图锚点、再有 <a><div class="glink">标题</div>…</a>。
const ehSearchHTML = `<!DOCTYPE html><html><head><title>E-Hentai</title></head><body>
<table class="itg">
<tr><td class="gl1c glcat"><div class="cn ct2" onclick="document.location='https://e-hentai.org/doujinshi'">Doujinshi</div></td>
<td class="gl2c"><div class="glthumb" id="it4247907"><div>
<img style="height:360px" alt="thumb" src="https://ehgt.org/w/02/724/21934-k0thhy4l.webp"></div></div></td>
<td class="gl3c glname"><a href="https://e-hentai.org/g/4264303/ffd224e2bf/"><div class="glink">(C100) [kibun tenkan (Eru Takagaki)] untitled (THE iDOLM@STER: Shiny Colors)</div><div><div class="gt" title="parody:the idolmaster">the idolmaster</div></div></a></td>
<td class="gl4c glhide"><div><a href="https://e-hentai.org/uploader/panzzzzz">panzzzzz</a></div><div>30 pages</div></td></tr>
<tr><td class="gl1c glcat"><div class="cn ct2" onclick="document.location='https://e-hentai.org/doujinshi'">Doujinshi</div></td>
<td class="gl2c"><div class="glthumb" id="it4247908"><div>
<img style="height:360px" alt="thumb" data-src="https://ehgt.org/w/02/726/79228-qj7qexko.webp"></div></div></td>
<td class="gl3c glname"><a href="https://e-hentai.org/g/4264304/aabbccdd01/"><div class="glink">[Asanagi] Mesu Kyoushi 3 (Azur Lane) [Chinese] [無邪気漢化組] [Digital]</div></a></td>
<td class="gl4c glhide"><div><a href="https://e-hentai.org/uploader/asanagi">asanagi</a></div><div>24 pages</div></td></tr>
</table></body></html>`

// ehGDataJSON 生成 gdata 响应（照真实响应形状；gid/token 回显请求值，贴近服务端真实行为）。
func ehGDataJSON(gid int, token string) string {
	return `{"gmetadata":[{"gid":` + itoa(gid) + `,"token":"` + token + `",
"title":"[Asanagi] Mesu Kyoushi 3 (Azur Lane) [Chinese] [無邪気漢化組] [Digital]",
"title_jpn":"雌教師3 (アズールレーン) [中国翻訳]","category":"Doujinshi",
"thumb":"https://ehgt.org/w/02/726/79228-qj7qexko.webp","uploader":"asanagi",
"posted":"1600000000","filecount":"24","filesize":"123456789","rating":"4.32",
"tags":["parody:azur lane","character:ganyu","artist:asanagi","female:ahegao","language:chinese","other:full color"]}]}`
}

// newEhTestServer 起一个 mock：/ 返回搜索页，/api.php 返 gdata，/g/<id>/ 供只给 gid 时补 token。
func newEhTestServer(t *testing.T, gotBody *map[string]any) *httptest.Server {
	t.Helper()
	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		if strings.Contains(r.URL.RawQuery, "f_search=contentwarning") {
			io.WriteString(w, `<html><body><h1>Content Warning</h1><form method="post"></form></body></html>`)
			return
		}
		if r.URL.Path == "/g/999" || r.URL.Path == "/g/999/" {
			// 只给 gid 时：画廊页里带一个规范链接，用来补 token。
			io.WriteString(w, `<html><body><a href="https://e-hentai.org/g/999/abcd1234/">x</a></body></html>`)
			return
		}
		if r.URL.Path != "/" {
			http.NotFound(w, r)
			return
		}
		io.WriteString(w, ehSearchHTML)
	})
	mux.HandleFunc("/api.php", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "must POST", http.StatusMethodNotAllowed)
			return
		}
		raw, _ := io.ReadAll(r.Body)
		gid, token := 0, ""
		var m map[string]any
		if err := json.Unmarshal(raw, &m); err == nil {
			if gotBody != nil {
				*gotBody = m
			}
			if list, ok := m["gidlist"].([]any); ok && len(list) > 0 {
				if pair, ok := list[0].([]any); ok && len(pair) == 2 {
					if f, ok := pair[0].(float64); ok {
						gid = int(f)
					}
					token, _ = pair[1].(string)
				}
			}
		}
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, ehGDataJSON(gid, token))
	})
	srv := httptest.NewServer(mux)
	t.Cleanup(srv.Close)
	return srv
}

func TestEhSearchParsesHits(t *testing.T) {
	srv := newEhTestServer(t, nil)
	c, err := NewEhClient(ProxyConfig{}, srv.URL, nil, 0)
	if err != nil {
		t.Fatalf("创建客户端失败: %v", err)
	}
	hits, err := c.Search(context.Background(), "mesu kyoushi", 25)
	if err != nil {
		t.Fatalf("搜索失败: %v", err)
	}
	if len(hits) != 2 {
		t.Fatalf("期望 2 条命中，实际 %d：%+v", len(hits), hits)
	}
	h := hits[1]
	if h.GID != 4264304 || h.Token != "aabbccdd01" {
		t.Errorf("gid/token 解析错：%+v", h)
	}
	if h.Title != "[Asanagi] Mesu Kyoushi 3 (Azur Lane) [Chinese] [無邪気漢化組] [Digital]" {
		t.Errorf("标题解析错：%q", h.Title)
	}
	if h.Category != "doujinshi" || h.Pages != "24" || h.Uploader != "asanagi" {
		t.Errorf("行字段解析错：category=%q pages=%q uploader=%q", h.Category, h.Pages, h.Uploader)
	}
	if h.Thumb != "https://ehgt.org/w/02/726/79228-qj7qexko.webp" {
		t.Errorf("data-src 缩略图未取到：%q", h.Thumb)
	}
	if !strings.HasSuffix(h.URL, "/g/4264304/aabbccdd01/") {
		t.Errorf("链接未规范化：%q", h.URL)
	}
	if hits[0].Thumb != "https://ehgt.org/w/02/724/21934-k0thhy4l.webp" {
		t.Errorf("src 缩略图未取到：%q", hits[0].Thumb)
	}
}

func TestEhSearchContentWarning(t *testing.T) {
	srv := newEhTestServer(t, nil)
	c, _ := NewEhClient(ProxyConfig{}, srv.URL, nil, 0)
	_, err := c.Search(context.Background(), "contentwarning", 25)
	if err == nil || !strings.Contains(err.Error(), "内容警告") {
		t.Fatalf("内容警告页应返回明确错误，实际: %v", err)
	}
}

func TestEhCandidatesCleanTitle(t *testing.T) {
	srv := newEhTestServer(t, nil)
	c, _ := NewEhClient(ProxyConfig{}, srv.URL, nil, 0)
	cands, err := c.Candidates(context.Background(), "mesu kyoushi", 25)
	if err != nil {
		t.Fatalf("候选失败: %v", err)
	}
	if len(cands) != 2 {
		t.Fatalf("期望 2 个候选，实际 %d", len(cands))
	}
	got := cands[1]
	if got.Source != EhSource {
		t.Errorf("Source 应为 %q，实际 %q", EhSource, got.Source)
	}
	if got.EhID != "4264304/aabbccdd01" {
		t.Errorf("EhID 错：%q", got.EhID)
	}
	if got.Title != "Mesu Kyoushi 3 (Azur Lane)" {
		t.Errorf("清洗后标题错：%q", got.Title)
	}
	if got.Author != "Asanagi" {
		t.Errorf("首段方括号作者错：%q", got.Author)
	}
	if !strings.Contains(got.Aliases, "無邪気漢化組") {
		t.Errorf("原始标题应保留在 Aliases：%q", got.Aliases)
	}
	// 会场标记要被剥掉
	if cands[0].Title != "untitled (THE iDOLM@STER: Shiny Colors)" {
		t.Errorf("会场标记未剥离：%q", cands[0].Title)
	}
}

func TestEhGetGallery(t *testing.T) {
	var body map[string]any
	srv := newEhTestServer(t, &body)
	c, _ := NewEhClient(ProxyConfig{}, srv.URL, nil, 0)
	g, err := c.GetGallery(context.Background(), "4264304/aabbccdd01")
	if err != nil {
		t.Fatalf("取详情失败: %v", err)
	}
	if g.GID != 4264304 || g.Token != "aabbccdd01" {
		t.Errorf("gid/token 错：%+v", g)
	}
	if g.TitleJpn != "雌教師3 (アズールレーン) [中国翻訳]" {
		t.Errorf("日文原名错：%q", g.TitleJpn)
	}
	if g.Category != "doujinshi" {
		t.Errorf("category 应转小写：%q", g.Category)
	}
	if g.Posted != "1600000000" || g.FileCount != "24" || g.Rating != "4.32" {
		t.Errorf("字符串字段错：posted=%q filecount=%q rating=%q", g.Posted, g.FileCount, g.Rating)
	}
	if len(g.Tags) != 6 {
		t.Errorf("标签数错：%d %v", len(g.Tags), g.Tags)
	}
	if !strings.HasSuffix(g.URL, "/g/4264304/aabbccdd01/") {
		t.Errorf("画廊链接错：%q", g.URL)
	}
	if c.CoverURL(g) != "https://ehgt.org/w/02/726/79228-qj7qexko.webp" {
		t.Errorf("封面地址错：%q", c.CoverURL(g))
	}
	// 请求体必须是 gdata + 单条 gidlist + namespace=1
	if body["method"] != "gdata" {
		t.Errorf("method 错：%v", body["method"])
	}
	if ns, _ := body["namespace"].(float64); ns != 1 {
		t.Errorf("namespace 应为 1（英文标签），实际 %v", body["namespace"])
	}
	list, _ := body["gidlist"].([]any)
	if len(list) != 1 {
		t.Fatalf("gidlist 应有 1 项：%v", body["gidlist"])
	}
}

func TestEhGetGallerySurfacesError(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, `{"error":"Gallery not found"}`)
	}))
	defer srv.Close()
	c, _ := NewEhClient(ProxyConfig{}, srv.URL, nil, 0)
	_, err := c.GetGallery(context.Background(), "1/deadbeef")
	if err == nil || !strings.Contains(err.Error(), "Gallery not found") {
		t.Fatalf("应把 gdata 的 error 透出，实际: %v", err)
	}
}

func TestEhResolveTokenWhenOnlyGid(t *testing.T) {
	srv := newEhTestServer(t, nil)
	c, _ := NewEhClient(ProxyConfig{}, srv.URL, nil, 0)
	g, err := c.GetGallery(context.Background(), "999")
	if err != nil {
		t.Fatalf("只给 gid 时应能从画廊页补 token: %v", err)
	}
	if g.Token != "abcd1234" {
		t.Errorf("token 应为 abcd1234（页面链接补出），实际 %q", g.Token)
	}
}

func TestEhParseIDAndLink(t *testing.T) {
	ids := []struct {
		in    string
		gid   int
		token string
		ok    bool
	}{
		{"4264304/aabbccdd01", 4264304, "aabbccdd01", true},
		{"4264304", 4264304, "", true},
		{"  4264304 / aabbccdd01 ", 4264304, "aabbccdd01", true}, // 两侧空格容忍
		{"4264304/zzzz", 0, "", false},                           // token 必须是十六进制
		{"https://e-hentai.org/g/4264304/aabbccdd01/", 4264304, "aabbccdd01", true},
		{"", 0, "", false},
		{"abc/def", 0, "", false},
		{"1/2/3", 0, "", false},
	}
	for _, c := range ids {
		gid, token, ok := ParseEhID(c.in)
		if ok != c.ok || (ok && (gid != c.gid || token != c.token)) {
			t.Errorf("ParseEhID(%q) = (%d,%q,%v)，期望 (%d,%q,%v)", c.in, gid, token, ok, c.gid, c.token, c.ok)
		}
	}

	links := []struct {
		in    string
		ok    bool
		token string
	}{
		{"https://e-hentai.org/g/123/abcdef/", true, "abcdef"},
		{"http://e-hentai.org/g/123/abcdef", true, "abcdef"},
		{"https://exhentai.org/g/123/abcdef/", true, "abcdef"},
		{"//e-hentai.org/g/123/abcdef/", true, "abcdef"},
		{"e-hentai.org/g/123/abcdef/", true, "abcdef"},
		{"https://e-hentai.org/s/abcdef/123-1", false, ""},
		{"https://e-hentai.org/g/123/", true, ""},
		{"123", false, ""},
		{"https://example.com/g/123/abcdef/", false, ""},
	}
	for _, c := range links {
		gid, token, ok := ParseEhLink(c.in)
		if ok != c.ok {
			t.Errorf("ParseEhLink(%q) ok=%v，期望 %v", c.in, ok, c.ok)
			continue
		}
		if ok && (gid != 123 || token != c.token) {
			t.Errorf("ParseEhLink(%q) = (%d,%q)，期望 (123,%q)", c.in, gid, token, c.token)
		}
		if IsEhGalleryLink(c.in) != c.ok {
			t.Errorf("IsEhGalleryLink(%q) 与 ParseEhLink 不一致", c.in)
		}
	}
}

func TestEhSplitTags(t *testing.T) {
	got := SplitEhTags([]string{"artist:asanagi", "parody:azur lane", "language:chinese", "other:full color"})
	if len(got["artist"]) != 1 || got["artist"][0] != "asanagi" {
		t.Errorf("artist 分组错：%v", got)
	}
	if len(got["parody"]) != 1 || got["parody"][0] != "azur lane" {
		t.Errorf("parody 值应保留空格：%v", got)
	}
	if len(got["language"]) != 1 || len(got["other"]) != 1 {
		t.Errorf("分组数量错：%v", got)
	}
}

// TestEhCircuitBreaker 连接层连续失败后必须熔断：墙内直连表站不能每个系列都白等 15s。
func TestEhCircuitBreaker(t *testing.T) {
	// 127.0.0.1:1 上没有人监听 → 立刻 connection refused（不依赖超时，测试很快）。
	c, err := NewEhClient(ProxyConfig{}, "http://127.0.0.1:1", nil, 0)
	if err != nil {
		t.Fatalf("创建客户端失败: %v", err)
	}
	ctx := context.Background()
	for i := 1; i <= ehCircuitFails; i++ {
		if _, err := c.Search(ctx, "x", 5); err == nil {
			t.Fatalf("第 %d 次请求应当失败", i)
		}
	}
	// 熔断已打开：这次必须**不发出请求**就失败，错误里带「熔断」。
	start := time.Now()
	_, err = c.Search(ctx, "x", 5)
	if err == nil || !strings.Contains(err.Error(), "熔断") {
		t.Fatalf("熔断后应快速失败并说明原因，实际: %v", err)
	}
	if d := time.Since(start); d > time.Second {
		t.Errorf("熔断后应立刻返回，实际耗时 %v", d)
	}
	// 冷却期过后应自动恢复（把 openUntil 拨回过去模拟 3 分钟已过），且成功后计数清零。
	srv := newEhTestServer(t, nil)
	c2, _ := NewEhClient(ProxyConfig{}, srv.URL, nil, 0)
	c2.noteFail()
	c2.noteFail()
	if err := c2.circuitError(); err == nil {
		t.Fatal("连续失败后应处于熔断态")
	}
	c2.circMu.Lock()
	c2.openUntil = time.Now().Add(-time.Second)
	c2.circMu.Unlock()
	if _, err := c2.Search(ctx, "mesu kyoushi", 5); err != nil {
		t.Fatalf("冷却期过后应能恢复请求，实际: %v", err)
	}
	if err := c2.circuitError(); err != nil {
		t.Errorf("成功后熔断应解除，实际: %v", err)
	}
}

func TestEhSearchTagSyntax(t *testing.T) {
	var gotQuery string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotQuery = r.URL.Query().Get("f_search")
		io.WriteString(w, ehSearchHTML)
	}))
	defer srv.Close()
	c, _ := NewEhClient(ProxyConfig{}, srv.URL, nil, 0)
	if _, err := c.SearchTag(context.Background(), "artist", "asanagi", 10); err != nil {
		t.Fatalf("标签搜索失败: %v", err)
	}
	if gotQuery != "artist:asanagi" {
		t.Errorf("f_search 应为 artist:asanagi，实际 %q", gotQuery)
	}
	if _, err := c.SearchTag(context.Background(), "", "x", 10); err != nil {
		t.Errorf("空命名空间应静默返回 nil，实际 %v", err)
	}
}

func TestEhCleanTitle(t *testing.T) {
	cases := map[string]string{
		"(C100) [kibun tenkan (Eru Takagaki)] untitled (THE iDOLM@STER: Shiny Colors)": "untitled (THE iDOLM@STER: Shiny Colors)",
		"[Asanagi] Mesu Kyoushi 3 (Azur Lane) [Chinese] [Digital]":                     "Mesu Kyoushi 3 (Azur Lane)",
		"[MTSP (Jin)] Baishun-Gakuen 2 (Original) [Decensored]":                        "Baishun-Gakuen 2 (Original)",
		"(Reitaisai 10) [x] Touhou &amp; more":                                         "Touhou & more",
	}
	for in, want := range cases {
		if got := cleanEhTitle(in); got != want {
			t.Errorf("cleanEhTitle(%q) = %q，期望 %q", in, got, want)
		}
	}
}

// TestEhLive 打真实表站（需出网 + 代理），默认跳过：EH_LIVE=1 且 EH_LIVE_PROXY=http://127.0.0.1:7890。
// 这是「解析器对真实页面/真实 JSON 是否成立」的唯一硬证据，改动解析逻辑时必须跑一次。
func TestEhLive(t *testing.T) {
	if os.Getenv("EH_LIVE") != "1" {
		t.Skip("跳过真实站点测试（EH_LIVE=1 开启）")
	}
	pc := ProxyConfig{}
	if raw := os.Getenv("EH_LIVE_PROXY"); raw != "" {
		u, err := url.Parse(raw)
		if err != nil {
			t.Fatalf("EH_LIVE_PROXY 解析失败: %v", err)
		}
		port, _ := strconv.Atoi(u.Port())
		pc = ProxyConfig{Protocol: u.Scheme, Host: u.Hostname(), Port: port}
	}
	c, err := NewEhClient(pc, "", nil, -1)
	if err != nil {
		t.Fatalf("创建客户端失败: %v", err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	defer cancel()

	hits, err := c.Search(ctx, "asanagi", 25)
	if err != nil {
		t.Fatalf("真实搜索失败（检查代理是否可用）: %v", err)
	}
	if len(hits) == 0 {
		t.Fatal("真实搜索 0 条，解析器可能失效（EH 页面结构变了？）")
	}
	t.Logf("真实搜索命中 %d 条，首条：gid=%d token=%s category=%s pages=%s title=%q",
		len(hits), hits[0].GID, hits[0].Token, hits[0].Category, hits[0].Pages, hits[0].Title)
	if hits[0].GID == 0 || hits[0].Token == "" {
		t.Errorf("首条 gid/token 解析失败：%+v", hits[0])
	}

	g, err := c.GetGallery(ctx, EhID(hits[0].GID, hits[0].Token))
	if err != nil {
		t.Fatalf("真实 gdata 失败: %v", err)
	}
	if g.Title == "" {
		t.Error("真实 gdata 标题为空")
	}
	if len(g.Tags) == 0 {
		t.Error("真实 gdata 没有任何标签")
	}
	if g.Posted == "" || g.Rating == "" {
		t.Errorf("posted/rating 未取到：posted=%q rating=%q", g.Posted, g.Rating)
	}
	t.Logf("真实元数据：title=%q jpn=%q category=%s rating=%s posted=%s tags=%d cover=%s",
		g.Title, g.TitleJpn, g.Category, g.Rating, g.Posted, len(g.Tags), c.CoverURL(g))
	t.Logf("标签前 8 个：%v", g.Tags[:min(8, len(g.Tags))])
}
