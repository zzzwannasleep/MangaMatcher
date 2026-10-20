package ehtags

import (
	"bytes"
	"compress/gzip"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

// 真实 db.text.json 的结构子集（13 命名空间取 4 个，字段名照实）。
const dbFixture = `{
  "repo": "EhTagTranslation/Database",
  "head": {"sha": "48e0589"},
  "version": 7,
  "data": [
    {"namespace": "rows", "frontMatters": {"name": "内容索引", "description": "标签列表的行名", "key": "rows"},
     "count": 2, "data": {"female": {"name": "女性", "intro": "女性角色相关的恋物标签。", "links": ""}}},
    {"namespace": "artist", "frontMatters": {"name": "艺术家", "description": "绘画作者/写手。", "key": "artist"},
     "count": 2, "data": {
        "asanagi": {"name": "朝凪", "intro": "日本漫画家。", "links": ""},
        "2": {"name": "2", "intro": "", "links": ""}}},
    {"namespace": "parody", "frontMatters": {"name": "原作", "key": "parody"}, "count": 1,
     "data": {"azur lane": {"name": "碧蓝航线", "intro": "手机游戏。", "links": ""}}},
    {"namespace": "reclass", "frontMatters": {"name": "重新分类", "key": "reclass"}, "count": 1,
     "data": {"artistcg": {"name": "画师CG", "intro": "画师CG集。", "links": ""}}}
  ]
}`

func writeFixture(t *testing.T) string {
	t.Helper()
	p := filepath.Join(t.TempDir(), "db.text.json")
	if err := os.WriteFile(p, []byte(dbFixture), 0o644); err != nil {
		t.Fatalf("写 fixture 失败: %v", err)
	}
	return p
}

func TestLoadAndTranslate(t *testing.T) {
	db, err := Load(writeFixture(t))
	if err != nil {
		t.Fatalf("加载失败: %v", err)
	}
	if db.Size() != 5 {
		t.Errorf("词条数应为 5，实际 %d", db.Size())
	}
	if name, ok := db.Translate("artist", "asanagi"); !ok || name != "朝凪" {
		t.Errorf("artist:asanagi 应为 朝凪，实际 %q ok=%v", name, ok)
	}
	// 大小写/空格都不敏感
	if name, ok := db.Translate("Artist", "ASANAGI"); !ok || name != "朝凪" {
		t.Errorf("大小写不敏感失败：%q ok=%v", name, ok)
	}
	// "artist cg"（带空格）要对上库里的 "artistcg"
	if name, ok := db.Translate("reclass", "artist cg"); !ok || name != "画师CG" {
		t.Errorf("带空格标签未归一：%q ok=%v", name, ok)
	}
	// 未收录
	if _, ok := db.Translate("artist", "nobody"); ok {
		t.Error("未收录的标签不应返回 ok")
	}
	// 译文与原文相同视为未翻译（库里大量 "2": "2" 这种占位）
	if _, ok := db.Translate("artist", "2"); ok {
		t.Error("译文等于原文时应返回 false")
	}
	// 命名的空 namespace 不 panic
	if _, ok := db.Translate("", "x"); ok {
		t.Error("空命名空间不应命中")
	}
}

func TestLabels(t *testing.T) {
	db, err := Load(writeFixture(t))
	if err != nil {
		t.Fatalf("加载失败: %v", err)
	}
	if got := db.Label("artist"); got != "艺术家" {
		t.Errorf("artist 命名空间中文名应为 艺术家，实际 %q", got)
	}
	if got := db.Label("parody"); got != "原作" {
		t.Errorf("parody 应为 原作，实际 %q", got)
	}
	// 库里没有的命名空间走内置兜底表
	if got := db.Label("group"); got != "团队" {
		t.Errorf("group 兜底应为 团队，实际 %q", got)
	}
	if got := db.Label("unknown_ns"); got != "unknown_ns" {
		t.Errorf("未知命名空间应原样返回，实际 %q", got)
	}
	if got := Label("female"); got != "女性" {
		t.Errorf("未加载时兜底表应可用，实际 %q", got)
	}
}

func TestIntroTruncate(t *testing.T) {
	db, err := Load(writeFixture(t))
	if err != nil {
		t.Fatalf("加载失败: %v", err)
	}
	if s, ok := db.Intro("artist", "asanagi", 0); !ok || s != "日本漫画家。" {
		t.Errorf("intro 应原样返回：%q ok=%v", s, ok)
	}
	if s, ok := db.Intro("artist", "asanagi", 2); !ok || s != "日本…" {
		t.Errorf("intro 截断失败：%q", s)
	}
	if _, ok := db.Intro("artist", "2", 0); ok {
		t.Error("空 intro 应返回 false")
	}
}

func TestUseSetsProcessDB(t *testing.T) {
	defer func() { _ = Use("") }()
	if err := Use(writeFixture(t)); err != nil {
		t.Fatalf("Use 失败: %v", err)
	}
	if !Ready() {
		t.Fatal("Use 之后 Ready 应为 true")
	}
	if Version() == "" {
		t.Error("Version 不应为空")
	}
	if name, ok := Translate("parody", "azur lane"); !ok || name != "碧蓝航线" {
		t.Errorf("包级 Translate 失败：%q ok=%v", name, ok)
	}
	if Size() != 5 {
		t.Errorf("包级 Size 应为 5，实际 %d", Size())
	}
	// 坏文件：Use 应报错并清空当前库
	if err := Use(filepath.Join(t.TempDir(), "nope.json")); err == nil {
		t.Error("不存在的文件应报错")
	}
	if Ready() {
		t.Error("加载失败后 Ready 应为 false")
	}
	if LastError() == "" {
		t.Error("失败原因应记录在 LastError")
	}
}

func TestWriteGunzip(t *testing.T) {
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	if _, err := zw.Write([]byte(dbFixture)); err != nil {
		t.Fatalf("gzip 写失败: %v", err)
	}
	if err := zw.Close(); err != nil {
		t.Fatalf("gzip 关闭失败: %v", err)
	}
	dest := filepath.Join(t.TempDir(), "sub", "db.text.json")
	if err := writeGunzip(bytes.NewReader(buf.Bytes()), dest); err != nil {
		t.Fatalf("解压落盘失败: %v", err)
	}
	if _, err := Load(dest); err != nil {
		t.Fatalf("落盘结果应能被 Load：%v", err)
	}
	// 非 gzip 内容必须报错且不留半截文件
	bad := filepath.Join(t.TempDir(), "bad.json")
	if err := writeGunzip(bytes.NewReader([]byte("not gzip")), bad); err == nil {
		t.Error("非 gzip 内容应报错")
	}
	if _, err := os.Stat(bad); err == nil {
		t.Error("失败时不应留下目标文件")
	}
}

// TestEnsureLiveDownload 真机验证「下载 → 解压 → 解析 → 翻译」整条链路（默认跳过）。
// 开启：EHTAGS_LIVE=1，代理：EHTAGS_LIVE_PROXY=http://127.0.0.1:7890。
// 这是「中文标签自动翻译」唯一不靠 mock 的证据，改动下载/解析逻辑时必须跑一次。
func TestEnsureLiveDownload(t *testing.T) {
	if os.Getenv("EHTAGS_LIVE") != "1" {
		t.Skip("跳过真实标签库下载（EHTAGS_LIVE=1 开启）")
	}
	hc := &http.Client{Timeout: 120 * time.Second}
	if raw := os.Getenv("EHTAGS_LIVE_PROXY"); raw != "" {
		u, err := url.Parse(raw)
		if err != nil {
			t.Fatalf("EHTAGS_LIVE_PROXY 解析失败: %v", err)
		}
		port, _ := strconv.Atoi(u.Port())
		p, err := proxyURL(u.Scheme, u.Hostname(), port, u.User)
		if err != nil {
			t.Fatalf("代理地址构造失败: %v", err)
		}
		hc.Transport = &http.Transport{Proxy: http.ProxyURL(p)}
	}
	dir := t.TempDir()
	path, err := Ensure(dir, hc, func(msg string) { t.Log(msg) })
	if err != nil {
		t.Fatalf("真实下载失败（检查代理是否可用）: %v", err)
	}
	st, err := os.Stat(path)
	if err != nil || st.Size() < 1<<20 {
		t.Fatalf("标签库文件异常: %v size=%d", err, st.Size())
	}
	t.Logf("真实标签库已下载：%s（%d 字节）", path, st.Size())

	if err := Use(path); err != nil {
		t.Fatalf("解析失败: %v", err)
	}
	if Size() < 20000 {
		t.Errorf("词条数偏少：%d（真实库约 4.4 万）", Size())
	}
	// 真实库实测值（2026-10-20，v7，44,668 条）——这几个是「中文翻译真的生效」的硬证据。
	for _, c := range []struct{ ns, tag, want string }{
		{"artist", "asanagi", "朝凪"},
		{"language", "chinese", "汉语"},
		{"parody", "azur lane", "碧蓝航线"},
		{"female", "big breasts", "巨乳"},
		{"character", "ganyu", "甘雨"},
	} {
		got, ok := Translate(c.ns, c.tag)
		if !ok || got != c.want {
			t.Errorf("%s:%s 应为 %q，实际 %q ok=%v", c.ns, c.tag, c.want, got, ok)
		}
	}
	if Label("artist") != "艺术家" {
		t.Errorf("artist 命名空间应为 艺术家，实际 %q", Label("artist"))
	}
	// 库里的值等于原文时（如 group:fatalpulse → Fatalpulse）必须回落，别写一堆没意义的「中文」
	if got, ok := Translate("group", "fatalpulse"); ok {
		t.Errorf("译文等于原文时应回落，实际 ok=true %q", got)
	}
	t.Logf("词条数=%d 版本=%s 示例：artist:asanagi→%s parody:azur lane→%s",
		Size(), Version(), mustTranslate("artist", "asanagi"), mustTranslate("parody", "azur lane"))
}

func mustTranslate(ns, tag string) string {
	if v, ok := Translate(ns, tag); ok {
		return v
	}
	return tag
}

// proxyURL 组装带鉴权的代理地址（与 sources 包同口径，测试内自备避免跨包依赖）。
func proxyURL(scheme, host string, port int, user *url.Userinfo) (*url.URL, error) {
	if scheme == "" {
		scheme = "http"
	}
	if host == "" || port <= 0 {
		return nil, fmt.Errorf("代理地址不完整: %s:%d", host, port)
	}
	u := &url.URL{Scheme: scheme, Host: fmt.Sprintf("%s:%d", host, port)}
	if user != nil {
		u.User = user
	}
	return u, nil
}

func TestPickAsset(t *testing.T) {
	rel := &ghRelease{TagName: "v1", Assets: []ghAsset{
		{Name: "other.zip", BrowserDownloadURL: "https://x/other.zip"},
		{Name: "db.text.json.gz", BrowserDownloadURL: "https://x/db.text.json.gz"},
	}}
	if a := pickAsset(rel); a == nil || a.Name != "db.text.json.gz" {
		t.Fatalf("应精确命中 db.text.json.gz，实际 %+v", a)
	}
	// 精确名缺失时退化为「包含 db.text.json」
	rel2 := &ghRelease{TagName: "v2", Assets: []ghAsset{{Name: "db.text.json.gz.old", BrowserDownloadURL: "u"}}}
	if a := pickAsset(rel2); a == nil || a.Name != "db.text.json.gz.old" {
		t.Fatalf("应退化为包含匹配，实际 %+v", a)
	}
	if a := pickAsset(&ghRelease{TagName: "v3"}); a != nil {
		t.Errorf("没有资产时应返回 nil，实际 %+v", a)
	}
	if a := pickAsset(nil); a != nil {
		t.Errorf("nil release 应返回 nil")
	}
	// 直链必须指向「最新 release 资产」的永久地址（不消耗 API 配额）
	if !strings.HasPrefix(latestAssetURL, "https://github.com/EhTagTranslation/Database/releases/latest/download/") {
		t.Errorf("直链前缀不对：%s", latestAssetURL)
	}
	if !strings.HasSuffix(latestAssetURL, assetName) {
		t.Errorf("直链应以 %s 结尾：%s", assetName, latestAssetURL)
	}
}

func TestEnsureUsesFreshCache(t *testing.T) {
	dir := t.TempDir()
	dest := Path(dir)
	if err := os.MkdirAll(filepath.Dir(dest), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(dest, []byte(dbFixture+strings.Repeat(" ", 2048)), 0o644); err != nil {
		t.Fatal(err)
	}
	// 缓存新鲜 + hc 为 nil：不应触网，直接返回路径
	got, err := Ensure(dir, nil, nil)
	if err != nil {
		t.Fatalf("新鲜缓存应直接命中：%v", err)
	}
	if got != dest {
		t.Errorf("路径应为 %q，实际 %q", dest, got)
	}
}
