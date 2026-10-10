using KomgaScraper.Core.Komga;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace KomgaScraper.Core.Scraping;

/// <summary>
/// 封面过大(413)自动压缩器。
/// 历史背景：服务器（反代）对上传体积有限制，网络源"首选"封面常 >1MB → 413，
/// 旧逻辑只能换更小尺寸 URL，冷门作品小尺寸也没有 → 整个封面失败。
/// 方案（用户指定，2026-10-10）：转 WebP，质量从 95 分级降到 80（下限 80）；
/// 质量到底仍超限 → 缩小分辨率再压一轮。服务器实测接受 image/webp（HTTP 200，2026-10-10 探针）。
/// </summary>
public static class CoverCompressor
{
    /// <summary>
    /// 把 original 的候选压缩档逐档交给 upload 上传（返回 false = 仍 413，换下一档）。
    /// 压缩档：WebP q95/90/85/80 → 缩放 60% 再 q95/80。任何一档成功即停。
    /// 返回是否成功上传。
    /// </summary>
    public static async Task<bool> CompressAndUploadAsync(
        byte[] original, Func<byte[], Task<bool>> upload, IAppLogger log, string label, CancellationToken ct)
    {
        foreach (var (quality, scale, data) in Candidates(original))
        {
            if (data.Length <= 0) continue;
            bool ok;
            try
            {
                ok = await upload(data).ConfigureAwait(false);
            }
            catch (KomgaApiException e) when ((int)e.Status == 413)
            {
                ok = false;
            }
            if (ok)
            {
                log.Info($"{label} 封面已压缩上传成功（WebP q{quality}" +
                         (scale < 1 ? $"，缩放 {scale:P0}" : "") +
                         $"，{data.Length / 1024.0:F0}KB ← 原图 {original.Length / 1024.0:F0}KB）");
                return true;
            }
            log.Info($"{label} WebP q{quality}{(scale < 1 ? $"（缩放 {scale:P0}）" : "")} 仍过大({data.Length / 1024.0:F0}KB)，降级…");
        }
        return false;
    }

    /// <summary>惰性生成压缩候选（真到需要时才算下一档，不浪费编码时间）。</summary>
    private static IEnumerable<(int Quality, double Scale, byte[] Data)> Candidates(byte[] original)
    {
        foreach (var q in new[] { 95, 90, 85, 80 })
            yield return (q, 1.0, EncodeWebp(original, q, 1.0));
        foreach (var q in new[] { 95, 80 })
            yield return (q, 0.6, EncodeWebp(original, q, 0.6));
    }

    /// <summary>编码为有损 WebP（quality 1-100；scale<1 时按比例缩小分辨率）。供压缩链与测试复用。</summary>
    public static byte[] EncodeWebp(byte[] original, int quality, double scale)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load(original);
            if (scale < 1.0)
            {
                var w = Math.Max(1, (int)Math.Round(img.Width * scale));
                var h = Math.Max(1, (int)Math.Round(img.Height * scale));
                img.Mutate(x => x.Resize(w, h));
            }
            using var ms = new MemoryStream();
            img.SaveAsWebp(ms, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder
            {
                Quality = quality,
                FileFormat = SixLabors.ImageSharp.Formats.Webp.WebpFileFormatType.Lossy,
            });
            return ms.ToArray();
        }
        catch (Exception)
        {
            return [];   // 解码失败（坏图/不支持的格式）→ 空档，上层自动跳过
        }
    }
}