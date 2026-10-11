// Package komgacache：Komga 库数据双层缓存（内存 + SQLite 持久化）。
//
// 目标：打开软件即出库数据，不重复拉全库；后台按间隔自动对齐 Komga 增量。
// 读路径全部走内存（零 Komga 请求）；写路径（元数据/封面变更）走 InvalidateSeries
// 单系列回源刷新。Komga 连接设置变更时 Reset 清空，避免串库。
package komgacache

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"log/slog"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"komgabangumi/internal/komga"
)

// Cache 库缓存。
type Cache struct {
	mu      sync.RWMutex
	libs    []komga.LibraryDto
	series  map[string]*komga.SeriesDto // seriesID → DTO
	byLib   map[string][]string         // libraryID → 按 name 排序的 seriesID 列表
	lastSync time.Time                  // 最近一次成功全量对齐

	refreshing atomic.Bool
	db         *sql.DB
	client     func() *komga.Client
	log        *slog.Logger
	stop       chan struct{}
	startOnce  sync.Once
}

// New 创建缓存（不自动加载，Load 在 Start 前调用）。
func New(db *sql.DB, clientFn func() *komga.Client, log *slog.Logger) *Cache {
	return &Cache{db: db, client: clientFn, log: log, stop: make(chan struct{})}
}

// Load 从 SQLite 恢复内存（Start 的一部分；测试可单独调用）。
func (c *Cache) Load() error { return c.load() }

// Start 从 SQLite 恢复内存并启动自动刷新循环。
// interval 每轮循环重新求值（设置热更新）；<=0 表示关闭自动刷新。
func (c *Cache) Start(interval func() time.Duration) {
	c.startOnce.Do(func() {
		if err := c.Load(); err != nil && c.log != nil {
			c.log.Warn("库缓存从 SQLite 恢复失败: " + err.Error())
		}
		go func() {
			// 启动后短暂延迟再做首次对齐（等设置/Komga 就绪）
			select {
			case <-c.stop:
				return
			case <-time.After(3 * time.Second):
			}
			if interval() <= 0 {
				return
			}
			for {
				if err := c.RefreshAll(); err != nil && c.log != nil {
					c.log.Warn("库缓存自动刷新失败: " + err.Error())
				}
				d := interval()
				if d <= 0 {
					return
				}
				select {
				case <-c.stop:
					return
				case <-time.After(d):
				}
			}
		}()
	})
}

// Stop 停止自动刷新。
func (c *Cache) Stop() {
	select {
	case <-c.stop:
	default:
		close(c.stop)
	}
}

// ---------- 读路径（零 Komga 请求） ----------

// Ready 缓存是否可用（有库数据）。
func (c *Cache) Ready() bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.lastSync != time.Time{}
}

// Libraries 库列表。
func (c *Cache) Libraries() []komga.LibraryDto {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.libs
}

// SeriesPage 库内系列分页（内存裁剪，name 升序）。
func (c *Cache) SeriesPage(libraryID string, page, size int) *komga.Page[komga.SeriesDto] {
	c.mu.RLock()
	defer c.mu.RUnlock()
	ids := c.byLib[libraryID]
	start := page * size
	out := &komga.Page[komga.SeriesDto]{
		Content: []komga.SeriesDto{}, TotalElements: int64(len(ids)),
		NumberOfElements: 0,
	}
	if start >= len(ids) || size <= 0 {
		return out
	}
	end := start + size
	if end > len(ids) {
		end = len(ids)
	}
	for _, id := range ids[start:end] {
		if s, ok := c.series[id]; ok {
			out.Content = append(out.Content, *s)
		}
	}
	out.NumberOfElements = len(out.Content)
	return out
}

// SeriesInLib 库内全部系列（筛选/搜索/导出用）。
func (c *Cache) SeriesInLib(libraryID string) []komga.SeriesDto {
	c.mu.RLock()
	defer c.mu.RUnlock()
	ids := c.byLib[libraryID]
	out := make([]komga.SeriesDto, 0, len(ids))
	for _, id := range ids {
		if s, ok := c.series[id]; ok {
			out = append(out, *s)
		}
	}
	return out
}

// GetSeries 单系列（命中缓存返回 true）。
func (c *Cache) GetSeries(id string) (*komga.SeriesDto, bool) {
	c.mu.RLock()
	defer c.mu.RUnlock()
	s, ok := c.series[id]
	return s, ok
}

// SeriesCount 缓存的系列总数。
func (c *Cache) SeriesCount() int {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return len(c.series)
}

// LibraryCount 缓存的库数量。
func (c *Cache) LibraryCount() int {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return len(c.libs)
}

// LastSyncAt 最近一次成功全量对齐时间。
func (c *Cache) LastSyncAt() time.Time {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.lastSync
}

// IsRefreshing 是否正在后台刷新。
func (c *Cache) IsRefreshing() bool { return c.refreshing.Load() }

// ---------- 写路径 ----------

// RefreshAll 全量对齐：库列表 + 每库全量系列，差量合并进内存并落 SQLite。
// 并发去重：已在刷新中直接返回 nil。
func (c *Cache) RefreshAll() error {
	if !c.refreshing.CompareAndSwap(false, true) {
		return nil
	}
	defer c.refreshing.Store(false)

	client := c.client()
	libs, err := client.GetLibraries()
	if err != nil {
		return fmt.Errorf("获取库列表失败: %w", err)
	}

	// 新快照
	newSeries := map[string]*komga.SeriesDto{}
	newByLib := map[string][]string{}
	for _, lib := range libs {
		list, err := client.ListSeriesInLibrary(lib.ID)
		if err != nil {
			return fmt.Errorf("获取库 %s 系列失败: %w", lib.Name, err)
		}
		ids := make([]string, 0, len(list))
		for i := range list {
			s := list[i]
			s.LibraryID = lib.ID
			newSeries[s.ID] = &s
			ids = append(ids, s.ID)
		}
		newByLib[lib.ID] = ids
	}

	c.mu.Lock()
	oldIDs := make(map[string]bool, len(c.series))
	for id := range c.series {
		oldIDs[id] = true
	}
	changed := map[string]*komga.SeriesDto{}
	for id, ns := range newSeries {
		old, ok := c.series[id]
		if !ok || dtoChanged(old, ns) {
			changed[id] = ns
		}
		delete(oldIDs, id)
	}
	removed := make([]string, 0, len(oldIDs))
	for id := range oldIDs {
		removed = append(removed, id)
		delete(c.series, id)
	}
	for id, ns := range newSeries {
		c.series[id] = ns
	}
	c.libs = libs
	c.byLib = newByLib
	for id := range c.byLib {
		sortIDs(c.byLib[id], newSeries)
	}
	c.lastSync = time.Now()
	c.mu.Unlock()

	// 落库（差量）
	if err := c.persist(libs, changed, removed); err != nil && c.log != nil {
		c.log.Warn("库缓存写入 SQLite 失败: " + err.Error())
	}
	if c.log != nil {
		c.log.Info(fmt.Sprintf("库缓存已对齐：%d 库 %d 系列（新增/变更 %d，移除 %d）",
			len(libs), len(newSeries), len(changed), len(removed)))
	}
	return nil
}

// RefreshSeries 单系列回源刷新（元数据/封面变更后调用）。
func (c *Cache) RefreshSeries(id string) {
	client := c.client()
	s, err := client.GetSeries(id)
	if err != nil {
		return // 拉不到保持旧值（可能是暂时性网络错误）
	}
	c.mu.Lock()
	c.series[id] = s
	// 库归属可能变化（极罕见）：重建该库索引
	libID := s.LibraryID
	if _, ok := c.byLib[libID]; ok && !containsID(c.byLib[libID], id) {
		c.byLib[libID] = append(c.byLib[libID], id)
		sortIDs(c.byLib[libID], c.series)
	}
	c.mu.Unlock()
	_ = c.persistSeries(map[string]*komga.SeriesDto{id: s})
}

// InvalidateSeries 异步单系列回源（写路径钩子调用，不阻塞请求）。
func (c *Cache) InvalidateSeries(id string) {
	if id == "" {
		return
	}
	go func() {
		select {
		case <-c.stop:
			return
		default:
		}
		c.RefreshSeries(id)
	}()
}

// Reset 清空缓存（Komga 连接设置变更时调用，避免串库）。
func (c *Cache) Reset() {
	c.mu.Lock()
	c.libs = nil
	c.series = map[string]*komga.SeriesDto{}
	c.byLib = map[string][]string{}
	c.lastSync = time.Time{}
	c.mu.Unlock()
	if c.db != nil {
		_, _ = c.db.Exec(`DELETE FROM cache_series`)
		_, _ = c.db.Exec(`DELETE FROM cache_libraries`)
	}
	if c.log != nil {
		c.log.Info("库缓存已清空（Komga 连接设置变更）")
	}
}

// ---------- 持久化 ----------

func (c *Cache) load() error {
	if c.db == nil {
		return nil
	}
	rows, err := c.db.Query(`SELECT id, data FROM cache_libraries`)
	if err != nil {
		return err
	}
	var libs []komga.LibraryDto
	for rows.Next() {
		var id, data string
		if err := rows.Scan(&id, &data); err != nil {
			rows.Close()
			return err
		}
		var l komga.LibraryDto
		if json.Unmarshal([]byte(data), &l) == nil {
			libs = append(libs, l)
		}
	}
	rows.Close()

	rows2, err := c.db.Query(`SELECT data FROM cache_series`)
	if err != nil {
		return err
	}
	defer rows2.Close()
	series := map[string]*komga.SeriesDto{}
	byLib := map[string][]string{}
	for rows2.Next() {
		var data string
		if err := rows2.Scan(&data); err != nil {
			return err
		}
		var s komga.SeriesDto
		if json.Unmarshal([]byte(data), &s) != nil || s.ID == "" {
			continue
		}
		series[s.ID] = &s
		byLib[s.LibraryID] = append(byLib[s.LibraryID], s.ID)
	}
	for id := range byLib {
		sortIDs(byLib[id], series)
	}

	c.mu.Lock()
	c.libs = libs
	c.series = series
	c.byLib = byLib
	// lastSync 以库数据是否恢复成功为准（重启后视为已同步，间隔从下次循环起算）
	if len(series) > 0 {
		c.lastSync = time.Now()
	}
	c.mu.Unlock()
	if c.log != nil && len(series) > 0 {
		c.log.Info(fmt.Sprintf("库缓存已从 SQLite 恢复：%d 库 %d 系列", len(libs), len(series)))
	}
	return nil
}

func (c *Cache) persist(libs []komga.LibraryDto, changed map[string]*komga.SeriesDto, removed []string) error {
	if c.db == nil {
		return nil
	}
	tx, err := c.db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err := tx.Exec(`DELETE FROM cache_libraries`); err != nil {
		return err
	}
	for _, l := range libs {
		b, err := json.Marshal(l)
		if err != nil {
			return err
		}
		if _, err := tx.Exec(`INSERT INTO cache_libraries (id, data) VALUES (?,?)
			ON CONFLICT(id) DO UPDATE SET data=excluded.data`, l.ID, string(b)); err != nil {
			return err
		}
	}
	for id, s := range changed {
		b, err := json.Marshal(s)
		if err != nil {
			return err
		}
		if _, err := tx.Exec(`INSERT INTO cache_series (series_id, library_id, data) VALUES (?,?,?)
			ON CONFLICT(series_id) DO UPDATE SET library_id=excluded.library_id, data=excluded.data`,
			id, s.LibraryID, string(b)); err != nil {
			return err
		}
	}
	for _, id := range removed {
		if _, err := tx.Exec(`DELETE FROM cache_series WHERE series_id=?`, id); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func (c *Cache) persistSeries(changed map[string]*komga.SeriesDto) error {
	if c.db == nil {
		return nil
	}
	tx, err := c.db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()
	for id, s := range changed {
		b, err := json.Marshal(s)
		if err != nil {
			return err
		}
		if _, err := tx.Exec(`INSERT INTO cache_series (series_id, library_id, data) VALUES (?,?,?)
			ON CONFLICT(series_id) DO UPDATE SET library_id=excluded.library_id, data=excluded.data`,
			id, s.LibraryID, string(b)); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// ---------- 工具 ----------

// dtoChanged 差量判断：DTO JSON 是否有实际变化。
func dtoChanged(a, b *komga.SeriesDto) bool {
	ab, _ := json.Marshal(a)
	bb, _ := json.Marshal(b)
	return string(ab) != string(bb)
}

// sortIDs 按系列名升序（内存与 Komga 默认序对齐，UI 顺序稳定）。
func sortIDs(ids []string, series map[string]*komga.SeriesDto) {
	sort.Slice(ids, func(i, j int) bool {
		a, b := series[ids[i]], series[ids[j]]
		an, bn := "", ""
		if a != nil {
			an = a.Name
		}
		if b != nil {
			bn = b.Name
		}
		return strings.ToLower(an) < strings.ToLower(bn)
	})
}

func containsID(ids []string, id string) bool {
	for _, v := range ids {
		if v == id {
			return true
		}
	}
	return false
}
