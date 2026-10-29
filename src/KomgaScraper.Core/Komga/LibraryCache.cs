using System.Text.Json;
using System.Text.Json.Serialization;
using KomgaScraper.Core.Komga;

namespace KomgaScraper.Core.Komga;

/// <summary>
/// Komga 库数据本地缓存（内存 + 磁盘双层）。
/// 打开软件即出库数据，不再每次重新连接拉全库；后台按间隔自动对齐 Komga 增量。
/// </summary>
/// <remarks>
/// - 持久层：<c>DataDir\library-cache.json</c>（原子写入：先 .tmp 再改名，损坏即重建）；
/// - 连接指纹（url|key|user|pass）一起持久化：Komga 连接信息变更 → 缓存作废，绝不串库；
/// - 读路径零 Komga 请求；元数据/封面写入后调用 <see cref="UpdateSeries"/> 单系列回源。
/// </remarks>
public sealed class LibraryCache
{
    private readonly object _gate = new();
    private readonly string _filePath;

    private List<LibraryDto> _libraries = [];
    private readonly Dictionary<string, SeriesDto> _series = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _byLib = new(StringComparer.Ordinal);
    private DateTime _lastSync;
    private string _connKey = "";

    public LibraryCache(string filePath)
    {
        _filePath = filePath;
        LoadFromDisk();
    }

    // ---------- 快照（零网络） ----------

    public List<LibraryDto> Libraries { get { lock (_gate) return [.. _libraries]; } }

    public DateTime LastSync { get { lock (_gate) return _lastSync; } }

    public bool Ready { get { lock (_gate) return _lastSync != default && _libraries.Count > 0; } }

    public int SeriesTotal { get { lock (_gate) return _series.Count; } }

    /// <summary>缓存是否命中（该库有系列数据）。</summary>
    public bool HasLibrary(string libraryId)
    {
        lock (_gate) return _byLib.TryGetValue(libraryId, out var ids) && ids.Count > 0;
    }

    public SeriesDto? TryGetSeries(string seriesId)
    {
        lock (_gate) return _series.TryGetValue(seriesId, out var s) ? s : null;
    }

    /// <summary>库内系列（name 升序，与 Komga 默认序对齐）。未命中返回 null。</summary>
    public List<SeriesDto>? SeriesInLibrary(string libraryId)
    {
        lock (_gate)
        {
            if (!_byLib.TryGetValue(libraryId, out var ids)) return null;
            var list = new List<SeriesDto>(ids.Count);
            foreach (var id in ids)
                if (_series.TryGetValue(id, out var s)) list.Add(s);
            return list;
        }
    }

    // ---------- 连接指纹 ----------

    public static string ConnKeyOf(AppSettings s) =>
        $"{s.KomgaUrl.TrimEnd('/')}|{s.KomgaApiKey}|{s.KomgaUsername}|{s.KomgaPassword}";

    /// <summary>连接信息与缓存不一致（用户改了地址/密钥）→ 清空，避免串库。</summary>
    public bool EnsureConnection(AppSettings settings)
    {
        var key = ConnKeyOf(settings);
        lock (_gate)
        {
            if (_connKey.Length > 0 && _connKey == key) return false;
            if (_libraries.Count == 0 && _series.Count == 0)
            {
                _connKey = key;
                return false;
            }
        }
        Reset(key);
        return true;
    }

    public void Reset(string connKey)
    {
        lock (_gate)
        {
            _libraries = [];
            _series.Clear();
            _byLib.Clear();
            _lastSync = default;
            _connKey = connKey;
        }
        TryDeleteFile();
    }

    // ---------- 写入 ----------

    /// <summary>全量对齐：库列表 + 每库系列拉取、差量合并、落盘。返回 (系列总数, 新增/变更数, 移除数)。</summary>
    public async Task<(int Total, int Changed, int Removed)> RefreshAllAsync(
        KomgaClient client, IAppLogger? log, CancellationToken ct)
    {
        var libs = await client.GetLibrariesAsync(ct).ConfigureAwait(false);
        var newSeries = new Dictionary<string, SeriesDto>(StringComparer.Ordinal);
        var newByLib = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var lib in libs)
        {
            var list = await client.ListSeriesInLibraryAsync(lib.Id, ct).ConfigureAwait(false);
            var ids = new List<string>(list.Count);
            foreach (var s in list)
            {
                s.LibraryId = lib.Id;
                newSeries[s.Id] = s;
                ids.Add(s.Id);
            }
            newByLib[lib.Id] = ids;
        }

        int changed = 0, removed = 0;
        lock (_gate)
        {
            var oldIds = new HashSet<string>(_series.Keys, StringComparer.Ordinal);
            foreach (var (id, ns) in newSeries)
            {
                oldIds.Remove(id);
                if (!_series.TryGetValue(id, out var old) || SeriesJsonDiffers(old, ns))
                    changed++;
            }
            removed = oldIds.Count;
            _series.Clear();
            foreach (var (id, ns) in newSeries) _series[id] = ns;
            _libraries = libs;
            _byLib.Clear();
            foreach (var (libId, ids) in newByLib)
            {
                SortIds(ids);
                _byLib[libId] = ids;
            }
            _lastSync = DateTime.Now;
        }
        SaveToDisk();
        log?.Info($"库缓存已对齐：{libs.Count} 库 {newSeries.Count} 系列（新增/变更 {changed}，移除 {removed}）");
        return (newSeries.Count, changed, removed);
    }

    /// <summary>单系列回源刷新（元数据/封面写入后调用；失败静默保留旧值）。</summary>
    public async Task RefreshSeriesAsync(KomgaClient client, string seriesId, CancellationToken ct)
    {
        try
        {
            var s = await client.GetSeriesAsync(seriesId, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _series[seriesId] = s;
                var libId = s.LibraryId;
                if (libId.Length > 0 && _byLib.TryGetValue(libId, out var ids) && !ids.Contains(seriesId))
                {
                    ids.Add(seriesId);
                    SortIds(ids);
                }
            }
            SaveToDisk();
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 拉不到保持旧值（可能只是暂时性网络错误） */ }
    }

    /// <summary>单系列就地更新（UI 已从服务器拿到新 DTO 时回填缓存）。</summary>
    public void UpdateSeries(SeriesDto s)
    {
        if (string.IsNullOrEmpty(s.Id)) return;
        lock (_gate) _series[s.Id] = s;
        SaveToDisk();
    }

    /// <summary>
    /// 立即移除已确认不存在的系列（重复清理删掉后调用）。Komga 的删除是异步的，
    /// 马上去 RefreshLibraryAsync 反而会把还没删完的系列又拉回缓存，所以先本地剔除。
    /// </summary>
    public void RemoveSeries(IReadOnlyCollection<string> seriesIds)
    {
        if (seriesIds.Count == 0) return;
        var removed = 0;
        lock (_gate)
        {
            foreach (var id in seriesIds)
            {
                if (!_series.Remove(id)) continue;
                removed++;
                foreach (var ids in _byLib.Values) ids.Remove(id);
            }
        }
        if (removed > 0) SaveToDisk();
    }

    /// <summary>仅回填库列表（连接测试成功后；不触碰已缓存的系列数据）。</summary>
    public void SetLibraries(List<LibraryDto> libs, string connKey)
    {
        lock (_gate)
        {
            if (_connKey.Length > 0 && _connKey != connKey)
            {
                _libraries = [];
                _series.Clear();
                _byLib.Clear();
                _lastSync = default;
            }
            _connKey = connKey;
            _libraries = libs;
        }
        SaveToDisk();
    }

    /// <summary>整库快照替换（UI 从服务器拉了全量系列后回填缓存）。</summary>
    public void ReplaceLibrary(string libraryId, List<SeriesDto> series, string connKey)
    {
        lock (_gate)
        {
            if (_connKey.Length > 0 && _connKey != connKey)
            {
                _libraries = [];
                _series.Clear();
                _byLib.Clear();
                _lastSync = default;
            }
            _connKey = connKey;
            // 移除该库旧系列
            if (_byLib.TryGetValue(libraryId, out var oldIds))
                foreach (var id in oldIds) _series.Remove(id);
            var ids = new List<string>(series.Count);
            foreach (var s in series)
            {
                s.LibraryId = libraryId;
                _series[s.Id] = s;
                ids.Add(s.Id);
            }
            SortIds(ids);
            _byLib[libraryId] = ids;
            _lastSync = DateTime.Now;
        }
        SaveToDisk();
    }

    /// <summary>静默对齐单库：拉全量、差量合并、落盘。返回 (系列数, 新增/变更数)。</summary>
    public async Task<(int Total, int Changed)> RefreshLibraryAsync(
        KomgaClient client, string libraryId, IAppLogger? log, CancellationToken ct)
    {
        var list = await client.ListSeriesInLibraryAsync(libraryId, ct).ConfigureAwait(false);
        int changed = 0;
        lock (_gate)
        {
            if (_byLib.TryGetValue(libraryId, out var oldIds))
            {
                var keep = new HashSet<string>(list.Select(s => s.Id), StringComparer.Ordinal);
                foreach (var id in oldIds.Where(id => !keep.Contains(id)).ToList())
                    _series.Remove(id);
            }
            var ids = new List<string>(list.Count);
            foreach (var s in list)
            {
                s.LibraryId = libraryId;
                if (!_series.TryGetValue(s.Id, out var old) || SeriesJsonDiffers(old, s)) changed++;
                _series[s.Id] = s;
                ids.Add(s.Id);
            }
            SortIds(ids);
            _byLib[libraryId] = ids;
            _lastSync = DateTime.Now;
        }
        SaveToDisk();
        log?.Info($"库缓存对齐（单库 {libraryId}）：{list.Count} 系列，新增/变更 {changed}");
        return (list.Count, changed);
    }

    // ---------- 持久化 ----------

    /// <summary>按显示名（元数据标题优先，回退文件夹名）升序。调用方持有 _gate。</summary>
    private void SortIds(List<string> ids)
    {
        ids.Sort((a, b) =>
        {
            _series.TryGetValue(a, out var sa); _series.TryGetValue(b, out var sb);
            return string.Compare(SortName(sa), SortName(sb), StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string SortName(SeriesDto? s)
    {
        if (s is null) return "";
        var t = SeriesRowTitle(s);
        return t.Length > 0 ? t : s.Name;
    }

    private static string SeriesRowTitle(SeriesDto s)
    {
        var t = s.Metadata?.Title;
        return string.IsNullOrWhiteSpace(t) ? s.Name : t;
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_filePath));
            var root = doc.RootElement;
            _connKey = root.TryGetProperty("connKey", out var ck) && ck.ValueKind == JsonValueKind.String
                ? ck.GetString() ?? "" : "";
            if (root.TryGetProperty("lastSync", out var ls) && ls.ValueKind == JsonValueKind.String)
                _lastSync = DateTime.TryParse(ls.GetString(), out var t) ? t : default;
            if (root.TryGetProperty("libraries", out var libsEl) && libsEl.ValueKind == JsonValueKind.Array)
            {
                _libraries = libsEl.Deserialize<List<LibraryDto>>(JsonOpts) ?? [];
            }
            if (root.TryGetProperty("series", out var serEl) && serEl.ValueKind == JsonValueKind.Array)
            {
                var list = serEl.Deserialize<List<SeriesDto>>(JsonOpts) ?? [];
                foreach (var s in list)
                {
                    if (string.IsNullOrEmpty(s.Id)) continue;
                    _series[s.Id] = s;
                    var lib = s.LibraryId;
                    if (lib.Length == 0) lib = "";
                    if (!_byLib.TryGetValue(lib, out var ids)) _byLib[lib] = ids = [];
                    ids.Add(s.Id);
                }
                foreach (var ids in _byLib.Values) SortIds(ids);
            }
        }
        catch
        {
            // 损坏就重建：缓存丢了无非重新拉一次
            _libraries = [];
            _series.Clear();
            _byLib.Clear();
            _lastSync = default;
        }
    }

    private void SaveToDisk()
    {
        try
        {
            List<SeriesDto> snapshot;
            List<LibraryDto> libs;
            string key; DateTime sync;
            lock (_gate)
            {
                snapshot = [.. _series.Values];
                libs = [.. _libraries];
                key = _connKey;
                sync = _lastSync;
            }
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var payload = new CacheFile
            {
                ConnKey = key,
                LastSync = sync.ToString("O"),
                Libraries = libs,
                Series = snapshot,
            };
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOpts));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // 落盘失败只影响下次启动的恢复速度，不影响本次使用
        }
    }

    private void TryDeleteFile()
    {
        try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
    }

    private static bool SeriesJsonDiffers(SeriesDto a, SeriesDto b) =>
        !string.Equals(
            JsonSerializer.Serialize(a, JsonOpts),
            JsonSerializer.Serialize(b, JsonOpts),
            StringComparison.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class CacheFile
    {
        [JsonPropertyName("connKey")] public string ConnKey { get; set; } = "";
        [JsonPropertyName("lastSync")] public string LastSync { get; set; } = "";
        [JsonPropertyName("libraries")] public List<LibraryDto> Libraries { get; set; } = [];
        [JsonPropertyName("series")] public List<SeriesDto> Series { get; set; } = [];
    }
}
