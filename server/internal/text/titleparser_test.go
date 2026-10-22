package text

import "testing"

// 用例全部来自 2026-10-22 桌面版一次 1217 系列任务的真实日志
// （KomgaScraper-日志-20261022-141919.txt）：252 次「解析不出卷号」+ 180 个失败系列的文件夹名。

func TestExtractVolumeNumberRealLog(t *testing.T) {
	cases := []struct {
		name string
		want int
	}{
		// Bangumi 单行本用日文新字体「巻」，以前 [卷册] 漏了它 → 18 卷全解析不出
		{"X (第18巻)", 18},
		{"X (第01巻)", 1},
		{"第 5 巻", 5},
		// 卷号后跟副标题
		{"100億の男 1 人生を売った男", 1},
		{"100億の男 10 再出発する男", 10},
		{"せんごくっ!? 3 悠久乱世恋華譚", 3},
		{"火之鸟 复刻版 07 乱世篇 上", 7},
		{"サイレントメビウス完全版 01―Silent Mobius", 1},
		// 罗马数字 / 汉字数字 / 英文前缀编号 / 数字紧跟卷字
		{"コヨーテ I", 1},
		{"コヨーテ IV", 4},
		{"うせもの宿 (三)", 3},
		{"D・N・A² 〜何処かで失くしたあいつのアイツ〜 FILE2", 2},
		{"ドラえもん 0巻", 0},
		{"ネトゲの嫁は女の子じゃないと思った? Lv.3", 3},
		// 回归：老的形态不能坏
		{"乱马 Vol.01 [东立]", 1},
		{"乱马 01", 1},
		{"海贼王.Vol.12", 12},
		{"[Comic][火影忍者][岸本齐史][东立][C.C]Vol_58", 58},
	}
	for _, c := range cases {
		got, ok := ExtractVolumeNumber(c.name)
		if !ok || got != c.want {
			t.Errorf("ExtractVolumeNumber(%q) = %d,%v；期望 %d", c.name, got, ok, c.want)
		}
	}
}

func TestExtractVolumeNumberNoFalsePositive(t *testing.T) {
	// 这些没有卷号可解析：必须返回 false，交给调用方按「最小未占用卷号」兜底，不能瞎猜
	for _, name := range []string{
		"21世紀少年 上",
		"ホワイトアウト (中)",
		"CLAMP PREMIUM COLLECTION X (18.5)",
		"D・N・A² 〜何処かで失くしたあいつのアイツ〜",
		"メイドインアビス 公式アンソロジー 第三層 白笛たちのユウウツ",
		"FAIRY TAIL 100 YEARS QUEST", // 100 是年份不是卷号（3 位数不认）
		"ヨメがコレなもんで。",
	} {
		if n, ok := ExtractVolumeNumber(name); ok {
			t.Errorf("ExtractVolumeNumber(%q) 不该解析出卷号，实际 %d", name, n)
		}
	}
}

func TestExtractSeriesTitlesRealLog(t *testing.T) {
	cases := []struct {
		input string
		want  string // 首词
	}{
		// 搜索词排序：标题段比 [作者]/[出版社] 段长 → 标题当首词（以前纯汉字作者名排第一，白打一次搜索）
		// 注：Go 侧 T2S 是内置单字表（无 OpenCC），比桌面版的 OpenCC 窄；cjk.go 的 extraT2S 补了
		// 实测缺的 銃/滸 两个字（铳梦 完全版、異聞水滸傳 都因此归一化不了）。
		{"[AKABOSHI異聞水滸傳][天野洋一][東立][aaa874160][3完]", "AKABOSHI异闻水浒传"},
		{"[DUDS.HUNT][筒井哲也][玉皇朝][C.C][1完]", "DUDS.HUNT"},
		{"[ANGELIC.LAYER天使領域][CLAMP][東贩][C.C][1-5完]", "ANGELIC.LAYER天使领域"},
		{"[东云太郎][圣诞之吻precious diary 薰篇][完结]", "圣诞之吻precious diary 薰篇"},
		{"[猎人x猎人][富坚义博][Vol.01-Vol.36][东立数码电子]", "猎人x猎人"},
		// 无方括号的繁体文件夹名要转简（以前只有方括号段过 T2S）
		{"齊木楠雄的災難", "齐木楠雄的灾难"},
		{"魔法少女小圓", "魔法少女小圆"},
		// 版本/来源后缀
		{"铳梦 完全版", "铳梦"},
		{"七龙珠港版完全版", "七龙珠"},
		{"RE-TAKE全年龄版", "RE-TAKE"},
		{"(一般コミック) [小島あきら] まほらば 全12卷 +白、碧、蒼、朱", "まほらば"},
		// 【】只剥注释型的，名字型的保留（以前整段删 → 搜索词变空、归一化变空串）
		{"【我推的孩子】", "我推的孩子"},
		{"文学少女与美味故事【1-2卷全是中文】【已完结】", "文学少女与美味故事"},
	}
	for _, c := range cases {
		terms := ExtractSeriesTitles(c.input, 4)
		if len(terms) == 0 {
			t.Errorf("ExtractSeriesTitles(%q) 返回空", c.input)
			continue
		}
		if terms[0] != c.want {
			t.Errorf("ExtractSeriesTitles(%q)[0] = %q；期望 %q（全部候选 %v）", c.input, terms[0], c.want, terms)
		}
	}
}

func TestExtractSeriesTitlesKeepsPlusTitle(t *testing.T) {
	// "+" 剥离只针对「空白 + 加号」的附加卷尾，不能吃掉 C100+C104 这种正常标题
	terms := ExtractSeriesTitles("(C100+C104)CLOSED ISLAND Volume.2+4 [中国翻訳]", 4)
	if len(terms) == 0 || terms[0] != "C100+C104 CLOSED ISLAND Volume.2+4" {
		t.Fatalf("C100+C104 被加号规则吃掉了：%v", terms)
	}
}

func TestMainTitleHead(t *testing.T) {
	terms := ExtractSeriesTitles(
		"Fate Grand Order-Epic of Remnant-亞種特異點III 亞種平行世界 屍山血河舞台 下總國 英靈劍豪七番勝負", 4)
	found := false
	for _, s := range terms {
		if s == "Fate Grand Order" {
			found = true
		}
	}
	if !found {
		t.Fatalf("长副标题没截出主标题：%v", terms)
	}
	// 短标题不动
	if h := MainTitleHead("乱马1/2"); h != "" {
		t.Fatalf("短标题不该截断，得到 %q", h)
	}
}

func TestExtractAndNormalizeTitle(t *testing.T) {
	pairs := [][2]string{
		{"FAIRY TAIL  ZERO", "FAIRY TAIL ZERO"}, // 双空格（标点换空格留下）必须等价
		{"Gimmick! 特技化裝", "Gimmick  特技化裝"},      // 同上
		{"AKB49~恋爱禁止条例〜", "AKB49〜恋愛禁止条例〜"},      // 波浪线 + 繁简
		{"【我推的孩子】", "我推的孩子"},                    // 书名号
		{"齊木楠雄的災難", "齐木楠雄的灾难"},                  // 繁简
	}
	for _, p := range pairs {
		a, b := ExtractAndNormalizeTitle(p[0]), ExtractAndNormalizeTitle(p[1])
		if a != b || a == "" {
			t.Errorf("归一化应相等：%q → %q，%q → %q", p[0], a, p[1], b)
		}
	}
}

func TestIsBoilerplateSegmentAllVolumes(t *testing.T) {
	for _, s := range []string{"全一册", "全一卷", "全12卷", "全3巻", "3完", "Vol.01-Vol.18", "东贩"} {
		if !IsBoilerplateSegment(s) {
			t.Errorf("IsBoilerplateSegment(%q) 应为元信息段", s)
		}
	}
	// 繁体形态靠调用方先过 T2S（ExtractSeriesTitles 就是这么做的）：東販 → 东贩 → 命中出版社词表
	if T2S("東販") != "东贩" {
		t.Errorf("T2S(東販) = %q；期望 东贩", T2S("東販"))
	}
	if !IsBoilerplateSegment(T2S("東販")) {
		t.Error("T2S 后的 东贩 应被识别为出版社段")
	}
	// 长标题段里含出版社词不受影响
	if IsBoilerplateSegment("天下第一的剑客成长记") {
		t.Error("长标题不该被出版社词误判")
	}
}
