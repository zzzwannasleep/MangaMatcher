package text

import (
	"regexp"
	"strconv"
	"strings"
)

// TitleParser：标题/卷号/日期解析，移植自 TitleParser.cs
// （extractSeriesTitles / extractAndNormalizeTitle / extractVolumeNumber / normalizeDate / capitalize）。

var (
	reVolNum      = regexp.MustCompile(`(?i)vol(?:ume)?[._\s]*v?(\d{1,4})(?:完|end)?$`)
	reParenNum    = regexp.MustCompile(`(?i)[（(](\d{1,4})[）)]\s*(?:完)?$`)
	reTrailingNum = regexp.MustCompile(`\s(\d{1,4})$`)
	// 卷号字含日文新字体 巻/冊 与 集：Bangumi 单行本大量用 "X (第18巻)"，以前 [卷册] 漏了 巻，
	// 整批单行本卷号解析不出来 → 静默按列表序号兜底（实测一次任务 252 次）。
	reChineseVol    = regexp.MustCompile(`(?i)第\s*(\d{1,4})\s*[卷册巻冊集]\s*(?:完)?$`)
	reVPrefixNum    = regexp.MustCompile(`(?i)(?:^|[^\w/])v[._\s]?(\d{1,4})$`)
	reParenAnywhere = regexp.MustCompile(`(?i)[（(](\d{1,4})[）)]`)
	reVolAnywhere   = regexp.MustCompile(`(?i)vol(?:ume)?[._\s]*v?(\d{1,4})`)
	reCnVolAnywhere = regexp.MustCompile(`(?i)第\s*(\d{1,4})\s*[卷册巻冊集]`)

	// 卷号后跟副标题："100億の男 1 人生を売った男"、"せんごくっ!? 3 悠久乱世恋華譚"
	reNumWithSubtitle = regexp.MustCompile(`[\s　](\d{1,3})[\s　]+\S+$`)
	// 卷号后跟任意副标题（含空格）："火之鸟 复刻版 07 乱世篇 上"、"… 01―Silent Mobius"。
	// 只认 1~2 位数，避免把 "FAIRY TAIL 100 YEARS QUEST" 的 100 当卷号。
	reNumWithLongSubtitle = regexp.MustCompile(`[\s　](\d{1,2})(?:[\s　]+|[-–—―]+).*$`)
	// 数字紧跟卷字："ドラえもん 0巻"、"5巻+特典"
	reVolCharNum = regexp.MustCompile(`[（(]?\s*(\d{1,4})\s*[卷巻冊册集]`)
	// 英文前缀编号："… FILE1"、"Part.2"、"Lv.3"
	reFileNum = regexp.MustCompile(`(?i)(?:file|part|disk|disc|chapter|chap|episode|ep|lv)[\s._-]*(\d{1,4})$`)
	// 罗马数字卷号："コヨーテ I"（要求前面有空白，且整串不能就是那个罗马数字）
	reRomanNum = regexp.MustCompile(`[\s　]([IVX]{1,4})$`)
	// 汉字数字卷号："うせもの宿 (一)"
	reKanjiNumParen = regexp.MustCompile(`[（(]\s*([一二三四五六七八九十]+)\s*[）)]$`)

	// VolumeTitleRegex 书名卷号模式（用于批量匹配中的卷识别）。
	VolumeTitleRegex = regexp.MustCompile(`(?i)(?:vol(?:ume)?s?|巻|卷|册|冊)[\W_]*?(?P<volNum>\d+)|第\s*(?P<volNum2>\d+)\s*(?:巻|卷|册|冊|集)|(?P<volNum3>\d+)\s*(?:巻|卷|册|冊|集)`)

	reCopySuffixCn  = regexp.MustCompile(`\s*[-_－—]*\s*副本(?:\s*[（(]\d{1,3}[)）])?$`)
	reCopySuffixNum = regexp.MustCompile(`\s*[（(]\d{1,3}[)）]$`)

	rePunctNorm  = regexp.MustCompile(`[:：•·․,，。、'’?？!！~⁓～〜]`)
	rePunctClean = regexp.MustCompile(`[:：•·․,，。'’?？!！~⁓～〜]`)
	reSpaces     = regexp.MustCompile(`\s+`)

	reDateCnFull = regexp.MustCompile(`^(\d{4})年(\d{1,2})月(\d{1,2})日$`)
	reDateCnMon  = regexp.MustCompile(`^(\d{4})年(\d{1,2})月$`)
	reDateIso    = regexp.MustCompile(`^(\d{4})-(\d{1,2})-(\d{1,2})$`)
	reDateIsoMon = regexp.MustCompile(`^(\d{4})-(\d{1,2})$`)
	reCapitalize = regexp.MustCompile(`\b[a-z]`)

	reMinJingWai = regexp.MustCompile(`[\(\[【（]?境外版[\)\]】）]?\s*`)
	reMinDan     = regexp.MustCompile(`[\(\[【（]?单行本[\)\]】）]?\s*$`)
	// reMinJuan 兼容「[全3卷]」「（全12卷）」「N卷」尾巴（上游 C# 会把 "[全3卷]" 剥成 "[全" 留下孤字"全"——已修）
	reMinJuan    = regexp.MustCompile(`[\(\[【（]?(?:全)?\d+卷[\)\]】）]?\s*$`)
	reMinBracket = regexp.MustCompile(`\[[^\[\]]*\]`)
	reMinHei     = regexp.MustCompile(`【([^【】]*)】`)
	reMinParen   = regexp.MustCompile(`[（()）]`)
	reMinTail    = regexp.MustCompile(`[_-]?\s*$`)
	// 版本/合辑/来源后缀（"铳梦 完全版"、"七龙珠港版完全版"、"RE-TAKE全年龄版"）
	reMinIchiban   = regexp.MustCompile(`[（(]?\s*一般コミック\s*[）)]?`)
	reMinAllVol    = regexp.MustCompile(`全\s*\d+\s*[卷巻冊册]`)
	reMinPlusTail  = regexp.MustCompile(`\s+[+＋].*$`)
	reMinHeadSplit = regexp.MustCompile(`^(.{3,40}?)\s*[-–—~～:：]`)

	// 纯卷号/卷数区间：25、第12卷、3完、38全、全3卷、全3、Vol.01、Vol.01-Vol.18、1-100完、全一册
	reBoilerplateVol = regexp.MustCompile(`(?i)^(?:第)?(?:全)?\d+(?:[-~～]\d+)?[卷话話册集完全]?$|^vol\.?\s*\d+(?:\s*[-~～]\s*(?:vol\.?\s*)?\d+)?$|^全\s*[一二三四五六七八九十\d]+\s*[卷巻册冊集]$`)
)

// ExtractVolumeNumber 书名/文件名 → 卷号（Vol.x / Vol_x / (x) / （x） / 尾数字 / 第x卷 / v01）。
func ExtractVolumeNumber(name string) (int, bool) {
	if strings.TrimSpace(name) == "" {
		return 0, false
	}
	for _, re := range []*regexp.Regexp{reVolNum, reParenNum, reChineseVol, reVPrefixNum, reFileNum, reTrailingNum} {
		if m := re.FindStringSubmatch(name); m != nil {
			if n, err := strconv.Atoi(m[1]); err == nil {
				return n, true
			}
		}
	}
	if m := reKanjiNumParen.FindStringSubmatch(name); m != nil {
		if n, ok := kanjiNumToInt(m[1]); ok {
			return n, true
		}
	}
	if m := reRomanNum.FindStringSubmatch(name); m != nil && len([]rune(name)) > len(m[1])+1 {
		if n, ok := romanNumToInt(m[1]); ok {
			return n, true
		}
	}
	if m := reNumWithSubtitle.FindStringSubmatch(name); m != nil {
		if n, err := strconv.Atoi(m[1]); err == nil {
			return n, true
		}
	}
	if m := reNumWithLongSubtitle.FindStringSubmatch(name); m != nil {
		if n, err := strconv.Atoi(m[1]); err == nil {
			return n, true
		}
	}
	if m := reVolCharNum.FindStringSubmatch(name); m != nil {
		if n, err := strconv.Atoi(m[1]); err == nil {
			return n, true
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

// kanjiNumToInt 汉字数字 → 阿拉伯数字（一~九十九）；解析不出返回 false。
func kanjiNumToInt(s string) (int, bool) {
	if s == "" {
		return 0, false
	}
	digits := map[rune]int{'一': 1, '二': 2, '三': 3, '四': 4, '五': 5, '六': 6, '七': 7, '八': 8, '九': 9}
	r := []rune(s)
	if s == "十" {
		return 10, true
	}
	ten := strings.Index(s, "十")
	if ten < 0 {
		if len(r) == 1 {
			if d, ok := digits[r[0]]; ok {
				return d, true
			}
		}
		return 0, false
	}
	tens := 1
	if ten > 0 {
		d, ok := digits[r[0]]
		if !ok {
			return 0, false
		}
		tens = d
	}
	if ten == len(r)-1 {
		return tens * 10, true
	}
	ones, ok := digits[r[len(r)-1]]
	if !ok {
		return 0, false
	}
	return tens*10 + ones, true
}

// romanNumToInt 罗马数字 → 阿拉伯数字（1~30）；非法或超范围返回 false。
func romanNumToInt(s string) (int, bool) {
	vals := map[rune]int{'I': 1, 'V': 5, 'X': 10}
	total, prev := 0, 0
	r := []rune(s)
	for i := len(r) - 1; i >= 0; i-- {
		v, ok := vals[r[i]]
		if !ok {
			return 0, false
		}
		if v < prev {
			total -= v
		} else {
			total += v
		}
		if v > prev {
			prev = v
		}
	}
	if total < 1 || total > 30 {
		return 0, false
	}
	return total, true
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
	// 去「全部」空白：以前只 Trim，于是 "FAIRY TAIL  ZERO"（双空格，来自标点换空格）
	// 与候选 "FAIRY TAIL ZERO" 永远不相等 —— 实测日志里 FAIRY TAIL ZERO / Gimmick! 特技化裝 就是这么漏的。
	return reSpaces.ReplaceAllString(strings.ToLower(rePunctNorm.ReplaceAllString(title, " ")), "")
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
	// 实测漏网（日志里出现 "词=东贩" 这种搜索词）：东贩/天下/时报/尚禾 等港台出版社
	"东贩", "天下", "时报", "尚禾", "大辣", "东贩出版", "集英社", "讲谈社", "小学馆", "白泉社",
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

	minimalProcessedTitle := MinimalProcessTitle(T2S(input))
	if !IsBoilerplateSegment(minimalProcessedTitle) { // 剥完 [] 剩下的 "38全" 之类不当搜索词
		push(minimalProcessedTitle)
	}
	// 长副标题截断：「Fate Grand Order-Epic of Remnant-亞種特異點III …」→ 再补一个主标题候选
	push(MainTitleHead(minimalProcessedTitle))
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

// sortCandidates 稳定排序：先无假名，再长段优先（标题通常比 [作者]/[出版社] 段长），最后拉丁占比升序。
// 以前第二关键字是拉丁占比升序 —— 纯汉字作者名（0.0）会被排到混排标题（如 AKABOSHI異聞水滸傳）前面，
// 白打一次搜索。
func sortCandidates(list []string) {
	type kv struct {
		s     string
		kana  bool
		latin float64
		n     int
	}
	items := make([]kv, len(list))
	for i, s := range list {
		items[i] = kv{s, hasKana(s), latinRatio(s), len([]rune(s))}
	}
	// 插入排序保持稳定性，元素数量小
	less := func(a, b kv) bool {
		if a.kana != b.kana {
			return !a.kana // 无假名的排前
		}
		if a.n != b.n {
			return a.n > b.n // 长段优先
		}
		return a.latin < b.latin
	}
	for i := 1; i < len(items); i++ {
		for j := i; j > 0 && less(items[j], items[j-1]); j-- {
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
	// 标点换空格后要把连续空白压成一个：搜索词里的双空格会让 Bangumi 搜不到
	//（实测 "FAIRY TAIL  ZERO" / "战国    悠久乱世恋华谭"）。
	return strings.Trim(reSpaces.ReplaceAllString(rePunctClean.ReplaceAllString(t, " "), " "), " ")
}

// MainTitleHead 长标题截断：从第一个分隔符（- ~ ：）截出主标题；太短/无分隔符/截不出内容返回空串。
func MainTitleHead(title string) string {
	t := strings.TrimSpace(title)
	if len([]rune(t)) < 12 {
		return ""
	}
	m := reMinHeadSplit.FindStringSubmatch(t)
	if m == nil {
		return ""
	}
	head := cleanCandidate(strings.TrimSpace(m[1]))
	if len([]rune(head)) < 3 || len([]rune(head)) >= len([]rune(t)) {
		return ""
	}
	return head
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

// MinimalProcessTitle 最小处理标题：去 [境外版]/[单行本]/N卷/[]、括号转空格、剥版本后缀、尾部标点。
func MinimalProcessTitle(input string) string {
	s := reMinJingWai.ReplaceAllString(input, "")
	s = reMinDan.ReplaceAllString(s, "")
	s = reMinJuan.ReplaceAllString(s, "")
	// 【】以前整段删掉 —— 对「名字被书名号包着」的标题（Bangumi 的 【推しの子】/【我推的孩子】、
	// 用户文件夹 "【作品名】"）会把名字一起删光：归一化结果变空串，与任何候选都不相等，
	// 搜索词也变空（直接报"无法从标题解析搜索词"）。现在只删「注释型」【】
	//（含数字或完整度/汉化等元信息词，如 【已完结】【1-2卷全是中文】），名字型的只去括号保留内容。
	s = reMinHei.ReplaceAllStringFunc(s, func(m string) string {
		inner := strings.TrimSpace(reMinHei.FindStringSubmatch(m)[1])
		if inner == "" || IsBoilerplateSegment(inner) || strings.ContainsAny(inner, "0123456789") {
			return " "
		}
		return " " + inner + " "
	})
	// 循环剥最内层 []（嵌套 "[Fate/stay night [UBW]]" 只剥一层会留下垃圾 "]"）
	for reMinBracket.MatchString(s) {
		s = reMinBracket.ReplaceAllString(s, "")
	}
	s = strings.ReplaceAll(s, "[", "")
	s = strings.ReplaceAll(s, "]", "")
	s = reMinParen.ReplaceAllString(s, " ")
	// 版本/合辑/来源后缀：完全版、港版、爱藏版、全年龄版、一般コミック、全N卷、+特典+…
	s = reMinIchiban.ReplaceAllString(s, " ")
	s = reMinAllVol.ReplaceAllString(s, " ")
	// 只剥「空白 + 加号」开头的附加卷尾；C100+C104 这种没空格的不能碰
	s = reMinPlusTail.ReplaceAllString(s, " ")
	for _, marker := range VersionSuffixMarkers {
		s = strings.ReplaceAll(s, marker, " ")
	}
	s = reMinTail.ReplaceAllString(s, "")
	s = strings.TrimSpace(s)
	// 标点清洗（与 finalTitles 一致）
	s = rePunctClean.ReplaceAllString(s, " ")
	s = strings.ReplaceAll(s, "／", "/")
	s = reSpaces.ReplaceAllString(s, " ")
	return strings.Trim(s, " ")
}

// VersionSuffixMarkers 版本/装帧/来源后缀词（当搜索词会搜不到，如「铳梦 完全版」）。
var VersionSuffixMarkers = []string{
	"完全版", "愛藏版", "爱藏版", "新装版", "新裝版", "典藏版", "豪華版", "豪华版",
	"特裝版", "特装版", "文庫版", "文库版", "復刻版", "复刻版", "盒裝版", "盒装版",
	"港版", "台版", "全年龄版", "全年齢版", "电子版", "電子版",
}
