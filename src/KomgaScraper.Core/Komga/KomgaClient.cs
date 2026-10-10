using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Sources;

namespace KomgaScraper.Core.Komga;

/// <summary>
/// Komga REST API 客户端。逻辑对齐 KomgaBangumi 脚本（X-API-Key 优先，Basic 认证兜底）。
/// 可选代理：Komga 部署在境外/跨网段时由设置打开（本机/内网部署默认直连）。
/// </summary>
public sealed class KomgaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IAppLogger _log;

    public string BaseUrl { get; }

    public KomgaClient(string baseUrl, string? apiKey, string? username, string? password,
        ProxyConfig? proxy = null, IAppLogger? log = null)
    {
        _log = log ?? NullLogger.Instance;
        BaseUrl = baseUrl.TrimEnd('/');
        // 显式超时：默认 100s 的 TaskCanceledException 会被当成"用户取消"（历史事故：整批莫名"已取消"）
        _http = HttpFactory.Create(proxy ?? ProxyConfig.None, TimeSpan.FromSeconds(30),
            "KomgaScraper/1.0", warn: _log.Warn);
        _http.BaseAddress = new Uri(BaseUrl + "/api/");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KomgaScraper/1.0");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        }
        else if (!string.IsNullOrWhiteSpace(username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password ?? ""}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req)
    {
        // 重试前置快照：HttpRequestMessage 发过一次就不能复用，先把方法/地址/正文备份，
        // 网络抖动时用快照重建请求再打一次（正文是字节数组，JSON 查询与 multipart 封面都适用）。
        // 历史事故（2026-10-10）：这里只快照了 ContentType.MediaType —— 该属性只返回
        // "multipart/form-data"，boundary 参数丢失 → 重建的封面请求被 Komga 拒收
        // （500 "Failed to parse multipart servlet request"）。必须快照完整 Content-Type 头。
        var method = req.Method;
        var uri = req.RequestUri;
        byte[]? body = null;
        var contentType = req.Content?.Headers.ContentType?.ToString();   // 完整头值（multipart 含 boundary）
        if (req.Content != null)
            body = await req.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        HttpResponseMessage? resp = null;
        for (var attempt = 1; ; attempt++)
        {
            var attemptReq = new HttpRequestMessage(method, uri);
            if (body is { Length: > 0 })
            {
                var c = new ByteArrayContent(body);
                if (!string.IsNullOrEmpty(contentType))
                    c.Headers.TryAddWithoutValidation("Content-Type", contentType);
                attemptReq.Content = c;
            }
            try
            {
                resp = await _http.SendAsync(attemptReq).ConfigureAwait(false);
                break;
            }
            catch (Exception e) when (attempt < 3 && (e is HttpRequestException || e is TaskCanceledException))
            {
                // 网络级异常（SSL 被掐/DNS 抖动/连接超时）→ 最多重试 3 次（1.5s/4s 退避）；
                // 这里没有外部 ct，TaskCanceledException 只可能是 HttpClient 超时（非用户取消）。
                attemptReq.Dispose();
                _log.Warn($"Komga 请求网络异常（第 {attempt} 次，{Trunc(e.Message, 120)}），退避后重试");
                await Task.Delay(attempt == 1 ? 1500 : 4000).ConfigureAwait(false);
            }
            catch
            {
                attemptReq.Dispose();
                throw;
            }
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new KomgaApiException("认证失败(401)：请检查 API Key 或用户名/密码", resp.StatusCode);
        if ((int)resp.StatusCode == 413)
            throw new KomgaApiException("服务器拒绝(413 Payload Too Large)：封面过大", resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
        {
            var body2 = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new KomgaApiException(
                $"Komga API {(int)resp.StatusCode}: {Trunc(body2, 200)}", resp.StatusCode);
        }
        return resp;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    // ---------- 系列 ----------

    public async Task<List<LibraryDto>> GetLibrariesAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "v1/libraries")).ConfigureAwait(false);
        var list = await resp.Content.ReadFromJsonAsync<List<LibraryDto>>(JsonOpts, ct).ConfigureAwait(false);
        return list ?? [];
    }

    /// <summary>分页拉取库内全部系列（POST /series/list，size=2000，对齐脚本）。</summary>
    /// <summary>最近一次 ListSeriesInLibraryAsync 的服务器端总数（用于校验是否加载完整）。</summary>
    public long LastSeriesTotalElements { get; private set; }

    public async Task<List<SeriesDto>> ListSeriesInLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        var all = new List<SeriesDto>();
        LastSeriesTotalElements = 0;
        const int size = 2000;
        int page = 0;
        while (true)
        {
            var payload = KomgaQuery.SeriesInLibrary(libraryId);
            var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
                $"v1/series/list?size={size}&page={page}") { Content = JsonContent.Create(payload) })
                .ConfigureAwait(false);
            var data = await resp.Content.ReadFromJsonAsync<PageDto<SeriesDto>>(JsonOpts, ct).ConfigureAwait(false);
            if (data != null && data.TotalElements > LastSeriesTotalElements)
                LastSeriesTotalElements = data.TotalElements;
            var content = data?.Content ?? [];
            all.AddRange(content);
            if (content.Count < size) break;
            page++;
        }
        return all;
    }

    public async Task<SeriesDto> GetSeriesAsync(string seriesId, CancellationToken ct = default)
    {
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"v1/series/{seriesId}")).ConfigureAwait(false);
        var s = await resp.Content.ReadFromJsonAsync<SeriesDto>(JsonOpts, ct).ConfigureAwait(false);
        return s ?? throw new KomgaApiException($"系列 {seriesId} 数据为空", resp.StatusCode);
    }

    public async Task<SeriesMetadataDto?> GetSeriesMetadataAsync(string seriesId, CancellationToken ct = default)
    {
        try
        {
            var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"v1/series/{seriesId}/metadata"))
                .ConfigureAwait(false);
            return await resp.Content.ReadFromJsonAsync<SeriesMetadataDto>(JsonOpts, ct).ConfigureAwait(false);
        }
        catch (KomgaApiException)
        {
            return null; // 对齐脚本：无元数据时返回 null 继续流程
        }
    }

    /// <summary>PATCH 系列元数据：只发非空标量，空数组允许发送（用于清空 tags），对齐脚本 cleanMeta 规则。</summary>
    public async Task PatchSeriesMetadataAsync(string seriesId, SeriesMetadataDto meta, CancellationToken ct = default)
    {
        var body = CleanMeta(meta);
        if (body.Count == 0) return;
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"v1/series/{seriesId}/metadata")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json")
        }).ConfigureAwait(false);
        resp.Dispose();
    }

    /// <summary>列出库内全部书（POST /books/list，unpaged=true，numberSort 升序，对齐脚本）。</summary>
    public async Task<List<BookDto>> ListBooksInSeriesAsync(string seriesId, CancellationToken ct = default)
    {
        var payload = KomgaQuery.BooksInSeries(seriesId);
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            "v1/books/list?unpaged=true&sort=metadata.numberSort,asc")
        { Content = JsonContent.Create(payload) }).ConfigureAwait(false);
        var data = await resp.Content.ReadFromJsonAsync<PageDto<BookDto>>(JsonOpts, ct).ConfigureAwait(false);
        return data?.Content ?? [];
    }

    /// <summary>全库关键词搜索系列（GET /series?search=…，编辑器的搜索栏用；跨库，返回含各库系列）。</summary>
    public async Task<List<SeriesDto>> SearchSeriesAsync(string query, int size = 50, CancellationToken ct = default)
    {
        var url = $"v1/series?search={Uri.EscapeDataString(query)}&size={size}&page=0";
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url)).ConfigureAwait(false);
        var data = await resp.Content.ReadFromJsonAsync<PageDto<SeriesDto>>(JsonOpts, ct).ConfigureAwait(false);
        return data?.Content ?? [];
    }

    /// <summary>取单册当前选中缩略图原图（GET /books/{id}/thumbnail；404/无封面时返回 null）。</summary>
    public async Task<byte[]?> GetBookThumbnailBytesAsync(string bookId, CancellationToken ct = default)
    {
        try
        {
            var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"v1/books/{bookId}/thumbnail"))
                .ConfigureAwait(false);
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (KomgaApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task PatchBookMetadataAsync(string bookId, BookMetadataDto meta, CancellationToken ct = default)
    {
        var body = CleanMeta(meta);
        if (body.Count == 0) return;
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"v1/books/{bookId}/metadata")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json")
        }).ConfigureAwait(false);
        resp.Dispose();
    }

    /// <summary>该 DTO 经 CleanMeta 后是否还有可写字段（空则不必发请求，避免一堆空 PATCH）。</summary>
    public static bool HasChanges<T>(T meta) => CleanMeta(meta).Count > 0;

    /// <summary>
    /// 清空后仅保留非空标量与数组字段；键名转小写驼峰由 DTO 的 JsonPropertyName 保证。
    /// 整数字段 0 一律不发：Komga 对 totalBookCount 校验 must be greater than 0，发 0 会 400；
    /// ageRating 例外（0 = 通用，是合法值）。
    /// </summary>
    private static Dictionary<string, object?> CleanMeta<T>(T meta)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in typeof(T).GetProperties())
        {
            var name = prop.GetCustomAttributes(true)
                .OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
                .FirstOrDefault()?.Name ?? prop.Name;
            var value = prop.GetValue(meta);
            if (value is null) continue;
            if (value is string s && s.Length == 0) continue;
            if (value is int i && i == 0 && !string.Equals(name, "ageRating", StringComparison.Ordinal))
                continue;
            dict[name] = value;
        }
        return dict;
    }

    // ---------- 封面 ----------

    public async Task<List<ThumbnailDto>> GetSeriesThumbnailsAsync(string seriesId, CancellationToken ct = default)
    {
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"v1/series/{seriesId}/thumbnails"))
            .ConfigureAwait(false);
        var list = await resp.Content.ReadFromJsonAsync<List<ThumbnailDto>>(JsonOpts, ct).ConfigureAwait(false);
        return list ?? [];
    }

    public async Task DeleteSeriesThumbnailAsync(string seriesId, string thumbId, CancellationToken ct = default)
    {
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"v1/series/{seriesId}/thumbnails/{thumbId}"))
            .ConfigureAwait(false);
        resp.Dispose();
    }

    /// <summary>上传系列封面（multipart, selected=true）。</summary>
    public async Task UploadSeriesCoverAsync(string seriesId, byte[] image, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(image);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(fileContent, "file", fileName);
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"v1/series/{seriesId}/thumbnails?selected=true") { Content = form }).ConfigureAwait(false);
        resp.Dispose();
    }

    public async Task<List<ThumbnailDto>> GetBookThumbnailsAsync(string bookId, CancellationToken ct = default)
    {
        try
        {
            var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"v1/books/{bookId}/thumbnails"))
                .ConfigureAwait(false);
            var list = await resp.Content.ReadFromJsonAsync<List<ThumbnailDto>>(JsonOpts, ct).ConfigureAwait(false);
            return list ?? [];
        }
        catch (KomgaApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return []; // 旧版 Komga 无书封面接口时优雅降级
        }
    }

    public async Task DeleteBookThumbnailAsync(string bookId, string thumbId, CancellationToken ct = default)
    {
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"v1/books/{bookId}/thumbnails/{thumbId}"))
            .ConfigureAwait(false);
        resp.Dispose();
    }

    public async Task UploadBookCoverAsync(string bookId, byte[] image, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(image);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(fileContent, "file", fileName);
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"v1/books/{bookId}/thumbnails?selected=true") { Content = form }).ConfigureAwait(false);
        resp.Dispose();
    }

    // ---------- 收藏夹 ----------

    public async Task<List<CollectionDto>> GetCollectionsAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "v1/collections?unpaged=true"))
            .ConfigureAwait(false);
        var data = await resp.Content.ReadFromJsonAsync<PageDto<CollectionDto>>(JsonOpts, ct).ConfigureAwait(false);
        return data?.Content ?? [];
    }

    public async Task<CollectionDto> CreateCollectionAsync(string name, List<string> seriesIds, bool ordered,
        CancellationToken ct = default)
    {
        var payload = new { name, seriesIds, ordered };
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "v1/collections")
        {
            Content = JsonContent.Create(payload)
        }).ConfigureAwait(false);
        var c = await resp.Content.ReadFromJsonAsync<CollectionDto>(JsonOpts, ct).ConfigureAwait(false);
        return c ?? throw new KomgaApiException("创建收藏夹返回为空", resp.StatusCode);
    }

    public async Task UpdateCollectionSeriesAsync(string collectionId, List<string> seriesIds, CancellationToken ct = default)
    {
        var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"v1/collections/{collectionId}")
        {
            Content = JsonContent.Create(new { seriesIds })
        }).ConfigureAwait(false);
        resp.Dispose();
    }

    public async Task<byte[]> DownloadImageAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new KomgaApiException($"下载图片失败 {(int)resp.StatusCode}: {url}", resp.StatusCode);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>获取系列当前选中缩略图原图（GET /series/{id}/thumbnail）。
    /// 历史事故：客户端默认 Accept: application/json，Komga 对图片端点只认 image/jpeg → 恒 406
    /// （编辑页的系列封面因此从来没显示过）。请求级覆盖 Accept。</summary>
    public async Task<byte[]> GetSeriesThumbnailAsync(string seriesId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"v1/series/{seriesId}/thumbnail");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        var resp = await SendAsync(req).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 编辑器专用：按给定键值直接 PATCH 系列元数据（不经 CleanMeta，
    /// 空串会真实写入以实现"清空字段"；锁字段由调用方原样带上）。
    /// </summary>
    public async Task PatchSeriesMetadataRawAsync(string seriesId, Dictionary<string, object?> body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"v1/series/{seriesId}/metadata")
        { Content = JsonContent.Create(body, options: JsonOpts) };
        await SendAsync(req).ConfigureAwait(false);
    }

    /// <summary>编辑器专用：按给定键值直接 PATCH 单卷元数据（同上，空串=清空）。</summary>
    public async Task PatchBookMetadataRawAsync(string bookId, Dictionary<string, object?> body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"v1/books/{bookId}/metadata")
        { Content = JsonContent.Create(body, options: JsonOpts) };
        await SendAsync(req).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class KomgaApiException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
