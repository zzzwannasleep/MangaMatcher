using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KomgaScraper.Core.Sources;

namespace KomgaScraper.Core.Sources;

/// <summary>
/// Bangumi (bgm.tv) v0 API 客户端，逻辑对齐 KomgaBangumi 脚本。
/// Search: POST /v0/search/subjects?limit=20 {keyword, sort:"match", filter:{type:[1], nsfw:true}}
/// Subject: GET /v0/subjects/{id}；关联单行本: GET /v0/subjects/{id}/subjects
/// 支持自定义 API 基址（测试用 mock）与 HTTP 代理（bgm.tv 部分网络需代理访问）。
/// </summary>
public sealed class BangumiClient : IDisposable
{
    public const string DefaultApiBase = "https://api.bgm.tv";
    public const string LegacyBase = "https://bangumi.tv";

    private readonly HttpClient _http;
    private readonly IAppLogger _log;
    private string? _accessToken;

    /// <summary>
    /// 两次 Bangumi API 请求起始时刻的最小间隔（毫秒），默认 300（≈3.3 次/秒）。
    /// 并发批量刮削时所有系列共享同一个节拍：谁的请求都会排队，总体速率不会因并发而超速；
    /// 设为 0 可完全放开（本地 mock 测试用）。图片 CDN（lain.bgm.tv）下载不参与节流。
    /// </summary>
    public int MinRequestIntervalMs { get; set; } = 300;

    private readonly SemaphoreSlim _paceGate = new(1, 1);
    private long _nextStartTick;

    /// <summary>把请求的"起始时刻"按最小间隔排开；在途请求互相不阻塞（RTT 长×时也能保持并发）。</summary>
    private async Task ScheduleStartAsync(CancellationToken ct)
    {
        if (MinRequestIntervalMs <= 0) return;
        await _paceGate.WaitAsync(ct).ConfigureAwait(false);
        long delay;
        try
        {
            var now = Environment.TickCount64;
            var start = Math.Max(now, Interlocked.Read(ref _nextStartTick));
            Interlocked.Exchange(ref _nextStartTick, start + MinRequestIntervalMs);
            delay = start - now;
        }
        finally
        {
            _paceGate.Release();
        }
        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false);
    }

    public BangumiClient(string? accessToken, string? apiBase = null, string? proxyUrl = null, IAppLogger? log = null,
        string? proxyUser = null, string? proxyPassword = null)
    {
        _log = log ?? NullLogger.Instance;
        _accessToken = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken;
        _http = HttpFactory.Create(
            new ProxyConfig(proxyUrl, proxyUser, proxyPassword),
            TimeSpan.FromSeconds(30),
            "KomgaScraper/1.0 (https://github.com/local/KomgaScraper)",
            warn: _log.Warn);
        _http.BaseAddress = new Uri(apiBase ?? DefaultApiBase);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_accessToken != null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    public void SetAccessToken(string? token)
    {
        _accessToken = string.IsNullOrWhiteSpace(token) ? null : token;
        _http.DefaultRequestHeaders.Authorization = _accessToken == null
            ? null
            : new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new BangumiApiException("Bangumi 认证失败(401)：Access Token 已失效，请在设置中更新", resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new BangumiApiException($"Bangumi API {(int)resp.StatusCode}: {Trunc(body, 200)}", resp.StatusCode);
        }
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        await ScheduleStartAsync(ct).ConfigureAwait(false);
        var text = await GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(text, JsonOpts)
               ?? throw new BangumiApiException($"Bangumi 返回数据为空: {url}", HttpStatusCode.OK);
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>按名称搜索（type=1 书籍，含 NSFW），过滤 platform，返回归一化候选。</summary>
    public async Task<List<SearchCandidate>> SearchSubjectsAsync(
        string keyword, string matchType, int limit = 8, CancellationToken ct = default)
    {
        var payload = new
        {
            keyword,
            sort = "match",
            filter = new { type = new[] { 1 }, nsfw = true },
        };
        await ScheduleStartAsync(ct).ConfigureAwait(false);
        using var resp = await _http.PostAsync("/v0/search/subjects?limit=20",
            new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"), ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new BangumiApiException($"Bangumi 搜索失败 {(int)resp.StatusCode}: {Trunc(body, 200)}", resp.StatusCode);
        }
        var search = await resp.Content.ReadFromJsonAsync<BangumiSearchResponseDto>(JsonOpts, ct).ConfigureAwait(false);
        var data = search?.Data ?? [];
        data = data.Where(i => string.Equals(i.Platform, matchType, StringComparison.Ordinal)).ToList();

        // 日漫过滤：谚文标题/韩漫标签/韩国平台连载的条目直接从候选中剔除（本地库只收录日漫）
        if (OriginFilter.JapaneseOnly)
        {
            var before = data.Count;
            data = data.Where(i => !OriginFilter.LooksKorean(
                    i.Name, i.NameCn, null,
                    i.Tags?.Select(t => t.Name),
                    SubjectMapper.ParseInfobox(i.Infobox, "连载杂志")))
                .ToList();
            if (data.Count < before)
                _log.Info($"日漫过滤：剔除 {before - data.Count} 个疑似韩漫候选");
        }

        return data.Select(ToCandidate).Take(limit > 0 ? limit : data.Count).ToList();
    }

    private static SearchCandidate ToCandidate(BangumiSearchItemDto item)
    {
        var authorName = "未知作者";
        string aliases = "";
        if (item.Infobox != null)
        {
            var author = SubjectMapper.ParseInfobox(item.Infobox, "作画")
                         ?? SubjectMapper.ParseInfobox(item.Infobox, "作者")
                         ?? SubjectMapper.ParseInfobox(item.Infobox, "原作")
                         ?? SubjectMapper.ParseInfobox(item.Infobox, "脚本");
            if (author != null)
                authorName = SubjectMapper.FirstAuthor(author);
            aliases = SubjectMapper.ExtractAliases(item.Infobox);
        }
        var cover = item.Image
                    ?? item.Images?.Medium
                    ?? item.Images?.Common
                    ?? item.Images?.Small;
        return new SearchCandidate(item.Id, item.NameCn ?? item.Name ?? "", item.Name ?? "",
            authorName, aliases, cover);
    }

    public Task<BangumiSubjectDto> GetSubjectAsync(int subjectId, CancellationToken ct = default) =>
        GetJsonAsync<BangumiSubjectDto>($"/v0/subjects/{subjectId}", ct);

    public Task<List<BangumiRelatedSubjectDto>> GetRelatedSubjectsAsync(int subjectId, CancellationToken ct = default) =>
        GetJsonAsync<List<BangumiRelatedSubjectDto>>($"/v0/subjects/{subjectId}/subjects", ct);

    public async Task<byte[]> DownloadImageAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new BangumiApiException($"下载图片失败 {(int)resp.StatusCode}: {url}", resp.StatusCode);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class BangumiApiException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
