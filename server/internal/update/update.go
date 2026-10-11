// Package update：自动更新检查。
//
// Docker 版：容器不可原地替换，语义是「检查新版本 → 提示 → docker compose pull && up -d
// （或启用 Watchtower 全自动）」。对比基准：GitHub 仓库 tags 里最新的 docker-vX.Y.Z，
// 与镜像构建时注入的 VERSION 比较。
package update

import (
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Status 一次检查的结果。
type Status struct {
	Current    string    `json:"current"`
	Latest     string    `json:"latest"`
	HasUpdate  bool      `json:"hasUpdate"`
	ReleaseURL string    `json:"releaseUrl"`
	CheckedAt  time.Time `json:"checkedAt"`
	Error      string    `json:"error,omitempty"`
}

var (
	tagVerRe = regexp.MustCompile(`^docker-v(\d+)\.(\d+)\.(\d+)$`)
	curRe    = regexp.MustCompile(`^v?(\d+)\.(\d+)\.(\d+)`)
	currentVer = "dev"
)

// SetBuildVersion 注入构建版本（main.go 从 ldflags 传入；Docker 镜像为 0.0.N 或 docker-vX.Y.Z）。
func SetBuildVersion(v string) {
	if v != "" {
		currentVer = v
	}
}

// Current 构建版本。
func Current() string { return currentVer }

// semver 可比较版本；不可解析 = false（开发构建不参与对比）。
func semver(s string) (int, int, int, bool) {
	m := curRe.FindStringSubmatch(s)
	if m == nil {
		return 0, 0, 0, false
	}
	a, _ := strconv.Atoi(m[1])
	b, _ := strconv.Atoi(m[2])
	c, _ := strconv.Atoi(m[3])
	return a, b, c, true
}

// Checker 周期检查器。
type Checker struct {
	mu       sync.Mutex
	last     *Status
	checking bool
	repoAPI  string
	client   *http.Client
}

// NewChecker 创建（repoAPI 默认仓库 tags API；测试注入 httptest URL）。
// 环境变量 GITHUB_TAGS_API 可覆盖基址：本地用 tools/mock-github.js 离线自测更新链路。
// 传完整 API URL 或裸 base 均可（裸 base 自动补 /repos/... 路径）。
func NewChecker(repoAPI string, client *http.Client) *Checker {
	if repoAPI == "" {
		repoAPI = os.Getenv("GITHUB_TAGS_API")
	}
	if repoAPI != "" && !strings.Contains(repoAPI, "/repos/") {
		repoAPI = strings.TrimRight(repoAPI, "/") + "/repos/zzzwannasleep/MangaMatcher/tags?per_page=100"
	}
	if repoAPI == "" {
		repoAPI = "https://api.github.com/repos/zzzwannasleep/MangaMatcher/tags?per_page=100"
	}
	if client == nil {
		client = &http.Client{Timeout: 20 * time.Second}
	}
	return &Checker{repoAPI: repoAPI, client: client}
}

// Last 最近一次检查结果（未检查过 = nil）。
func (c *Checker) Last() *Status {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.last
}

// Checking 是否正在检查。
func (c *Checker) Checking() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.checking
}

// SetClient 更换 HTTP 客户端（代理设置变更时调用；nil = 默认直连）。
func (c *Checker) SetClient(client *http.Client) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if client != nil {
		c.client = client
	}
}

// Check 立即检查（网络调用）：仓库 tags 里找最新的 docker-vX.Y.Z。
func (c *Checker) Check() (*Status, error) {
	c.mu.Lock()
	if c.checking {
		last := c.last
		c.mu.Unlock()
		return last, nil
	}
	c.checking = true
	c.mu.Unlock()
	defer func() {
		c.mu.Lock()
		c.checking = false
		c.mu.Unlock()
	}()

	st, err := c.fetch()
	now := time.Now()
	c.mu.Lock()
	if st != nil {
		st.CheckedAt = now
		c.last = st
	}
	c.mu.Unlock()
	return st, err
}

func (c *Checker) fetch() (*Status, error) {
	cur := currentVer
	curA, curB, curC, curOK := semver(cur)
	req, err := http.NewRequest(http.MethodGet, c.repoAPI, nil)
	if err != nil {
		return &Status{Current: cur, Error: err.Error()}, err
	}
	req.Header.Set("Accept", "application/vnd.github+json")
	req.Header.Set("User-Agent", "KomgaBangumi-Docker")
	resp, err := c.client.Do(req)
	if err != nil {
		st := &Status{Current: cur, Error: "检查更新失败: " + err.Error()}
		return st, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		st := &Status{Current: cur, Error: fmt.Sprintf("GitHub API %d", resp.StatusCode)}
		return st, fmt.Errorf("%s", st.Error)
	}
	var tags []struct {
		Name string `json:"name"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&tags); err != nil {
		st := &Status{Current: cur, Error: "解析 GitHub 响应失败: " + err.Error()}
		return st, err
	}
	best := ""
	bestA, bestB, bestC := 0, 0, 0
	for _, t := range tags {
		m := tagVerRe.FindStringSubmatch(t.Name)
		if m == nil {
			continue
		}
		a, _ := strconv.Atoi(m[1])
		b, _ := strconv.Atoi(m[2])
		d, _ := strconv.Atoi(m[3])
		if best == "" || a > bestA || (a == bestA && (b > bestB || (b == bestB && d > bestC))) {
			best, bestA, bestB, bestC = m[1]+"."+m[2]+"."+m[3], a, b, d
		}
	}
	if best == "" {
		st := &Status{Current: cur, Error: "仓库还没有 docker-v* 版本标签"}
		return st, fmt.Errorf("%s", st.Error)
	}
	// 当前版本不可解析（开发构建/sha 构建）→ 不提示更新（避免误导）
	has := curOK && (bestA > curA || (bestA == curA && bestB > curB) || (bestA == curA && bestB == curB && bestC > curC))
	return &Status{
		Current:    cur,
		Latest:     best,
		HasUpdate:  has,
		ReleaseURL: "https://github.com/zzzwannasleep/MangaMatcher/pkgs/container/mangamatcher",
	}, nil
}
