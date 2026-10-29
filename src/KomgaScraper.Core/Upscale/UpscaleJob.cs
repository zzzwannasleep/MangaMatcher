using System.Diagnostics;

namespace KomgaScraper.Core.Upscale;

/// <summary>超分前的一页输入（来自解压会话目录，顺序就是最终 CBZ 的页序）。</summary>
public sealed record UpscalePageInput(string FilePath, string Name, long Bytes, int Width, int Height);

/// <summary>超分设置（与模型无关的部分）。</summary>
public sealed class UpscaleOptions
{
    /// <summary>放大倍率。必须落在所选模型的 <see cref="UpscaleModelInfo.Scales"/> 里。</summary>
    public int Scale { get; init; } = 4;

    /// <summary>ncnn 瓦片大小，0 = 自动（显存不足时调小）。</summary>
    public int TileSize { get; init; }

    /// <summary>TTA 模式：质量更好但慢数倍。</summary>
    public bool Tta { get; init; }

    /// <summary>Vulkan 设备编号；null = 引擎默认（不推荐，可能是核显）。</summary>
    public int? GpuIndex { get; init; }

    /// <summary>输出长边上限（像素，0 = 不限制）。</summary>
    public int MaxLongSide { get; init; }

    /// <summary>WebP 质量；用户要求固定 95，保留参数只为测试。</summary>
    public int WebpQuality { get; init; } = WebpTranscoder.DefaultQuality;

    /// <summary>单页失败时跳过继续；false = 中止整次任务。</summary>
    public bool SkipFailedPages { get; init; }

    /// <summary>输出同名文件时直接覆盖；false = 自动改名 (2)、(3)…</summary>
    public bool Overwrite { get; init; }

    /// <summary>完整输出路径（含文件名）。null = 用标题 + <see cref="UpscaleMetadata.Title"/> 自动命名。</summary>
    public string? OutputPath { get; init; }
}

public sealed record UpscaleProgressInfo(
    int Done,
    int Total,
    string Message,
    TimeSpan? Eta,
    int LastWidth,
    int LastHeight);

public sealed record UpscaleResult(
    string OutputPath,
    int Pages,
    int Skipped,
    long InputBytes,
    long OutputBytes,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings);

public sealed class UpscaleSetupException(string message) : Exception(message);

public sealed class UpscaleJobException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// 超分任务：逐页调用引擎 → 转 WebP q95（可选长边限制）→ 写带 ComicInfo.xml 的 CBZ。
/// 单页照片/整部漫画都走同一条流水线；中间 PNG 用完即删，避免几 GB 的临时文件堆积。
/// </summary>
public sealed class UpscaleJob(UpscaleRuntime runtime, IUpscalerRunner runner, IAppLogger log)
{
    /// <summary>单页输出像素上限（约 120MP，4x 大跨页也够用；再大 ImageSharp 会吃光内存）。</summary>
    private const long MaxOutputPixels = 120_000_000;

    /// <summary>按倍率 + 长边上限折算实际输出尺寸（长边上限会先缩小，再判断上限）。</summary>
    public static (long Width, long Height) EffectiveOutputSize(int width, int height, int scale, int maxLongSide)
    {
        long w = Math.Max(1, (long)width * scale);
        long h = Math.Max(1, (long)height * scale);
        if (maxLongSide > 0)
        {
            var longSide = Math.Max(w, h);
            if (longSide > maxLongSide)
            {
                var ratio = (double)maxLongSide / longSide;
                w = Math.Max(1, (long)Math.Round(w * ratio));
                h = Math.Max(1, (long)Math.Round(h * ratio));
            }
        }
        return (w, h);
    }

    public async Task<UpscaleResult> RunAsync(
        IReadOnlyList<UpscalePageInput> pages,
        UpscaleModelInfo model,
        UpscaleOptions options,
        UpscaleMetadata meta,
        string outputDirectory,
        IProgress<UpscaleProgressInfo>? progress,
        CancellationToken ct)
    {
        if (pages.Count == 0) throw new ArgumentException("没有可超分的页面", nameof(pages));
        if (!model.SupportsScale(options.Scale))
            throw new UpscaleSetupException(
                $"模型「{model.DisplayName}」只支持 {model.ScalesText}，当前选择 {options.Scale}x。" +
                "请换一个倍率，或改选支持该倍率的模型。");
        if (!runtime.EngineReady)
            throw new UpscaleSetupException(
                "超分引擎还没安装。请先点「一键下载引擎 + 模型」，或从下载页手动下载后点「导入引擎包」。");
        if (!runtime.IsModelReady(model, options.Scale))
            throw new UpscaleSetupException(
                $"模型「{model.DisplayName}」的文件缺失：{string.Join("、", runtime.MissingModelFiles(model, options.Scale))}\n" +
                "请重新下载引擎包，或导入自己的模型。");

        // 4x 之后像素翻 16 倍：先按「长边上限」折算实际输出，再判断内存与 WebP 单边硬上限
        var oversize = new List<string>();
        foreach (var page in pages.Where(p => p.Width > 0 && p.Height > 0))
        {
            var (w, h) = EffectiveOutputSize(page.Width, page.Height, options.Scale, options.MaxLongSide);
            if (Math.Max(w, h) > WebpTranscoder.MaxWebpSide)
                oversize.Add($"{page.Name} → {w}×{h}（超过 WebP 单边上限 {WebpTranscoder.MaxWebpSide}）");
            else if (w * h > MaxOutputPixels)
                oversize.Add($"{page.Name} → {w}×{h}（{w * h:N0} 像素，超过 {MaxOutputPixels:N0}）");
        }
        if (oversize.Count > 0)
        {
            throw new UpscaleSetupException(
                $"有 {oversize.Count} 页输出过大，可能耗尽内存或超出 WebP 上限：\n" +
                string.Join("\n", oversize.Take(3)) +
                (oversize.Count > 3 ? $"\n…另 {oversize.Count - 3} 页" : "") +
                "\n\n请把「输出长边上限」设成 6000 以下（推荐 4000～6000），或改用 2x。");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("必须指定输出目录（不能把 CBZ 写进临时会话目录）", nameof(outputDirectory));

        var title = string.IsNullOrWhiteSpace(meta.Title) ? "未命名" : meta.Title.Trim();
        var dir = outputDirectory;
        Directory.CreateDirectory(dir);
        var outputPath = string.IsNullOrWhiteSpace(options.OutputPath)
            ? Path.Combine(dir, ImageOrder.SanitizeFileName(title) + ".cbz")
            : options.OutputPath!;
        if (!options.Overwrite) outputPath = UniquePath(outputPath);

        var work = Path.Combine(Path.GetTempPath(), "KomgaScraper-upscale-job", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var webps = new List<string>(pages.Count);
        var sizes = new List<long>(pages.Count);
        var warnings = new List<string>();
        var skipped = 0;
        var done = 0;
        var lastWidth = 0;
        var lastHeight = 0;
        var total = pages.Count;
        var watch = Stopwatch.StartNew();

        log.Info($"[超分] 开始：{total} 页，模型 {model.DisplayName}，{options.Scale}x，WebP q{options.WebpQuality}" +
                 (options.MaxLongSide > 0 ? $"，长边上限 {options.MaxLongSide}px" : "") +
                 (options.Tta ? "，TTA 模式" : "") +
                 $"，输出 {outputPath}");

        try
        {
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = pages[i];
                var png = Path.Combine(work, i.ToString("D5") + ".png");
                var webp = Path.Combine(work, i.ToString("D5") + ".webp");

                progress?.Report(new UpscaleProgressInfo(done, total,
                    $"第 {i + 1}/{total} 页：{page.Name}" + (page.Width > 0 ? $"（{page.Width}×{page.Height}）" : ""),
                    Eta(watch.Elapsed, done, total), lastWidth, lastHeight));

                try
                {
                    await runner.RunAsync(new UpscaleInvocation(
                        runtime.ExePath, runtime.ModelsDir, model.Id, options.Scale,
                        options.TileSize, options.Tta, options.GpuIndex, page.FilePath, png), null, ct)
                        .ConfigureAwait(false);

                    var (w, h) = WebpTranscoder.Encode(png, webp, options.WebpQuality,
                        options.MaxLongSide, ct);
                    lastWidth = w;
                    lastHeight = h;
                    TryDelete(png);

                    var bytes = new FileInfo(webp).Length;
                    if (bytes == 0) throw new InvalidOperationException("WebP 输出为空");
                    webps.Add(webp);
                    sizes.Add(bytes);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    if (!options.SkipFailedPages)
                        throw new UpscaleJobException(
                            $"第 {i + 1}/{total} 页（{page.Name}）超分失败：{e.Message}\n" +
                            "如果只是个别图片坏了，可在设置里勾选「单页失败时跳过」。", e);
                    skipped++;
                    warnings.Add($"第 {i + 1} 页 {page.Name} 失败已跳过：{e.Message}");
                    log.Warn($"[超分] 跳过第 {i + 1} 页 {page.Name}：{e.Message}");
                    TryDelete(png);
                }

                done++;
                progress?.Report(new UpscaleProgressInfo(done, total,
                    $"已完成 {done}/{total} 页" + (lastWidth > 0 ? $"（最近：{lastWidth}×{lastHeight}）" : ""),
                    Eta(watch.Elapsed, done, total), lastWidth, lastHeight));
            }

            if (webps.Count == 0)
                throw new UpscaleJobException("所有页面都处理失败了，没有生成任何图片。");

            var notes = BuildNotes(model, options, skipped);
            var comicInfo = ComicInfoXml.Build(meta, sizes, notes);
            await CbzWriter.WriteAsync(outputPath, webps, comicInfo, ct).ConfigureAwait(false);

            watch.Stop();
            var outputBytes = new FileInfo(outputPath).Length;
            var inputBytes = pages.Sum(p => p.Bytes);
            log.Info($"[超分] 完成：{webps.Count} 页，{ComicArchiveReader.FormatBytes(inputBytes)} → " +
                     $"{ComicArchiveReader.FormatBytes(outputBytes)}，用时 {FormatDuration(watch.Elapsed)}；{outputPath}");
            if (skipped > 0) log.Warn($"[超分] 有 {skipped} 页失败被跳过，见日志。");

            return new UpscaleResult(outputPath, webps.Count, skipped, inputBytes, outputBytes,
                watch.Elapsed, warnings);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private static string BuildNotes(UpscaleModelInfo model, UpscaleOptions options, int skipped)
    {
        var parts = new List<string>
        {
            $"由 MangaMatcher 超分导出（Real-ESRGAN ncnn-vulkan，模型 {model.Id}，×{options.Scale}，WebP q{options.WebpQuality}）",
        };
        if (model.Kind == UpscaleModelKind.Custom) parts.Add("自定义模型");
        if (options.MaxLongSide > 0) parts.Add($"长边上限 {options.MaxLongSide}px");
        if (options.Tta) parts.Add("TTA");
        if (skipped > 0) parts.Add($"跳过 {skipped} 页");
        return string.Join("；", parts);
    }

    private static TimeSpan? Eta(TimeSpan elapsed, int done, int total)
    {
        if (done <= 0 || total <= 0) return null;
        var perPage = elapsed.TotalSeconds / done;
        var remaining = total - done;
        if (remaining <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Max(1, perPage * remaining));
    }

    public static string FormatDuration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
        : span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒"
            : $"{Math.Max(1, (int)Math.Round(span.TotalSeconds))} 秒";

    /// <summary>同名文件自动改名为 "name (2).cbz"、"name (3).cbz"…</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({DateTime.Now:HHmmss}){ext}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* 忽略 */ }
    }
}
