using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using KomgaScraper.Core.Text;

namespace KomgaScraper.Core.Sources;

/// <summary>Mox（bookof.moe）系列详情：数据本身就是中文（繁体站，已统一转简体）。</summary>
public sealed record MoxSeries(
    string Id,               // BoF 内部 id（bookof.moe/b/{id}.htm）
    string Title,            // 主标题（简体）
    string OriTitle,         // 原名（繁体）
    string EngName,          // 英文名（name_subt 括号里，可空）
    string Summary,          // 简介（简体）
    IReadOnlyList<string> Authors,
    string Status,           // 已映射成 Komga 状态：ONGOING/ENDED/HIATUS/ABANDONED
    IReadOnlyList<string> Tags,
    string Publisher,
    bool IsAdult,
    string CoverFrameUrl)    // data_vol.php 帧页 URL（解析每卷封面用，可空）
{
    public string BoFUrl => $"https://bookof.moe/b/{Id}.htm";
    public string MoxUrl => $"https://kox.moe/c/{Id}.htm";
}

/// <summary>Mox 搜索结果（datainfo-B 行自带封面 URL，可直接给候选卡显示）。</summary>
public sealed record MoxSearchResult(string Id, string Title, string Author, string CoverUrl = "");

/// <summary>
/// Mox（bookof.moe）中文漫画库客户端 —— 脚本里的第三源（fetchMoeBookByName / fetchMoeBookByUrl）。
/// 搜索：GET /data_list.php?s={简→繁标题}&amp;p=1，解析 datainfo-B 行；
/// 详情：GET /b/{id}.htm 解析标题/简介/作者/状态/分类/版本/分级 + 封面帧页 URL；
/// 卷封面：GET 帧页解析 datainfo-V 行（img.mxomo.com 绝对地址或站内相对路径）。
/// 该站是繁体中文库 → 查询前 S2T，解析后统一 T2S。
/// </summary>
public sealed partial class MoxClient : IDisposable
{
    public const string DefaultBase = "https://bookof.moe";

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly IAppLogger _log;

    /// <summary>对 bookof.moe 的礼貌节流（两次请求起始的最小间隔）。mock 测试设 0。</summary>
    public int MinRequestIntervalMs { get; set; } = 500;

    private readonly SemaphoreSlim _paceGate = new(1, 1);
    private long _nextStartTick;

    /// <summary>bookof.moe 源站偶发 520/521/SSL 抖动（Cloudflare 源站错误，站点本身没问题）——这类错误值得重试。</summary>
    private sealed class MoxTransientException(string message) : Exception(message);

    public MoxClient(string? proxyUrl = null, string? proxyUser = null, string? proxyPassword = null,
        string? baseUrl = null, IAppLogger? log = null)
    {
        _log = log ?? NullLogger.Instance;
        _base = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl).TrimEnd('/');
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* 重复注册或平台不支持时忽略 */ }
        _http = HttpFactory.Create(
            new ProxyConfig(proxyUrl, proxyUser, proxyPassword),
            TimeSpan.FromSeconds(30),
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            warn: _log.Warn);
    }

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

    /// <summary>
    /// 站内请求严格串行（用户实测：不能并发）+ 失败退避重试。
    /// 520/521/522/524（Cloudflare 源站错误）、429、SSL/超时等抖动 → 最多 3 次重试；404 等确定性失败不重试。
    /// </summary>
    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await ScheduleStartAsync(ct).ConfigureAwait(false);
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode is 520 or 521 or 522 or 523 or 524 or 429 or 502 or 503 or 504)
                    throw new MoxTransientException($"Mox 请求失败 {(int)resp.StatusCode}: {url}");
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Mox 请求失败 {(int)resp.StatusCode}: {url}");
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                return DecodeHtml(bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { last = e; }   // HttpClient 超时
            catch (MoxTransientException e) { last = e; }
            catch (HttpRequestException e) { last = e; }                                       // SSL/DNS/连接错误
            if (attempt < 3)
            {
                var delay = TimeSpan.FromSeconds(attempt * 1.5);
                _log.Warn($"Mox 请求失败，{delay.TotalSeconds:F0}s 后重试（第 {attempt} 次）: {last?.Message}");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException($"Mox 请求失败: {url}");
    }

    /// <summary>bookof.moe 是老站，编码可能是 Big5/GBK；先按 UTF-8 解，出现替换符再降级。</summary>
    private static string DecodeHtml(byte[] bytes)
    {
        var utf8 = Encoding.UTF8.GetString(bytes);
        if (!utf8.Contains('\uFFFD')) return utf8;
        try
        {
            var big5 = Encoding.GetEncoding("big5").GetString(bytes);
            if (!big5.Contains('\uFFFD')) return big5;
        }
        catch { /* 缺 codepages 提供者时忽略 */ }
        return utf8;
    }

    // ================= 搜索 =================

    /// <summary>按名称搜索（站内是繁体 → 先 S2T 再查，与脚本 s2t 一致）。结果标题已转回简体。</summary>
    public async Task<List<MoxSearchResult>> SearchAsync(string seriesName, int limit = 8, CancellationToken ct = default)
    {
        var term = CJK.S2T(seriesName.Trim());
        var url = $"{_base}/data_list.php?s={Uri.EscapeDataString(term)}&p=1";
        var html = await GetTextAsync(url, ct).ConfigureAwait(false);
        var results = new List<MoxSearchResult>();
        // 与脚本一致：先按 <script> 切块，避免跨块误匹配；行格式 datainfo-B=分类,ID,标题,作者,出版日期
        foreach (var chunk in html.Split("<script>", StringSplitOptions.RemoveEmptyEntries))
        foreach (var line in chunk.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("datainfo-B=", StringComparison.Ordinal)) continue;
            var body = t["datainfo-B=".Length..];
            var segs = body.Split(',');
            if (segs.Length < 4) continue;
            var id = segs[1].Trim();
            var title = CJK.T2S(segs[2].Trim());
            var author = CJK.T2S(segs.Length > 3 ? segs[3].Trim() : "");
            if (id.Length == 0 || title.Length == 0) continue;
            if (results.Any(r => r.Id == id)) continue;
            // 第 6 段起是封面 URL（sign 参数可能含逗号 → 不能直接 Split 取，从第 5 个逗号后整段截取再正则抽 URL）
            var cover = "";
            var commaIdx = IndexOfNthComma(body, 5);
            if (commaIdx >= 0)
            {
                var cm = CoverUrlRegex().Match(body[commaIdx..]);
                if (cm.Success) cover = cm.Value;
            }
            results.Add(new MoxSearchResult(id, title, author.Length > 0 ? author : "未知作者", cover));
            if (results.Count >= Math.Max(1, limit)) break;
        }
        return results;
    }

    private static int IndexOfNthComma(string s, int nth)
    {
        var idx = -1;
        for (var i = 0; i < nth; i++)
        {
            idx = s.IndexOf(',', idx + 1);
            if (idx < 0) return -1;
        }
        return idx;
    }

    // ================= 详情 =================

    /// <summary>抓取 BoF 详情页并解析。失败抛异常，由调用方兜底。</summary>
    public async Task<MoxSeries> GetSeriesAsync(string moxId, CancellationToken ct = default)
    {
        var url = moxId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? moxId
            : $"{_base}/b/{moxId}.htm";
        var idFromUrl = Regex.Match(url, @"bookof\.moe/b/(.*?)\.htm") is { Success: true } m ? m.Groups[1].Value : moxId;
        var html = await GetTextAsync(url, ct).ConfigureAwait(false);

        // 封面帧页（window.iframe_action.location.href = "..."）
        var frameUrl = "";
        var fm = FrameUrlRegex().Match(html);
        if (fm.Success)
        {
            var p = fm.Groups[1].Value.Trim();
            frameUrl = p.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? p : _base + (p.StartsWith('/') ? p : '/' + p);
        }

        // r18 图标存在且未隐藏 → 成人
        var isAdult = false;
        var r18 = R18ImgRegex().Match(html);
        if (r18.Success)
        {
            var tag = r18.Groups[1].Value;
            isAdult = !tag.Contains("display", StringComparison.OrdinalIgnoreCase) ||
                      !tag.Replace(" ", "").Contains("display:none", StringComparison.OrdinalIgnoreCase);
        }

        // 原名保留繁体原样；主标题再转简体
        var oriTitle = RawTextOf(NameMainRegex().Match(html));
        if (oriTitle.Length == 0)
            throw new InvalidOperationException("Mox 详情页缺少主标题（.name_main），页面结构可能已变化");

        // name_subt：第一个常是 "(英文名) …"，第二个（或第一个）含 狀態/分類/版本 信息
        var subt = NameSubtRegex().Matches(html).Select(m => TextOf(m)).Where(s => s.Length > 0).ToList();
        var engName = "";
        var infoText = "";
        if (subt.Count > 0)
        {
            var em = Regex.Match(subt[0], @"\(([^)]+)\)\s*(.*)");
            if (em.Success)
            {
                engName = em.Groups[1].Value.Trim();
                infoText = em.Groups[2].Value.Trim();
            }
            else infoText = subt[0].Trim();
            if (subt.Count > 1 && infoText.Length == 0) infoText = subt[1].Trim();
            else if (subt.Count > 1 && subt[1].Contains('：')) infoText = infoText.Length > 0 ? infoText + " " + subt[1].Trim() : subt[1].Trim();
        }

        // 简介是纯文本（<br> 分行），尾部常带整段【…】备注 → 去掉（对齐脚本）
        var summary = CJK.T2S(SubjectMapper.CleanMultiline(
            TrailingDescNoteRegex().Replace(TextOf(DescRegex().Match(html)), "")));

        var authors = AuthorLinkRegex().Matches(html)
            .Select(m => CJK.T2S(BracketSuffixRegex().Replace(m.Groups[1].Value.Trim(), "").Trim()))
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 状态 / 分类 / 版本（站点是繁体：正则同时认简/繁写法，不依赖映射表的覆盖率）
        var info = infoText;
        string? status = null;
        var sm = StatusRegex().Match(info);
        if (sm.Success)
        {
            status = sm.Groups[1].Value.Trim() switch
            {
                "连载" or "連載" => "ONGOING",
                "完结" or "完結" => "ENDED",
                "停更" or "休刊" => "HIATUS",
                "弃坑" or "放棄" or "弃坑" => "ABANDONED",
                _ => null,
            };
        }

        var tags = new List<string>();
        var tm = CategoryRegex().Match(info);
        if (tm.Success)
            tags = tm.Groups[1].Value.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(t => CJK.T2S(t)).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        var publisher = "";
        var pm = VersionRegex().Match(info);
        if (pm.Success) publisher = CJK.T2S(pm.Groups[1].Value.Trim());

        return new MoxSeries(idFromUrl, CJK.T2S(oriTitle), oriTitle,
            CJK.T2S(engName), summary, authors, status ?? "", tags, publisher, isAdult, frameUrl);
    }

    // ================= 每卷封面 =================

    /// <summary>抓取封面帧页（data_vol.php / temp_...），解析 datainfo-V 行得到按卷序排列的封面 URL。</summary>
    public async Task<List<string>> GetVolumeCoverUrlsAsync(string frameUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(frameUrl)) return [];
        var html = await GetTextAsync(frameUrl, ct).ConfigureAwait(false);
        var covers = new List<string>();
        foreach (var line in html.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("datainfo-V=", StringComparison.Ordinal)) continue;
            var segs = t["datainfo-V=".Length..].Split(',');
            // datainfo-V=ID,标题,类型,未知,封面URL,未知
            if (segs.Length < 5) continue;
            var u = segs[4].Trim();
            if (u.Length == 0) continue;
            if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                u = _base + (u.StartsWith('/') ? u : '/' + u);
            if (!covers.Contains(u, StringComparer.Ordinal)) covers.Add(u);
        }
        return covers;
    }

    // ---------- 解析辅助 ----------

    /// <summary>取匹配组的 HTML 内文 → 去标签 → 转简体 → 清空白。</summary>
    private static string TextOf(Match m)
    {
        if (!m.Success) return "";
        var inner = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
        inner = HtmlTagRegex().Replace(inner, "");
        return CJK.T2S(SubjectMapper.CleanMultiline(inner));
    }

    /// <summary>同 TextOf，但不转简体（保留繁体原样，用于「原名」）。</summary>
    private static string RawTextOf(Match m)
    {
        if (!m.Success) return "";
        var inner = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
        inner = HtmlTagRegex().Replace(inner, "");
        return SubjectMapper.CleanMultiline(inner).Trim();
    }

    [GeneratedRegex(@"window\.iframe_action\.location\.href\s*=\s*""([^""]+)""")]
    private static partial Regex FrameUrlRegex();

    [GeneratedRegex(@"(<img[^>]*id=""logo_r18""[^>]*>)")]
    private static partial Regex R18ImgRegex();

    [GeneratedRegex(@"class=""name_main""[^>]*>([^<]+)<")]
    private static partial Regex NameMainRegex();

    [GeneratedRegex(@"class=""name_subt""[^>]*>(.*?)<(?:/|\w)")]
    private static partial Regex NameSubtRegex();

    [GeneratedRegex(@"id=""div_desctext""[^>]*>(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex DescRegex();

    [GeneratedRegex(@"<a[^>]*href=""https?://bookof\.moe/s/AUT[^""]*""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorLinkRegex();

    [GeneratedRegex(@"[\s\S]*[状狀][态態]：([^ \t]*(?:\s|$))")]
    private static partial Regex StatusRegex();

    [GeneratedRegex(@"分[类類]：(.*?)(?=\s*(?:版[本本]：|[最最][后後]出版：|$))")]
    private static partial Regex CategoryRegex();

    [GeneratedRegex(@"版[本本]：([^ \t]*)(?:\s|[最最][后後]出版：|$)")]
    private static partial Regex VersionRegex();

    /// <summary>简介尾部整段【…】备注（脚本 t2s(desc.replace(/\【.*?\】$/,''))）。</summary>
    [GeneratedRegex(@"\s*【[^】]*】\s*$")]
    private static partial Regex TrailingDescNoteRegex();

    /// <summary>作者名尾部的括注（书名号/圆括号等），对齐脚本 authors 清理。</summary>
    [GeneratedRegex(@"[《【（\[\(（][^》】）\]\)）]*[》】）\]\)）]\s*$")]
    private static partial Regex BracketSuffixRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    /// <summary>搜索行里的封面 URL（kmimg.moex.ink 等，行尾可能跟着 `", "* );` 的 JS 残片）。</summary>
    [GeneratedRegex(@"https?://[^\s""]+")]
    private static partial Regex CoverUrlRegex();

    public void Dispose() => _http.Dispose();
}