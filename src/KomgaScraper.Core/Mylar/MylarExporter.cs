using System.Text.Json;
using System.Text.RegularExpressions;
using KomgaScraper.Core.Komga;

namespace KomgaScraper.Core.Mylar;

/// <summary>
/// Mylar series.json 导出（移植 KomgaMylar 的 export_series_as_mylar_json）：
/// 导出库内系列元数据为 Mylar 格式，保持相对目录结构，可选下载封面。
/// </summary>
public sealed partial class MylarExporter(KomgaClient komga, IAppLogger log)
{
    [GeneratedRegex(@"(?:vol(?:ume)?s?|巻|卷|册|冊)[\W_]*?(\d+)|第\s*(\d+)\s*(?:巻|卷|册|冊|集)|(\d+)\s*(?:巻|卷|册|冊|集)", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeTitleRegex();

    public async Task ExportAsync(string libraryId, string? libraryRoot, bool downloadCovers,
        string? outputDir, CancellationToken ct = default)
    {
        log.Info($"开始导出库 {libraryId} 的系列到 {outputDir ?? "(系列原始目录)"}");
        var seriesList = await komga.ListSeriesInLibraryAsync(libraryId, ct);
        if (seriesList.Count == 0)
        {
            log.Warn("未获取到任何系列，退出");
            return;
        }
        log.Info($"共获取到系列数：{seriesList.Count}");

        var valid = new List<SeriesDto>();
        foreach (var s in seriesList)
        {
            var title = s.Metadata?.Title ?? s.Name;
            var totalBooks = s.Metadata?.TotalBookCount ?? (int?)s.BooksCount;
            if (totalBooks is null or <= 0)
            {
                log.Warn($"[跳过] 系列 '{title}' 书籍数异常: totalBookCount={s.Metadata?.TotalBookCount} booksCount={s.BooksCount}");
                continue;
            }
            valid.Add(s);
        }
        log.Info($"过滤后有效系列数：{valid.Count}");

        int exported = 0;
        foreach (var series in valid)
        {
            ct.ThrowIfCancellationRequested();
            var metadata = series.Metadata;
            var title = metadata?.Title ?? series.Name;
            var komgaStatus = (metadata?.Status ?? "").ToUpperInvariant();
            var mylarStatus = komgaStatus switch
            {
                "ONGOING" => "Continuing",
                "HIATUS" => "Continuing",
                "ABANDONED" => "Continuing",
                "ENDED" => "Ended",
                _ => "Continuing",
            };

            var seriesLocalPath = series.Url;
            if (string.IsNullOrEmpty(seriesLocalPath))
            {
                log.Warn($"[跳过] 系列 '{title}' 缺少目录信息");
                continue;
            }

            var urlPath = new DirectoryInfo(seriesLocalPath.Replace('/', Path.DirectorySeparatorChar));
            string? seriesFileStem;
            string seriesDir;
            if (series.Oneshot)
            {
                seriesDir = urlPath.Parent?.FullName ?? urlPath.FullName;
                seriesFileStem = Path.GetFileNameWithoutExtension(urlPath.FullName);
            }
            else
            {
                seriesDir = urlPath.FullName;
                seriesFileStem = null;
            }

            string outputSeriesDir;
            if (!string.IsNullOrEmpty(outputDir))
            {
                if (!string.IsNullOrEmpty(libraryRoot))
                {
                    var libRoot = Path.GetFullPath(libraryRoot);
                    var seriesPath = Path.GetFullPath(seriesDir);
                    if (seriesPath.StartsWith(libRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        var relative = seriesPath[libRoot.Length..].TrimStart(Path.DirectorySeparatorChar, '/');
                        outputSeriesDir = Path.Combine(outputDir, relative);
                    }
                    else
                    {
                        log.Warn($"[跳过] 系列 '{title}' 的路径无法相对于库根目录解析");
                        continue;
                    }
                }
                else
                {
                    outputSeriesDir = Path.Combine(outputDir, Path.GetFileName(seriesDir));
                }
            }
            else
            {
                outputSeriesDir = seriesDir;
            }

            try
            {
                Directory.CreateDirectory(outputSeriesDir);
            }
            catch (Exception e)
            {
                log.Warn($"[跳过] 无法创建目录 {outputSeriesDir}: {e.Message}");
                continue;
            }

            var seriesFile = series.Oneshot
                ? Path.Combine(outputSeriesDir, $"{seriesFileStem}.oneshot.json")
                : Path.Combine(outputSeriesDir, "series.json");

            var mylarData = new Dictionary<string, object?>
            {
                ["version"] = "1.0.2",
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["type"] = "comicSeries",
                    ["publisher"] = metadata?.Publisher ?? "",
                    ["imprint"] = null,
                    ["name"] = title ?? "",
                    ["comicid"] = 9527,
                    ["year"] = 2001,
                    ["description_text"] = metadata?.Summary ?? series.BooksMetadata?.Summary ?? "",
                    ["description_formatted"] = null,
                    ["volume"] = null,
                    ["booktype"] = "Print",
                    ["age_rating"] = NormalizeAgeRating(metadata?.AgeRating),
                    ["collects"] = null,
                    ["comic_image"] = "",
                    ["total_issues"] = metadata?.TotalBookCount ?? 0,
                    ["publication_run"] = "",
                    ["status"] = mylarStatus,
                    ["language"] = metadata?.Language,
                    ["readingDirection"] = metadata?.ReadingDirection,
                    ["releaseDate"] = series.BooksMetadata?.ReleaseDate,
                    ["authors"] = series.BooksMetadata?.Authors,
                    ["links"] = metadata?.Links,
                    ["alternateTitles"] = metadata?.AlternateTitles,
                    ["genres"] = metadata?.Genres,
                    ["tags"] = metadata?.Tags ?? series.BooksMetadata?.Tags,
                },
            };

            var releaseDate = series.BooksMetadata?.ReleaseDate;
            if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4 && releaseDate[..4].All(char.IsDigit))
            {
                ((Dictionary<string, object?>)mylarData["metadata"]!)["year"] = int.Parse(releaseDate[..4]);
            }

            var jsonOpts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            await File.WriteAllTextAsync(seriesFile, JsonSerializer.Serialize(mylarData, jsonOpts), ct);
            log.Info($"✅ 已导出系列 '{title}' 到 {seriesFile}");
            exported++;

            if (downloadCovers)
            {
                var coverPath = series.Oneshot
                    ? Path.Combine(outputSeriesDir, $"{seriesFileStem}.cover.jpg")
                    : Path.Combine(outputSeriesDir, "cover.jpg");
                if (File.Exists(coverPath))
                {
                    log.Info($"封面已存在，跳过下载: {coverPath}");
                }
                else
                {
                    try
                    {
                        var thumb = await komga.GetSeriesThumbnailAsync(series.Id, ct);
                        await File.WriteAllBytesAsync(coverPath, thumb, ct);
                        log.Info($"🖼️ 已保存封面到 {coverPath}");
                    }
                    catch (Exception e)
                    {
                        log.Warn($"[⚠️] 系列 '{title}' 封面下载失败: {e.Message}");
                    }
                }
            }
        }
        log.Info($"导出完成，共 {exported} 个系列");
    }

    /// <summary>Komga 数字分级 → Mylar 分级字符串（对齐脚本 normalize_age_rating）。</summary>
    public static string? NormalizeAgeRating(int? value)
    {
        if (value is null) return null;
        return value.Value switch
        {
            <= 0 => "All",
            < 12 => "9+",
            < 15 => "12+",
            < 17 => "15+",
            < 18 => "17+",
            _ => "Adult",
        };
    }
}