using System.Text.Json;
using System.Text.Json.Serialization;

namespace KomgaScraper.Core.Scraping;

/// <summary>单个系列的刮削状态（series 级封面/简介是否已刮上）。</summary>
public sealed class ScrapeStateEntry
{
    [JsonPropertyName("cover")] public bool Cover { get; set; }
    [JsonPropertyName("coverAt")] public string? CoverAt { get; set; }
    [JsonPropertyName("summary")] public bool Summary { get; set; }
    [JsonPropertyName("summaryAt")] public string? SummaryAt { get; set; }
}

/// <summary>
/// 刮削状态库（data/scrape-state.json）：按 seriesId 记录「封面 / 简介」是否已刮。
/// Komga 只能逐系列 GET /series/{id}/thumbnails 判断封面是否用户上传版（上千系列逐个查太慢），
/// 所以本工具刮成功的直接记入本地；其它途径刮的封面由 UI 的后台封面检查补录并缓存。
/// </summary>
public sealed class ScrapeStateStore
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private Dictionary<string, ScrapeStateEntry> _entries;
    private System.Timers.Timer? _saveTimer;

    public ScrapeStateStore(string filePath)
    {
        _filePath = filePath;
        _entries = Load(filePath);
    }

    private static Dictionary<string, ScrapeStateEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("series", out var seriesEl)) return [];
            var result = new Dictionary<string, ScrapeStateEntry>(StringComparer.Ordinal);
            foreach (var p in seriesEl.EnumerateObject())
            {
                var e = new ScrapeStateEntry();
                if (p.Value.ValueKind == JsonValueKind.Object)
                {
                    if (p.Value.TryGetProperty("cover", out var c) && c.ValueKind == JsonValueKind.True) e.Cover = true;
                    if (p.Value.TryGetProperty("coverAt", out var ca) && ca.ValueKind == JsonValueKind.String) e.CoverAt = ca.GetString();
                    if (p.Value.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.True) e.Summary = true;
                    if (p.Value.TryGetProperty("summaryAt", out var sa) && sa.ValueKind == JsonValueKind.String) e.SummaryAt = sa.GetString();
                }
                result[p.Name] = e;
            }
            return result;
        }
        catch
        {
            return [];   // 损坏就重建，状态只是缓存，丢了无非重新检查一遍
        }
    }

    /// <summary>封面状态：true=已刮 / false=确认没刮 / null=未知（还没检查过）。</summary>
    public bool? GetCover(string seriesId)
    {
        lock (_gate)
            return _entries.TryGetValue(seriesId, out var e) ? e.Cover : null;
    }

    public bool IsSummaryKnown(string seriesId)
    {
        lock (_gate)
            return _entries.TryGetValue(seriesId, out var e) && e.Summary;
    }

    /// <summary>记封面状态（本工具上传成功 / 后台从服务器检查到）。</summary>
    public void SetCover(string seriesId, bool cover)
    {
        lock (_gate)
        {
            var e = _entries.TryGetValue(seriesId, out var old) ? old : new ScrapeStateEntry();
            e.Cover = cover;
            e.CoverAt = cover ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : e.CoverAt;
            _entries[seriesId] = e;
        }
        ScheduleSave();
    }

    public void SetSummary(string seriesId, bool summary)
    {
        lock (_gate)
        {
            var e = _entries.TryGetValue(seriesId, out var old) ? old : new ScrapeStateEntry();
            e.Summary = summary;
            e.SummaryAt = summary ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : e.SummaryAt;
            _entries[seriesId] = e;
        }
        ScheduleSave();
    }

    /// <summary>刮削流程上报：该系列的系列封面已成功上传。</summary>
    public void MarkCoverUploaded(string seriesId) => SetCover(seriesId, true);

    /// <summary>刮削流程上报：该系列的系列简介最终有值（本来就有的也算已刮）。</summary>
    public void MarkSummaryPresent(string seriesId) => SetSummary(seriesId, true);

    /// <summary>还未检查过封面状态的系列（后台补查只查这些：只要记过一条（哪怕 false）就不再重查）。</summary>
    public List<string> UnknownCoverIds(IEnumerable<string> seriesIds)
    {
        lock (_gate)
            return seriesIds.Where(id => !_entries.ContainsKey(id)).ToList();
    }

    private void ScheduleSave()
    {
        lock (_gate)
        {
            if (_saveTimer != null) return;
            _saveTimer = new System.Timers.Timer(800) { AutoReset = false };
            _saveTimer.Elapsed += (_, _) =>
            {
                lock (_gate)
                {
                    _saveTimer?.Dispose();
                    _saveTimer = null;
                }
                SaveNow();
            };
            _saveTimer.Start();
        }
    }

    public void SaveNow()
    {
        Dictionary<string, ScrapeStateEntry> snapshot;
        lock (_gate) snapshot = new Dictionary<string, ScrapeStateEntry>(_entries, StringComparer.Ordinal);
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var obj = new Dictionary<string, object> { ["series"] = snapshot };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (Exception e)
        {
            // 目录只读等情况：静默放弃（状态只是缓存）——但测试环境要让问题现形
            Console.Error.WriteLine($"[scrape-state] SaveNow failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
