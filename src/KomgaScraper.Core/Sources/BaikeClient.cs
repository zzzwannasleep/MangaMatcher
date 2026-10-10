using System.Text.Json;
using System.Text.Json.Serialization;

namespace KomgaScraper.Core.Sources;

/// <summary>百度百科词条基本信息卡字段（card[] 的一项：中文名/原版名称/作画/地区/出版社/类型…）。</summary>
public sealed class BaikeCardField
{
    /// <summary>字段显示名（已去空格转简体，如「作画」「其他出版社」）。</summary>
    public string Name { get; init; } = "";
    /// <summary>字段值（多值合并为顿号串）。</summary>
    public string Value { get; init; } = "";
}

/// <summary>百度百科词条卡片：标题 / 一句话描述 / 摘要 / 概述图 / 词条页 / 基本信息卡。</summary>
public sealed class BaikeCard
{
    public long LemmaId { get; init; }
    /// <summary>词条标题（中文通行名）。</summary>
    public string Title { get; init; } = "";
    /// <summary>一句话描述（如「日本漫画家アタモト作画的漫画」）。</summary>
    public string Desc { get; init; } = "";
    /// <summary>词条摘要（本身是中文，可直接当简介）。</summary>
    public string Abstract { get; init; } = "";
    /// <summary>概述图 URL（bkimg.cdn.bcebos.com；可能为空）。</summary>
    public string ImageUrl { get; init; } = "";
    /// <summary>词条页 URL（已转 https）。</summary>
    public string EntryUrl { get; init; } = "";
    public IReadOnlyList<BaikeCardField> Fields { get; init; } = [];

    /// <summary>按字段名取值（「出版社」「其他出版社」「作者」「作画」「原版名称」「类型」…），没有返回空串。</summary>
    public string Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))?.Value ?? "";

    /// <summary>
    /// 是否为「漫画作品」词条（可安全使用概述图当封面）。
    /// 历史事故（2026-10-10）：自动链路把非漫画词条的概述图刮成了封面——
    /// ① 作者人物词条（齐藤千穗/黑乃奈奈绘/高桥留美子…）：概述图是本人照片；
    /// ② 影视词条（《熊的故事》= 1988 年电影）：概述图是电影海报；
    /// ③ 文学词条（《斗罗大陆》= 唐家三少的小说）：概述图是小说封面。
    /// 判定（保守，宁缺毋错）：信息卡有漫画作品字段 → 是；有人物/影视/文学特征字段 → 否；
    /// 字段稀疏时用摘要兜底（含「漫画家」类称呼且无「作画」 → 人物，否）。
    /// </summary>
    public bool LooksLikeMangaWork
    {
        get
        {
            // 漫画作品词条信息卡的特征字段（有任何一个就视为漫画作品）
            string[] mangaFields = ["出版社", "作画", "原版名称", "连载杂志", "连载期间", "丛书", "单行本册数", "其他出版社"];
            if (Fields.Any(f => mangaFields.Contains(f.Name))) return true;
            // 人物词条特征字段
            string[] personFields = ["职业", "国籍", "出生日期", "逝世日期", "出生地", "毕业院校", "经纪公司", "民族", "出生名"];
            if (Fields.Any(f => personFields.Contains(f.Name))) return false;
            // 影视/游戏词条特征字段（类型字段值也可能暴露：电影/电视剧/动画/游戏）
            string[] mediaFields = ["出品公司", "制片地区", "导演", "主演", "上映日期", "票房", "发行公司", "首播电视台", "编剧", "文学体裁", "创作年代", "出品人", "制片人"];
            if (Fields.Any(f => mediaFields.Contains(f.Name))) return false;
            var typeVal = Field("类型");
            if (typeVal is "电影" or "电视剧" or "动画" or "游戏") return false;
            // 字段稀疏时用摘要兜底：人物词条含「漫画家」类称呼；作品词条描述通常含「作画」
            var text = Desc + Abstract;
            var personWords = new[] { "漫画家", "插画家", "绘师", "声优", "小说家", "作家" };
            if (personWords.Any(text.Contains) && !text.Contains("作画")) return false;
            return true;
        }
    }
}

/// <summary>
/// 百度百科客户端 —— 冷门漫画的最终兜底源（Bangumi/BookWalker/Mox/AI 都没有时）。
/// 搜索：GET /api/openapi/BaikeLemmaCardApi?scope=103&amp;format=json&amp;appid=379020&amp;bk_key={词}
/// （百科搜索框的开放卡片接口，无需密钥），返回词条标题/摘要/概述图/基本信息卡的 JSON。
/// 百科在国内直连即可，不走代理（用户代理走境外代理反而可能绕远）。
/// </summary>
public sealed class BaikeClient : IDisposable
{
    public const string DefaultBase = "https://baike.baidu.com";
    public const string CardApiPath = "/api/openapi/BaikeLemmaCardApi";

    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly IAppLogger _log;

    public BaikeClient(string? baseUrl = null, IAppLogger? log = null)
    {
        _log = log ?? NullLogger.Instance;
        _base = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl).TrimEnd('/');
        _http = HttpFactory.Create(ProxyConfig.None, TimeSpan.FromSeconds(20), UA, warn: _log.Warn);
        try
        {
            _http.DefaultRequestHeaders.Referrer = new Uri(DefaultBase + "/");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }
        catch { /* 极端情况（头已存在）忽略 */ }
    }

    private sealed class CardResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("newLemmaId")] public long? NewLemmaId { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("desc")] public string? Desc { get; set; }
        [JsonPropertyName("abstract")] public string? Abstract { get; set; }
        [JsonPropertyName("image")] public string? Image { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("card")] public List<CardFieldResponse>? Card { get; set; }
    }

    private sealed class CardFieldResponse
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("value")] public List<string?>? Value { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>按关键词取词条卡片；未命中（接口无 title）返回 null，网络错误抛异常。</summary>
    public async Task<BaikeCard?> SearchCardAsync(string keyword, CancellationToken ct = default)
    {
        var kw = keyword.Trim();
        if (kw.Length == 0) return null;
        var url = $"{_base}{CardApiPath}?scope=103&format=json&appid=379020&bk_key={Uri.EscapeDataString(kw)}&bk_length=600";
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        var r = JsonSerializer.Deserialize<CardResponse>(json, JsonOpts);
        var title = (r?.Title ?? "").Trim();
        if (title.Length == 0) return null;   // 未命中：接口只回 errno/errMsg，没有 title
        // 注意：不做 T2S —— 百科正文本身是简体，但「原版名称/作画」等卡片值是日文原文，
        // 繁转简会把 ベモノ戦線異状ナシ 这类原文改坏，原样保留
        return new BaikeCard
        {
            LemmaId = r?.NewLemmaId ?? r?.Id ?? 0,
            Title = title,
            Desc = (r?.Desc ?? "").Trim(),
            Abstract = (r?.Abstract ?? "").Trim(),
            ImageUrl = (r?.Image ?? "").Trim(),
            EntryUrl = ToHttps((r?.Url ?? "").Trim()),
            Fields = [.. (r?.Card ?? []).Select(f => new BaikeCardField
            {
                Name = (f.Name ?? "").Trim().Replace(" ", ""),   // 防御：个别字段名带内嵌空格
                Value = string.Join("、", (f.Value ?? [])
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!.Trim())),
            })],
        };
    }

    private static string ToHttps(string url) =>
        url.StartsWith("http://", StringComparison.Ordinal) ? "https://" + url["http://".Length..] : url;

    /// <summary>
    /// 概述图直链。注意：bkimg CDN 的 x-bce-process 只认 format,f_auto 与 resize 系列，
    /// 不认 format,jpg（实测 400）——历史上把 f_auto 换成 jpg 恰恰是把好 URL 改坏的元凶，现在原样返回。
    /// f_auto 由 Accept 头协商，本客户端不带 image/webp Accept 时 CDN 返回的就是 image/jpeg。
    /// </summary>
    public static string JpegCoverUrl(string imageUrl) => imageUrl;

    /// <summary>
    /// 下载词条图（概述图/封面）。bkimg.cdn.bcebos.com 对畸形处理参数回 400；
    /// 三段回退：原 URL → 去掉 x-bce-process 参数的裸图（原图，实测 200）→ 再失败才抛。
    /// 400 的历史元凶是程序自己把 f_auto 改写成不存在的 format,jpg，不是防盗链。
    /// </summary>
    public async Task<byte[]> DownloadImageAsync(string url, CancellationToken ct = default)
    {
        try
        {
            return await DownloadOnceAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (url.Contains("x-bce-process=", StringComparison.Ordinal))
        {
            // 处理参数被 CDN 拒绝（400 等）→ 剥掉参数直接要原图（原图 = 最大尺寸，上传给 Komga 最优）
            return await DownloadOnceAsync(StripProcessParam(url), ct).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> DownloadOnceAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载百度百科图片失败 {(int)resp.StatusCode}: {url}");
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>剥掉 URL 里的 x-bce-process 处理参数（裸图直链）。</summary>
    public static string StripProcessParam(string url)
    {
        var q = url.IndexOf("?x-bce-process=", StringComparison.Ordinal);
        if (q < 0) return url;
        var amp = url.IndexOf('&', q);
        return amp > 0 ? url[..q] + url[amp..] : url[..q];
    }

    public void Dispose() => _http.Dispose();
}
