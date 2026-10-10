using System.Security.Cryptography;
using System.Text;

namespace KomgaScraper.Core.Scraping;

/// <summary>
/// 封面磁盘暂存缓存（staging）：刮削里最慢、最脆的材料（外网封面图）先落本地，
/// <b>材料齐了再写 Komga</b>——收集阶段被取消/中断时，Komga 一个字节都没写过；
/// 重跑时命中缓存，秒级补完，不再从头慢下。
/// </summary>
/// <remarks>
/// - 目录：<c>DataDir\staging\covers\&lt;sha256(url)&gt;.jpg</c>，跨任务、跨重启持久；
/// - 同一 URL 只下载一次（成功才落盘；取消/失败绝不写半成品——先写 .tmp 再原子改名）；
/// - 超过 300MB 时按最旧优先清理到 75%，用户无感。
/// </remarks>
public static class CoverCache
{
    private static readonly object Gate = new();
    private static string? _dir;
    private const long MaxBytes = 300L * 1024 * 1024;

    public static string Dir
    {
        get
        {
            if (_dir != null) return _dir;
            lock (Gate)
            {
                if (_dir == null)
                {
                    _dir = Path.Combine(AppSettings.DataDir, "staging", "covers");
                    Directory.CreateDirectory(_dir);
                }
                return _dir;
            }
        }
    }

    private static string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(Dir, hash + ".jpg");
    }

    /// <summary>命中缓存直接返回；未命中走 download 拉取（成功才落盘）。</summary>
    public static async Task<byte[]> GetOrDownloadAsync(
        string url, Func<string, CancellationToken, Task<byte[]>> download, CancellationToken ct)
    {
        var path = PathFor(url);
        if (File.Exists(path))
        {
            try
            {
                var cached = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                if (cached.Length > 0) return cached;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 缓存文件损坏/被占 → 当作未命中重新下载 */ }
        }

        var data = await download(url, ct).ConfigureAwait(false);
        if (data.Length == 0) return data;

        try
        {
            var tmp = Path.Combine(Dir, $".tmp-{Guid.NewGuid():N}");
            await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);   // 半成品只存在于 .tmp，改名是原子的
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 落盘失败只影响下次缓存命中，不影响本次使用 */ }
        return data;
    }

    /// <summary>
    /// 刮削写入前的材料预热：把该系列会用到的全部封面 URL 拉进缓存。
    /// 任意一个失败只记日志不中断（写入阶段还会按原逻辑现拉现试）；
    /// 取消（OperationCanceledException）正常上抛 → 上层"零写入"语义由这里保证。
    /// </summary>
    public static async Task WarmUpAsync(
        IEnumerable<string> urls, Func<string, CancellationToken, Task<byte[]>> download,
        Action<string>? warn, bool serial, CancellationToken ct)
    {
        var list = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return;
        Trim();

        var ok = 0;
        var fail = 0;
        void Note(bool success)
        {
            if (success) Interlocked.Increment(ref ok);
            else Interlocked.Increment(ref fail);
        }

        if (serial)
        {
            foreach (var url in list)
            {
                ct.ThrowIfCancellationRequested();
                Note(await OneAsync(url, download, warn, ct).ConfigureAwait(false));
            }
        }
        else
        {
            await Parallel.ForEachAsync(list,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (url, token) => Note(await OneAsync(url, download, warn, token).ConfigureAwait(false)))
                .ConfigureAwait(false);
        }

        if (ok + fail > 0)
            warn?.Invoke($"封面材料暂存：{ok} 张就绪" + (fail > 0 ? $"，{fail} 张待写入阶段重试" : ""));
    }

    private static async Task<bool> OneAsync(
        string url, Func<string, CancellationToken, Task<byte[]>> download,
        Action<string>? warn, CancellationToken ct)
    {
        try
        {
            await GetOrDownloadAsync(url, download, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            warn?.Invoke($"封面材料预取失败（不阻塞写入，稍后重试）: {e.Message}");
            return false;
        }
    }

    /// <summary>超过 300MB 时按最旧优先清理到 75%（顺带删掉残留的 .tmp）。</summary>
    private static void Trim()
    {
        try
        {
            var di = new DirectoryInfo(Dir);
            foreach (var tmp in di.GetFiles(".tmp-*")) TryDelete(tmp);
            var files = di.GetFiles("*.jpg");
            long total = files.Sum(f => f.Length);
            if (total <= MaxBytes) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= MaxBytes * 3 / 4) break;
                total -= f.Length;
                TryDelete(f);
            }
        }
        catch { /* 清理失败无所谓，缓存照用 */ }
    }

    private static void TryDelete(FileInfo f)
    {
        try { f.Delete(); } catch { /* 被占用就留给下次 */ }
    }
}
