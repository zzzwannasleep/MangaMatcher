using KomgaScraper.Core;
using KomgaScraper.Core.Komga;

// 重复清理（跨库同尺寸去重）自测：
//  1) 规划逻辑：整部/逐册；页数、哈希、大小未知的判定；册数不完整不整部删
//  2) 端到端：本地假 Komga（自建 TCP HTTP 服务）跑扫描（强制小分页，验证按 totalElements 取全量）
//     + Dry-run + 真实删除 + 部分失败 + 404 幂等。
// 退出码 0 = 全过。

var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail == null ? "" : "  — " + detail)}");
    if (!ok) failures.Add(name);
}

// ===================== 1. 规划逻辑（纯内存） =====================
// 约定：k* 在保留库，c* 在清理库；第二参数是 Series.BooksCount。

var keepSeries = new List<SeriesDto>
{
    Series("k1", "碧蓝之海", "keep", 3),
    Series("k2", "别的书", "keep", 1),
    Series("k3", "页数陷阱", "keep", 1),
    Series("k4", "哈希陷阱", "keep", 1),
    Series("k5", "无页数陷阱", "keep", 1),
    Series("k6", "册数统计", "keep", 1),
};
var keepBooks = new List<BookDto>
{
    Book("kb1", "k1", "碧蓝之海", 1000, 10, "h1"),
    Book("kb2", "k1", "碧蓝之海", 2000, 20),
    Book("kb3", "k1", "碧蓝之海", 3000, 30),
    Book("kb4", "k2", "别的书", 9000, 50),
    Book("kb5", "k3", "页数陷阱", 4000, 15),
    Book("kb6", "k4", "哈希陷阱", 6000, 6, "aaa"),
    Book("kb7", "k5", "无页数陷阱", 8000, 0),        // 页数未知、无哈希
    Book("kb8", "k6", "册数统计", 1200, 12),
};

var cleanSeries = new List<SeriesDto>
{
    Series("c1", "碧蓝之海", "clean", 3),
    Series("c2", "半重复", "clean", 2),
    Series("c3", "完全不同", "clean", 1),
    Series("c4", "大小相同页数不同", "clean", 1),
    Series("c5", "大小相同哈希不同", "clean", 1),
    Series("c6", "无页数陷阱", "clean", 1),
    Series("c7", "册数不完整", "clean", 3),          // 列表只返回 2 册（BooksCount=3）
    Series("c8", "大小未知", "clean", 1),
};
var cleanBooks = new List<BookDto>
{
    Book("cb1", "c1", "碧蓝之海", 1000, 10, "h1"),
    Book("cb2", "c1", "碧蓝之海", 2000, 20),
    Book("cb3", "c1", "碧蓝之海", 3000, 30),
    Book("cb4", "c2", "半重复", 1000, 10, "h1"),
    Book("cb5", "c2", "半重复", 7777, 7),
    Book("cb6", "c3", "完全不同", 5555, 5),
    Book("cb7", "c4", "大小相同页数不同", 4000, 5),
    Book("cb8", "c5", "大小相同哈希不同", 6000, 6, "bbb"),
    Book("cb9", "c6", "无页数陷阱", 8000, 0),
    Book("cb10", "c7", "册数不完整", 1200, 12),
    Book("cb11", "c7", "册数不完整", 2000, 20),
    Book("cb12", "c8", "大小未知", 0, 0),            // 大小未知（跳过的册）
};

var whole = DedupePlanner.Build(keepSeries, keepBooks, cleanSeries, cleanBooks, new DedupeOptions());
var c1Row = whole.Candidates.FirstOrDefault(c => c.DeleteId == "c1");
Check("整部模式：c1 整部可删（3/3 册）",
    c1Row is { Kind: DedupeKind.Series, Selectable: true, BookCount: 3, MatchedBooks: 3 });
Check("整部模式：匹配依据写明大小+页数+哈希",
    c1Row != null && c1Row.Evidence.Contains("大小+页数+哈希"), c1Row?.Evidence);
Check("整部模式：c2 部分重复且不可勾选",
    whole.Candidates.Any(c => c.DeleteId == "c2" && c.Kind == DedupeKind.PartialSeries && !c.Selectable));
Check("整部模式：c7 列表册数不完整 → 不整部删",
    whole.Candidates.Any(c => c.DeleteId == "c7" && c.Kind == DedupeKind.PartialSeries
                              && c.Evidence.Contains("只返回")));
Check("整部模式：页数不同被拒（c4）", whole.Candidates.All(c => c.DeleteId != "c4"));
Check("整部模式：哈希不同被拒（c5）", whole.Candidates.All(c => c.DeleteId != "c5"));
Check("整部模式：页数未知且无哈希被拒（c6）", whole.Candidates.All(c => c.DeleteId != "c6"));
Check("整部模式：大小未知的册跳过并计数",
    whole.CleanUnknownSizeBooks == 1 && whole.Summary.Contains("大小未知"), whole.Summary);
Check("整部模式：只可删 1 部、2 部需人工",
    whole.Deletable.Count() == 1 && whole.Candidates.Count(c => c.Kind == DedupeKind.PartialSeries) == 2);

var perBook = DedupePlanner.Build(keepSeries, keepBooks, cleanSeries, cleanBooks,
    new DedupeOptions { Mode = DedupeMode.PerBook });
Check("逐册模式：6 册可删", perBook.Deletable.Count() == 6, $"实际 {perBook.Deletable.Count()}");
Check("逐册模式：待删字节合计 10200", perBook.Candidates.Sum(c => c.Bytes) == 10200);
Check("逐册模式：大小未知的 cb12 不出现", perBook.Candidates.All(c => c.DeleteId != "cb12"));

var sizeOnly = DedupePlanner.Build(keepSeries, keepBooks, cleanSeries, cleanBooks,
    new DedupeOptions { Match = DedupeMatch.SizeOnly });
Check("仅大小模式：页数/哈希/页数未知的陷阱都命中",
    sizeOnly.Candidates.Any(c => c.DeleteId == "c4") &&
    sizeOnly.Candidates.Any(c => c.DeleteId == "c5") &&
    sizeOnly.Candidates.Any(c => c.DeleteId == "c6"));
Check("仅大小模式：册数不完整的 c7 仍不整部删",
    sizeOnly.Candidates.Any(c => c.DeleteId == "c7" && c.Kind == DedupeKind.PartialSeries));
Check("仅大小模式：4 部可删", sizeOnly.Deletable.Count() == 4);

var strict = DedupePlanner.Build(keepSeries, keepBooks, cleanSeries, cleanBooks,
    new DedupeOptions { Match = DedupeMatch.SizePagesAndHash });
Check("最严模式：缺哈希的册不认（只剩部分重复、无可删）",
    strict.Deletable.Count() == 0 && strict.Candidates.All(c => c.Kind == DedupeKind.PartialSeries),
    $"可删 {strict.Deletable.Count()}");

Check("字节格式化", DuplicateCleaner.FormatBytes(1536) == "1.5 KB" && DuplicateCleaner.FormatBytes(0) == "0 B");

// ===================== 2. 端到端（本地假 Komga，强制小分页） =====================

using var mock = new MockKomga { BookPageSize = 3 };
var keepLib = new LibraryDto { Id = "keep", Name = "保留库", Root = "/data/keep" };
var cleanLib = new LibraryDto { Id = "clean", Name = "清理库", Root = "/data/clean" };
mock.Libraries.Add(keepLib);
mock.Libraries.Add(cleanLib);
foreach (var s in keepSeries.Concat(cleanSeries)) mock.Series.Add(s);
foreach (var b in keepBooks.Concat(cleanBooks)) mock.Books.Add(b);
// 服务端多一条已删书，验证 deleted=isFalse 条件确实被模拟并过滤
mock.Books.Add(Book("cb13", "c3", "完全不同", 5555, 5, deleted: true));

using var komga = new KomgaClient(mock.BaseUrl, "test-key", null, null, null, NullLogger.Instance);
var cleaner = new DuplicateCleaner(komga, new ConsLog());

var serverPlan = await cleaner.ScanAsync(keepLib, cleanLib, new DedupeOptions());
Check("服务端扫描：小分页下仍取全量（保留 8 册 / 清理 12 册）",
    serverPlan.KeepBookCount == 8 && serverPlan.CleanBookCount == 12,
    $"{serverPlan.KeepBookCount} / {serverPlan.CleanBookCount}");
Check("服务端扫描：可删 1 部", serverPlan.Deletable.Count() == 1);

var dry = await cleaner.ExecuteAsync(serverPlan.Deletable, dryRun: true);
Check("Dry-run：处理 1 项且零删除请求", dry == 1 && mock.Deletes.IsEmpty, $"请求 {mock.Deletes.Count} 个");
Check("Dry-run：LastSucceeded 为空", cleaner.LastSucceeded.Count == 0);

var real = await cleaner.ExecuteAsync(serverPlan.Deletable, dryRun: false);
Check("真实删除：命中 series 接口",
    real == 1 && mock.Deletes.OrderBy(x => x).SequenceEqual(["series:c1"]),
    string.Join(",", mock.Deletes));
Check("真实删除：保留库系列一次都没被删",
    mock.Deletes.All(d => !d.Contains(":k")));

// 部分失败：book:cb2 返回 403，其余照删，LastSucceeded 不含失败项
mock.Deletes.Clear();
mock.DeleteStatus["book:cb2"] = 403;
var bookPlan = await cleaner.ScanAsync(keepLib, cleanLib, new DedupeOptions { Mode = DedupeMode.PerBook });
var deletedBooks = await cleaner.ExecuteAsync(bookPlan.Deletable, dryRun: false);
Check("逐册部分失败：5/6 成功、不中断",
    deletedBooks == 5 && mock.Deletes.Count == 6,
    $"done={deletedBooks} req={mock.Deletes.Count}");
Check("逐册部分失败：LastSucceeded 不含 cb2，其余 5 册在",
    cleaner.LastSucceeded.Count == 5 && cleaner.LastSucceeded.All(c => c.DeleteId != "cb2"));
Check("逐册：全部走 books 接口", mock.Deletes.All(d => d.StartsWith("book:")));

// 404 幂等：已被删掉的系列再删一次应视为成功，不记失败
mock.Deletes.Clear();
mock.DeleteStatus.Clear();
mock.DeleteStatus["series:c1"] = 404;
var idempotent = await cleaner.ExecuteAsync(serverPlan.Deletable, dryRun: false);
Check("404 视为已删除：仍算成功", idempotent == 1 && cleaner.LastSucceeded.Count == 1,
    $"done={idempotent}");

try
{
    await cleaner.ScanAsync(keepLib, keepLib, new DedupeOptions());
    Check("同库被拒（防止把唯一一份删掉）", false);
}
catch (InvalidOperationException)
{
    Check("同库被拒（防止把唯一一份删掉）", true);
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("=== 全部通过 ===");
    return 0;
}
Console.WriteLine($"=== 失败 {failures.Count} 项 ===");
foreach (var f in failures) Console.WriteLine(" - " + f);
return 1;

static SeriesDto Series(string id, string title, string libraryId, int booksCount) => new()
{
    Id = id,
    Name = title,
    LibraryId = libraryId,
    Url = $"/data/{libraryId}/{title}",
    BooksCount = booksCount,
    Metadata = new SeriesMetadataDto { Title = title },
};

static BookDto Book(string id, string seriesId, string seriesTitle, long size, int pages,
    string? hash = null, bool deleted = false) => new()
{
    Id = id,
    Name = id,
    SeriesId = seriesId,
    SeriesTitle = seriesTitle,
    // 测试数据约定：k* 属于保留库，c* 属于清理库
    LibraryId = seriesId.StartsWith("k", StringComparison.Ordinal) ? "keep" : "clean",
    SizeBytes = size,
    FileHash = hash,
    Deleted = deleted,
    Media = new MediaDto { PagesCount = pages },
};

sealed class ConsLog : IAppLogger
{
    public void Info(string message) => Console.WriteLine("    [INFO] " + message);
    public void Warn(string message) => Console.WriteLine("    [WARN] " + message);
    public void Error(string message) => Console.WriteLine("    [ERR ] " + message);
    public void Progress(int current, int total, string message) { }
}
