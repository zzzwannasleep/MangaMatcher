package update

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

// tagsServer 起一个返回指定 tag 列表的假 GitHub tags API。
func tagsServer(t *testing.T, tags ...string) *httptest.Server {
	t.Helper()
	type tag struct {
		Name string `json:"name"`
	}
	names := make([]tag, 0, len(tags))
	for _, n := range tags {
		names = append(names, tag{Name: n})
	}
	body, _ := json.Marshal(names)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(body)
	}))
	t.Cleanup(srv.Close)
	return srv
}

func TestFetch_HasUpdate(t *testing.T) {
	srv := tagsServer(t, "v1.2.3", "docker-v0.2.0", "docker-v0.1.9", "not-a-tag")
	c := NewChecker(srv.URL, srv.Client())
	currentVer = "0.1.5"
	st, err := c.Check()
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if !st.HasUpdate {
		t.Fatalf("希望提示更新，got %+v", st)
	}
	if st.Latest != "0.2.0" {
		t.Fatalf("Latest = %q, want 0.2.0", st.Latest)
	}
	if st.Current != "0.1.5" {
		t.Fatalf("Current = %q", st.Current)
	}
	if st.CheckedAt.IsZero() {
		t.Fatal("CheckedAt 未记录")
	}
}

func TestFetch_UpToDate(t *testing.T) {
	srv := tagsServer(t, "docker-v1.2.3", "docker-v1.2.2")
	c := NewChecker(srv.URL, srv.Client())
	currentVer = "v1.2.3"
	st, err := c.Check()
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if st.HasUpdate {
		t.Fatalf("当前已是最新，不应提示更新: %+v", st)
	}
	if st.Latest != "1.2.3" {
		t.Fatalf("Latest = %q, want 1.2.3", st.Latest)
	}
}

func TestFetch_DevVersionNoPrompt(t *testing.T) {
	srv := tagsServer(t, "docker-v1.2.3")
	c := NewChecker(srv.URL, srv.Client())
	currentVer = "dev"
	st, err := c.Check()
	if err != nil {
		t.Fatalf("Check: %v", err)
	}
	if st.HasUpdate {
		t.Fatal("开发构建不应提示更新")
	}
}

func TestFetch_NoTags(t *testing.T) {
	srv := tagsServer(t, "v1.0.0", "release-abc")
	c := NewChecker(srv.URL, srv.Client())
	currentVer = "0.1.0"
	st, err := c.Check()
	if err == nil {
		t.Fatal("没有 docker-v* tag 应报错")
	}
	if st == nil || st.Error == "" {
		t.Fatalf("错误信息应写入 Status: %+v", st)
	}
}

func TestFetch_HTTPError(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusForbidden)
	}))
	defer srv.Close()
	c := NewChecker(srv.URL, srv.Client())
	st, err := c.Check()
	if err == nil {
		t.Fatal("HTTP 403 应报错")
	}
	if st == nil || st.Error != "GitHub API 403" {
		t.Fatalf("Status.Error = %q", st.Error)
	}
}

func TestCheck_ConcurrentAndCache(t *testing.T) {
	srv := tagsServer(t, "docker-v0.2.0")
	c := NewChecker(srv.URL, srv.Client())
	currentVer = "0.1.0"
	// 并发多次 Check 不崩，且都拿到结果（checking 互斥 + last 缓存兜底）
	for i := 0; i < 5; i++ {
		go func() { _, _ = c.Check() }()
	}
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if st := c.Last(); st != nil && st.Latest == "0.2.0" {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("5 次并发检查后 Last() 仍没有有效结果")
}

func TestSemver(t *testing.T) {
	cases := []struct {
		in   string
		a, b, c int
		ok   bool
	}{
		{"0.2.0", 0, 2, 0, true},
		{"v1.10.3", 1, 10, 3, true},
		{"0.0.123-x", 0, 0, 123, true},
		{"dev", 0, 0, 0, false},
		{"", 0, 0, 0, false},
	}
	for _, tc := range cases {
		a, b, c, ok := semver(tc.in)
		if a != tc.a || b != tc.b || c != tc.c || ok != tc.ok {
			t.Errorf("semver(%q) = %d,%d,%d,%v want %d,%d,%d,%v", tc.in, a, b, c, ok, tc.a, tc.b, tc.c, tc.ok)
		}
	}
}
