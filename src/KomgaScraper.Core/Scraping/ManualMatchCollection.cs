using KomgaScraper.Core.Komga;

namespace KomgaScraper.Core.Scraping;

/// <summary>
/// "手动匹配" 收藏夹管理（对齐脚本 ensureManualMatchCollectionExists /
/// addSeriesToManualMatchCollectionImmediately / removeSeriesFromManualMatchCollectionIfExists）。
/// 并行批量刮削时多个系列会同时增删成员，内部用信号量把操作串行化，成员快照始终一致。
/// </summary>
public sealed class ManualMatchCollection(KomgaClient komga, IAppLogger log)
{
    public const string CollectionName = "手动匹配";

    private readonly SemaphoreSlim _sync = new(1, 1);
    private string? _collectionId;
    private List<string> _existingSeriesIds = [];

    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> work)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { _sync.Release(); }
    }

    /// <summary>确保收藏夹存在并缓存其成员；initialSeriesId 用于首次创建。</summary>
    public Task<bool> EnsureExistsAsync(string? initialSeriesId = null, CancellationToken ct = default) =>
        ExclusiveAsync(() => EnsureExistsCoreAsync(initialSeriesId, ct));

    private async Task<bool> EnsureExistsCoreAsync(string? initialSeriesId, CancellationToken ct)
    {
        if (_collectionId != null) return true;
        try
        {
            var collections = await komga.GetCollectionsAsync(ct);
            var existing = collections.FirstOrDefault(c => c.Name == CollectionName);
            if (existing != null)
            {
                _collectionId = existing.Id;
                _existingSeriesIds = existing.SeriesIds ?? [];
                log.Info($"[收藏夹] 已找到 \"{CollectionName}\" (ID:{_collectionId})，包含 {_existingSeriesIds.Count} 个系列");
                return true;
            }

            if (initialSeriesId == null)
            {
                log.Info($"[收藏夹] \"{CollectionName}\" 不存在，将等失败系列出现时创建");
                return false;
            }

            var created = await komga.CreateCollectionAsync(CollectionName, [initialSeriesId], ordered: false, ct);
            _collectionId = created.Id;
            _existingSeriesIds = created.SeriesIds ?? [initialSeriesId];
            log.Info($"[收藏夹] 已使用系列 {initialSeriesId} 创建 \"{CollectionName}\"");
            return true;
        }
        catch (Exception e)
        {
            log.Error($"[收藏夹] 操作 \"{CollectionName}\" 失败: {e.Message}");
            _collectionId = null;
            _existingSeriesIds = [];
            return false;
        }
    }

    public Task<bool> AddSeriesAsync(string seriesId, string seriesName, CancellationToken ct = default) =>
        ExclusiveAsync(() => AddSeriesCoreAsync(seriesId, seriesName, ct));

    private async Task<bool> AddSeriesCoreAsync(string seriesId, string seriesName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(seriesId)) return false;
        var ready = _collectionId != null || await EnsureExistsCoreAsync(seriesId, ct);
        if (!ready || _collectionId == null)
        {
            log.Error($"[收藏夹] \"{CollectionName}\" 未就绪，无法添加《{seriesName}》");
            return false;
        }
        if (_existingSeriesIds.Contains(seriesId)) return true;
        var newList = new List<string>(_existingSeriesIds) { seriesId };
        try
        {
            await komga.UpdateCollectionSeriesAsync(_collectionId, newList, ct);
            _existingSeriesIds = newList;
            log.Info($"[收藏夹] 《{seriesName}》已添加至 \"{CollectionName}\"");
            return true;
        }
        catch (Exception e)
        {
            log.Error($"[收藏夹] 添加《{seriesName}》失败: {e.Message}");
            return false;
        }
    }

    public Task<bool> RemoveSeriesAsync(string seriesId, string seriesName, CancellationToken ct = default) =>
        ExclusiveAsync(() => RemoveSeriesCoreAsync(seriesId, seriesName, ct));

    private async Task<bool> RemoveSeriesCoreAsync(string seriesId, string seriesName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(seriesId) || _collectionId == null) return false;
        if (!_existingSeriesIds.Contains(seriesId)) return false;
        var newList = _existingSeriesIds.Where(id => id != seriesId).ToList();
        try
        {
            await komga.UpdateCollectionSeriesAsync(_collectionId, newList, ct);
            _existingSeriesIds = newList;
            log.Info($"[收藏夹] 《{seriesName}》已从 \"{CollectionName}\" 移除（匹配成功）");
            return true;
        }
        catch (Exception e)
        {
            log.Error($"[收藏夹] 从收藏夹移除《{seriesName}》失败: {e.Message}");
            return false;
        }
    }
}
