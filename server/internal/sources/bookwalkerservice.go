package sources

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"time"

	"github.com/klauspost/compress/zstd"
)

// BookWalkerService BookWalker 官方目录库下载/导入 → 解压 → 建索引。
// 官方地址挂在 Cloudflare 后面，直连可能 403 —— 所以另有导入本地文件入口。
type BookWalkerService struct {
	log       Logger
	minPaceMs int
}

// DefaultBWDownloadURL 官方目录库地址。
const DefaultBWDownloadURL = "https://data.bookwalker.com/bkwk-db.sqlite.zst"

// NewBookWalkerService 创建服务。
func NewBookWalkerService(log Logger) *BookWalkerService {
	return &BookWalkerService{log: log}
}

// Progress 下载/解压/建索引进度回调（step 0..3, msg 中文）。
type Progress func(step int, total int, msg string)

// BuildIndex 下载 zst → 解压 → 建索引。返回索引文件路径。
// 403/503（Cloudflare 人机校验）→ 提示改用导入。
func (s *BookWalkerService) BuildIndex(ctx context.Context, zstURL, dbPath, indexPath string, p ProxyConfig, progress Progress) (string, error) {
	if progress == nil {
		progress = func(int, int, string) {}
	}
	if zstURL == "" {
		zstURL = DefaultBWDownloadURL
	}
	dir := filepath.Dir(dbPath)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	zstPath := dbPath + ".zst"

	progress(0, 3, "下载数据库…")
	if s.log != nil {
		s.log.Info("下载 BookWalker 数据库: " + zstURL + "（代理 " + p.Display() + "）")
	}
	hc, err := NewHTTPClient(p, 10*time.Minute,
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
		"https://bookwalker.jp/")
	if err != nil {
		return "", err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, zstURL, nil)
	if err != nil {
		return "", err
	}
	resp, err := hc.Do(req)
	if err != nil {
		return "", fmt.Errorf("下载失败: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode == 403 || resp.StatusCode == 503 {
		return "", fmt.Errorf("下载被 BookWalker 的 CDN 拒绝（%d，Cloudflare 人机校验）。请用浏览器下载 bkwk-db.sqlite.zst，再用「导入」建索引", resp.StatusCode)
	}
	if resp.StatusCode >= 300 {
		io.Copy(io.Discard, io.LimitReader(resp.Body, 1024))
		return "", fmt.Errorf("下载失败 %d: %s", resp.StatusCode, zstURL)
	}
	out, err := os.Create(zstPath)
	if err != nil {
		return "", err
	}
	_, copyErr := io.Copy(out, resp.Body)
	out.Close()
	if copyErr != nil {
		return "", copyErr
	}
	if s.log != nil {
		if fi, e := os.Stat(zstPath); e == nil {
			s.log.Info(fmt.Sprintf("数据库已下载: %s (%d MB)", zstPath, fi.Size()>>20))
		}
	}

	progress(1, 3, "解压 zstd…")
	if err := DecompressZst(zstPath, dbPath); err != nil {
		return "", err
	}
	os.Remove(zstPath)

	progress(2, 3, "扫描目录库并建索引…")
	if err := BuildBookWalkerIndexFromSqlite(dbPath, indexPath); err != nil {
		return "", err
	}
	progress(3, 3, "索引构建完成")
	if s.log != nil {
		s.log.Info("BookWalker 索引已生成: " + indexPath)
	}
	return indexPath, nil
}

// ImportLocal 导入本地文件建索引：支持官方 zst（按魔数）与已解压的 .sqlite/.db。
func (s *BookWalkerService) ImportLocal(localFile, dbPath, indexPath string, progress Progress) (string, error) {
	if progress == nil {
		progress = func(int, int, string) {}
	}
	if _, err := os.Stat(localFile); err != nil {
		return "", fmt.Errorf("找不到文件：%s", localFile)
	}
	switch {
	case IsSqliteFile(localFile):
		progress(0, 2, "读取本地 sqlite…")
		if err := BuildBookWalkerIndexFromSqlite(localFile, indexPath); err != nil {
			return "", err
		}
		progress(2, 2, "索引构建完成")
	case IsZstdFile(localFile):
		if err := os.MkdirAll(filepath.Dir(dbPath), 0o755); err != nil {
			return "", err
		}
		progress(0, 3, "解压 zst…")
		if err := DecompressZst(localFile, dbPath); err != nil {
			return "", err
		}
		progress(1, 3, "扫描目录库并建索引…")
		if err := BuildBookWalkerIndexFromSqlite(dbPath, indexPath); err != nil {
			return "", err
		}
		progress(3, 3, "索引构建完成")
	default:
		return "", fmt.Errorf("无法识别的文件：既不是 zstd 压缩包（bkwk-db.sqlite.zst），也不是 sqlite 数据库。请确认下载完整（约 55MB），或解压后再导入")
	}
	if s.log != nil {
		s.log.Info("BookWalker 索引已生成: " + indexPath)
	}
	return indexPath, nil
}

// DecompressZst zstd 解压（klauspost/compress 纯 Go 无 CGO）。
func DecompressZst(zstPath, dbPath string) error {
	src, err := os.Open(zstPath)
	if err != nil {
		return err
	}
	defer src.Close()
	dec, err := zstd.NewReader(src)
	if err != nil {
		return fmt.Errorf("zstd 解压失败: %w", err)
	}
	defer dec.Close()
	dst, err := os.Create(dbPath)
	if err != nil {
		return err
	}
	defer dst.Close()
	if _, err := io.Copy(dst, dec.IOReadCloser()); err != nil {
		return fmt.Errorf("zstd 解压失败: %w", err)
	}
	return nil
}
