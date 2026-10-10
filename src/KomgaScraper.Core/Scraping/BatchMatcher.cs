using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Sources;
using KomgaScraper.Core.Text;
using System.Text;

namespace KomgaScraper.Core.Scraping;

/// <summary>批量精确匹配统计（对齐脚本 batchStats）。</summary>
public sealed class BatchStats
{
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int SkippedCount { get; set; }
    public int AddedToCollectionCount { get; set; }
    public int Total { get; set; }
    public override string ToString() =>
        $"成功 {SuccessCount}，失败 {FailureCount}，跳过 {SkippedCount}，共 {Total}" +
        (AddedToCollectionCount > 0 ? $"（{AddedToCollectionCount} 个已加入\"{ManualMatchCollection.CollectionName}\"收藏夹）" : "");
}

/// <summary>单个系列的批量匹配结果。</summary>
public sealed record BatchMatchResult
{
    public bool Success { get; init; }
    public bool Skipped { get; init; }
    public string SeriesId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? MatchedTitle { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 批量精确匹配（对齐脚本 preciseMatchSeries + batchMatchTarget）：
/// 跳过已有所属源链接的系列（可强制刷新），标题/原名/别名精确匹配，
/// 无精确匹配时依次尝试：BookWalker 本地索引 → AI 智能选择（如已启用）→ 百度百科（冷门漫画兜底），
/// 失败加入"手动匹配"收藏夹。
/// </summary>
public sealed class BatchMatcher(KomgaClient komga, BangumiClient bangumi, IAppLogger log)
{
    private readonly ManualMatchCollection _manualMatch = new(komga, log);
    private readonly ScrapeService _scrape = new(komga, bangumi, log);

    /// <summary>逐条库写入改动流（转发内部 ScrapeService，供 UI 的 Dry-run 预览面板）。</summary>
    public event Action<ScrapeChange>? ChangeObserved
    {
        add => _scrape.ChangeObserved += value;
        remove => _scrape.ChangeObserved -= value;
    }

    /// <summary>Bangumi 匹配类型（platform 过滤）：漫画 / 小说。</summary>
    public string MatchTypePlatform { get; set; } = MatchType.Manga;

    private BookWalkerIndex? _bwIndex;

    /// <summary>BookWalker 本地索引（可选回退源）。同时供内部 ScrapeService 补 ISBN/发售日。</summary>
    public BookWalkerIndex? BwIndex
    {
        get => _bwIndex;
        set { _bwIndex = value; _scrape.BwIndex = value; }
    }

    /// <summary>是否抓取每卷详情（简介/发售日/ISBN）。</summary>
    public bool FetchVolumeData { get; set; }

    /// <summary>是否写入 BookWalker 类型/题材标签与阅读方向。</summary>
    public bool BwWriteTags
    {
        get => _scrape.BwWriteTags;
        set => _scrape.BwWriteTags = value;
    }

    /// <summary>刮削状态库（转发给内部 ScrapeService：记录系列封面/简介是否已刮，供列表列与筛选用）。</summary>
    public ScrapeStateStore? State
    {
        get => _scrape.State;
        set => _scrape.State = value;
    }

    /// <summary>AI 匹配客户端（可选，多候选模糊时智能挑选）。转发给内部 ScrapeService，翻译同源生效。</summary>
    public AiClient? Ai
    {
        get => _scrape.Ai;
        set => _scrape.Ai = value;
    }

    /// <summary>AI 简介翻译开关（转发给内部 ScrapeService）。批量链路（精确/覆盖匹配）的
    /// 卷简介翻译也走这里——此前只有主窗体自建的 ScrapeService 拿到开关，批量刮出的卷简介全是日文。</summary>
    public bool TranslateSummary
    {
        get => _scrape.TranslateSummary;
        set => _scrape.TranslateSummary = value;
    }

    private MoxClient? _mox;

    /// <summary>Mox（bookof.moe）中文漫画库（可选第三源）。Bangumi/BookWalker 都没命中时按中文名搜。</summary>
    public MoxClient? Mox
    {
        get => _mox;
        set { _mox = value; _scrape.Mox = value; }
    }

    private BaikeClient? _baike;

    /// <summary>百度百科（可选最终兜底源）。全部源都没命中时按中文名查词条（冷门漫画的救命稻草）。</summary>
    public BaikeClient? Baike
    {
        get => _baike;
        set { _baike = value; _scrape.Baike = value; }
    }

    /// <summary>MangaDex（英文圈图源，免密钥公开 API）。百科兜底前的英文区回退。</summary>
    public MangaDexClient? MangaDex { get; set; }

    /// <summary>版本/格式标记的黑白名单（转发给内部 ScrapeService；null/空 = 只用内置词表）。</summary>
    public List<string>? MarkerWhitelist
    {
        get => _scrape.MarkerWhitelist;
        set => _scrape.MarkerWhitelist = value;
    }

    public List<string>? MarkerBlacklist
    {
        get => _scrape.MarkerBlacklist;
        set => _scrape.MarkerBlacklist = value;
    }

    /// <summary>Dry-run：只记录将执行的动作，不实际 PATCH / 建收藏夹。</summary>
    public bool DryRun { get; set; }

    /// <summary>同时刮削的系列数量（1 = 逐个串行；默认 3，设置里可调）。
    /// Bangumi API 侧由 BangumiClient 的全局节流按 ~3.3 次/秒排开，并发不会超速。</summary>
    public int SeriesConcurrency { get; set; } = 3;

    private ProxyConfig _proxy = ProxyConfig.None;

    /// <summary>外部封面下载代理（转发给内部 ScrapeService）。</summary>
    public ProxyConfig Proxy
    {
        get => _proxy;
        set { _proxy = value; _scrape.Proxy = value; }
    }

    private ScrapeOptions Opts(SyncType syncType, bool replaceMatch = false, bool coverOnly = false) =>
        new() { SyncType = syncType, FetchVolumeData = FetchVolumeData, DryRun = DryRun, ReplaceMatch = replaceMatch, CoverOnly = coverOnly };

    /// <summary>
    /// 封面独立刮削：与覆盖匹配完全同一条自动匹配链路（Bangumi 精确 → BookWalker → AI 原名重搜 →
    /// Mox → MangaDex → 百度百科），但 ScrapeOptions.CoverOnly = true —— 各刮削路径只上传
    /// 系列封面与卷封面，元数据一个字段不写。用于封面失败（如百科 400）后的单独补救。
    /// </summary>
    public async Task<BatchStats> ReMatchCoverOnlyAsync(
        IReadOnlyList<SeriesDto> seriesList, CancellationToken ct = default)
    {
        var stats = new BatchStats { Total = seriesList.Count };
        _scrape.ManualMatch = _manualMatch;

        var total = seriesList.Count;
        var started = 0;
        await Parallel.ForEachAsync(seriesList,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(SeriesConcurrency, 1, 8),
                CancellationToken = ct,
            },
            async (series, token) =>
            {
                token.ThrowIfCancellationRequested();
                var n = Interlocked.Increment(ref started);
                log.Progress(n, total, $"《{series.Name}》");
                try
                {
                    var r = await PreciseMatchCoverOnlyAsync(series, stats: null, ct: token);
                    lock (stats)
                    {
                        if (r.Success) stats.SuccessCount++;
                        else stats.FailureCount++;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    lock (stats) stats.FailureCount++;
                    log.Error($"《{series.Name}》异常: {e.Message}");
                }
            }).ConfigureAwait(false);
        return stats;
    }

    /// <summary>PreciseMatchAsync 的 CoverOnly 版：同一套搜索/回退逻辑，全部刮削走 CoverOnly 选项。</summary>
    private async Task<BatchMatchResult> PreciseMatchCoverOnlyAsync(
        SeriesDto series, BatchStats? stats = null, CancellationToken ct = default)
    {
        var oriTitle = series.Name.Trim();
        var result = new BatchMatchResult { SeriesId = series.Id, Name = oriTitle };
        try
        {
            var komgaMeta = series.Metadata?.Links != null
                ? series.Metadata
                : await komga.GetSeriesMetadataAsync(series.Id, ct);
            var seriesName = oriTitle;

            var terms = TitleParser.ExtractSeriesTitles(seriesName, 4);
            if (terms.Count == 0 || string.IsNullOrEmpty(terms[0]))
                return result with { Error = "无法从标题解析搜索词" };
            var searchTerm = terms[0];

            var searchResults = new List<SearchCandidate>();
            SearchCandidate? precise = null;
            foreach (var term in terms)
            {
                List<SearchCandidate> r;
                try { r = await bangumi.SearchSubjectsAsync(term, MatchTypePlatform, limit: 0, ct); }
                catch (Exception e) { log.Warn($"《{seriesName}》\"{term}\" 搜索失败: {e.Message}"); continue; }
                foreach (var it in r)
                    if (searchResults.All(x => x.Id != it.Id)) searchResults.Add(it);
                precise = FindPreciseMatch(r, TitleParser.ExtractAndNormalizeTitle(term));
                if (precise != null) { searchTerm = term; break; }
            }

            if (precise == null)
            {
                // 与覆盖匹配相同的回退序（仅封面场景里 BW/百科这些源的封面就是全部产出）
                var bw = BwIndex?.FindExact(terms[0]) ?? BwIndex?.FindExact(terms.Skip(1).FirstOrDefault() ?? "");
                if (bw != null)
                {
                    var bwResult = await _scrape.ScrapeFromBookWalkerAsync(series.Id, bw,
                        Opts(SyncType.All, coverOnly: true), series, komgaMeta, ct);
                    if (bwResult.Success) return result with { Success = true, MatchedTitle = bw.Title };
                }

                AiClient.IdentifyResult? identify = null;
                if (Ai != null)
                {
                    identify = await IdentifySafeAsync(seriesName, terms, ct);
                    if (identify != null)
                    {
                        var aiTerms = new List<string>();
                        if (identify.Jp.Length > 0) aiTerms.Add(identify.Jp);
                        if (identify.En.Length > 0) aiTerms.Add(identify.En);
                        if (identify.Cn.Length > 0 && !aiTerms.Contains(identify.Cn, StringComparer.Ordinal)) aiTerms.Add(identify.Cn);
                        foreach (var t in aiTerms)
                        {
                            List<SearchCandidate> r;
                            try { r = await bangumi.SearchSubjectsAsync(t, MatchTypePlatform, 0, ct); }
                            catch (Exception e) { log.Warn($"《{seriesName}》AI 原名 \"{t}\" 搜索失败: {e.Message}"); continue; }
                            precise = FindPreciseMatch(r, TitleParser.ExtractAndNormalizeTitle(t));
                            if (precise != null) break;
                        }
                        if (precise == null && BwIndex != null)
                        {
                            foreach (var t in aiTerms)
                            {
                                var bwAi = BwIndex.FindExact(t);
                                if (bwAi == null) continue;
                                var r2 = await _scrape.ScrapeFromBookWalkerAsync(series.Id, bwAi,
                                    Opts(SyncType.All, coverOnly: true), series, komgaMeta, ct);
                                if (r2.Success) return result with { Success = true, MatchedTitle = bwAi.Title };
                                break;
                            }
                        }

                        // Mox：AI 给的通行中文名重搜（每卷封面来自帧页）
                        if (Mox != null)
                        {
                            var moxTerms = new List<string>();
                            if (identify.Cn.Length > 0) moxTerms.Add(identify.Cn);
                            moxTerms.Add(terms[0]);
                            foreach (var t in moxTerms.Distinct(StringComparer.Ordinal).Take(2))
                            {
                                List<MoxSearchResult> ml;
                                try { ml = await Mox.SearchAsync(t, 8, ct); }
                                catch (Exception e) { log.Warn($"《{seriesName}》Mox 搜索失败（\"{t}\"）: {e.Message}"); continue; }
                                if (ml.Count == 0) continue;
                                if (OriginFilter.JapaneseOnly)
                                {
                                    ml = ml.Where(m => !OriginFilter.ContainsHangul(m.Title)).ToList();
                                    if (ml.Count == 0) continue;
                                }
                                var moxCands = ml.Select(m => new SearchCandidate(0, m.Title, m.Title, m.Author, "", m.CoverUrl, SourceMox, null, m.Id)).ToList();
                                var moxPick = FindPreciseMatch(moxCands, TitleParser.ExtractAndNormalizeTitle(t));
                                if (moxPick == null && moxCands.Count > 1)
                                {
                                    try { moxPick = await Ai!.PickBestAsync(seriesName, moxCands, ct); }
                                    catch (Exception e) { log.Warn($"《{seriesName}》Mox 候选 AI 选择失败: {e.Message}"); }
                                }
                                if (moxPick is { MoxId: { Length: > 0 } moxId })
                                {
                                    var moxDetail = await Mox.GetSeriesAsync(moxId, ct);
                                    if (OriginFilter.JapaneseOnly && OriginFilter.LooksKorean(
                                            moxDetail.Title, moxDetail.OriTitle, moxDetail.EngName, moxDetail.Tags, null))
                                    {
                                        log.Warn($"《{seriesName}》Mox 命中的《{moxDetail.Title}》疑似韩漫，跳过");
                                    }
                                    else
                                    {
                                        var moxResult = await _scrape.ScrapeFromMoxAsync(series.Id, moxDetail,
                                            Opts(SyncType.All, coverOnly: true), series, komgaMeta, ct);
                                        if (moxResult.Success)
                                            return result with { Success = true, MatchedTitle = moxDetail.Title };
                                    }
                                }
                                break;
                            }
                        }
                    }
                }

                if (MangaDex != null)
                {
                    foreach (var t in terms.Take(2))
                    {
                        List<MangaDexSeries> mdList;
                        try { mdList = await MangaDex.SearchAsync(t, 8, ct); }
                        catch (Exception e) { log.Warn($"《{seriesName}》MangaDex 搜索失败: {e.Message}"); continue; }
                        if (mdList.Count == 0) continue;
                        if (OriginFilter.JapaneseOnly)
                        {
                            mdList = mdList.Where(m => string.Equals(m.OriginalLanguage, "ja", StringComparison.OrdinalIgnoreCase)).ToList();
                            if (mdList.Count == 0) continue;
                        }
                        var mdPick = FindPreciseMatch(
                            mdList.Select(m => new SearchCandidate(0, m.Title, m.OriTitle, "", "", m.CoverUrl, SourceMangaDex, MdId: m.Id)).ToList(),
                            TitleParser.ExtractAndNormalizeTitle(t));
                        var mdHit = mdList.FirstOrDefault(m => m.Id == mdPick?.MdId);
                        if (mdHit == null) continue;
                        var mdResult = await _scrape.ScrapeFromMangaDexAsync(series.Id, mdHit,
                            Opts(SyncType.All, coverOnly: true), series, komgaMeta, ct);
                        if (mdResult.Success) return result with { Success = true, MatchedTitle = mdHit.Title };
                        break;
                    }
                }

                if (Baike != null)
                {
                    // 百科搜索词：AI 识别出的通行名优先（AI 认得出作者名/汉化组/出版社标签，
                    // 识别的作品名不会撞人物词条），文件夹分词殿后兜底。
                    var bkTerms = new List<string>();
                    if (identify is { Cn.Length: > 0 }) bkTerms.Add(identify.Cn);
                    if (identify is { Jp.Length: > 0 }) bkTerms.Add(identify.Jp);
                    bkTerms.AddRange(terms);
                    foreach (var t in bkTerms.Distinct(StringComparer.Ordinal).Take(4))
                    {
                        BaikeCard? card;
                        try { card = await Baike.SearchCardAsync(t, ct); }
                        catch (Exception e) { log.Warn($"《{seriesName}》百度百科搜索失败: {e.Message}"); continue; }
                        if (card == null) continue;
                        if (!card.LooksLikeMangaWork)
                        {
                            log.Warn($"《{seriesName}》百度百科《{card.Title}》非漫画作品词条（人物/影视/小说，概述图不是漫画封面），跳过");
                            continue;
                        }
                        var bkCands = new List<SearchCandidate>
                        {
                            new(0, card.Title, card.Field("原版名称"), card.Field("作者"), "", null, SourceBaike),
                        };
                        if (FindPreciseMatch(bkCands, TitleParser.ExtractAndNormalizeTitle(t)) == null) continue;
                        var bkResult = await _scrape.ScrapeFromBaikeAsync(series.Id, card,
                            Opts(SyncType.All, coverOnly: true), series, komgaMeta, ct);
                        if (bkResult.Success) return result with { Success = true, MatchedTitle = card.Title };
                        break;
                    }
                }

                var err = searchResults.Count > 0 ? "无精确匹配（标题/原名/别名）" : "搜索无结果";
                log.Warn($"《{seriesName}》(词:\"{searchTerm}\") {err}");
                return result with { Error = err };
            }

            var display = FirstNonEmpty(precise.Title, precise.OriTitle, "未知匹配标题");
            log.Info($"《{seriesName}》封面刮削命中:《{display}》 subject={precise.Id}，刮削中…");
            var scrapeResult = await _scrape.ScrapeAsync(series.Id, precise.Id,
                Opts(SyncType.All, replaceMatch: true, coverOnly: true), series, komgaMeta, ct);
            if (!scrapeResult.Success)
                return result with { Error = scrapeResult.Error };
            return result with { Success = true, MatchedTitle = display };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException oce)
        {
            log.Error($"《{result.Name}》请求超时/被中止（非手动取消），按失败处理: {oce.Message}");
            return result with { Error = $"请求超时/中止（非取消）: {oce.Message}" };
        }
        catch (Exception e)
        {
            log.Error($"《{result.Name}》封面刮削出错: {e.Message}");
            return result with { Error = e.Message };
        }
    }

    /// <summary>
    /// 对单系列执行精确匹配并刮削。
    /// <paramref name="forceRematch"/>（覆盖匹配）= 完全无视旧的匹配结果：
    /// 不跳过已有源链接、搜索词只用文件夹名（元数据标题可能已被旧匹配污染）、
    /// 命中后以 ReplaceMatch 让新条目覆盖旧数据；失败的系列保持原样，
    /// 也不进"手动匹配"收藏夹（旧链接还在，别当"未匹配"记进去）。
    /// </summary>
    public async Task<BatchMatchResult> PreciseMatchAsync(
        SeriesDto series, SyncType syncType, bool forceRefreshBtv,
        BatchStats? stats = null, CancellationToken ct = default, bool forceRematch = false)
    {
        var oriTitle = forceRematch ? series.Name.Trim() : series.Metadata?.Title?.Trim() ?? series.Name.Trim();
        var result = new BatchMatchResult { SeriesId = series.Id, Name = oriTitle };

        try
        {
            // 列表接口（POST /series/list）本来就带完整元数据（含 links/全部锁字段），
            // 而 GET /series/{id}/metadata 在部分 Komga（实测 comic.wenjian.de）返回 405 → 永远是 null，
            // 后果是「已刮削的系列检测不到 Btv 链接」→ 每次批量都全量重刮。优先用列表数据，缺 links 才回退 GET。
            var komgaMeta = forceRematch
                ? series.Metadata   // 覆盖匹配：旧元数据只作清理用（ClearedForReplace），不作为任何搜索材料
                : series.Metadata?.Links != null
                    ? series.Metadata
                    : await komga.GetSeriesMetadataAsync(series.Id, ct);
            var seriesName = forceRematch
                ? series.Name.Trim()   // 文件夹名是用户的原始命名（含正确中文名），未被旧匹配污染
                : !string.IsNullOrWhiteSpace(komgaMeta?.Title) ? komgaMeta.Title.Trim()
                : !string.IsNullOrWhiteSpace(oriTitle) ? oriTitle
                : series.Name.Trim();
            result = result with { Name = seriesName };

            // 已有源链接 → 跳过或强制刷新（覆盖匹配：无视旧链接，直接重走匹配流程）
            if (!forceRematch)
            {
                var existingLink = komgaMeta?.Links?.FirstOrDefault(l =>
                    !string.IsNullOrEmpty(l.Url) && IsBtvLink(l.Url));
                if (existingLink != null)
                {
                    if (forceRefreshBtv)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(existingLink.Url, @"/subject/(\d+)");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var existingId))
                        {
                            log.Info($"《{seriesName}》已存在 Btv 链接，强制刷新中…");
                            // 系列与元数据都已拿到，传进去省两次重复 GET
                            var r = await _scrape.ScrapeAsync(series.Id, existingId,
                                Opts(syncType, forceRematch), series, komgaMeta, ct);
                            return r.Success
                                ? result with { Success = true, MatchedTitle = r.MatchedTitle }
                                : result with { Error = r.Error };
                        }
                    }
                    log.Info($"《{seriesName}》已存在 Btv 链接，跳过");
                    return result with { Success = true, Skipped = true };
                }
            }

            // 候选搜索词：中文压制组命名下真正的标题可能排在第 2~4 位，逐个试到命中为止
            var terms = TitleParser.ExtractSeriesTitles(seriesName, 4);
            if (terms.Count == 0 || string.IsNullOrEmpty(terms[0]))
            {
                return result with { Error = "无法从标题解析搜索词" };
            }
            var searchTerm = terms[0];

            var searchResults = new List<SearchCandidate>();
            SearchCandidate? precise = null;
            var usedBackup = false;
            foreach (var term in terms)
            {
                if (!string.Equals(term, searchTerm, StringComparison.Ordinal))
                {
                    try
                    {
                        var r = await bangumi.SearchSubjectsAsync(term, MatchTypePlatform, limit: 0, ct);
                        foreach (var it in r)
                            if (searchResults.All(x => x.Id != it.Id)) searchResults.Add(it);
                        precise = FindPreciseMatch(r, TitleParser.ExtractAndNormalizeTitle(term));
                    }
                    catch (Exception e)
                    {
                        log.Warn($"《{seriesName}》备选词 \"{term}\" 搜索失败: {e.Message}");
                        continue;
                    }
                }
                else
                {
                    searchResults = await bangumi.SearchSubjectsAsync(term, MatchTypePlatform, limit: 0, ct);
                    precise = FindPreciseMatch(searchResults, TitleParser.ExtractAndNormalizeTitle(term));
                }
                if (precise != null)
                {
                    if (!string.Equals(term, searchTerm, StringComparison.Ordinal)) usedBackup = true;
                    searchTerm = term;
                    break;
                }
            }

            if (precise == null)
            {
                // ---- 回退 1：BookWalker 本地索引精确匹配（每个候选词都试一遍） ----
                var bw = BwIndex?.FindExact(terms[0], terms.Count > 1 ? terms[1] : "");
                if (bw == null)
                {
                    foreach (var t in terms.Skip(1))
                    {
                        bw = BwIndex?.FindExact(t);
                        if (bw != null) break;
                    }
                }
                if (bw != null)
                {
                    log.Info($"《{seriesName}》Bangumi 无精确匹配，BookWalker 命中:《{bw.Title}》，刮削中…");
                    var bwResult = await _scrape.ScrapeFromBookWalkerAsync(series.Id, bw,
                        Opts(syncType, forceRematch), series, komgaMeta, ct);
                    if (bwResult.Success)
                        return result with { Success = true, MatchedTitle = bw.Title };
                    // 单源失败不再终止整条链，继续试其它源（原来直接返回错误，Mox 没机会救场）
                    log.Warn($"《{seriesName}》BookWalker 刮削失败，继续尝试其它源: {bwResult.Error}");
                }

                // ---- 回退 2：问 AI「这是哪部作品」→ 拿日文/中/英原名重搜三源 ----
                // 简中/繁中/日文/英文名差异巨大（海贼王≠ワンピース≠ONE PIECE），中文名直接搜
                // Bangumi 多半是噪声，AI 在噪声候选里挑也挑不准（实测 40+ 候选全判不匹配）；
                // 让 AI 直接报原名再重搜，命中率高得多。AI 认不出的作品才走 3/4 兜底。
                AiClient.IdentifyResult? identify = null;
                if (Ai != null)
                {
                    identify = await IdentifySafeAsync(seriesName, terms, ct);
                    if (identify != null)
                    {
                        var aiTerms = new List<string>();
                        if (identify.Jp.Length > 0) aiTerms.Add(identify.Jp);
                        if (identify.En.Length > 0) aiTerms.Add(identify.En);
                        if (identify.Cn.Length > 0 && !aiTerms.Contains(identify.Cn, StringComparer.Ordinal)) aiTerms.Add(identify.Cn);
                        log.Info($"《{seriesName}》AI 识别为《{identify.Jp}》" +
                                 (identify.Cn.Length > 0 ? $"（《{identify.Cn}》）" : "") +
                                 (identify.En.Length > 0 ? $" 英文 {identify.En}" : "") +
                                 (identify.Confident ? "" : "（AI 不太确定）") + "，按原名重搜…");

                        // 2a. Bangumi 按原名重搜 → 精确匹配 → AI 从候选里挑
                        SearchCandidate? aiPrecise = null;
                        var aiCands = new List<SearchCandidate>();
                        foreach (var t in aiTerms)
                        {
                            try
                            {
                                var r = await bangumi.SearchSubjectsAsync(t, MatchTypePlatform, 0, ct);
                                foreach (var it in r)
                                    if (aiCands.All(x => x.Id != it.Id)) aiCands.Add(it);
                                aiPrecise = FindPreciseMatch(r, TitleParser.ExtractAndNormalizeTitle(t));
                                if (aiPrecise != null) break;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception e) { log.Warn($"《{seriesName}》AI 原名 \"{t}\" 搜索失败: {e.Message}"); }
                        }

                        if (aiPrecise == null && aiCands.Count > 1)
                        {
                            try
                            {
                                log.Info($"《{seriesName}》原名重搜候选 {aiCands.Count} 个，AI 选择中…");
                                aiPrecise = await Ai.PickBestAsync(seriesName, RankCandidates(aiCands, aiTerms[0], 12), ct);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception e) { log.Warn($"《{seriesName}》AI 选择失败: {e.Message}"); }
                        }

                        if (aiPrecise != null)
                        {
                            var aiDisplay = FirstNonEmpty(aiPrecise.Title, aiPrecise.OriTitle, "未知匹配标题");
                            log.Info($"《{seriesName}》AI 原名重搜命中:《{aiDisplay}》 subject={aiPrecise.Id}，刮削中…");
                            var aiScrape = await _scrape.ScrapeAsync(series.Id, aiPrecise.Id,
                                Opts(syncType, forceRematch), series, komgaMeta, ct);
                            if (aiScrape.Success)
                                return result with { Success = true, MatchedTitle = aiDisplay };
                            log.Warn($"《{seriesName}》AI 原名重搜命中项刮削失败，继续其它源: {aiScrape.Error}");
                        }

                        // 2b. BookWalker 用日文/英文原名查（BW 目录只有日英标题，本地中文名永远查不到的那条正路）
                        if (BwIndex != null)
                        {
                            foreach (var t in aiTerms)
                            {
                                var bwAi = BwIndex.FindExact(t);
                                if (bwAi == null) continue;
                                log.Info($"《{seriesName}》AI 原名「{t}」BookWalker 命中:《{bwAi.Title}》，刮削中…");
                                var bwAiResult = await _scrape.ScrapeFromBookWalkerAsync(series.Id, bwAi,
                                    Opts(syncType, forceRematch), series, komgaMeta, ct);
                                if (bwAiResult.Success)
                                    return result with { Success = true, MatchedTitle = bwAi.Title };
                                log.Warn($"《{seriesName}》BookWalker（AI 原名）刮削失败，继续其它源: {bwAiResult.Error}");
                                break;
                            }
                        }

                        // 2c. Mox 用 AI 给的中文名查（AI 识别出的通行译名往往比文件名里的更准）
                        if (Mox != null)
                        {
                            var moxCnTerms = new List<string>();
                            if (identify.Cn.Length > 0) moxCnTerms.Add(identify.Cn);
                            moxCnTerms.Add(terms[0]);   // 原词兜底
                            foreach (var t in moxCnTerms.Distinct(StringComparer.Ordinal).Take(2))
                            {
                                List<MoxSearchResult> ml;
                                try { ml = await Mox.SearchAsync(t, 8, ct); }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception e)
                                {
                                    log.Warn($"《{seriesName}》Mox 搜索失败（\"{t}\"）: {e.Message}");
                                    continue;
                                }
                                if (ml.Count == 0) continue;
                                if (OriginFilter.JapaneseOnly)
                                {
                                    var kBefore = ml.Count;
                                    ml = ml.Where(m => !OriginFilter.ContainsHangul(m.Title)).ToList();
                                    if (ml.Count < kBefore)
                                        log.Info($"《{seriesName}》日漫过滤：Mox 剔除 {kBefore - ml.Count} 个疑似韩漫候选");
                                    if (ml.Count == 0) continue;
                                }
                                var mCands = ml.Select(m => new SearchCandidate(
                                    0, m.Title, m.Title, m.Author, "", m.CoverUrl, SourceMox, null, m.Id)).ToList();
                                var mPick = FindPreciseMatch(mCands, TitleParser.ExtractAndNormalizeTitle(t));
                                if (mPick == null && Ai != null && mCands.Count > 1)
                                {
                                    try { mPick = await Ai.PickBestAsync(seriesName, mCands, ct); }
                                    catch (OperationCanceledException) { throw; }
                                    catch (Exception e) { log.Warn($"《{seriesName}》Mox 候选 AI 选择失败: {e.Message}"); }
                                }
                                if (mPick is { MoxId: { Length: > 0 } moxId })
                                {
                                    try
                                    {
                                        log.Info($"《{seriesName}》Mox 命中:《{mPick.Title}》，刮削中…");
                                        var moxDetail = await Mox.GetSeriesAsync(moxId, ct);
                                        if (OriginFilter.JapaneseOnly && OriginFilter.LooksKorean(
                                                moxDetail.Title, moxDetail.OriTitle, moxDetail.EngName,
                                                moxDetail.Tags, null))
                                        {
                                            log.Warn($"《{seriesName}》Mox 命中的《{moxDetail.Title}》疑似韩漫，跳过（只匹配日漫）");
                                            break;
                                        }
                                        var moxResult = await _scrape.ScrapeFromMoxAsync(series.Id, moxDetail,
                                            Opts(syncType, forceRematch), series, komgaMeta, ct);
                                        if (moxResult.Success)
                                            return result with { Success = true, MatchedTitle = moxResult.MatchedTitle ?? moxDetail.Title };
                                        log.Warn($"《{seriesName}》Mox 刮削失败，继续其它源: {moxResult.Error}");
                                    }
                                    catch (OperationCanceledException) { throw; }
                                    catch (Exception e) { log.Warn($"《{seriesName}》Mox 刮削失败: {e.Message}"); }
                                }
                                break;   // 这个词搜到候选了（哪怕没刮成）就不再换词
                            }
                        }
                    }
                }

                // ---- 回退 3/4：AI 没配置或 AI 认不出这部作品时的老兜底 ----
                if (Ai == null || identify == null)
                {
                    // ---- 回退 3：Mox（bookof.moe 中文漫画库）——中文/繁体标题直接搜 ----
                    // 本地是中文名、Bangumi 搜不到的书，很多能在 Mox 命中（港台汉化/绝版书覆盖广）
                    if (Mox != null)
                    {
                        var moxList = new List<MoxSearchResult>();
                        foreach (var t in terms.Take(2))
                        {
                            try
                            {
                                moxList = await Mox.SearchAsync(t, 8, ct);
                                if (moxList.Count > 0) break;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception e)
                            {
                                log.Warn($"《{seriesName}》Mox 搜索失败（\"{t}\"）: {e.Message}");
                            }
                        }
                        if (moxList.Count > 0 && OriginFilter.JapaneseOnly)
                        {
                            var kBefore = moxList.Count;
                            moxList = moxList.Where(m => !OriginFilter.ContainsHangul(m.Title)).ToList();
                            if (moxList.Count < kBefore)
                                log.Info($"《{seriesName}》日漫过滤：Mox 剔除 {kBefore - moxList.Count} 个疑似韩漫候选");
                        }
                        if (moxList.Count > 0)
                        {
                            var moxCands = moxList.Select(m => new SearchCandidate(
                                0, m.Title, m.Title, m.Author, "", m.CoverUrl, SourceMox, null, m.Id)).ToList();
                            // 中文标题在 Mox 里就是精确字符串 → 复用精确匹配逻辑
                            var moxPick = FindPreciseMatch(moxCands, TitleParser.ExtractAndNormalizeTitle(searchTerm));
                            if (moxPick == null && Ai != null && moxCands.Count > 1)
                            {
                                try
                                {
                                    log.Info($"《{seriesName}》Mox 候选 {moxCands.Count} 个，AI 选择中…");
                                    moxPick = await Ai.PickBestAsync(seriesName, moxCands, ct);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception e)
                                {
                                    log.Warn($"《{seriesName}》Mox 候选 AI 选择失败: {e.Message}");
                                }
                            }
                            if (moxPick is { MoxId: { Length: > 0 } moxId })
                            {
                                try
                                {
                                    log.Info($"《{seriesName}》Mox 命中:《{moxPick.Title}》，刮削中…");
                                    var moxDetail = await Mox.GetSeriesAsync(moxId, ct);
                                    if (OriginFilter.JapaneseOnly && OriginFilter.LooksKorean(
                                            moxDetail.Title, moxDetail.OriTitle, moxDetail.EngName,
                                            moxDetail.Tags, null))
                                    {
                                        log.Warn($"《{seriesName}》Mox 命中的《{moxDetail.Title}》疑似韩漫，跳过（只匹配日漫）");
                                    }
                                    else
                                    {
                                        var moxResult = await _scrape.ScrapeFromMoxAsync(series.Id, moxDetail,
                                            Opts(syncType, forceRematch), series, komgaMeta, ct);
                                        if (moxResult.Success)
                                            return result with { Success = true, MatchedTitle = moxResult.MatchedTitle ?? moxDetail.Title };
                                        log.Warn($"《{seriesName}》Mox 刮削失败: {moxResult.Error}");
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception e)
                                {
                                    log.Warn($"《{seriesName}》Mox 刮削失败: {e.Message}");
                                }
                            }
                        }
                    }

                    // ---- 回退 4：AI 把中文标题译成日文原名 → 再查 BookWalker ----
                    // （AI 识别失败时的译名兜底；AI 识别成功时 2b 已经用过更好的原名了）
                    if (BwIndex != null && Ai != null)
                    {
                        foreach (var t in terms.Take(2))
                        {
                            string? jp;
                            try { jp = await Ai.TranslateTitleToJapaneseAsync(t, ct); }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception) { jp = null; }
                            if (jp == null) continue;
                            var bwJp = BwIndex.FindExact(jp);
                            if (bwJp == null) continue;
                            log.Info($"《{seriesName}》\"{t}\" 译成日文「{jp}」后 BookWalker 命中:《{bwJp.Title}》，刮削中…");
                            var bwJpResult = await _scrape.ScrapeFromBookWalkerAsync(series.Id, bwJp,
                                Opts(syncType, forceRematch), series, komgaMeta, ct);
                            if (bwJpResult.Success)
                                return result with { Success = true, MatchedTitle = bwJp.Title };
                            log.Warn($"《{seriesName}》BookWalker（译名）刮削失败: {bwJpResult.Error}");
                            break;
                        }
                    }
                }

                // ---- 回退 4.5：MangaDex（英文圈图源，免密钥）——bgm/目录站没有但英文区收录的作品 ----
                if (MangaDex != null)
                {
                    foreach (var t in terms.Take(2))
                    {
                        List<MangaDexSeries> mdList;
                        try { mdList = await MangaDex.SearchAsync(t, 8, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e)
                        {
                            log.Warn($"《{seriesName}》MangaDex 搜索失败（\"{t}\"）: {e.Message}");
                            continue;
                        }
                        if (mdList.Count == 0) continue;
                        // 客户端兜底（服务端已按 originalLanguage=ja 限定）：防基址/参数被改后混入非日漫
                        if (OriginFilter.JapaneseOnly)
                        {
                            mdList = mdList.Where(m => string.Equals(m.OriginalLanguage, "ja", StringComparison.OrdinalIgnoreCase)).ToList();
                            if (mdList.Count == 0) continue;
                        }
                        var mdCands = mdList.Select(m => new SearchCandidate(
                            0, m.Title, m.OriTitle, "", string.Join(" / ", m.AltTitles.Take(4)),
                            m.CoverUrl, SourceMangaDex, MdId: m.Id)).ToList();
                        // 命中标准与其它源一致（规范名相等/包含+残留检查），避免张冠李戴
                        var mdPick = FindPreciseMatch(mdCands, TitleParser.ExtractAndNormalizeTitle(t));
                        if (mdPick == null) continue;
                        var mdHit = mdList.FirstOrDefault(m => m.Id == mdPick.MdId);
                        if (mdHit == null) continue;
                        try
                        {
                            log.Info($"《{seriesName}》MangaDex 命中:《{mdHit.Title}》，刮削中…");
                            var mdResult = await _scrape.ScrapeFromMangaDexAsync(series.Id, mdHit,
                                Opts(syncType, forceRematch), series, komgaMeta, ct);
                            if (mdResult.Success)
                                return result with { Success = true, MatchedTitle = mdHit.Title };
                            log.Warn($"《{seriesName}》MangaDex 刮削失败: {mdResult.Error}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { log.Warn($"《{seriesName}》MangaDex 刮削失败: {e.Message}"); }
                        break;   // 搜到了（哪怕没刮成）就不再换词
                    }
                }

                // ---- 回退 5：百度百科（词条卡片）——bgm/目录站/AI 都没有的冷门漫画，百科多半有 ----
                if (Baike != null)
                {
                    // 百科搜索词：AI 识别出的通行名优先（AI 认得出作者名/汉化组/出版社标签，
                    // 识别的作品名不会撞人物词条），文件夹分词殿后兜底。
                    var bkTerms = new List<string>();
                    if (identify is { Cn.Length: > 0 }) bkTerms.Add(identify.Cn);
                    if (identify is { Jp.Length: > 0 }) bkTerms.Add(identify.Jp);
                    bkTerms.AddRange(terms);
                    foreach (var t in bkTerms.Distinct(StringComparer.Ordinal).Take(4))
                    {
                        BaikeCard? card;
                        try { card = await Baike.SearchCardAsync(t, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e)
                        {
                            log.Warn($"《{seriesName}》百度百科搜索失败（\"{t}\"）: {e.Message}");
                            continue;
                        }
                        if (card == null) continue;
                        if (!card.LooksLikeMangaWork)
                        {
                            log.Warn($"《{seriesName}》百度百科《{card.Title}》非漫画作品词条（人物/影视/小说，概述图不是漫画封面），跳过");
                            continue;
                        }
                        var bkCands = new List<SearchCandidate>
                        {
                            new(0, card.Title, card.Field("原版名称"), card.Field("作者"), "", null, SourceBaike),
                        };
                        // 词条命中标准与其它源一致（规范名相等/包含），避免相近词条张冠李戴
                        if (FindPreciseMatch(bkCands, TitleParser.ExtractAndNormalizeTitle(t)) == null) continue;
                        try
                        {
                            log.Info($"《{seriesName}》百度百科命中:《{card.Title}》，刮削中…");
                            var bkResult = await _scrape.ScrapeFromBaikeAsync(series.Id, card,
                                Opts(syncType, forceRematch), series, komgaMeta, ct);
                            if (bkResult.Success)
                                return result with { Success = true, MatchedTitle = card.Title };
                            log.Warn($"《{seriesName}》百度百科刮削失败: {bkResult.Error}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { log.Warn($"《{seriesName}》百度百科刮削失败: {e.Message}"); }
                        break;   // 词条搜到了（哪怕没刮成）就不再换词
                    }
                }

                var err = searchResults.Count > 0 ? "无精确匹配（标题/原名/别名）" : "搜索无结果";
                log.Warn($"《{seriesName}》(词:\"{searchTerm}\") {err}");
                return result with { Error = err };
            }

            var display = FirstNonEmpty(precise.Title, precise.OriTitle, "未知匹配标题");
            log.Info($"《{seriesName}》精确匹配{(usedBackup ? "（备选词）" : "")}:《{display}》 subject={precise.Id}，刮削中…");
            var scrapeResult = await _scrape.ScrapeAsync(series.Id, precise.Id,
                Opts(syncType, forceRematch), series, komgaMeta, ct);
            if (!scrapeResult.Success)
                return result with { Error = scrapeResult.Error };
            return result with { Success = true, MatchedTitle = display };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException oce)
        {
            // 超时/连接中止也抛 TaskCanceledException（如 HttpClient.Timeout 到点）——
            // 它不是用户取消，绝不能让它杀掉整批（历史上整批 30 分钟的成果一次 100s 超时全灭）。
            log.Error($"《{result.Name}》请求超时/被中止（非手动取消），该系列按失败处理，批次继续: {oce.Message}");
            result = result with { Error = $"请求超时/中止（非取消）: {oce.Message}" };
        }
        catch (Exception e)
        {
            log.Error($"《{result.Name}》批量匹配出错: {e.Message}");
            result = result with { Error = e.Message };
        }

        // 失败 → 手动匹配收藏夹（Dry-run 只提示，不写；覆盖匹配不进收藏夹——旧数据还挂着，保持原样）
        if (!result.Success && !result.Skipped && stats != null && !forceRematch)
        {
            if (DryRun)
            {
                log.Info($"[Dry-run] 将把《{result.Name}》加入\"手动匹配\"收藏夹");
                _scrape.EmitChange(new ScrapeChange(
                    ScrapeChangeKind.Collection, series.Id, $"《{result.Name}》",
                    "将加入\"手动匹配\"收藏夹"));
            }
            else
            {
                var added = await _manualMatch.AddSeriesAsync(series.Id, result.Name, ct);
                if (added)
                {
                    lock (stats) stats.AddedToCollectionCount++;
                    _scrape.EmitChange(new ScrapeChange(
                        ScrapeChangeKind.Collection, series.Id, $"《{result.Name}》",
                        "已加入\"手动匹配\"收藏夹"));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 批量精确匹配一个系列集合：并行处理 <see cref="SeriesConcurrency"/> 个系列
    /// （原版对齐脚本是逐个 + 固定 300ms 间隔；现在礼貌节流下沉到 BangumiClient 统一控制，
    /// 每个系列内部还能重叠 Komga / 封面下载，速度大幅提升且对 Bangumi 更友好）。
    /// </summary>
    public async Task<BatchStats> BatchMatchAsync(
        IReadOnlyList<SeriesDto> seriesList, SyncType syncType, bool forceRefreshBtv,
        CancellationToken ct = default)
    {
        var stats = new BatchStats { Total = seriesList.Count };
        // 并行时批量层与内部刮削层必须共用同一份收藏夹管理器（两份成员快照会互相覆盖）
        _scrape.ManualMatch = _manualMatch;
        if (!DryRun) await _manualMatch.EnsureExistsAsync(ct: ct);

        var total = seriesList.Count;
        var started = 0;
        await Parallel.ForEachAsync(seriesList,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(SeriesConcurrency, 1, 8),
                CancellationToken = ct,
            },
            async (series, token) =>
            {
                token.ThrowIfCancellationRequested();
                var n = Interlocked.Increment(ref started);
                log.Progress(n, total, $"《{series.Metadata?.Title ?? series.Name}》");
                try
                {
                    var r = await PreciseMatchAsync(series, syncType, forceRefreshBtv, stats, token);
                    lock (stats)
                    {
                        if (r.Skipped) stats.SkippedCount++;
                        else if (r.Success) stats.SuccessCount++;
                        else stats.FailureCount++;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    lock (stats) stats.FailureCount++;
                    log.Error($"《{series.Name}》异常: {e.Message}");
                }
            }).ConfigureAwait(false);
        return stats;
    }

    /// <summary>
    /// 覆盖匹配：对系列集合重走一遍自动匹配流程——与批量精确匹配同一套链路
    /// （Bangumi 精确 → BookWalker → AI 原名重搜 → Mox → MangaDex → 百度百科），
    /// 但完全无视旧的匹配结果：不跳过已有链接、搜索词只用文件夹名（不看被旧匹配
    /// 污染的元数据标题），命中即以 ReplaceMatch 覆盖写入（旧链接剔除、被锁字段
    /// 解锁重写、全部卷重抓详情）。没匹配到的系列保持原样，不进"手动匹配"收藏夹。
    /// </summary>
    public async Task<BatchStats> ReMatchAsync(
        IReadOnlyList<SeriesDto> seriesList, SyncType syncType, CancellationToken ct = default)
    {
        var stats = new BatchStats { Total = seriesList.Count };
        // 共用同一份收藏夹管理器：覆盖成功后 ScrapeService 会顺手把系列从"手动匹配"收藏夹移除；
        // 收藏夹不存在也不会被创建（EnsureExists 无 initialSeriesId 时只查找不创建）
        _scrape.ManualMatch = _manualMatch;

        var total = seriesList.Count;
        var started = 0;
        await Parallel.ForEachAsync(seriesList,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(SeriesConcurrency, 1, 8),
                CancellationToken = ct,
            },
            async (series, token) =>
            {
                token.ThrowIfCancellationRequested();
                var n = Interlocked.Increment(ref started);
                log.Progress(n, total, $"《{series.Name}》");
                try
                {
                    var r = await PreciseMatchAsync(series, syncType,
                        forceRefreshBtv: false, stats: null, ct: token, forceRematch: true);
                    lock (stats)
                    {
                        if (r.Success) stats.SuccessCount++;
                        else stats.FailureCount++;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    lock (stats) stats.FailureCount++;
                    log.Error($"《{series.Name}》异常: {e.Message}");
                }
            }).ConfigureAwait(false);
        return stats;
    }

    /// <summary>Bangumi 站链接判定（bangumi.tv / bgm.tv / chii.in 及子域）。</summary>
    public static bool IsBtvLink(string url)
    {
        var host = url.Replace("https://", "").Replace("http://", "").Split('/')[0].ToLowerInvariant();
        return host == "bangumi.tv" || host.EndsWith(".bangumi.tv") ||
               host == "bgm.tv" || host.EndsWith(".bgm.tv") ||
               host == "chii.in" || host.EndsWith(".chii.in");
    }

    private async Task<AiClient.IdentifyResult?> IdentifySafeAsync(
        string seriesName, List<string> terms, CancellationToken ct)
    {
        try
        {
            return await Ai!.IdentifySeriesAsync(seriesName, terms, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            log.Warn($"《{seriesName}》AI 识别失败: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把候选按与搜索词的相似度排到前面再截断：搜索接口每次最多返回 20 条、多词合并后可能 40+，
    /// 全部塞给 AI 会让提示词爆炸且噪声淹没正确项（实测「候选 59 个，AI 选择中…」全数判不匹配）。
    /// 相似度用字符 bigram 的 Jaccard 系数，足够区分「同名/别名」与「毫不相干」。
    /// </summary>
    internal static List<SearchCandidate> RankCandidates(
        IReadOnlyList<SearchCandidate> list, string term, int take = 12)
    {
        var normTerm = TitleParser.ExtractAndNormalizeTitle(term);
        var termGrams = Bigrams(normTerm);
        var scored = new List<(SearchCandidate C, double Score)>();
        foreach (var c in list)
        {
            var best = 0.0;
            foreach (var name in CandidateNames(c))
            {
                var grams = Bigrams(TitleParser.ExtractAndNormalizeTitle(name));
                if (grams.Count == 0) continue;
                var inter = 0;
                foreach (var g in grams) if (termGrams.Contains(g)) inter++;
                var jac = (double)inter / (grams.Count + termGrams.Count - inter);
                if (jac > best) best = jac;
            }
            scored.Add((c, best));
        }
        // 稳定排序：同分保持原顺序（原顺序≈搜索相关度）
        var rank = scored
            .Select((x, i) => (x.C, x.Score, i))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.i)
            .Select(x => x.C)
            .Take(Math.Max(1, take));
        return rank.ToList();
    }

    private static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (s.Length == 1) set.Add(s);
        for (var i = 0; i + 1 < s.Length; i++) set.Add(s.Substring(i, 2));
        return set;
    }

    /// <summary>取第一个非空字符串（Bangumi 有时返回 name_cn="" 而 name 有值）。</summary>
    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    /// <summary>在搜索结果里找精确匹配（标题/原名/别名完全相同，或搜索词包含规范名）。</summary>
    public static SearchCandidate? FindPreciseMatch(List<SearchCandidate> list, string searchNorm)
    {
        foreach (var item in list)
        {
            foreach (var cand in CandidateNames(item))
            {
                if (TitleParser.ExtractAndNormalizeTitle(cand) == searchNorm) return item;
            }
        }

        // 搜索词是「苍蓝钢铁战舰、苍蓝钢铁的琶音」这种合并串时，命中的规范名被包含即可。
        // 残留检查（宁缺毋滥）：多出来的部分含拉丁字母/水印/汉化等垃圾词 → 拒绝。
        // 乱马1/2 完全版 ⊃ 乱马1/2（残 完全版 → 版本词放行）✓、
        // zero有水印 ⊃ zero（残 有水印 → 水印垃圾）✗ —— 曾把《食物战线无异状》刮成《ZERO》
        if (searchNorm.Length >= 4)
        {
            foreach (var item in list)
            {
                foreach (var cand in CandidateNames(item))
                {
                    var n = TitleParser.ExtractAndNormalizeTitle(cand);
                    if (n.Length < 4 || !searchNorm.Contains(n, StringComparison.Ordinal)) continue;
                    var idx = searchNorm.IndexOf(n, StringComparison.Ordinal);
                    var residue = searchNorm[..idx] + searchNorm[(idx + n.Length)..];
                    if (TitleParser.ResidueLooksLikeJunk(residue)) continue;
                    return item;
                }
            }
        }

        // 反向包含：候选名比搜索词长（带副标题/版本名），如词 "fate/stay night" ↔
        // 候选 "Fate／stay night [Unlimited Blade Works]"（嵌套括号会把副标题留在候选名里）
        if (searchNorm.Length >= 6)
        {
            foreach (var item in list)
            {
                foreach (var cand in CandidateNames(item))
                {
                    var n = TitleParser.ExtractAndNormalizeTitle(cand);
                    if (n.Length > searchNorm.Length && n.Contains(searchNorm, StringComparison.Ordinal))
                        return item;
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateNames(SearchCandidate item)
    {
        if (!string.IsNullOrEmpty(item.Title)) yield return item.Title;
        if (!string.IsNullOrEmpty(item.OriTitle)) yield return item.OriTitle;
        if (!string.IsNullOrEmpty(item.Aliases))
        {
            foreach (var alias in item.Aliases.Split(" / "))
                if (alias.Length > 0) yield return alias;
        }
    }

    /// <summary>候选面板的来源标签：Bgm（Bangumi）/ Bw（BookWalker）/ Mox（bookof.moe）/ MangaDex / Baike（百度百科）。</summary>
    public const string SourceMox = "Mox";
    public const string SourceBaike = "Baike";
    public const string SourceMangaDex = "MangaDex";
}

/// <summary>匹配类型（platform 过滤），对齐脚本 getBangumiMatchType。</summary>
public static class MatchType
{
    public const string Manga = "漫画";
    public const string Novel = "小说";
}