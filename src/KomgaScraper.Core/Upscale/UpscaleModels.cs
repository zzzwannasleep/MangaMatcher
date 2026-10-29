namespace KomgaScraper.Core.Upscale;

/// <summary>超分模型分类：轻量 / 普适 / 特化（用户自己导入的算 Custom）。</summary>
public enum UpscaleModelKind
{
    Lightweight,
    General,
    Specialized,
    Custom,
}

/// <summary>
/// 一个可用于超分的模型描述。官方三个模型的文件随 Real-ESRGAN ncnn 便携包一起下载，
/// 不预置在安装包里（安装包只带引擎下载逻辑）。
/// </summary>
public sealed record UpscaleModelInfo(
    string Id,
    string DisplayName,
    UpscaleModelKind Kind,
    string Description,
    IReadOnlyList<int> Scales,
    long ApproxBytes,
    string SourceUrl)
{
    public bool SupportsScale(int scale) => Scales.Contains(scale);

    public string KindText => Kind switch
    {
        UpscaleModelKind.Lightweight => "轻量",
        UpscaleModelKind.General => "普适",
        UpscaleModelKind.Specialized => "特化",
        _ => "自定义",
    };

    public string Label => DisplayName;

    public string ScalesText => string.Join(" / ", Scales.Select(s => $"{s}x"));
}

/// <summary>
/// 官方模型清单（与 Real-ESRGAN v0.2.5.0 的 Windows 便携包一一对应）：
/// <list type="bullet">
/// <item>轻量：realesr-animevideov3（1.2MB，2x/3x/4x，最快）</item>
/// <item>普适：realesrgan-x4plus（32MB，通用 4x）</item>
/// <item>特化：realesrgan-x4plus-anime（8.5MB，二次元/漫画 4x）</item>
/// </list>
/// 参考：https://github.com/xinntao/Real-ESRGAN ；模型选择也可参考 https://openmodeldb.info/
/// </summary>
public static class UpscaleModelCatalog
{
    public const string LightweightId = "realesr-animevideov3";
    public const string GeneralId = "realesrgan-x4plus";
    public const string SpecializedId = "realesrgan-x4plus-anime";

    public static IReadOnlyList<UpscaleModelInfo> Official { get; } =
    [
        new(
            LightweightId,
            "轻量 · 动漫视频 v3",
            UpscaleModelKind.Lightweight,
            "1.2 MB 小模型，速度最快；原生 2x / 3x / 4x，线条干净的漫画/动画截图用它最省事。",
            [2, 3, 4],
            1_247_368 + 3_173,
            "https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0"),
        new(
            GeneralId,
            "普适 · 通用 4x",
            UpscaleModelKind.General,
            "官方通用模型（约 32 MB），照片、彩页、插画细节最强；黑白网点可能过锐。仅原生 4x。",
            [4],
            33_424_520 + 116_029,
            "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth"),
        new(
            SpecializedId,
            "特化 · 二次元 4x",
            UpscaleModelKind.Specialized,
            "官方 anime 6B 模型（约 8.5 MB），线条锐利、网点干净，黑白漫画首选。仅原生 4x。",
            [4],
            8_943_500 + 30_290,
            "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.2.4/RealESRGAN_x4plus_anime_6B.pth"),
    ];

    public static UpscaleModelInfo? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Official.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>模型展示名；未知 id 直接回显 id。</summary>
    public static string DisplayName(string? id) => Find(id)?.DisplayName ?? (id ?? "未知模型");
}
