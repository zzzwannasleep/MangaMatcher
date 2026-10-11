package scrape

import (
	"log/slog"
	"strconv"
	"sync"

	"komgabangumi/internal/komga"
)

// ManualMatchCollection 「待手动匹配」收藏夹管理（对齐 ManualMatchCollection.cs）。
// 并行批量刮削时多个系列会同时增删成员，内部用互斥锁把操作串行化，成员快照始终一致。
type ManualMatchCollection struct {
	Name string // 收藏夹名（SPEC：待手动匹配，可配置）

	komga     *komga.Client
	log       *slog.Logger
	mu        sync.Mutex
	colID     string
	memberIDs []string
}

// NewManualMatchCollection 创建管理器。
func NewManualMatchCollection(k *komga.Client, name string, log *slog.Logger) *ManualMatchCollection {
	if name == "" {
		name = "待手动匹配"
	}
	return &ManualMatchCollection{komga: k, Name: name, log: log}
}

// EnsureExists 确保收藏夹存在并缓存其成员；initialSeriesID 用于首次创建。
func (m *ManualMatchCollection) EnsureExists(initialSeriesID string) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.ensureExistsLocked(initialSeriesID)
}

func (m *ManualMatchCollection) ensureExistsLocked(initialSeriesID string) bool {
	if m.colID != "" {
		return true
	}
	collections, err := m.komga.GetCollections()
	if err != nil {
		m.log.Error("[收藏夹] 操作 " + m.Name + " 失败: " + err.Error())
		m.colID = ""
		m.memberIDs = nil
		return false
	}
	for _, c := range collections {
		if c.Name == m.Name {
			m.colID = c.ID
			m.memberIDs = c.SeriesIds
			m.log.Info("[收藏夹] 已找到 \"" + m.Name + "\" (ID:" + m.colID + ")，包含 " + strconv.Itoa(len(m.memberIDs)) + " 个系列")
			return true
		}
	}
	if initialSeriesID == "" {
		m.log.Info("[收藏夹] \"" + m.Name + "\" 不存在，将等失败系列出现时创建")
		return false
	}
	created, err := m.komga.CreateCollection(m.Name, []string{initialSeriesID}, false)
	if err != nil {
		m.log.Error("[收藏夹] 创建 \"" + m.Name + "\" 失败: " + err.Error())
		return false
	}
	m.colID = created.ID
	m.memberIDs = created.SeriesIds
	if len(m.memberIDs) == 0 {
		m.memberIDs = []string{initialSeriesID}
	}
	m.log.Info("[收藏夹] 已使用系列 " + initialSeriesID + " 创建 \"" + m.Name + "\"")
	return true
}

// AddSeries 失败系列加入收藏夹。
func (m *ManualMatchCollection) AddSeries(seriesID, seriesName string) bool {
	if seriesID == "" {
		return false
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	ready := m.colID != "" || m.ensureExistsLocked(seriesID)
	if !ready || m.colID == "" {
		m.log.Error("[收藏夹] \"" + m.Name + "\" 未就绪，无法添加《" + seriesName + "》")
		return false
	}
	for _, id := range m.memberIDs {
		if id == seriesID {
			return true
		}
	}
	newList := append(append([]string{}, m.memberIDs...), seriesID)
	if err := m.komga.UpdateCollectionSeries(m.colID, newList); err != nil {
		m.log.Error("[收藏夹] 添加《" + seriesName + "》失败: " + err.Error())
		return false
	}
	m.memberIDs = newList
	m.log.Info("[收藏夹] 《" + seriesName + "》已添加至 \"" + m.Name + "\"")
	return true
}

// RemoveSeries 匹配成功后从收藏夹移除。
func (m *ManualMatchCollection) RemoveSeries(seriesID, seriesName string) bool {
	if seriesID == "" || m.colID == "" {
		return false
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	idx := -1
	for i, id := range m.memberIDs {
		if id == seriesID {
			idx = i
			break
		}
	}
	if idx < 0 {
		return false
	}
	newList := append(append([]string{}, m.memberIDs[:idx]...), m.memberIDs[idx+1:]...)
	if err := m.komga.UpdateCollectionSeries(m.colID, newList); err != nil {
		m.log.Error("[收藏夹] 从收藏夹移除《" + seriesName + "》失败: " + err.Error())
		return false
	}
	m.memberIDs = newList
	m.log.Info("[收藏夹] 《" + seriesName + "》已从 \"" + m.Name + "\" 移除（匹配成功）")
	return true
}
