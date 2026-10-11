package api

import (
	"encoding/json"
	"net/http"
	"strconv"

	"komgabangumi/internal/auth"
	"komgabangumi/internal/komga"
)

// handleLibraries GET /api/libraries（master 仅见被分配的库）。
func (s *Server) handleLibraries(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	client := s.komgaClient()
	libs, err := client.GetLibraries()
	if err != nil {
		writeError(w, 502, "获取 Komga 库列表失败: "+err.Error())
		return
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

// handleSeriesThumbnail GET /api/series/{id}/thumbnail → 图片二进制代理（Accept: image/jpeg）。
func (s *Server) handleSeriesThumbnail(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	id := r.PathValue("id")
	client := s.komgaClient()
	series, err := client.GetSeries(id)
	if err != nil {
		writeError(w, statusOf(err), "获取系列失败: "+err.Error())
		return
	}
	if !s.canAccessLibrary(c, series.LibraryID) {
		writeError(w, 403, "没有该库的访问权限")
		return
	}
	blob, err := client.GetSeriesThumbnail(id)
	if err != nil {
		writeError(w, statusOf(err), "获取封面失败: "+err.Error())
		return
	}
	w.Header().Set("Content-Type", "image/jpeg")
	w.WriteHeader(200)
	w.Write(blob)
}

// handleBookThumbnail GET /api/books/{id}/thumbnail → 单册封面二进制代理。
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
	blob, err := client.GetBookThumbnail(id)
	if err != nil {
		writeError(w, statusOf(err), "获取单册封面失败: "+err.Error())
		return
	}
	w.Header().Set("Content-Type", "image/jpeg")
	w.WriteHeader(200)
	w.Write(blob)
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