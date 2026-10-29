using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace KomgaScraper.Core.Upscale;

/// <summary>
/// 超分输出转 WebP（用户指定：质量 95）。
/// ncnn 便携版的 webp 是**无损**编码，体积降不下来，所以这里统一先出 PNG，
/// 再用 ImageSharp 按有损 q95 重编码；可选「长边上限」在编码前做一次 Lanczos 缩小。
/// </summary>
public static class WebpTranscoder
{
    public const int DefaultQuality = 95;

    /// <summary>把 PNG/任意图片转成有损 WebP，返回最终像素尺寸。</summary>
    public static (int Width, int Height) Encode(
        string sourcePath,
        string destPath,
        int quality = DefaultQuality,
        int maxLongSide = 0,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"待压缩的图片不存在：{sourcePath}", sourcePath);

        quality = Math.Clamp(quality, 1, 100);

        using var image = Image.Load(sourcePath);
        ct.ThrowIfCancellationRequested();

        if (maxLongSide > 0)
        {
            var longSide = Math.Max(image.Width, image.Height);
            if (longSide > maxLongSide)
            {
                var ratio = (double)maxLongSide / longSide;
                var w = Math.Max(1, (int)Math.Round(image.Width * ratio));
                var h = Math.Max(1, (int)Math.Round(image.Height * ratio));
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(w, h),
                    Sampler = KnownResamplers.Lanczos3,
                    Compand = true,
                }));
                ct.ThrowIfCancellationRequested();
            }
        }

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        image.SaveAsWebp(destPath, new WebpEncoder
        {
            Quality = quality,
            FileFormat = WebpFileFormatType.Lossy,
        });
        return (image.Width, image.Height);
    }
}
