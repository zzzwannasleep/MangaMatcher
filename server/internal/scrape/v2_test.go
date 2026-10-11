package scrape

import (
	"io"
	"log/slog"
	"testing"

	"komgabangumi/internal/bangumi"
	"komgabangumi/internal/sources"
	"komgabangumi/internal/text"
)

// ============================================================
// v2 scrape 层单测：BW 跨源补空 + 防串卷 / 打标 Detect / 精确匹配
// ============================================================

// discardLogger 全丢弃日志（Service 构造用）。
func discardLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(io.Discard, nil))
}

// bwSeriesOf 构造 n 卷的 BW 条目。
func bwSeriesOf(n int) *sources.BwSeries {
	bw := &sources.BwSeries{ID: "CNT_test", Title: "Test Series"}
	for i := 1; i <= n; i++ {
		v := sources.BwVolume{Order: i, ReleaseDate: "2020-01-01", CoverName: coverOf(i)}
		if i == 1 {
			v.Isbn = "9781111111111"
		} else if i == 2 {
			v.Isbn = "9782222222222"
		}
		bw.Volumes = append(bw.Volumes, v)
	}
	return bw
}

func coverOf(i int) string {
	if i == 1 {
		return "" // 卷1无独立封面
	}
	return "cover_v" + itoa(i) + ".jpg"
}

// TestV2BWMergeFillAndGuard BW 跨源补空 + 防串卷。
func TestV2BWMergeFillAndGuard(t *testing.T) {
	svc := &Service{Log: discardLogger()}

	// 防串卷：BW 200 卷 vs 本地 10 册（差 >25% 且 ≥2 册）→ 拒绝
	volMates, n := svc.mergeBookWalkerVolumeMates(nil, bwSeriesOf(200), "测试", 10)
	if n != 0 || len(volMates) != 0 {
		t.Fatalf("防串卷应拒绝: n=%d mates=%d", n, len(volMates))
	}

	// 正常：BW 2 卷补空 ISBN/发售日/封面；不覆盖中文简介
	mates := []bangumi.VolumeMate{{Num: "1", Summary: "已有中文简介"}}
	mates2, n := svc.mergeBookWalkerVolumeMates(mates, bwSeriesOf(2), "测试", 2)
	if n != 3 || len(mates2) != 2 { // 卷1补空 2 项（ISBN+发售日）+ 卷2新增 1 卷
		t.Fatalf("补空错误: n=%d mates=%+v", n, mates2)
	}
	if mates2[0].Summary != "已有中文简介" {
		t.Fatal("中文简介不得被 BW 覆盖")
	}
	if mates2[0].Isbn != "9781111111111" || mates2[0].ReleaseDate != "2020-01-01" {
		t.Fatalf("卷1补空错误: %+v", mates2[0])
	}
	if len(mates2[1].CoverURLs) == 0 || mates2[1].Isbn != "9782222222222" {
		t.Fatalf("卷2新增错误: %+v", mates2[1])
	}
	// 卷1无独立封面 → 不补
	if len(mates2[0].CoverURLs) != 0 {
		t.Fatalf("卷1 BW 无独立封面不应补: %+v", mates2[0])
	}
}

// TestV2VolumesCompatible 边界。
func TestV2VolumesCompatible(t *testing.T) {
	if !volumesCompatible(2, 2) {
		t.Fatal("相等应兼容")
	}
	if !volumesCompatible(3, 2) {
		t.Fatal("差1应兼容")
	}
	if volumesCompatible(0, 2) {
		t.Fatal("空源不兼容")
	}
	if volumesCompatible(200, 2) {
		t.Fatal("差过大不兼容")
	}
}

// TestV2DetectMarker 打标 Detect：版本词从文件夹名识别。
func TestV2DetectMarker(t *testing.T) {
	markers := text.Detect("[碧蓝之海][爱藏版]", nil, nil)
	found := false
	for _, m := range markers {
		if m == "爱藏版" {
			found = true
		}
	}
	if !found {
		t.Fatalf("Detect 应识别 爱藏版: %v", markers)
	}
	folder := text.FolderName("/data/[碧蓝之海][爱藏版]", false, "fallback")
	if folder != "[碧蓝之海][爱藏版]" {
		t.Fatalf("FolderName 错误: %q", folder)
	}
}

// TestV2FindPreciseMatchShared 跨源精确匹配共用标准。
func TestV2FindPreciseMatchShared(t *testing.T) {
	cands := []sources.Candidate{
		{Title: "海贼王", OriTitle: "ONE PIECE", Source: SourceBgm, BgmID: 42},
		{Title: "海贼王外传", Source: SourceBgm, BgmID: 43},
	}
	hit := sources.FindPreciseMatch(cands, text.ExtractAndNormalizeTitle("海贼王"))
	if hit == nil || hit.BgmID != 42 {
		t.Fatalf("精确匹配错误: %+v", hit)
	}
	// 残留垃圾词拒绝（水印）
	noisy := []sources.Candidate{{Title: "ZERO"}}
	if sources.FindPreciseMatch(noisy, text.ExtractAndNormalizeTitle("食物战线无异状zero有水印")) != nil {
		t.Fatal("水印残留应拒绝")
	}
	// 反向包含（候选带副标题）
	sub := []sources.Candidate{{Title: "Fate／stay night [Unlimited Blade Works]"}}
	if sources.FindPreciseMatch(sub, text.ExtractAndNormalizeTitle("fate/stay night")) == nil {
		t.Fatal("反向包含应命中")
	}
}
