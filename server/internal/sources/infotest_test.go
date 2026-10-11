package sources

import (
	"testing"

	"komgabangumi/internal/text"
)

// TestMoxInfoFieldDebug 信息字段解析器专项（状态/分类/版本对齐 C# 正则语义）。
func TestMoxInfoFieldDebug(t *testing.T) {
	// T2S 转换后的 name_subt 文本（mock 语义）
	info := "状态：连载 分类：喜剧 校园　版本：东立　最后出版：2020"
	if v := infoField(info, []string{"狀態：", "状态："}, []string{"分類：", "分类：", "最後出版：", "最后出版：", " ", "\t"}); v != "连载" {
		t.Fatalf("状态字段错误: %q", v)
	}
	if raw := infoFieldUntil(info, []string{"分類：", "分类："}, []string{"版本：", "最後出版：", "最后出版："}); raw != "喜剧 校园" {
		t.Fatalf("分类字段错误: %q", raw)
	}
	if v := infoField(info, []string{"版本："}, []string{"最後出版：", "最后出版：", " ", "\t"}); v != "东立" {
		t.Fatalf("版本字段错误: %q", v)
	}
	// T2S 是否把 狀態：連載 转成 状态：连载
	got := text.T2S("狀態：連載")
	if got != "状态：连载" {
		t.Logf("T2S(狀態：連載) = %q（词表未覆盖也允许，labels 双简繁兜底）", got)
	}
}
