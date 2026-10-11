package scrape

import (
	"strings"

	"komgabangumi/internal/bangumi"
	"komgabangumi/internal/text"
)

// Matcher：批量精确匹配（移植自 BatchMatcher.cs 的 PreciseMatchAsync / FindPreciseMatch）。
// 标题/原名/别名精确匹配，宁缺毋滥；无匹配返回错误，由调用方把系列送进"待手动匹配"收藏夹。

// candidateNames 候选名（标题/原名/别名）。
func candidateNames(c bangumi.SearchCandidate) []string {
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

// FindPreciseMatch 在搜索结果里找精确匹配（标题/原名/别名完全相同，或包含+残留检查）。
func FindPreciseMatch(list []bangumi.SearchCandidate, searchNorm string) *bangumi.SearchCandidate {
	for i := range list {
		for _, cand := range candidateNames(list[i]) {
			if text.ExtractAndNormalizeTitle(cand) == searchNorm {
				return &list[i]
			}
		}
	}

	// 搜索词是「苍蓝钢铁战舰、苍蓝钢铁的琶音」这种合并串时，命中的规范名被包含即可。
	// 残留检查（宁缺毋滥）：多出来的部分含拉丁字母/水印/汉化等垃圾词 → 拒绝。
	if len([]rune(searchNorm)) >= 4 {
		for i := range list {
			for _, cand := range candidateNames(list[i]) {
				n := text.ExtractAndNormalizeTitle(cand)
				if len([]rune(n)) < 4 || !strings.Contains(searchNorm, n) {
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

	// 反向包含：候选名比搜索词长（带副标题/版本名）。
	if len([]rune(searchNorm)) >= 6 {
		for i := range list {
			for _, cand := range candidateNames(list[i]) {
				n := text.ExtractAndNormalizeTitle(cand)
				if len([]rune(n)) > len([]rune(searchNorm)) && strings.Contains(n, searchNorm) {
					return &list[i]
				}
			}
		}
	}
	return nil
}

// MatchSeries 对单系列执行精确匹配并返回 subject id（ Bangumi 主链 v1）。
// 与桌面版差异：v1 无 BookWalker/AI/Mox/MangaDex/百科回退（留桩），也不跳过已刮系列。
func MatchSeries(
	search func(keyword string) ([]bangumi.SearchCandidate, error),
	seriesName string,
) (*bangumi.SearchCandidate, string, error) {
	terms := text.ExtractSeriesTitles(seriesName, 4)
	if len(terms) == 0 || terms[0] == "" {
		return nil, "", errOf("无法从标题解析搜索词")
	}
	searchTerm := terms[0]
	var searchResults []bangumi.SearchCandidate
	for _, term := range terms {
		r, err := search(term)
		if err != nil {
			// 单词搜索失败不终止，继续下一个候选词
			continue
		}
		for _, it := range r {
			dup := false
			for _, x := range searchResults {
				if x.ID == it.ID {
					dup = true
					break
				}
			}
			if !dup {
				searchResults = append(searchResults, it)
			}
		}
		if precise := FindPreciseMatch(r, text.ExtractAndNormalizeTitle(term)); precise != nil {
			return precise, term, nil
		}
	}
	if len(searchResults) > 0 {
		return nil, searchTerm, errOf("无精确匹配（标题/原名/别名）")
	}
	return nil, searchTerm, errOf("搜索无结果")
}
