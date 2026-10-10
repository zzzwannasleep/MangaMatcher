using System.Text.Json;
using System.Text.Json.Serialization;

namespace KomgaScraper.Core.Sources;

/// <summary>
/// MangaDex 结果条目（GET /manga 展开后的归一化模型）。
/// 标题挑选规则：title/altTitles 是多语言字典——首选 zh-Hans，其次 ja/en 首个；
/// 简介取 description（en/ja 齐全，中文少见，刮削时走 AI 翻译链路）。
/// </summary>
public sealed class MangaDexSeries
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string OriTitle { get; init; } = "";
    public List<string> AltTitles { get; init; } = [];
    public string Description { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public string Status { get; init; } = "";
    public string OriginalLanguage { get; init; } = "";
    public List<string> Tags { get; init; } = [];
}

/// <summary>
/// MangaDex 客户端 —— 英文圈图源（免密钥公开 API，api.mangadex.org/docs）。
/// 搜索：GET /manga?title={词}&amp;limit={n}&amp;includes[]=cover_art（title 匹配主标题+别名，
/// 中文别名也能命中，实测"海贼王"搜到 One Piece）；封面：uploads.mangadex.org/covers/{id}/{file}。
/// 接口国外可直连，但用户环境常需代理（与 Bangumi/封面下载同路）。
/// </summary>
public sealed class MangaDexClient : IDisposable
{
    public const string DefaultBase = "https://api.mangadex.org";
    public const string CoverBase = "https://uploads.mangadex.org/covers";

    private const string UA = "KomgaScraper/1.0 (metadata scraper)";

    private readonly HttpClient _http;
    private readonly string _base;

    public MangaDexClient(string? baseUrl = null, ProxyConfig? proxy = null, IAppLogger? log = null)
    {
        _base = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl).TrimEnd('/');
        var lg = log ?? NullLogger.Instance;
        _http = HttpFactory.Create(proxy ?? ProxyConfig.None, TimeSpan.FromSeconds(20), UA, warn: lg.Warn);
    }

    private sealed class MdResponse
    {
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("data")] public List<MdManga>? Data { get; set; }
    }

    private sealed class MdManga
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("attributes")] public MdAttributes? Attributes { get; set; }
        [JsonPropertyName("relationships")] public List<MdRelationship>? Relationships { get; set; }
    }

    private sealed class MdAttributes
    {
        [JsonPropertyName("title")] public Dictionary<string, string?>? Title { get; set; }
        [JsonPropertyName("altTitles")] public List<Dictionary<string, string?>>? AltTitles { get; set; }
        [JsonPropertyName("description")] public Dictionary<string, string?>? Description { get; set; }
        [JsonPropertyName("originalLanguage")] public string? OriginalLanguage { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("tags")] public List<MdTag>? Tags { get; set; }
    }

    private sealed class MdRelationship
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("attributes")] public MdCoverAttributes? Attributes { get; set; }
    }

    private sealed class MdCoverAttributes
    {
        [JsonPropertyName("fileName")] public string? FileName { get; set; }
    }

    private sealed class MdTag
    {
        [JsonPropertyName("attributes")] public MdTagAttributes? Attributes { get; set; }
    }

    private sealed class MdTagAttributes
    {
        [JsonPropertyName("name")] public Dictionary<string, string?>? Name { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>按关键词搜索（title 匹配主标题与别名）；异常抛出，调用方自行兜底。
    /// 日漫过滤开时加 originalLanguage[]=ja 服务端限定（韩漫/美漫/国漫条目不进候选）。</summary>
    public async Task<List<MangaDexSeries>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return [];
        var url = $"{_base}/manga?title={Uri.EscapeDataString(keyword)}&limit={Math.Clamp(limit, 1, 100)}" +
                  "&includes%5B%5D=cover_art&contentRating%5B%5D=safe&contentRating%5B%5D=suggestive&contentRating%5B%5D=erotica";
        if (OriginFilter.JapaneseOnly)
            url += "&originalLanguage%5B%5D=ja";
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MangaDex 搜索失败 {(int)resp.StatusCode}: {keyword}");
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<MdResponse>(json, JsonOpts);
        return (parsed?.Data ?? []).Select(ToSeries).Where(s => s.Id.Length > 0).ToList();
    }

    /// <summary>按 id 取单部详情（选中候选后刮削用）。</summary>
    public async Task<MangaDexSeries?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var url = $"{_base}/manga/{Uri.EscapeDataString(id)}?includes%5B%5D=cover_art";
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MangaDex 详情失败 {(int)resp.StatusCode}: {id}");
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<MdResponse>(json, JsonOpts);
        var data = parsed?.Data?.FirstOrDefault();
        return data == null ? null : ToSeries(data);
    }

    /// <summary>下载封面（uploads CDN 公开直连，无防盗链；走本客户端与 API 同一代理）。</summary>
    public async Task<byte[]> DownloadCoverAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载 MangaDex 封面失败 {(int)resp.StatusCode}: {url}");
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static MangaDexSeries ToSeries(MdManga d)
    {
        var attr = d.Attributes;
        var titleDict = attr?.Title ?? [];
        var oriLang = attr?.OriginalLanguage ?? "";

        // 标题：zh-Hans > zh-Hant > 原文语言 > 首个
        string Pick(Dictionary<string, string?> dict, params string[] keys)
        {
            foreach (var k in keys)
                if (dict.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v!;
            return "";
        }
        var title = Pick(titleDict, "zh-Hans", "zh-Hant", oriLang);
        if (title.Length == 0) title = Pick(titleDict, "ja", "en");
        if (title.Length == 0) title = titleDict.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

        var ori = Pick(titleDict, oriLang.Length > 0 ? oriLang : "ja");
        if (ori.Length == 0) ori = Pick(titleDict, "ja", "en");

        // 别名：所有 altTitles 的值去重（排除已选标题/原名）
        var alts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alt in attr?.AltTitles ?? [])
        {
            foreach (var v in alt.Values)
            {
                if (string.IsNullOrWhiteSpace(v) || !seen.Add(v!)) continue;
                if (v == title || v == ori) continue;
                if (alts.Count < 8) alts.Add(v!);
            }
        }

        // 简介：en > ja > 首个非空
        var desc = attr?.Description ?? [];
        var description = Pick(desc, "en", "ja");
        if (description.Length == 0) description = desc.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

        // 封面：cover_art 关系 → uploads.mangadex.org/covers/{id}/{file}
        var coverRel = (d.Relationships ?? []).FirstOrDefault(r => r.Type == "cover_art");
        var coverFile = coverRel?.Attributes?.FileName ?? "";
        var coverUrl = coverFile.Length > 0 ? $"{CoverBase}/{d.Id}/{coverFile}" : "";

        // 标签：tag 关系的 name.en（英文圈标签，如 Action/Fantasy）
        var tags = (attr?.Tags ?? [])
            .Select(t => t.Attributes?.Name)
            .Select(n => Pick(n ?? [], "en"))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MangaDexSeries
        {
            Id = d.Id ?? "",
            Title = title,
            OriTitle = ori,
            AltTitles = alts,
            Description = description,
            CoverUrl = coverUrl,
            Status = attr?.Status ?? "",
            OriginalLanguage = oriLang,
            Tags = tags,
        };
    }

    public void Dispose() => _http.Dispose();
}
