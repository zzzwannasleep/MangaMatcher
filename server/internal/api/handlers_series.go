package api

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"os"
	"strconv"
	"strings"

	"komgabangumi/internal/auth"
	"komgabangumi/internal/komga"
)

// handleLibraries GET /api/libraries（master 仅见被分配的库）。
func (s *Server) handleLibraries(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	var libs []komga.LibraryDto
	// 缓存命中 → 零 Komga 请求（打开软件即出数据，不重连重拉）
	if s.cacheEnabled() && s.cache.Ready() {
		libs = s.cache.Libraries()
	} else {
		client := s.komgaClient()
		var err error
		libs, err = client.GetLibraries()
		if err != nil {
			writeError(w, 502, "获取 Komga 库列表失败: "+err.Error())
			return
		}
	}
	if c.Role != "admin" {
		allowed, _ := s.store.GetLibraries(c.UserID)
		filtered := libs[:0]
		for _, l := range libs {
			for _, a := range allowed {
				if l.ID == a {
					filtered = append(filtered, l)
					break
				}
			}
		}
		libs = filtered
	}
	if libs == nil {
		libs = []komga.LibraryDto{}
	}
	writeJSON(w, 200, libs)
}

// handleTestKomga POST /api/settings/test-komga → {ok, version} 或 400。

// serveCover 封面输出：磁盘缓存命中直接读文件（ETag=内容哈希→304），
// 未命中回源 Komga 后写盘。写盘失败不影响响应。
func (s *Server) serveCover(w http.ResponseWriter, r *http.Request, kind, id string, fetch func() ([]byte, error), errMsg string) {
	path := s.coverPath(kind, id)
	var blob []byte
	if b, err := os.ReadFile(path); err == nil && len(b) > 0 {
		blob = b
	} else {
		b, err := fetch()
		if err != nil {
			writeError(w, statusOf(err), errMsg+": "+err.Error())
			return
		}
		blob = b
		_ = os.MkdirAll(s.coversDir(), 0o755)
		tmp := path + ".tmp"
		if os.WriteFile(tmp, blob, 0o644) == nil {
			_ = os.Rename(tmp, path) // 原子落盘，避免半张图
		}
	}
	etag := `"` + coverETag(blob) + `"`
	w.Header().Set("ETag", etag)
	w.Header().Set("Cache-Control", "private, max-age=604800")
	if match := r.Header.Get("If-None-Match"); match != "" && strings.Contains(match, etag) {
		w.WriteHeader(http.StatusNotModified)
		return
	}
	w.Header().Set("Content-Type", "image/jpeg")
	w.WriteHeader(200)
	w.Write(blob)
}

// coverETag 封面内容指纹（前 64KB 哈希 + 长度，快且足够唯一）。
func coverETag(b []byte) string {
	h := sha256.New()
	h.Write(b)
	return hex.EncodeToString(h.Sum(nil))[:16] + "-" + strconv.Itoa(len(b))
}

// dropCover 封面变更后删除磁盘缓存（下一次请求回源新图）。
func (s *Server) dropCover(kind, id string) {
	_ = os.Remove(s.coverPath(kind, id))
}

// handleSeriesThumbnail GET /api/series/{id}/thumbnail → 图片二进制代理（磁盘缓存 + ETag/304）。
func (s *Server) handleSeriesThumbnail(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	id := r.PathValue("id")
	// 权限：优先缓存内的库归属；未命中回源一次（缓存关闭/重启首访）
	client := s.komgaClient()
	libID := ""
	if s.cache != nil {
		if cs, ok := s.cache.GetSeries(id); ok {
			libID = cs.LibraryID
		}
	}
	if libID == "" {
		series, err := client.GetSeries(id)
		if err != nil {
			writeError(w, statusOf(err), "获取系列失败: "+err.Error())
			return
		}
		libID = series.LibraryID
	}
	if !s.canAccessLibrary(c, libID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	s.serveCover(w, r, "series", id, func() ([]byte, error) {
		return client.GetSeriesThumbnail(id)
	}, "获取封面失败")
}

// handleBookThumbnail GET /api/books/{id}/thumbnail → 单册封面二进制代理（磁盘缓存 + ETag/304）。
func (s *Server) handleBookThumbnail(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	id := r.PathValue("id")
	client := s.komgaClient()
	book, err := client.GetBook(id)
	if err != nil {
		writeError(w, statusOf(err), "获取书卷失败: "+err.Error())
		return
	}
	series, err := client.GetSeries(book.SeriesID)
	if err != nil {
		writeError(w, statusOf(err), "获取书卷所属系列失败: "+err.Error())
		return
	}
	if !s.canAccessLibrary(c, series.LibraryID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	s.serveCover(w, r, "book", id, func() ([]byte, error) {
		return client.GetBookThumbnail(id)
	}, "获取单册封面失败")
}

// handleTestKomga POST /api/settings/test-komga → {ok, version} 或 400。
func (s *Server) handleTestKomga(w http.ResponseWriter, r *http.Request) {
	st := s.LoadSettings()
	// 允许用请求体里的临时地址测试（不改设置）
	var body struct {
		KomgaURL     string `json:"komgaUrl"`
		KomgaAPIKey  string `json:"komgaApiKey"`
		KomgaUser    string `json:"komgaUser"`
		KomgaPassword string `json:"komgaPassword"`
	}
	if r.Body != nil {
		var raw map[string]any
		if err := json.NewDecoder(r.Body).Decode(&raw); err == nil {
			if v, ok := raw["komgaUrl"].(string); ok && v != "" {
				body.KomgaURL = v
			}
			if v, ok := raw["komgaApiKey"].(string); ok && v != "" && v != mask {
				body.KomgaAPIKey = v
			}
			if v, ok := raw["komgaUser"].(string); ok {
				body.KomgaUser = v
			}
			if v, ok := raw["komgaPassword"].(string); ok && v != "" && v != mask {
				body.KomgaPassword = v
			}
		}
	}
	url := orStr(body.KomgaURL, st.KomgaURL)
	apiKey := orStr(body.KomgaAPIKey, st.KomgaAPIKey)
	user := orStr(body.KomgaUser, st.KomgaUser)
	password := orStr(body.KomgaPassword, st.KomgaPassword)

	client := komga.New(url, apiKey, user, password)
	version, err := client.TestKomga()
	if err != nil {
		writeError(w, 400, "Komga 连接失败: "+err.Error())
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "version": version})
}

func orStr(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

func atoiDefault(s string, def int) int {
	if s == "" {
		return def
	}
	n, err := strconv.Atoi(s)
	if err != nil {
		return def
	}
	return n
}

func statusOf(err error) int {
	if apiErr, ok := err.(*komga.APIError); ok {
		if apiErr.Status == 401 {
			// Komga 侧认证失败映射为 502（本服务自身的 401 由认证中间件负责）
			return 502
		}
		return apiErr.Status
	}
	return 502
}