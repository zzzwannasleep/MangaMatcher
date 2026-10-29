namespace KomgaScraper.Core.Komga;

/// <summary>重复清理的粒度：整部系列（默认，全册都对上才删）/ 逐册。</summary>
public enum DedupeMode
{
    WholeSeries,
    PerBook,
}

/// <summary>重复判定强度：只看大小 / 大小+页数（默认）/ 大小+页数+文件哈希。</summary>
public enum DedupeMatch
{
    SizeOnly,
    SizeAndPages,
    SizePagesAndHash,
}

/// <summary>清理项类型。PartialSeries = 只重复了一部分，整部模式下不提供勾选（需人工处理）。</summary>
public enum DedupeKind
{
    Series,
    Book,
    PartialSeries,
}

public sealed class DedupeOptions
{
    public DedupeMode Mode { get; init; } = DedupeMode.WholeSeries;
    public DedupeMatch Match { get; init; } = DedupeMatch.SizeAndPages;
}

/// <summary>一条“待删除”候选：要么是清理库里的一整部系列，要么是其中一册。</summary>
public sealed class DedupeCandidate
{
    public DedupeKind Kind { get; init; }
    /// <summary>删除目标 id：Kind=Series → seriesId；Kind=Book → bookId。</summary>
    public string DeleteId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Folder { get; init; } = "";
    /// <summary>清理库这部系列的册数（逐册行恒为 1）。</summary>
    public int BookCount { get; init; }
    /// <summary>其中命中重复的册数。</summary>
    public int MatchedBooks { get; init; }
    /// <summary>将删除的字节数。</summary>
    public long Bytes { get; init; }
    /// <summary>保留库里对应的系列标题（多部时用「等 N 部」概括）。</summary>
    public string KeepTitles { get; init; } = "";
    public string Evidence { get; init; } = "";

    /// <summary>整部模式下部分重复的行不可勾选（避免删出残缺系列）。</summary>
    public bool Selectable => Kind != DedupeKind.PartialSeries;

    public string SizeText => DuplicateCleaner.FormatBytes(Bytes);

    public string KindText => Kind switch
    {
        DedupeKind.Series => "整部系列",
        DedupeKind.Book => "单册",
        _ => "部分重复",
    };
}

public sealed class DedupePlan
{
    public List<DedupeCandidate> Candidates { get; init; } = [];
    public int KeepBookCount { get; init; }
    public int CleanBookCount { get; init; }
    public string Summary { get; init; } = "";

    public IEnumerable<DedupeCandidate> Deletable => Candidates.Where(c => c.Selectable);
}

/// <summary>
/// 跨库重复规划：以「保留库」为基准，在「清理库」里找大小相同（可叠加页数/哈希确认）的书籍，
/// 再按用户选的粒度产出可删除清单。纯内存计算，不碰网络，便于单测。
/// </summary>
public static class DedupePlanner
{
    public static DedupePlan Build(
        IReadOnlyList<SeriesDto> keepSeries,
        IReadOnlyList<BookDto> keepBooks,
        IReadOnlyList<SeriesDto> cleanSeries,
        IReadOnlyList<BookDto> cleanBooks,
        DedupeOptions options)
    {
        // 保留库：大小 → 书籍列表（索引一次，O(n) 比对）
        var keepBySize = new Dictionary<long, List<BookDto>>();
        foreach (var b in keepBooks)
        {
            var size = b.EffectiveSizeBytes;
            if (size <= 0) continue;
            if (!keepBySize.TryGetValue(size, out var list)) keepBySize[size] = list = [];
            list.Add(b);
        }

        // 系列 id → 标题（series 列表优先，books/list 的 seriesTitle 兜底）
        var keepTitles = SeriesTitles(keepSeries, keepBooks);

        // 清理库：系列 id → 书籍列表
        var cleanBySeries = new Dictionary<string, List<BookDto>>();
        foreach (var b in cleanBooks)
        {
            if (!cleanBySeries.TryGetValue(b.SeriesId, out var list)) cleanBySeries[b.SeriesId] = list = [];
            list.Add(b);
        }

        var rows = new List<DedupeCandidate>();
        foreach (var series in cleanSeries)
        {
            if (!cleanBySeries.TryGetValue(series.Id, out var books) || books.Count == 0) continue;
            var title = SeriesTitle(series);
            var folder = FolderName(series);

            var matched = new List<(BookDto Clean, BookDto Keep, string KeepTitle)>();
            foreach (var book in books)
            {
                var hit = FindKeepMatch(book, keepBySize, title, keepTitles, options.Match);
                if (hit != null) matched.Add((book, hit.Value.Book, hit.Value.Title));
            }
            if (matched.Count == 0) continue;

            var bytes = matched.Sum(m => m.Clean.EffectiveSizeBytes);
            var keepLabel = KeepTitlesText(matched.Select(m => m.KeepTitle));

            if (options.Mode == DedupeMode.WholeSeries)
            {
                var full = matched.Count == books.Count;
                rows.Add(new DedupeCandidate
                {
                    Kind = full ? DedupeKind.Series : DedupeKind.PartialSeries,
                    DeleteId = series.Id,
                    Title = title,
                    Folder = folder,
                    BookCount = books.Count,
                    MatchedBooks = matched.Count,
                    Bytes = bytes,
                    KeepTitles = keepLabel,
                    Evidence = full
                        ? $"{books.Count}/{books.Count} 册全部对应（{MatchText(options.Match)}）"
                        : $"{matched.Count}/{books.Count} 册重复 · {Fmt(bytes)}（整部模式不处理）",
                });
            }
            else
            {
                foreach (var m in matched.OrderBy(m => m.Clean.Number))
                {
                    var pages = m.Clean.Media?.PagesCount ?? 0;
                    rows.Add(new DedupeCandidate
                    {
                        Kind = DedupeKind.Book,
                        DeleteId = m.Clean.Id,
                        Title = $"{title} · {BookLabel(m.Clean)}",
                        Folder = folder,
                        BookCount = 1,
                        MatchedBooks = 1,
                        Bytes = m.Clean.EffectiveSizeBytes,
                        KeepTitles = string.IsNullOrWhiteSpace(m.KeepTitle) ? "（保留库同尺寸书籍）" : m.KeepTitle,
                        Evidence = Fmt(m.Clean.EffectiveSizeBytes) +
                                   (pages > 0 ? $" · {pages} 页" : "") +
                                   $" · {MatchText(options.Match)}",
                    });
                }
            }
        }

        var ordered = rows.OrderByDescending(r => r.Bytes).ToList();
        var deletable = ordered.Where(r => r.Selectable).ToList();
        var partial = ordered.Count(r => r.Kind == DedupeKind.PartialSeries);
        var bytesTotal = deletable.Sum(r => r.Bytes);
        var unitCount = options.Mode == DedupeMode.WholeSeries
            ? deletable.Count
            : deletable.Sum(r => r.BookCount);

        var summary = ordered.Count == 0
            ? $"未发现重复：保留库 {keepBooks.Count} 册 / 清理库 {cleanBooks.Count} 册"
            : $"共扫描 保留库 {keepBooks.Count} 册 / 清理库 {cleanBooks.Count} 册 → " +
              (options.Mode == DedupeMode.WholeSeries
                  ? $"可删 {unitCount} 部系列"
                  : $"可删 {unitCount} 册") +
              $"（{Fmt(bytesTotal)}）" +
              (partial > 0 ? $"；另有 {partial} 部部分重复，需人工处理" : "");

        return new DedupePlan
        {
            Candidates = ordered,
            KeepBookCount = keepBooks.Count,
            CleanBookCount = cleanBooks.Count,
            Summary = summary,
        };
    }

    /// <summary>
    /// 在保留库同一大小的书里挑一本对得上的：优先同系列标题，其次任意一本；
    /// 页数（双方都有时）与哈希（双方都有时）必须一致。
    /// </summary>
    private static (BookDto Book, string Title)? FindKeepMatch(
        BookDto clean, Dictionary<long, List<BookDto>> keepBySize, string cleanTitle,
        Dictionary<string, string> keepTitles, DedupeMatch match)
    {
        var size = clean.EffectiveSizeBytes;
        if (size <= 0 || !keepBySize.TryGetValue(size, out var candidates)) return null;

        (BookDto Book, string Title)? fallback = null;
        foreach (var keep in candidates)
        {
            if (keep.Id == clean.Id) continue;
            if (!Compatible(clean, keep, match)) continue;
            var keepTitle = keepTitles.TryGetValue(keep.SeriesId, out var t) ? t : (keep.SeriesTitle ?? "");
            if (SameTitle(cleanTitle, keepTitle)) return (keep, keepTitle);
            fallback ??= (keep, keepTitle);
        }
        return fallback;
    }

    private static bool Compatible(BookDto clean, BookDto keep, DedupeMatch match)
    {
        if (match == DedupeMatch.SizeOnly) return true;   // 大小已在索引键上相等

        // 页数：双方都已知时必须一致（任一侧未知则放行，避免旧库没页数时全被拒）
        var cleanPages = clean.Media?.PagesCount ?? 0;
        var keepPages = keep.Media?.PagesCount ?? 0;
        if (cleanPages > 0 && keepPages > 0 && cleanPages != keepPages) return false;

        if (match == DedupeMatch.SizePagesAndHash &&
            !string.IsNullOrWhiteSpace(clean.FileHash) &&
            !string.IsNullOrWhiteSpace(keep.FileHash) &&
            !string.Equals(clean.FileHash, keep.FileHash, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static Dictionary<string, string> SeriesTitles(
        IReadOnlyList<SeriesDto> series, IReadOnlyList<BookDto> books)
    {
        var map = new Dictionary<string, string>();
        foreach (var s in series) map[s.Id] = SeriesTitle(s);
        foreach (var b in books)
        {
            if (!map.ContainsKey(b.SeriesId) && !string.IsNullOrWhiteSpace(b.SeriesTitle))
                map[b.SeriesId] = b.SeriesTitle!;
        }
        return map;
    }

    public static string SeriesTitle(SeriesDto s)
    {
        var title = s.Metadata?.Title;
        return string.IsNullOrWhiteSpace(title) ? s.Name : title!;
    }

    /// <summary>文件夹名 = series.url 最后一段（与 Mylar 导出一致）。</summary>
    public static string FolderName(SeriesDto s)
    {
        var url = s.Url ?? "";
        var idx = url.LastIndexOfAny(['/', '\\']);
        return idx >= 0 && idx + 1 < url.Length ? url[(idx + 1)..] : url;
    }

    private static string BookLabel(BookDto b)
    {
        if (!string.IsNullOrWhiteSpace(b.Metadata?.Title)) return b.Metadata!.Title!;
        if (!string.IsNullOrWhiteSpace(b.Name)) return b.Name;
        return $"第 {b.Number} 册";
    }

    private static string KeepTitlesText(IEnumerable<string> titles)
    {
        var distinct = titles.Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return "";
        return distinct.Count <= 2
            ? string.Join(" / ", distinct)
            : $"{string.Join(" / ", distinct.Take(2))} 等 {distinct.Count} 部";
    }

    private static bool SameTitle(string a, string b)
    {
        static string Norm(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c))).Trim();
        return Norm(a).Equals(Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string MatchText(DedupeMatch match) => match switch
    {
        DedupeMatch.SizeOnly => "仅大小",
        DedupeMatch.SizeAndPages => "大小+页数",
        _ => "大小+页数+哈希",
    };

    private static string Fmt(long bytes) => DuplicateCleaner.FormatBytes(bytes);
}

/// <summary>
/// 重复清理服务：拉两个库的数据 → 规划 → 按用户勾选删除「清理库」里重复的册/系列。
/// 删除走 Komga 官方接口（DELETE /books/{id}/file 或 /series/{id}/file），返回 202 异步完成。
/// 保留库永远只读，不会被删。
/// </summary>
public sealed class DuplicateCleaner(KomgaClient komga, IAppLogger log)
{
    /// <summary>最近一次 ExecuteAsync 已处理的项数（中途取消时也有效，UI 用来刷新缓存）。</summary>
    public int LastRunCount { get; private set; }

    public async Task<DedupePlan> ScanAsync(LibraryDto keep, LibraryDto clean, DedupeOptions options,
        CancellationToken ct = default)
    {
        if (keep.Id == clean.Id)
            throw new InvalidOperationException("保留库与清理库不能是同一个库");

        log.Info($"[重复清理] 扫描开始：保留库「{keep.Name}」/ 清理库「{clean.Name}」");
        var keepSeries = await komga.ListSeriesInLibraryAsync(keep.Id, ct).ConfigureAwait(false);
        var keepBooks = await komga.ListBooksInLibraryAsync(keep.Id, ct).ConfigureAwait(false);
        log.Info($"[重复清理] 保留库「{keep.Name}」：{keepSeries.Count} 部 / {keepBooks.Count} 册");

        var cleanSeries = await komga.ListSeriesInLibraryAsync(clean.Id, ct).ConfigureAwait(false);
        var cleanBooks = await komga.ListBooksInLibraryAsync(clean.Id, ct).ConfigureAwait(false);
        log.Info($"[重复清理] 清理库「{clean.Name}」：{cleanSeries.Count} 部 / {cleanBooks.Count} 册");

        var plan = DedupePlanner.Build(keepSeries, keepBooks, cleanSeries, cleanBooks, options);
        log.Info($"[重复清理] {plan.Summary}");
        return plan;
    }

    /// <summary>执行删除；dryRun=true 只逐条打「将删除」日志。返回实际处理项数。</summary>
    public async Task<int> ExecuteAsync(IEnumerable<DedupeCandidate> selected, bool dryRun,
        CancellationToken ct = default)
    {
        var list = selected.Where(c => c.Selectable).ToList();
        LastRunCount = 0;
        if (list.Count == 0) return 0;
        log.Info($"[重复清理] {(dryRun ? "Dry-run 预览" : "开始删除")}：{list.Count} 项");
        var done = 0;
        for (var i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = list[i];
            var target = c.Kind == DedupeKind.Series ? $"整部系列《{c.Title}》（{c.BookCount} 册）" : $"《{c.Title}》";
            try
            {
                if (dryRun)
                {
                    log.Info($"[重复清理][Dry-run] 将删除 {target} · {c.SizeText}");
                }
                else
                {
                    if (c.Kind == DedupeKind.Series)
                        await komga.DeleteSeriesFileAsync(c.DeleteId, ct).ConfigureAwait(false);
                    else
                        await komga.DeleteBookFileAsync(c.DeleteId, ct).ConfigureAwait(false);
                    log.Info($"[重复清理] 已删除 {target} · {c.SizeText}");
                    await Task.Delay(150, ct).ConfigureAwait(false);   // 节流，别一次打爆 Komga
                }
                done++;
                LastRunCount = done;
                log.Progress(done, list.Count, "重复清理");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                log.Error($"[重复清理] 删除失败 {target}: {e.Message}");
            }
        }
        log.Info($"[重复清理] 完成：{(dryRun ? "预览" : "删除")} {done}/{list.Count} 项");
        return done;
    }

    /// <summary>字节数 → 人类可读（KB/MB/GB，保留一位小数）。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytes switch
        {
            < (long)kb => $"{bytes} B",
            < (long)mb => $"{bytes / kb:F1} KB",
            < (long)gb => $"{bytes / mb:F1} MB",
            _ => $"{bytes / gb:F2} GB",
        };
    }
}
