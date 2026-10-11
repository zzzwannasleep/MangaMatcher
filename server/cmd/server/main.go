// KomgaBangumi Docker 版 — Go 后端入口。
//
// env：
//
//	DATA_DIR        数据目录（默认 ./data）：SQLite 库文件 + JWT secret + BW 索引 + 日志
//	PORT            监听端口（默认 8080）
//	ADMIN_USERNAME  首次启动 bootstrap 管理员用户名（默认 admin）
//	ADMIN_PASSWORD  首次启动 bootstrap 管理员密码（默认 admin123，日志打印警告）
//	KOMGA_URL       Komga 地址（首次种子，DB 设置优先）
//	KOMGA_API_KEY   Komga API Key（首次种子，DB 设置优先）
package main

import (
	"io"
	"log/slog"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"time"

	"komgabangumi/internal/api"
	"komgabangumi/internal/auth"
	"komgabangumi/internal/store"
)

func env(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func main() {
	dataDir := env("DATA_DIR", "./data")
	port := env("PORT", "8080")

	// slog 双写：stdout + DATA_DIR/logs/log-YYYYMMDD.log（v2：GET /api/logs 导出）
	logFile := setupLogFile(dataDir)
	var writer io.Writer = os.Stdout
	if logFile != nil {
		writer = io.MultiWriter(os.Stdout, logFile)
		defer logFile.Close()
	}
	log := slog.New(slog.NewTextHandler(writer, &slog.HandlerOptions{Level: slog.LevelInfo}))

	st, err := store.Open(dataDir)
	if err != nil {
		log.Error("打开数据存储失败: " + err.Error())
		os.Exit(1)
	}
	defer st.Close()

	// JWT secret（首次随机生成持久化）
	secret, err := store.EnsureJWTSecret(dataDir)
	if err != nil {
		log.Error("生成 JWT secret 失败: " + err.Error())
		os.Exit(1)
	}

	// 设置种子：KOMGA_URL / KOMGA_API_KEY / BANGUMI_TOKEN / BANGUMI_API_BASE 环境变量（DB 设置优先）
	if v := os.Getenv("KOMGA_URL"); v != "" {
		st.SeedSettingIfAbsent("komgaUrl", v)
	}
	if v := os.Getenv("KOMGA_API_KEY"); v != "" {
		st.SeedSettingIfAbsent("komgaApiKey", v)
	}
	if v := os.Getenv("BANGUMI_TOKEN"); v != "" {
		st.SeedSettingIfAbsent("bangumiToken", v)
	}
	if v := os.Getenv("BANGUMI_API_BASE"); v != "" {
		st.SeedSettingIfAbsent("bangumiApiBase", v)
	}

	// 源基址注入（env，非持久化；自建反代/mock 场景用，空 = 官方站）
	api.SetSourceBaseOverrides(
		os.Getenv("MOX_API_BASE"),
		os.Getenv("MANGADEX_API_BASE"),
		os.Getenv("BAIKE_API_BASE"),
		func() int {
			if v := os.Getenv("MOX_PACE_MS"); v != "" {
				if n, err := strconv.Atoi(v); err == nil {
					return n
				}
			}
			return 0
		}(),
	)

	// 首次启动 bootstrap 管理员
	if n, _ := st.CountUsers(); n == 0 {
		username := env("ADMIN_USERNAME", "admin")
		password := env("ADMIN_PASSWORD", "admin123")
		if password == "admin123" {
			log.Warn("使用默认管理员密码 admin123——请尽快通过环境变量 ADMIN_PASSWORD 修改！")
		}
		hash, err := auth.HashPassword(password)
		if err != nil {
			log.Error("管理员密码加密失败: " + err.Error())
			os.Exit(1)
		}
		if _, err := st.CreateUser(username, hash, "admin", nil); err != nil {
			log.Error("创建初始管理员失败: " + err.Error())
			os.Exit(1)
		}
		log.Info("已创建初始管理员 " + username)
	}

	am := auth.NewManager(secret)
	server := api.NewServer(st, am, log)
	server.SetDataDir(dataDir)

	log.Info("KomgaBangumi 后端启动: http://0.0.0.0:" + port + "（数据目录 " + dataDir + "）")
	if err := http.ListenAndServe(":"+port, server.Router()); err != nil {
		log.Error("HTTP 服务退出: " + err.Error())
		os.Exit(1)
	}
}

// setupLogFile 打开今日日志文件（DATA_DIR/logs/log-YYYYMMDD.log，跨天在启动时轮转、运行中保持当日文件）。
func setupLogFile(dataDir string) *os.File {
	dir := filepath.Join(dataDir, "logs")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil
	}
	path := filepath.Join(dir, "log-"+time.Now().Format("20060102")+".log")
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
	if err != nil {
		return nil
	}
	return f
}
