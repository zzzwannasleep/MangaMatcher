// fetch.go：EhTagTranslation 标签库的获取与缓存（GitHub release → gunzip → 原子落盘）。
//
// 失败策略：不抛到调用链上。下载失败但本地有缓存时沿用缓存；两者都没有就只记一条告警，
// 刮削继续用英文标签跑（绝不因为标签翻译不可用而让刮削失败）。
package ehtags

import (
	"compress/gzip"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	// EnvPath 指定本地 db.text.json 路径（离线 / 测试用）。设了之后完全不联网。
	EnvPath = "EHTAGS_DB_PATH"
	// EnvRefreshHours 缓存刷新周期（小时），默认 168（7 天）。
	EnvRefreshHours = "EHTAGS_REFRESH_HOURS"

	releaseAPI = "https://api.github.com/repos/EhTagTranslation/Database/releases/latest"
	assetName  = "db.text.json.gz"
	// latestAssetURL GitHub 的「最新 release 资产」永久直链（会 302 到带签名的资产地址）。
	// 🟢 实测（2026-10-20）：HTTP 200 / 1,320,780 字节，且不消耗 API 配额；
	// 而 api.github.com 会因共享出口 IP 被限流（实测 "API rate limit exceeded"），
	// 所以这条直链必须是首选，API 只做兜底。
	latestAssetURL = "https://github.com/EhTagTranslation/Database/releases/latest/download/" + assetName
	dbFileName     = "db.text.json"
	maxDBBytes     = 64 << 20
	minDBBytes     = 512 // 小于这个数一定是错误页/半截文件（真实库 ~4.2MB）
	ghUserAgent    = "MangaMatcher/1.0"
	defaultFresh   = 168 * time.Hour
)

var (
	configureOnce sync.Once
	fetchMu       sync.Mutex
	// Enable 关闭时可跳过联网（测试 / 用户不想联网时）。
	Enable = true
)

// Dir 标签库缓存目录。
func Dir(dataDir string) string {
	if strings.TrimSpace(dataDir) == "" {
		dataDir = "data"
	}
	return filepath.Join(dataDir, "ehtags")
}

// Path 缓存文件路径。
func Path(dataDir string) string { return filepath.Join(Dir(dataDir), dbFileName) }

// Configure 异步准备标签库（幂等，可重复调用）。
// 优先用 EHTAGS_DB_PATH 指定的本地文件，否则走 缓存 → GitHub release 下载。
func Configure(dataDir string, hc *http.Client, logf func(string)) {
	if logf == nil {
		logf = func(string) {}
	}
	configureOnce.Do(func() {
		if !Enable {
			logf("EH 中文标签库已按配置关闭，标签将使用英文原名")
			return
		}
		go func() {
			if p := strings.TrimSpace(os.Getenv(EnvPath)); p != "" {
				if err := Use(p); err != nil {
					logf("EH 中文标签库加载失败（将回落英文标签）：" + err.Error())
					return
				}
				logf(fmt.Sprintf("EH 中文标签库已加载：%s（%s，%d 条）", p, Version(), Size()))
				return
			}
			if hc == nil {
				hc = &http.Client{Timeout: 90 * time.Second}
			}
			p, err := Ensure(dataDir, hc, logf)
			if err != nil {
				logf("EH 中文标签库不可用（将回落英文标签）：" + err.Error())
				return
			}
			if err := Use(p); err != nil {
				logf("EH 中文标签库解析失败（将回落英文标签）：" + err.Error())
				return
			}
			logf(fmt.Sprintf("EH 中文标签库已就绪：%s（%s，%d 条）", p, Version(), Size()))
		}()
	})
}

// refreshAfter 缓存有效期。
func refreshAfter() time.Duration {
	if v := strings.TrimSpace(os.Getenv(EnvRefreshHours)); v != "" {
		if h, err := strconv.Atoi(v); err == nil && h > 0 {
			return time.Duration(h) * time.Hour
		}
	}
	return defaultFresh
}

// Ensure 确保本地有可用的 db.text.json（新鲜则直接用；过期则尝试更新，更新失败沿用旧文件）。
func Ensure(dataDir string, hc *http.Client, logf func(string)) (string, error) {
	if logf == nil {
		logf = func(string) {}
	}
	if hc == nil {
		hc = &http.Client{Timeout: 90 * time.Second}
	}
	p := Path(dataDir)
	if st, err := os.Stat(p); err == nil && st.Size() > 1024 && time.Since(st.ModTime()) < refreshAfter() {
		return p, nil
	}
	fetchMu.Lock()
	defer fetchMu.Unlock()
	// 双检：等锁期间别的协程可能已经下好。
	if st, err := os.Stat(p); err == nil && st.Size() > 1024 && time.Since(st.ModTime()) < refreshAfter() {
		return p, nil
	}
	if err := download(hc, p, logf); err != nil {
		if st, statErr := os.Stat(p); statErr == nil && st.Size() > 1024 {
			logf("更新 EH 标签库失败，沿用本地缓存（" + st.ModTime().Format("2006-01-02") + "）：" + err.Error())
			return p, nil
		}
		return "", err
	}
	return p, nil
}

type ghAsset struct {
	Name               string `json:"name"`
	BrowserDownloadURL string `json:"browser_download_url"`
	Size               int64  `json:"size"`
}

type ghRelease struct {
	TagName string    `json:"tag_name"`
	Assets  []ghAsset `json:"assets"`
}

// download 取最新 release 的 db.text.json.gz 并解压落盘。
// 先走不消耗 API 配额的直链，失败再走 API 查资产地址（直链被拦但 API 可用的环境）。
func download(hc *http.Client, dest string, logf func(string)) error {
	logf("正在下载 EH 中文标签库：" + assetName + "（latest release 直链）")
	if err := fetchGunzipTo(hc, latestAssetURL, dest); err == nil {
		return nil
	} else {
		logf("标签库直链下载失败，改用 GitHub API 查询资产地址：" + err.Error())
	}

	rel, err := latestRelease(hc)
	if err != nil {
		return err
	}
	asset := pickAsset(rel)
	if asset == nil {
		return fmt.Errorf("release %s 里没有 %s 资产", rel.TagName, assetName)
	}
	logf("正在下载 EH 中文标签库：" + asset.Name + "（" + rel.TagName + "）")
	return fetchGunzipTo(hc, asset.BrowserDownloadURL, dest)
}

// pickAsset 从 release 资产里挑出标签库（优先精确名，其次包含 db.text.json 的）。
func pickAsset(rel *ghRelease) *ghAsset {
	if rel == nil {
		return nil
	}
	for i := range rel.Assets {
		if rel.Assets[i].Name == assetName {
			return &rel.Assets[i]
		}
	}
	for i := range rel.Assets {
		if strings.Contains(rel.Assets[i].Name, "db.text.json") {
			return &rel.Assets[i]
		}
	}
	return nil
}

// fetchGunzipTo 下载 u 并 gunzip 落盘。
func fetchGunzipTo(hc *http.Client, u, dest string) error {
	req, err := http.NewRequest(http.MethodGet, u, nil)
	if err != nil {
		return err
	}
	req.Header.Set("User-Agent", ghUserAgent)
	req.Header.Set("Accept", "application/octet-stream")
	resp, err := hc.Do(req)
	if err != nil {
		return fmt.Errorf("下载标签库失败：%w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		io.Copy(io.Discard, io.LimitReader(resp.Body, 4096))
		return fmt.Errorf("下载标签库失败：HTTP %d", resp.StatusCode)
	}
	return writeGunzip(resp.Body, dest)
}

// latestRelease 取 EhTagTranslation/Database 的最新 release。
func latestRelease(hc *http.Client) (*ghRelease, error) {
	req, err := http.NewRequest(http.MethodGet, releaseAPI, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", ghUserAgent)
	req.Header.Set("Accept", "application/vnd.github+json")
	resp, err := hc.Do(req)
	if err != nil {
		return nil, fmt.Errorf("查询标签库最新版本失败：%w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		io.Copy(io.Discard, io.LimitReader(resp.Body, 4096))
		return nil, fmt.Errorf("查询标签库最新版本失败：HTTP %d（GitHub API 可能被限流或需要代理）", resp.StatusCode)
	}
	var rel ghRelease
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&rel); err != nil {
		return nil, fmt.Errorf("解析标签库版本信息失败：%w", err)
	}
	return &rel, nil
}

// headWriter 边写边记首字节与总长度，用于识别「200 但其实是错误页/半截文件」。
type headWriter struct {
	w    io.Writer
	n    int64
	head byte
	got  bool
}

func (h *headWriter) Write(p []byte) (int, error) {
	if !h.got {
		for _, b := range p {
			if b == ' ' || b == '\n' || b == '\r' || b == '\t' {
				continue
			}
			h.head = b
			h.got = true
			break
		}
	}
	n, err := h.w.Write(p)
	h.n += int64(n)
	return n, err
}

// writeGunzip 解压 gzip 流并原子落盘（.tmp → rename），避免半截文件被当成有效缓存。
func writeGunzip(src io.Reader, dest string) error {
	if err := os.MkdirAll(filepath.Dir(dest), 0o755); err != nil {
		return fmt.Errorf("创建标签库目录失败：%w", err)
	}
	gz, err := gzip.NewReader(io.LimitReader(src, maxDBBytes))
	if err != nil {
		return fmt.Errorf("标签库不是合法 gzip：%w", err)
	}
	defer gz.Close()
	tmp := dest + ".tmp"
	out, err := os.Create(tmp)
	if err != nil {
		return fmt.Errorf("写入标签库失败：%w", err)
	}
	hw := &headWriter{w: out}
	_, copyErr := io.Copy(hw, io.LimitReader(gz, maxDBBytes))
	closeErr := out.Close()
	if copyErr != nil || closeErr != nil {
		os.Remove(tmp)
		if copyErr != nil {
			return fmt.Errorf("解压标签库失败：%w", copyErr)
		}
		return fmt.Errorf("写入标签库失败：%w", closeErr)
	}
	// 体检：够大 + 是 JSON 对象（防「HTTP 200 但返回错误页」被当成缓存）。
	if hw.n < minDBBytes || !hw.got || hw.head != '{' {
		os.Remove(tmp)
		return fmt.Errorf("标签库内容异常（%d 字节，首字节 %q）", hw.n, string(hw.head))
	}
	if err := os.Rename(tmp, dest); err != nil {
		os.Remove(tmp)
		return fmt.Errorf("保存标签库失败：%w", err)
	}
	return nil
}
