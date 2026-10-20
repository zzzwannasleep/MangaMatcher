// Package ehtags：EhTagTranslation 中文标签库（E-Hentai 标签 → 中文名）。
//
// 数据源：https://github.com/EhTagTranslation/Database 的 release 产物 db.text.json.gz
// （13 个命名空间、约 4.1 万条，含 name/intro/links 三个字段）。
//
// 设计要点：
//   - 纯静态只读库，进程内一份；Configure 异步加载，失败只告警、绝不阻塞主流程；
//   - 未就绪时 Translate 返回 false，调用方回落到英文原名，不抛错；
//   - 命名空间中文名优先取库里的 frontMatters.name（可随库自更新），无库时用内置兜底表。
package ehtags

import (
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"sync"
	"time"
)

// Entry 单条标签。
type Entry struct {
	Name  string `json:"name"`
	Intro string `json:"intro"`
	Links string `json:"links"`
}

// Namespace 一个命名空间（db.text.json 的 data[] 元素）。
type Namespace struct {
	Namespace    string `json:"namespace"`
	FrontMatters struct {
		Name        string   `json:"name"`
		Description string   `json:"description"`
		Key         string   `json:"key"`
		Rules       []string `json:"rules"`
	} `json:"frontMatters"`
	Count int              `json:"count"`
	Data  map[string]Entry `json:"data"`
}

// File db.text.json 顶层结构（repo/head/version/data）。
type File struct {
	Repo    string          `json:"repo"`
	Head    json.RawMessage `json:"head"`
	Version int             `json:"version"`
	Data    []Namespace     `json:"data"`
}

// DB 已加载的标签库（只读，可并发使用）。
type DB struct {
	Version string // 库自述版本（db.text.json 的 version 字段 + 文件修改时间）
	Path    string
	Loaded  time.Time

	tags   map[string]map[string]Entry // 命名空间(小写) → 标签(小写) → 词条
	labels map[string]string           // 命名空间(小写) → 中文名
	total  int
}

// fallbackLabels 兜底命名空间中文名（库未就绪时用；取自 db.text.json 的 frontMatters.name）。
var fallbackLabels = map[string]string{
	"rows":      "内容索引",
	"reclass":   "重新分类",
	"language":  "语言",
	"parody":    "原作",
	"character": "角色",
	"group":     "团队",
	"artist":    "艺术家",
	"cosplayer": "Coser",
	"male":      "男性",
	"female":    "女性",
	"mixed":     "混合",
	"other":     "其他",
	"location":  "地点",
	"category":  "分类",
}

// Load 从磁盘读取 db.text.json。
func Load(path string) (*DB, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("读取 EH 标签库失败：%w", err)
	}
	var f File
	if err := json.Unmarshal(raw, &f); err != nil {
		return nil, fmt.Errorf("解析 EH 标签库失败（%s 不是合法的 db.text.json）：%w", path, err)
	}
	if len(f.Data) == 0 {
		return nil, fmt.Errorf("EH 标签库为空：%s", path)
	}
	db := &DB{
		Path:   path,
		Loaded: time.Now(),
		tags:   make(map[string]map[string]Entry, len(f.Data)),
		labels: make(map[string]string, len(f.Data)),
	}
	for _, ns := range f.Data {
		key := strings.ToLower(strings.TrimSpace(ns.Namespace))
		if key == "" {
			continue
		}
		m := make(map[string]Entry, len(ns.Data))
		for tag, e := range ns.Data {
			m[normalizeTag(tag)] = e
			db.total++
		}
		db.tags[key] = m
		if name := strings.TrimSpace(ns.FrontMatters.Name); name != "" {
			db.labels[key] = name
		}
	}
	ver := fmt.Sprintf("v%d", f.Version)
	if st, err := os.Stat(path); err == nil {
		ver = fmt.Sprintf("v%d·%s", f.Version, st.ModTime().Format("2006-01-02"))
	}
	db.Version = ver
	return db, nil
}

// normalizeTag E-Hentai 侧会把多词标签写成 "artist cg" 这类带空格的形式，
// 而库里的键是 "artistcg"（本次实测确认）。统一去空格 + 小写后比对。
func normalizeTag(s string) string {
	s = strings.ToLower(strings.TrimSpace(s))
	return strings.Join(strings.Fields(s), " ")
}

// tagKeys 返回某个标签可能的查表键（原样 + 去空格）。
func tagKeys(s string) []string {
	n := normalizeTag(s)
	out := []string{n}
	if compact := strings.ReplaceAll(n, " ", ""); compact != n {
		out = append(out, compact)
	}
	return out
}

// Translate 查中文名；未收录 / 未加载 / 译文与原文相同 时返回 false。
func (d *DB) Translate(ns, tag string) (string, bool) {
	if d == nil || tag == "" {
		return "", false
	}
	m := d.tags[strings.ToLower(strings.TrimSpace(ns))]
	if m == nil {
		return "", false
	}
	for _, k := range tagKeys(tag) {
		if e, ok := m[k]; ok {
			name := strings.TrimSpace(e.Name)
			if name == "" || strings.EqualFold(name, tag) {
				return "", false
			}
			return name, true
		}
	}
	return "", false
}

// Intro 查中文简介（最多截断到 limit 个字符，limit<=0 不截断）。
func (d *DB) Intro(ns, tag string, limit int) (string, bool) {
	if d == nil || tag == "" {
		return "", false
	}
	m := d.tags[strings.ToLower(strings.TrimSpace(ns))]
	if m == nil {
		return "", false
	}
	for _, k := range tagKeys(tag) {
		if e, ok := m[k]; ok {
			s := strings.TrimSpace(e.Intro)
			if s == "" {
				return "", false
			}
			r := []rune(s)
			if limit > 0 && len(r) > limit {
				s = string(r[:limit]) + "…"
			}
			return s, true
		}
	}
	return "", false
}

// Label 命名空间中文名（库里优先，兜底内置表，再兜底返回原名）。
func (d *DB) Label(ns string) string {
	key := strings.ToLower(strings.TrimSpace(ns))
	if d != nil {
		if v := d.labels[key]; v != "" {
			return v
		}
	}
	if v := fallbackLabels[key]; v != "" {
		return v
	}
	return key
}

// Size 词条总数。
func (d *DB) Size() int {
	if d == nil {
		return 0
	}
	return d.total
}

// ---- 进程内单例 ----

var (
	mu        sync.RWMutex
	cur       *DB
	lastError string
)

// Use 直接装载一个本地库（测试 / 离线场景）；失败会清掉当前库。
func Use(path string) error {
	db, err := Load(path)
	mu.Lock()
	defer mu.Unlock()
	if err != nil {
		cur = nil
		lastError = err.Error()
		return err
	}
	cur = db
	lastError = ""
	return nil
}

// Ready 库是否可用。
func Ready() bool {
	mu.RLock()
	defer mu.RUnlock()
	return cur != nil
}

// Version 库版本描述（未加载返回 ""）。
func Version() string {
	mu.RLock()
	defer mu.RUnlock()
	if cur == nil {
		return ""
	}
	return cur.Version
}

// LastError 最近一次加载失败原因（成功时为空）。
func LastError() string {
	mu.RLock()
	defer mu.RUnlock()
	return lastError
}

// Translate 用当前进程库翻译；未就绪返回 false。
func Translate(ns, tag string) (string, bool) {
	mu.RLock()
	db := cur
	mu.RUnlock()
	return db.Translate(ns, tag)
}

// Intro 用当前进程库查简介。
func Intro(ns, tag string, limit int) (string, bool) {
	mu.RLock()
	db := cur
	mu.RUnlock()
	return db.Intro(ns, tag, limit)
}

// Label 命名空间中文名。
func Label(ns string) string {
	mu.RLock()
	db := cur
	mu.RUnlock()
	return db.Label(ns)
}

// Size 当前库词条数。
func Size() int {
	mu.RLock()
	db := cur
	mu.RUnlock()
	return db.Size()
}
