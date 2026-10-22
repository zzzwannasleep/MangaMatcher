using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace KomgaScraper.Core.Sources;

/// <summary>
/// EhTagTranslation 中文词库（E-Hentai 标签 → 中文）。
///
/// 数据源：https://github.com/EhTagTranslation/Database 的 release 产物 <c>db.text.json.gz</c>
/// （13 个命名空间、约 4.4 万条，每条只有 name/intro/links，本类只取 <c>name</c>）。
///
/// 语义移植自 Go 版 <c>server/internal/ehtags</c>（ehtags.go + fetch.go），下面三条是那边实测踩过的坑：
/// <list type="number">
/// <item><b>半截文件防护</b>：HTTP 200 也可能是错误页、或被中断的半截 gzip。
/// 所以下载时先写 <c>db.text.json.tmp</c>，体检（解压后 &gt; 512 字节 + 首个非空白字符是 <c>{</c>）通过后
/// 才 <see cref="File.Move(string,string,bool)"/> 覆盖正式缓存 —— 否则半截文件会被下一次启动当成有效缓存，
/// 表现为"词库怎么突然全查不到"。</item>
/// <item><b>7 天新鲜期</b>：按缓存文件的最后写入时间判断，而不是比对内容里的 version 字段 ——
/// 内容版本号要下完才知道，而"多久没更新过"看 mtime 不需要任何网络。</item>
/// <item><b>失败不抛</b>：词库只是锦上添花，下载/解析失败一律只记 <see cref="LastError"/> 并回落英文原名，
/// 绝不因为标签翻译不可用而让刮削主流程失败。这里也刻意不做"负缓存"（不写"本次失败"标志）：
/// 下一次 <see cref="EnsureAsync"/> 仍会重试，一次网络抖动不该让整个进程永久失去中文标签。</item>
/// </list>
///
/// 线程安全：词库加载完成后是只读快照，<see cref="Translate"/> / <see cref="Label"/> 可并发调用；
/// <see cref="EnsureAsync"/> 用 <see cref="SemaphoreSlim"/> 保证多处同时调用时只下载一次。
/// </summary>
public static class EhTags
{
    /// <summary>GitHub「最新 release 资产」永久直链（会 302 到带签名的资产地址）。
    /// 首选它是为了不消耗 GitHub API 配额：实测 api.github.com 在共享出口 IP 上会被限流
    /// （"API rate limit exceeded"），而这条直链不消耗配额；Go 版留的 API 兜底本类不实现，
    /// 直链失败时把原因写进 <see cref="LastError"/> 即可。</summary>
    private const string LatestAssetUrl =
        "https://github.com/EhTagTranslation/Database/releases/latest/download/db.text.json.gz";

    /// <summary>缓存目录名（位于 dataDir 下）。</summary>
    private const string DirName = "ehtags";

    /// <summary>解压后的明文缓存文件名。</summary>
    private const string DbFileName = "db.text.json";

    /// <summary>下载/落盘用的 UA（GitHub 对无 UA 的请求可能直接 403）。</summary>
    private const string UserAgent = "KomgaScraper/1.0";

    /// <summary>缓存新鲜期 7 天（照 Go 版 defaultFresh = 168h）。</summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);

    /// <summary>小于 512 字节一定是错误页/半截文件（真实库解压后约 4.2MB）。</summary>
    private const long MinDbBytes = 512;

    /// <summary>缓存文件小于 1KB 视为不可用（照 Go 版 Ensure 的门槛，比 MinDbBytes 更宽）。</summary>
    private const long UsableCacheBytes = 1024;

    /// <summary>解压/读取上限，防 zip bomb 与异常大文件（照 Go 版 maxDBBytes = 64MB）。</summary>
    private const long MaxDbBytes = 64L << 20;

    /// <summary>
    /// 固定命名空间中文名。刻意<b>优先于</b>库里自带的 frontMatters.name：
    /// 上游会改口径（实测库里 reclass 是"重新分类"、rows 是"内容索引"），而集成方/界面要的是稳定标签，
    /// 所以这里定死；库里的名字只在固定表没覆盖时兜底。
    /// </summary>
    private static readonly Dictionary<string, string> FixedLabels = new(StringComparer.Ordinal)
    {
        ["rows"] = "行",
        ["reclass"] = "分类",
        ["language"] = "语言",
        ["parody"] = "原作",
        ["character"] = "角色",
        ["group"] = "社团",
        ["artist"] = "艺术家",
        ["cosplayer"] = "cosplay",
        ["male"] = "男性",
        ["female"] = "女性",
        ["mixed"] = "混合",
        ["other"] = "其他",
        ["temp"] = "临时",
        // Go 版兜底表里还有这两个（当前 release 里也有 location 命名空间）
        ["location"] = "地点",
        ["category"] = "分类",
    };

    /// <summary>配置读写的互斥锁（<see cref="Configure"/> 与后台加载可能来自不同线程）。</summary>
    private static readonly object Gate = new();

    /// <summary>下载闸门：EnsureAsync 可能被多处同时调用，保证只下载一次。</summary>
    private static readonly SemaphoreSlim FetchLock = new(1, 1);

    private static string _dataDir = "data";
    private static ProxyConfig? _proxy;
    private static Action<string>? _log;

    /// <summary>当前词库快照（null = 未就绪）；加载完成后不再改动，故读取用 Volatile 即可。</summary>
    private static Db? _db;

    private static string? _lastError;

    /// <summary>已加载的只读词库。</summary>
    private sealed class Db
    {
        /// <summary>来源文件路径（用于判断"已经装的就是这个文件"）。</summary>
        public string Path = "";

        /// <summary>库自述版本，形如 <c>v7·2026-10-20</c>（version 字段 + 文件最后写入日期）。</summary>
        public string Version = "";

        /// <summary>词条总数（data 里各命名空间条目之和，含没有 name 的条目）。</summary>
        public int Total;

        /// <summary>命名空间(小写) → 标签(小写、空白已压缩) → 中文名。</summary>
        public Dictionary<string, Dictionary<string, string>> Tags = new(StringComparer.Ordinal);

        /// <summary>命名空间(小写) → 中文名（库自带 frontMatters.name，仅作固定表之外的兜底）。</summary>
        public Dictionary<string, string> Labels = new(StringComparer.Ordinal);
    }

    // ================= 公开 API =================

    /// <summary>配置数据目录与代理（启动时调用一次；dataDir 为空则用 ./data）。</summary>
    /// <remarks>
    /// 只记配置、不做任何 IO：真正的加载在 <see cref="EnsureAsync"/> 里。
    /// 这样调用方可以先 Configure（同步、不会卡界面），再决定何时 await 加载。
    /// </remarks>
    public static void Configure(string? dataDir, ProxyConfig? proxy, Action<string>? log)
    {
        var dir = dataDir?.Trim();
        if (string.IsNullOrEmpty(dir)) dir = "data";
        lock (Gate)
        {
            _dataDir = dir;
            _proxy = proxy;
            _log = log;
        }
    }

    /// <summary>
    /// 确保词库就绪：有新鲜缓存就直接加载，否则下载最新 release。
    /// 任何失败（含取消）都只记 <see cref="LastError"/>，<b>不抛</b> —— 调用方可以放心地在启动流程里 await。
    /// </summary>
    public static async Task EnsureAsync(CancellationToken ct = default)
    {
        try
        {
            var path = CachePath();

            // ① 新鲜缓存命中：完全不联网（离线/内网环境走的就是这条）
            if (TryLoadFresh(path)) return;

            await FetchLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // ② 双检：等锁期间别的调用方可能已经下好并装上了（多处同时 EnsureAsync 很常见）
                if (TryLoadFresh(path)) return;

                Log($"正在下载 EH 中文标签库：{DbFileName}.gz（latest release 直链）");
                try
                {
                    await DownloadAsync(path, ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    var reason = Describe(e);
                    // 下载失败但本地还有旧库（> 1KB）：沿用旧库，刮削照常能用中文标签；
                    // 失败原因仍然写进 LastError（集成方可据此提示"当前用的是旧库"）。
                    if (File.Exists(path) && SafeLength(path) > UsableCacheBytes)
                    {
                        var loaded = TryLoadFile(path);
                        SetError($"更新失败，沿用本地缓存：{reason}");
                        Log(loaded
                            ? $"更新 EH 中文标签库失败，沿用本地缓存（{SafeWriteTime(path):yyyy-MM-dd}）：{reason}"
                            : $"更新 EH 中文标签库失败，且本地缓存也无法解析：{reason}");
                    }
                    else
                    {
                        SetError(reason);
                        Log($"EH 中文标签库不可用（标签将回落英文原名）：{reason}");
                    }
                    return;
                }

                if (TryLoadFile(path))
                    Log($"EH 中文标签库已就绪：{path}（{Version}，{Count} 条）");
            }
            finally
            {
                FetchLock.Release();
            }
        }
        catch (Exception e)
        {
            // 兜底（含 OperationCanceledException）：词库是可选项，任何异常都不许冒泡到刮削主流程
            var reason = Describe(e);
            SetError(reason);
            Log($"EH 中文标签库不可用（标签将回落英文原名）：{reason}");
        }
    }

    /// <summary>词库是否可用。</summary>
    public static bool Ready => Volatile.Read(ref _db) != null;

    /// <summary>
    /// 最近一次失败原因（成功加载后清空）。
    /// 注意：<b>非空不代表未就绪</b> —— 更新失败但沿用了旧缓存时，<see cref="Ready"/> 仍是 true。
    /// </summary>
    public static string? LastError
    {
        get { lock (Gate) return _lastError; }
    }

    /// <summary>词库 version 字段（渲染成 <c>v7·2026-10-20</c>：version + 缓存文件最后写入日期）；未就绪返回 null。</summary>
    public static string? Version => Volatile.Read(ref _db)?.Version;

    /// <summary>词条总数（data 里各命名空间条目之和）；未就绪返回 0。</summary>
    public static int Count => Volatile.Read(ref _db)?.Total ?? 0;

    /// <summary>命名空间标签：artist → 艺术家。</summary>
    /// <remarks>固定表 → 库自带 frontMatters.name → 原样返回小写命名空间。</remarks>
    public static string Label(string ns)
    {
        var key = NormalizeKey(ns);
        if (FixedLabels.TryGetValue(key, out var fixedLabel)) return fixedLabel;

        var db = Volatile.Read(ref _db);
        if (db != null && db.Labels.TryGetValue(key, out var fromDb) && fromDb.Length > 0) return fromDb;

        return key;
    }

    /// <summary>标签翻译：("artist","asanagi") → 朝凪；未收录返回原名（不要返回空串）。</summary>
    /// <remarks>
    /// 查表键统一小写 + 空白压缩（EH 标签是小写，库里也是小写，但调用方可能传来 "Asanagi"）；
    /// 返回的是库里的中文名或调用方传入的原始 tag，<b>不做大小写改写</b>。
    /// 词库未就绪时也返回原名，调用方无需判空。
    /// </remarks>
    public static string Translate(string ns, string tag)
    {
        var db = Volatile.Read(ref _db);
        if (db == null || string.IsNullOrEmpty(tag)) return tag;
        if (!db.Tags.TryGetValue(NormalizeKey(ns), out var map)) return tag;

        var key = NormalizeKey(tag);
        if (key.Length == 0) return tag;
        if (map.TryGetValue(key, out var name)) return name;

        // EH 侧偶尔把多词标签写成 "artist cg"，而库里的键是 "artistcg"（Go 版实测确认），去空格再试一次
        if (key.Contains(' '))
        {
            var compact = key.Replace(" ", "");
            if (compact.Length > 0 && map.TryGetValue(compact, out name)) return name;
        }
        return tag;
    }

    /// <summary>从本地 db.text.json 加载（测试/离线用），成功返回 true。</summary>
    /// <remarks>失败时清空当前词库（不留"半截状态"）并把原因写进 <see cref="LastError"/>。</remarks>
    public static bool Load(string path) => TryLoadFile(path);

    // ================= 缓存与下载 =================

    /// <summary>缓存文件路径：{dataDir}\ehtags\db.text.json。</summary>
    private static string CachePath()
    {
        string dir;
        lock (Gate) dir = _dataDir;
        return Path.Combine(dir, DirName, DbFileName);
    }

    /// <summary>缓存存在、够大、且在 7 天新鲜期内（照 Go 版：size &gt; 1KB 且 mtime 在新鲜期内）。</summary>
    private static bool IsFresh(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= UsableCacheBytes) return false;
            return DateTime.UtcNow - fi.LastWriteTimeUtc < FreshFor;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 新鲜缓存 → 加载并返回 true。
    /// 已经装的就是这个文件时直接复用（避免每次 EnsureAsync 都重复解析 4MB）。
    /// 缓存存在但解析失败 → 返回 false，交给下载路径去修（自愈）。
    /// </summary>
    private static bool TryLoadFresh(string path)
    {
        var db = Volatile.Read(ref _db);
        if (db != null && string.Equals(db.Path, path, StringComparison.OrdinalIgnoreCase) && IsFresh(path))
            return true;
        if (!IsFresh(path)) return false;
        if (!TryLoadFile(path)) return false;

        Log($"EH 中文标签库命中本地缓存：{path}（{Version}，{Count} 条）");
        return true;
    }

    /// <summary>下载 gz → 解压落盘 → 体检 → 原子覆盖。</summary>
    private static async Task DownloadAsync(string dest, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        ProxyConfig? proxy;
        lock (Gate) proxy = _proxy;
        using var http = HttpFactory.Create(proxy, TimeSpan.FromSeconds(30), UserAgent, warn: Log);

        using var resp = await http.GetAsync(LatestAssetUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"下载标签库失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var tmp = dest + ".tmp";
        try
        {
            await GunzipToAsync(body, tmp, ct).ConfigureAwait(false);

            // 体检：够大 + 是 JSON 对象。Go 版就是靠这两条挡住「HTTP 200 但其实是错误页/半截文件」，
            // 通不过就删掉临时文件、绝不覆盖正式缓存。
            var length = SafeLength(tmp);
            if (length <= MinDbBytes)
                throw new InvalidOperationException($"标签库内容异常（解压后仅 {length} 字节，疑似错误页或半截文件）");
            var head = await FirstNonWhitespaceByteAsync(tmp, ct).ConfigureAwait(false);
            if (head != (byte)'{')
                throw new InvalidOperationException($"标签库内容异常（解压结果首字符是 {(char)head}，不是 JSON 对象）");

            File.Move(tmp, dest, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* 临时文件删不掉不该让本次下载算失败 */ }
            }
        }
    }

    /// <summary>gzip 流 → 明文落盘。全程流式，不把解压后的 4MB 整包读进内存。</summary>
    private static async Task GunzipToAsync(Stream gzSource, string dest, CancellationToken ct)
    {
        await using var gz = new GZipStream(gzSource, CompressionMode.Decompress);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
            1 << 16, useAsync: true);

        var buf = new byte[1 << 16];
        long total = 0;
        int read;
        // 截断的 gzip 会在读到末尾时抛 InvalidDataException（比"看起来正常"更好：直接算下载失败）
        while ((read = await gz.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDbBytes)
                throw new InvalidOperationException("标签库解压后体积异常（超过 64MB），已中止");
            await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        await dst.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>文件里第一个非空白字节（Go 版是边写边记首字节，这里落盘后回读一小段，等价且更简单）。</summary>
    private static async Task<byte> FirstNonWhitespaceByteAsync(string path, CancellationToken ct)
    {
        var buf = new byte[64];
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            buf.Length, useAsync: true);
        var n = await fs.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
        for (var i = 0; i < n; i++)
        {
            var b = buf[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return b;
        }
        return 0;
    }

    // ================= 解析与装载 =================

    /// <summary>解析并装载；成功 true。失败时清空当前词库并把原因写进 LastError。</summary>
    private static bool TryLoadFile(string path)
    {
        var db = Parse(path, out var error);
        lock (Gate)
        {
            if (db == null)
            {
                _db = null;
                _lastError = error;
                return false;
            }
            _db = db;
            _lastError = null;   // 成功即清掉上一次的失败原因
            return true;
        }
    }

    /// <summary>解析 db.text.json；失败返回 null 并给出原因（不抛）。</summary>
    private static Db? Parse(string path, out string? error)
    {
        error = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.SequentialScan);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"EH 标签库解析失败：{path} 不是 JSON 对象";
                return null;
            }
            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                error = $"EH 标签库为空或缺少 data 数组：{path}";
                return null;
            }

            var db = new Db { Path = path, Version = ReadVersion(root, path) };
            foreach (var nsEl in data.EnumerateArray())
            {
                if (nsEl.ValueKind != JsonValueKind.Object) continue;

                var nsKey = NormalizeKey(nsEl.TryGetProperty("namespace", out var nsProp) &&
                                         nsProp.ValueKind == JsonValueKind.String
                    ? nsProp.GetString()
                    : null);
                if (nsKey.Length == 0) continue;   // 命名空间为空 → 跳过（照 Go 版）

                if (nsEl.TryGetProperty("data", out var entries) && entries.ValueKind == JsonValueKind.Object)
                {
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var entry in entries.EnumerateObject())
                    {
                        db.Total++;   // 词条总数按 data 里的条目数算（含没有 name 的条目，与 Go 版一致）
                        var name = ReadName(entry.Value);
                        if (name.Length == 0) continue;   // 没有中文名就不必占内存：查表时自然会回落原名
                        map[NormalizeKey(entry.Name)] = name;
                    }
                    if (map.Count > 0) db.Tags[nsKey] = map;
                }

                // 库自带的命名空间中文名（只在固定表没覆盖时兜底，见 FixedLabels 的说明）
                if (nsEl.TryGetProperty("frontMatters", out var fm) && fm.ValueKind == JsonValueKind.Object &&
                    fm.TryGetProperty("name", out var fn) && fn.ValueKind == JsonValueKind.String)
                {
                    var label = fn.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(label)) db.Labels[nsKey] = label;
                }
            }

            if (db.Tags.Count == 0)
            {
                error = $"EH 标签库没有可用词条：{path}";
                return null;
            }
            return db;
        }
        catch (Exception e)
        {
            error = $"解析 EH 标签库失败（{path} 不是合法的 db.text.json）：{e.Message}";
            return null;
        }
    }

    /// <summary>库自述版本：version 字段渲染成 <c>v7</c>，再拼上缓存文件最后写入日期（照 Go 版）。</summary>
    private static string ReadVersion(JsonElement root, string path)
    {
        var raw = root.TryGetProperty("version", out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.String => v.GetString()?.Trim() ?? "",
                _ => "",
            }
            : "";

        var text = raw.Length == 0 ? "v?" : raw.StartsWith('v') ? raw : "v" + raw;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) text += "·" + fi.LastWriteTime.ToString("yyyy-MM-dd");
        }
        catch
        {
            // 拿不到文件时间就不带日期，不影响可用性
        }
        return text;
    }

    /// <summary>取条目的 name 字段（中文名）；结构不符返回空串。</summary>
    private static string ReadName(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)   // 容错：万一上游把条目直接写成字符串
            return entry.GetString()?.Trim() ?? "";
        if (entry.ValueKind != JsonValueKind.Object) return "";
        if (!entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return "";
        return name.GetString()?.Trim() ?? "";
    }

    /// <summary>
    /// 查表键归一：统一小写 + 把空白串压成单个空格（等价 Go 的 strings.Fields + Join(" ")）。
    /// 只在查表键上归一 —— 返回的中文名和"未收录时原样返回的 tag"都不动大小写/空白。
    /// </summary>
    private static string NormalizeKey(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var text = s.Trim().ToLowerInvariant();
        if (text.Length == 0) return "";

        var needsCollapse = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i])) continue;
            // 单空格是正常词内分隔（"big breasts"），要保留；其余情况（制表符/换行/连续空白）都归一到单空格
            if (text[i] != ' ' || (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])))
            {
                needsCollapse = true;
                break;
            }
        }
        if (!needsCollapse) return text;

        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = true; continue; }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ================= 小工具 =================

    private static void SetError(string? message)
    {
        lock (Gate) _lastError = message;
    }

    /// <summary>日志回调自身出错不能影响词库（用户传进来的 Action 不可信）。</summary>
    private static void Log(string message)
    {
        Action<string>? log;
        lock (Gate) log = _log;
        try { log?.Invoke(message); } catch { /* 忽略 */ }
    }

    private static string Describe(Exception e) =>
        e is OperationCanceledException ? "已取消" : $"{e.GetType().Name}：{e.Message}";

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
    }
}
