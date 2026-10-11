package api

import (
	"encoding/json"
	"net/http"

	"komgabangumi/internal/auth"
	"komgabangumi/internal/store"
)

// userView 用户 JSON 视图（不含密码哈希）。
type userView struct {
	ID        string   `json:"id"`
	Username  string   `json:"username"`
	Role      string   `json:"role"`
	Libraries []string `json:"libraries"`
	CreatedAt string   `json:"createdAt"`
}

func toUserView(u *store.User) userView {
	libs := u.Libraries
	if libs == nil {
		libs = []string{}
	}
	return userView{ID: u.ID, Username: u.Username, Role: u.Role, Libraries: libs, CreatedAt: u.CreatedAt.Format("2006-01-02T15:04:05Z")}
}

// handleLogin POST /api/auth/login {username, password} → {token, user}。
func (s *Server) handleLogin(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Username string `json:"username"`
		Password string `json:"password"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeError(w, 400, "请求体格式错误")
		return
	}
	u, err := s.store.GetUserByUsername(body.Username)
	if err != nil {
		writeError(w, 500, "查询用户失败")
		return
	}
	if u == nil || !auth.CheckPassword(u.PasswordHash, body.Password) {
		writeError(w, 401, "用户名或密码错误")
		return
	}
	token, err := s.auth.Issue(u)
	if err != nil {
		writeError(w, 500, "签发令牌失败")
		return
	}
	writeJSON(w, 200, map[string]any{
		"token": token,
		"user":  map[string]string{"id": u.ID, "username": u.Username, "role": u.Role},
	})
}

// handleMe GET /api/me → {id, username, role, libraries}。
func (s *Server) handleMe(w http.ResponseWriter, r *http.Request) {
	c := auth.FromContext(r.Context())
	u, err := s.store.GetUserByID(c.UserID)
	if err != nil || u == nil {
		writeError(w, 404, "用户不存在")
		return
	}
	writeJSON(w, 200, map[string]any{
		"id":        u.ID,
		"username":  u.Username,
		"role":      u.Role,
		"libraries": s.userLibraries(c),
	})
}

// handleUsersList GET /api/users。
func (s *Server) handleUsersList(w http.ResponseWriter, r *http.Request) {
	users, err := s.store.ListUsers()
	if err != nil {
		writeError(w, 500, "查询用户列表失败")
		return
	}
	out := make([]userView, 0, len(users))
	for _, u := range users {
		out = append(out, toUserView(u))
	}
	writeJSON(w, 200, out)
}

// handleUsersNameIndex GET /api/users/name-index（内部用：用户名索引）。
func (s *Server) handleUsersNameIndex(w http.ResponseWriter, r *http.Request) {
	users, err := s.store.ListUsers()
	if err != nil {
		writeError(w, 500, "查询用户列表失败")
		return
	}
	out := make([]string, 0, len(users))
	for _, u := range users {
		out = append(out, u.Username)
	}
	writeJSON(w, 200, out)
}

// handleUsersCreate POST /api/users {username, password, role, libraries}。
func (s *Server) handleUsersCreate(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Username  string   `json:"username"`
		Password  string   `json:"password"`
		Role      string   `json:"role"`
		Libraries []string `json:"libraries"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeError(w, 400, "请求体格式错误")
		return
	}
	if body.Username == "" || body.Password == "" {
		writeError(w, 400, "用户名和密码不能为空")
		return
	}
	if body.Role != "admin" && body.Role != "master" {
		writeError(w, 400, "角色必须是 admin 或 master")
		return
	}
	if existing, _ := s.store.GetUserByUsername(body.Username); existing != nil {
		writeError(w, 400, "用户名已存在")
		return
	}
	hash, err := auth.HashPassword(body.Password)
	if err != nil {
		writeError(w, 500, "密码加密失败")
		return
	}
	u, err := s.store.CreateUser(body.Username, hash, body.Role, body.Libraries)
	if err != nil {
		writeError(w, 500, "创建用户失败")
		return
	}
	s.log.Info("管理员创建用户 " + u.Username + "（角色 " + u.Role + "）")
	writeJSON(w, 200, toUserView(u))
}

// handleUsersPatch PATCH /api/users/{id} {password?, role?, libraries?}。
func (s *Server) handleUsersPatch(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	u, err := s.store.GetUserByID(id)
	if err != nil || u == nil {
		writeError(w, 404, "用户不存在")
		return
	}
	var body struct {
		Password  *string   `json:"password"`
		Role      *string   `json:"role"`
		Libraries *[]string `json:"libraries"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeError(w, 400, "请求体格式错误")
		return
	}
	newRole := ""
	if body.Role != nil {
		if *body.Role != "admin" && *body.Role != "master" {
			writeError(w, 400, "角色必须是 admin 或 master")
			return
		}
		newRole = *body.Role
	}
	hash := ""
	if body.Password != nil && *body.Password != "" {
		h, err := auth.HashPassword(*body.Password)
		if err != nil {
			writeError(w, 500, "密码加密失败")
			return
		}
		hash = h
	}
	// 最后一个 admin 保护：降级/改角色不能让 admin 数量归零
	if newRole != "" && newRole != "admin" && u.Role == "admin" {
		n, _ := s.store.CountAdmins()
		if n <= 1 {
			writeError(w, 400, "不能修改最后一个管理员的角色")
			return
		}
	}
	if err := s.store.UpdateUser(id, hash, newRole, body.Libraries); err != nil {
		writeError(w, 500, "更新用户失败")
		return
	}
	s.log.Info("管理员更新用户 " + u.Username)
	u2, _ := s.store.GetUserByID(id)
	writeJSON(w, 200, toUserView(u2))
}

// handleUsersDelete DELETE /api/users/{id}。
func (s *Server) handleUsersDelete(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	caller := auth.FromContext(r.Context())
	u, err := s.store.GetUserByID(id)
	if err != nil || u == nil {
		writeError(w, 404, "用户不存在")
		return
	}
	if u.ID == caller.UserID {
		writeError(w, 400, "不能删除自己")
		return
	}
	if u.Role == "admin" {
		n, _ := s.store.CountAdmins()
		if n <= 1 {
			writeError(w, 400, "不能删除最后一个管理员")
			return
		}
	}
	if err := s.store.DeleteUser(id); err != nil {
		writeError(w, 500, "删除用户失败")
		return
	}
	s.log.Info("管理员删除用户 " + u.Username)
	writeJSON(w, 200, map[string]any{"ok": true})
}