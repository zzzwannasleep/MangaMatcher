using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace KomgaScraper.Core.Sources;

/// <summary>
/// BookWalker 官方目录库（https://data.bookwalker.com/ bkwk-db.sqlite.zst）下载 → 解压 → 建索引。
/// 索引为紧凑 JSON（gzip），由 BookWalkerIndex 读取；用户点击"下载"时才执行。
///
/// 该地址挂在 Cloudflare 后面，程序直连可能返回 403（人机校验）——所以另有
/// <see cref="ImportLocalAsync"/>：用浏览器下载 zst（或解压出的 sqlite）后导入建索引。
/// </summary>
public sealed class BookWalkerService(IAppLogger log)
{
    /// <summary>官方目录库地址（默认值）。</summary>
    public const string DefaultDownloadUrl = "https://data.bookwalker.com/bkwk-db.sqlite.zst";

    /// <summary>下载并重建索引，返回索引文件路径。</summary>
    public async Task<string> BuildIndexAsync(
        string zstUrl, string dbPath, string indexPath, ProxyConfig? proxy = null,
        Action<int, int, string>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var zstPath = dbPath + ".zst";

        // ---- 1. 下载 zst ----
        progress?.Invoke(0, 3, "下载数据库…");
        log.Info($"下载 BookWalker 数据库: {zstUrl}" +
                 (proxy is { IsSet: true } ? $"（代理 {proxy.Display}）" : ""));
        using (var http = CreateHttp(proxy))
        {
            using var resp = await http.GetAsync(zstUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.ServiceUnavailable)
                throw new InvalidOperationException(
                    $"下载被 BookWalker 的 CDN 拒绝（{(int)resp.StatusCode}，Cloudflare 人机校验）。" +
                    "请点『打开下载页』用浏览器下载 bkwk-db.sqlite.zst，再点『导入本地文件…』选择它。");
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(zstPath);
            var buf = new byte[1 << 16];
            long done = 0;
            int read;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (sw.ElapsedMilliseconds > 2000)
                {
                    sw.Restart();
                    progress?.Invoke(0, 3, total > 0
                        ? $"下载数据库… {done / 1024 / 1024}/{total / 1024 / 1024} MB"
                        : $"下载数据库… {done / 1024 / 1024} MB");
                }
            }
        }
        log.Info($"数据库已下载: {zstPath} ({new FileInfo(zstPath).Length / 1024 / 1024} MB)");

        // ---- 2. 解压 zst → sqlite ----
        progress?.Invoke(1, 3, "解压 zstd…");
        log.Info("解压 zstd…");
        await DecompressZstAsync(zstPath, dbPath, ct).ConfigureAwait(false);
        File.Delete(zstPath);
        log.Info($"解压完成: {dbPath} ({new FileInfo(dbPath).Length / 1024 / 1024} MB)");

        // ---- 3. 建索引 ----
        progress?.Invoke(2, 3, "扫描目录库并建索引…");
        await Task.Run(() => BuildIndexFromSqliteCore(dbPath, indexPath), ct).ConfigureAwait(false);
        progress?.Invoke(3, 3, "索引构建完成");
        log.Info($"BookWalker 索引已生成: {indexPath}");
        return indexPath;
    }

    /// <summary>
    /// 导入本地文件建索引：支持官方 <c>bkwk-db.sqlite.zst</c>（zstd 压缩包），
    /// 也支持用户自行解压出的 <c>.sqlite</c> / <c>.db</c>（按文件头魔数自动判断，不看扩展名）。
    /// </summary>
    public async Task<string> ImportLocalAsync(
        string localFile, string dbPath, string indexPath,
        Action<int, int, string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(localFile))
            throw new FileNotFoundException($"找不到文件：{localFile}", localFile);

        if (IsSqliteFile(localFile))
        {
            // 已经是解压好的 sqlite：直接就地建索引，不复制
            progress?.Invoke(0, 2, "读取本地 sqlite…");
            log.Info($"导入本地数据库: {localFile}（{new FileInfo(localFile).Length / 1024 / 1024} MB）");
            await Task.Run(() => BuildIndexFromSqliteCore(localFile, indexPath), ct).ConfigureAwait(false);
            progress?.Invoke(2, 2, "索引构建完成");
        }
        else if (IsZstdFile(localFile))
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            progress?.Invoke(0, 3, "解压 zst…");
            log.Info($"导入本地 zst: {localFile}（{new FileInfo(localFile).Length / 1024 / 1024} MB）");
            await DecompressZstAsync(localFile, dbPath, ct).ConfigureAwait(false);
            log.Info($"解压完成: {dbPath}（{new FileInfo(dbPath).Length / 1024 / 1024} MB）");
            progress?.Invoke(1, 3, "扫描目录库并建索引…");
            await Task.Run(() => BuildIndexFromSqliteCore(dbPath, indexPath), ct).ConfigureAwait(false);
            progress?.Invoke(3, 3, "索引构建完成");
        }
        else
        {
            throw new InvalidDataException(
                "无法识别的文件：既不是 zstd 压缩包（bkwk-db.sqlite.zst），也不是 sqlite 数据库。" +
                "请确认下载完整（约 55MB），或解压后再导入。");
        }

        log.Info($"BookWalker 索引已生成: {indexPath}");
        return indexPath;
    }

    private static async Task DecompressZstAsync(string zstPath, string dbPath, CancellationToken ct)
    {
        await using var src = File.OpenRead(zstPath);
        await using var dst = File.Create(dbPath);
        await using var dec = new DecompressionStream(src);
        await dec.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private static bool IsZstdFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[4];
            return fs.Read(b) == 4 && b[0] == 0x28 && b[1] == 0xB5 && b[2] == 0x2F && b[3] == 0xFD;
        }
        catch { return false; }
    }

    private static bool IsSqliteFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[16];
            return fs.Read(b) == 16 && System.Text.Encoding.ASCII.GetString(b) == "SQLite format 3\0";
        }
        catch { return false; }
    }

    /// <summary>下载用 HttpClient：可选代理 + 浏览器化请求头（部分 CDN 会按 UA 拦截）。</summary>
    private HttpClient CreateHttp(ProxyConfig? proxy)
    {
        return HttpFactory.Create(
            proxy,
            TimeSpan.FromMinutes(10),
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            keepReferrerToBookWalker: true,
            warn: log.Warn);
    }

    /// <summary>从已下载的 sqlite 构建索引（公开以便测试/手动重建）。</summary>
    public void BuildIndexFromSqlite(string dbPath, string indexPath) =>
        BuildIndexFromSqliteCore(dbPath, indexPath);

    /// <summary>单个系列最多收录多少卷（防止个别超长连载把索引撑爆）。</summary>
    private const int MaxVolumesPerSeries = 400;

    /// <summary>每卷简介的保留上限（超出截断）。</summary>
    private const int MaxVolumeSummary = 400;

    /// <summary>
    /// 从官方目录库构建索引。除系列级字段外，还收录：
    /// 每卷（顶层 product）的卷名/发售日/ISBN/独立封面/独立简介、类型与题材标签、
    /// 出版品牌（label）、complete 状态、series.type。
    /// </summary>
    private static void BuildIndexFromSqliteCore(string dbPath, string indexPath)
    {
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        con.Open();

        var cmd = con.CreateCommand();
        cmd.CommandTimeout = 600;
        // 分组/排序需要临时表：默认写 TMPDIR，受限环境下会失败 → 强制走内存
        foreach (var pragma in new[] { "PRAGMA temp_store = MEMORY", "PRAGMA cache_size = -65536" })
        {
            cmd.CommandText = pragma;
            cmd.ExecuteNonQuery();
        }

        // ---- 出版品牌 label → 品牌名 + 出版社 ----
        var pubByLabel = new Dictionary<string, string>();
        var labelName = new Dictionary<string, string>();
        cmd.CommandText = "SELECT l.id, l.name, p.display_name FROM labels l JOIN publishers p ON l.publisher_id = p.id";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                labelName[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
                pubByLabel[r.GetString(0)] = r.IsDBNull(2) ? "" : r.GetString(2);
            }

        // ---- series → 外部数字 id（bookwalker.jp 系列 id, type=1）----
        var extBySeries = new Dictionary<string, string>();
        cmd.CommandText = "SELECT series_id, external_id FROM series_external_ids WHERE type = 1";
        using (var r = cmd.ExecuteReader())
            while (r.Read()) extBySeries[r.GetString(0)] = r.GetString(1);

        // ---- 系列主表 ----
        var seriesRows = new List<(string Id, string Title, string AltJson, string Desc, string ImgId, int Type)>();
        cmd.CommandText = "SELECT id, title, alt_titles, description, image_id, type FROM series";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                seriesRows.Add((
                    r.GetString(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    SubjectMapper.StripHtml(r.IsDBNull(3) ? "" : r.GetString(3)),
                    r.IsDBNull(4) ? "" : r.GetString(4),
                    r.IsDBNull(5) ? 0 : Convert.ToInt32(r.GetValue(5))));

        // ---- 顶层卷数（parent_content_id IS NULL，排除「分话」）----
        var topVolBySeries = new Dictionary<string, int>();
        cmd.CommandText = "SELECT series_id, COUNT(*) FROM products WHERE parent_content_id IS NULL GROUP BY series_id";
        using (var r = cmd.ExecuteReader())
            while (r.Read()) topVolBySeries[r.GetString(0)] = Convert.ToInt32(r.GetValue(1));

        // ---- 每系列的 label / 封面（优先取顶层 product）----
        var labelBySeries = new Dictionary<string, string>();
        var imgBySeries = new Dictionary<string, string>();
        void LoadSeriesLabelImg(string sql, bool onlyMissing)
        {
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var sid = r.GetString(0);
                if (onlyMissing && labelBySeries.ContainsKey(sid)) continue;
                labelBySeries[sid] = r.IsDBNull(1) ? "" : r.GetString(1);
                imgBySeries[sid] = r.IsDBNull(2) ? "" : r.GetString(2);
            }
        }
        LoadSeriesLabelImg(
            "SELECT series_id, label_id, image_id FROM products WHERE parent_content_id IS NULL GROUP BY series_id",
            onlyMissing: false);
        // 兜底：只有分话、没有顶层 product 的系列
        LoadSeriesLabelImg(
            "SELECT series_id, label_id, image_id FROM products GROUP BY series_id",
            onlyMissing: true);

        // ---- contributor 名称 ----
        var nameByCon = new Dictionary<string, string>();
        cmd.CommandText = "SELECT id, name, name_alt FROM contributors";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                nameByCon[r.GetString(0)] = !r.IsDBNull(2) && !string.IsNullOrEmpty(r.GetString(2))
                    ? r.GetString(2) : r.GetString(1);

        // ---- 系列作者（role 1=作者 2=作画）----
        var authorsBySeries = new Dictionary<string, List<object[]>>();
        cmd.CommandText = "SELECT DISTINCT p.series_id, pc.contributor_id, pc.role FROM product_contributors pc " +
                          "JOIN products p ON p.id = pc.product_id WHERE pc.role IN (1, 2)";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var sid = r.GetString(0);
                if (!authorsBySeries.TryGetValue(sid, out var list))
                    authorsBySeries[sid] = list = [];
                if (list.Count < 8) list.Add([nameByCon.GetValueOrDefault(r.GetString(1), ""), r.GetInt32(2)]);
            }

        // ---- 系列标签：namespace 1=类型(genre) 2=题材(theme) 3=状态(complete/adult/safe) ----
        var genresBySeries = new Dictionary<string, List<string>>();
        var themesBySeries = new Dictionary<string, List<string>>();
        var completeSeries = new HashSet<string>(StringComparer.Ordinal);
        var adultSeries = new HashSet<string>(StringComparer.Ordinal);
        cmd.CommandText = "SELECT st.series_id, t.namespace, t.name FROM series_tags st " +
                          "JOIN tags t ON t.id = st.tag_id WHERE t.namespace IN (1, 2, 3)";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var sid = r.GetString(0);
                var ns = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
                var name = r.IsDBNull(2) ? "" : r.GetString(2).Trim();
                if (name.Length == 0) continue;
                if (ns == 1)
                {
                    if (!genresBySeries.TryGetValue(sid, out var l)) genresBySeries[sid] = l = [];
                    if (l.Count < 12 && !l.Contains(name)) l.Add(name);
                }
                else if (ns == 2)
                {
                    if (!themesBySeries.TryGetValue(sid, out var l)) themesBySeries[sid] = l = [];
                    if (l.Count < 15 && !l.Contains(name)) l.Add(name);
                }
                else if (ns == 3)
                {
                    if (name == "complete") completeSeries.Add(sid);
                    else if (name == "adult") adultSeries.Add(sid);
                }
            }

        // ---- image_id → 文件名 ----
        var imgName = new Dictionary<string, string>();
        cmd.CommandText = "SELECT id, name FROM images";
        using (var r = cmd.ExecuteReader())
            while (r.Read()) imgName[r.GetString(0)] = r.GetString(1);

        // ---- ISBN（product_external_ids.type = 3）----
        var isbnByProduct = new Dictionary<string, string>();
        cmd.CommandText = "SELECT product_id, external_id FROM product_external_ids WHERE type = 3";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var eid = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                if (eid.Length is 13 or 10) isbnByProduct[r.GetString(0)] = eid;
            }

        // ---- 每卷原始行（顶层 product，按 display_order 排序）----
        var rawBySeries = new Dictionary<string, List<(string Pid, int Order, string Title, string Date, string ImgId, string Desc)>>();
        cmd.CommandText = "SELECT series_id, id, display_order, title, on_sale_at, image_id, description " +
                          "FROM products WHERE parent_content_id IS NULL ORDER BY series_id, display_order";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var sid = r.GetString(0);
                if (!rawBySeries.TryGetValue(sid, out var list)) rawBySeries[sid] = list = [];
                if (list.Count >= MaxVolumesPerSeries) continue;
                var sale = r.IsDBNull(4) ? "" : r.GetString(4);
                list.Add((
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2)),
                    r.IsDBNull(3) ? "" : r.GetString(3).Trim(),
                    sale.Length >= 10 ? sale[..10] : sale,
                    r.IsDBNull(5) ? "" : r.GetString(5),
                    SubjectMapper.StripHtml(r.IsDBNull(6) ? "" : r.GetString(6))));
            }

        // ---- 组装 ----
        var entries = new List<Dictionary<string, object?>>();
        foreach (var s in seriesRows)
        {
            var sid = s.Id;

            List<string> alts = [];
            if (s.AltJson.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(s.AltJson);
                    alts.AddRange(doc.RootElement.EnumerateArray()
                        .Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(12));
                }
                catch { /* alt_titles 非 JSON 时忽略 */ }
            }

            var coverName = imgName.GetValueOrDefault(s.ImgId, "");
            if (coverName.Length == 0)
                coverName = imgName.GetValueOrDefault(imgBySeries.GetValueOrDefault(sid, ""), "");

            // 每卷：只保留「有增量信息」的字段
            var raw = rawBySeries.GetValueOrDefault(sid, []);
            var volumes = new List<Dictionary<string, object?>>();
            foreach (var v in raw)
            {
                var isbn = isbnByProduct.GetValueOrDefault(v.Pid, "");
                var volCover = v.ImgId.Length > 0 && v.ImgId != s.ImgId ? imgName.GetValueOrDefault(v.ImgId, "") : "";
                var summary = v.Desc.Length > 0 && v.Desc != s.Desc ? Truncate(v.Desc, MaxVolumeSummary) : "";
                var title = v.Title.Length > 0 && v.Title != s.Title ? Truncate(v.Title, 60) : "";

                var item = new Dictionary<string, object?> { ["o"] = v.Order };
                if (title.Length > 0) item["t"] = title;
                if (v.Date.Length > 0) item["d"] = v.Date;
                if (isbn.Length > 0) item["i"] = isbn;
                if (volCover.Length > 0) item["c"] = volCover;
                if (summary.Length > 0) item["s"] = summary;
                volumes.Add(item);
            }

            var labelId = labelBySeries.GetValueOrDefault(sid, "");
            var entry = new Dictionary<string, object?>
            {
                ["id"] = sid,
                ["t"] = s.Title,
                ["a"] = alts,
                ["d"] = Truncate(s.Desc, 1600),
                ["p"] = pubByLabel.GetValueOrDefault(labelId, ""),
                ["c"] = coverName,
                ["v"] = topVolBySeries.GetValueOrDefault(sid, 0),
                ["s"] = extBySeries.GetValueOrDefault(sid, ""),
                ["au"] = authorsBySeries.GetValueOrDefault(sid, [])
                    .Select(a => new Dictionary<string, object> { ["n"] = a[0], ["r"] = a[1] }).ToList(),
                ["x"] = adultSeries.Contains(sid) ? 1 : 0,
                ["ty"] = s.Type,
                ["g"] = genresBySeries.GetValueOrDefault(sid, []),
                ["th"] = themesBySeries.GetValueOrDefault(sid, []),
                ["cp"] = completeSeries.Contains(sid) ? 1 : 0,
                ["lb"] = labelName.GetValueOrDefault(labelId, ""),
            };
            if (volumes.Count > 0) entry["vol"] = volumes;
            entries.Add(entry);
        }

        using var fs = File.Create(indexPath);
        using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
        JsonSerializer.Serialize(gz, new Dictionary<string, object?>
        {
            ["v"] = BookWalkerIndex.CurrentFormatVersion,
            ["n"] = entries.Count,
            ["e"] = entries,
        }, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}