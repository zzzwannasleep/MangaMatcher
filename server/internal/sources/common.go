package sources

import (
	"fmt"
	"sort"
	"strings"

	"komgabangumi/internal/text"
)

// Candidate 归一化跨源候选（候选面板 / 匹配链共用）。
type Candidate struct {
	BgmID    int    // Bangumi subject id（其它源为 0）
	Title    string // 中文/通行名优先
	OriTitle string // 原名
	Author   string
	Aliases  string // " / " 连接
	CoverURL string
	Source   string // Bgm|Bw|Mox|MangaDex|Baike
	BwID     string // BookWalker 内部 id
	MoxID    string // bookof.moe 内部 id
	MdID     string // MangaDex uuid
	BaikeID  string // 百度百科 lemma id（字符串化的数字）
	EhID     string // E-Hentai 内部 id（"gid/token"）
}

// CandidateNames 候选名（标题/原名/别名）。
func CandidateNames(c Candidate) []string {
	var names []string
	if c.Title != "" {
		names = append(names, c.Title)
	}
	if c.OriTitle != "" {
		names = append(names, c.OriTitle)
	}
	if c.Aliases != "" {
		for _, a := range strings.Split(c.Aliases, " / ") {
			if a != "" {
				names = append(names, a)
			}
		}
	}
	return names
}

// FindPreciseMatch 在候选列表里找精确匹配（规范名相等 / 包含+残留垃圾词检查，宁缺毋滥）。
// 全链路共用这一份标准（对齐 BatchMatcher.FindPreciseMatch）。
func FindPreciseMatch(list []Candidate, searchNorm string) *Candidate {
	for i := range list {
		for _, cand := range CandidateNames(list[i]) {
			if text.ExtractAndNormalizeTitle(cand) == searchNorm {
				return &list[i]
			}
		}
	}
	// 搜索词是「苍蓝钢铁战舰、苍蓝钢铁的琶音」这种合并串时，命中的规范名被包含即可；
	// 残留检查（宁缺毋滥）：多出来的部分含拉丁字母/水印/汉化等垃圾词 → 拒绝。
	if len([]rune(searchNorm)) >= 4 {
		for i := range list {
			for _, cand := range CandidateNames(list[i]) {
				n := text.ExtractAndNormalizeTitle(cand)
				nr := []rune(n)
				if len(nr) < 4 || !strings.Contains(searchNorm, n) {
					continue
				}
				idx := strings.Index(searchNorm, n)
				residue := searchNorm[:idx] + searchNorm[idx+len(n):]
				if text.ResidueLooksLikeJunk(residue) {
					continue
				}
				return &list[i]
			}
		}
	}
	// 反向包含：候选名比搜索词长（带副标题/版本名/英文原名），如词 "家庭教师" ↔ 候选
	// "家庭教師ヒットマンREBORN!"（归一化后 "家庭教师ヒットマンreborn"）。
	// ① 前缀匹配：门槛以前是 6，中文 2~5 字的完整标题永远走不到这里
	//    （实测一次任务 66/180 条失败卡在这：家庭教师/我推的孩子/赌博启示录/迷宫干/LIFE…）。
	//    放宽的同时加两道约束防张冠李戴：候选名必须以搜索词「开头」，且取最短的那个候选。
	if len([]rune(searchNorm)) >= 3 {
		var best *Candidate
		bestLen := -1
		for i := range list {
			for _, cand := range CandidateNames(list[i]) {
				n := text.ExtractAndNormalizeTitle(cand)
				nr := []rune(n)
				if len(nr) <= len([]rune(searchNorm)) || !strings.HasPrefix(n, searchNorm) {
					continue
				}
				if best == nil || len(nr) < bestLen {
					best = &list[i]
					bestLen = len(nr)
				}
			}
		}
		if best != nil {
			return best
		}
	}
	// ② 原来的「包含即可」（不要求开头）只在 >=6 时保留，避免放宽后把短词配到不相干的候选上。
	if len([]rune(searchNorm)) >= 6 {
		for i := range list {
			for _, cand := range CandidateNames(list[i]) {
				n := text.ExtractAndNormalizeTitle(cand)
				if len([]rune(n)) > len([]rune(searchNorm)) && strings.Contains(n, searchNorm) {
					return &list[i]
				}
			}
		}
	}
	return nil
}

// RankCandidates 候选按与搜索词的相似度（bigram Jaccard）排序后截断（候选>12 截断再给 AI）。
func RankCandidates(list []Candidate, term string, take int) []Candidate {
	if take <= 0 {
		take = 12
	}
	normTerm := text.ExtractAndNormalizeTitle(term)
	termGrams := bigrams(normTerm)
	type scored struct {
		c     Candidate
		score float64
		idx   int
	}
	var out []scored
	for i, c := range list {
		best := 0.0
		for _, name := range CandidateNames(c) {
			grams := bigrams(text.ExtractAndNormalizeTitle(name))
			if len(grams) == 0 {
				continue
			}
			inter := 0
			for g := range grams {
				if termGrams[g] {
					inter++
				}
			}
			jac := float64(inter) / float64(len(grams)+len(termGrams)-inter)
			if jac > best {
				best = jac
			}
		}
		out = append(out, scored{c, best, i})
	}
	sort.SliceStable(out, func(i, k int) bool {
		if out[i].score != out[k].score {
			return out[i].score > out[k].score
		}
		return out[i].idx < out[k].idx
	})
	res := make([]Candidate, 0, take)
	for _, s := range out {
		if len(res) >= take {
			break
		}
		res = append(res, s.c)
	}
	return res
}

func bigrams(s string) map[string]bool {
	set := map[string]bool{}
	r := []rune(s)
	if len(r) == 1 {
		set[s] = true
	}
	for i := 0; i+1 < len(r); i++ {
		set[string(r[i:i+2])] = true
	}
	return set
}

// FirstNonEmpty 第一个非空字符串。
func FirstNonEmpty(values ...string) string {
	for _, v := range values {
		if strings.TrimSpace(v) != "" {
			return v
		}
	}
	return ""
}

// LooksKoreanMox 疑似韩漫判定（候选标题层面）。
func LooksKoreanMox(title string) bool {
	return text.ContainsHangul(title)
}

var _ = fmt.Sprintf
