package text

import (
	"regexp"
	"strconv"
	"strings"
)

// TitleParser：标题/卷号/日期解析，移植自 TitleParser.cs
// （extractSeriesTitles / extractAndNormalizeTitle / extractVolumeNumber / normalizeDate / capitalize）。

var (
	reVolNum        = regexp.MustCompile(`(?i)vol(?:ume)?[._\s]*v?(\d{1,4})(?:完|end)?$`)
	reParenNum      = regexp.MustCompile(`(?i)[（(](\d{1,4})[）)]\s*(?:完)?$`)
	reTrailingNum   = regexp.MustCompile(`\s(\d{1,4})$`)
	reChineseVol    = regexp.MustCompile(`(?i)第\s*(\d{1,4})\s*[卷册]\s*(?:完)?$`)
	reVPrefixNum    = regexp.MustCompile(`(?i)(?:^|[^\w/])v[._\s]?(\d{1,4})$`)
	reParenAnywhere = regexp.MustCompile(`(?i)[（(](\d{1,4})[）)]`)
	reVolAnywhere   = regexp.MustCompile(`(?i)vol(?:ume)?[._\s]*v?(\d{1,4})`)
	reCnVolAnywhere = regexp.MustCompile(`(?i)第\s*(\d{1,4})\s*[卷册]`)

	// VolumeTitleRegex 书名卷号模式（用于批量匹配中的卷识别）。
	VolumeTitleRegex = regexp.MustCompile(`(?i)(?:vol(?:ume)?s?|巻|卷|册|冊)[\W_]*?(?P<volNum>\d+)|第\s*(?P<volNum2>\d+)\s*(?:巻|卷|册|冊|集)|(?P<volNum3>\d+)\s*(?:巻|卷|册|冊|集)`)

	reCopySuffixCn  = regexp.MustCompile(`\s*[-_－—]*\s*副本(?:\s*[（(]\d{1,3}[)）])?$`)
	reCopySuffixNum = regexp.MustCompile(`\s*[（(]\d{1,3}[)）]$`)

	rePunctNorm  = regexp.MustCompile(`[:：•·․,，。、'’?？!！~⁓～]`)
	rePunctClean = regexp.MustCompile(`[:：•·․,，。'’?？!！~⁓～]`)

	reDateCnFull = regexp.MustCompile(`^(\d{4})年(\d{1,2})月(\d{1,2})日$`)
	reDateCnMon  = regexp.MustCompile(`^(\d{4})年(\d{1,2})月$`)
	reDateIso    = regexp.MustCompile(`^(\d{4})-(\d{1,2})-(\d{1,2})$`)
	reDateIsoMon = regexp.MustCompile(`^(\d{4})-(\d{1,2})$`)
	reCapitalize = regexp.MustCompile(`\b[a-z]`)

	reMinJingWai = regexp.MustCompile(`[\(\[【（]?境外版[\)\]】）]?\s*`)
	reMinDan     = regexp.MustCompile(`[\(\[【（]?单行本[\)\]】）]?\s*$`)
	// reMinJuan 兼容「[全3卷]」「（全12卷）」「N卷」尾巴（上游 C# 会把 "[全3卷]" 剥成 "[全" 留下孤字"全"——已修）
	reMinJuan = regexp.MustCompile(`[\(\[【（]?(?:全)?\d+卷[\)\]】）]?\s*$`)
	reMinBracket = regexp.MustCompile(`\[[^\[\]]*\]`)
	reMinHei     = regexp.MustCompile(`【.*?】`)
	reMinParen   = regexp.MustCompile(`[（()）]`)
	reMinTail    = regexp.MustCompile(`[_-]?\s*$`)

	// 纯卷号/卷数区间：25、第12卷、3完、38全、全3卷、全3、Vol.01、Vol.01-Vol.18、1-100完
	reBoilerplateVol = regexp.MustCompile(`(?i)^(?:第)?(?:全)?\d+(?:[-~～]\d+)?[卷话話册集完全]?$|^vol\.?\s*\d+(?:\s*[-~～]\s*(?:vol\.?\s*)?\d+)?$`)
)

// ExtractVolumeNumber 书名/文件名 → 卷号（Vol.x / Vol_x / (x) / （x） / 尾数字 / 第x卷 / v01）。
func ExtractVolumeNumber(name string) (int, bool) {
	if strings.TrimSpace(name) == "" {
		return 0, false
	}
	for _, re := range []*regexp.Regexp{reVolNum, reParenNum, reChineseVol, reVPrefixNum, reTrailingNum} {
		if m := re.FindStringSubmatch(name); m != nil {
			if n, err := strconv.Atoi(m[1]); err == nil {
				return n, true
			}
		}
	}
	// 非行尾兜底：卷号不在结尾（"乱马 Vol.01 [东立]"）→ 取串中最后一次出现的卷号模式。
	// 括号/vol 模式取最后一个匹配；裸数字不兜底（"乱马1/2" 的 2 会被误当卷 2）。
	if ms := reParenAnywhere.FindAllStringSubmatch(name, -1); len(ms) > 0 {
		if n, err := strconv.Atoi(ms[len(ms)-1][1]); err == nil {
			return n, true
		}
	}
	if ms := reVolAnywhere.FindAllStringSubmatch(name, -1); len(ms) > 0 {
		if n, err := strconv.Atoi(ms[len(ms)-1][1]); err == nil {
			return n, true
		}
	}
	if ms := reCnVolAnywhere.FindAllStringSubmatch(name, -1); len(ms) > 0 {
		if n, err := strconv.Atoi(ms[len(ms)-1][1]); err == nil {
			return n, true
		}
	}
	return 0, false
}

// ExtractBookVolumeNumber Komga 书 → 卷号：书名/元数据标题里的卷号模式 → 元数据 number 字段 → 0。
func ExtractBookVolumeNumber(bookName, bookTitle, metaNumber string) int {
	for _, text := range []string{bookName, bookTitle} {
		if strings.TrimSpace(text) == "" {
			continue
		}
		m := VolumeTitleRegex.FindStringSubmatch(text)
		if m == nil {
			// VolumeTitleRegex 抓不到时再用行尾/括号模式（(01) 结尾、乱马 01 等）
			if n, ok := ExtractVolumeNumber(text); ok {
				return n
			}
			continue
		}
		volStr := ""
		for i, name := range VolumeTitleRegex.SubexpNames() {
			if name == "volNum" && m[i] != "" {
				volStr = m[i]
				break
			}
			if name == "volNum2" && m[i] != "" {
				volStr = m[i]
				break
			}
			if name == "volNum3" && m[i] != "" {
				volStr = m[i]
				break
			}
		}
		if n, err := strconv.Atoi(volStr); err == nil && volStr != "" {
			return n
		}
	}
	// ③ Komga 自己解析的 number 字段（"01"、"3.5"→3）：文件名复杂时它通常是对的
	if strings.TrimSpace(metaNumber) != "" {
		intPart := metaNumber
		if dot := strings.IndexByte(metaNumber, '.'); dot > 0 {
			intPart = metaNumber[:dot]
		}
		if n, err := strconv.Atoi(intPart); err == nil {
			return n
		}
	}
	return 0
}

// NormalizeDate 日期归一：YYYY年M月D日 / YYYY年M月 / YYYY-M-D / YYYY-M → YYYY-MM-DD(-01)。
func NormalizeDate(dateStr string) string {
	if strings.TrimSpace(dateStr) == "" {
		return ""
	}
	s := strings.TrimSpace(dateStr)
	if m := reDateCnFull.FindStringSubmatch(s); m != nil {
		return m[1] + "-" + pad2(m[2]) + "-" + pad2(m[3])
	}
	if m := reDateCnMon.FindStringSubmatch(s); m != nil {
		return m[1] + "-" + pad2(m[2]) + "-01"
	}
	if m := reDateIso.FindStringSubmatch(s); m != nil {
		return m[1] + "-" + pad2(m[2]) + "-" + pad2(m[3])
	}
	if m := reDateIsoMon.FindStringSubmatch(s); m != nil {
		return m[1] + "-" + pad2(m[2]) + "-01"
	}
	return ""
}

func pad2(s string) string {
	if len(s) < 2 {
		return "0" + s
	}
	return s
}

// Capitalize 拉丁单词首字母大写。
func Capitalize(str string) string {
	if str == "" {
		return str
	}
	return reCapitalize.ReplaceAllStringFunc(str, func(m string) string {
		return strings.ToUpper(m)
	})
}

// HasCopySuffix 标题是否带复制副本后缀（"标题 (1)" / "标题 - 副本"）。
func HasCopySuffix(title string) bool {
	if strings.TrimSpace(title) == "" {
		return false
	}
	return reCopySuffixCn.MatchString(title) || reCopySuffixNum.MatchString(title)
}

// StripCopySuffix 剥掉标题尾部的复制副本后缀（循环剥，"XXX (1) (2)" 连环复制）。
func StripCopySuffix(title string) string {
	if strings.TrimSpace(title) == "" {
		return title
	}
	s := strings.TrimRight(title, " \t\r\n")
	for {
		prev := s
		s = strings.TrimRight(reCopySuffixCn.ReplaceAllString(s, ""), " \t\r\n")
		s = strings.TrimRight(reCopySuffixNum.ReplaceAllString(s, ""), " \t\r\n")
		if s == prev || len(s) == 0 {
			break
		}
	}
	return s
}

// ExtractAndNormalizeTitle 匹配用规范化：候选词首个 → 繁→简 → 去标点 → 小写。
// 两边（搜索词与候选名）统一过 T2S，繁体候选（碧藍之海）才能命中简体搜索词（碧蓝之海）。
func ExtractAndNormalizeTitle(str string) string {
	candidates := ExtractSeriesTitles(str, 1)
	title := ""
	if len(candidates) > 0 {
		title = candidates[0]
	}
	title = T2S(title)
	// 标点替换成空格后保留（对齐脚本，不显式去空白）
	return strings.ToLower(strings.Trim(rePunctNorm.ReplaceAllString(title, " "), " "))
}

// NormalizeForMatch 轻量规范化（不去空白、不小写），仅用于显示层预处理。
func NormalizeForMatch(title string) string {
	return strings.Trim(rePunctNorm.ReplaceAllString(title, " "), " ")
}

// BoilerplateMarkers 明显是完整度/汉化/扫描/水印/格式等元信息的段。
var BoilerplateMarkers = []string{
	"未完", "完结", "已完", "连载", "汉化", "扫描", "原档", "pdf", "高清",
	"合集", "生肉", "熟肉", "简中", "繁中", "补档", "搬运", "境外版", "单行本",
	"水印", "嵌字", "修图", "扫图", "置换", "缺页",
}

// PublisherTitleMarkers 出版社/平台/汉化组标签（短段包含这些词 → 元信息）。
var PublisherTitleMarkers = []string{
	"东立", "台湾角川", "角川", "青文", "长鸿", "大然", "尖端", "玉皇朝",
	"文化传信", "文传", "文苑", "电子版", "bili", "zzgundam", "汉化组",
}

// IsBoilerplateSegment 是否元信息段（不当搜索词）。
func IsBoilerplateSegment(s string) bool {
	low := strings.ToLower(s)
	for _, m := range BoilerplateMarkers {
		if strings.Contains(low, m) {
			return true
		}
	}
	if len([]rune(s)) <= 8 {
		for _, m := range PublisherTitleMarkers {
			if strings.Contains(low, m) {
				return true
			}
		}
	}
	// 纯卷号/卷数区间：25、第12卷、3完、38全、Vol.01、Vol.01-Vol.18、1-100完
	return reBoilerplateVol.MatchString(s)
}

// ResidueLooksLikeJunk 包含匹配的残留检查（宁缺毋滥）。
func ResidueLooksLikeJunk(residue string) bool {
	if residue == "" {
		return false
	}
	for _, ch := range residue {
		if (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') {
			return true
		}
	}
	low := strings.ToLower(residue)
	for _, m := range BoilerplateMarkers {
		if strings.Contains(low, m) {
			return true
		}
	}
	for _, m := range PublisherTitleMarkers {
		if strings.Contains(low, m) {
			return true
		}
	}
	return false
}

// ExtractSeriesTitles 从系列名提取候选搜索词（移植脚本 extractSeriesTitles +
// 中文压制组命名的方括号段兜底候选），候选统一繁→简 + 标点清洗 + 去重。
func ExtractSeriesTitles(input string, limitCount int) []string {
	if strings.TrimSpace(input) == "" {
		return nil
	}

	parts := ExtractBracketParts(input)

	var title string
	var authors []string

	var authorCandidate string
	hasAuthorCandidate := false
	for _, p := range parts {
		if strings.Contains(p, "×") {
			authorCandidate = p
			hasAuthorCandidate = true
			break
		}
	}
	if hasAuthorCandidate {
		for _, s := range strings.Split(authorCandidate, "×") {
			authors = append(authors, T2S(strings.TrimSpace(s)))
		}
		for _, p := range parts {
			if p != authorCandidate && title == "" {
				title = T2S(strings.TrimSpace(p))
			}
		}
	} else {
		if len(parts) > 0 {
			title = T2S(strings.TrimSpace(parts[0]))
		}
		if len(parts) > 1 {
			authors = []string{T2S(strings.TrimSpace(parts[1]))}
		}
	}

	var titleParts []string
	if title != "" {
		titleParts = expand(title)
	}
	combinedParts := append([]string{}, titleParts...)
	combinedParts = append(combinedParts, authors...)

	cleanedTitles := make([]string, 0, len(combinedParts))
	for _, t := range combinedParts {
		cleanedTitles = append(cleanedTitles, T2S(strings.TrimSpace(t)))
	}

	// 去重 + 标点清洗
	var portedTitles []string
	seenPorted := map[string]struct{}{}
	for _, t := range cleanedTitles {
		c := cleanCandidate(t)
		if len([]rune(c)) == 0 {
			continue
		}
		if _, ok := seenPorted[c]; ok {
			continue
		}
		seenPorted[c] = struct{}{}
		portedTitles = append(portedTitles, c)
	}

	// 方括号段兜底候选（中文压制组命名）。排序「宁缺毋滥」优先级：
	// ① 无假名的段排前；② 同组内按拉丁占比升序（作者/汉化组多是罗马字）。
	var bracketTitles []string
	for _, p := range parts {
		if strings.Contains(p, "×") {
			continue // 汉化组/作者组
		}
		for _, seg := range expand(T2S(strings.TrimSpace(p))) {
			c := cleanCandidate(seg)
			rl := len([]rune(c))
			if rl == 0 || rl > 60 {
				continue
			}
			if IsBoilerplateSegment(c) {
				continue
			}
			dup := false
			for _, b := range bracketTitles {
				if b == c {
					dup = true
					break
				}
			}
			if dup {
				continue
			}
			bracketTitles = append(bracketTitles, c)
		}
	}
	sortCandidates(bracketTitles)

	var finalTitles []string
	push := func(t string) {
		if len([]rune(t)) == 0 {
			return
		}
		for _, f := range finalTitles {
			if f == t {
				return
			}
		}
		finalTitles = append(finalTitles, t)
	}

	minimalProcessedTitle := MinimalProcessTitle(input)
	if !IsBoilerplateSegment(minimalProcessedTitle) { // 剥完 [] 剩下的 "38全" 之类不当搜索词
		push(minimalProcessedTitle)
	}
	for _, t := range bracketTitles {
		push(t)
	}
	for _, t := range portedTitles {
		push(t)
	}

	final := finalTitles[:0]
	for _, t := range finalTitles {
		if len([]rune(t)) > 0 {
			final = append(final, t)
		}
	}

	if limitCount <= 0 {
		return nil
	}
	if len(final) > limitCount {
		final = final[:limitCount]
	}
	return final
}

// sortCandidates 稳定排序：先无假名，再拉丁占比升序（对齐 C# OrderBy().ThenBy() 稳定语义）。
func sortCandidates(list []string) {
	type kv struct {
		s     string
		kana  bool
		latin float64
	}
	items := make([]kv, len(list))
	for i, s := range list {
		items[i] = kv{s, hasKana(s), latinRatio(s)}
	}
	// 插入排序保持稳定性，元素数量小
	for i := 1; i < len(items); i++ {
		for j := i; j > 0 && (items[j].kana && !items[j-1].kana ||
			(items[j].kana == items[j-1].kana && items[j].latin < items[j-1].latin)); j-- {
			items[j], items[j-1] = items[j-1], items[j]
		}
	}
	for i, it := range items {
		list[i] = it.s
	}
}

// expand 整段 + 下划线/顿号/斜杠/＆ 再拆分；整段始终排第一。
func expand(segment string) []string {
	t := strings.TrimSpace(segment)
	if t == "" {
		return nil
	}
	out := []string{t}
	seps := []string{"_", "、", "／", "/", "＆"}
	rest := t
	for _, sep := range seps {
		rest = strings.ReplaceAll(rest, sep, "\x00")
	}
	for _, p := range strings.Split(rest, "\x00") {
		p = strings.TrimSpace(p)
		if p != "" && p != t {
			out = append(out, p)
		}
	}
	return out
}

// ExtractBracketParts 提取 [] 分段（手写扫描，支持嵌套）。
func ExtractBracketParts(input string) []string {
	var parts []string
	depth := 0
	var cur strings.Builder
	for _, ch := range input {
		switch {
		case ch == '[':
			if depth == 0 {
				cur.Reset()
			} else {
				parts = append(parts, cur.String())
				cur.Reset()
			}
			depth++
		case ch == ']':
			if depth > 0 {
				depth--
				parts = append(parts, cur.String())
				cur.Reset()
			}
		default:
			if depth > 0 {
				cur.WriteRune(ch)
			}
		}
	}
	out := parts[:0]
	for _, p := range parts {
		if strings.TrimSpace(p) != "" {
			out = append(out, p)
		}
	}
	return out
}

func cleanCandidate(t string) string {
	return strings.Trim(rePunctClean.ReplaceAllString(t, " "), " ")
}

// latinRatio 拉丁字母占「字母+汉字+假名」的比例；纯符号/数字返回 0.5。
func latinRatio(s string) float64 {
	latin, cjk := 0, 0
	for _, ch := range s {
		switch {
		case ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z':
			latin++
		case ch >= '\u3040' && ch <= '\u30ff': // 平假名/片假名
			cjk++
		case ch >= '\u3400' && ch <= '\u9fff': // 汉字
			cjk++
		}
	}
	total := latin + cjk
	if total == 0 {
		return 0.5
	}
	return float64(latin) / float64(total)
}

// hasKana 段里是否含假名。含假名的段多半是日文作者/原名，搜索词排序时靠后。
func hasKana(s string) bool {
	for _, ch := range s {
		if ch >= '\u3040' && ch <= '\u30ff' {
			return true
		}
	}
	return false
}

// MinimalProcessTitle 最小处理标题：去 [境外版]/[单行本]/N卷/[]/【】、括号转空格、尾部标点。
func MinimalProcessTitle(input string) string {
	s := reMinJingWai.ReplaceAllString(input, "")
	s = reMinDan.ReplaceAllString(s, "")
	s = reMinJuan.ReplaceAllString(s, "")
	// 循环剥最内层 []（嵌套 "[Fate/stay night [UBW]]" 只剥一层会留下垃圾 "]"）
	for reMinBracket.MatchString(s) {
		s = reMinBracket.ReplaceAllString(s, "")
	}
	s = strings.ReplaceAll(s, "[", "")
	s = strings.ReplaceAll(s, "]", "")
	s = reMinHei.ReplaceAllString(s, "")
	s = reMinParen.ReplaceAllString(s, " ")
	s = reMinTail.ReplaceAllString(s, "")
	s = strings.TrimSpace(s)
	// 标点清洗（与 finalTitles 一致）
	s = rePunctClean.ReplaceAllString(s, " ")
	s = strings.ReplaceAll(s, "／", "/")
	return strings.Trim(s, " ")
}
