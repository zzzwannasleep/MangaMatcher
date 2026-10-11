package api

import (
	"encoding/json"
	"net/http"

	"komgabangumi/internal/auth"
	"komgabangumi/internal/jobs"
)

// handleJobsCreate POST /api/jobs {libraryId, seriesIds（空=全库）, dryRun, forceRefresh?, overrideMatch?, kind?} → {id}。
func (s *Server) handleJobsCreate(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	var body struct {
		LibraryID     string   `json:"libraryId"`
		SeriesIDs     []string `json:"seriesIds"`
		DryRun        *bool    `json:"dryRun"`
		ForceRefresh  *bool    `json:"forceRefresh"`
		OverrideMatch bool     `json:"overrideMatch"`
		Kind          string   `json:"kind"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeError(w, 400, "请求体格式错误")
		return
	}
	if body.LibraryID == "" {
		writeError(w, 400, "libraryId 不能为空")
		return
	}
	if c.Role != "admin" && !s.canAccessLibrary(c, body.LibraryID) {
		writeError(w, 403, "没有该库的刮削权限")
		return
	}
	st := s.LoadSettings()
	dryRun := st.DryRunDefault
	if body.DryRun != nil {
		dryRun = *body.DryRun
	}
	forceRefresh := st.BatchForceRefresh
	if body.ForceRefresh != nil {
		forceRefresh = *body.ForceRefresh
	}
	kind := body.Kind
	if kind != "" && kind != "scrape" && kind != "tag" {
		writeError(w, 400, "无效的 kind："+kind+"（可选 scrape|tag）")
		return
	}
	opts := jobs.JobOptions{DryRun: dryRun, ForceRefresh: forceRefresh,
		OverrideMatch: body.OverrideMatch, Kind: kind}
	if kind == "" {
		opts.Kind = "scrape"
	}
	id := s.jobs.Submit(body.LibraryID, body.SeriesIDs, opts)
	s.log.Info("创建任务 " + id + "（kind=" + opts.Kind + "，库 " + body.LibraryID +
		"，dry-run=" + boolCn(dryRun) + "，forceRefresh=" + boolCn(forceRefresh) +
		"，overrideMatch=" + boolCn(body.OverrideMatch) + "）")
	writeJSON(w, 200, map[string]string{"id": id})
}

// handleJobsList GET /api/jobs。
func (s *Server) handleJobsList(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, 200, s.jobs.List())
}

// handleJobGet GET /api/jobs/{id}。
func (s *Server) handleJobGet(w http.ResponseWriter, r *http.Request) {
	job, ok := s.jobs.Get(r.PathValue("id"))
	if !ok {
		writeError(w, 404, "任务不存在")
		return
	}
	writeJSON(w, 200, job)
}

// handleJobCancel POST /api/jobs/{id}/cancel。
func (s *Server) handleJobCancel(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	if !s.jobs.Cancel(id) {
		job, ok := s.jobs.Get(id)
		if !ok {
			writeError(w, 404, "任务不存在")
			return
		}
		writeError(w, 400, "任务不在运行中（当前状态 "+job.Status+"）")
		return
	}
	s.log.Info("请求取消任务 " + id)
	writeJSON(w, 200, map[string]any{"ok": true})
}

// handleSettingsGet GET /api/settings。
func (s *Server) handleSettingsGet(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, 200, s.LoadSettings().Masked())
}

// handleSettingsPatch PATCH /api/settings。
func (s *Server) handleSettingsPatch(w http.ResponseWriter, r *http.Request) {
	body, err := readBody(r)
	if err != nil {
		writeError(w, 400, "请求体格式错误")
		return
	}
	st, err := s.LoadSettings().ApplyPatch(body)
	if err != nil {
		writeError(w, 400, "设置格式错误: "+err.Error())
		return
	}
	if err := s.SaveSettings(st); err != nil {
		writeError(w, 500, "保存设置失败")
		return
	}
	s.log.Info("管理员更新了全局设置")
	writeJSON(w, 200, st.Masked())
}

func readBody(r *http.Request) (json.RawMessage, error) {
	var raw json.RawMessage
	if err := json.NewDecoder(r.Body).Decode(&raw); err != nil {
		return nil, err
	}
	return raw, nil
}

func boolCn(b bool) string {
	if b {
		return "是"
	}
	return "否"
}