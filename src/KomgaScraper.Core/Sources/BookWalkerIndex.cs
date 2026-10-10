using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using KomgaScraper.Core.Text;

namespace KomgaScraper.Core.Sources;

/// <summary>BookWalker 单卷（顶层 product）：卷名 / 发售日 / ISBN / 独立封面 / 独立简介。</summary>
public sealed record BookWalkerVolume(
    int Order,
    string Title,
    string ReleaseDate,
    string Isbn,
    string CoverName,
    string Summary)
{
    /// <summary>除了卷号之外没有任何有用数据。</summary>
    public bool IsEmpty =>
        string.IsNullOrEmpty(ReleaseDate) && string.IsNullOrEmpty(Isbn) &&
        string.IsNullOrEmpty(CoverName) && string.IsNullOrEmpty(Summary);
}

public sealed record BookWalkerAuthor(string Name, int Role); // 1=作者 2=作画

/// <summary>BookWalker 系列条目（来自官方目录数据库导出的索引）。</summary>
public sealed record BookWalkerSeries(
    string Id,               // CNT_xxx（BookWalker 内部 id）
    string Title,            // 主标题（多为英文/日文）
    IReadOnlyList<string> AltTitles,
    string Description,
    string Publisher,
    string Imprint,          // 出版品牌（labels.name，如 VIZ Media / KADOKAWA MANGA）
    IReadOnlyList<BookWalkerAuthor> Authors,
    IReadOnlyList<string> CoverUrls,   // 多 CDN 候选
    string SeriesUrl,        // https://bookwalker.jp/series/{n}/
    int VolumeCount,         // 顶层卷数（不含分话）
    bool IsAdult,
    IReadOnlyList<string> Genres,      // tags.namespace=1（Shounen / Action / Romance…）
    IReadOnlyList<string> Themes,      // tags.namespace=2（Prince / Military / War…）
    bool Complete,           // tags.namespace=3 含 complete
    int Type,                // series.type：1=漫画 2=小说 3/4=其他
    IReadOnlyList<BookWalkerVolume> Volumes)
{
    /// <summary>series.type = 1：漫画（阅读方向按从右往左）。</summary>
    public bool IsComic => Type == 1;

    /// <summary>series.type = 2：小说 / 轻小说。</summary>
    public bool IsNovel => Type == 2;
}

/// <summary>
/// BookWalker 本地离线索引（bkwk-index.json.gz，由 BookWalkerService 从官方目录库生成）。
/// 标题 + 别名做规范化精确匹配；数据为全球站目录（英/日文）。
///
/// 索引格式：v2 = <c>{"v":2,"e":[...]}</c>（含每卷数据与标签）；
/// v1 = 裸数组（旧版，缺每卷/标签数据，仍可加载但会提示重建）。
/// </summary>
public sealed class BookWalkerIndex
{
    /// <summary>当前索引格式版本。</summary>
    public const int CurrentFormatVersion = 2;

    public sealed class BwAuthorDto
    {
        [JsonPropertyName("n")] public string Name { get; set; } = "";
        [JsonPropertyName("r")] public int Role { get; set; }
    }

    public sealed class BwVolumeDto
    {
        [JsonPropertyName("o")] public int Order { get; set; }
        [JsonPropertyName("t")] public string? Title { get; set; }
        [JsonPropertyName("d")] public string? Date { get; set; }      // yyyy-MM-dd
        [JsonPropertyName("i")] public string? Isbn { get; set; }
        [JsonPropertyName("c")] public string? Cover { get; set; }     // 与系列封面不同时才有
        [JsonPropertyName("s")] public string? Summary { get; set; }   // 与系列简介不同时才有
    }

    public sealed class BwEntryDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("t")] public string Title { get; set; } = "";
        [JsonPropertyName("a")] public List<string>? AltTitles { get; set; }
        [JsonPropertyName("d")] public string Description { get; set; } = "";
        [JsonPropertyName("p")] public string Publisher { get; set; } = "";
        [JsonPropertyName("c")] public string CoverName { get; set; } = "";
        [JsonPropertyName("v")] public int VolumeCount { get; set; }
        [JsonPropertyName("s")] public string SeriesNumId { get; set; } = "";
        [JsonPropertyName("au")] public List<BwAuthorDto>? Authors { get; set; }
        [JsonPropertyName("x")] public int Adult { get; set; }

        // ---- v2 新增 ----
        [JsonPropertyName("ty")] public int Type { get; set; }
        [JsonPropertyName("g")] public List<string>? Genres { get; set; }
        [JsonPropertyName("th")] public List<string>? Themes { get; set; }
        [JsonPropertyName("cp")] public int Complete { get; set; }
        [JsonPropertyName("lb")] public string? Label { get; set; }
        [JsonPropertyName("vol")] public List<BwVolumeDto>? Volumes { get; set; }
    }

    private readonly Dictionary<string, BookWalkerSeries> _byNormTitle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BookWalkerSeries> _byId = new(StringComparer.Ordinal);

    public int Count { get; private set; }

    /// <summary>索引里的总卷数（顶层 product）。</summary>
    public int VolumeTotal { get; private set; }

    /// <summary>文件里记录的格式版本。</summary>
    public int FormatVersion { get; private set; } = 1;

    /// <summary>旧格式（v1）索引：没有每卷数据与类型/题材标签。</summary>
    public bool IsLegacyFormat => FormatVersion < CurrentFormatVersion;

    public static string NormalizeKey(string title) => TitleParser.ExtractAndNormalizeTitle(title);

    /// <summary>封面候选 URL（c.bookwalker.jp 优先，CloudFront 兜底）。</summary>
    public static IReadOnlyList<string> BuildCoverUrls(string coverName) =>
        string.IsNullOrEmpty(coverName)
            ? []
            : [$"https://c.bookwalker.jp/{coverName}", $"https://d2snwnmzyr8jue.cloudfront.net/{coverName}"];

    public BookWalkerIndex(string gzPath)
    {
        using var fs = File.OpenRead(gzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz);

        JsonElement array;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            array = doc.RootElement;               // v1：裸数组
            FormatVersion = 1;
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                 doc.RootElement.TryGetProperty("e", out var e))
        {
            array = e;
            FormatVersion = doc.RootElement.TryGetProperty("v", out var v) && v.TryGetInt32(out var ver)
                ? ver : CurrentFormatVersion;
        }
        else
        {
            throw new InvalidDataException("BookWalker 索引格式无法识别，请在设置里重新构建索引。");
        }

        var entries = array.Deserialize<List<BwEntryDto>>() ?? [];

        foreach (var e in entries)
        {
            var covers = BuildCoverUrls(e.CoverName);
            var volumes = new List<BookWalkerVolume>(e.Volumes?.Count ?? 0);
            foreach (var v in e.Volumes ?? [])
                volumes.Add(new BookWalkerVolume(
                    v.Order, v.Title ?? "", v.Date ?? "", v.Isbn ?? "", v.Cover ?? "", v.Summary ?? ""));

            var bw = new BookWalkerSeries(
                e.Id, e.Title,
                e.AltTitles ?? [],
                e.Description ?? "",
                e.Publisher ?? "",
                e.Label ?? "",
                (e.Authors ?? []).Select(a => new BookWalkerAuthor(a.Name, a.Role)).ToList(),
                covers,
                !string.IsNullOrEmpty(e.SeriesNumId) ? $"https://bookwalker.jp/series/{e.SeriesNumId}/" : "",
                e.VolumeCount,
                e.Adult == 1,
                e.Genres ?? [],
                e.Themes ?? [],
                e.Complete == 1,
                e.Type,
                volumes);

            _byId[e.Id] = bw;
            AddKey(NormalizeKey(bw.Title), bw);
            foreach (var alt in bw.AltTitles) AddKey(NormalizeKey(alt), bw);
        }

        Count = entries.Count;
        VolumeTotal = entries.Sum(x => x.Volumes?.Count ?? 0);
    }

    private void AddKey(string key, BookWalkerSeries bw)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!_byNormTitle.ContainsKey(key)) _byNormTitle[key] = bw;
    }

    public BookWalkerSeries? FindById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>全部条目（诊断/测试用）。</summary>
    public IEnumerable<BookWalkerSeries> All => _byId.Values;

    /// <summary>模糊查找（规范化标题互含），供候选面板展示；最多返回 max 条。</summary>
    public List<BookWalkerSeries> SearchFuzzy(string term, int max)
    {
        var key = NormalizeKey(term);
        if (key.Length < 2) return [];
        var result = new List<BookWalkerSeries>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in _byId.Values)
        {
            if (seen.Contains(kv.Id)) continue;
            var hit = NormalizeKey(kv.Title).Contains(key, StringComparison.Ordinal);
            if (!hit)
                foreach (var alt in kv.AltTitles)
                    if (NormalizeKey(alt).Contains(key, StringComparison.Ordinal)) { hit = true; break; }
            if (hit)
            {
                seen.Add(kv.Id);
                result.Add(kv);
                if (result.Count >= max) break;
            }
        }
        return result;
    }

    /// <summary>按标题（含别名）规范化精确查找；主词未命中时依次尝试备选词。</summary>
    public BookWalkerSeries? FindExact(params string[] titles)
    {
        foreach (var t in titles)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (_byNormTitle.TryGetValue(NormalizeKey(t), out var bw)) return bw;
        }
        return null;
    }

    /// <summary>转成候选面板用的候选条目。</summary>
    public SearchCandidate ToCandidate(BookWalkerSeries bw) => new(
        0,
        bw.Title,
        bw.Title,
        bw.Authors.FirstOrDefault(a => a.Role == 1)?.Name
            ?? bw.Authors.FirstOrDefault(a => a.Role == 2)?.Name ?? "",
        string.Join(" / ", bw.AltTitles.Take(5)),
        bw.CoverUrls.FirstOrDefault(),
        SourceBw, bw.Id);

    public const string SourceBw = "Bw";
}
