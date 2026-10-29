using KomgaScraper.Core;
using KomgaScraper.Core.Komga;

// 重复清理（跨库同尺寸去重）自测：
//  1) 规划逻辑：整部/逐册、页数与哈希校验、部分重复不可勾选
//  2) 端到端：本地假 Komga（自建 TCP HTTP 服务）跑扫描 + Dry-run + 真实删除，
//     校验删除请求打到哪个库、哪个接口。
// 退出码 0 = 全过。

var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail == null ? "" : "  — " + detail)}");
    if (!ok) failures.Add(name);
}

// ===================== 1. 规划逻辑（纯内存） =====================

var keepSeries = new List<SeriesDto>
{
    Series("k1", "碧蓝之海", "keep"),
    Series("k2", "别的书", "keep"),
    Series("k3", "页数陷阱", "keep"),
    Series("k4", "哈希陷阱", "keep"),
};
var keepBooks = new List<BookDto>
{
    Book("kb1", "k1", "碧蓝之海", 1000, 10),
    Book("kb2", "k1", "碧蓝之海", 2000, 20),
    Book("kb3", "k1", "碧蓝之海", 3000, 30),
    Book("kb4", "k2", "别的书", 9000, 50),
    Book("kb5", "k3", "页数陷阱", 4000, 15),
    Book("kb6", "k4", "哈希陷阱", 6000, 6, "aaa"),
};

var cleanSeries = new List<SeriesDto>
{
    Series("c1", "碧蓝之海", "clean"),          // 整部重复
    Series("c2", "半重复", "clean"),            // 只重复一册
    Series("c3", "完全不同", "clean"),          // 大小不撞
    Series("c4", "大小相同页数不同", "clean"),
    Series("c5", "大小相同哈希不同", "clean"),
};
var cleanBooks = new List<BookDto>
{
    Book("cb1", "c1", "碧蓝之海", 1000, 10),
    Book("cb2", "c1", "碧蓝之海", 2000, 20),
    Book("cb3", "c1", "碧蓝之海", 3000, 30),
    Book("cb4", "c2", "半重复", 1000, 10),
    Book("cb5", "c2", "半重复", 7777, 7),
    Book("cb6", "c3", "完全不同", 5555, 5),
    Book("cb7", "c4", "大小相同页数不同", 4000, 5),
    Book("cb8", "c5", "大小相同哈希不同", 6000, 6, "bbb"),
};

var whole = DedupePlanner.Build(keepSeries, keepBooks, cleanSeries, cleanBooks, new DedupeOptions());
Check("整部模式：c1 判为整部系列（3/3 册）",
    whole.Candidates.Any(c => c.DeleteId == "c1" && c.Kind == DedupeKind.Series
                              && c.BookCount == 3 && c.MatchedBooks == 3));
Check("整部模式：c2 判为部分重复且不可勾选",
    whole.Candidates.Any(c => c.DeleteId == "c2" && c.Kind == DedupeKind.PartialSeries && !c.Selectable));
Check("整部模式：页数不同 → 不匹配（c4 不出现）",
    whole.Candidates.All(c => c.DeleteId != "c4"));
Check("整部模式：页数一致 → c5 可删（哈希未开启时不管哈希）",
    whole.Candidates.Any(c => c.DeleteId == "c5" && c.Selectable));
Check("整部模式：保留库对应标题带到行上",
    whole.Candidates.First(c => c.DeleteId == "c1").KeepTitles.Contains("碧蓝之海"));
Check("整部模式：可删 2 部、另有 1 部部分重复",
    whole.Deletable.Count() == 2 && whole.Candidates.Count(c => c.Kind == DedupeKind.PartialSeries) == 1,
    whole.Summary);

var perBook = DedupePlanner.Build(keepSeries, keepBooks, cleanSeries, cleanBooks,
    new DedupeOptions { Mode = DedupeMode.PerBook });
Check("逐册模式：5 册可删", perBook.Deletable.Count() == 5, $"实际 {perBook.Deletable.Count()}");
Check("逐册模式：行全是单册类型", perBook.Candidates.All(c => c.Kind == DedupeKind.Book));
Check("逐册模式：待删字节合计 13000", perBook.Candidates.Sum(c => c.Bytes) == 13000,
    perBook.Candidates.Sum(c => c.Bytes).ToString());

var sizeOnly = DedupePlanner.Build(keepSeries, keepBooks, cleanSeries, cleanBooks,
    new DedupeOptions { Match = DedupeMatch.SizeOnly });
Check("仅大小模式：页数不同的 c4 也会命中", sizeOnly.Candidates.Any(c => c.DeleteId == "c4"));

var hashMode = DedupePlanner.Build(keepSeries, keepBooks, cleanSeries, cleanBooks,
    new DedupeOptions { Match = DedupeMatch.SizePagesAndHash });
Check("哈希模式：哈希不同的 c5 被拒", hashMode.Candidates.All(c => c.DeleteId != "c5"));
Check("哈希模式：c1/c2 仍正常",
    hashMode.Candidates.Any(c => c.DeleteId == "c1") && hashMode.Candidates.Any(c => c.DeleteId == "c2"));

Check("字节格式化", DuplicateCleaner.FormatBytes(1536) == "1.5 KB" && DuplicateCleaner.FormatBytes(0) == "0 B",
    DuplicateCleaner.FormatBytes(1536));

// ===================== 2. 端到端（本地假 Komga） =====================

using var mock = new MockKomga();
var keepLib = new LibraryDto { Id = "keep", Name = "保留库", Root = "/data/keep" };
var cleanLib = new LibraryDto { Id = "clean", Name = "清理库", Root = "/data/clean" };
mock.Libraries.Add(keepLib);
mock.Libraries.Add(cleanLib);
foreach (var s in keepSeries.Concat(cleanSeries)) mock.Series.Add(s);
foreach (var b in keepBooks.Concat(cleanBooks)) mock.Books.Add(b);

using var komga = new KomgaClient(mock.BaseUrl, "test-key", null, null, null, NullLogger.Instance);
var cleaner = new DuplicateCleaner(komga, new ConsLog());

var serverPlan = await cleaner.ScanAsync(keepLib, cleanLib, new DedupeOptions());
Check("服务端扫描：可删 2 部", serverPlan.Deletable.Count() == 2);
Check("服务端扫描：两库册数与本地一致（6 / 8）",
    serverPlan.KeepBookCount == 6 && serverPlan.CleanBookCount == 8,
    $"{serverPlan.KeepBookCount} / {serverPlan.CleanBookCount}");

var dry = await cleaner.ExecuteAsync(serverPlan.Deletable, dryRun: true);
Check("Dry-run：处理 2 项", dry == 2);
Check("Dry-run：零删除请求", mock.Deletes.IsEmpty, $"实际 {mock.Deletes.Count} 个");

var real = await cleaner.ExecuteAsync(serverPlan.Deletable, dryRun: false);
Check("真实删除：2 项", real == 2);
Check("真实删除：命中清理库的两部系列（series 接口）",
    mock.Deletes.OrderBy(x => x).SequenceEqual(["series:c1", "series:c5"]),
    string.Join(",", mock.Deletes));
Check("真实删除：保留库系列一次都没被删",
    mock.Deletes.All(d => !d.Contains(":k1") && !d.Contains(":k2") && !d.Contains(":k3") && !d.Contains(":k4")));

mock.Deletes.Clear();
var bookPlan = await cleaner.ScanAsync(keepLib, cleanLib, new DedupeOptions { Mode = DedupeMode.PerBook });
var deletedBooks = await cleaner.ExecuteAsync(bookPlan.Deletable, dryRun: false);
Check("逐册模式：删除 5 册", deletedBooks == 5, deletedBooks.ToString());
Check("逐册模式：全部走 books 接口", mock.Deletes.Count == 5 && mock.Deletes.All(d => d.StartsWith("book:")));
Check("逐册模式：删的是 cb1/cb2/cb3/cb4/cb8",
    mock.Deletes.OrderBy(x => x).SequenceEqual(["book:cb1", "book:cb2", "book:cb3", "book:cb4", "book:cb8"]),
    string.Join(",", mock.Deletes.OrderBy(x => x)));

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

static SeriesDto Series(string id, string title, string libraryId) => new()
{
    Id = id,
    Name = title,
    LibraryId = libraryId,
    Url = $"/data/{libraryId}/{title}",
    Metadata = new SeriesMetadataDto { Title = title },
};

static BookDto Book(string id, string seriesId, string seriesTitle, long size, int pages, string? hash = null) => new()
{
    Id = id,
    Name = id,
    SeriesId = seriesId,
    SeriesTitle = seriesTitle,
    // 测试数据约定：k* 属于保留库，c* 属于清理库
    LibraryId = seriesId.StartsWith("k", StringComparison.Ordinal) ? "keep" : "clean",
    SizeBytes = size,
    FileHash = hash,
    Media = new MediaDto { PagesCount = pages },
};

sealed class ConsLog : IAppLogger
{
    public void Info(string message) => Console.WriteLine("    [INFO] " + message);
    public void Warn(string message) => Console.WriteLine("    [WARN] " + message);
    public void Error(string message) => Console.WriteLine("    [ERR ] " + message);
    public void Progress(int current, int total, string message) { }
}
