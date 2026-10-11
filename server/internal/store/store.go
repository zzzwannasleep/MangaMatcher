// Package store：SQLite 持久化（modernc.org/sqlite，纯 Go 无 CGO）。
// users / user_libraries / settings 三张表 + JWT secret 文件。
package store

import (
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"

	_ "modernc.org/sqlite"
)

// User 用户。
type User struct {
	ID           string    `json:"id"`
	Username     string    `json:"username"`
	PasswordHash string    `json:"-"`
	Role         string    `json:"role"` // admin|master
	Libraries    []string  `json:"libraries"`
	CreatedAt    time.Time `json:"createdAt"`
}

// Store SQLite 存储。
type Store struct {
	db *sql.DB
}

// Open 打开（或创建）数据库，执行迁移。dataDir 默认 ./data。
func Open(dataDir string) (*Store, error) {
	if err := os.MkdirAll(dataDir, 0o755); err != nil {
		return nil, fmt.Errorf("创建数据目录失败: %w", err)
	}
	db, err := sql.Open("sqlite", filepath.Join(dataDir, "komgabangumi.db"))
	if err != nil {
		return nil, fmt.Errorf("打开数据库失败: %w", err)
	}
	// modernc sqlite 单写者：限制连接数避免 SQLITE_BUSY
	db.SetMaxOpenConns(1)
	s := &Store{db: db}
	if err := s.migrate(); err != nil {
		db.Close()
		return nil, err
	}
	return s, nil
}

// Close 关闭数据库。
func (s *Store) Close() error { return s.db.Close() }

func (s *Store) migrate() error {
	stmts := []string{
		`CREATE TABLE IF NOT EXISTS users (
			id TEXT PRIMARY KEY,
			username TEXT NOT NULL UNIQUE,
			password_hash TEXT NOT NULL,
			role TEXT NOT NULL,
			created_at TEXT NOT NULL
		)`,
		`CREATE TABLE IF NOT EXISTS user_libraries (
			user_id TEXT NOT NULL,
			library_id TEXT NOT NULL,
			PRIMARY KEY (user_id, library_id)
		)`,
		`CREATE TABLE IF NOT EXISTS settings (
			key TEXT PRIMARY KEY,
			value TEXT NOT NULL
		)`,
		`CREATE TABLE IF NOT EXISTS scrape_state (
			series_id TEXT PRIMARY KEY,
			has_cover INT NOT NULL DEFAULT 0,
			has_summary INT NOT NULL DEFAULT 0,
			links TEXT,
			checked_at TEXT
		)`,
	}
	for _, q := range stmts {
		if _, err := s.db.Exec(q); err != nil {
			return fmt.Errorf("迁移失败: %w", err)
		}
	}
	return nil
}

// ---------- 刮削状态（封面/简介是否已刮，6 种筛选用） ----------

// ScrapeState 单系列的刮削状态。
type ScrapeState struct {
	SeriesID   string
	HasCover   bool
	HasSummary bool
	Links      string
	CheckedAt  time.Time
}

// GetScrapeState 读单系列状态（无记录返回 nil）。
func (s *Store) GetScrapeState(seriesID string) (*ScrapeState, error) {
	row := s.db.QueryRow(`SELECT series_id, has_cover, has_summary, links, checked_at FROM scrape_state WHERE series_id=?`, seriesID)
	return s.scanState(row)
}

func (s *Store) scanState(row *sql.Row) (*ScrapeState, error) {
	var st ScrapeState
	var cover, summary int
	var links, checked sql.NullString
	if err := row.Scan(&st.SeriesID, &cover, &summary, &links, &checked); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	st.HasCover, st.HasSummary = cover == 1, summary == 1
	st.Links = links.String
	st.CheckedAt, _ = time.Parse(time.RFC3339, checked.String)
	return &st, nil
}

// ListScrapeStates 批量读状态（返回 map，缺的系列无键）。
func (s *Store) ListScrapeStates(ids []string) (map[string]ScrapeState, error) {
	out := map[string]ScrapeState{}
	for _, id := range ids {
		st, err := s.GetScrapeState(id)
		if err != nil {
			return nil, err
		}
		if st != nil {
			out[id] = *st
		}
	}
	return out, nil
}

// SetScrapeState 写单系列状态（upsert；links 为快照）。
func (s *Store) SetScrapeState(st ScrapeState) error {
	cover, summary := 0, 0
	if st.HasCover {
		cover = 1
	}
	if st.HasSummary {
		summary = 1
	}
	checked := st.CheckedAt
	if checked.IsZero() {
		checked = time.Now().UTC()
	}
	_, err := s.db.Exec(`INSERT INTO scrape_state (series_id, has_cover, has_summary, links, checked_at)
		VALUES (?,?,?,?,?)
		ON CONFLICT(series_id) DO UPDATE SET has_cover=excluded.has_cover,
		has_summary=excluded.has_summary, links=excluded.links, checked_at=excluded.checked_at`,
		st.SeriesID, cover, summary, st.Links, checked.Format(time.RFC3339))
	return err
}

// ---------- JWT secret ----------

// EnsureJWTSecret 首次生成随机 secret 并持久化（DATA_DIR/jwt.secret，0600）。
func EnsureJWTSecret(dataDir string) ([]byte, error) {
	path := filepath.Join(dataDir, "jwt.secret")
	if b, err := os.ReadFile(path); err == nil && len(b) >= 32 {
		return b, nil
	}
	buf := make([]byte, 32)
	if _, err := rand.Read(buf); err != nil {
		return nil, err
	}
	if err := os.WriteFile(path, buf, 0o600); err != nil {
		return nil, err
	}
	return buf, nil
}

// ---------- 用户 ----------

func newID() string {
	buf := make([]byte, 8)
	rand.Read(buf)
	return hex.EncodeToString(buf)
}

// CountUsers 用户总数。
func (s *Store) CountUsers() (int, error) {
	var n int
	err := s.db.QueryRow(`SELECT COUNT(*) FROM users`).Scan(&n)
	return n, err
}

// CountAdmins admin 数量。
func (s *Store) CountAdmins() (int, error) {
	var n int
	err := s.db.QueryRow(`SELECT COUNT(*) FROM users WHERE role='admin'`).Scan(&n)
	return n, err
}

// CreateUser 创建用户（含库授权）。
func (s *Store) CreateUser(username, passwordHash, role string, libraries []string) (*User, error) {
	id := newID()
	createdAt := time.Now().UTC()
	_, err := s.db.Exec(`INSERT INTO users (id, username, password_hash, role, created_at) VALUES (?,?,?,?,?)`,
		id, username, passwordHash, role, createdAt.Format(time.RFC3339))
	if err != nil {
		return nil, err
	}
	if err := s.setLibraries(id, libraries); err != nil {
		return nil, err
	}
	return s.GetUserByID(id)
}

// GetUserByID 按 id 查询。
func (s *Store) GetUserByID(id string) (*User, error) {
	row := s.db.QueryRow(`SELECT id, username, password_hash, role, created_at FROM users WHERE id=?`, id)
	return s.scanUser(row)
}

// GetUserByUsername 按用户名查询。
func (s *Store) GetUserByUsername(username string) (*User, error) {
	row := s.db.QueryRow(`SELECT id, username, password_hash, role, created_at FROM users WHERE username=?`, username)
	return s.scanUser(row)
}

func (s *Store) scanUser(row *sql.Row) (*User, error) {
	var u User
	var createdAt string
	if err := row.Scan(&u.ID, &u.Username, &u.PasswordHash, &u.Role, &createdAt); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	u.CreatedAt, _ = time.Parse(time.RFC3339, createdAt)
	u.Libraries, _ = s.GetLibraries(u.ID)
	return &u, nil
}

// ListUsers 用户列表。
// 注意：MaxOpenConns(1)，外层 rows 迭代期间不能嵌套查询（GetLibraries 会永久排队死锁），
// 必须先收完行再逐个取库授权。
func (s *Store) ListUsers() ([]*User, error) {
	rows, err := s.db.Query(`SELECT id, username, password_hash, role, created_at FROM users ORDER BY created_at`)
	if err != nil {
		return nil, err
	}
	var out []*User
	for rows.Next() {
		var u User
		var createdAt string
		if err := rows.Scan(&u.ID, &u.Username, &u.PasswordHash, &u.Role, &createdAt); err != nil {
			rows.Close()
			return nil, err
		}
		u.CreatedAt, _ = time.Parse(time.RFC3339, createdAt)
		out = append(out, &u)
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return nil, err
	}
	rows.Close()
	for _, u := range out {
		u.Libraries, _ = s.GetLibraries(u.ID)
	}
	return out, nil
}

// UpdateUser 更新用户（passwordHash/role 传空表示不改；libraries 传 nil 表示不改）。
func (s *Store) UpdateUser(id, passwordHash, role string, libraries *[]string) error {
	if passwordHash != "" {
		if _, err := s.db.Exec(`UPDATE users SET password_hash=? WHERE id=?`, passwordHash, id); err != nil {
			return err
		}
	}
	if role != "" {
		if _, err := s.db.Exec(`UPDATE users SET role=? WHERE id=?`, role, id); err != nil {
			return err
		}
	}
	if libraries != nil {
		return s.setLibraries(id, *libraries)
	}
	return nil
}

// DeleteUser 删除用户。
func (s *Store) DeleteUser(id string) error {
	if _, err := s.db.Exec(`DELETE FROM user_libraries WHERE user_id=?`, id); err != nil {
		return err
	}
	_, err := s.db.Exec(`DELETE FROM users WHERE id=?`, id)
	return err
}

func (s *Store) setLibraries(userID string, libraries []string) error {
	if _, err := s.db.Exec(`DELETE FROM user_libraries WHERE user_id=?`, userID); err != nil {
		return err
	}
	for _, lib := range libraries {
		if _, err := s.db.Exec(`INSERT INTO user_libraries (user_id, library_id) VALUES (?,?)`, userID, lib); err != nil {
			return err
		}
	}
	return nil
}

// GetLibraries 用户的库授权列表。
func (s *Store) GetLibraries(userID string) ([]string, error) {
	rows, err := s.db.Query(`SELECT library_id FROM user_libraries WHERE user_id=?`, userID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []string
	for rows.Next() {
		var lib string
		if err := rows.Scan(&lib); err != nil {
			return nil, err
		}
		out = append(out, lib)
	}
	return out, rows.Err()
}

// ---------- 设置（key/value） ----------

// GetSetting 读单个设置。
func (s *Store) GetSetting(key string) (string, bool) {
	var v string
	err := s.db.QueryRow(`SELECT value FROM settings WHERE key=?`, key).Scan(&v)
	if err != nil {
		return "", false
	}
	return v, true
}

// SetSetting 写单个设置。
func (s *Store) SetSetting(key, value string) error {
	_, err := s.db.Exec(`INSERT INTO settings (key, value) VALUES (?,?)
		ON CONFLICT(key) DO UPDATE SET value=excluded.value`, key, value)
	return err
}

// SeedSettingIfAbsent 环境变量种子：仅当键不存在时写入。
func (s *Store) SeedSettingIfAbsent(key, value string) error {
	if _, ok := s.GetSetting(key); ok {
		return nil
	}
	return s.SetSetting(key, value)
}
