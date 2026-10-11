package text

import (
	"regexp"
	"strings"
)

// VersionMarker：版本/格式标记，移植自 VersionMarker.cs（tag_by_foldername）。
// 刮削会用刮到的标题整体替换 Komga 标题，用户文件夹里区分版本的标注
// （爱藏版/完全版/电子版/汉化组/BW原档…）会被抹平 → 这里从文件夹名提取
// 这些标注，写回标题后缀（方括号格式）+ 标签，保证多版本可分辨、可追溯。

// MachineTag 程序整理打标：凡本程序写入过元数据的系列都带此标签。
const MachineTag = "MACHINE"

type versionWord struct {
	raw  []string
	word string
	tag  string
}

// versionWords 版本词：原始写法（含繁体）→ 标题后缀词 / 标签词。
var versionWords = []versionWord{
	{[]string{"爱藏版", "愛藏版"}, "爱藏版", "爱藏版"},
	{[]string{"完全版"}, "完全版", "完全版"},
	{[]string{"新装版", "新裝版"}, "新装版", "新装版"},
	{[]string{"典藏版"}, "典藏版", "典藏版"},
	{[]string{"文库版", "文庫版"}, "文库版", "文库版"},
	{[]string{"豪华版", "豪華版"}, "豪华版", "豪华版"},
	{[]string{"全彩", "彩色版", "全彩版"}, "全彩版", "全彩版"},
	{[]string{"漫画版", "漫畫版"}, "漫画版", "漫画版"},
	{[]string{"重修版"}, "重修版", "重修版"},
	{[]string{"无修", "無修", "无修正", "無修正", "无修版", "無修版"}, "无修版", "无修版"},
	{[]string{"合订本", "合訂本"}, "合订本", "合订本"},
	{[]string{"复刻版", "復刻版"}, "复刻版", "复刻版"},
	{[]string{"电子版", "電子版"}, "电子版", "电子版"},
	{[]string{"境外版", "境外"}, "境外版", "境外版"},
	{[]string{"b漫版", "b漫", "bili", "bilibili", "哔哩"}, "b漫版", "bili"},
}

// formatHints 来源/格式提示词：命中该词的括号段视为「来源/格式标记」。
var formatHints = []string{
	"原档", "提取档", "压缩档", "汉化", "嵌", "移植", "水印",
	"电子版", "境外版", "PDF", "bili", "生肉",
}

var (
	reTrailBracket  = regexp.MustCompile(`[（]([^（()]{1,40})[）]\s*$`)
	reInnerBracket  = regexp.MustCompile(`[（]([^（()]{1,40})[）]`)
	reVolumeJunkStr = regexp.MustCompile(`(?i)番外|外传|外傳|特典|特别篇|特別篇|vol(?:ume)?s?|chap(?:ter)?\.?|连载|連載|[\d\s\-~～.．+×話话卷巻冊册完未全已结]`)
)

// Detect 从文件夹名提取版本/格式标记（内置规则）。
func Detect(folderName string, whitelist, blacklist []string) []string {
	markers := detectCore(folderName, whitelist)
	if len(blacklist) == 0 {
		return markers
	}
	out := make([]string, 0, len(markers))
	for _, m := range markers {
		if !IsBlacklisted(m, blacklist) {
			out = append(out, m)
		}
	}
	return out
}

// DetectBlacklisted 被黑名单丢掉的标记（调用方用它把标题里残留的旧后缀一并清掉）。
func DetectBlacklisted(folderName string, whitelist, blacklist []string) []string {
	if len(blacklist) == 0 {
		return nil
	}
	markers := detectCore(folderName, whitelist)
	out := make([]string, 0, len(markers))
	for _, m := range markers {
		if IsBlacklisted(m, blacklist) {
			out = append(out, m)
		}
	}
	return out
}

// IsBlacklisted 标记是否被黑名单命中（双向包含）。
func IsBlacklisted(marker string, blacklist []string) bool {
	for _, b := range blacklist {
		if strings.TrimSpace(b) == "" {
			continue
		}
		b = T2S(strings.TrimSpace(b))
		if strings.Contains(marker, b) || strings.Contains(b, marker) {
			return true
		}
	}
	return false
}

// PublisherKeyword 文件夹名中的出版社/来源关键词。
func PublisherKeyword(folderName string) string {
	if folderName == "" {
		return ""
	}
	simplified := T2S(folderName)
	for _, k := range PublisherKeywords {
		if strings.Contains(simplified, k) {
			return k
		}
	}
	return ""
}

func detectCore(folderName string, whitelist []string) []string {
	var markers []string
	if strings.TrimSpace(folderName) == "" {
		return markers
	}

	simplified := T2S(folderName)

	// ① 版本词（爱藏版/完全版/电子版…）
	for _, vw := range versionWords {
		for _, w := range vw.raw {
			if strings.Contains(simplified, w) {
				addContained(&markers, vw.word)
				break
			}
		}
	}

	// ② 出版社段后面的供应商/汉化组段（[标题][作者][出版社][汉化组][卷数] 惯例）；
	//    没有出版社段时，退而收集命中来源/格式提示词的段
	var segs []string
	for _, p := range ExtractBracketParts(folderName) {
		t := T2S(strings.TrimSpace(p))
		if t != "" {
			segs = append(segs, t)
		}
	}
	pubIdx := -1
	for i := len(segs) - 1; i >= 0; i-- {
		p := segs[i]
		if !strings.Contains(p, "汉化") {
			hit := false
			for _, k := range PublisherKeywords {
				if strings.Contains(p, k) {
					hit = true
					break
				}
			}
			if hit {
				pubIdx = i
				break
			}
		}
	}
	if pubIdx >= 0 {
		if pubIdx+1 < len(segs) {
			acceptSupplier(&markers, segs[pubIdx+1])
		}
	} else {
		for _, seg := range segs {
			for _, h := range formatHints {
				if strings.Contains(seg, h) {
					acceptSupplier(&markers, seg)
					break
				}
			}
		}
	}

	// ③ 尾部全角括号（…（BW原档）/（HM重嵌））
	if m := reTrailBracket.FindStringSubmatch(folderName); m != nil {
		acceptSupplier(&markers, T2S(strings.TrimSpace(m[1])))
	}

	// ④ 白名单扩展：内置词表认不出的后缀也能落标签。
	if len(whitelist) > 0 {
		for _, rawWord := range whitelist {
			if strings.TrimSpace(rawWord) == "" {
				continue
			}
			w := T2S(strings.TrimSpace(rawWord))
			if len([]rune(w)) == 0 {
				continue
			}
			covered := false
			for _, m := range markers {
				if strings.Contains(m, w) {
					covered = true
					break
				}
			}
			if covered {
				continue
			}
			seg := ""
			found := false
			for _, s := range segs {
				if strings.Contains(s, w) && !isVolumeJunk(s) {
					seg = s
					found = true
					break
				}
			}
			if found {
				addContained(&markers, seg)
			} else if strings.Contains(simplified, w) {
				addContained(&markers, w)
			}
		}
	}

	return markers
}

// TagForms 标记 → 标签词（b漫版 → bili，与库内已有标签一致；其余原样）。
func TagForms(markers []string) []string {
	out := make([]string, len(markers))
	for i, m := range markers {
		if m == "b漫版" {
			out[i] = "bili"
		} else {
			out[i] = m
		}
	}
	return out
}

// ApplySuffixes 标题补写标记后缀（方括号格式：乱马1/2 [b漫版]）。
// 返回新标题与 changed。
func ApplySuffixes(title string, markers []string) (string, bool) {
	t := title
	normalized := T2S(t)
	changed := false
	for _, word := range markers {
		suffix := "[" + word + "]"
		converted := false
		for _, old := range []string{"（" + word + "）", "(" + word + ")"} {
			if !strings.Contains(t, old) {
				continue
			}
			t = strings.ReplaceAll(t, old, suffix)
			normalized = strings.ReplaceAll(normalized, old, suffix)
			changed = true
			converted = true
		}
		if converted {
			continue // 已归一，无需追加
		}
		if strings.Contains(normalized, word) {
			continue // 已带标记（任意格式）
		}
		sep := " "
		if strings.HasSuffix(t, "]") {
			sep = "" // 连续标记紧贴成一组
		}
		t += sep + suffix
		normalized += sep + suffix
		changed = true
	}
	if changed {
		// 归一产生的括号是紧贴原标题的 → 补一个空格
		idx := strings.IndexByte(t, '[')
		if idx > 0 && !isSpaceByte(t[idx-1]) {
			t = t[:idx] + " " + t[idx:]
		}
	}
	return t, changed
}

func isSpaceByte(b byte) bool {
	return b == ' ' || b == '\t' || b == '\r' || b == '\n'
}

// StripSuffixes 黑名单命中的标记：把标题里残留的旧后缀一并清掉。
func StripSuffixes(title string, words []string) (string, bool) {
	changed := false
	t := title
	for _, word := range words {
		for _, s := range []string{"（" + word + "）", "[" + word + "]", "(" + word + ")"} {
			if !strings.Contains(t, s) {
				continue
			}
			t = strings.ReplaceAll(t, s, "")
			changed = true
		}
	}
	if !changed {
		return title, false
	}
	for strings.Contains(t, "  ") {
		t = strings.ReplaceAll(t, "  ", " ")
	}
	return strings.TrimSpace(t), true
}

// FolderName 系列所在文件夹名（oneshot 的 url 指向文件 → 取其父目录）。
func FolderName(url string, oneshot bool, fallbackName string) string {
	if strings.TrimSpace(url) != "" {
		trimmed := strings.TrimRight(url, "/\\")
		if trimmed != "" {
			name := fileNameOf(trimmed)
			if oneshot {
				name = fileNameOf(parentOf(trimmed))
			}
			if strings.TrimSpace(name) != "" {
				return name
			}
		}
	}
	return fallbackName
}

func parentOf(p string) string {
	i := strings.LastIndexAny(p, "/\\")
	if i <= 0 {
		return ""
	}
	return p[:i]
}

func fileNameOf(p string) string {
	i := strings.LastIndexAny(p, "/\\")
	if i >= 0 && i+1 < len(p) {
		return p[i+1:]
	}
	return p
}

func addContained(list *[]string, item string) {
	for _, m := range *list {
		if strings.Contains(m, item) || strings.Contains(item, m) {
			return
		}
	}
	*list = append(*list, item)
}

// acceptSupplier 供应商/格式段接受规则：长度 2–40、不是纯卷号完结度、与已有标记无包含关系。
func acceptSupplier(markers *[]string, seg string) {
	rl := len([]rune(seg))
	if rl < 2 || rl > 40 {
		return
	}

	if strings.Contains(seg, "（") {
		for _, m := range reInnerBracket.FindAllStringSubmatch(seg, -1) {
			mm := strings.TrimSpace(m[1])
			rl := len([]rune(mm))
			if rl < 2 || rl > 40 || isVolumeJunk(mm) {
				continue
			}
			addContained(markers, mm)
			return
		}
		return
	}

	if isVolumeJunk(seg) {
		return
	}
	addContained(markers, seg)
}

func isVolumeJunk(seg string) bool {
	return len([]rune(reVolumeJunkStr.ReplaceAllString(seg, ""))) == 0
}
