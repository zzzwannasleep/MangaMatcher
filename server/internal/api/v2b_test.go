package api

import (
	"archive/zip"
	"bytes"
	"database/sql"
	"encoding/json"
	"io"
	"mime/multipart"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/klauspost/compress/zstd"
	_ "modernc.org/sqlite"
)

// sqlOpen 打开测试 sqlite。
func sqlOpen(path string) (*sql.DB, error) { return sql.Open("sqlite", path) }

// ---------- 1. Settings v2 打码规则（ai.apiKey / proxy.password） ----------

func TestV2SettingsMasking(t *testing.T) {
	env := newTestEnv(t)
	env.patchSettings(map[string]any{
		"proxy": map[string]any{"protocol": "socks5", "host": "127.0.0.1", "port": 1080,
			"username": "u", "password": "secret"},
		"ai": map[string]any{"enabled": true, "baseURL": "http://ai", "apiKey": "sk-xxx", "model": "m1",
			"identify": true, "smartPick": true, "translateSummary": true, "writeSummary": true},
		"matchType":         "小说",
		"baikeEnabled":      false,
		"mangaDexEnabled":   false,
		"bookwalker":        map[string]any{"downloadURL": "http://z", "writeExtras": false},
		"batchForceRefresh": true,
		"versionTagWhitelist": []string{"自装订", "自装订"}, // 去重保序
		"versionTagBlacklist": []string{"垃圾水印"},
		"comicsDir":         "/comics",
	})
	_, data := env.do("GET", "/api/settings", env.adminToken, nil)
	var masked struct {
		Proxy struct {
			Password string `json:"password"`
		} `json:"proxy"`
		AI struct {
			APIKey string `json:"apiKey"`
		} `json:"ai"`
		MatchType  string `json:"matchType"`
		Baike      bool   `json:"baikeEnabled"`
		ComicsDir  string `json:"comicsDir"`
		Whitelist  []string `json:"versionTagWhitelist"`
	}
	json.Unmarshal(data, &masked)
	if masked.Proxy.Password != "****" {
		t.Fatalf("proxy.password 应打码: %s", data)
	}
	if masked.AI.APIKey != "****" {
		t.Fatalf("ai.apiKey 应打码: %s", data)
	}
	if masked.MatchType != "小说" || masked.Baike || masked.ComicsDir != "/comics" {
		t.Fatalf("v2 字段错误: %s", data)
	}
	if len(masked.Whitelist) != 1 || masked.Whitelist[0] != "自装订" {
		t.Fatalf("白名单应去重: %v", masked.Whitelist)
	}
	// PATCH 传 **** 不修改：真实密码还在（再次 PATCH 别的字段后 GET 验证）
	env.patchSettings(map[string]any{"matchType": "漫画"})
	_, data = env.do("GET", "/api/settings", env.adminToken, nil)
	var again struct {
		Proxy struct {
			Password string `json:"password"`
		} `json:"proxy"`
	}
	json.Unmarshal(data, &again)
	if again.Proxy.Password != "****" {
		t.Fatalf("打码后 GET 仍应为 ****: %s", data)
	}
	// 内部存储里密码仍可用（Proxy.password 真实值未丢）：
	st := env.server.LoadSettings()
	if st.Proxy.Password != "secret" || st.AI.APIKey != "sk-xxx" {
		t.Fatalf("PATCH **** 不得清空真实密码: %q %q", st.Proxy.Password, st.AI.APIKey)
	}
}

// ---------- 10. BW 导入 zst（真 zstd 解压链路）+ 索引 status ----------

func TestV2BWImportZstUpload(t *testing.T) {
	env := newTestEnv(t)

	// 造一个真 zst：内容是合法的空 sqlite（modernc 生成）
	tmpDB := filepath.Join(env.dataDir, "make-test.sqlite")
	if err := makeEmptySqlite(tmpDB); err != nil {
		t.Fatal(err)
	}
	sqlite, err := os.ReadFile(tmpDB)
	if err != nil {
		t.Fatal(err)
	}
	var zstBuf bytes.Buffer
	enc2, err := zstd.NewWriter(&zstBuf)
	if err != nil {
		t.Fatal(err)
	}
	enc2.Write(sqlite)
	enc2.Close()

	// multipart 上传（.zst 扩展 + 魔数识别）
	pr := &bytes.Buffer{}
	mw := newMultipartWriter(pr)
	fw, _ := mw.CreateFormFile("file", "bkwk-db.sqlite.zst")
	fw.Write(zstBuf.Bytes())
	mw.Close()
	req, _ := http.NewRequest("POST", env.api.URL+"/api/settings/bw/import", pr)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	req.Header.Set("Authorization", "Bearer "+env.adminToken)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	data, _ := ioReadAllResp(resp.Body)
	resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("zst 导入应成功: %d %s", resp.StatusCode, data)
	}
	var out struct {
		OK          bool `json:"ok"`
		SeriesCount int  `json:"seriesCount"`
		VolumeCount int  `json:"volumeCount"`
	}
	json.Unmarshal(data, &out)
	if !out.OK || out.SeriesCount != 0 { // fake sqlite 无表 → 空索引
		t.Fatalf("导入结果错误: %s", data)
	}
	// 索引文件已生成
	if _, err := os.Stat(filepath.Join(env.dataDir, "bkwk-index.json.gz")); err != nil {
		t.Fatalf("索引应落盘: %v", err)
	}
	// status 反映 building=false / indexed=true
	resp2, data2 := env.do("GET", "/api/settings/bw/status", env.adminToken, nil)
	var st struct {
		Indexed bool `json:"indexed"`
	}
	json.Unmarshal(data2, &st)
	if !st.Indexed {
		t.Fatalf("status 应 indexed: %s", data2)
	}
	_ = resp2
}

// ---------- 8. Mylar oneshot 特殊名 + comicsDir 直写 ----------

func TestV2MylarOneshotAndDirectWrite(t *testing.T) {
	env := newTestEnv(t)
	// oneshot 系列：url 指向文件（xxx.cbz）
	env.komga.seedSeries("os1", "lib1", "单话作品")
	env.komga.mu.Lock()
	env.komga.series["os1"]["oneshot"] = true
	env.komga.series["os1"]["url"] = "/data/单话作品/xxx.cbz"
	env.komga.series["os1"]["booksCount"] = 1
	env.komga.mu.Unlock()

	// comicsDir 配置 → 直写导出
	env.patchSettings(map[string]any{"comicsDir": filepath.Join(env.dataDir, "comics-out")})
	resp, data := env.do("POST", "/api/libraries/lib1/mylar/export", env.adminToken, map[string]any{"withCovers": true})
	if resp.StatusCode != 200 {
		t.Fatalf("直写导出应成功: %d %s", resp.StatusCode, data)
	}
	var out struct {
		Written int `json:"written"`
	}
	json.Unmarshal(data, &out)
	if out.Written < 2 {
		t.Fatalf("应写出 series.json + oneshot.json: %s", data)
	}
	// 库内相对目录结构 + oneshot 特殊名
	oneshotPath := filepath.Join(env.dataDir, "comics-out", "单话作品", "xxx.oneshot.json")
	if _, err := os.Stat(oneshotPath); err != nil {
		t.Fatalf("oneshot 应写为 xxx.oneshot.json（相对目录结构）: %v", err)
	}
	coverPath := filepath.Join(env.dataDir, "comics-out", "单话作品", "xxx.cover.jpg")
	if _, err := os.Stat(coverPath); err != nil {
		t.Fatalf("oneshot 封面应为 xxx.cover.jpg: %v", err)
	}
	// ZIP 路径同样含 oneshot 特殊名
	resp, data = env.do("GET", "/api/libraries/lib1/mylar?withCovers=1", env.adminToken, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("ZIP 导出应成功: %d", resp.StatusCode)
	}
	zr, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, f := range zr.File {
		if strings.HasSuffix(f.Name, "xxx.oneshot.json") {
			found = true
		}
	}
	if !found {
		t.Fatal("ZIP 应含 xxx.oneshot.json")
	}
}

// multipart 构造辅助。
func newMultipartWriter(buf *bytes.Buffer) *multipart.Writer {
	return multipart.NewWriter(buf)
}

// ioReadAllResp 读全响应体。
func ioReadAllResp(r io.Reader) ([]byte, error) { return io.ReadAll(r) }

// makeEmptySqlite 生成合法空 sqlite 文件（modernc 驱动）。
func makeEmptySqlite(path string) error {
	db, err := sqlOpen(path)
	if err != nil {
		return err
	}
	defer db.Close()
	_, err = db.Exec("CREATE TABLE t (id int)")
	return err
}
