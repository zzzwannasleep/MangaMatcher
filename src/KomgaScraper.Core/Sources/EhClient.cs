using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KomgaScraper.Core.Sources;

/// <summary>
/// E-Hentai 表站（e-hentai.org，免登录、零 Cookie）客户端。
///
/// 两条腿（与 Go/Docker 版 server/internal/sources/ehentai.go 同源）：
///  1. 搜索 → HTML 抓取（/?f_search=…&amp;advsearch=1&amp;f_sfu=on&amp;f_sft=on&amp;f_sfl=on），拿 gid/token；
///  2. 元数据 → JSON API（POST api.e-hentai.org/api.php，method=gdata，namespace=1）拿全字段。
///
/// 实测要点：
///  • gdata 免鉴权；rating/posted/filecount/filesize 平时是字符串，画廊被删时会退化成裸数字 → 宽容读取；
///  • 一次最多 25 个 gid（服务端硬限制）；
///  • tags 自带 "namespace:value" 前缀；
///  • 标签语法搜索（artist:asanagi）远优于纯文本搜索 —— 纯文本是罗马字取向，中文/日文标题词基本搜不到；
///  • 表站在墙内直连不通，必须经代理。
///
/// 稳健性（都是踩过的坑）：
///  • 15s 超时 + 瞬态状态码重试 3 次，**连接层错误不重试**（墙内直连必然 dial timeout，重试会把整库拖死）；
///  • 连接层连续失败 2 次 → 熔断 3 分钟，本次任务不再碰 EH（一个客户端 = 一次刮削任务）；
///  • 请求起始最小间隔 1100ms（EH 对爬取敏感，礼貌节流）。
/// </summary>
public sealed class EhClient
{
    public const string Source = "EH";

    private const string DefaultBase = "https://e-hentai.org";
    private const string GDataUrl = "https://api.e-hentai.org/api.php";
    /// <summary>gdata 一次最多查询 25 个 gid（服务端硬限制）。</summary>
    public const int MaxBatch = 25;
    /// <summary>表站礼貌间隔：两次请求起始的最小间隔。</summary>
    public const int DefaultPaceMs = 1100;
    private const int MaxBody = 8 << 20;
    private const int CircuitFails = 2;
    private static readonly TimeSpan CircuitCooldown = TimeSpan.FromMinutes(3);
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly IAppLogger? _log;
    private readonly bool _proxied;
    private readonly int _paceMs;
    private readonly object _paceLock = new();
    private DateTime _nextStart = DateTime.MinValue;
    private readonly object _circLock = new();
    private int _connFails;
    private DateTime _openUntil = DateTime.MinValue;

    public string BaseUrl { get; }
    public string GDataEndpoint { get; }

    /// <param name="proxy">代理（表站必须；空则直连，墙内会超时）。</param>
    /// <param name="baseUrl">站点根，空 = 官方表站；测试/mock 可指向本地。</param>
    /// <param name="minIntervalMs">两次请求起始的最小间隔；负数 = 用默认 1100ms，0 = 不节流（测试用）。</param>
    public EhClient(ProxyConfig? proxy, IAppLogger? log = null, string? baseUrl = null, int minIntervalMs = -1)
    {
        _log = log;
        _proxied = proxy is { IsSet: true };
        _http = HttpFactory.Create(proxy, TimeSpan.FromSeconds(15), UserAgent, warn: log is null ? null : log.Warn);
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        BaseUrl = b.Length == 0 ? DefaultBase : b;
        // 测试 / 自建反代：非官方站点时 gdata 走同一主机的 /api.php
        GDataEndpoint = BaseUrl == DefaultBase || BaseUrl == "http://e-hentai.org" ? GDataUrl : BaseUrl + "/api.php";
        _paceMs = minIntervalMs < 0 ? DefaultPaceMs : minIntervalMs;
    }

    // ---------- 节流与熔断 ----------

    private void ScheduleStart()
    {
        if (_paceMs <= 0) return;
        DateTime start;
        lock (_paceLock)
        {
            var now = DateTime.UtcNow;
            start = _nextStart > now ? _nextStart : now;
            _nextStart = start.AddMilliseconds(_paceMs);
        }
        var wait = start - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) Thread.Sleep(wait);
    }

    /// <summary>EH 的瞬态状态码：509 是「带宽超限」，要退避更久。</summary>
    private static bool IsTransient(int status) =>
        status is 429 or 500 or 502 or 503 or 504 or 509 or 520 or 521 or 522 or 523 or 524;

    private static TimeSpan RetryDelay(int attempt, string lastMsg) =>
        lastMsg.Contains("509") ? TimeSpan.FromSeconds(5 * attempt) : TimeSpan.FromMilliseconds(1500 * attempt);

    /// <summary>连接失败时给用户一句可执行的提示（别只丢一个 dial timeout）。</summary>
    private string ReachHint() => _proxied
        ? "已配置代理但仍连不上，请检查代理是否可用"
        : "e-hentai.org 在中国大陆直连不通，请在「设置 → 代理」里配置后重试";

    /// <summary>熔断中则返回错误（不发任何请求）。</summary>
    private string? CircuitError()
    {
        DateTime until;
        lock (_circLock) until = _openUntil;
        if (until == DateTime.MinValue || DateTime.UtcNow > until) return null;
        return $"E-Hentai 源已熔断至 {until.ToLocalTime():HH:mm:ss}（连续 {CircuitFails} 次请求失败，本次任务不再重试）：{ReachHint()}";
    }

    /// <summary>
    /// 记录一次 HTTP 层失败（连接层或重试耗尽），达到阈值即熔断。
    /// 只统计「请求本身失败」：gdata 逻辑错误（画廊已删除等）不算，那说明站点是通的。
    /// </summary>
    private void NoteFail()
    {
        bool opened = false;
        int fails;
        DateTime until;
        lock (_circLock)
        {
            _connFails++;
            fails = _connFails;
            if (_connFails >= CircuitFails && DateTime.UtcNow > _openUntil)
            {
                _openUntil = DateTime.UtcNow.Add(CircuitCooldown);
                opened = true;
            }
            until = _openUntil;
        }
        if (opened)
            _log?.Warn($"E-Hentai 连续失败 {fails} 次，本次任务熔断至 {until.ToLocalTime():HH:mm:ss}");
    }

    private void NoteOk()
    {
        lock (_circLock)
        {
            _connFails = 0;
            _openUntil = DateTime.MinValue;
        }
    }

    // ---------- 底层请求 ----------

    private async Task<(string Body, string FinalUrl)> GetTextAsync(string url, CancellationToken ct)
    {
        if (CircuitError() is { } circ) throw new InvalidOperationException(circ);
        string lastMsg = "";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ScheduleStart();
            HttpResponseMessage resp;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                // 连接层错误：直接返回，不重试（见类注释）
                NoteFail();
                throw new InvalidOperationException($"E-Hentai 连接失败（{ReachHint()}）：{e.Message}", e);
            }

            using (resp)
            {
                var final = resp.RequestMessage?.RequestUri?.ToString() ?? url;
                var status = (int)resp.StatusCode;
                if (IsTransient(status))
                {
                    lastMsg = $"E-Hentai 请求失败 {status}：{url}";
                }
                else if (status >= 300)
                {
                    NoteFail();
                    throw new InvalidOperationException($"E-Hentai 请求失败 {status}：{url}");
                }
                else
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    NoteOk();
                    return (DecodeHtml(bytes), final);
                }
            }

            if (attempt < 3)
            {
                _log?.Warn($"E-Hentai 请求重试 {attempt}/3：{lastMsg}");
                await Task.Delay(RetryDelay(attempt, lastMsg), ct).ConfigureAwait(false);
            }
        }
        NoteFail();
        throw new InvalidOperationException(lastMsg.Length > 0 ? lastMsg : $"E-Hentai 请求失败：{url}");
    }

    private async Task<string> PostJsonAsync(string url, object payload, CancellationToken ct)
    {
        if (CircuitError() is { } circ) throw new InvalidOperationException(circ);
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        string lastMsg = "";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ScheduleStart();
            HttpResponseMessage resp;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new ByteArrayContent(body),
                };
                req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                NoteFail();
                throw new InvalidOperationException($"E-Hentai 元数据接口连接失败（{ReachHint()}）：{e.Message}", e);
            }

            using (resp)
            {
                var status = (int)resp.StatusCode;
                if (status == 200)
                {
                    var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    NoteOk();
                    return text;
                }
                if (IsTransient(status))
                {
                    lastMsg = $"E-Hentai 元数据接口失败 {status}";
                }
                else
                {
                    NoteFail();
                    throw new InvalidOperationException($"E-Hentai 元数据接口失败 {status}：{Snippet(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))}");
                }
            }

            if (attempt < 3)
            {
                _log?.Warn($"E-Hentai 元数据接口重试 {attempt}/3：{lastMsg}");
                await Task.Delay(RetryDelay(attempt, lastMsg), ct).ConfigureAwait(false);
            }
        }
        NoteFail();
        throw new InvalidOperationException(lastMsg.Length > 0 ? lastMsg : "E-Hentai 元数据接口失败");
    }

    private static string Snippet(string s)
    {
        s = s.Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    /// <summary>表站是 UTF-8；退化为 Latin-1 只会让个别字符变问号，不会炸。</summary>
    private static string DecodeHtml(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    // ---------- 搜索 ----------

    /// <summary>
    /// 关键词搜索（表站第一页）。也支持标签语法（"artist:asanagi"）与引号短语。
    /// </summary>
    public async Task<List<EhHit>> SearchAsync(string term, int limit = MaxBatch, CancellationToken ct = default)
    {
        term = (term ?? "").Trim();
        if (term.Length == 0) return [];
        if (limit <= 0 || limit > MaxBatch) limit = MaxBatch;

        var q = "f_search=" + Uri.EscapeDataString(term) + "&advsearch=1&f_sfu=on&f_sft=on&f_sfl=on";
        var (page, _) = await GetTextAsync($"{BaseUrl}/?{q}", ct).ConfigureAwait(false);
        var hits = ParseSearch(page);
        if (hits.Count == 0)
        {
            // 把「不是没搜到，而是被拦/需要确认」说清楚，别让用户以为是标题问题
            if (page.Contains("Content Warning", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("E-Hentai 返回了内容警告页：需要先在浏览器打开 e-hentai.org 确认一次（或在站点设置里关闭该提示）");
            if (page.Contains("temporarily banned", StringComparison.OrdinalIgnoreCase) ||
                page.Contains("Your IP address has been", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("E-Hentai 提示 IP 被临时限制：请降低频率或更换代理出口");
        }
        return hits.Count > limit ? hits.Take(limit).ToList() : hits;
    }

    /// <summary>标签语法搜索：f_search="artist:asanagi"。同人志自动匹配靠它兜精度。</summary>
    public Task<List<EhHit>> SearchTagAsync(string ns, string tag, int limit = MaxBatch, CancellationToken ct = default)
    {
        ns = (ns ?? "").Trim();
        tag = (tag ?? "").Trim();
        if (ns.Length == 0 || tag.Length == 0) return Task.FromResult(new List<EhHit>());
        return SearchAsync($"{ns}:{tag}", limit, ct);
    }

    // ---------- 元数据 ----------

    /// <summary>取单个画廊元数据（id 形如 "123/abcdef" 或完整画廊链接）。</summary>
    public async Task<EhGallery> GetGalleryAsync(string id, CancellationToken ct = default)
    {
        if (!ParseEhId(id, out var gid, out var token))
            throw new InvalidOperationException($"E-Hentai 画廊标识无效：\"{id}\"（应为 gid/token 或画廊链接）");
        if (token.Length == 0) token = await ResolveTokenAsync(gid, ct).ConfigureAwait(false);

        var payload = new Dictionary<string, object>
        {
            ["method"] = "gdata",
            ["gidlist"] = new object[] { new object[] { gid, token } },
            ["namespace"] = 1,
        };
        var raw = await PostJsonAsync(GDataEndpoint, payload, ct).ConfigureAwait(false);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException e) { throw new InvalidOperationException($"解析 E-Hentai 元数据失败：{e.Message}", e); }

        using (doc)
        {
            var root = doc.RootElement;
            var err = EhStr(root, "error");
            if (err.Length > 0) throw new InvalidOperationException($"E-Hentai 返回错误：{ErrText(err)}");
            if (!root.TryGetProperty("gmetadata", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                throw new InvalidOperationException($"E-Hentai 未返回该画廊元数据（gid {gid}，可能已删除或 token 失效）");
            var it = list[0];
            var itemErr = EhStr(it, "error");
            if (itemErr.Length > 0) throw new InvalidOperationException($"E-Hentai 返回错误：{ErrText(itemErr)}");
            var tok = EhStr(it, "token");
            if (tok.Length > 0) token = tok;

            return new EhGallery
            {
                Gid = gid,
                Token = token,
                Url = $"{BaseUrl}/g/{gid}/{token}/",
                Title = WebUtility.HtmlDecode(EhStr(it, "title")),
                TitleJpn = WebUtility.HtmlDecode(EhStr(it, "title_jpn")),
                Category = EhStr(it, "category").ToLowerInvariant(),
                Thumb = EhStr(it, "thumb"),
                Uploader = EhStr(it, "uploader"),
                Posted = EhStr(it, "posted"),
                FileCount = EhStr(it, "filecount"),
                Filesize = EhStr(it, "filesize"),
                Rating = EhStr(it, "rating"),
                Tags = it.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : [],
            };
        }
    }

    /// <summary>
    /// 宽容读取：gdata 大多数时候给字符串，但画廊被删/被 expunge 时会退化成裸数字
    /// （实测 filesize 返回 0）—— 统一按字符串收，别让一个字段的类型把整条元数据搞挂。
    /// </summary>
    private static string EhStr(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => (v.GetString() ?? "").Trim(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => "",
        };
    }

    /// <summary>只给了 gid 时，抓画廊页并从跳转后 URL / 正文里补出 token。</summary>
    private async Task<string> ResolveTokenAsync(int gid, CancellationToken ct)
    {
        var (page, final) = await GetTextAsync($"{BaseUrl}/g/{gid}/", ct).ConfigureAwait(false);
        foreach (var s in new[] { final, page })
        {
            var m = GalleryTokenRe.Match(s);
            if (m.Success) return m.Groups[2].Value;
        }
        throw new InvalidOperationException(
            $"无法解析 E-Hentai 画廊 token（gid {gid}）：画廊可能已删除，或该内容需要登录（本项目只支持表站）");
    }

    /// <summary>封面地址（gdata 的 thumb 已经是 CDN 直链；空则返回空串）。</summary>
    public string CoverUrl(EhGallery? g)
    {
        var u = (g?.Thumb ?? "").Trim();
        return u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? u
            : "";
    }

    // ---------- ID / 链接解析 ----------

    public static string EhId(int gid, string? token) =>
        string.IsNullOrWhiteSpace(token) ? gid.ToString(CultureInfo.InvariantCulture)
                                         : gid.ToString(CultureInfo.InvariantCulture) + "/" + token.Trim();

    private static readonly Regex TokenRe = new(@"^[0-9a-fA-F]+$", RegexOptions.Compiled);
    private static readonly Regex GalleryUrlRe = new(
        @"^(?:https?:)?(?://)?(?:www\.)?(?:e-hentai\.org|exhentai\.org|e-hentai\.com)/g/(\d+)(?:/([0-9a-f]+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GalleryTokenRe = new(@"/g/(\d+)/([0-9a-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从画廊链接里解出 gid/token（支持 http(s)://、协议相对、裸域名；表站与里站域名）。</summary>
    public static bool ParseEhLink(string? url, out int gid, out string token)
    {
        gid = 0;
        token = "";
        var s = (url ?? "").Trim();
        if (s.Length == 0) return false;
        var m = GalleryUrlRe.Match(s);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out var g) || g <= 0) return false;
        gid = g;
        token = m.Groups[2].Success ? m.Groups[2].Value : "";
        return true;
    }

    /// <summary>是否是 E-Hentai / ExHentai 画廊链接（只认带域名的形态，避免把纯数字当画廊）。</summary>
    public static bool IsEhGalleryLink(string? url) => ParseEhLink(url, out _, out _);

    /// <summary>解析内部 id（"123/abcdef"、"123"，也容忍直接贴完整链接）。</summary>
    public static bool ParseEhId(string? id, out int gid, out string token)
    {
        gid = 0;
        token = "";
        var s = (id ?? "").Trim();
        if (s.Length == 0) return false;
        if (ParseEhLink(s, out gid, out token)) return true;
        s = s.Trim('/');
        var parts = s.Split('/');
        if (parts.Length is 0 or > 2) return false;
        if (!int.TryParse(parts[0].Trim(), out var g) || g <= 0) return false;
        gid = g;
        if (parts.Length == 2)
        {
            var t = parts[1].Trim();
            if (t.Length > 0 && !TokenRe.IsMatch(t)) return false;
            token = t;
        }
        return true;
    }

    /// <summary>把 "namespace:value" 列表按命名空间分组（命名空间小写；无冒号的归到 ""）。</summary>
    public static Dictionary<string, List<string>> SplitTags(IEnumerable<string>? tags)
    {
        var outp = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var raw in tags ?? [])
        {
            var t = (raw ?? "").Trim();
            if (t.Length == 0) continue;
            var ns = "";
            var val = t;
            var i = t.IndexOf(':');
            if (i > 0)
            {
                ns = t[..i].Trim().ToLowerInvariant();
                val = t[(i + 1)..].Trim();
            }
            if (val.Length == 0) continue;
            if (!outp.TryGetValue(ns, out var bucket)) outp[ns] = bucket = [];
            bucket.Add(val);
        }
        return outp;
    }

    // ---------- 搜索页解析 ----------

    private static readonly Regex RowLinkRe = new(
        "<a\\s+href=\"(https?://[^\"]*/g/(\\d+)/([0-9a-f]+)/?)\"[^>]*>\\s*<div class=\"glink\">(.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CategoryRe = new("class=\"cn[^\"]*\"[^>]*>\\s*([^<]+?)\\s*<",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex PagesRe = new(">\\s*(\\d+)\\s+pages?\\s*<",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex UploaderRe = new("<a href=\"https?://[^\"]*/uploader/[^\"]*\">\\s*([^<]+?)\\s*</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ThumbRe = new("<img[^>]+(?:src|data-src)=\"(https?://[^\"]+/w/[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BracketRe = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex EventRe = new(
        @"\(\s*(?:c\d{2,3}|comiket[^)]*|reitaisai[^)]*|例大祭[^)]*|sunshine creation[^)]*|hakurei[^)]*)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpaceRe = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex AuthorPrefixRe = new(@"^\s*\[([^\]]+)\]", RegexOptions.Compiled);

    /// <summary>搜索页每行一个 &lt;tr&gt;，行内有 glink 标题与 /g/gid/token/ 链接。</summary>
    internal static List<EhHit> ParseSearch(string page)
    {
        var outp = new List<EhHit>(MaxBatch);
        var seen = new HashSet<int>();
        foreach (var row in page.Split("<tr", StringSplitOptions.None))
        {
            var m = RowLinkRe.Match(row);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[2].Value, out var gid) || gid <= 0 || !seen.Add(gid)) continue;
            var link = m.Groups[1].Value;
            if (!link.EndsWith('/')) link += "/";
            var hit = new EhHit
            {
                Gid = gid,
                Token = m.Groups[3].Value,
                Url = link,
                Title = SpaceRe.Replace(WebUtility.HtmlDecode(m.Groups[4].Value.Trim()), " "),
            };
            var cm = CategoryRe.Match(row);
            if (cm.Success) hit.Category = WebUtility.HtmlDecode(cm.Groups[1].Value.Trim()).ToLowerInvariant();
            var pm = PagesRe.Match(row);
            if (pm.Success) hit.Pages = pm.Groups[1].Value;
            var um = UploaderRe.Match(row);
            if (um.Success) hit.Uploader = WebUtility.HtmlDecode(um.Groups[1].Value.Trim());
            var tm = ThumbRe.Match(row);
            if (tm.Success) hit.Thumb = tm.Groups[1].Value;
            outp.Add(hit);
        }
        return outp;
    }

    /// <summary>
    /// 剥掉方括号段与会场标记，得到「核心标题」——用于和文件名派生的查询词比对。
    /// 例："(C100) [kibun tenkan (Eru Takagaki)] untitled (THE iDOLM@STER: Shiny Colors)"
    ///   → "untitled (THE iDOLM@STER: Shiny Colors)"
    /// </summary>
    public static string CleanTitle(string title)
    {
        var s = WebUtility.HtmlDecode((title ?? "").Trim());
        s = BracketRe.Replace(s, " ");
        s = EventRe.Replace(s, " ");
        s = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Trim(' ', '-', '_', '|', '·');
    }

    /// <summary>取标题首个方括号段当作者/社团（EH 约定 [circle (artist)]）。</summary>
    public static string AuthorOf(string title)
    {
        var m = AuthorPrefixRe.Match((title ?? "").Trim());
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string ErrText(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return "未知错误";
        if (s.Contains("Invalid JSON", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("invalid", StringComparison.OrdinalIgnoreCase)) return $"请求格式被拒绝（{s}）";
        if (s.Contains("Key missing", StringComparison.OrdinalIgnoreCase)) return $"缺少 token（{s}）";
        return s;
    }
}

/// <summary>搜索结果条目（gdata 未调用前的轻量信息）。</summary>
public sealed class EhHit
{
    public int Gid { get; init; }
    public string Token { get; init; } = "";
    public string Url { get; init; } = "";
    /// <summary>原始标题（含 [汉化组] 等方括号段）。</summary>
    public string Title { get; init; } = "";
    // 下面几个字段是解析搜索页时逐项补出来的（有则填），所以用 set 而不是 init
    public string Category { get; set; } = "";
    public string Thumb { get; set; } = "";
    public string Posted { get; set; } = "";
    public string Pages { get; set; } = "";
    public string Uploader { get; set; } = "";

    public string Id => EhClient.EhId(Gid, Token);
}

/// <summary>gdata 归一后的画廊详情。</summary>
public sealed class EhGallery
{
    public int Gid { get; init; }
    public string Token { get; init; } = "";
    public string Url { get; init; } = "";
    /// <summary>英文/罗马字标题。</summary>
    public string Title { get; init; } = "";
    /// <summary>日文原名（无则空）。</summary>
    public string TitleJpn { get; init; } = "";
    public string Category { get; init; } = "";
    public string Thumb { get; init; } = "";
    public string Uploader { get; init; } = "";
    /// <summary>unix 秒（字符串形态，别当数字）。</summary>
    public string Posted { get; init; } = "";
    public string FileCount { get; init; } = "";
    public string Filesize { get; init; } = "";
    public string Rating { get; init; } = "";
    /// <summary>原始 "namespace:value" 标签。</summary>
    public List<string> Tags { get; init; } = [];

    public string Id => EhClient.EhId(Gid, Token);
}
