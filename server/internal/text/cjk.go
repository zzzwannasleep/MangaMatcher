// Package text：标题清洗/卷号解析/繁简转换/标签规则，从桌面版 KomgaScraper.Core 的
// Text 层（CJK.cs / TagRules.cs / TitleParser.cs / VersionMarker.cs）移植。
package text

import "strings"

// 繁→简 / 简→繁 单字映射（OpenCC 不可用时的内置映射，与桌面版降级路径一致）。
var (
	t2sMap = map[rune]rune{}
	s2tMap = map[rune]rune{}
)

func init() {
	simp := []rune(jt2ft)
	trad := []rune(ft2jt)
	for i := 0; i < len(simp) && i < len(trad); i++ {
		t2sMap[trad[i]] = simp[i]
		s2tMap[simp[i]] = trad[i]
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
