package text

import "strings"

// TagRules：标签/关键词规则集，移植自 TagRules.cs。

// TagWhitelist 标签白名单（源自脚本 tagLabels 常量）。
var TagWhitelist = map[string]struct{}{}

// EqualLabelMap 等价标签组归一映射。
var EqualLabelMap = map[string]string{}

// StatusTags 状态类标签（不进 tags，仅用于状态判定/清理）。
var StatusTags = []string{"连载", "连载中", "完结", "已完结", "腰斩", "停刊", "长期休载", "停止连载", "休刊"}

// ChineseOriginTags 作品产地类标签（语言/阅读方向判定用）。
var ChineseOriginTags = []string{"国产", "国漫", "国创", "港漫", "中漫", "内地漫画"}

// WebtoonTags 网页条漫类标签（从左往右阅读）。
var WebtoonTags = []string{"条漫", "webtoon", "WEBTOON"}

// AdultTags 成人向标签词表（ageRating 兜底推断）。
var AdultTags = []string{
	"エロ", "官能", "乱交", "SM", "触手", "鬼畜", "催眠", "扶她", "母系", "熟女",
	"调教", "恶堕", "幼女", "R18", "成年コミック", "成人漫画", "アダルトコミック",
	"18X", "無修正", "無修", "无修", "H本", "A书", "黄漫",
}

// PublisherKeywords 文件夹名出版社/来源关键词。
var PublisherKeywords = []string{
	"台湾角川", "台湾东贩", "尖端", "青文", "东立", "长鸿", "尚禾", "大然", "龙成",
	"群英", "未来数位", "新视界", "玉皇朝", "天下", "传信", "天闻角川", "bili",
	"bilibili", "哔哩哔哩", "汉化", "生肉", "日版", "原版", "正版", "官方",
	"中文版", "简中", "繁中", "简体中文", "繁体中文", "简体", "繁体",
}

// AuthorRoles infobox 角色键 → Komga 作者角色。
var AuthorRoles = []struct {
	Key  string
	Role string
}{
	{"作者", "writer"},
	{"原作", "writer"},
	{"分镜", "writer"},
	{"脚本·分镜", "writer"},
	{"脚本", "writer"},
	{"漫画家", "writer"},
	{"作画", "penciller"},
	{"铅稿", "penciller"},
	{"插图", "illustrator"},
	{"插画家", "illustrator"},
	{"人物原案", "conceptor"},
	{"人物设定", "designer"},
	{"原案", "story"},
	{"系列构成", "scriptwriter"},
	{"上色", "colorist"},
}

func init() {
	for _, t := range strings.Split(TagLabels, ",") {
		if t != "" {
			TagWhitelist[t] = struct{}{}
		}
	}
	for _, group := range EqualLabels {
		labels := strings.Split(group, ",")
		for _, l := range labels {
			EqualLabelMap[l] = labels[0]
		}
	}
}

// IsValidTag 标签是否在白名单内且不是状态标签。
func IsValidTag(tag string) bool {
	if _, ok := TagWhitelist[tag]; !ok {
		return false
	}
	for _, s := range StatusTags {
		if s == tag {
			return false
		}
	}
	return true
}

// NormalizeEqualLabel 等价标签归一（如 治癒→治愈），无匹配原样返回。
func NormalizeEqualLabel(tag string) string {
	if n, ok := EqualLabelMap[tag]; ok {
		return n
	}
	return tag
}

// ContainsHangul 文本里是否含谚文（韩文字母 AC00–D7A3）。
func ContainsHangul(s string) bool {
	for _, ch := range s {
		if ch >= '\uac00' && ch <= '\ud7a3' {
			return true
		}
	}
	return false
}

// KoreanTags 韩漫类标签词表（语言判定也用它）。
var KoreanTags = []string{"韩漫", "韓漫", "韩国漫画", "韓國漫畫", "韩网漫", "韓网漫", "Korean"}

var koreanPlatforms = []string{
	"naver", "lezhin", "kakao", "toomics", "bomtoon", "ridibooks", "ridi", "munpia", "mrblue", "comico", "joara",
}

// LooksKorean 疑似韩漫条目：谚文标题/别名、韩漫类标签、韩国平台连载杂志。
func LooksKorean(title, oriTitle, aliases string, tags []string, infoboxMagazine string) bool {
	if ContainsHangul(title) || ContainsHangul(oriTitle) || ContainsHangul(aliases) {
		return true
	}
	for _, t := range tags {
		for _, k := range KoreanTags {
			if strings.EqualFold(t, k) {
				return true
			}
		}
	}
	if infoboxMagazine != "" {
		m := strings.ToLower(infoboxMagazine)
		for _, p := range koreanPlatforms {
			if strings.Contains(m, p) {
				return true
			}
		}
	}
	return false
}
