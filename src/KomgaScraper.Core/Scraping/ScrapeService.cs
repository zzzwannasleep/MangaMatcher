using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Sources;
using KomgaScraper.Core.Text;

namespace KomgaScraper.Core.Scraping;

/// <summary>刮削模式：meta=仅元数据；all=元数据+封面。</summary>
public enum SyncType
{
    Meta,
    All,
}

public sealed record ScrapeOptions
{
    public SyncType SyncType { get; init; } = SyncType.Meta;
    /// <summary>是否抓取单行本详情（每卷 summary/发售日/ISBN），对应脚本 getVolumeDataFetch。</summary>
    public bool FetchVolumeData { get; init; }
    /// <summary>Dry-run：只记录将执行的动作，不实际 PATCH/上传。</summary>
    public bool DryRun { get; init; }
    /// <summary>
    /// 纠正匹配：把旧的 Btv/Bw/Mox 来源链接从合并中剔除，让这次选中的新链接顶替旧链接。
    /// 用于"之前匹配错了，重新选一个"的场景（否则按 label 合并会保留旧链接）。
    /// </summary>
    public bool ReplaceMatch { get; init; }
    /// <summary>
    /// 只刮封面：跳过全部元数据 PATCH（系列/卷），仅上传系列封面与卷封面。
    /// 用于"封面单独覆盖刮削"（百科封面 400 失败后重跑，元数据已齐不再动）。
    /// </summary>
    public bool CoverOnly { get; init; }
}

public sealed record ScrapeResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? MatchedTitle { get; init; }
}

/// <summary>
/// 单系列刮削全流程编排，对齐脚本 fetchBtvSubjectByUrlAPI / updateKomgaBookAll /
/// updateKomgaSeriesCover / updateKomgaBookCover。
/// </summary>
public sealed class ScrapeService(KomgaClient komga, BangumiClient bangumi, IAppLogger log)
{
    private const int MaxReqBooks = 500;

    public ManualMatchCollection ManualMatch { get; set; } = new(komga, log);

    /// <summary>同时处理的并发度（系列级由 BatchMatcher 控制；这里只管单系列内的书籍循环）。</summary>
    internal int BookConcurrency { get; set; } = 4;

    /// <summary>AI 客户端（可选）：供"简介翻译成中文"使用。</summary>
    public AiClient? Ai { get; set; }

    /// <summary>
    /// 刮削状态库（可选）：记录每个系列「封面/简介」是否已刮（列表列 + 筛选用）。
    /// 封面在 UpdateSeriesCoverAsync 上传成功时记入；简介在 PATCH 成功后值非空时记入。
    /// </summary>
    public ScrapeStateStore? State { get; set; }

    /// <summary>是否把非中文简介翻译成中文（设置里勾选，需先配好 AI 接口）。</summary>
    public bool TranslateSummary { get; set; }

    /// <summary>BookWalker 本地索引（可选）：给 Bangumi 结果补 ISBN / 发售日 / 单卷封面。</summary>
    public BookWalkerIndex? BwIndex { get; set; }

    /// <summary>Mox（bookof.moe）中文漫画库客户端（可选）：Bangumi/BookWalker 都没命中时的第三源。</summary>
    public MoxClient? Mox { get; set; }

    /// <summary>百度百科客户端（可选最终兜底源）：所有源都没命中时的冷门漫画数据（词条卡片，国内直连不走代理）。</summary>
    public BaikeClient? Baike { get; set; }

    /// <summary>是否把 BookWalker 推断出的类型/题材标签、阅读方向写入 Komga。</summary>
    public bool BwWriteTags { get; set; } = true;

    /// <summary>
    /// 下载外部封面（Bangumi 的 lain.bgm.tv、BookWalker 的 c.bookwalker.jp）时使用的代理。
    /// 这些域名在部分网络下直连超时 / DNS 被污染，必须和 Bangumi API 走同一个代理；Komga 自身请求不受影响。
    /// </summary>
    public ProxyConfig Proxy { get; set; } = ProxyConfig.None;

    private HttpClient? _imageHttp;
    private readonly object _imageHttpGate = new();

    /// <summary>外部封面下载客户端（懒创建，带浏览器 UA，走代理；并行刮削时只建一次）。</summary>
    private HttpClient ImageHttp
    {
        get
        {
            if (_imageHttp != null) return _imageHttp;
            lock (_imageHttpGate)
            {
                _imageHttp ??= HttpFactory.Create(
                    Proxy, TimeSpan.FromSeconds(40),
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                    warn: log.Warn);
                return _imageHttp;
            }
        }
    }

    /// <summary>下载外部封面（不走 Komga 的 HttpClient，避免带上 Komga 认证头/基址）。
    /// 百度百科图片有防盗链（缺 Referer 直接 400）→ 走 BaikeClient 的直连通道。
    /// 全部封面下载经 CoverCache 磁盘暂存：材料先落本地，重跑命中缓存不再重复下载。</summary>
    private async Task<byte[]> DownloadImageAsync(string url, CancellationToken ct)
    {
        Func<string, CancellationToken, Task<byte[]>> fetch =
            url.Contains("bkimg", StringComparison.OrdinalIgnoreCase) && Baike != null
                ? (u, token) => Baike.DownloadImageAsync(u, token)
                : async (u, token) =>
                {
                    using var resp = await ImageHttp.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"下载封面失败 {(int)resp.StatusCode}: {u}");
                    return await resp.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                };
        return await CoverCache.GetOrDownloadAsync(url, fetch, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 简介中文化三步：已是中文 → OpenCC 繁→简统一（词汇级）；
    /// 非中文 → AI 翻译成简体；译文再过一遍繁→简兜底（模型偶发输出繁体）。
    /// 未启用 AI 时非中文原样返回，翻译失败也绝不让刮削失败。
    /// </summary>
    public async Task<string?> TranslateIfNeededAsync(string? text, string label, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (!AiClient.NeedsChineseTranslation(text))
        {
            // 已是中文：繁→简统一（OpenCC 词汇级）。日文文本（含假名）绝不过繁简转换——
            // 新字体汉字会被误转（進撃の巨人 → 进撃の巨人），这类文本原样返回。
            if (AiClient.IsHanOnly(text))
            {
                var simplified = CJK.T2S(text!);
                if (simplified != text)
                    log.Info($"《{label}》简介繁→简统一（OpenCC）");
                return simplified;
            }
            return text;
        }
        if (!TranslateSummary || Ai == null) return text;
        try
        {
            log.Info($"《{label}》简介为非中文，AI 翻译中…");
            var translated = await Ai.TranslateToChineseAsync(text!, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(translated) && translated != text)
            {
                // 译文仍是日文（模型没翻干净）→ 明示告警；是中文（可能夹繁体）→ 繁→简兜底
                if (!AiClient.IsHanOnly(translated))
                {
                    log.Warn($"《{label}》AI 译文仍含非中文内容（日文残留），建议人工检查");
                }
                else
                {
                    translated = CJK.T2S(translated);
                }
                log.Info($"《{label}》简介已翻译成中文（{text!.Length} → {translated.Length} 字）");
                return translated;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            log.Warn($"《{label}》简介翻译失败，保留原文: {e.Message}");
        }
        return text;
    }

    /// <summary>
    /// 标题中文化（对齐用户口径"标题也要变成中文"）：
    /// 纯中文（含繁体）→ OpenCC 繁→简；非中文（日文/英文标题，Bangumi 无中文名时常见）→
    /// AI 翻译成简体（复用"AI 简介翻译"开关）。译文仍非中文时保留原标题并告警，绝不写半截译文。
    /// </summary>
    private async Task<string?> TranslateTitleIfNeededAsync(string? title, string label, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        if (AiClient.IsHanOnly(title)) return CJK.T2S(title);   // 纯中文（含繁体）→ 简体统一
        if (Ai == null || !TranslateSummary) return title;      // 未启用翻译：原样保留
        try
        {
            var translated = await Ai.TranslateToChineseAsync(title!, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(translated) || translated == title) return title;
            if (!AiClient.IsHanOnly(translated))
            {
                log.Warn($"《{label}》AI 标题译文仍含非中文内容，保留原标题：{title}");
                return title;
            }
            var final = CJK.T2S(translated);
            log.Info($"《{label}》标题已翻译成中文：{title} → {final}");
            return final;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            log.Warn($"《{label}》标题翻译失败，保留原文: {e.Message}");
            return title;
        }
    }

    /// <summary>
    /// 匹配源无简介时清空旧简介（用户口径：匹配不到的就清空，脏数据不留）。
    /// 旧简介多半来自上一次的错误匹配（CleanMeta 不发空串，这里走 Raw PATCH 才能真正清空）。
    /// </summary>
    private async Task ClearStaleSeriesAsync(
        string komgaSeriesId, SeriesMetadataDto meta, SeriesMetadataDto? komgaMeta,
        string seriesNameForDisplay, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(meta.Summary) && !string.IsNullOrWhiteSpace(komgaMeta?.Summary))
        {
            try
            {
                await komga.PatchSeriesMetadataRawAsync(komgaSeriesId,
                    new Dictionary<string, object?> { ["summary"] = "" }, ct).ConfigureAwait(false);
                log.Info($"《{seriesNameForDisplay}》匹配源无简介，旧简介已清空");
            }
            catch (Exception e)
            {
                log.Warn($"《{seriesNameForDisplay}》旧简介清空失败: {e.Message}");
            }
        }
    }

    /// <summary>
    /// 匹配源无任何封面时清空旧封面（USER_UPLOADED 缩略图全部删除）：
    /// 旧封面是上次错误匹配留下的，Komga 会回退到首页自动生成图，不留脏图。
    /// </summary>
    private async Task ClearSeriesCoverAsync(string seriesId, string seriesName, CancellationToken ct)
    {
        try
        {
            var thumbs = await komga.GetSeriesThumbnailsAsync(seriesId, ct).ConfigureAwait(false);
            var uploaded = thumbs.Where(t => t.Type == "USER_UPLOADED").ToList();
            if (uploaded.Count == 0) return;
            foreach (var t in uploaded)
            {
                try { await komga.DeleteSeriesThumbnailAsync(seriesId, t.Id, ct).ConfigureAwait(false); }
                catch (Exception e)
                {
                    log.Error($"《{seriesName}》旧封面删除失败 (ID: {t.Id}): {e.Message}");
                }
            }
            log.Info($"《{seriesName}》匹配源无封面，旧封面已清空（Komga 将用首页自动生成图）");
        }
        catch (Exception e)
        {
            log.Warn($"《{seriesName}》旧封面清理失败: {e.Message}");
        }
    }

    /// <summary>
    /// 各源都没有简介时，让 AI 直接补写一段（AI 不认识的作品会返回空，绝不编内容）。
    /// 返回值直接塞进待 PATCH 的 meta.Summary；Dry-run 不调用只提示。
    /// </summary>
    private async Task<string?> FillAiSynopsisAsync(
        string seriesId, string seriesName, string? originalTitle, string? author,
        SeriesMetadataDto? komgaMeta, ScrapeOptions opt, CancellationToken ct)
    {
        if (Ai == null || !string.IsNullOrWhiteSpace(komgaMeta?.Summary)) return null;
        if (opt.DryRun)
        {
            log.Info($"[Dry-run] 《{seriesName}》各源均无简介，真实模式将用 AI 补写简介");
            return null;
        }
        log.Info($"《{seriesName}》各源均无简介，AI 补写中…");
        var synopsis = await Ai.GenerateSynopsisAsync(seriesName, originalTitle, author, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(synopsis))
        {
            log.Info($"《{seriesName}》AI 无法确认这部作品，未补写简介（宁缺毋滥）");
            return null;
        }
        log.Info($"《{seriesName}》AI 简介已生成（{synopsis.Length} 字）");
        return AiClient.IsHanOnly(synopsis) ? CJK.T2S(synopsis) : synopsis;   // 简繁统一（仅纯中文）
    }

    /// <summary>
    /// 逐条库写入改动流：Dry-run 时实时发给 UI 做预览面板（目的就是让"预览"真正看得见），
    /// 真实模式同样发出（描述改为"已…"），供界面做写入留痕。事件在线程池线程触发，UI 端自行调度。
    /// </summary>
    public event Action<ScrapeChange>? ChangeObserved;

    /// <summary>单条改动发射（dry-run=预览语气"将…"；真实=完成语气"已…"）。</summary>
    private void Observe(ScrapeChangeKind kind, string seriesId, string target, string desc, string detail = "") =>
        ChangeObserved?.Invoke(new ScrapeChange(kind, seriesId, target, desc, detail));

    /// <summary>同程序集的调用方（BatchMatcher 的收藏夹改动）复用这条发射通道。</summary>
    internal void EmitChange(ScrapeChange change) => ChangeObserved?.Invoke(change);

    /// <summary>PATCH 成功后把「系列简介已刮」记入状态库（最终值为空的不记）。</summary>
    private void MarkSummaryIfPresent(string seriesId, string? finalSummary)
    {
        if (!string.IsNullOrWhiteSpace(finalSummary)) State?.SetSummary(seriesId, true);
    }

    public async Task<ScrapeResult> ScrapeAsync(
        string komgaSeriesId, int subjectId, ScrapeOptions opt,
        SeriesDto? knownSeries = null, SeriesMetadataDto? knownMeta = null, CancellationToken ct = default)
    {
        try
        {
            // 已知数据直接复用（批量匹配入口在检查链接时已经拿过系列与元数据，重复 GET 纯属浪费）；
            // 列表 DTO 自带的元数据含 links/锁字段，等价于 GET /metadata（后者在部分 Komga 上 405）
            var knownMetaEff = knownMeta ?? (knownSeries?.Metadata?.Links != null ? knownSeries.Metadata : null);
            if (opt.ReplaceMatch && knownMetaEff != null)
                knownMetaEff = ClearedForReplace(knownMetaEff);   // 纠正匹配：旧匹配写入的字段全部让位给新源
            var seriesTask = knownSeries != null ? Task.FromResult(knownSeries) : komga.GetSeriesAsync(komgaSeriesId, ct);
            var metaTask = knownMetaEff != null ? Task.FromResult<SeriesMetadataDto?>(knownMetaEff) : komga.GetSeriesMetadataAsync(komgaSeriesId, ct);
            var subjectTask = bangumi.GetSubjectAsync(subjectId, ct);
            var series = await seriesTask.ConfigureAwait(false);
            var subject = await subjectTask.ConfigureAwait(false);
            var komgaMeta = await metaTask.ConfigureAwait(false);

            var seriesNameForDisplay = series.Metadata?.Title ?? series.Name;
            log.Info($"刮削 《{seriesNameForDisplay}》 ← Bangumi subject {subjectId} 《{subject.NameCn ?? subject.Name}》" +
                     (opt.DryRun ? " [Dry-run]" : ""));

            // ---- 0. 系列元数据（纯内存组装；CoverOnly 只跳过翻译/AI/写入，不碰任何 Komga 字段）----
            var seriesMeta = SubjectMapper.BuildSeriesMeta(subject, series);
            seriesMeta = SubjectMapper.MergeWithExisting(seriesMeta, komgaMeta, replaceSourceLinks: opt.ReplaceMatch);
            if (!opt.CoverOnly)
            {
                // 标题中文化：Bangumi 无中文名时标题常是日文，用户口径"标题也要变成中文"（排序标题同步）
                seriesMeta.Title = TitleParser.StripCopySuffix(
                    await TranslateTitleIfNeededAsync(seriesMeta.Title, seriesNameForDisplay, ct));
                if (!string.IsNullOrEmpty(seriesMeta.Title)) seriesMeta.TitleSort = seriesMeta.Title;
                seriesMeta.Summary = await TranslateIfNeededAsync(seriesMeta.Summary, seriesNameForDisplay, ct);

                // 合并后仍无简介 → AI 直接补写（Bangumi 条目缺简介/翻译未启用等场景）
                if (string.IsNullOrWhiteSpace(seriesMeta.Summary))
                {
                    var synopsis = await FillAiSynopsisAsync(komgaSeriesId, seriesNameForDisplay, subject.Name,
                        SubjectMapper.ParseInfobox(subject.Infobox, "作者"), komgaMeta, opt, ct).ConfigureAwait(false);
                    if (synopsis != null) seriesMeta.Summary = synopsis;
                }
            }

            // 程序打标：MACHINE 标签（可追溯）+ 文件夹名版本/格式标记（爱藏版/电子版/汉化组…→ 标题后缀）
            ApplyScrapeMarks(seriesMeta, series, komgaMeta);

            // ---- 0.5 材料收集（零写入）：卷表 → 每卷详情 → BookWalker 补齐 → 封面全部落暂存。
            //      材料齐了才开始写 Komga：中途取消/中断 = Komga 原样未动；
            //      重跑时命中暂存缓存，慢材料不再重复下载，秒级补完。
            var books = await komga.ListBooksInSeriesAsync(komgaSeriesId, ct);
            var updateAuthorsFlag = !opt.CoverOnly && seriesMeta.Authors is { Count: > 0 } && IfUpdateBook(books, seriesMeta.Authors);
            var needUpdateVolumeNums = opt.CoverOnly ? [] : GetVolumeNumsNeedUpdate(books);
            if (opt.ReplaceMatch && opt.FetchVolumeData)
            {
                // 纠正匹配：旧匹配写过的卷简介/ISBN/发售日可能是错的（比如乱马的卷错位），
                // 本地字段"看起来齐全"会让增量检测跳过 → 纠正时对全部有卷号的书强制重抓详情
                needUpdateVolumeNums = [.. books
                    .Select(ExtractBookVolumeNumber)
                    .Where(n => n > 0)];
                log.Info($"纠正匹配：将重抓 {needUpdateVolumeNums.Count} 卷的详情（简介/ISBN/发售日）");
            }

            var volumeMates = await FetchVolumeMatesAsync(subjectId, series, books, needUpdateVolumeNums,
                opt.FetchVolumeData, ct);

            // ---- 3.5 BookWalker 补充：只补 Bangumi 缺的 ISBN / 发售日 / 单卷封面（语言无关）----
            // 匹配词除了 Komga 标题外，再带上 Bangumi 的日文原名与别名——BW 目录是日文/英文标题，
            // 本地是中文名时直接用中文名查永远查不到（中文漫画库的常见盲区）
            var bwMates = 0;
            if (opt.FetchVolumeData && !opt.CoverOnly && BwIndex != null && !series.Oneshot)
            {
                var bwTitles = new List<string> { seriesNameForDisplay, series.Name ?? "", subject.Name ?? "", subject.NameCn ?? "" };
                bwTitles.AddRange(SubjectMapper.ExtractAliases(subject.Infobox).Split(" / ", StringSplitOptions.RemoveEmptyEntries));
                var bwHit = BwIndex.FindExact(bwTitles
                    .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).Take(12).ToArray());
                if (bwHit is { Volumes.Count: > 0 })
                {
                    bwMates = MergeBookWalkerVolumeMates(volumeMates, bwHit, seriesNameForDisplay, books.Count);
                    // Bangumi 判完结但缺总卷数（条目 volumes 没填）→ 用 BookWalker 目录的卷数补齐
                    //（只补完结系列：连载中的已出版卷数不能当总卷数写）
                    if (string.Equals(seriesMeta.Status, "ENDED", StringComparison.Ordinal) &&
                        (seriesMeta.TotalBookCount ?? 0) <= 0 && bwHit.VolumeCount > 0)
                    {
                        seriesMeta.TotalBookCount = bwHit.VolumeCount;
                        log.Info($"《{seriesNameForDisplay}》总卷数补齐（BookWalker）：{bwHit.VolumeCount}");
                    }
                }
            }

            var seriesCoverUrls = SubjectMapper.GetSeriesCoverUrls(subject);
            if (opt.SyncType == SyncType.All && !opt.DryRun)
            {
                // 封面材料预热：系列候选 + 全部卷候选一起进暂存缓存（这是整单最慢的一步，写在前面做完）
                var warm = new List<string>(seriesCoverUrls);
                warm.AddRange(volumeMates.SelectMany(v => v.CoverUrls));
                if (warm.Count > 0)
                    await CoverCache.WarmUpAsync(warm, DownloadImageAsync, log.Warn, serial: false, ct);
            }

            // ---- 1. 系列元数据（材料已齐，开始写入；CoverOnly 跳过）----
            if (opt.CoverOnly)
            {
                log.Info($"《{seriesNameForDisplay}》封面独立刮削：跳过元数据写入");
            }
            else if (opt.DryRun)
            {
                log.Info($"[Dry-run] PATCH /series/{komgaSeriesId}/metadata：" + SummarizeMeta(seriesMeta));
                Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesNameForDisplay}》",
                    "将更新系列元数据（Bangumi）", PreviewSeriesPatch(komgaMeta, seriesMeta));
            }
            else
            {
                await komga.PatchSeriesMetadataAsync(komgaSeriesId, seriesMeta, ct);
                log.Info($"《{seriesNameForDisplay}》系列信息已更新");
                MarkSummaryIfPresent(komgaSeriesId, seriesMeta.Summary);
                Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesNameForDisplay}》",
                    "已更新系列元数据（Bangumi）", PreviewSeriesPatch(komgaMeta, seriesMeta));
                // 匹配源无简介 → 清空旧简介（脏数据不留，用户口径"匹配不到的就清空"）
                await ClearStaleSeriesAsync(komgaSeriesId, seriesMeta, komgaMeta, seriesNameForDisplay, ct);
            }

            // ---- 2. 手动匹配收藏夹移除（CoverOnly 不动收藏夹）----
            if (!opt.DryRun && !opt.CoverOnly)
            {
                await ManualMatch.EnsureExistsAsync(ct: ct);
                await ManualMatch.RemoveSeriesAsync(komgaSeriesId, seriesNameForDisplay, ct);
            }

            // ---- 4+5. 封面与书籍更新并行跑（互不依赖；封面走外网 CDN，书籍走 Komga，谁都不堵谁）----
            if (opt.SyncType == SyncType.All)
            {
                if (seriesCoverUrls.Count == 0)
                {
                    // 匹配源没有任何封面 URL → 旧封面是上次错误匹配留下的脏图，直接清空
                    //（Komga 回退到首页自动生成图）。真实模式才清，Dry-run 只提示。
                    if (opt.DryRun)
                        log.Info($"[Dry-run] 《{seriesNameForDisplay}》匹配源无封面，真实模式将清空旧封面");
                    else
                        await ClearSeriesCoverAsync(komgaSeriesId, seriesNameForDisplay, ct).ConfigureAwait(false);
                }

                var coverTask = seriesCoverUrls.Count == 0
                    ? Task.CompletedTask
                    : series.Oneshot
                        ? UpdateOneshotBookCoverAsync(series, books, seriesNameForDisplay, seriesCoverUrls, ct)
                        : UpdateSeriesCoverAsync(komgaSeriesId, seriesNameForDisplay, seriesCoverUrls, opt, ct);
                var booksTask = UpdateBooksAsync(series, books, seriesNameForDisplay,
                    updateAuthorsFlag ? seriesMeta.Authors : [],
                    series.Oneshot ? [] : volumeMates.Select(v => v.CoverUrls).ToList(),
                    volumeMates, opt, ct);
                await Task.WhenAll(coverTask, booksTask).ConfigureAwait(false);
            }
            else if (updateAuthorsFlag || needUpdateVolumeNums.Count > 0 || bwMates > 0)
            {
                await UpdateBooksAsync(series, books, seriesNameForDisplay,
                    seriesMeta.Authors, [], volumeMates, opt, ct);
            }

            // 程序打标兜底：真跑过刮削就确保 MACHINE 标签（PATCH 带过 tags 时 meta.Tags 非空，会直接跳过）
            if (!opt.DryRun && !opt.CoverOnly && seriesMeta.Tags == null)
                await EnsureMachineTagAsync(komgaSeriesId, seriesNameForDisplay, komgaMeta, ct).ConfigureAwait(false);

            return new ScrapeResult { Success = true, MatchedTitle = subject.NameCn ?? subject.Name };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error($"刮削系列 {komgaSeriesId} 失败: {e.Message}");
            return new ScrapeResult { Success = false, Error = e.Message };
        }
    }
    /// <summary>
    /// 用 BookWalker 本地目录数据刮削系列（回退源）。
    /// 数据为英/日文，因此只填充缺失字段，并追加 "Bw" 链接；成人目录标记 AgeRating=18。
    /// 另用目录库里的每卷数据补齐「卷级」信息（发售日 / ISBN / 单卷简介 / 单卷封面），
    /// 并按 complete 标签判断状态、按 series.type 推断阅读方向。
    /// </summary>
    public async Task<ScrapeResult> ScrapeFromBookWalkerAsync(
        string komgaSeriesId, BookWalkerSeries bw, ScrapeOptions opt,
        SeriesDto? knownSeries = null, SeriesMetadataDto? knownMeta = null, CancellationToken ct = default)
    {
        try
        {
            var series = knownSeries ?? await komga.GetSeriesAsync(komgaSeriesId, ct).ConfigureAwait(false);
            var seriesName = series.Metadata?.Title ?? series.Name;
            log.Info($"刮削 《{seriesName}》 ← BookWalker 《{bw.Title}》" + (opt.DryRun ? " [Dry-run]" : "") +
                     (bw.Volumes.Count > 0 ? $"（目录含 {bw.Volumes.Count} 卷数据）" : ""));

            var komgaMeta = knownMeta ?? (knownSeries?.Metadata?.Links != null ? knownSeries.Metadata : null)
                            ?? await komga.GetSeriesMetadataAsync(komgaSeriesId, ct).ConfigureAwait(false);
            if (opt.ReplaceMatch && komgaMeta != null)
                komgaMeta = ClearedForReplace(komgaMeta);   // 纠正匹配：旧匹配写入的字段让位给 BookWalker

            var meta = new SeriesMetadataDto();
            var hasAny = false;
            var linkAdded = false;
            var changes = new List<string>();

            // ---- 标题 / 排序标题 ----
            if (!opt.CoverOnly && (string.IsNullOrWhiteSpace(komgaMeta?.Title) || TitleParser.HasCopySuffix(komgaMeta.Title)))
            {
                // 标题带 " (1)" 副本后缀 = 旧工具把文件夹名写库的污染 → 有新源数据就顶替，不留丑后缀
                var bwTitle = TitleParser.StripCopySuffix(
                    await TranslateTitleIfNeededAsync(bw.Title, seriesName, ct) ?? bw.Title);
                meta.Title = bwTitle;
                meta.TitleSort = bwTitle;
                hasAny = true; changes.Add("标题");
            }

            // ---- 简介（BookWalker 是日文/英文 + HTML → 去标签，按需翻译成中文）----
            if (!opt.CoverOnly && string.IsNullOrWhiteSpace(komgaMeta?.Summary) && !string.IsNullOrWhiteSpace(bw.Description))
            {
                meta.Summary = await TranslateIfNeededAsync(
                    SubjectMapper.StripHtml(bw.Description), seriesName, ct);
                hasAny = true; changes.Add("简介");
            }

            // 目录里也没有简介 → AI 直接补写（AI 认不出这部作品就保持空，不编内容）
            if (!opt.CoverOnly && string.IsNullOrWhiteSpace(meta.Summary) && string.IsNullOrWhiteSpace(komgaMeta?.Summary))
            {
                var synopsis = await FillAiSynopsisAsync(komgaSeriesId, seriesName, bw.Title,
                    bw.Authors.Count > 0 ? bw.Authors[0].Name : null, komgaMeta, opt, ct).ConfigureAwait(false);
                if (synopsis != null) { meta.Summary = synopsis; hasAny = true; changes.Add("AI简介"); }
            }

            // ---- 出版社 / 出版品牌 ----
            if (string.IsNullOrWhiteSpace(komgaMeta?.Publisher) && !string.IsNullOrWhiteSpace(bw.Publisher))
            {
                meta.Publisher = bw.Publisher; hasAny = true; changes.Add("出版社");
            }

            // ---- 成人分级 ----
            if (bw.IsAdult && komgaMeta?.AgeRating is null or 0)
            {
                meta.AgeRating = 18; hasAny = true; changes.Add("分级18+");
            }

            // ---- 总卷数（目录库里已排除「分话」，只数顶层卷）----
            if (bw.VolumeCount > 0 && (komgaMeta?.TotalBookCount ?? 0) == 0)
            {
                meta.TotalBookCount = bw.VolumeCount; hasAny = true; changes.Add($"总卷数 {bw.VolumeCount}");
            }

            // ---- 状态：complete 标签才是完结；有卷数但未标 complete → 连载中 ----
            if (string.IsNullOrWhiteSpace(komgaMeta?.Status) && komgaMeta?.StatusLock != true)
            {
                var status = bw.Complete ? "ENDED" : bw.VolumeCount > 0 ? "ONGOING" : null;
                if (status != null) { meta.Status = status; hasAny = true; changes.Add($"状态 {status}"); }
            }

            // ---- 作者 ----
            if ((komgaMeta?.Authors ?? []).Count == 0 && bw.Authors.Count > 0)
            {
                meta.Authors = bw.Authors.Select(a => new AuthorDto
                {
                    Name = a.Name,
                    Role = a.Role == 1 ? "writer" : "penciller",
                }).ToList();
                hasAny = true; changes.Add("作者");
            }

            // ---- 别名 → alternateTitles ----
            if (bw.AltTitles.Count > 0 && komgaMeta?.AlternateTitlesLock != true)
            {
                var merged = new List<AlternateTitleDto>(komgaMeta?.AlternateTitles ?? []);
                var seen = new HashSet<string>(merged.Select(t => t.Title), StringComparer.OrdinalIgnoreCase);
                var added = 0;
                foreach (var alt in bw.AltTitles)
                {
                    var v = alt.Trim();
                    if (v.Length == 0 || v.Equals(bw.Title, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(v)) continue;
                    merged.Add(new AlternateTitleDto { Label = "别名", Title = v });
                    if (++added >= 8) break;
                }
                if (added > 0) { meta.AlternateTitles = merged; hasAny = true; changes.Add($"别名 {added}"); }
            }

            // ---- 类型 / 题材标签 + 阅读方向 ----
            if (BwWriteTags)
            {
                if (bw.Genres.Count > 0 && komgaMeta?.GenresLock != true)
                {
                    var genres = new List<string>(komgaMeta?.Genres ?? []);
                    var added = 0;
                    foreach (var g in bw.Genres)
                        if (!genres.Contains(g, StringComparer.OrdinalIgnoreCase)) { genres.Add(g); added++; }
                    if (added > 0) { meta.Genres = genres; hasAny = true; changes.Add($"类型 {added}"); }
                }

                var newTags = BuildBwTags(bw, komgaMeta?.Tags ?? []);
                if (newTags.Count > 0 && komgaMeta?.TagsLock != true)
                {
                    var tags = new List<string>(komgaMeta?.Tags ?? []);
                    tags.AddRange(newTags);
                    meta.Tags = tags.Distinct(StringComparer.Ordinal).ToList();
                    hasAny = true; changes.Add($"标签 {newTags.Count}");
                }

                // 漫画 → 从右往左阅读（Komga 默认 LEFT_TO_RIGHT）
                if (bw.IsComic && komgaMeta?.ReadingDirectionLock != true &&
                    string.Equals(komgaMeta?.ReadingDirection ?? "LEFT_TO_RIGHT", "LEFT_TO_RIGHT", StringComparison.OrdinalIgnoreCase))
                {
                    meta.ReadingDirection = "RIGHT_TO_LEFT"; hasAny = true; changes.Add("阅读方向 RTL");
                }
            }

            // 程序打标：MACHINE 标签 + 文件夹名版本/格式标记（这次写标题时才补后缀）
            var markChanges = new List<string>();
            ApplyScrapeMarks(meta, series, komgaMeta, markChanges);
            if (markChanges.Count > 0) { hasAny = true; changes.AddRange(markChanges); }

            // ---- 系列首发日：目录里最早一卷的发售日 ----
            var firstDate = bw.Volumes
                .Select(v => v.ReleaseDate)
                .Where(d => d.Length == 10)
                .OrderBy(d => d, StringComparer.Ordinal)
                .FirstOrDefault();
            if (firstDate != null && string.IsNullOrWhiteSpace(komgaMeta?.ReleaseDate) &&
                komgaMeta?.ReleaseDateLock != true)
            {
                meta.ReleaseDate = firstDate; hasAny = true; changes.Add($"首发日 {firstDate}");
            }

            // ---- 链接：追加 Bw（没有变化就不动 links，避免把已有链接回写一遍）----
            if (!string.IsNullOrEmpty(bw.SeriesUrl))
            {
                var links = new List<LinkDto>(komgaMeta?.Links ?? []);
                if (!links.Any(l => l.Url.Contains("bookwalker", StringComparison.OrdinalIgnoreCase)))
                {
                    links.Add(new LinkDto { Label = "Bw", Url = bw.SeriesUrl });
                    meta.Links = links;
                    linkAdded = true;
                }
            }

            // 材料收集：封面（系列候选 + 每卷候选）在首次 PATCH 前全部进暂存缓存
            if (opt.SyncType == SyncType.All && !opt.DryRun)
            {
                var warm = new List<string>(bw.CoverUrls);
                warm.AddRange(bw.Volumes.SelectMany(v => BookWalkerIndex.BuildCoverUrls(v.CoverName)));
                if (warm.Count > 0)
                    await CoverCache.WarmUpAsync(warm, DownloadImageAsync, log.Warn, serial: false, ct);
            }

            if (opt.CoverOnly)
            {
                log.Info($"《{seriesName}》封面独立刮削：跳过元数据写入（BookWalker）");
            }
            else if (!hasAny && !linkAdded)
            {
                log.Info($"《{seriesName}》BookWalker 系列字段均已存在，无需更新");
            }
            else if (opt.DryRun)
            {
                log.Info($"[Dry-run] PATCH /series/{komgaSeriesId}/metadata（BookWalker）：" +
                         (changes.Count > 0 ? string.Join("、", changes) : "仅链接") + " " + SummarizeMeta(meta));
                Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesName}》",
                    "将更新系列元数据（BookWalker）", PreviewSeriesPatch(komgaMeta, meta));
            }
            else
            {
                if (hasAny)
                {
                    await komga.PatchSeriesMetadataAsync(komgaSeriesId, meta, ct);
                    log.Info($"《{seriesName}》系列信息已更新（BookWalker）：{string.Join("、", changes)}");
                    Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesName}》",
                        "已更新系列元数据（BookWalker）", PreviewSeriesPatch(komgaMeta, meta));
                }
                else if (linkAdded)
                {
                    await komga.PatchSeriesMetadataAsync(komgaSeriesId, meta, ct);
                    log.Info($"《{seriesName}》已追加 Bw 链接");
                    Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesName}》",
                        "已追加 Bw 链接（BookWalker）", PreviewSeriesPatch(komgaMeta, meta));
                }
            }
            if (!opt.DryRun && !opt.CoverOnly)
            {
                // 匹配源（BookWalker）无简介 → 清空旧简介（脏数据不留）
                await ClearStaleSeriesAsync(komgaSeriesId, meta, komgaMeta, seriesName, ct).ConfigureAwait(false);
            }
            if (!opt.DryRun)
                MarkSummaryIfPresent(komgaSeriesId,
                    string.IsNullOrWhiteSpace(meta.Summary) ? komgaMeta?.Summary : meta.Summary);

            if (!opt.DryRun && !opt.CoverOnly)
            {
                await ManualMatch.EnsureExistsAsync(ct: ct);
                await ManualMatch.RemoveSeriesAsync(komgaSeriesId, seriesName, ct);
            }

            // ---- 卷级数据（目录库的每卷发售日 / ISBN / 简介 / 封面）与系列封面并行跑 ----
            var wantVolumes = opt.FetchVolumeData && !opt.CoverOnly && bw.Volumes.Count > 0;
            var wantCover = opt.SyncType == SyncType.All && bw.CoverUrls.Count > 0;
            var volumesTask = wantVolumes
                ? Task.Run(async () =>
                {
                    var books = await komga.ListBooksInSeriesAsync(komgaSeriesId, ct).ConfigureAwait(false);
                    await UpdateBookWalkerVolumesAsync(series, books, bw, seriesName, opt, ct).ConfigureAwait(false);
                }, ct)
                : Task.CompletedTask;
            var bwCoverTask = wantCover
                ? UpdateSeriesCoverAsync(komgaSeriesId, seriesName, bw.CoverUrls.ToList(), opt, ct)
                : Task.CompletedTask;
            await Task.WhenAll(volumesTask, bwCoverTask).ConfigureAwait(false);

            // BookWalker 目录无封面 → 旧封面是脏图，清空（Komga 回退首页生成图）
            if (opt.SyncType == SyncType.All && !opt.DryRun && !opt.CoverOnly && bw.CoverUrls.Count == 0)
                await ClearSeriesCoverAsync(komgaSeriesId, seriesName, ct).ConfigureAwait(false);

            // 程序打标兜底：真跑过刮削就确保 MACHINE 标签（PATCH 带过 tags 时 meta.Tags 非空，会直接跳过）
            if (!opt.DryRun && !opt.CoverOnly && meta.Tags == null)
                await EnsureMachineTagAsync(komgaSeriesId, seriesName, komgaMeta, ct).ConfigureAwait(false);

            return new ScrapeResult { Success = true, MatchedTitle = bw.Title };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error($"BookWalker 刮削系列 {komgaSeriesId} 失败: {e.Message}");
            return new ScrapeResult { Success = false, Error = e.Message };
        }
    }

    /// <summary>
    /// 纠正匹配（ReplaceMatch）使用的数据视图：旧匹配写入的来源字段全部清空，让新源重填；
    /// 保留 tags/genres/阅读方向/发售日/总卷数（用户侧或与语言无关的数据，刮削时会合并去重）；
    /// links 剔除 Btv/Bw/Bof/Mox 旧来源链接（新链接稍后顶上）。不改缓存里的原对象。
    /// </summary>
    internal static SeriesMetadataDto ClearedForReplace(SeriesMetadataDto src)
    {
        var c = new SeriesMetadataDto
        {
            Tags = src.Tags, Genres = src.Genres, TagsLock = src.TagsLock, GenresLock = src.GenresLock,
            TotalBookCount = src.TotalBookCount, TotalBookCountLock = src.TotalBookCountLock,
            ReadingDirection = src.ReadingDirection, ReadingDirectionLock = src.ReadingDirectionLock,
            ReleaseDate = src.ReleaseDate, ReleaseDateLock = src.ReleaseDateLock,
            Language = src.Language, LanguageLock = src.LanguageLock,
        };
        return c;
    }

    /// <summary>版本/格式标记的黑白名单（settings.json 注入；null/空 = 只用内置词表）。
    /// 白名单：内置词表认不出的后缀也落标签；黑名单：命中的标记丢掉（不落标签、标题旧后缀一并清）。</summary>
    public List<string>? MarkerWhitelist { get; set; }
    public List<string>? MarkerBlacklist { get; set; }

    /// <summary>
    /// 程序打标（用户要求的可追溯标记）：
    /// ① MACHINE 标签——凡本程序写入过元数据的系列都带上（必打，不受 tags 锁限制：
    ///    "只要用了程序的任意一个功能对库里进行了操作 就要打标 这不是选择 是必须的"），
    ///    用户可以在 Komga 里直接筛出"程序整理过的系列"；
    /// ② 版本/格式标记——刮削会把标题整体替换成刮到的名字，文件夹名里区分版本的标注
    ///    （爱藏版/完全版/电子版/汉化组/BW原档…）会被抹平 → 多版本系列全清一色。
    ///    对齐 KomgaMylar 的 tag_by_foldername：从文件夹名提取标记 → 标签 + 标题后缀（一个后缀一个标签，
    ///    方括号格式：乱马1/2 [b漫版]）。版本/格式标记词属于内容数据，仍尊重 tags 锁（锁住时不追加）。
    /// ③ 黑白名单（用户设置）：白名单扩展识别；黑名单命中的标记丢掉 + 标题旧后缀清除 + 老标签剔除。
    /// </summary>
    internal void ApplyScrapeMarks(SeriesMetadataDto meta, SeriesDto series,
        SeriesMetadataDto? komgaMeta, List<string>? changes = null)
    {
        var folder = VersionMarker.FolderName(series);
        var markers = VersionMarker.Detect(folder, MarkerWhitelist, MarkerBlacklist);
        var blacklisted = VersionMarker.DetectBlacklisted(folder, MarkerWhitelist, MarkerBlacklist);

        // 标签：标记词（尊重 tags 锁）+ 出版社词（一个后缀一个标签，对齐 FolderTagger 行为）+ MACHINE（必打，无视锁）
        var tags = new List<string>(meta.Tags ?? komgaMeta?.Tags ?? []);
        var before = tags.Count;
        if (MarkerBlacklist is { Count: > 0 })
        {
            // 黑名单命中的旧标签一并丢掉（用户口径：黑名单里的标签都丢掉）；MACHINE 是程序留痕，永不丢
            var removed = tags.RemoveAll(t => t != VersionMarker.MachineTag &&
                                              VersionMarker.IsBlacklisted(t, MarkerBlacklist));
            if (removed > 0) changes?.Add($"丢弃黑名单标签 -{removed}");
        }
        if (komgaMeta?.TagsLock != true)
        {
            foreach (var t in VersionMarker.TagForms(markers))
                if (!tags.Contains(t, StringComparer.Ordinal)) tags.Add(t);
            var pub = VersionMarker.PublisherKeyword(folder);
            if (pub != null && !tags.Contains(pub, StringComparer.Ordinal)) tags.Add(pub);
        }
        if (!tags.Contains(VersionMarker.MachineTag, StringComparer.Ordinal))
            tags.Add(VersionMarker.MachineTag);
        if (tags.Count != before)
        {
            meta.Tags = tags;
            changes?.Add($"打标 +{tags.Count - before}");
        }

        // 标题后缀：只对这次确实要写入的标题补（TitleSort 保持无后缀的净标题，排序不受影响）。
        // 黑名单命中的标记先把标题里残留的旧后缀清掉（（join）→ 丢），再按方括号格式补回保留的标记。
        if (!string.IsNullOrEmpty(meta.Title))
        {
            var t = meta.Title!;
            if (blacklisted.Count > 0)
            {
                t = VersionMarker.StripSuffixes(t, blacklisted, out var stripped);
                if (stripped) changes?.Add("清黑名单后缀");
            }
            var marked = VersionMarker.ApplySuffixes(t, markers, out var changed);
            if (changed)
            {
                meta.Title = marked;
                changes?.Add("版本标记");
            }
        }
    }

    /// <summary>
    /// 程序打标兜底：本次没走字段 PATCH、但确实对库动过手（卷数据/封面等写入）时，
    /// 补一条只带 tags 的 PATCH，保证 MACHINE 标签必然落上（用户要求：必打）。
    /// 已带 MACHINE 时不发请求（幂等，不会每轮多写一次）。
    /// </summary>
    private async Task EnsureMachineTagAsync(string komgaSeriesId, string seriesName,
        SeriesMetadataDto? komgaMeta, CancellationToken ct)
    {
        var tags = new List<string>(komgaMeta?.Tags ?? []);
        if (tags.Contains(VersionMarker.MachineTag, StringComparer.Ordinal)) return;
        tags.Add(VersionMarker.MachineTag);
        try
        {
            await komga.PatchSeriesMetadataAsync(komgaSeriesId, new SeriesMetadataDto { Tags = tags }, ct);
            log.Info($"《{seriesName}》本次写入封面/卷数据，已补打 MACHINE 标签");
            Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesName}》",
                "已补打 MACHINE 标签（程序写入留痕）", $"标签 +{VersionMarker.MachineTag}");
        }
        catch (Exception e)
        {
            log.Error($"《{seriesName}》补打 MACHINE 标签失败: {e.Message}");
        }
    }

    /// <summary>
    /// 用 Mox（bookof.moe，中文漫画库）刮削系列（脚本第三源 fetchMoeBookByUrl）。
    /// 数据本身是中文（繁体站已转简体），天然适合中文命名的本地库：
    /// 标题/简介/作者/状态/分类/版本直接可用，每卷封面来自封面帧页（img.mxomo.com）。
    /// 只填 Komga 缺失的字段（与 BookWalker 回退一致，不覆盖已有中文数据）。
    /// </summary>
    public async Task<ScrapeResult> ScrapeFromMoxAsync(
        string komgaSeriesId, MoxSeries mox, ScrapeOptions opt,
        SeriesDto? knownSeries = null, SeriesMetadataDto? knownMeta = null, CancellationToken ct = default)
    {
        try
        {
            var series = knownSeries ?? await komga.GetSeriesAsync(komgaSeriesId, ct).ConfigureAwait(false);
            var seriesName = series.Metadata?.Title ?? series.Name;
            log.Info($"刮削 《{seriesName}》 ← Mox(bookof.moe) 《{mox.Title}》" + (opt.DryRun ? " [Dry-run]" : ""));

            var komgaMeta = knownMeta ?? (knownSeries?.Metadata?.Links != null ? knownSeries.Metadata : null)
                            ?? await komga.GetSeriesMetadataAsync(komgaSeriesId, ct).ConfigureAwait(false);
            if (opt.ReplaceMatch && komgaMeta != null)
                komgaMeta = ClearedForReplace(komgaMeta);

            var meta = new SeriesMetadataDto();
            var hasAny = false;
            var changes = new List<string>();

            // ---- 标题 / 排序标题（Mox 就是中文名）----
            if (!opt.CoverOnly && (string.IsNullOrWhiteSpace(komgaMeta?.Title) || TitleParser.HasCopySuffix(komgaMeta.Title)) && mox.Title.Length > 0)
            {
                var moxTitle = TitleParser.StripCopySuffix(mox.Title);
                meta.Title = moxTitle;
                meta.TitleSort = moxTitle;
                hasAny = true; changes.Add("标题");
            }

            // ---- 简介（中文，无需翻译）----
            if (string.IsNullOrWhiteSpace(komgaMeta?.Summary) && mox.Summary.Length > 0)
            {
                meta.Summary = mox.Summary; hasAny = true; changes.Add("简介");
            }

            // Mox 页面也没有简介 → AI 补写
            if (!opt.CoverOnly && string.IsNullOrWhiteSpace(meta.Summary) && string.IsNullOrWhiteSpace(komgaMeta?.Summary))
            {
                var synopsis = await FillAiSynopsisAsync(komgaSeriesId, seriesName, mox.OriTitle,
                    mox.Authors.Count > 0 ? mox.Authors[0] : null, komgaMeta, opt, ct).ConfigureAwait(false);
                if (synopsis != null) { meta.Summary = synopsis; hasAny = true; changes.Add("AI简介"); }
            }

            // ---- 出版社（站内叫「版本」）----
            if (string.IsNullOrWhiteSpace(komgaMeta?.Publisher) && mox.Publisher.Length > 0)
            {
                meta.Publisher = mox.Publisher; hasAny = true; changes.Add($"出版社 {mox.Publisher}");
            }

            // ---- 成人分级 ----
            if (mox.IsAdult && komgaMeta?.AgeRating is null or 0)
            {
                meta.AgeRating = 18; hasAny = true; changes.Add("分级18+");
            }

            // ---- 状态（连载/完结/停更/弃坑 → Komga 状态）----
            if (string.IsNullOrWhiteSpace(komgaMeta?.Status) && komgaMeta?.StatusLock != true && mox.Status.Length > 0)
            {
                meta.Status = mox.Status; hasAny = true; changes.Add($"状态 {mox.Status}");
            }

            // ---- 作者 ----
            if ((komgaMeta?.Authors ?? []).Count == 0 && mox.Authors.Count > 0)
            {
                meta.Authors = mox.Authors.Select(a => new AuthorDto { Name = a, Role = "writer" }).ToList();
                hasAny = true; changes.Add("作者");
            }

            // ---- 别名（原名=繁体站名，别名=英文名）----
            if (komgaMeta?.AlternateTitlesLock != true)
            {
                var merged = new List<AlternateTitleDto>(komgaMeta?.AlternateTitles ?? []);
                var seen = new HashSet<string>(merged.Select(t => t.Title), StringComparer.OrdinalIgnoreCase);
                var added = 0;
                void AddAlt(string label, string? value)
                {
                    var v = (value ?? "").Trim();
                    if (v.Length == 0 || v.Length > 80) return;
                    if (v.Equals(mox.Title, StringComparison.OrdinalIgnoreCase)) return;
                    if (v.Equals(mox.OriTitle, StringComparison.OrdinalIgnoreCase) && label == "别名") return;
                    if (!seen.Add(v)) return;
                    merged.Add(new AlternateTitleDto { Label = label, Title = TitleParser.Capitalize(v) });
                    added++;
                }
                AddAlt("原名", mox.OriTitle);
                AddAlt("别名", mox.EngName);
                if (added > 0) { meta.AlternateTitles = merged; hasAny = true; changes.Add($"别名 {added}"); }
            }

            // ---- 题材标签（BwWriteTags 开关共用：同属"目录站推断"类数据）----
            if (BwWriteTags && mox.Tags.Count > 0 && komgaMeta?.TagsLock != true)
            {
                var tags = new List<string>(komgaMeta?.Tags ?? []);
                var added = 0;
                foreach (var t in mox.Tags.Take(12))
                    if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase)) { tags.Add(t); added++; }
                if (added > 0)
                {
                    meta.Tags = tags.Distinct(StringComparer.Ordinal).ToList();
                    hasAny = true; changes.Add($"题材 {added}");
                }
            }

            // 程序打标：MACHINE 标签 + 文件夹名版本/格式标记（这次写标题时才补后缀）
            var markChanges = new List<string>();
            ApplyScrapeMarks(meta, series, komgaMeta, markChanges);
            if (markChanges.Count > 0) { hasAny = true; changes.AddRange(markChanges); }

            // ---- 链接：Bof（详情页）+ Mox（kox.moe 阅读页）----
            var links = new List<LinkDto>(komgaMeta?.Links ?? []);
            var linkAdded = false;
            if (!links.Any(l => l.Url.Contains("bookof.moe", StringComparison.OrdinalIgnoreCase)))
            {
                links.Add(new LinkDto { Label = "Bof", Url = mox.BoFUrl });
                linkAdded = true;
            }
            if (!links.Any(l => l.Url.Contains("kox.moe", StringComparison.OrdinalIgnoreCase)))
            {
                links.Add(new LinkDto { Label = "Mox", Url = mox.MoxUrl });
                linkAdded = true;
            }
            if (linkAdded) meta.Links = links;

            // ---- 封面：帧页 → 每卷 URL（第 1 张同时作系列封面）----
            // 材料收集提前到首次 PATCH 之前：帧页解析 + 逐张预热进暂存（bookof.moe 严格串行）
            List<string>? coverUrls = null;
            if (opt.SyncType == SyncType.All && Mox != null && mox.CoverFrameUrl.Length > 0)
            {
                try
                {
                    coverUrls = await Mox.GetVolumeCoverUrlsAsync(mox.CoverFrameUrl, ct).ConfigureAwait(false);
                    if (coverUrls.Count == 0) log.Warn($"《{seriesName}》Mox 封面帧页没有解析出封面");
                }
                catch (Exception e)
                {
                    log.Warn($"《{seriesName}》Mox 封面列表获取失败: {e.Message}");
                }
                if (coverUrls is { Count: > 0 })
                    await CoverCache.WarmUpAsync(coverUrls, DownloadImageAsync, log.Warn, serial: true, ct);
            }

            if (opt.CoverOnly)
            {
                log.Info($"《{seriesName}》封面独立刮削：跳过元数据写入（Mox）");
            }
            else if (!hasAny && !linkAdded)
            {
                log.Info($"《{seriesName}》Mox 系列字段均已存在，无需更新");
            }
            else if (opt.DryRun)
            {
                log.Info($"[Dry-run] PATCH /series/{komgaSeriesId}/metadata（Mox）：" +
                         (changes.Count > 0 ? string.Join("、", changes) : "仅链接") + " " + SummarizeMeta(meta));
                Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesName}》",
                    "将更新系列元数据（Mox）", PreviewSeriesPatch(komgaMeta, meta));
            }
            else
            {
                await komga.PatchSeriesMetadataAsync(komgaSeriesId, meta, ct);
                log.Info($"《{seriesName}》系列信息已更新（Mox）：{string.Join("、", changes)}");
                Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesName}》",
                    "已更新系列元数据（Mox）", PreviewSeriesPatch(komgaMeta, meta));
            }
            if (!opt.DryRun && !opt.CoverOnly)
            {
                // 匹配源（Mox）无简介 → 清空旧简介（脏数据不留）
                await ClearStaleSeriesAsync(komgaSeriesId, meta, komgaMeta, seriesName, ct).ConfigureAwait(false);
            }
            if (!opt.DryRun)
                MarkSummaryIfPresent(komgaSeriesId,
                    string.IsNullOrWhiteSpace(meta.Summary) ? komgaMeta?.Summary : meta.Summary);

            if (!opt.DryRun && !opt.CoverOnly)
            {
                await ManualMatch.EnsureExistsAsync(ct: ct);
                await ManualMatch.RemoveSeriesAsync(komgaSeriesId, seriesName, ct);
            }

            if (opt.SyncType == SyncType.All && coverUrls is { Count: > 0 })
            {
                if (opt.DryRun)
                {
                    log.Info($"[Dry-run] 将上传系列封面 + 最多 {coverUrls.Count} 张卷封面（Mox）");
                    Observe(ScrapeChangeKind.SeriesCover, komgaSeriesId, $"《{seriesName}》",
                        $"将上传系列封面 + 最多 {coverUrls.Count} 张卷封面（Mox）");
                }
                else
                {
                    // 严格串行：先系列封面，再逐卷封面（bookof.moe 源不能并发）
                    if (!series.Oneshot)
                        await UpdateSeriesCoverAsync(komgaSeriesId, seriesName, [coverUrls[0]], opt, ct).ConfigureAwait(false);
                    var books = await komga.ListBooksInSeriesAsync(komgaSeriesId, ct).ConfigureAwait(false);
                    if (series.Oneshot)
                        await UpdateOneshotBookCoverAsync(series, books, seriesName, coverUrls, ct).ConfigureAwait(false);
                    else
                        await UpdateMoxVolumeCoversAsync(books, seriesName, coverUrls, ct).ConfigureAwait(false);
                    if (coverUrls.Count > 0)
                        Observe(ScrapeChangeKind.SeriesCover, komgaSeriesId, $"《{seriesName}》",
                            $"已处理系列封面 + 最多 {coverUrls.Count} 张卷封面（Mox）");
                }
            }
            else if (opt.SyncType == SyncType.All && !opt.DryRun && !opt.CoverOnly)
            {
                // Mox 帧页没有解析出任何封面 → 旧封面是脏图，清空
                await ClearSeriesCoverAsync(komgaSeriesId, seriesName, ct).ConfigureAwait(false);
            }

            // 程序打标兜底：真跑过刮削就确保 MACHINE 标签（PATCH 带过 tags 时 meta.Tags 非空，会直接跳过）
            if (!opt.DryRun && !opt.CoverOnly && meta.Tags == null)
                await EnsureMachineTagAsync(komgaSeriesId, seriesName, komgaMeta, ct).ConfigureAwait(false);

            return new ScrapeResult { Success = true, MatchedTitle = mox.Title };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error($"Mox 刮削系列 {komgaSeriesId} 失败: {e.Message}");
            return new ScrapeResult { Success = false, Error = e.Message };
        }
    }

    /// <summary>
    /// 用百度百科词条卡片刮削系列（最终兜底源）。
    /// 数据是中文：词条标题=中文通行名、摘要=简介、基本信息卡=作者/作画/出版社/类型；
    /// 概述图可作系列封面（该源本来就只在其它源全部落空时使用，有图就值得填）。
    /// </summary>
    public async Task<ScrapeResult> ScrapeFromBaikeAsync(
        string komgaSeriesId, BaikeCard card, ScrapeOptions opt,
        SeriesDto? knownSeries = null, SeriesMetadataDto? knownMeta = null, CancellationToken ct = default)
    {
        try
        {
            var series = knownSeries ?? await komga.GetSeriesAsync(komgaSeriesId, ct).ConfigureAwait(false);
            var seriesName = series.Metadata?.Title ?? series.Name;
            log.Info($"刮削 《{seriesName}》 ← 百度百科 《{card.Title}》" + (opt.DryRun ? " [Dry-run]" : ""));

            var komgaMeta = knownMeta ?? (knownSeries?.Metadata?.Links != null ? knownSeries.Metadata : null)
                            ?? await komga.GetSeriesMetadataAsync(komgaSeriesId, ct).ConfigureAwait(false);
            if (opt.ReplaceMatch && komgaMeta != null)
                komgaMeta = ClearedForReplace(komgaMeta);

            var meta = new SeriesMetadataDto();
            var hasAny = false;
            var changes = new List<string>();

            // ---- 标题 / 排序标题（词条标题就是中文通行名）----
            if (!opt.CoverOnly && (string.IsNullOrWhiteSpace(komgaMeta?.Title) || TitleParser.HasCopySuffix(komgaMeta.Title)) && card.Title.Length > 0)
            {
                var bkTitle = TitleParser.StripCopySuffix(card.Title);
                meta.Title = bkTitle;
                meta.TitleSort = bkTitle;
                hasAny = true; changes.Add("标题");
            }

            // ---- 简介（百科摘要本身是中文，无需翻译）----
            if (string.IsNullOrWhiteSpace(komgaMeta?.Summary) && card.Abstract.Length > 0)
            {
                meta.Summary = card.Abstract; hasAny = true; changes.Add("简介");
            }

            // ---- 出版社（出版社 / 其他出版社 卡）----
            var press = card.Field("出版社").Length > 0 ? card.Field("出版社") : card.Field("其他出版社");
            if (string.IsNullOrWhiteSpace(komgaMeta?.Publisher) && press.Length > 0)
            {
                meta.Publisher = press; hasAny = true; changes.Add($"出版社 {press}");
            }

            // ---- 作者 / 作画（作画 → penciller；同名只落一个）----
            if ((komgaMeta?.Authors ?? []).Count == 0)
            {
                var authors = new List<AuthorDto>();
                var writer = card.Field("作者");
                if (writer.Length > 0) authors.Add(new AuthorDto { Name = writer, Role = "writer" });
                var artist = card.Field("作画");
                if (artist.Length > 0 && authors.All(a => a.Name != artist))
                    authors.Add(new AuthorDto { Name = artist, Role = "penciller" });
                if (authors.Count > 0) { meta.Authors = authors; hasAny = true; changes.Add("作者"); }
            }

            // ---- 别名：原版名称（日文原名）----
            if (komgaMeta?.AlternateTitlesLock != true)
            {
                var merged = new List<AlternateTitleDto>(komgaMeta?.AlternateTitles ?? []);
                var seen = new HashSet<string>(merged.Select(t => t.Title), StringComparer.OrdinalIgnoreCase);
                var ori = card.Field("原版名称");
                if (ori.Length > 0 && ori.Length <= 80 &&
                    !ori.Equals(card.Title, StringComparison.OrdinalIgnoreCase) && seen.Add(ori))
                {
                    merged.Add(new AlternateTitleDto { Label = "原名", Title = TitleParser.Capitalize(ori) });
                    meta.AlternateTitles = merged; hasAny = true; changes.Add("别名 1");
                }
            }

            // ---- 类型（类型卡：如「推理喜剧」→ genres）----
            if (BwWriteTags && komgaMeta?.GenresLock != true)
            {
                var typeRaw = card.Field("类型");
                if (typeRaw.Length > 0)
                {
                    var genres = new List<string>(komgaMeta?.Genres ?? []);
                    var added = 0;
                    foreach (var g in typeRaw.Split(['，', '、', ' ', ','],
                                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (g.Length > 0 && !genres.Contains(g, StringComparer.OrdinalIgnoreCase)) { genres.Add(g); added++; }
                    if (added > 0) { meta.Genres = genres; hasAny = true; changes.Add($"类型 {added}"); }
                }
            }

            // ---- 阅读方向：日本漫画 → 从右往左（词条描述/摘要提到「漫画」才认定，避免误伤非漫画词条）----
            if ((card.Desc.Contains("漫画") || card.Abstract.Contains("漫画")) &&
                komgaMeta?.ReadingDirectionLock != true &&
                string.Equals(komgaMeta?.ReadingDirection ?? "LEFT_TO_RIGHT", "LEFT_TO_RIGHT", StringComparison.OrdinalIgnoreCase))
            {
                meta.ReadingDirection = "RIGHT_TO_LEFT"; hasAny = true; changes.Add("阅读方向 RTL");
            }

            // 程序打标：MACHINE 标签 + 文件夹名版本/格式标记（这次写标题时才补后缀）
            var markChanges = new List<string>();
            ApplyScrapeMarks(meta, series, komgaMeta, markChanges);
            if (markChanges.Count > 0) { hasAny = true; changes.AddRange(markChanges); }

            // ---- 链接：Baike（词条页）----
            var links = new List<LinkDto>(komgaMeta?.Links ?? []);
            var linkAdded = false;
            if (card.EntryUrl.Length > 0 &&
                !links.Any(l => l.Url.Contains("baike.baidu.com", StringComparison.OrdinalIgnoreCase)))
            {
                links.Add(new LinkDto { Label = "Baike", Url = card.EntryUrl });
                linkAdded = true;
            }
            if (linkAdded) meta.Links = links;

            // 材料收集：概述图（该源唯一的图）进暂存，再开始写 Komga
            if (opt.SyncType == SyncType.All && card.ImageUrl.Length > 0 && !opt.DryRun)
            {
                await CoverCache.WarmUpAsync([BaikeClient.JpegCoverUrl(card.ImageUrl)],
                    DownloadImageAsync, log.Warn, serial: true, ct);
            }

            if (opt.CoverOnly)
            {
                log.Info($"《{seriesName}》封面独立刮削：跳过元数据写入（百度百科）");
            }
            else if (!hasAny && !linkAdded)
            {
                log.Info($"《{seriesName}》百度百科字段均已存在，无需更新");
            }
            else if (opt.DryRun)
            {
                log.Info($"[Dry-run] PATCH /series/{komgaSeriesId}/metadata（百度百科）：" +
                         (changes.Count > 0 ? string.Join("、", changes) : "仅链接") + " " + SummarizeMeta(meta));
                Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesName}》",
                    "将更新系列元数据（百度百科）", PreviewSeriesPatch(komgaMeta, meta));
            }
            else
            {
                await komga.PatchSeriesMetadataAsync(komgaSeriesId, meta, ct);
                log.Info($"《{seriesName}》系列信息已更新（百度百科）：{string.Join("、", changes)}");
                Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesName}》",
                    "已更新系列元数据（百度百科）", PreviewSeriesPatch(komgaMeta, meta));
            }
            if (!opt.DryRun && !opt.CoverOnly)
            {
                // 匹配源（百度百科）无简介 → 清空旧简介（脏数据不留）
                await ClearStaleSeriesAsync(komgaSeriesId, meta, komgaMeta, seriesName, ct).ConfigureAwait(false);
            }
            if (!opt.DryRun)
                MarkSummaryIfPresent(komgaSeriesId,
                    string.IsNullOrWhiteSpace(meta.Summary) ? komgaMeta?.Summary : meta.Summary);

            if (!opt.DryRun && !opt.CoverOnly)
            {
                await ManualMatch.EnsureExistsAsync(ct: ct);
                await ManualMatch.RemoveSeriesAsync(komgaSeriesId, seriesName, ct);
            }

            // ---- 封面：词条概述图（该源只在其它源全落空时用，概述图就是唯一的图）----
            if (opt.SyncType == SyncType.All && card.ImageUrl.Length > 0)
            {
                var jpgUrl = BaikeClient.JpegCoverUrl(card.ImageUrl);
                if (opt.DryRun)
                {
                    log.Info("[Dry-run] 将上传系列封面（百度百科概述图）");
                    Observe(ScrapeChangeKind.SeriesCover, komgaSeriesId, $"《{seriesName}》",
                        "将上传系列封面（百度百科概述图）");
                }
                else if (series.Oneshot)
                {
                    var books = await komga.ListBooksInSeriesAsync(komgaSeriesId, ct).ConfigureAwait(false);
                    await UpdateOneshotBookCoverAsync(series, books, seriesName, [jpgUrl], ct).ConfigureAwait(false);
                }
                else
                {
                    await UpdateSeriesCoverAsync(komgaSeriesId, seriesName, [jpgUrl], opt, ct).ConfigureAwait(false);
                }
            }
            else if (opt.SyncType == SyncType.All && !opt.DryRun && !opt.CoverOnly)
            {
                // 百科词条连概述图都没有 → 旧封面是脏图，清空
                await ClearSeriesCoverAsync(komgaSeriesId, seriesName, ct).ConfigureAwait(false);
            }

            // 程序打标兜底：真跑过刮削就确保 MACHINE 标签（PATCH 带过 tags 时 meta.Tags 非空，会直接跳过）
            if (!opt.DryRun && !opt.CoverOnly && meta.Tags == null)
                await EnsureMachineTagAsync(komgaSeriesId, seriesName, komgaMeta, ct).ConfigureAwait(false);

            return new ScrapeResult { Success = true, MatchedTitle = card.Title };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error($"百度百科刮削系列 {komgaSeriesId} 失败: {e.Message}");
            return new ScrapeResult { Success = false, Error = e.Message };
        }
    }

    /// <summary>
    /// 用 MangaDex 条目刮削系列（英文圈图源，免密钥公开 API）。
    /// 标题取 zh-Hans（没有则原文/英文），简介多为英/日文 → 走 AI 翻译链路；
    /// 别名来自 altTitles，类型来自英文 tag；封面 uploads CDN 直出。
    /// </summary>
    public async Task<ScrapeResult> ScrapeFromMangaDexAsync(
        string komgaSeriesId, MangaDexSeries md, ScrapeOptions opt,
        SeriesDto? knownSeries = null, SeriesMetadataDto? knownMeta = null, CancellationToken ct = default)
    {
        try
        {
            var series = knownSeries ?? await komga.GetSeriesAsync(komgaSeriesId, ct).ConfigureAwait(false);
            var seriesName = series.Metadata?.Title ?? series.Name;
            log.Info($"刮削 《{seriesName}》 ← MangaDex 《{md.Title}》" + (opt.DryRun ? " [Dry-run]" : ""));

            var komgaMeta = knownMeta ?? (knownSeries?.Metadata?.Links != null ? knownSeries.Metadata : null)
                            ?? await komga.GetSeriesMetadataAsync(komgaSeriesId, ct).ConfigureAwait(false);
            if (opt.ReplaceMatch && komgaMeta != null)
                komgaMeta = ClearedForReplace(komgaMeta);

            var meta = new SeriesMetadataDto();
            var hasAny = false;
            var changes = new List<string>();

            // ---- 标题 / 排序标题（MD 标题 zh-Hans 优先，否则原文/英文）----
            if (!opt.CoverOnly && (string.IsNullOrWhiteSpace(komgaMeta?.Title) || TitleParser.HasCopySuffix(komgaMeta.Title)) && md.Title.Length > 0)
            {
                var mdTitle = TitleParser.StripCopySuffix(
                    await TranslateTitleIfNeededAsync(md.Title, seriesName, ct) ?? md.Title);
                meta.Title = mdTitle;
                meta.TitleSort = mdTitle;
                hasAny = true; changes.Add("标题");
            }

            // ---- 简介（en/ja 居多 → AI 翻译链路；未启用翻译时原样写入）----
            if (!opt.CoverOnly && string.IsNullOrWhiteSpace(komgaMeta?.Summary) && md.Description.Length > 0)
            {
                var summary = await TranslateIfNeededAsync(md.Description, seriesName, ct).ConfigureAwait(false);
                meta.Summary = summary ?? md.Description;
                hasAny = true; changes.Add("简介");
            }

            // ---- 别名：altTitles（最多 5 条）----
            if (komgaMeta?.AlternateTitlesLock != true && md.AltTitles.Count > 0)
            {
                var merged = new List<AlternateTitleDto>(komgaMeta?.AlternateTitles ?? []);
                var seen = new HashSet<string>(merged.Select(t => t.Title), StringComparer.OrdinalIgnoreCase);
                var added = 0;
                foreach (var alt in md.AltTitles.Where(a => a.Length <= 80))
                {
                    if (!seen.Add(alt)) continue;
                    merged.Add(new AlternateTitleDto { Label = "别名", Title = TitleParser.Capitalize(alt) });
                    added++;
                    if (added >= 5) break;
                }
                if (added > 0) { meta.AlternateTitles = merged; hasAny = true; changes.Add($"别名 {added}"); }
            }

            // ---- 类型：英文 tag（Action/Fantasy…）写入 genres ----
            if (BwWriteTags && md.Tags.Count > 0 && komgaMeta?.GenresLock != true)
            {
                var genres = new List<string>(komgaMeta?.Genres ?? []);
                var added = 0;
                foreach (var g in md.Tags.Take(8))
                    if (!genres.Contains(g, StringComparer.OrdinalIgnoreCase)) { genres.Add(g); added++; }
                if (added > 0) { meta.Genres = genres; hasAny = true; changes.Add($"类型 {added}"); }
            }

            // ---- 阅读方向：日文原作 → 从右往左 ----
            if (string.Equals(md.OriginalLanguage, "ja", StringComparison.OrdinalIgnoreCase) &&
                komgaMeta?.ReadingDirectionLock != true &&
                string.Equals(komgaMeta?.ReadingDirection ?? "LEFT_TO_RIGHT", "LEFT_TO_RIGHT", StringComparison.OrdinalIgnoreCase))
            {
                meta.ReadingDirection = "RIGHT_TO_LEFT"; hasAny = true; changes.Add("阅读方向 RTL");
            }

            // 程序打标：MACHINE 标签 + 文件夹名版本/格式标记（写库红线：动了库必须可追溯）
            var markChanges = new List<string>();
            ApplyScrapeMarks(meta, series, komgaMeta, markChanges);
            if (markChanges.Count > 0) { hasAny = true; changes.AddRange(markChanges); }

            // ---- 链接：MangaDex（条目页）----
            var links = new List<LinkDto>(komgaMeta?.Links ?? []);
            var linkAdded = false;
            if (md.Id.Length > 0 &&
                !links.Any(l => l.Url.Contains("mangadex.org", StringComparison.OrdinalIgnoreCase)))
            {
                links.Add(new LinkDto { Label = "MangaDex", Url = $"https://mangadex.org/title/{md.Id}" });
                linkAdded = true;
            }
            if (linkAdded) meta.Links = links;

            // 材料收集：条目封面进暂存，再开始写 Komga
            if (opt.SyncType == SyncType.All && md.CoverUrl.Length > 0 && !opt.DryRun)
            {
                await CoverCache.WarmUpAsync([md.CoverUrl], DownloadImageAsync, log.Warn, serial: true, ct);
            }

            if (opt.CoverOnly)
            {
                log.Info($"《{seriesName}》封面独立刮削：跳过元数据写入（MangaDex）");
            }
            else if (!hasAny && !linkAdded)
            {
                log.Info($"《{seriesName}》MangaDex 字段均已存在，无需更新");
            }
            else if (opt.DryRun)
            {
                log.Info($"[Dry-run] PATCH /series/{komgaSeriesId}/metadata（MangaDex）：" +
                         (changes.Count > 0 ? string.Join("、", changes) : "仅链接") + " " + SummarizeMeta(meta));
                Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesName}》",
                    "将更新系列元数据（MangaDex）", PreviewSeriesPatch(komgaMeta, meta));
            }
            else
            {
                await komga.PatchSeriesMetadataAsync(komgaSeriesId, meta, ct);
                log.Info($"《{seriesName}》系列信息已更新（MangaDex）：{string.Join("、", changes)}");
                Observe(ScrapeChangeKind.SeriesMeta, komgaSeriesId, $"《{seriesName}》",
                    "已更新系列元数据（MangaDex）", PreviewSeriesPatch(komgaMeta, meta));
            }

            if (!opt.DryRun && !opt.CoverOnly)
            {
                // 匹配源（MangaDex）无简介 → 清空旧简介（脏数据不留）
                await ClearStaleSeriesAsync(komgaSeriesId, meta, komgaMeta, seriesName, ct).ConfigureAwait(false);
            }
            if (!opt.DryRun)
                MarkSummaryIfPresent(komgaSeriesId,
                    string.IsNullOrWhiteSpace(meta.Summary) ? komgaMeta?.Summary : meta.Summary);

            if (!opt.DryRun && !opt.CoverOnly)
            {
                await ManualMatch.EnsureExistsAsync(ct: ct);
                await ManualMatch.RemoveSeriesAsync(komgaSeriesId, seriesName, ct);
            }

            // ---- 封面：uploads CDN 直出（与 API 同一客户端，无需特殊头）----
            if (opt.SyncType == SyncType.All && md.CoverUrl.Length > 0)
            {
                if (opt.DryRun)
                {
                    log.Info("[Dry-run] 将上传系列封面（MangaDex）");
                    Observe(ScrapeChangeKind.SeriesCover, komgaSeriesId, $"《{seriesName}》",
                        "将上传系列封面（MangaDex）");
                }
                else if (series.Oneshot)
                {
                    var books = await komga.ListBooksInSeriesAsync(komgaSeriesId, ct).ConfigureAwait(false);
                    await UpdateOneshotBookCoverAsync(series, books, seriesName, [md.CoverUrl], ct).ConfigureAwait(false);
                }
                else
                {
                    await UpdateSeriesCoverAsync(komgaSeriesId, seriesName, [md.CoverUrl], opt, ct).ConfigureAwait(false);
                }
            }
            else if (opt.SyncType == SyncType.All && !opt.DryRun && !opt.CoverOnly)
            {
                // MangaDex 条目无封面 → 旧封面是脏图，清空
                await ClearSeriesCoverAsync(komgaSeriesId, seriesName, ct).ConfigureAwait(false);
            }

            // 程序打标兜底：真跑过刮削就确保 MACHINE 标签
            if (!opt.DryRun && !opt.CoverOnly && meta.Tags == null)
                await EnsureMachineTagAsync(komgaSeriesId, seriesName, komgaMeta, ct).ConfigureAwait(false);

            return new ScrapeResult { Success = true, MatchedTitle = md.Title };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error($"MangaDex 刮削系列 {komgaSeriesId} 失败: {e.Message}");
            return new ScrapeResult { Success = false, Error = e.Message };
        }
    }

    /// <summary>
    /// Mox 每卷封面按顺序对齐本地书（帧页封面列表顺序 = 卷序）。
    /// 数量差距过大说明两边不是同一套「卷」→ 只保留系列封面，放弃逐卷对齐。
    /// </summary>
    private async Task UpdateMoxVolumeCoversAsync(List<BookDto> books, string seriesName,
        List<string> coverUrls, CancellationToken ct)
    {
        if (books.Count == 0) return;
        if (!VolumesCompatible(coverUrls.Count, books.Count))
        {
            log.Info($"《{seriesName}》Mox 封面数（{coverUrls.Count}）与本地册数（{books.Count}）差距过大，跳过卷封面");
            return;
        }

        // 严格串行：bookof.moe 明确不能并发（图片虽在 img.mxomo.com CDN，也统一走串行更稳妥）
        for (var i = 0; i < books.Count; i++)
        {
            if (i >= coverUrls.Count) break;
            ct.ThrowIfCancellationRequested();
            var book = books[i];
            var volNum = ExtractBookVolumeNumber(book);
            var volDisplay = (volNum > 0 ? volNum : i + 1).ToString().PadLeft(2, '0');
            try
            {
                await UpdateBookCoverAsync(book, seriesName, volDisplay, [coverUrls[i]],
                    new ScrapeOptions { SyncType = SyncType.All }, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                log.Error($"处理《{seriesName}》卷 {volDisplay} (book {book.Id}) 出错: {e.Message}");
            }
        }
    }

    /// <summary>BookWalker 题材标签 + 出版品牌 + 漫画/小说 类型标记（跳过已存在的）。</summary>
    private static List<string> BuildBwTags(BookWalkerSeries bw, IReadOnlyList<string> existing)
    {
        var tags = new List<string>();
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var v = raw.Trim();
            if (v.Length is 0 or > 40) return;
            if (existing.Any(e => string.Equals(e, v, StringComparison.OrdinalIgnoreCase))) return;
            if (tags.Any(e => string.Equals(e, v, StringComparison.OrdinalIgnoreCase))) return;
            tags.Add(v);
        }

        foreach (var t in bw.Themes) Add(t);
        if (!string.IsNullOrWhiteSpace(bw.Imprint) &&
            !string.Equals(bw.Imprint, bw.Publisher, StringComparison.OrdinalIgnoreCase))
            Add(bw.Imprint);
        if (bw.IsComic) Add("漫画");
        else if (bw.IsNovel) Add("小说");
        return tags;
    }

    /// <summary>
    /// BookWalker 每卷数据 → Komga 书元数据（只补发售日 / ISBN / 简介 / 封面）。
    /// 卷号优先按 Komga 书名解析，其次按顺序对齐（数量一致时）。
    /// </summary>
    private async Task UpdateBookWalkerVolumesAsync(
        SeriesDto series, List<BookDto> books, BookWalkerSeries bw, string seriesName,
        ScrapeOptions opt, CancellationToken ct)
    {
        if (books.Count == 0) return;

        // 卷数差太多说明两边不是同一套「卷」（例如 BookWalker 按话连载 200 条 vs 本地 10 册），
        // 硬按卷号对齐会把别的话的 ISBN 写到卷上 → 宁可跳过。
        if (!VolumesCompatible(bw.Volumes.Count, books.Count))
        {
            log.Info($"《{seriesName}》BookWalker 卷数（{bw.Volumes.Count}）与本地册数（{books.Count}）差距过大，跳过每卷补齐");
            return;
        }

        var byOrder = new Dictionary<int, BookWalkerVolume>();
        foreach (var v in bw.Volumes)
            if (v.Order > 0) byOrder.TryAdd(v.Order, v);

        var updated = 0;
        await Parallel.ForEachAsync(books.Select((b, i) => (b, i)),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, BookConcurrency), CancellationToken = ct },
            async (pair, token) =>
            {
                var (book, i) = pair;
                var volNum = ExtractBookVolumeNumber(book);
                BookWalkerVolume? v = null;
                if (volNum > 0) byOrder.TryGetValue(volNum, out v);
                if (v == null && books.Count == bw.Volumes.Count && i < bw.Volumes.Count) v = bw.Volumes[i];
                if (v == null || v.IsEmpty) return;

                var volDisplay = (volNum > 0 ? volNum : v.Order).ToString().PadLeft(2, '0');
                try
                {
                    var bookMeta = new BookMetadataDto();
                    if (!string.IsNullOrWhiteSpace(v.Summary))
                        bookMeta.Summary = await TranslateIfNeededAsync(
                            SubjectMapper.StripHtml(v.Summary), $"{seriesName} 卷 {volDisplay}", token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(v.ReleaseDate)) bookMeta.ReleaseDate = v.ReleaseDate;
                    if (!string.IsNullOrWhiteSpace(v.Isbn)) bookMeta.Isbn = v.Isbn;

                    if (bookMeta.Summary != null || bookMeta.ReleaseDate != null || bookMeta.Isbn != null)
                    {
                        if (opt.DryRun)
                        {
                            log.Info($"[Dry-run] PATCH /books/{book.Id}/metadata（BookWalker）：《{seriesName}》卷 {volDisplay} " +
                                     $"releaseDate={bookMeta.ReleaseDate ?? "-"} isbn={bookMeta.Isbn ?? "-"} " +
                                     $"summary={(bookMeta.Summary != null ? "有" : "无")}");
                            Observe(ScrapeChangeKind.BookMeta, book.SeriesId, $"《{seriesName}》卷 {volDisplay}",
                                "将更新卷信息（BookWalker）",
                                PreviewBookPatch(book.Metadata, bookMeta));
                        }
                        else
                        {
                            await komga.PatchBookMetadataAsync(book.Id, bookMeta, token).ConfigureAwait(false);
                            Interlocked.Increment(ref updated);
                            Observe(ScrapeChangeKind.BookMeta, book.SeriesId, $"《{seriesName}》卷 {volDisplay}",
                                "已更新卷信息（BookWalker）", PreviewBookPatch(book.Metadata, bookMeta));
                        }
                    }

                    if (opt.SyncType == SyncType.All && v.CoverName.Length > 0)
                        await UpdateBookCoverAsync(book, seriesName, volDisplay,
                            BookWalkerIndex.BuildCoverUrls(v.CoverName).ToList(), opt, token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.Error($"处理《{seriesName}》卷 {volDisplay} (book {book.Id}) 出错: {e.Message}");
                }
            }).ConfigureAwait(false);

        if (updated > 0)
            log.Info($"《{seriesName}》BookWalker 补齐 {updated} 卷信息（发售日/ISBN/简介）");
    }

    /// <summary>
    /// 两边的「卷」是否可能是同一套：数量接近才算。
    /// 差太多（如目录库按话连载 200 条、本地只有 10 册）就放弃每卷对齐，
    /// 否则会把别的话的 ISBN/发售日写到卷上。
    /// </summary>
    private static bool VolumesCompatible(int bwVolumeCount, int bookCount)
    {
        if (bwVolumeCount <= 0 || bookCount <= 0) return false;
        return Math.Abs(bwVolumeCount - bookCount) <= Math.Max(2, bookCount / 4);
    }

    /// <summary>
    /// 把 BookWalker 的每卷数据合并进 Bangumi 的 volumeMates：
    /// 只补空缺的 ISBN / 发售日 / 单卷封面（这些与语言无关），不覆盖 Bangumi 的中文简介。
    /// </summary>
    private int MergeBookWalkerVolumeMates(List<VolumeMate> volumeMates, BookWalkerSeries bw, string seriesName, int bookCount)
    {
        if (!VolumesCompatible(bw.Volumes.Count, bookCount))
        {
            log.Info($"《{seriesName}》BookWalker 卷数（{bw.Volumes.Count}）与本地册数（{bookCount}）差距过大，跳过每卷补空");
            return 0;
        }

        var added = 0;
        var filled = 0;
        foreach (var v in bw.Volumes)
        {
            if (v.Order <= 0) continue;
            var num = v.Order.ToString();
            var mate = volumeMates.FirstOrDefault(m => m.Num == num);
            if (mate == null)
            {
                if (volumeMates.Count >= MaxReqBooks) break;
                volumeMates.Add(new VolumeMate
                {
                    Num = num,
                    ReleaseDate = v.ReleaseDate,
                    Isbn = v.Isbn,
                    CoverUrls = v.CoverName.Length > 0 ? BookWalkerIndex.BuildCoverUrls(v.CoverName).ToList() : [],
                });
                added++;
            }
            else
            {
                if (string.IsNullOrEmpty(mate.ReleaseDate) && v.ReleaseDate.Length > 0) { mate.ReleaseDate = v.ReleaseDate; filled++; }
                if (string.IsNullOrEmpty(mate.Isbn) && v.Isbn.Length > 0) { mate.Isbn = v.Isbn; filled++; }
                if (mate.CoverUrls.Count == 0 && v.CoverName.Length > 0)
                    mate.CoverUrls = BookWalkerIndex.BuildCoverUrls(v.CoverName).ToList();
            }
        }

        if (added + filled > 0)
            log.Info($"《{seriesName}》BookWalker 补充 {added} 卷、补空 {filled} 项（ISBN/发售日/封面）");
        return added + filled;
    }

    /// <summary>从书名 / 现有标题 / Komga number 字段解析卷号（解析不到返回 0）。</summary>
    private static int ExtractBookVolumeNumber(BookDto book) =>
        TitleParser.ExtractBookVolumeNumber(book.Name, book.Metadata?.Title, book.Metadata?.Number);

    private static string SummarizeMeta(SeriesMetadataDto m)
    {
        var parts = new List<string>
        {
            $"title={m.Title}",
            $"summary={(m.Summary?.Length > 40 ? m.Summary[..40] + "…" : m.Summary)}",
            $"publisher={m.Publisher}",
            $"status={m.Status}",
            $"ageRating={m.AgeRating?.ToString() ?? "null"}",
            $"totalBookCount={m.TotalBookCount?.ToString() ?? "null"}",
            $"authors=[{string.Join(";", (m.Authors ?? []).Select(a => $"{a.Name}({a.Role})"))}]",
            $"tags=[{string.Join(",", (m.Tags ?? []).Take(15))}{(m.Tags is { Count: > 15 } ? ",…" : "")}]",
            $"genres=[{string.Join(",", (m.Genres ?? []).Take(10))}]",
            $"readingDirection={m.ReadingDirection ?? "-"}",
            $"releaseDate={m.ReleaseDate ?? "-"}",
            $"links=[{string.Join(";", (m.Links ?? []).Select(l => l.Label))}]",
            $"altTitles={m.AlternateTitles?.Count ?? 0}",
        };
        return string.Join(" ", parts);
    }

    /// <summary>卷 PATCH 预览：逐字段「旧值 → 新值」（只列本次真的要写的字段）。</summary>
    private static string PreviewBookPatch(BookMetadataDto? old, BookMetadataDto meta)
    {
        var parts = new List<string>();
        void F(string name, string? oldV, string? newV)
        {
            if (string.IsNullOrWhiteSpace(newV)) return;
            if (string.Equals(oldV ?? "", newV, StringComparison.Ordinal)) return;
            parts.Add($"{name}: {Pv(oldV)} → {Pv(newV)}");
        }
        F("标题", old?.Title, meta.Title);
        F("简介", old?.Summary, meta.Summary);
        F("发售日", old?.ReleaseDate, meta.ReleaseDate);
        F("ISBN", old?.Isbn, meta.Isbn);
        if (meta.Authors is { Count: > 0 } && (old?.Authors ?? []).Count == 0)
            parts.Add($"作者: {Pv(string.Join("/", meta.Authors.Select(a => a.Name)))}");
        return parts.Count > 0 ? string.Join("；", parts) : "(无字段变化)";
    }

    /// <summary>预览用截断（60 字符，过长打省略号；换行压成空格）。</summary>
    private static string Pv(string? v)
    {
        var s = (v ?? "").Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 60 ? s : s[..60] + "…";
    }

    /// <summary>
    /// 系列 PATCH 预览：逐字段「旧值 → 新值」，只列本次真的要写的字段
    /// （与 CleanMeta 一致：null / 空串 / 0 都不写，所以也不出现在预览里）。
    /// </summary>
    private static string PreviewSeriesPatch(SeriesMetadataDto? old, SeriesMetadataDto meta)
    {
        var parts = new List<string>();
        void F(string name, string? oldV, string? newV)
        {
            if (string.IsNullOrWhiteSpace(newV)) return;                       // 空串不会写入
            if (string.Equals(oldV ?? "", newV, StringComparison.Ordinal)) return;
            parts.Add($"{name}: {Pv(oldV)} → {Pv(newV)}");
        }
        F("标题", old?.Title, meta.Title);
        F("排序标题", old?.TitleSort, meta.TitleSort);
        F("状态", old?.Status, meta.Status);
        F("简介", old?.Summary, meta.Summary);
        F("出版社", old?.Publisher, meta.Publisher);
        F("语言", old?.Language, meta.Language);
        F("阅读方向", old?.ReadingDirection, meta.ReadingDirection);
        F("发售日", old?.ReleaseDate, meta.ReleaseDate);
        if (meta.AgeRating.HasValue && meta.AgeRating != old?.AgeRating)
            parts.Add($"分级: {old?.AgeRating?.ToString() ?? "无"} → {meta.AgeRating}");
        if (meta.TotalBookCount is > 0 && meta.TotalBookCount != old?.TotalBookCount)
            parts.Add($"总卷数: {old?.TotalBookCount?.ToString() ?? "无"} → {meta.TotalBookCount}");
        if (meta.Genres != null)
        {
            var added = meta.Genres.Where(g => !(old?.Genres ?? []).Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
            if (added.Count > 0) parts.Add($"类型 +{added.Count}: {Pv(string.Join("/", added))}");
        }
        if (meta.Tags != null)
        {
            var added = meta.Tags.Where(t => !(old?.Tags ?? []).Contains(t, StringComparer.Ordinal)).ToList();
            if (added.Count > 0) parts.Add($"标签 +{added.Count}: {Pv(string.Join("/", added))}");
        }
        if (meta.AlternateTitles != null)
        {
            var added = meta.AlternateTitles.Where(t => !(old?.AlternateTitles ?? []).Any(a => a.Title == t.Title)).ToList();
            if (added.Count > 0) parts.Add($"别名 +{added.Count}: {Pv(string.Join("/", added.Take(4).Select(a => a.Title)))}");
        }
        if (meta.Authors is { Count: > 0 } && (old?.Authors ?? []).Count == 0)
            parts.Add($"作者: {Pv(string.Join("/", meta.Authors.Select(a => a.Name)))}");
        if (meta.Links != null)
        {
            var added = meta.Links.Where(l => !(old?.Links ?? []).Any(x => x.Url == l.Url)).ToList();
            if (added.Count > 0) parts.Add($"链接 +{added.Count}: {Pv(string.Join("/", added.Select(l => l.Label)))}");
        }
        return parts.Count > 0 ? string.Join("；", parts) : "(无字段变化)";
    }

    // ---------- 单行本抓取（对齐脚本 volumeMatesFetcher + asyncPool(10)） ----------

    private async Task<List<VolumeMate>> FetchVolumeMatesAsync(
        int subjectId, SeriesDto series, List<BookDto> books,
        HashSet<int> needUpdateVolumeNums, bool fetchVolumeData, CancellationToken ct)
    {
        List<BangumiRelatedSubjectDto> related;
        try
        {
            related = await bangumi.GetRelatedSubjectsAsync(subjectId, ct);
        }
        catch (Exception e)
        {
            log.Warn($"获取单行本关联失败: {e.Message}");
            return [];
        }

        // 单行本名 → 卷号。关键坑：Bangumi 很多系列的单行本条目 name_cn 是空字符串（不是 null！），
        // 「NameCn ?? Name」拿到的是 ""，卷号解析全部失败 → 旧逻辑退化成「关联列表的序号」当卷号，
        // 而关联列表按 Bangumi subject ID 排序、与卷序毫无关系 → 每卷封面/ISBN/简介整体张冠李戴
        // （实测乱马1/2：38 卷按 ID 序错位，Vol.02 拿到第 27 卷的封面，四份拷贝全乱）。
        // 必须先挑非空名字段再解析；两个名字都解析不出时才按列表序号兜底并告警。
        static string EffName(BangumiRelatedSubjectDto r) =>
            !string.IsNullOrWhiteSpace(r.NameCn) ? r.NameCn! :
            !string.IsNullOrWhiteSpace(r.Name) ? r.Name! : "";
        static int? VolNumOf(BangumiRelatedSubjectDto r)
        {
            var n = TitleParser.ExtractVolumeNumber(EffName(r));
            if (n.HasValue) return n;
            // 中文名解析不出 → 再试日文原名（顺序相反的两字段都试一遍）
            var other = string.IsNullOrWhiteSpace(r.NameCn) ? null : r.Name;
            return other != null ? TitleParser.ExtractVolumeNumber(other) : null;
        }

        var volumes = related
            .Where(r => r.Relation == "单行本")
            .OrderBy(r => VolNumOf(r) ?? int.MaxValue)
            .ThenBy(r => r.Id)
            .ToList();

        if (volumes.Count == 0)
        {
            log.Info("Bangumi 无 单行本 关联条目");
            return [];
        }

        // 解析不出卷号的单行本：以前直接拿「排序后的列表序号」当卷号 —— 未解析的都被 OrderBy 排到末尾，
        // 序号会落进已解析卷号的区间里撞车（实测一次任务 252 次兜底）→ 封面/简介静默挂到错误的册。
        // 现在按「最小未占用卷号」补齐，保证与已解析卷号不冲突。
        var parsedNums = volumes.Select(VolNumOf).ToList();
        var usedNums = parsedNums.Where(n => n.HasValue).Select(n => n!.Value).ToHashSet();
        var fallbackNums = new Dictionary<int, int>();
        var nextFree = 1;
        for (var i = 0; i < volumes.Count; i++)
        {
            if (parsedNums[i].HasValue) continue;
            while (usedNums.Contains(nextFree)) nextFree++;
            fallbackNums[i] = nextFree;
            usedNums.Add(nextFree);
        }

        var mates = new VolumeMate[volumes.Count];
        using var semaphore = new SemaphoreSlim(10);
        var tasks = volumes.Select(async (vol, index) =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var coverUrls = new List<string>();
                if (!string.IsNullOrEmpty(vol.Image)) coverUrls.Add(vol.Image);
                if (vol.Images != null)
                {
                    if (!string.IsNullOrEmpty(vol.Images.Large)) coverUrls.Add(vol.Images.Large);
                    if (!string.IsNullOrEmpty(vol.Images.Medium)) coverUrls.Add(vol.Images.Medium);
                    if (!string.IsNullOrEmpty(vol.Images.Common)) coverUrls.Add(vol.Images.Common);
                }
                coverUrls = coverUrls.Distinct(StringComparer.Ordinal).ToList();

                var num = parsedNums[index] ?? fallbackNums[index];
                if (parsedNums[index] == null)
                    log.Warn($"Bangumi 单行本《{EffName(vol)}》解析不出卷号，按未占用卷号 {num} 处理（避免与已解析卷号冲突）");

                var isNeedUpdate = needUpdateVolumeNums.Contains(num);

                var mate = new VolumeMate { Num = num.ToString(), CoverUrls = coverUrls };

                if (isNeedUpdate && fetchVolumeData)
                {
                    try
                    {
                        var detail = await bangumi.GetSubjectAsync(vol.Id, ct);
                        mate.Summary = SubjectMapper.CleanMultiline(detail.Summary);
                        // 单行本简介也可能是日文；已是中文时这一步不会调接口
                        mate.Summary = await TranslateIfNeededAsync(mate.Summary, $"卷 {num}", ct) ?? "";
                        var dateStr = SubjectMapper.ParseInfobox(detail.Infobox, "发售日")
                                      ?? SubjectMapper.ParseInfobox(detail.Infobox, "放送开始");
                        mate.ReleaseDate = TitleParser.NormalizeDate(dateStr) ?? "";
                        mate.Isbn = SubjectMapper.ParseInfobox(detail.Infobox, "ISBN") ?? "";
                    }
                    catch (Exception e)
                    {
                        log.Warn($"获取单行本详情失败 ({vol.Id}): {e.Message}");
                    }
                }

                mates[index] = mate;
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return mates.Where(m => m != null).ToList();
    }

    // ---------- 书籍更新（对齐脚本 updateKomgaBookAll） ----------

    private async Task UpdateBooksAsync(
        SeriesDto series, List<BookDto> books, string seriesName,
        List<AuthorDto>? bookAuthors, List<List<string>> bookVolumeCoverSets,
        List<VolumeMate> volumeMates, ScrapeOptions opt, CancellationToken ct)
    {
        if (books.Count == 0) return;
        if (books.Count >= MaxReqBooks)
        {
            log.Warn($"系列《{seriesName}》书籍数 ({books.Count}) 达到 {MaxReqBooks} 上限，跳过书籍处理");
            return;
        }

        // oneshot：用系列自身的卷信息
        if (series.Oneshot)
        {
            var existing = volumeMates.Count > 0 ? volumeMates[0] : new VolumeMate();
            var merged = new VolumeMate
            {
                Num = existing.Num,
                CoverUrls = existing.CoverUrls,
                Isbn = existing.Isbn,
                Summary = series.Metadata?.Summary ?? existing.Summary,
                ReleaseDate = existing.ReleaseDate,
            };
            volumeMates = [merged];
        }

        var bookUpdateNeeded = !opt.CoverOnly && (bookAuthors is { Count: > 0 } || volumeMates.Count > 0);
        var coverUpdateNeeded = bookVolumeCoverSets.Count > 0 && bookVolumeCoverSets.Any(s => s.Count > 0);
        if (!bookUpdateNeeded && !coverUpdateNeeded) return;

        await Parallel.ForEachAsync(books.Select((b, i) => (b, i)),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, BookConcurrency), CancellationToken = ct },
            async (pair, token) =>
            {
                var (book, i) = pair;
                var bookName = book.Name ?? "";
                var bookTitle = book.Metadata?.Title ?? "";
                var volNum = ExtractBookVolumeNumber(book);
                var volDisplay = volNum.ToString().PadLeft(2, '0');

                try
                {
                    VolumeMate? mate = null;
                    if (series.Oneshot)
                        mate = volumeMates.Count > 0 ? volumeMates[0] : null;
                    else if (volumeMates.Count > 0 && volNum > 0)
                        mate = volumeMates.FirstOrDefault(m => int.TryParse(m.Num, out var n) && n == volNum);

                                        // 兜底：卷号解析不出但两边「卷」数量一致 → 按 numberSort 序对齐
                    // （books 已按 numberSort 升序、volumeMates 已按卷号升序，同数时位置即卷序）
                    if (mate == null && volNum <= 0 && !series.Oneshot &&
                        books.Count == volumeMates.Count && i < volumeMates.Count)
                    {
                        mate = volumeMates[i];
                        if (int.TryParse(mate.Num, out var mn)) volDisplay = mn.ToString().PadLeft(2, '0');
                    }

                    if (bookUpdateNeeded)
                    {
                        var bookMeta = new BookMetadataDto { Authors = bookAuthors };
                        var nameMatches = TitleParser.VolumeTitleRegex().IsMatch(bookName)
                                          || TitleParser.VolumeTitleRegex().IsMatch(bookTitle);
                        if (nameMatches)
                        {
                            bookMeta.Title = $"卷 {volDisplay}";
                            bookMeta.Number = volDisplay;
                        }
                        if (mate != null)
                        {
                            if (!string.IsNullOrEmpty(mate.Summary)) bookMeta.Summary = mate.Summary;
                            if (!string.IsNullOrEmpty(mate.ReleaseDate)) bookMeta.ReleaseDate = mate.ReleaseDate;
                            if (!string.IsNullOrEmpty(mate.Isbn)) bookMeta.Isbn = mate.Isbn;
                        }
                        else if (opt.FetchVolumeData)
                        {
                            // 匹配源没有该卷的数据 → 旧简介/ISBN/发售日多半是上次错误匹配留下的，直接清空
                            //（用户口径：匹配不到的就清空，脏数据不留；Dry-run 只预览不动库）
                            var stale = new Dictionary<string, object?>();
                            if (!string.IsNullOrWhiteSpace(book.Metadata?.Summary)) stale["summary"] = "";
                            if (!string.IsNullOrWhiteSpace(book.Metadata?.ReleaseDate)) stale["releaseDate"] = "";
                            if (!string.IsNullOrWhiteSpace(book.Metadata?.Isbn)) stale["isbn"] = "";
                            if (stale.Count > 0)
                            {
                                if (opt.DryRun)
                                {
                                    log.Info($"[Dry-run] 《{seriesName}》卷 {volDisplay}：匹配源无该卷数据，将清空旧简介/ISBN/发售日");
                                    Observe(ScrapeChangeKind.BookMeta, book.SeriesId, $"《{seriesName}》卷 {volDisplay}",
                                        "将清空卷简介/ISBN/发售日（匹配源无该卷数据）", string.Join("、", stale.Keys));
                                }
                                else
                                {
                                    await komga.PatchBookMetadataRawAsync(book.Id, stale, token).ConfigureAwait(false);
                                    log.Info($"《{seriesName}》卷 {volDisplay}：匹配源无该卷数据，旧简介/ISBN/发售日已清空");
                                    Observe(ScrapeChangeKind.BookMeta, book.SeriesId, $"《{seriesName}》卷 {volDisplay}",
                                        "已清空卷简介/ISBN/发售日（匹配源无该卷数据）", string.Join("、", stale.Keys));
                                }
                            }
                        }

                        // 没有任何可写字段（常见于本地书名无卷号、Bangumi 也没给单行本数据）→ 不发请求也不刷日志
                        var hasBookChange = KomgaClient.HasChanges(bookMeta);
                        if (hasBookChange && opt.DryRun)
                        {
                            log.Info($"[Dry-run] PATCH /books/{book.Id}/metadata：《{seriesName}》卷 {volDisplay} " +
                                     $"title={bookMeta.Title} summary={(!string.IsNullOrEmpty(bookMeta.Summary) ? "有" : "无")} " +
                                     $"releaseDate={bookMeta.ReleaseDate ?? "-"} isbn={bookMeta.Isbn ?? "-"}" +
                                     (bookMeta.Authors is { Count: > 0 }
                                         ? $" authors=[{string.Join(";", bookMeta.Authors.Select(a => a.Name))}]"
                                         : ""));
                            Observe(ScrapeChangeKind.BookMeta, book.SeriesId, $"《{seriesName}》卷 {volDisplay}",
                                "将更新卷信息（Bangumi）",
                                PreviewBookPatch(book.Metadata, bookMeta));
                        }
                        else if (hasBookChange)
                        {
                            await komga.PatchBookMetadataAsync(book.Id, bookMeta, token).ConfigureAwait(false);
                            log.Info($"《{seriesName}》第 {book.Number} 卷信息已更新");
                            Observe(ScrapeChangeKind.BookMeta, book.SeriesId, $"《{seriesName}》卷 {volDisplay}",
                                "已更新卷信息（Bangumi）", PreviewBookPatch(book.Metadata, bookMeta));
                        }
                    }

                    if (coverUpdateNeeded && opt.SyncType == SyncType.All)
                    {
                        List<string>? coverUrls = null;
                        if (volumeMates.Count > 0)
                        {
                            if (mate != null)
                            {
                                var mateIndex = volumeMates.IndexOf(mate);
                                if (mateIndex >= 0 && mateIndex < bookVolumeCoverSets.Count &&
                                    bookVolumeCoverSets[mateIndex].Count > 0)
                                    coverUrls = bookVolumeCoverSets[mateIndex];
                            }
                        }
                        else if (i < bookVolumeCoverSets.Count && bookVolumeCoverSets[i].Count > 0)
                        {
                            coverUrls = bookVolumeCoverSets[i];
                        }

                        if (coverUrls != null)
                            await UpdateBookCoverAsync(book, seriesName, volDisplay, coverUrls, opt, token)
                                .ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    log.Error($"处理《{seriesName}》卷 {volDisplay} (book {book.Id}) 出错: {e.Message}");
                }
            }).ConfigureAwait(false);
    }

    // ---------- 封面上传（对齐脚本 updateKomgaSeriesCover / updateKomgaBookCover） ----------

    private async Task UpdateSeriesCoverAsync(string seriesId, string seriesName,
        List<string> orderedUrls, ScrapeOptions opt, CancellationToken ct)
    {
        if (opt.DryRun)
        {
            log.Info($"[Dry-run] 将清理并上传系列封面《{seriesName}》（{orderedUrls.Count} 个候选 URL）");
            Observe(ScrapeChangeKind.SeriesCover, seriesId, $"《{seriesName}》",
                $"将清理并上传系列封面（{orderedUrls.Count} 个候选）");
            return;
        }

        await CleanSeriesCoversAsync(seriesId, seriesName, ct);

        var validTried = false;
        for (int i = 0; i < orderedUrls.Count; i++)
        {
            var url = orderedUrls[i];
            var sizeLabel = i == 0 ? "首选" : i == 1 ? "中尺寸" : i == 2 ? "通用尺寸" : "小尺寸";

            byte[] blob;
            try
            {
                blob = await DownloadImageAsync(url, ct);
            }
            catch (Exception e)
            {
                // 单个 URL 失败（超时/DNS/代理不通）不该终止整条链，继续试下一个尺寸
                log.Warn($"《{seriesName}》{sizeLabel} 封面下载失败，换下一个尺寸: {e.Message}");
                continue;
            }

            try
            {
                if (blob.Length == 0)
                {
                    log.Warn($"《{seriesName}》下载 {sizeLabel} 封面失败或为空");
                    continue;
                }
                // 不做大小过滤：能下载到的就是最好的封面（太大由服务器 413 兜底换下一档）
                validTried = true;
                await komga.UploadSeriesCoverAsync(seriesId, blob, $"series_cover_{seriesId}.jpg", ct);
                log.Info($"《{seriesName}》系列封面 ({sizeLabel}, {blob.Length / 1024.0:F0}KB) 已更新");
                Observe(ScrapeChangeKind.SeriesCover, seriesId, $"《{seriesName}》",
                    $"已上传系列封面（{sizeLabel}，{blob.Length / 1024.0:F0}KB）");
                State?.SetCover(seriesId, true);
                return;
            }
            catch (KomgaApiException e) when ((int)e.Status == 413)
            {
                log.Warn($"《{seriesName}》{sizeLabel} 封面过大(413)，尝试 WebP 分级压缩（质量 95→80）…");
                var uploaded = await CoverCompressor.CompressAndUploadAsync(blob, async data =>
                {
                    await komga.UploadSeriesCoverAsync(seriesId, data, $"series_cover_{seriesId}.webp", ct);
                    return true;
                }, log, $"《{seriesName}》{sizeLabel}", ct);
                if (uploaded)
                {
                    Observe(ScrapeChangeKind.SeriesCover, seriesId, $"《{seriesName}》",
                        $"已上传系列封面（{sizeLabel}，WebP 压缩，{blob.Length / 1024.0:F0}KB→压缩档）");
                    State?.SetCover(seriesId, true);
                    return;
                }
                log.Warn($"《{seriesName}》{sizeLabel} 压缩后仍过大，尝试更小尺寸…");
                if (i == orderedUrls.Count - 1 && validTried)
                    log.Error($"《{seriesName}》所有尺寸封面均因过大(413)上传失败（含 WebP 压缩档），请检查服务器配置");
            }
            catch (Exception e)
            {
                log.Error($"《{seriesName}》系列封面 ({sizeLabel}) 更新失败: {e.Message}");
                return;
            }
        }
        if (!validTried)
            log.Error($"《{seriesName}》所有封面候选都不可用（下载失败），未上传封面。\n" +
                      "  → 若日志里是连接超时/DNS，请在 设置 → 代理 里配好代理（Bangumi 与封面共用）");
    }

    private async Task CleanSeriesCoversAsync(string seriesId, string seriesName, CancellationToken ct)
    {
        try
        {
            var thumbs = await komga.GetSeriesThumbnailsAsync(seriesId, ct);
            var toClean = thumbs.Where(t => t.Type == "USER_UPLOADED" && !t.Selected).ToList();
            foreach (var thumb in toClean)
            {
                try
                {
                    await komga.DeleteSeriesThumbnailAsync(seriesId, thumb.Id, ct);
                }
                catch (Exception e)
                {
                    log.Error($"《{seriesName}》系列封面清理失败 (ID: {thumb.Id}): {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            log.Warn($"《{seriesName}》获取现有封面失败: {e.Message}");
        }
    }

    private async Task UpdateOneshotBookCoverAsync(SeriesDto series, List<BookDto> books,
        string seriesName, List<string> coverUrls, CancellationToken ct)
    {
        if (books.Count == 0)
        {
            log.Warn($"《{seriesName}》One-Shot 没有关联书籍");
            return;
        }
        var opt = new ScrapeOptions { SyncType = SyncType.All };
        if (await UpdateBookCoverAsync(books[0], seriesName, "", coverUrls, opt, ct).ConfigureAwait(false))
            State?.SetCover(series.Id, true);   // One-Shot 的书封面就是系列封面
    }

    /// <summary>上传单册封面；返回是否封面就位（上传成功，或已存在相同封面视为已刮）。</summary>
    private async Task<bool> UpdateBookCoverAsync(BookDto book, string seriesName, string volDisplay,
        List<string> orderedUrls, ScrapeOptions opt, CancellationToken ct)
    {
        if (orderedUrls.Count == 0) return false;
        if (opt.DryRun)
        {
            log.Info($"[Dry-run] 将上传《{seriesName}》卷 {volDisplay} 书封面（{orderedUrls.Count} 个候选 URL）");
            Observe(ScrapeChangeKind.BookCover, book.SeriesId, $"《{seriesName}》卷 {volDisplay}",
                $"将上传卷封面（{orderedUrls.Count} 个候选）");
            return false;
        }

        var validTried = false;
        for (int i = 0; i < orderedUrls.Count; i++)
        {
            var url = orderedUrls[i];
            var sizeLabel = i == 0 ? "首选" : i == 1 ? "中等" : i == 2 ? "通用" : "较小";

            byte[] blob;
            try
            {
                blob = await DownloadImageAsync(url, ct);
            }
            catch (Exception e)
            {
                log.Warn($"《{seriesName}》卷 {volDisplay} {sizeLabel} 封面下载失败，换下一个尺寸: {e.Message}");
                continue;
            }

            try
            {
                if (blob.Length == 0) throw new Exception("下载图片失败");

                // 已存在相同大小的封面则跳过（同一张图不重复上传，不是大小过滤）
                var existing = await komga.GetBookThumbnailsAsync(book.Id, ct);
                if (existing.Any(t => t.FileSize == blob.Length))
                {
                    log.Info($"《{seriesName}》卷 {volDisplay} 封面已存在相同大小，跳过");
                    return true;
                }

                validTried = true;
                await komga.UploadBookCoverAsync(book.Id, blob, $"book_cover_{book.Id}.jpg", ct);
                log.Info($"《{seriesName}》卷 {volDisplay} 封面 ({sizeLabel}, {blob.Length / 1024.0:F0}KB) 已更新");
                Observe(ScrapeChangeKind.BookCover, book.SeriesId, $"《{seriesName}》卷 {volDisplay}",
                    $"已上传卷封面（{sizeLabel}，{blob.Length / 1024.0:F0}KB）");
                return true;
            }
            catch (KomgaApiException e) when ((int)e.Status == 413)
            {
                log.Warn($"《{seriesName}》卷 {volDisplay} {sizeLabel} 封面过大(413)，尝试 WebP 分级压缩（质量 95→80）…");
                var uploaded = await CoverCompressor.CompressAndUploadAsync(blob, async data =>
                {
                    await komga.UploadBookCoverAsync(book.Id, data, $"book_cover_{book.Id}.webp", ct);
                    return true;
                }, log, $"《{seriesName}》卷 {volDisplay} {sizeLabel}", ct);
                if (uploaded)
                {
                    Observe(ScrapeChangeKind.BookCover, book.SeriesId, $"《{seriesName}》卷 {volDisplay}",
                        $"已上传卷封面（{sizeLabel}，WebP 压缩，{blob.Length / 1024.0:F0}KB→压缩档）");
                    return true;
                }
                log.Warn($"《{seriesName}》卷 {volDisplay} {sizeLabel} 压缩后仍过大，尝试更小…");
                if (i == orderedUrls.Count - 1 && validTried)
                    log.Error($"《{seriesName}》卷 {volDisplay} 所有尺寸封面均因过大(413)上传失败（含 WebP 压缩档）");
            }
            catch (Exception e)
            {
                log.Error($"《{seriesName}》卷 {volDisplay} 封面 ({sizeLabel}) 更新失败: {e.Message}");
                return false;
            }
        }
        if (!validTried)
            log.Error($"《{seriesName}》卷 {volDisplay} 所有封面候选都不可用（下载失败），未上传");
        return false;
    }

    // ---------- 启发式（对齐脚本 ifUpdateBook / getVolumeNumsNeedUpdate） ----------

    private static bool IfUpdateBook(List<BookDto> books, List<AuthorDto>? bookAuthors)
    {
        if (bookAuthors is not { Count: > 0 }) return false;
        if (books.Count == 0) return false;
        var lastBook = books[^1];
        var meta = lastBook.Metadata;
        if (meta == null || string.IsNullOrEmpty(meta.Title) || string.IsNullOrEmpty(meta.Summary) ||
            string.IsNullOrEmpty(meta.ReleaseDate) || string.IsNullOrEmpty(meta.Isbn))
            return true;
        var title = meta.Title;
        if (title.Length > 16) return true;
        if (title.Contains('[') || title.Contains(']')) return true;
        var lower = title.ToLowerInvariant();
        if (lower.Contains(".zip") || lower.Contains(".cbz")) return true;
        if (!System.Text.RegularExpressions.Regex.IsMatch(title, @"^vol(ume)?s?\s*\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            !System.Text.RegularExpressions.Regex.IsMatch(title, @"^(?:巻|卷|册|第)\s*\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        if ((meta.Authors ?? []).Count == 0) return true;
        return false;
    }

    private static HashSet<int> GetVolumeNumsNeedUpdate(List<BookDto> books)
    {
        var need = new HashSet<int>();
        foreach (var book in books)
        {
            var volNum = TitleParser.ExtractBookVolumeNumber(book.Name, book.Metadata?.Title, book.Metadata?.Number);
            if (volNum <= 0) continue;

            var meta = book.Metadata;
            if (meta == null || string.IsNullOrEmpty(meta.Title) || string.IsNullOrEmpty(meta.Summary) ||
                string.IsNullOrEmpty(meta.ReleaseDate) || string.IsNullOrEmpty(meta.Isbn))
                need.Add(volNum);
        }
        return need;
    }
}