using System.IO.Compression;
using KomgaScraper.Core;
using SixLabors.ImageSharp;

namespace KomgaScraper.Core.Upscale;

/// <summary>解压出来的一页（FilePaths 指向会话临时目录里的真实文件）。</summary>
public sealed record ExtractedPage(
    string RelativePath,
    string FilePath,
    long Bytes,
    int Width,
    int Height)
{
    public string DisplayName => RelativePath.Contains('/')
        ? RelativePath[(RelativePath.LastIndexOf('/') + 1)..]
        : RelativePath;
}

/// <summary>
/// 从漫画压缩包里读出图片页。支持 zip/cbz（内置）与 rar/cbr/7z/cb7/tar（SharpCompress，只读）。
/// 页序在解压后统一按 <see cref="ImageSortMode"/> 重排（默认自然排序），并过滤 __MACOSX 等垃圾。
/// </summary>
public static class ComicArchiveReader
{
    /// <summary>单张图片解压上限（扫描件通常 &lt;50MB；超过基本是异常或 zip bomb）。</summary>
    private const long MaxEntryBytes = 256L * 1024 * 1024;

    /// <summary>整包解压总量上限，防止 zip bomb 写满磁盘。</summary>
    private const long MaxTotalBytes = 20L * 1024 * 1024 * 1024;

    private static readonly string[] Extensions =
        [".zip", ".cbz", ".rar", ".cbr", ".7z", ".cb7", ".tar"];

    public static bool IsSupportedArchive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    public static string SupportedFormatsText => "zip / cbz / rar / cbr / 7z / cb7 / tar";

    /// <summary>
    /// 解压所有图片到 <paramref name="destDir"/>，返回按 <paramref name="sortMode"/> 排好序的页列表。
    /// 解压失败（加密/损坏）会抛出带中文说明的异常。
    /// </summary>
    public static async Task<IReadOnlyList<ExtractedPage>> ExtractAsync(
        string archivePath,
        string destDir,
        ImageSortMode sortMode,
        IAppLogger log,
        CancellationToken ct,
        Action<int, int, string>? progress = null)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"压缩包不存在：{archivePath}", archivePath);
        if (!IsSupportedArchive(archivePath))
            throw new NotSupportedException(
                $"不支持的压缩包格式（{Path.GetExtension(archivePath)}）。当前支持：{SupportedFormatsText}。" +
                "如果是分卷压缩包，请先合并成一个再导入。");

        Directory.CreateDirectory(destDir);
        var pages = new List<ExtractedPage>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            switch (Path.GetExtension(archivePath).ToLowerInvariant())
            {
                case ".zip":
                case ".cbz":
                    await ExtractZipAsync(archivePath, destDir, used, pages, log, ct, progress).ConfigureAwait(false);
                    break;
                default:
                    await ExtractSharpCompressAsync(archivePath, destDir, used, pages, log, ct, progress).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;   // 自己的大小上限/坏包提示已经说清楚了，不要再套一层
        }
        catch (IOException e)
        {
            throw new IOException(
                $"解压失败：写入临时目录出错（磁盘空间不足或目录不可写？）：{e.Message}", e);
        }
        catch (Exception e) when (e is not NotSupportedException and not FileNotFoundException)
        {
            throw new InvalidDataException(
                $"读取压缩包失败：{e.Message}\n" +
                "常见原因：压缩包已加密、分卷不完整、或文件损坏。请先在解压软件里试解一次。", e);
        }

        if (pages.Count == 0)
        {
            throw new InvalidDataException(
                $"压缩包里没有找到图片（支持 {string.Join(" / ", new[] { "jpg", "jpeg", "png", "webp", "bmp", "gif", "tif" })}）。\n" +
                "请确认导入的是漫画压缩包，而不是把图片放在二级文件夹之外的其他文件。");
        }

        // 读尺寸并剔除无法识别的图片（ncnn 会直接失败，不如提前说清楚）
        var valid = new List<ExtractedPage>(pages.Count);
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = Image.Identify(page.FilePath);
                if (info.Width <= 0 || info.Height <= 0)
                {
                    log.Warn($"[超分] 跳过尺寸异常的图片：{page.RelativePath}");
                    continue;
                }
                valid.Add(page with { Width = info.Width, Height = info.Height });
            }
            catch (Exception e)
            {
                log.Warn($"[超分] 跳过无法识别的图片：{page.RelativePath}（{e.Message}）");
                TryDelete(page.FilePath);
            }
        }

        if (valid.Count == 0)
            throw new InvalidDataException("压缩包里的图片都无法识别（可能全部损坏或不是图片文件）。");

        var sorted = ImageOrder.Sort(valid, p => p.RelativePath, sortMode);
        log.Info($"[超分] 已解压 {sorted.Count} 页（共 {FormatBytes(sorted.Sum(p => p.Bytes))}），" +
                 (sortMode == ImageSortMode.NaturalName ? "按文件名自然排序" : "按压缩包内原始顺序"));
        return sorted;
    }

    private static async Task ExtractZipAsync(
        string archivePath,
        string destDir,
        HashSet<string> used,
        List<ExtractedPage> pages,
        IAppLogger log,
        CancellationToken ct,
        Action<int, int, string>? progress)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var entries = zip.Entries;
        var total = entries.Count(e => !IsDirEntry(e.FullName) && IsImageEntry(e.FullName));
        var done = 0;
        long totalBytes = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (IsDirEntry(entry.FullName)) continue;
            if (!TryPrepare(entry.FullName, destDir, used, out var relative, out var dest)) continue;

            var declared = entry.Length;
            if (declared > MaxEntryBytes)
                throw new InvalidDataException(
                    $"压缩包里的单张图片过大（{FormatBytes(declared)}），超过上限 {FormatBytes(MaxEntryBytes)}。" +
                    "请确认压缩包内容，或先解压后重新打包。");

            progress?.Invoke(Math.Max(1, Math.Min(done + 1, Math.Max(1, total))), Math.Max(1, total), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using (var src = entry.Open())
            await using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            var length = new FileInfo(dest).Length;
            CheckEntrySize(length, totalBytes, relative);
            totalBytes += length;
            pages.Add(new ExtractedPage(relative, dest, length, 0, 0));
            done++;
        }
    }

    private static async Task ExtractSharpCompressAsync(
        string archivePath,
        string destDir,
        HashSet<string> used,
        List<ExtractedPage> pages,
        IAppLogger log,
        CancellationToken ct,
        Action<int, int, string>? progress)
    {
        using var archive = SharpCompress.Archives.ArchiveFactory.Open(archivePath);
        var entries = archive.Entries.ToList();
        var total = entries.Count(e => !e.IsDirectory && IsImageEntry(e.Key));
        var done = 0;
        long totalBytes = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;
            var key = entry.Key ?? "";
            if (!TryPrepare(key, destDir, used, out var relative, out var dest)) continue;

            if (entry.Size > MaxEntryBytes)
                throw new InvalidDataException(
                    $"压缩包里的单张图片过大（{FormatBytes(entry.Size)}），超过上限 {FormatBytes(MaxEntryBytes)}。");

            progress?.Invoke(Math.Max(1, Math.Min(done + 1, Math.Max(1, total))), Math.Max(1, total), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using (var src = entry.OpenEntryStream())
            await using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            var length = new FileInfo(dest).Length;
            CheckEntrySize(length, totalBytes, relative);
            totalBytes += length;
            pages.Add(new ExtractedPage(relative, dest, length, 0, 0));
            done++;
        }
    }

    private static bool IsDirEntry(string? name) =>
        name is not null && (name.EndsWith('/') || name.EndsWith('\\'));

    private static bool IsImageEntry(string? name) =>
        ImageOrder.TryNormalizeRelativePath(name, out var normalized) &&
        !ImageOrder.IsJunk(normalized) &&
        ImageOrder.IsImageFile(normalized);

    private static void CheckEntrySize(long length, long totalBytes, string relative)
    {
        if (length > MaxEntryBytes)
            throw new InvalidDataException(
                $"压缩包里的单张图片过大（{relative}：{FormatBytes(length)}），超过上限 {FormatBytes(MaxEntryBytes)}。");
        if (totalBytes + length > MaxTotalBytes)
            throw new InvalidDataException(
                $"解压总量超过上限 {FormatBytes(MaxTotalBytes)}（到 {relative} 时已超）。" +
                "压缩包可能是异常文件（zip bomb），请检查内容。");
    }

    /// <summary>校验路径安全 + 过滤垃圾 + 处理重名；返回 false 表示这条记录直接忽略。</summary>
    private static bool TryPrepare(string rawName, string destDir, HashSet<string> used,
        out string relative, out string dest)
    {
        relative = "";
        dest = "";
        if (!ImageOrder.TryNormalizeRelativePath(rawName, out var normalized))
            return false;
        if (ImageOrder.IsJunk(normalized))
            return false;
        if (!ImageOrder.IsImageFile(normalized))
            return false;

        // 同一路径（含大小写重名）在 Windows 上会互相覆盖 → 自动加序号，保证一页都不丢
        var candidate = normalized;
        if (!used.Add(candidate))
        {
            var dir = Path.GetDirectoryName(candidate)?.Replace('\\', '/');
            var stem = Path.GetFileNameWithoutExtension(candidate);
            var ext = Path.GetExtension(candidate);
            var n = 2;
            do
            {
                var name = $"{stem}_{n}{ext}";
                candidate = string.IsNullOrEmpty(dir) ? name : $"{dir}/{name}";
                n++;
            } while (n < 1000 && !used.Add(candidate));
        }

        relative = candidate;
        var full = Path.GetFullPath(Path.Combine(destDir, candidate.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;   // 双保险：即使 normalize 漏了，也绝不写出目标目录
        dest = full;
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
