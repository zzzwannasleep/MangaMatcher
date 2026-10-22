package sources

import (
	"encoding/json"
	"testing"

	"komgabangumi/internal/text"
)

// 短中文标题的反向包含闸门（实测一次 1217 系列任务里 66/180 条失败卡在 >=6 这个门槛上）。

func TestFindPreciseMatchShortChineseTitle(t *testing.T) {
	cases := []struct {
		term    string
		cand    Candidate
		wantHit bool
	}{
		// 4 字中文前缀 → 命中（以前 >=6 直接跳过反向包含）
		{"家庭教师", Candidate{Title: "家庭教師ヒットマンREBORN!"}, true},
		// 书名号归一化后完全相等
		{"我推的孩子", Candidate{Title: "【我推的孩子】"}, true},
		// 3 字前缀
		{"迷宫干", Candidate{Title: "迷宫干 日后谈"}, true},
		// 完全相同
		{"天驱", Candidate{Title: "天驱"}, true},
		// 繁简
		{"铳梦", Candidate{Title: "銃夢"}, true},
		{"LIFE", Candidate{Title: "LIFE 完全版"}, true},
		// 前缀约束：候选名不是以搜索词开头 → 不认（防张冠李戴）
		{"LIFE", Candidate{Title: "HALF LIFE"}, false},
		// 名字本来就不同 → 不认
		{"赌博启示录", Candidate{Title: "賭博黙示録カイジ"}, false},
	}
	for _, c := range cases {
		got := FindPreciseMatch([]Candidate{c.cand}, text.ExtractAndNormalizeTitle(c.term))
		if (got != nil) != c.wantHit {
			t.Errorf("FindPreciseMatch(%q, %q) 命中=%v；期望 %v", c.term, c.cand.Title, got != nil, c.wantHit)
		}
	}
}

func TestFindPreciseMatchPicksShortestPrefix(t *testing.T) {
	list := []Candidate{
		{Title: "铳梦 完全版 附录设定集"}, // 更长
		{Title: "铳梦 完全版"},       // 更短 → 应选中它
		{Title: "其它作品"},
	}
	got := FindPreciseMatch(list, text.ExtractAndNormalizeTitle("铳梦"))
	if got == nil || got.Title != "铳梦 完全版" {
		t.Fatalf("应选最短的前缀候选，实际 %+v", got)
	}
}

// 百度百科 id / newLemmaId 有时回字符串（实测报错 json: cannot unmarshal string into ... int64）。

func TestBaikeFlexInt64(t *testing.T) {
	for _, js := range []string{
		`{"id":123,"newLemmaId":456,"title":"铳梦"}`,
		`{"id":"123","newLemmaId":"456","title":"铳梦"}`,
		`{"id":"","newLemmaId":"","title":"铳梦"}`,
		`{"newLemmaId":null,"title":"铳梦"}`,
		`{"newLemmaId":{"a":1},"title":"铳梦"}`,
	} {
		var r baikeResp
		if err := json.Unmarshal([]byte(js), &r); err != nil {
			t.Errorf("解析 %s 失败：%v", js, err)
			continue
		}
		if r.Title != "铳梦" {
			t.Errorf("解析 %s 后 title = %q", js, r.Title)
		}
	}
	var r baikeResp
	if err := json.Unmarshal([]byte(`{"newLemmaId":"9527"}`), &r); err != nil {
		t.Fatalf("字符串 id 解析失败：%v", err)
	}
	if int64(r.NewLemmaID) != 9527 {
		t.Errorf("字符串 newLemmaId 应解析成 9527，实际 %d", r.NewLemmaID)
	}
}
