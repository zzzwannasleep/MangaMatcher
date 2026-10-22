// Package text：标题清洗/卷号解析/繁简转换/标签规则，从桌面版 KomgaScraper.Core 的
// Text 层（CJK.cs / TagRules.cs / TitleParser.cs / VersionMarker.cs）移植。
package text

import "strings"

// 繁→简 / 简→繁 单字映射（OpenCC 不可用时的内置映射，与桌面版降级路径一致）。
var (
	t2sMap = map[rune]rune{}
	s2tMap = map[rune]rune{}
)

// extraT2S 补充映射：内置单字表（cjkdata.go，自动生成，勿手改）缺的常用繁体字。
// 桌面版走 OpenCC 不受影响，Go 侧只有这张表，缺字会让标题匹配不上 —— 实测日志里
// 「铳梦 完全版」（Bangumi 叫 銃夢）与「[AKABOSHI異聞水滸傳]」都因为这两个字漏了而无法归一化相等。
var extraT2S = map[rune]rune{
	'銃': '铳',
	'滸': '浒',
}

func init() {
	simp := []rune(jt2ft)
	trad := []rune(ft2jt)
	for i := 0; i < len(simp) && i < len(trad); i++ {
		t2sMap[trad[i]] = simp[i]
		s2tMap[simp[i]] = trad[i]
	}
	for t, s := range extraT2S {
		t2sMap[t] = s
		s2tMap[s] = t
	}
}

// T2S 繁体 → 简体（单字映射；日文新字体不在表内的字符原样保留）。
func T2S(input string) string {
	if input == "" {
		return input
	}
	var sb strings.Builder
	sb.Grow(len(input))
	changed := false
	for _, c := range input {
		if s, ok := t2sMap[c]; ok {
			sb.WriteRune(s)
			changed = true
		} else {
			sb.WriteRune(c)
		}
	}
	if !changed {
		return input
	}
	return sb.String()
}

// S2T 简体 → 繁体（单字映射）。
func S2T(input string) string {
	if input == "" {
		return input
	}
	var sb strings.Builder
	sb.Grow(len(input))
	for _, c := range input {
		if t, ok := s2tMap[c]; ok {
			sb.WriteRune(t)
		} else {
			sb.WriteRune(c)
		}
	}
	return sb.String()
}

// IsHanOnly 纯中文判定（含繁体；含假名/谚文即非中文）。
func IsHanOnly(s string) bool {
	if strings.TrimSpace(s) == "" {
		return false
	}
	han := 0
	for _, ch := range s {
		if ch == ' ' || ch == '\t' || (ch >= '\u0009' && ch <= '\r') {
			continue
		}
		if ch >= '\u3040' && ch <= '\u30ff' { // 假名 → 日文
			return false
		}
		if ch >= '\uac00' && ch <= '\ud7a3' { // 谚文 → 韩文
			return false
		}
		if ch >= '\u4e00' && ch <= '\u9fff' {
			han++
		}
	}
	return han > 0
}
