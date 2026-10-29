using System.IO.Compression;

namespace KomgaScraper.Core.Upscale;

/// <summary>
/// 写 CBZ：ComicInfo.xml + 按页序命名的 WebP（0001.webp…）。
/// 先写 .part 再原子改名，失败不会留下半成品；WebP 已压缩，条目用 Stored 更快。
/// </summary>
public static class CbzWriter
{
    public static async Task WriteAsync(
        string outputPath,
        IReadOnlyList<string> imagePaths,
        string comicInfoXml,
        CancellationToken ct = default)
    {
        if (imagePaths.Count == 0) throw new ArgumentException("没有可写入的图片", nameof(imagePaths));

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = outputPath + ".part";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                var info = zip.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal);
                await using (var s = info.Open())
                {
                    var bytes = ComicInfoXml.ToUtf8Bytes(comicInfoXml);
                    await s.WriteAsync(bytes, ct).ConfigureAwait(false);
                }

                var digits = Math.Max(4, imagePaths.Count.ToString().Length);
                for (var i = 0; i < imagePaths.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry((i + 1).ToString($"D{digits}") + ".webp",
                        CompressionLevel.NoCompression);
                    await using var src = new FileStream(imagePaths[i], FileMode.Open, FileAccess.Read,
                        FileShare.Read, 81920, true);
                    await using var dst = entry.Open();
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }
            }

            File.Move(tmp, outputPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略清理失败 */ }
        }
    }
}
