using KomgaScraper.Core;
using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Mylar;
using KomgaScraper.Core.Scraping;
using KomgaScraper.Core.Sources;
using KomgaScraper.Core.Text;

// Core 层端到端测试驱动：
// 1) 纯函数测试（CJK/标题解析/卷号/日期）
// 2) Bangumi 搜索 + 详情映射（默认用 mock；设置真实模式可连官方 API）
// 3) 对 mock Komga (127.0.0.1:25699) 执行完整刮削流程（真实更新模式）
// 环境变量: REAL_BANGUMI=1 时连官方 api.bgm.tv（需网络可达/代理）

var realBangumi = Environment.GetEnvironmentVariable("REAL_BANGUMI") == "1";
var bangumiBase = realBangumi ? null : "http://127.0.0.1:25698";

// 查看 BookWalker 官方库的表结构（只读诊断）：dotnet run --project tools/TestCli -- bwschema
if (args.Contains("bwschema"))
{
    await DumpBwSchema();
    return;
}

// 深入探查 BookWalker 库里「还没利用」的字段（只读诊断）：dotnet run --project tools/TestCli -- bwprobe
if (args.Contains("bwprobe"))
{
    await ProbeBwDb();
    return;
}

// 端到端验证：新索引（每卷/标签）→ mock Komga 的 PATCH 内容：dotnet run --project tools/TestCli -- bwscrape
if (args.Contains("bwscrape"))
{
    await TestBookWalkerScrapeAsync();
    return;
}

// 只测搜索词解析（把真实文件夹名丢进去看候选词）：dotnet run --project tools/TestCli -- titleparse "名1" "名2"
if (args.Contains("titleparse"))
{
    var names = args.SkipWhile(a => a != "titleparse").Skip(1).ToArray();
    if (names.Length == 0)
        names =
        [
            "[Ark Performance][苍蓝钢铁战舰、苍蓝钢铁的琶音][未完][长鸿][PDF原档]",
            "[BH Snow+Clinic＆苍はるか][小小雪精灵 糖糖][角川][SilentMist][3完]",
            "[Boichi][最强不良传说][东立][ZD×Nearfly][25完]",
            "【堀越耕平】我的英雄学院",
            "海贼王",
        ];
    foreach (var n in names)
    {
        Console.WriteLine($"输入: {n}");
        Console.WriteLine($"  候选(全部): {string.Join(" | ", TitleParser.ExtractSeriesTitles(n, 10))}");
        Console.WriteLine($"  候选(前4) : {string.Join(" | ", TitleParser.ExtractSeriesTitles(n, 4))}");
        Console.WriteLine($"  规范化  : {TitleParser.ExtractAndNormalizeTitle(n)}");
    }

    // ---- 断言：真实日志里出现过的垃圾搜索词必须被过滤掉 ----
    Console.WriteLine("\n==== 垃圾词过滤断言 ====");
    var tf = 0;
    void TCheck(string what, bool ok)
    {
        if (!ok) tf++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }
    var t1 = TitleParser.ExtractSeriesTitles("[KIMIMARU][RE-TAKE 总集篇 全年龄版][Vol.01-Vol.03][完结][汉化]", 4);
    TCheck("完结/汉化/卷号段不进候选", t1.All(t => !t.Contains("完结") && !t.Contains("汉化") && !t.Contains("Vol")));
    var t2 = TitleParser.ExtractSeriesTitles("[MEEくん][辣妹奋战记][大然][黑龙&asimeylada][4完]", 4);
    TCheck("出版社（大然）不进候选", !t2.Contains("大然"));
    TCheck("书名进候选首位", t2.Count > 0 && t2[0] == "辣妹奋战记");
    var t3 = TitleParser.ExtractSeriesTitles("[CLAMP][CLAMP PREMIUM COLLECTION X][台湾角川]", 4);
    TCheck("台湾角川不进候选", !t3.Any(t => t.Contains("台湾角川")));
    var t4 = TitleParser.ExtractSeriesTitles(
        "[NO.ゴメス × EKZ × 空蝉 × 吉沢メガネ][公主骑士是我的同班同学！THE COMIC][Vol.01-Vol.04][青文][电子版]", 4);
    TCheck("青文/电子版不进候选", !t4.Any(t => t.Contains("青文") || t.Contains("电子版")));
    TCheck("书名首位（×作者段不抢位）", t4.Count > 0 && t4[0].StartsWith("公主骑士"));
    var t5 = TitleParser.ExtractSeriesTitles(
        "[TYPE-MOONx森山大辅][Fate／stay night [Unlimited Blade Works]][未完][角川][电子版]", 4);
    TCheck("嵌套括号不再产生 ] 残片", t5.All(t => !t.Contains("]")));
    TCheck("外层标题 Fate/stay night 进候选", t5.Any(t => t.StartsWith("Fate")));
    TCheck("角川不进候选", !t5.Any(t => t.Contains("角川")));
    var t6 = TitleParser.ExtractSeriesTitles("[SOTSU Fields×绫峰栏人×新岛光][银河机攻队][Vol.01-Vol.16][完结][bili]", 4);
    TCheck("bili 不进候选", !t6.Contains("bili"));
    var t7 = TitleParser.ExtractSeriesTitles("[尾田荣一郎][海贼王][1-100完][汉化]", 4);
    TCheck("卷数区间（1-100完）不进候选", !t7.Any(t => t.Contains("1-100")));
    // ---- 断言：用户实测刮错的两部（zero有水印 段曾撞名《ZERO》）----
    var t8 = TitleParser.ExtractSeriesTitles("[アタモ卜][食物战线无异状][Vol.01-Vol.03][zero有水印]", 4);
    TCheck("食物战线无异状 当第一搜索词（含假名的作者段靠后）", t8.Count > 0 && t8[0] == "食物战线无异状");
    TCheck("zero有水印（水印段）不进候选", t8.All(t => !t.Contains("zero") && !t.Contains("水印")));
    TCheck("アタモ卜 仍作备选词", t8.Contains("アタモ卜"));
    var t9 = TitleParser.ExtractSeriesTitles("[马田イスケ][名侦探贵鸟同学][Vol.01][zero有水印]", 4);
    TCheck("名侦探贵鸟同学 当第一搜索词", t9.Count > 0 && t9[0] == "名侦探贵鸟同学");
    TCheck("zero有水印 不进候选（第二部）", t9.All(t => !t.Contains("zero") && !t.Contains("水印")));
    var t10 = TitleParser.ExtractSeriesTitles("[高桥留美子][乱马1/2][大然][join]38全", 4);
    TCheck("剥完 [] 剩下的 38全 不当搜索词", t10.All(t => t != "38全"));
    TCheck("乱马1/2 进候选", t10.Contains("乱马1/2"));
    // ---- 断言：FindPreciseMatch 收紧（宁缺毋滥）----
    static SearchCandidate Cand(string title, string ori = "", string aliases = "") =>
        new(0, title, ori, "", aliases, null);
    var m1 = BatchMatcher.FindPreciseMatch([Cand("ZERO")], "zero有水印");
    TCheck("zero有水印 ⊄ ZERO（残留含水印垃圾 → 拒绝）", m1 == null);
    var m2 = BatchMatcher.FindPreciseMatch([Cand("乱马1/2")], "乱马1/2 完全版");
    TCheck("乱马1/2 完全版 ⊃ 乱马1/2（残留版本词 → 命中）", m2 != null);
    var m3 = BatchMatcher.FindPreciseMatch([Cand("海贼王")], "海贼王 爱藏版");
    TCheck("短候选名（<4 字）不参与包含匹配", m3 == null);
    var m4 = BatchMatcher.FindPreciseMatch([Cand("苍蓝钢铁战舰")], "苍蓝钢铁战舰、苍蓝钢铁的琶音");
    TCheck("合并串 ⊃ 苍蓝钢铁战舰（残留另一标题 → 命中）", m4 != null);
    var m5 = BatchMatcher.FindPreciseMatch([Cand("ONE PIECE")], "ONE PIECE LOG");
    TCheck("one piece log ⊃ one piece（残留 log 拉丁字母 → 拒绝）", m5 == null);
    var m6 = BatchMatcher.FindPreciseMatch([Cand("海贼王", aliases: "海贼王 / ONE PIECE / ワンピース")], "海贼王");
    TCheck("规范名相等仍直接命中", m6 != null);
    Console.WriteLine($"  => {tf} FAIL");
    if (tf > 0) Environment.ExitCode = 1;
    return;
}

// 代理：解析各种写法 + 真实下载一张 lain.bgm.tv 封面（验证代理真的生效）
// dotnet run --project tools/TestCli -- proxytest [scheme://host:port] [user] [pass]
if (args.Contains("proxytest"))
{
    var rest = args.SkipWhile(a => a != "proxytest").Skip(1).ToArray();
    var url = rest.FirstOrDefault(a => a.Contains("://")) ?? "http://127.0.0.1:7890";
    var user = rest.Length > 1 ? rest[1] : null;
    var pass = rest.Length > 2 ? rest[2] : null;

    Console.WriteLine("==== 代理字符串解析 ====");
    (string In, string Scheme, string Host, int Port, string User)[] cases =
    [
        ("127.0.0.1:7890", "http", "127.0.0.1", 7890, ""),
        ("http://127.0.0.1:7890", "http", "127.0.0.1", 7890, ""),
        ("socks5://127.0.0.1:1080", "socks5", "127.0.0.1", 1080, ""),
        ("socks5h://127.0.0.1:1080", "http", "127.0.0.1", 1080, ""),   // 未知协议退回 http
        ("http:127.0.0.1:7890", "http", "127.0.0.1", 7890, ""),        // 漏了 //（真实用户配置就是这样）
        ("socks5:127.0.0.1:1080", "socks5", "127.0.0.1", 1080, ""),
        ("socks5://neo:pass@127.0.0.1:1080", "socks5", "127.0.0.1", 1080, "neo"),
        ("proxy.example.com", "http", "proxy.example.com", 7890, ""),
        ("https://proxy.example.com:8443", "https", "proxy.example.com", 8443, ""),
        ("socks4://[::1]:1080", "socks4", "::1", 1080, ""),
        ("", "", "", 0, ""),
    ];
    var parseFail = 0;
    foreach (var c in cases)
    {
        var (s, h, p, u, _) = ProxyProtocols.Parse(c.In);
        var ok = s == c.Scheme && h == c.Host && p == c.Port && u == c.User;
        if (!ok) parseFail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] \"{c.In}\" → {s}://{h}:{p} user={u}");
    }
    Console.WriteLine($"  组装: {ProxyProtocols.Build("socks5", "127.0.0.1", 1080)}");
    Console.WriteLine($"  解析失败 {parseFail} 条");

    var proxy = new ProxyConfig(url, user, pass);

    // 真实配置文件里的旧单字段代理能否正确迁移（用户老配置是 "http:127.0.0.1:7890"，少写了两条斜杠）
    var realCfg = @"D:\WorkProgram\komgabangumidocker\KomgaScraper\dist\data\settings.json";
    if (File.Exists(realCfg))
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(realCfg))!;
        Console.WriteLine("\n==== 真实 settings.json 的旧代理迁移 ====");
        Console.WriteLine($"  读到的原始值: bangumiProxy=\"{s.LegacyBangumiProxy}\" " +
                          $"scheme=\"{s.ProxyScheme}\" host=\"{s.ProxyHost}\" port={s.ProxyPort}");
        s.MigrateLegacyProxy();
        Console.WriteLine($"  → 迁移/自愈后: 协议={s.ProxyScheme} 地址={s.ProxyHost} 端口={s.ProxyPort} 账号={s.ProxyUser}");
        Console.WriteLine($"  → 组装 ProxyUrl = {s.ProxyUrl}");
        var verr = ProxyProtocols.Validate(s.ProxyScheme, s.ProxyHost, s.ProxyPort);
        Console.WriteLine($"  → 校验: {(verr == null ? "OK" : verr)}");
        if (verr == null && s.ProxyUrl != null)
        {
            try { _ = new System.Net.WebProxy(s.ProxyUrl); Console.WriteLine("  → WebProxy 可构造: OK"); }
            catch (Exception e) { Console.WriteLine($"  → WebProxy 构造失败: {e.Message}"); }
        }
        else if (verr != null)
        {
            Console.WriteLine("  [FAIL] 迁移后仍然非法，会导致启动即崩");
        }
    }

    Console.WriteLine($"\n==== 真实封面下载（走代理 {proxy.Display}）====");
    // 先用 Bangumi API 拿一个真实的封面地址，再分别直连/走代理下载
    string testUrl;
    using (var bgm = new BangumiClient(null, null, proxy.Url, proxyUser: proxy.User, proxyPassword: proxy.Password))
    {
        var probe = await bgm.GetSubjectAsync(21418);   // 苍蓝钢铁的琶音
        testUrl = probe.Images?.Large ?? probe.Images?.Common ?? probe.Image
                  ?? "https://lain.bgm.tv/pic/cover/l/00/00/0.jpg";
        Console.WriteLine($"  subject 21418《{probe.NameCn}》封面: {testUrl}");
    }
    foreach (var (label, cfg) in new (string, ProxyConfig)[] { ("直连", ProxyConfig.None), ("代理", proxy) })
    {
        try
        {
            using var http = HttpFactory.Create(cfg, TimeSpan.FromSeconds(25), "KomgaScraper/1.0");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bytes = await http.GetByteArrayAsync(testUrl);
            sw.Stop();
            Console.WriteLine($"  [{label}] 成功 {bytes.Length / 1024.0:F0} KB / {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            Console.WriteLine($"  [{label}] 失败: {e.Message}");
        }
    }
    return;
}

// 连真实 Bangumi（走代理）验证搜索词解析是否真能命中：dotnet run --project tools/TestCli -- searchreal [代理] [名称...]
if (args.Contains("searchreal"))
{
    var rest = args.SkipWhile(a => a != "searchreal").Skip(1).ToArray();
    var proxy = rest.FirstOrDefault(a => a.StartsWith("http")) ?? "http://127.0.0.1:7890";
    var names = rest.Where(a => !a.StartsWith("http")).ToArray();
    if (names.Length == 0)
        names =
        [
            "[Ark Performance][苍蓝钢铁战舰、苍蓝钢铁的琶音][未完][长鸿][PDF原档]",
            "[BH Snow+Clinic＆苍はるか][小小雪精灵 糖糖][角川][SilentMist][3完]",
            "[Boichi][最强不良传说][东立][ZD×Nearfly][25完]",
        ];
    using var bgm = new BangumiClient(null, null, proxy);
    foreach (var n in names)
    {
        Console.WriteLine($"==== {n}");
        var terms = TitleParser.ExtractSeriesTitles(n, 4);
        Console.WriteLine($"  候选: {string.Join(" | ", terms)}");
        var done = false;
        foreach (var t in terms)
        {
            List<SearchCandidate> res;
            try { res = await bgm.SearchSubjectsAsync(t, "漫画", 0); }
            catch (Exception e) { Console.WriteLine($"    [{t}] 搜索失败: {e.Message}"); continue; }
            var hit = BatchMatcher.FindPreciseMatch(res, TitleParser.ExtractAndNormalizeTitle(t));
            Console.WriteLine($"    [{t}] 返回 {res.Count} 条 → 精确匹配: " +
                              (hit == null ? "无" : $"#{hit.Id} 《{hit.Title}》/《{hit.OriTitle}》"));
            foreach (var r in res.Take(3)) Console.WriteLine($"        候选 #{r.Id} 《{r.Title}》/《{r.OriTitle}》");
            if (hit != null) { done = true; break; }
            await Task.Delay(1200);   // Bangumi 限速
        }
        if (!done) Console.WriteLine("    => 四个候选都没命中");
        await Task.Delay(1200);
    }
    return;
}

// 真机 Dry-run：读 dist\data\settings.json，连真实 Komga + Bangumi（走代理），只跑匹配不写库
// dotnet run --project tools/TestCli -- realdry "标题片段"
if (args.Contains("realdry"))
{
    var frags = args.SkipWhile(a => a != "realdry").Skip(1).ToArray();
    var cfgPath = @"D:\WorkProgram\komgabangumidocker\KomgaScraper\dist\data\settings.json";
    var cfg = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(cfgPath))!;
    cfg.MigrateLegacyProxy();
    Console.WriteLine($"Komga: {cfg.KomgaUrl}  代理: {cfg.Proxy.Display}  AI: {cfg.AiEnabled}  索引: 加载中…");
    var idxPath = @"D:\WorkProgram\komgabangumidocker\KomgaScraper\dist\data\bkwk-index.json.gz";
    var idx = File.Exists(idxPath) ? new BookWalkerIndex(idxPath) : null;
    using var komgaR = new KomgaClient(cfg.KomgaUrl, cfg.KomgaApiKey, cfg.KomgaUsername, cfg.KomgaPassword);
    using var bgmR = new BangumiClient(cfg.BangumiAccessToken,
        string.IsNullOrWhiteSpace(cfg.BangumiApiBase) ? null : cfg.BangumiApiBase,
        cfg.ProxyUrl, proxyUser: cfg.ProxyUser, proxyPassword: cfg.ProxyPassword);
    using var aiR = cfg.AiEnabled ? new AiClient(cfg.AiBaseUrl, cfg.AiApiKey, cfg.AiModel) : null;
    var matcher = new BatchMatcher(komgaR, bgmR, new ConsoleLogger())
    {
        Proxy = cfg.Proxy,
        MatchTypePlatform = cfg.MatchType,
        BwIndex = idx,
        FetchVolumeData = cfg.FetchVolumeData,
        BwWriteTags = cfg.BwWriteTags,
        DryRun = true,          // 只预览，绝不写
        Ai = aiR,
    };
    var libs = await komgaR.GetLibrariesAsync();
    var lib = libs.FirstOrDefault(l => l.Id == cfg.LastLibraryId) ?? libs[0];
    Console.WriteLine($"库: {lib.Name} ({lib.Id})");
    var all = await komgaR.ListSeriesInLibraryAsync(lib.Id);
    Console.WriteLine($"系列 {all.Count}");
    foreach (var f in frags)
    {
        var s = all.FirstOrDefault(x => (x.Metadata?.Title ?? x.Name).Contains(f, StringComparison.Ordinal));
        if (s == null) { Console.WriteLine($"[跳过] 找不到包含 \"{f}\" 的系列"); continue; }
        Console.WriteLine($"\n==== Dry-run: {s.Metadata?.Title ?? s.Name}");
        var r = await matcher.PreciseMatchAsync(s, SyncType.Meta, false);
        Console.WriteLine($"    => success={r.Success} skip={r.Skipped} matched={r.MatchedTitle} err={r.Error}");
    }
    return;
}

// 并行批量提速实测（全程 Dry-run，绝不写库）：
// 跳过路径（已链接系列）与刮削路径（未链接系列）分别计时，验证并行 + 去重 + 节流的真实收益
async Task SpeedCheckAsync(int skipN, int scrapeN)
{
    var cfgPath = @"D:\WorkProgram\komgabangumidocker\KomgaScraper\dist\data\settings.json";
    var cfg = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(cfgPath))!;
    cfg.MigrateLegacyProxy();
    Console.WriteLine($"Komga: {cfg.KomgaUrl}  代理: {cfg.Proxy.Display}  并发: 3");
    var idxPath = @"D:\WorkProgram\komgabangumidocker\KomgaScraper\dist\data\bkwk-index.json.gz";
    var idx = File.Exists(idxPath) ? new BookWalkerIndex(idxPath) : null;
    using var komga = new KomgaClient(cfg.KomgaUrl, cfg.KomgaApiKey, cfg.KomgaUsername, cfg.KomgaPassword);
    using var bangumi = new BangumiClient(cfg.BangumiAccessToken,
        string.IsNullOrWhiteSpace(cfg.BangumiApiBase) ? null : cfg.BangumiApiBase,
        cfg.ProxyUrl, proxyUser: cfg.ProxyUser, proxyPassword: cfg.ProxyPassword);
    using var ai = cfg.AiEnabled ? new AiClient(cfg.AiBaseUrl, cfg.AiApiKey, cfg.AiModel) : null;
    var matcher = new BatchMatcher(komga, bangumi, new ConsoleLogger())
    {
        Proxy = cfg.Proxy,
        MatchTypePlatform = cfg.MatchType,
        BwIndex = idx,
        FetchVolumeData = cfg.FetchVolumeData,
        BwWriteTags = cfg.BwWriteTags,
        DryRun = true,
        Ai = ai,
        SeriesConcurrency = 3,
    };
    var libs = await komga.GetLibrariesAsync();
    var lib = libs.FirstOrDefault(l => l.Id == cfg.LastLibraryId) ?? libs[0];
    var all = await komga.ListSeriesInLibraryAsync(lib.Id);
    bool HasLink(SeriesDto s) => s.Metadata?.Links?.Any(l => l.Url.Contains("/subject/", StringComparison.Ordinal)) == true;
    var linked = all.Where(s => HasLink(s)).Take(skipN).ToList();
    var unlinked = all.Where(s => !HasLink(s)).Take(scrapeN).ToList();
    Console.WriteLine($"库 \"{lib.Name}\"：共 {all.Count} 系列；取已链接 {linked.Count} + 未链接 {unlinked.Count} 做只读实测\n");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var st1 = await matcher.BatchMatchAsync(linked, SyncType.Meta, false);
    sw.Stop();
    Console.WriteLine($"\n→ 跳过路径: {st1}  {sw.ElapsedMilliseconds}ms（{sw.ElapsedMilliseconds / Math.Max(1, linked.Count)}ms/系列 × 3 并发）");

    sw.Restart();
    var st2 = await matcher.BatchMatchAsync(unlinked, SyncType.Meta, false);
    sw.Stop();
    Console.WriteLine($"\n→ 刮削路径(Dry): {st2}  {sw.ElapsedMilliseconds}ms（{sw.ElapsedMilliseconds / Math.Max(1, unlinked.Count)}ms/系列，含搜索+详情+翻译）");
}

// 并行批量提速实测（只读，不写库）：dotnet run --project tools/TestCli -- speedcheck [跳过数] [刮削数]
if (args.Contains("speedcheck"))
{
    var nums = args.SkipWhile(a => a != "speedcheck").Skip(1)
                   .Select(a => int.TryParse(a, out var n) ? n : 0).ToList();
    await SpeedCheckAsync(nums.Count > 0 ? nums[0] : 8, nums.Count > 1 ? nums[1] : 4);
    return;
}

// 只测 AI（匹配 + 简介翻译，用本地 mock 接口）：dotnet run --project tools/TestCli -- aitest
if (args.Contains("aitest"))
{
    await TestAiAsync();
    return;
}

// 第三源 Mox（bookof.moe）+ 纠正匹配 + 小封面不再被拒收：dotnet run --project tools/TestCli -- moxtest
if (args.Contains("moxtest"))
{
    await TestMoxAsync();
    return;
}

// AI 识别驱动的匹配回退链（中文名搜不到 → AI 报原名 → 原名重搜命中）：matchtest
if (args.Contains("matchtest"))
{
    await TestMatchChainAsync();
    return;
}

// 版本/格式标记 + MACHINE 打标（文件夹名标注 → 标签 + 标题后缀）：marktest
if (args.Contains("marktest"))
{
    await TestMarkerAsync();
    return;
}

// 卷号对齐修复验证（乱马1/2 每卷封面错位的回归测试）：volmatchtest
if (args.Contains("volmatchtest"))
{
    await TestVolumeMatchAsync();
    return;
}

// Dry-run 实时改动预览（ScrapeChange 事件逐条发射）：previewtest
if (args.Contains("previewtest"))
{
    await TestPreviewAsync();
    return;
}

// 编辑器 PATCH 体构建（MACHINE 必打 / 只发改动字段 / 锁回传）：editortest
if (args.Contains("editortest"))
{
    await TestEditorPatchAsync();
    return;
}

// 版本标记黑白名单（白名单扩识别 / 黑名单丢弃+清旧后缀 / [] 命名格式）：tagruletest
if (args.Contains("tagruletest"))
{
    await TestTagRuleAsync();
    return;
}

// 百度百科兜底源（真实接口 + 全 mock 兜底链）：baiketest
if (args.Contains("baiketest"))
{
    await TestBaikeAsync();
    return;
}

// MangaDex 源（真实接口 + 全 mock 兜底链）：mdtest
if (args.Contains("mdtest"))
{
    await TestMangaDexAsync();
    return;
}

// 只测 BookWalker 导入（不需要 mock 服务器）：dotnet run --project tools/TestCli -- bwimport
if (args.Contains("bwimport"))
{
    await TestBookWalkerImport();
    return;
}

Console.WriteLine("==== 1. 纯函数测试 ====");
Console.WriteLine($"T2S(經驗値別皚藹)={CJK.T2S("經驗値別皚藹")}  (期望: 经验值别皑蔼)");
var titles = TitleParser.ExtractSeriesTitles("[碧蓝之海][井上堅二×吉岡公威][Vol.01-Vol.18]", 3);
Console.WriteLine($"ExtractTitles: [{string.Join(" | ", titles)}]");
Console.WriteLine($"VolNum('Vol.03')={TitleParser.ExtractVolumeNumber("Vol.03")} VolNum('第12卷')={TitleParser.ExtractVolumeNumber("第12卷")}");
Console.WriteLine($"Date('2023年4月1日')={TitleParser.NormalizeDate("2023年4月1日")}  Date('2014年5月')={TitleParser.NormalizeDate("2014年5月")}");

Console.WriteLine();
Console.WriteLine($"==== 2. Bangumi {(realBangumi ? "官方API" : "MOCK")} 测试 ====");
using var bangumi = new BangumiClient(null, bangumiBase);
if (!realBangumi) bangumi.MinRequestIntervalMs = 0;   // 本地 mock 不用礼貌节流
List<SearchCandidate> candidates;
try
{
    candidates = await bangumi.SearchSubjectsAsync("碧蓝之海", KomgaScraper.Core.Scraping.MatchType.Manga, 8);
    Console.WriteLine($"搜索结果 {candidates.Count} 条:");
    foreach (var c in candidates.Take(3))
        Console.WriteLine($"  #{c.Id} 《{c.Title}》 原:《{c.OriTitle}》 作者:{c.Author} 别名:{c.Aliases} 封面:{(c.CoverUrl != null ? "有" : "无")}");
}
catch (Exception e)
{
    Console.WriteLine($"[搜索失败] {e.Message}");
    return;
}

if (candidates.Count == 0) { Console.WriteLine("无搜索结果"); return; }

var subject = await bangumi.GetSubjectAsync(candidates[0].Id);
Console.WriteLine($"详情: name={subject.Name} name_cn={subject.NameCn} platform={subject.Platform} volumes={subject.Volumes} age_rating={subject.AgeRating} nsfw={subject.Nsfw}");
var related = await bangumi.GetRelatedSubjectsAsync(candidates[0].Id);
Console.WriteLine($"关联条目 {related.Count} 个，其中单行本 {related.Count(r => r.Relation == "单行本")} 个");

var fakeSeries = new SeriesDto
{
    Id = "s1",
    Name = "[碧蓝之海][井上堅二×吉岡公威][Vol.01-Vol.05]",
    Metadata = new SeriesMetadataDto { Genres = [], Tags = [], Links = [] },
};
var meta = SubjectMapper.BuildSeriesMeta(subject, fakeSeries);
Console.WriteLine($"映射: title={meta.Title}");
Console.WriteLine($"      titleSort={meta.TitleSort}");
Console.WriteLine($"      publisher={meta.Publisher}");
Console.WriteLine($"      status={meta.Status}");
Console.WriteLine($"      totalBookCount={meta.TotalBookCount} ageRating={meta.AgeRating}");
Console.WriteLine($"      authors=[{string.Join(";", (meta.Authors ?? []).Select(a => $"{a.Name}({a.Role})"))}]");
Console.WriteLine($"      tags=[{string.Join(",", (meta.Tags ?? []).Take(10))}]");
Console.WriteLine($"      links=[{string.Join(";", (meta.Links ?? []).Select(l => l.Label + ":" + l.Url))}]");
Console.WriteLine($"      altTitles={string.Join(",", (meta.AlternateTitles ?? []).Select(a => a.Label + ":" + a.Title))}");

Console.WriteLine();
Console.WriteLine("==== 3. Mock Komga 全流程刮削 ====");
using var komga = new KomgaClient("http://127.0.0.1:25699", "test-key", null, null);
var logger = new ConsoleLogger();
var scrape = new ScrapeService(komga, bangumi, logger);
var result = await scrape.ScrapeAsync("s1", candidates[0].Id,
    new ScrapeOptions { SyncType = SyncType.All, FetchVolumeData = true, DryRun = false });
Console.WriteLine($"ScrapeAsync => success={result.Success} error={result.Error} matched={result.MatchedTitle}");

Console.WriteLine();
Console.WriteLine("==== 4. Dry-run 验证 ====");
var scrape2 = new ScrapeService(komga, bangumi, logger);
var dry = await scrape2.ScrapeAsync("s1", candidates[0].Id,
    new ScrapeOptions { SyncType = SyncType.All, FetchVolumeData = false, DryRun = true });
Console.WriteLine($"DryRun => success={dry.Success}");

Console.WriteLine();
Console.WriteLine("==== 5. BookWalker 导入建索引 ====");
await TestBookWalkerImport();

Console.WriteLine();
Console.WriteLine("==== 全部完成 ====");

// BookWalker 官方库表结构诊断（只读）：我们用了哪些表/字段，库里还有什么
async Task DumpBwSchema()
{
    var db = @"D:\WorkProgram\komgabangumidocker\reference\bkwk-db.sqlite";
    if (!File.Exists(db)) { Console.WriteLine($"[跳过] 未找到 {db}"); return; }
    using var con = new Microsoft.Data.Sqlite.SqliteConnection(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
        }.ToString());
    con.Open();

    var used = new Dictionary<string, string>
    {
        ["series"] = "主查询：id/title/alt_titles/description/image_id",
        ["series_external_ids"] = "type=1 → bookwalker.jp 系列数字 id（拼 Bw 链接）",
        ["products"] = "卷数 COUNT(*)、label_id、image_id（封面）",
        ["labels"] = "label_id → publisher_id",
        ["publishers"] = "display_name → 出版社",
        ["contributors"] = "name / name_alt → 作者名",
        ["product_contributors"] = "role IN (1,2) → 系列作者（1=作者 2=作画）",
        ["images"] = "image_id → 封面文件名",
        ["series_tags"] = "tag='adult' → 成人标记",
        ["tags"] = "标签名（只查 adult）",
    };

    var tables = new List<string>();
    using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) tables.Add(r.GetString(0));
    }

    Console.WriteLine($"表数量: {tables.Count}");
    foreach (var t in tables)
    {
        long rows = 0;
        try
        {
            using var c2 = con.CreateCommand();
            c2.CommandText = $"SELECT COUNT(*) FROM \"{t}\"";
            rows = Convert.ToInt64(c2.ExecuteScalar() ?? 0L);
        }
        catch { }
        var cols = new List<string>();
        using (var c3 = con.CreateCommand())
        {
            c3.CommandText = $"PRAGMA table_info(\"{t}\")";
            using var r3 = c3.ExecuteReader();
            while (r3.Read()) cols.Add(r3.GetString(1));
        }
        var mark = used.TryGetValue(t, out var why) ? $"  ← 用到：{why}" : "";
        Console.WriteLine($"\n[{t}] rows={rows}{mark}");
        Console.WriteLine($"   列: {string.Join(", ", cols)}");
    }

    Console.WriteLine("\n---- 未使用的表里有什么（抽样 1 行，看是否值得利用） ----");
    foreach (var t in tables.Where(x => !used.ContainsKey(x)))
    {
        try
        {
            using var c4 = con.CreateCommand();
            c4.CommandText = $"SELECT * FROM \"{t}\" LIMIT 1";
            using var r4 = c4.ExecuteReader();
            if (!r4.Read()) continue;
            var vals = new List<string>();
            for (int i = 0; i < r4.FieldCount; i++)
            {
                var v = r4.IsDBNull(i) ? "NULL" : r4.GetValue(i)?.ToString() ?? "";
                if (v.Length > 60) v = v[..60] + "…";
                vals.Add($"{r4.GetName(i)}={v}");
            }
            Console.WriteLine($"[{t}] {string.Join(" | ", vals)}");
        }
        catch { }
    }

    void Q(string title, string sql)
    {
        Console.WriteLine($"\n---- {title} ----");
        try
        {
            using var c = con.CreateCommand();
            c.CommandText = sql;
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var vals = new List<string>();
                for (int i = 0; i < r.FieldCount; i++)
                {
                    var v = r.IsDBNull(i) ? "NULL" : r.GetValue(i)?.ToString() ?? "";
                    if (v.Length > 70) v = v[..70] + "…";
                    vals.Add(v);
                }
                Console.WriteLine("  " + string.Join(" | ", vals));
            }
        }
        catch (Exception e) { Console.WriteLine("  查询失败: " + e.Message); }
    }

    Q("series.type 分布（我们没用这个字段）", "SELECT type, COUNT(*) FROM series GROUP BY type");
    Q("contributors role 取值（确认 1=作者 2=作画）", "SELECT role, COUNT(*) FROM product_contributors GROUP BY role ORDER BY 2 DESC");
    Q("tags 使用量 Top15（我们只用了 adult）",
      "SELECT t.namespace, t.name, COUNT(*) c FROM series_tags st JOIN tags t ON t.id = st.tag_id GROUP BY t.id ORDER BY c DESC LIMIT 15");
    Q("某系列在 products 里的每卷数据（BookWalker 有，我们现在没利用）",
      "SELECT p.title, substr(coalesce(p.description,''),1,45) d, p.on_sale_at, p.display_order, p.copyright " +
      "FROM products p WHERE p.series_id = (SELECT series_id FROM products GROUP BY series_id ORDER BY COUNT(*) DESC LIMIT 1) " +
      "ORDER BY p.display_order LIMIT 4");
    Q("product_external_ids 的 source/type 取值（每卷的其它站点 id）",
      "SELECT source, type, COUNT(*) FROM product_external_ids GROUP BY source, type ORDER BY 3 DESC LIMIT 8");
}

// 深入探查：BookWalker 库里「有价值但尚未利用」的字段分布（只读）
async Task ProbeBwDb()
{
    var db = @"D:\WorkProgram\komgabangumidocker\reference\bkwk-db.sqlite";
    if (!File.Exists(db)) { Console.WriteLine($"[跳过] 未找到 {db}"); return; }
    using var con = new Microsoft.Data.Sqlite.SqliteConnection(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
        }.ToString());
    con.Open();
    // 关键：分组/排序会用临时表，默认写 TMPDIR（沙箱里打不开）→ 强制走内存
    Exec("PRAGMA temp_store = MEMORY");
    Exec("PRAGMA cache_size = -65536");

    void Exec(string sql)
    {
        using var c = con.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    void Q(string title, string sql, int maxRows = 12)
    {
        Console.WriteLine($"\n---- {title} ----");
        try
        {
            using var c = con.CreateCommand();
            c.CommandText = sql;
            using var r = c.ExecuteReader();
            var n = 0;
            while (r.Read() && n++ < maxRows)
            {
                var vals = new List<string>();
                for (int i = 0; i < r.FieldCount; i++)
                {
                    var v = r.IsDBNull(i) ? "NULL" : r.GetValue(i)?.ToString() ?? "";
                    v = v.Replace('\n', ' ').Replace('\r', ' ');
                    if (v.Length > 90) v = v[..90] + "…";
                    vals.Add(v);
                }
                Console.WriteLine("  " + string.Join(" | ", vals));
            }
            if (n == 0) Console.WriteLine("  (无数据)");
        }
        catch (Exception e) { Console.WriteLine("  查询失败: " + e.Message); }
    }

    Q("1. products.level / content_type / product_type 分布（区分卷 / 话）",
      "SELECT level, content_type, product_type, COUNT(*) FROM products GROUP BY level, content_type, product_type ORDER BY 4 DESC");

    Q("2. series.type 分布 + 抽样标题（判断 1/2/3/4 是什么）",
      "SELECT type, COUNT(*), group_concat(substr(title,1,28), ' ;; ') FROM (SELECT type, title FROM series ORDER BY id LIMIT 4000) GROUP BY type");

    Q("3. tags.namespace + priority 分布（namespace 决定 genre/theme/status）",
      "SELECT namespace, priority, COUNT(*) FROM tags GROUP BY namespace, priority ORDER BY 3 DESC LIMIT 20");

    Q("4. product_external_ids 的 source/type 分布（找 ISBN）",
      "SELECT source, type, COUNT(*), min(length(external_id)), max(length(external_id)) FROM product_external_ids GROUP BY source, type ORDER BY 3 DESC LIMIT 12");

    Q("5. 疑似 ISBN 的 external_id 样本（13 位数字）",
      "SELECT source, type, external_id, external_id_original FROM product_external_ids WHERE external_id GLOB '[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]' LIMIT 5");

    Q("6. 每卷数据完整度：有 description / on_sale_at / copyright 的比例",
      "SELECT COUNT(*) total, SUM(description IS NOT NULL AND description <> '') has_desc, " +
      "SUM(on_sale_at IS NOT NULL) has_date, SUM(copyright IS NOT NULL) has_cr, SUM(image_id IS NOT NULL) has_img " +
      "FROM products");

    Q("7. 一个真实系列的全部 products（看卷标题/顺序/简介/发售日）",
      "SELECT p.display_order, p.title, p.subtitle, substr(coalesce(p.description,''),1,40), p.on_sale_at " +
      "FROM products p WHERE p.series_id = (SELECT series_id FROM products GROUP BY series_id ORDER BY COUNT(*) DESC LIMIT 1) " +
      "ORDER BY p.display_order LIMIT 6");

    Q("8. images 表样本（name 是否为可直接拼 URL 的文件名；有无多尺寸）",
      "SELECT i.id, i.name, i.mime, i.width, i.height FROM images i JOIN series s ON s.image_id = i.id LIMIT 6");

    Q("9. 某系列的 tags（含 namespace/priority，看能否当 genres/tags 用）",
      "SELECT t.namespace, t.priority, t.name FROM series_tags st JOIN tags t ON t.id = st.tag_id " +
      "WHERE st.series_id = (SELECT series_id FROM products GROUP BY series_id ORDER BY COUNT(*) DESC LIMIT 1) ORDER BY t.namespace, t.priority");

    Q("10. labels 样本（出版社品牌 / imprint，目前没用）",
      "SELECT l.id, l.name, l.aliases, p.display_name FROM labels l JOIN publishers p ON p.id = l.publisher_id LIMIT 8");

    Q("11. products 是否有 add_on / parent_content_id（特典、分话归属）",
      "SELECT parent_content_id IS NOT NULL has_parent, add_on, add_on_campaign_only, COUNT(*) FROM products GROUP BY has_parent, add_on, add_on_campaign_only ORDER BY 4 DESC LIMIT 10");

    Q("12. 系列标题语言样本（判断是否需要 language / readingDirection）",
      "SELECT id, title, substr(coalesce(alt_titles,''),1,40) FROM series WHERE title GLOB '*[A-Za-z]*' LIMIT 3");

    Q("13. products.alt_titles / display_title 样本（卷的别名，可用于 book alternateTitle? )",
      "SELECT title, alt_titles, display_title, display_title_short, subtitle FROM products WHERE alt_titles IS NOT NULL AND alt_titles <> '' LIMIT 3");

    Q("14. product_contributors.role 分布（现在只用了 1/2）",
      "SELECT role, COUNT(*) FROM product_contributors GROUP BY role ORDER BY 2 DESC LIMIT 15");

    Q("15. series_external_ids 的 type 分布（可能含 MAL/ANN 等外部链接）",
      "SELECT type, COUNT(*), min(external_id), max(external_id) FROM series_external_ids GROUP BY type ORDER BY 2 DESC LIMIT 10");

    Q("16. 顶层卷 vs 分话：按 series 统计（修正 totalBookCount）",
      "SELECT (SELECT COUNT(*) FROM products) all_rows, " +
      "(SELECT COUNT(*) FROM products WHERE parent_content_id IS NULL) top_level, " +
      "(SELECT COUNT(*) FROM products WHERE parent_content_id IS NOT NULL) children, " +
      "(SELECT COUNT(DISTINCT series_id) FROM products) series_cnt");

    Q("17. 一个系列：顶层卷 + 其下分话（看层级关系）",
      "SELECT p.level, p.parent_content_id IS NULL is_top, p.display_order, p.title, p.on_sale_at FROM products p " +
      "WHERE p.series_id = (SELECT series_id FROM products WHERE parent_content_id IS NULL GROUP BY series_id ORDER BY COUNT(*) DESC LIMIT 1) " +
      "ORDER BY is_top DESC, p.parent_content_id, p.display_order LIMIT 14");

    Q("18. tags.namespace=4 的样本（179 个，priority=0）",
      "SELECT id, name, slug, priority FROM tags WHERE namespace = 4 LIMIT 12");

    Q("19. namespace=3 的全部标签（状态/成人等）",
      "SELECT name, (SELECT COUNT(*) FROM series_tags st WHERE st.tag_id = tags.id) c FROM tags WHERE namespace = 3 ORDER BY c DESC LIMIT 20");

    Q("20. ISBN：type=3 / type=7 与卷的对应（同一个 product 两种 id?）",
      "SELECT p.title, p.display_order, " +
      "group_concat(CASE WHEN e.type = 3 THEN e.external_id END) isbn3, " +
      "group_concat(CASE WHEN e.type = 7 THEN substr(e.external_id,1,20) END) id7 " +
      "FROM products p JOIN product_external_ids e ON e.product_id = p.id " +
      "WHERE p.series_id = (SELECT series_id FROM products WHERE parent_content_id IS NULL GROUP BY series_id ORDER BY COUNT(*) DESC LIMIT 1) " +
      "GROUP BY p.id LIMIT 5");

    Q("21. 有 ISBN(type=3) 的顶层卷数量占比",
      "SELECT COUNT(DISTINCT p.id) top_with_isbn FROM products p JOIN product_external_ids e ON e.product_id = p.id " +
      "WHERE e.type = 3 AND p.parent_content_id IS NULL");

    Q("22. series.type 与 content_type/标签的关系（判断 1=漫画 2=小说…）",
      "SELECT s.type, COUNT(*) series_cnt, " +
      "(SELECT COUNT(*) FROM products p WHERE p.series_id = s.id AND p.content_type = 1) ct1, " +
      "(SELECT COUNT(*) FROM products p WHERE p.series_id = s.id AND p.content_type = 2) ct2 " +
      "FROM series s GROUP BY s.type");

    Q("23. alt_titles 非空的系列占比 & 是否含罗马字/日文（可填 Komga alternateTitles）",
      "SELECT COUNT(*) total, SUM(alt_titles IS NOT NULL AND alt_titles <> '') has_alt FROM series");

    Q("24. 一部漫画(type=1)的顶层卷：卷名/顺序/ISBN/子项数",
      "SELECT p.id, p.display_order, p.title, (SELECT e.external_id FROM product_external_ids e WHERE e.product_id = p.id AND e.type = 3) isbn, " +
      "(SELECT COUNT(*) FROM products c WHERE c.parent_content_id = p.id) children " +
      "FROM products p WHERE p.series_id = (SELECT id FROM series WHERE type = 1 ORDER BY id LIMIT 1) " +
      "AND p.parent_content_id IS NULL ORDER BY p.display_order LIMIT 10");

    Q("25. 顶层卷数量的分布（>50 的系列=按话连载）",
      "SELECT cnt, COUNT(*) series FROM (SELECT series_id, COUNT(*) cnt FROM products WHERE parent_content_id IS NULL GROUP BY series_id) GROUP BY cnt ORDER BY cnt DESC LIMIT 8");

    Q("26. 一个 product 的全部 external_id（判断 type 含义）",
      "SELECT e.type, e.source, e.external_id FROM product_external_ids e WHERE e.product_id = " +
      "(SELECT product_id FROM product_external_ids WHERE type = 3 LIMIT 1)");

    Q("27. 漫画卷标题样本（看是否 'Vol.1' 形式，便于匹配 Komga 卷号）",
      "SELECT group_concat(title, ' | ') FROM (SELECT title FROM products WHERE content_type = 1 AND parent_content_id IS NULL AND series_id IN (SELECT id FROM series WHERE type = 1) LIMIT 12)");

    Q("28. product_tags：卷级标签是否也有内容（而非只有 series_tags）",
      "SELECT pt.tag_id, t.namespace, t.name, COUNT(*) FROM product_tags pt JOIN tags t ON t.id = pt.tag_id GROUP BY pt.tag_id ORDER BY 4 DESC LIMIT 8");

    Q("29. 有 description 的顶层卷里，简介与系列简介不同的比例",
      "SELECT COUNT(*) top_with_desc, SUM(p.description <> s.description) differs " +
      "FROM products p JOIN series s ON s.id = p.series_id WHERE p.parent_content_id IS NULL AND p.description IS NOT NULL");

    Q("30. 顶层卷有独立封面图的比例（image_id 与 series.image_id 不同）",
      "SELECT COUNT(*) top_cnt, SUM(p.image_id <> s.image_id) own_cover " +
      "FROM products p JOIN series s ON s.id = p.series_id WHERE p.parent_content_id IS NULL");

    Q("31. 有顶层卷的系列数 vs 总系列数",
      "SELECT (SELECT COUNT(*) FROM series) total_series, " +
      "(SELECT COUNT(DISTINCT series_id) FROM products WHERE parent_content_id IS NULL) series_with_top");

    Q("32. 顶层卷数分桶（看多少系列会被「按话连载」过滤掉）",
      "SELECT CASE WHEN cnt <= 20 THEN '1-20' WHEN cnt <= 60 THEN '21-60' WHEN cnt <= 200 THEN '61-200' ELSE '200+' END bucket, " +
      "COUNT(*) series, SUM(cnt) vols FROM (SELECT series_id, COUNT(*) cnt FROM products WHERE parent_content_id IS NULL GROUP BY series_id) GROUP BY bucket");

    Q("33. ISBN 分布：按系列顶层卷数分桶（判断 ISBN 对真实卷集是否有用）",
      "SELECT CASE WHEN cnt <= 60 THEN '<=60' ELSE '>60' END bucket, COUNT(DISTINCT p.series_id) series, COUNT(*) isbn_rows " +
      "FROM products p JOIN product_external_ids e ON e.product_id = p.id AND e.type = 3 " +
      "JOIN (SELECT series_id, COUNT(*) cnt FROM products WHERE parent_content_id IS NULL GROUP BY series_id) t ON t.series_id = p.series_id " +
      "WHERE p.parent_content_id IS NULL GROUP BY bucket");

    Q("34. 顶层卷名是否多为 Chapter/Episode（判断过滤规则是否合理）",
      "SELECT SUM(title GLOB 'Chapter*') chapter, SUM(title GLOB 'Episode*') episode, " +
      "SUM(title GLOB 'Vol*') vol, COUNT(*) total FROM products WHERE parent_content_id IS NULL");
}

// 端到端验证：用真实目录库建 v2 索引 → mock Komga 看实际 PATCH 了什么
async Task TestBookWalkerScrapeAsync()
{
    const string bangumiSubjectJson =
        "{\"id\":1,\"name\":\"Test Original\",\"name_cn\":\"测试系列\",\"platform\":\"漫画\"," +
        "\"summary\":\"一个用于测试的中文简介。\",\"volumes\":3,\"nsfw\":false,\"age_rating\":0," +
        "\"infobox\":[{\"key\":\"作者\",\"value\":\"测试作者\"},{\"key\":\"出版社\",\"value\":\"测试出版社\"}]," +
        "\"tags\":[{\"name\":\"日常\",\"count\":10}]}";

    void Check(string what, bool ok) => Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");

    // mock Komga 路由：(method, path, body) → (status, json)
    (int, string) Route(string method, string path, string body, string seriesTitle,
        List<BookWalkerVolume> vols, Action<string> onSeriesPatch, List<string> patches)
    {
        if (method == "GET" && path == "/api/v1/series/s1")
            return (200, System.Text.Json.JsonSerializer.Serialize(new
            {
                id = "s1",
                name = seriesTitle,
                libraryId = "lib1",
                oneshot = false,
                booksCount = vols.Count,
                metadata = new { title = seriesTitle },
            }));
        if (method == "GET" && path == "/api/v1/series/s1/metadata")
            return (200, "{\"title\":" + System.Text.Json.JsonSerializer.Serialize(seriesTitle) + "}");
        if (method == "PATCH" && path == "/api/v1/series/s1/metadata") { onSeriesPatch(body); return (204, ""); }
        if (method == "POST" && path.StartsWith("/api/v1/books/list"))
            return (200, System.Text.Json.JsonSerializer.Serialize(new
            {
                content = vols.Select((v, i) => new
                {
                    id = $"b{i + 1}",
                    name = $"Vol.{v.Order:00}.cbz",
                    number = v.Order,
                    seriesId = "s1",
                    deleted = false,
                    metadata = new { title = (string?)null },
                }),
                totalElements = vols.Count,
                numberOfElements = vols.Count,
            }));
        if (method == "PATCH" && path.StartsWith("/api/v1/books/")) { patches.Add(body); return (204, ""); }
        if (method == "GET" && path.StartsWith("/api/v1/collections"))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        if (method == "GET" && path.Contains("/thumbnails")) return (200, "[]");
        return (404, "{}");
    }

    var db = @"D:\WorkProgram\komgabangumidocker\reference\bkwk-db.sqlite";
    if (!File.Exists(db)) { Console.WriteLine($"[跳过] 未找到 {db}"); return; }
    var work = @"D:\WorkProgram\komgabangumidocker\bwtest";
    Directory.CreateDirectory(work);
    var indexPath = Path.Combine(work, "idx-v2.json.gz");

    // ---- 1. 建 v2 索引 ----
    Console.WriteLine("==== 1. 从官方目录库构建 v2 索引 ====");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var svc = new BookWalkerService(new ConsoleLogger());
    await svc.ImportLocalAsync(db, Path.Combine(work, "bkwk-db.sqlite"), indexPath);
    sw.Stop();
    Console.WriteLine($"  耗时 {sw.Elapsed.TotalSeconds:F1}s，索引 {new FileInfo(indexPath).Length / 1024.0 / 1024.0:F2} MB");

    var idx = new BookWalkerIndex(indexPath);
    Console.WriteLine($"  版本 v{idx.FormatVersion}（旧格式={idx.IsLegacyFormat}）  系列 {idx.Count:N0}  收录卷 {idx.VolumeTotal:N0}");

    Console.WriteLine($"  有类型标签 {idx.All.Count(s => s.Genres.Count > 0):N0}  有每卷数据 {idx.All.Count(s => s.Volumes.Count > 0):N0}  " +
                      $"有 ISBN {idx.All.Count(s => s.Volumes.Any(v => v.Isbn.Length > 0)):N0}  有出版品牌 {idx.All.Count(s => s.Imprint.Length > 0):N0}");

    // 旧格式（v1 裸数组）索引必须仍能加载，只是没有每卷/标签数据
    var legacyPath = @"D:\WorkProgram\komgabangumidocker\reference\bkwk-index.json.gz";
    if (File.Exists(legacyPath))
    {
        var old = new BookWalkerIndex(legacyPath);
        Console.WriteLine($"  v1 兼容: 条目 {old.Count:N0} 版本 v{old.FormatVersion} 旧格式={old.IsLegacyFormat} 卷={old.VolumeTotal}");
        Check("v1 旧索引仍可加载并标记为旧格式", old.Count > 0 && old.IsLegacyFormat && old.VolumeTotal == 0);
    }

    // 选一个「有类型 + 有每卷 + 有 ISBN」的系列做端到端验证
    var target = idx.All
        .Where(s => s.Volumes.Count >= 2 && s.Genres.Count > 0 && s.Volumes.Count(v => v.Isbn.Length > 0) >= 2)
        .OrderBy(s => s.Volumes.Count)
        .FirstOrDefault();
    if (target == null) { Console.WriteLine("[失败] 索引里找不到满足条件的系列"); return; }

    Console.WriteLine($"  测试系列: 《{target.Title}》 type={target.Type} 品牌={target.Imprint} 出版社={target.Publisher}");
    Console.WriteLine($"    类型=[{string.Join(",", target.Genres)}] 题材=[{string.Join(",", target.Themes.Take(6))}] complete={target.Complete} 卷数={target.VolumeCount}");
    foreach (var v in target.Volumes.Take(3))
        Console.WriteLine($"    卷 {v.Order}: 名称={v.Title} 发售={v.ReleaseDate} ISBN={v.Isbn} " +
                          $"独立封面={(v.CoverName.Length > 0 ? "有" : "无")} 独立简介={(v.Summary.Length > 0 ? v.Summary.Length + "字" : "无")}");

    var testVols = target.Volumes.OrderBy(v => v.Order).Take(3).ToList();

    // ---- 2. BookWalker 路径（mock Komga） ----
    Console.WriteLine("\n==== 2. BookWalker 路径 → mock Komga ====");
    string? seriesPatch = null;
    var bookPatches = new List<string>();
    using (var mock = new MockHttp(25690, (method, path, body) => Route(
               method, path, body, target.Title, testVols, s => seriesPatch = s, bookPatches)))
    {
        using var komga = new KomgaClient("http://127.0.0.1:25690", "k", null, null);
        using var bangumi = new BangumiClient(null, "http://127.0.0.1:25691");
        bangumi.MinRequestIntervalMs = 0;   // 本地 mock 不用礼貌节流
        var scrape = new ScrapeService(komga, bangumi, new ConsoleLogger());
        var r = await scrape.ScrapeFromBookWalkerAsync("s1", target,
            new ScrapeOptions { SyncType = SyncType.Meta, FetchVolumeData = true, DryRun = false });
        Console.WriteLine($"  结果: success={r.Success} matched={r.MatchedTitle} err={r.Error}");
    }
    Check("系列 PATCH 已发出", seriesPatch != null);
    Check("系列 PATCH 含类型 genres", seriesPatch?.Contains("\"genres\"") == true);
    Check("系列 PATCH 含题材 tags", seriesPatch?.Contains("\"tags\"") == true);
    Check("系列 PATCH 含阅读方向 RTL", seriesPatch?.Contains("RIGHT_TO_LEFT") == true);
    Check("系列 PATCH 含总卷数", seriesPatch?.Contains("\"totalBookCount\"") == true);
    Check("系列 PATCH 含首发日", seriesPatch?.Contains("\"releaseDate\"") == true);
    Check("系列 PATCH 含别名", seriesPatch?.Contains("\"alternateTitles\"") == true);
    Console.WriteLine("  系列 PATCH 内容:");
    Console.WriteLine("    " + seriesPatch);
    Console.WriteLine($"  书籍 PATCH {bookPatches.Count} 条（期望 {testVols.Count}）:");
    foreach (var b in bookPatches) Console.WriteLine("    " + b);
    Check("每卷都补了 ISBN（来自目录库）", bookPatches.Count(b => b.Contains("\"isbn\"")) == testVols.Count);
    Check("每卷都补了发售日", bookPatches.Count(b => b.Contains("\"releaseDate\"")) == testVols.Count);

    // ---- 3. Bangumi 路径 + BookWalker 补空 ----
    Console.WriteLine("\n==== 3. Bangumi 主源 + BookWalker 补 ISBN/发售日 ====");
    seriesPatch = null;
    bookPatches.Clear();
    using (var mock = new MockHttp(25692, (method, path, body) => Route(
               method, path, body, target.Title, testVols, s => seriesPatch = s, bookPatches)))
    using (var bgm = new MockHttp(25691, (method, path, body) =>
           {
               if (path.StartsWith("/v0/subjects/1/subjects")) return (200, "[]");
               if (path.StartsWith("/v0/subjects/")) return (200, bangumiSubjectJson);
               return (404, "{}");
           }))
    {
        using var komga = new KomgaClient("http://127.0.0.1:25692", "k", null, null);
        using var bangumi = new BangumiClient(null, "http://127.0.0.1:25691");
        bangumi.MinRequestIntervalMs = 0;   // 本地 mock 不用礼貌节流
        var scrape = new ScrapeService(komga, bangumi, new ConsoleLogger()) { BwIndex = idx };
        var r = await scrape.ScrapeAsync("s1", 1,
            new ScrapeOptions { SyncType = SyncType.Meta, FetchVolumeData = true, DryRun = false });
        Console.WriteLine($"  结果: success={r.Success} matched={r.MatchedTitle} err={r.Error}");
    }
    Console.WriteLine($"  书籍 PATCH {bookPatches.Count} 条:");
    foreach (var b in bookPatches) Console.WriteLine("    " + b);
    Check("Bangumi 无单行本时，仍用 BookWalker 补上了 ISBN", bookPatches.Any(b => b.Contains("\"isbn\"")));
    Check("补的 ISBN 确实来自目录库", testVols.Any(v => bookPatches.Any(b => b.Contains(v.Isbn))));
    Check("未把英文每卷简介混进 Bangumi 结果", bookPatches.All(b => !b.Contains("\"summary\"")));

    // ---- 4. 反向用例：两边册数差距过大时必须跳过（不能把「话」写到「卷」上） ----
    Console.WriteLine("\n==== 4. 卷数不匹配时的保护 ====");
    var bigSeries = idx.All.OrderByDescending(s => s.Volumes.Count).First();
    Console.WriteLine($"  目录库最大的系列: 《{bigSeries.Title}》 {bigSeries.Volumes.Count} 卷");
    seriesPatch = null;
    bookPatches.Clear();
    using (var mock = new MockHttp(25693, (method, path, body) => Route(
               method, path, body, bigSeries.Title, testVols, s => seriesPatch = s, bookPatches)))
    {
        using var komga = new KomgaClient("http://127.0.0.1:25693", "k", null, null);
        using var bangumi = new BangumiClient(null, "http://127.0.0.1:25691");
        bangumi.MinRequestIntervalMs = 0;   // 本地 mock 不用礼貌节流
        var scrape = new ScrapeService(komga, bangumi, new ConsoleLogger());
        var r = await scrape.ScrapeFromBookWalkerAsync("s1", bigSeries,
            new ScrapeOptions { SyncType = SyncType.Meta, FetchVolumeData = true, DryRun = false });
        Console.WriteLine($"  结果: success={r.Success}");
    }
    Check("卷数差距过大时没有写任何书（保护生效）", bookPatches.Count == 0);
    Check("系列级信息仍然写入", seriesPatch != null);

    // ---- 5. Komga 400 回归：totalBookCount=0 绝不能发出去 ----
    Console.WriteLine("\n==== 5. 0 值整数字段（Komga: totalBookCount must be greater than 0）====");
    string? probeBody = null;
    using (var mock = new MockHttp(25694, (method, path, body) =>
           {
               if (method == "PATCH" && path == "/api/v1/series/s1/metadata") { probeBody = body; return (204, ""); }
               return (404, "{}");
           }))
    {
        using var komga = new KomgaClient("http://127.0.0.1:25694", "k", null, null);
        await komga.PatchSeriesMetadataAsync("s1", new SeriesMetadataDto
        {
            Title = "测试",
            TotalBookCount = 0,
            AgeRating = 0,
        });
    }
    Console.WriteLine("    " + probeBody);
    Check("totalBookCount=0 被剔除（否则 Komga 返回 400）", probeBody?.Contains("totalBookCount") == false);
    Check("ageRating=0 仍然发送（0=通用，合法值）", probeBody?.Contains("\"ageRating\":0") == true);
    Check("title 正常发送", probeBody?.Contains("测试") == true);

    // ---- 6. 批量匹配的 Dry-run 必须真的一点都不写（曾漏传 DryRun → 勾了预览也 PATCH）----
    Console.WriteLine("\n==== 6. 批量匹配 Dry-run 不写库 ====");
    // Bangumi mock：搜索返回同名条目 → 精确匹配 → 走完整刮削流程
    var searchJson = "{\"data\":[{\"id\":1,\"name\":\"Test Original\",\"name_cn\":\"测试系列\"," +
                     "\"platform\":\"漫画\",\"images\":{\"medium\":\"https://x/y.jpg\"}}],\"total\":1}";

    async Task<int> RunBatch(bool dryRun, int port, List<string> log)
    {
        using var bgmMock = new MockHttp(25691, (method, path, body) =>
            path.StartsWith("/v0/search/subjects") ? (200, searchJson)
            : path.StartsWith("/v0/subjects/1/subjects") ? (200, "[]")
            : path.StartsWith("/v0/subjects/") ? (200, bangumiSubjectJson)
            : (404, "{}"));
        using var komgaMock = new MockHttp(port, (method, path, body) =>
        {
            // Komga 用 POST 做查询（/books/list、/series/list），只有 PATCH/PUT/DELETE 才算写
            if (method is "PATCH" or "PUT" or "DELETE") { log.Add($"{method} {path}"); return (204, "{}"); }
            if (path.StartsWith("/api/v1/books/list"))
                return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
            if (path.StartsWith("/api/v1/collections"))
                return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
            if (path == "/api/v1/series/s1")
                return (200, System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = "s1", name = "测试系列", libraryId = "lib1", oneshot = false, booksCount = 2,
                    metadata = new { title = "测试系列" },
                }));
            if (path.Contains("/thumbnails")) return (200, "[]");
            return (200, "{\"title\":\"测试系列\"}");
        });
        using var komga = new KomgaClient($"http://127.0.0.1:{port}", "k", null, null);
        using var bangumi = new BangumiClient(null, "http://127.0.0.1:25691");
        bangumi.MinRequestIntervalMs = 0;   // 本地 mock 不用礼貌节流
        var matcher = new BatchMatcher(komga, bangumi, new ConsoleLogger())
        {
            DryRun = dryRun,
            MatchTypePlatform = "漫画",
        };
        var s = new SeriesDto { Id = "s1", Name = "测试系列", LibraryId = "lib1" };
        var r = await matcher.PreciseMatchAsync(s, SyncType.Meta, false, new BatchStats());
        Console.WriteLine($"  DryRun={dryRun}: success={r.Success} matched={r.MatchedTitle} 写请求={log.Count}");
        return r.Success ? log.Count : -1;
    }

    var dryWrites = new List<string>();
    var dryCount = await RunBatch(true, 25696, dryWrites);
    foreach (var w in dryWrites) Console.WriteLine("    " + w);
    Check("Dry-run 时流程跑通且 0 个写请求", dryCount == 0);
    var realWrites = new List<string>();
    var realCount = await RunBatch(false, 25697, realWrites);
    foreach (var w in realWrites) Console.WriteLine("    " + w);
    Check("关掉 Dry-run 时确实会写（证明上面的 0 不是假阳性）", realCount > 0);

    // ---- 7. 代理配置：解析 / 校验 / 自愈（不需要网络）----
    // ---- 6b. 并行批量：3 个系列同时刮削（2 个已有链接→跳过，1 个新刮削）----
    // 验证：① Dry-run 依旧 0 写；② 真实模式写请求恰好 1 个（只有新系列被 PATCH）；
    //       ③ 系列 metadata 每个只 GET 一次（批量入口拿过的数据不再重复请求）；④ 搜索只发 1 次
    Console.WriteLine("\n==== 6b. 并行批量 + 请求去重 ====");

    async Task<(int Writes, int MetaGets, int Searches)> RunBatch3Async(bool dryRun, int komgaPort, List<string> writes)
    {
        using var bgmMock = new MockHttp(25691, (method, path, body) =>
            path.StartsWith("/v0/search/subjects") ? (200, searchJson)
            : path.StartsWith("/v0/subjects/1/subjects") ? (200, "[]")
            : path.StartsWith("/v0/subjects/") ? (200, bangumiSubjectJson)
            : (404, "{}"));
        using var komgaMock = new MockHttp(komgaPort, (method, path, body) =>
        {
            if (method is "PATCH" or "PUT" or "DELETE") { writes.Add($"{method} {path}"); return (204, "{}"); }
            if (path.StartsWith("/api/v1/books/list"))
                return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
            if (path.StartsWith("/api/v1/collections"))
                return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
            if (path.StartsWith("/api/v1/series/") && path.EndsWith("/metadata"))
                return (200, path.Contains("s1") || path.Contains("s2")
                    ? "{\"title\":\"已链接系列\",\"links\":[{\"label\":\"Btv\",\"url\":\"https://bgm.tv/subject/12345\"}]}"
                    : "{\"title\":\"测试系列\"}");
            if (path.Contains("/thumbnails")) return (200, "[]");
            if (path.StartsWith("/api/v1/series/"))
                return (200, "{\"id\":\"s3\",\"name\":\"测试系列\",\"libraryId\":\"lib1\"," +
                             "\"oneshot\":false,\"booksCount\":0,\"metadata\":{\"title\":\"测试系列\"}}");
            return (200, "{}");
        });
        using var komga = new KomgaClient($"http://127.0.0.1:{komgaPort}", "k", null, null);
        using var bangumi = new BangumiClient(null, "http://127.0.0.1:25691");
        bangumi.MinRequestIntervalMs = 0;
        var matcher = new BatchMatcher(komga, bangumi, new ConsoleLogger())
        {
            DryRun = dryRun,
            MatchTypePlatform = "漫画",
            SeriesConcurrency = 3,
        };
        var list = new List<SeriesDto>
        {
            new() { Id = "s1", Name = "已链接系列一", LibraryId = "lib1" },
            new() { Id = "s2", Name = "已链接系列二", LibraryId = "lib1" },
            new() { Id = "s3", Name = "测试系列", LibraryId = "lib1" },
        };
        var st = await matcher.BatchMatchAsync(list, SyncType.Meta, false);
        Console.WriteLine($"  DryRun={dryRun}: 成功 {st.SuccessCount} 跳过 {st.SkippedCount} 失败 {st.FailureCount}，写请求={writes.Count}");
        return (writes.Count, komgaMock.Count("GET", "/metadata"), bgmMock.Count("POST", "/v0/search"));
    }

    var p3Writes = new List<string>();
    var (p3DryWrites, p3DryMetaGets, p3DrySearches) = await RunBatch3Async(true, 25695, p3Writes);
    Check("并行批量 Dry-run 仍然 0 个写请求", p3DryWrites == 0);
    Check("并行批量 Dry-run 请求去重：metadata 每系列只 GET 一次（3 系列=3 次）", p3DryMetaGets == 3);
    Check("已链接的系列不发起 Bangumi 搜索（只有未链接的 1 个搜索）", p3DrySearches == 1);

    var p3RealWrites = new List<string>();
    var (p3RealWrites2, p3RealMetaGets, _) = await RunBatch3Async(false, 25696, p3RealWrites);
    foreach (var w in p3RealWrites) Console.WriteLine("    " + w);
    Check("并行批量真实模式恰好 1 个写请求（只有新系列被 PATCH）", p3RealWrites2 == 1 && p3RealWrites[0].Contains("s3"));
    Check("并行批量真实模式同样去重（3 系列=3 次 metadata GET）", p3RealMetaGets == 3);

    // ---- 6c. Bangumi 礼貌节流：并发 4 个请求仍按最小间隔排开 ----
    Console.WriteLine("\n==== 6c. Bangumi 节流 ====");
    using (var paceMock = new MockHttp(25698, (_, _, _) => (200, searchJson)))
    using (var paceClient = new BangumiClient(null, "http://127.0.0.1:25698"))
    {
        paceClient.MinRequestIntervalMs = 300;
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => paceClient.SearchSubjectsAsync("测试", "漫画", 0)));
        sw2.Stop();
        var ticks = paceMock.CallTicks;
        var minGap = ticks.Count < 2
            ? long.MaxValue
            : ticks.Zip(ticks.Skip(1), (a, b) => b - a).Min();
        Console.WriteLine($"  4 个并发请求 {sw2.ElapsedMilliseconds}ms 完成，最小到达间隔 {minGap}ms");
        Check("并发 4 个请求仍按 ≥300ms 间隔排开（节流生效，批量并发不超速）",
              minGap >= 250 && ticks.Count == 4);
    }

    Console.WriteLine("\n==== 7. 代理配置解析与自愈 ====");
    (string In, string Scheme, string Host, int Port)[] pcases =
    [
        ("127.0.0.1:7890", "http", "127.0.0.1", 7890),
        ("http://127.0.0.1:7890", "http", "127.0.0.1", 7890),
        ("http:127.0.0.1:7890", "http", "127.0.0.1", 7890),      // 漏了 //（真实用户配置）
        ("socks5://127.0.0.1:1080", "socks5", "127.0.0.1", 1080),
        ("socks5:127.0.0.1:1080", "socks5", "127.0.0.1", 1080),
        ("socks4://[::1]:1080", "socks4", "::1", 1080),
        ("https://proxy.example.com:8443", "https", "proxy.example.com", 8443),
        ("proxy.example.com", "http", "proxy.example.com", 7890),
    ];
    var bad = 0;
    foreach (var c in pcases)
    {
        var (s, h, p, _, _) = ProxyProtocols.Parse(c.In);
        if (s != c.Scheme || h != c.Host || p != c.Port) { bad++; Console.WriteLine($"    FAIL {c.In} → {s}://{h}:{p}"); }
    }
    Check("代理字符串解析（含 socks4/5、IPv6、漏写 // 的写法）", bad == 0);
    Check("IPv6 组装时自动补方括号", ProxyProtocols.Build("socks5", "::1", 1080) == "socks5://[::1]:1080");
    Check("校验能挡住把 http:// 填进地址栏", ProxyProtocols.Validate("http", "http:127.0.0.1", 7890) != null);
    Check("校验接受正常主机名", ProxyProtocols.Validate("http", "proxy.example.com", 7890) == null);
    Check("不使用代理时永远合法", ProxyProtocols.Validate("", "", 0) == null);

    var broken = new AppSettings { LegacyBangumiProxy = "http:127.0.0.1:7890" };
    broken.MigrateLegacyProxy();
    Check("旧版单字段代理能迁移", broken.ProxyUrl == "http://127.0.0.1:7890");
    var corrupted = new AppSettings { ProxyScheme = "http", ProxyHost = "http:127.0.0.1", ProxyPort = 7890 };
    corrupted.MigrateLegacyProxy();
    Check("被写坏的主机字段能自愈（否则启动即崩）", corrupted.ProxyUrl == "http://127.0.0.1:7890");
    var pastedUrl = new AppSettings { ProxyScheme = "http", ProxyHost = "http://127.0.0.1:7890", ProxyPort = 7890 };
    pastedUrl.MigrateLegacyProxy();
    Check("整条 URL 粘进地址栏也能自愈", pastedUrl.ProxyUrl == "http://127.0.0.1:7890");
    var junkPort = new AppSettings { ProxyScheme = "http", ProxyHost = "127.0.0.1", ProxyPort = 99999 };
    junkPort.MigrateLegacyProxy();
    Check("端口越界被夹回默认值", junkPort.ProxyUrl == "http://127.0.0.1:7890");
    var urls = new[] { broken.ProxyUrl, corrupted.ProxyUrl, pastedUrl.ProxyUrl, "socks5://[::1]:1080" };
    var ctorOk = true;
    foreach (var u in urls)
    {
        try { _ = new System.Net.WebProxy(u!); } catch { ctorOk = false; Console.WriteLine($"    WebProxy 构造失败: {u}"); }
    }
    Check("组装出的代理 URL 都能被 WebProxy 接受", ctorOk);

    Console.WriteLine("\n==== 完成 ====");
}

// AI 匹配 + 简介翻译测试（本地 mock /v1/chat/completions，无需真实 API Key）
async Task TestAiAsync()
{
    var logger = new ConsoleLogger();
    var aiFail = 0;
    void Check(string what, bool ok)
    {
        if (!ok) aiFail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }
    var failMode = false;
    var unknownMode = false;
    using var mock = new MockHttp(25697, (path, body) =>
    {
        if (path != "/v1/chat/completions") return (404, "{}");
        if (failMode) return (500, "{\"error\":\"boom\"}");
        // 注意：请求体里的中文会被 System.Text.Json 转义成 \uXXXX，
        // 所以用 ASCII 关键词 "index" 区分「选择候选」，"日文"（\u65e5\u6587）区分「标题译日文」，
        // "confident" 区分「识别作品」，"UNKNOWN" 区分「补写简介」；
        // 其余请求都当「简介翻译」处理
        if (body.Contains("confident", StringComparison.Ordinal))
            return (200, "{\"choices\":[{\"message\":{\"content\":\"{\\\"jp\\\":\\\"ワンピース\\\",\\\"cn\\\":\\\"海贼王\\\",\\\"en\\\":\\\"ONE PIECE\\\",\\\"confident\\\":true}\"}}]}");
        if (unknownMode)
            return (200, "{\"choices\":[{\"message\":{\"content\":\"UNKNOWN\"}}]}");
        if (body.Contains("UNKNOWN", StringComparison.Ordinal))
            return (200, "{\"choices\":[{\"message\":{\"content\":\"这部作品讲述了一位少年的冒险故事，友情与成长交织的热血物语，画风细腻，剧情节奏紧凑。\"}}]}");
        if (body.Contains("index", StringComparison.Ordinal))
            return (200, "{\"choices\":[{\"message\":{\"content\":\"{\\\"index\\\": 2}\"}}]}");
        if (body.Contains("\\u65e5\\u6587", StringComparison.OrdinalIgnoreCase))
            return (200, "{\"choices\":[{\"message\":{\"content\":\"\u30ef\u30f3\u30d4\u30fc\u30b9\"}}]}");
        // 故意套上 ``` 代码块和"译文："前缀，验证清洗逻辑
        return (200, "{\"choices\":[{\"message\":{\"content\":\"```\\n译文：这是翻译后的中文简介。\\n```\"}}]}");
    });

    using var ai = new AiClient("http://127.0.0.1:25697", "test-key", "mock-model");

    Console.WriteLine("---- 1. NeedsChineseTranslation 判定 ----");
    foreach (var (t, expect) in new (string, bool)[]
             {
                 ("これは日本語のあらすじです。", true),
                 ("This is an English description of the series.", true),
                 ("这是一段中文简介，不需要翻译。", false),
                 ("", false),
                 ("12.5%!!", false),
                 ("ワンピース 第1巻", true),
             })
    {
        var got = AiClient.NeedsChineseTranslation(t);
        Console.WriteLine($"  [{(got == expect ? "OK" : "FAIL")}] '{t}' => {got}（期望 {expect}）");
    }

    Console.WriteLine("---- 2. CleanTranslation 清洗 ----");
    Console.WriteLine($"  => '{AiClient.CleanTranslation("```\n译文：这是翻译后的中文简介。\n```")}'");
    Console.WriteLine($"  => '{AiClient.CleanTranslation("\"带引号的译文\"")}'");

    Console.WriteLine("---- 3. AI 选择候选（PickBestAsync） ----");
    var cands = new List<SearchCandidate>
    {
        new(1, "海贼王", "ONE PIECE", "尾田荣一郎", "", null),
        new(2, "航海王", "ONE PIECE", "尾田荣一郎", "", null),
        new(3, "火影忍者", "NARUTO", "岸本齐史", "", null),
    };
    var pick = await ai.PickBestAsync("航海王", cands);
    Console.WriteLine($"  选中 => #{pick?.Id} 《{pick?.Title}》（期望 #2）  请求数={mock.Requests}");

    Console.WriteLine("---- 4. 翻译（走 mock 接口 + 清洗） ----");
    var before = mock.Requests;
    var ja = "これは日本語のあらすじです。";
    var translated = await ai.TranslateToChineseAsync(ja);
    Console.WriteLine($"  日文 => '{translated}'  请求数 +{mock.Requests - before}");

    Console.WriteLine("---- 5. 已是中文则不调接口 ----");
    before = mock.Requests;
    var zh = "这是一段中文简介，不需要翻译。";
    var same = await ai.TranslateToChineseAsync(zh);
    Console.WriteLine($"  中文 => '{same}'  请求数 +{mock.Requests - before}（期望 +0）");

    Console.WriteLine("---- 6. ScrapeService 接线（开关 / 失败兜底） ----");
    using var komgaDummy = new KomgaClient("http://127.0.0.1:1", "k", null, null);
    using var bangumiDummy = new BangumiClient(null, "http://127.0.0.1:1");
    var svc = new ScrapeService(komgaDummy, bangumiDummy, logger);

    // 6a. 开关关闭 → 原样返回，不调接口
    before = mock.Requests;
    var off = await svc.TranslateIfNeededAsync(ja, "测试");
    Console.WriteLine($"  开关关闭 => '{off}'  请求数 +{mock.Requests - before}（期望 +0，且为日文原文）");

    // 6b. 开关打开 → 翻译
    svc.Ai = ai;
    svc.TranslateSummary = true;
    var on = await svc.TranslateIfNeededAsync(ja, "测试");
    Console.WriteLine($"  开关打开 => '{on}'");

    // 6c. 接口 500 → 保留原文且不抛异常
    failMode = true;
    var fallback = await svc.TranslateIfNeededAsync(ja, "测试");
    Console.WriteLine($"  接口报错 => '{fallback}'（期望日文原文，不抛异常）");

    // 6d. 已是中文 → 不调接口
    failMode = false;
    before = mock.Requests;
    var zh2 = await svc.TranslateIfNeededAsync(zh, "测试");
    Console.WriteLine($"  中文简介 => '{zh2}'  请求数 +{mock.Requests - before}（期望 +0）");

    // 7. 标题译成日文原名（供 BookWalker 目录匹配：中文标题 → 日文标题）
    Console.WriteLine("---- 7. 标题译日文（BookWalker 匹配用） ----");
    before = mock.Requests;
    var jpTitle = await ai.TranslateTitleToJapaneseAsync("海贼王");
    Console.WriteLine($"  海贼王 => '{jpTitle}'（期望 ワンピース）  请求数 +{mock.Requests - before}（期望 +1）");
    var jpAgain = await ai.TranslateTitleToJapaneseAsync("海贼王");
    Console.WriteLine($"  第二次 => '{jpAgain}'  请求数 +{mock.Requests - before}（缓存命中，期望仍 +1）");
    var noHan = await ai.TranslateTitleToJapaneseAsync("ワンピース");
    Console.WriteLine($"  已是日文 => '{noHan}'（期望空，不调接口）");
    var latin = await ai.TranslateTitleToJapaneseAsync("Berserk");
    Console.WriteLine($"  纯英文 => '{latin}'（期望空，不调接口）");

    // 8. AI 识别作品（IdentifySeriesAsync：一次拿到 jp/cn/en 三语名字，供三源重搜）
    Console.WriteLine("---- 8. AI 识别作品（匹配回退主路径） ----");
    before = mock.Requests;
    var id1 = await ai.IdentifySeriesAsync("[测试][测试作品名][完]", ["测试作品名"]);
    Console.WriteLine($"  jp='{id1?.Jp}' cn='{id1?.Cn}' en='{id1?.En}' confident={id1?.Confident}");
    Check("jp/cn/en 都拿到", id1?.Jp == "ワンピース" && id1?.Cn == "海贼王" && id1?.En == "ONE PIECE");
    Check("confident=true", id1?.Confident == true);
    Check("只调了一次接口", mock.Requests == before + 1);
    before = mock.Requests;
    _ = await ai.IdentifySeriesAsync("[测试][测试作品名][完]", ["测试作品名"]);
    Check("缓存命中不重复调", mock.Requests == before);
    Check("空输入 → null", await ai.IdentifySeriesAsync("", []) == null);

    // 9. AI 补写简介（各源都没有简介时的兜底；AI 认不出的作品保持空，宁缺毋滥）
    Console.WriteLine("---- 9. AI 补写简介 ----");
    before = mock.Requests;
    var syn = await ai.GenerateSynopsisAsync("测试补写简介的作品", "テスト原作", "测试作者");
    Console.WriteLine($"  => '{syn}'");
    var afterFirstSyn = mock.Requests;
    Check("简介生成（>=20 字）", syn != null && syn.Length >= 20);
    Check("补写只调一次接口", afterFirstSyn == before + 1);
    _ = await ai.GenerateSynopsisAsync("测试补写简介的作品", "テスト原作", "测试作者");
    Check("简介缓存命中（不重复调）", mock.Requests == afterFirstSyn);
    unknownMode = true;
    var synUnknown = await ai.GenerateSynopsisAsync("AI不认识的作品", null, null);
    unknownMode = false;
    Check("AI 认不出 → null（宁缺毋滥）", synUnknown == null);
    Console.WriteLine(aiFail == 0 ? "  => aitest 全部通过" : $"  => aitest {aiFail} FAIL");
    if (aiFail > 0) Environment.ExitCode = 1;
}

// 第三源 Mox（bookof.moe）+ 纠正匹配 + 小封面不再被拒收（全 mock，无需外网）
async Task TestMoxAsync()
{
    var fail = 0;
    void Check(string what, bool ok)
    {
        if (!ok) fail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }

    // ---- mock Mox (bookof.moe) ----
    const string moxList =
        "<html><script>\n" +
        "datainfo-B=漫畫,9001,測試系列傳說,測試作者,20240101,http://127.0.0.1:25700/pic/B/b1.jpg!cover_l?sign=a-1-2\", \"*\" );\n" +
        "datainfo-B=漫畫,9002,另一部作品,其他作者,20240102,http://127.0.0.1:25700/pic/B/b2.jpg!cover_l?sign=a-3-4\", \"*\" );\n" +
        "</script></html>";
    const string moxDetail9001 =
        "<html><head><script>window.iframe_action.location.href = \"data_vol.php?tid=9001\";</script></head>" +
        "<body><table><td class=\"author\">" +
        "<img id=\"logo_r18\" style=\"display:none\" src=\"/pic/r18.png\">" +
        "<span class=\"name_main\">測試系列傳說</span>" +
        "<span class=\"name_subt\">(Test Series Legend) 測試系列傳說</span>" +
        "<span class=\"name_subt\">狀態：連載 分類：少年 愛情 版本：東立 最後出版：2024-01-05</span>" +
        "<div id=\"div_desctext\">這是<b>測試</b>簡介第一行<br>第二行【廣告內容】</div>" +
        "<a href=\"https://bookof.moe/s/AUT/123.htm\">測試作者</a>" +
        "</td></table></body></html>";
    const string moxDetail9002 =
        "<html><body><td class=\"author\">" +
        "<img id=\"logo_r18\" src=\"/pic/r18.png\">" +
        "<span class=\"name_main\">另一部作品</span>" +
        "</td></body></html>";
    const string moxVols =
        "<script>\n" +
        "datainfo-V=1,Vol.1,漫畫,0,http://127.0.0.1:25700/pic/V/v1.jpg,0\n" +
        "datainfo-V=2,Vol.2,漫畫,0,/pic/V/v2.jpg,0\n" +
        "</script>";

    using var moxMock = new MockHttp(25700, (method, path, _) =>
        path.StartsWith("/data_list.php") ? (200, moxList)
        : path.Contains("/b/9001.htm") ? (200, moxDetail9001)
        : path.Contains("/b/9002.htm") ? (200, moxDetail9002)
        : path.Contains("/data_vol.php") ? (200, moxVols)
        : path.StartsWith("/pic/") ? (200, "FAKE-JPEG-BYTES")
        : (404, "{}"));
    var mox = new MoxClient(baseUrl: "http://127.0.0.1:25700", log: new ConsoleLogger());
    mox.MinRequestIntervalMs = 0;

    Console.WriteLine("==== 1. Mox 搜索解析 ====");
    var moxResults = await mox.SearchAsync("测试系列传说", 8);
    Console.WriteLine($"  {string.Join(" | ", moxResults.Select(r => $"#{r.Id}《{r.Title}》{r.Author} 封面:{r.CoverUrl}"))}");
    Check("搜索返回 2 条", moxResults.Count == 2);
    Check("繁体站结果转成简体", moxResults[0].Title == "测试系列传说");
    Check("作者转成简体", moxResults[0].Author == "测试作者");
    Check("搜索行自带封面 URL（剔除 JS 残片）",
          moxResults[0].CoverUrl == "http://127.0.0.1:25700/pic/B/b1.jpg!cover_l?sign=a-1-2");

    Console.WriteLine("==== 1.5 源站 520 自动重试 ====");
    var retryAttempts = 0;
    using (var retryMock = new MockHttp(25704, (method, path, _) =>
    {
        if (path.StartsWith("/data_list.php"))
        {
            var n = Interlocked.Increment(ref retryAttempts);
            return n == 1 ? (520, "cloudflare origin error") : (200, moxList);
        }
        return (404, "{}");
    }))
    {
        var moxRetry = new MoxClient(baseUrl: "http://127.0.0.1:25704", log: new ConsoleLogger());
        moxRetry.MinRequestIntervalMs = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var retryResults = await moxRetry.SearchAsync("测试系列传说", 8);
        sw.Stop();
        Console.WriteLine($"  请求 {retryAttempts} 次，耗时 {sw.Elapsed.TotalSeconds:F1}s");
        Check("520 后重试拿到结果", retryResults.Count == 2 && retryAttempts == 2);
        Check("重试带退避（约 1.5s）", sw.Elapsed.TotalSeconds > 1.0);
    }

    Console.WriteLine("==== 2. Mox 详情解析 ====");
    var d1 = await mox.GetSeriesAsync("9001");
    Console.WriteLine($"  title={d1.Title} ori={d1.OriTitle} eng={d1.EngName} status={d1.Status} pub={d1.Publisher} adult={d1.IsAdult}");
    Console.WriteLine($"  authors=[{string.Join(",", d1.Authors)}] tags=[{string.Join(",", d1.Tags)}]");
    Console.WriteLine($"  summary={d1.Summary.Replace("\n", "\\n")}");
    Check("主标题转简体", d1.Title == "测试系列传说");
    Check("原名保留繁体", d1.OriTitle == "測試系列傳說");
    Check("英文名提取", d1.EngName == "Test Series Legend");
    Check("狀態：連載 → ONGOING", d1.Status == "ONGOING");
    Check("版本：東立 → 东立", d1.Publisher == "东立");
    Check("分類解析成 tags 且不混入版本/日期", d1.Tags.Contains("少年") && d1.Tags.Contains("爱情") &&
                                                !d1.Tags.Any(t => t.Contains("版本") || t.Contains("出版")));
    Check("隐藏的 r18 图标 → 非成人", d1.IsAdult == false);
    Check("作者（去掉括注）", d1.Authors.Count == 1 && d1.Authors[0] == "测试作者");
    Check("简介转简体且去掉尾部【广告内容】", d1.Summary.Contains("这是测试简介第一行") && !d1.Summary.Contains("广告"));
    Check("Bof/Mox 链接", d1.BoFUrl == "https://bookof.moe/b/9001.htm" && d1.MoxUrl == "https://kox.moe/c/9001.htm");
    var d2 = await mox.GetSeriesAsync("9002");
    Check("显示的 r18 图标 → 成人", d2.IsAdult);

    Console.WriteLine("==== 3. 卷封面解析 ====");
    var covers = await mox.GetVolumeCoverUrlsAsync(d1.CoverFrameUrl);
    Console.WriteLine($"  {string.Join("\n  ", covers)}");
    Check("解析出 2 张封面", covers.Count == 2);
    Check("绝对 URL 保留原样", covers[0] == "http://127.0.0.1:25700/pic/V/v1.jpg");
    Check("相对 URL 补上站点基址", covers[1] == "http://127.0.0.1:25700/pic/V/v2.jpg");

    Console.WriteLine("\n==== 4. ScrapeFromMoxAsync → mock Komga ====");
    string? seriesPatch = null;
    var bookPatches = new List<string>();
    var coverPosts = 0;
    using (var komgaMock = new MockHttp(25701, (method, path, body) =>
    {
        if (method == "GET" && path == "/api/v1/series/s1")
            return (200, "{\"id\":\"s1\",\"name\":\"Vol test\",\"libraryId\":\"lib1\",\"oneshot\":false,\"booksCount\":2," +
                         "\"metadata\":{\"title\":\"测试系列\"}}");
        if (method == "GET" && path == "/api/v1/series/s1/metadata") return (200, "{}");
        if (method == "PATCH" && path == "/api/v1/series/s1/metadata") { seriesPatch = body; return (204, ""); }
        if (method == "POST" && path.StartsWith("/api/v1/books/list"))
            return (200, "{\"content\":[" +
                         "{\"id\":\"b1\",\"name\":\"Vol.01.cbz\",\"number\":1,\"seriesId\":\"s1\",\"metadata\":{\"title\":null}}," +
                         "{\"id\":\"b2\",\"name\":\"Vol.02.cbz\",\"number\":2,\"seriesId\":\"s1\",\"metadata\":{\"title\":null}}]," +
                         "\"totalElements\":2,\"numberOfElements\":2}");
        if (method == "PATCH" && path.StartsWith("/api/v1/books/")) { bookPatches.Add(body); return (204, ""); }
        if (method == "GET" && path.StartsWith("/api/v1/collections"))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        if (method == "POST" && path.StartsWith("/api/v1/collections"))
            return (200, "{\"id\":\"c1\",\"name\":\"手动匹配\",\"seriesIds\":[]}");
        if (method == "PATCH" && path.StartsWith("/api/v1/collections")) return (204, "");
        if (method == "GET" && path.Contains("/thumbnails")) return (200, "[]");
        if (method == "POST" && path.Contains("/thumbnails")) { Interlocked.Increment(ref coverPosts); return (204, ""); }
        return (404, "{}");
    }))
    {
        using var komga = new KomgaClient("http://127.0.0.1:25701", "k", null, null);
        using var bangumi = new BangumiClient(null, "http://127.0.0.1:25702");
        bangumi.MinRequestIntervalMs = 0;
        var statePath = Path.Combine(AppContext.BaseDirectory, $"state-test-{Guid.NewGuid():N}.json");   // Temp 沙箱内不可写，用 bin 目录
        var state = new ScrapeStateStore(statePath);
        var scrape = new ScrapeService(komga, bangumi, new ConsoleLogger()) { Mox = mox, State = state };
        var known = new SeriesDto
        {
            Id = "s1", Name = "测试系列传说", LibraryId = "lib1",
            Metadata = new SeriesMetadataDto { Title = "测试系列" },
        };
        var r = await scrape.ScrapeFromMoxAsync("s1", d1,
            new ScrapeOptions { SyncType = SyncType.All, FetchVolumeData = true, DryRun = false }, known, null);
        Console.WriteLine($"  结果: success={r.Success} matched={r.MatchedTitle} err={r.Error}");
        Console.WriteLine("  系列 PATCH: " + seriesPatch);
        Check("刮削成功", r.Success);
        Check("补上中文标题", seriesPatch?.Contains("测试系列传说") == true);
        Check("PATCH 含 Bof 链接", seriesPatch?.Contains("bookof.moe/b/9001.htm") == true);
        Check("PATCH 含 Mox 链接", seriesPatch?.Contains("kox.moe/c/9001.htm") == true);
        Check("简介写入（中文无需翻译）", seriesPatch?.Contains("这是测试简介第一行") == true);
        Check("出版社写入", seriesPatch?.Contains("东立") == true);
        Check("状态写入", seriesPatch?.Contains("ONGOING") == true);
        Check("作者写入", seriesPatch?.Contains("测试作者") == true);
        Check("原名 alternateTitle 写入", seriesPatch?.Contains("測試系列傳說") == true);
        Check("题材 tags 写入", seriesPatch?.Contains("少年") == true);
        Check("系列封面已上传（POST thumbnails）", coverPosts >= 1);
        Check("两卷封面都已上传", coverPosts >= 3);
        Check("状态库记下封面已刮", state.GetCover("s1") == true);
        Check("状态库记下简介已刮", state.IsSummaryKnown("s1"));
        state.SaveNow();
        var state2 = new ScrapeStateStore(statePath);
        Check("状态库持久化后重载一致", state2.GetCover("s1") == true && state2.IsSummaryKnown("s1"));
        Console.WriteLine($"  书籍 PATCH {bookPatches.Count} 条（Mox 不提供 ISBN/发售日，预期不写元数据）");

        var postsBefore = coverPosts;
        var patchBefore = seriesPatch;
        var dry = await scrape.ScrapeFromMoxAsync("s1", d1,
            new ScrapeOptions { SyncType = SyncType.All, FetchVolumeData = true, DryRun = true }, known, null);
        Console.WriteLine($"  Dry-run: success={dry.Success} 新增写请求={coverPosts - postsBefore}");
        Check("Dry-run 0 个写请求", coverPosts == postsBefore && seriesPatch == patchBefore);
        try { File.Delete(statePath); } catch { }
    }

    // ---- 纠正匹配：ReplaceMatch 让新链接/标题顶替旧匹配 ----
    Console.WriteLine("\n==== 5. 纠正匹配（ReplaceMatch） ====");
    string? replaceBody = null;
    const string bangumiSubjectJson =
        "{\"id\":1,\"name\":\"Test Original\",\"name_cn\":\"新正确标题\",\"platform\":\"漫画\"," +
        "\"summary\":\"新的正确简介。\",\"volumes\":2,\"nsfw\":false,\"age_rating\":0,\"tags\":[]}";
    using (var bgmMock = new MockHttp(25702, (method, path, _) =>
           {
               if (path.StartsWith("/v0/subjects/1/subjects")) return (200, "[]");
               if (path.StartsWith("/v0/subjects/")) return (200, bangumiSubjectJson);
               return (404, "{}");
           }))
    using (var komgaMock = new MockHttp(25703, (method, path, body) =>
    {
        if (method == "PATCH" && path == "/api/v1/series/s1/metadata") { replaceBody = body; return (204, ""); }
        if (method == "GET" && path.StartsWith("/api/v1/collections"))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        if (method == "POST" && path.StartsWith("/api/v1/collections"))
            return (200, "{\"id\":\"c1\",\"name\":\"手动匹配\",\"seriesIds\":[]}");
        if (method == "PATCH" && path.StartsWith("/api/v1/collections")) return (204, "");
        if (method == "GET" && path.Contains("/thumbnails")) return (200, "[]");
        if (method == "POST" && path.Contains("/thumbnails")) return (204, "");
        if (method == "POST" && path.StartsWith("/api/v1/books/list"))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        return (200, "{}");
    }))
    {
        using var komga = new KomgaClient("http://127.0.0.1:25703", "k", null, null);
        using var bangumi = new BangumiClient(null, "http://127.0.0.1:25702");
        bangumi.MinRequestIntervalMs = 0;
        var scrape = new ScrapeService(komga, bangumi, new ConsoleLogger());
        var wrong = new SeriesDto
        {
            Id = "s1", Name = "旧错误标题", LibraryId = "lib1",
            Metadata = new SeriesMetadataDto
            {
                Title = "旧错误标题", TitleLock = true, Tags = ["旧标签"],
                Links = [new LinkDto { Label = "Btv", Url = "https://bangumi.tv/subject/999" }],
            },
        };

        // 5a. 带纠正：新 subject 的链接/标题生效，旧 Btv 链接消失，旧锁不再压制
        var ra = await scrape.ScrapeAsync("s1", 1,
            new ScrapeOptions { SyncType = SyncType.Meta, DryRun = false, ReplaceMatch = true }, wrong, null);
        Console.WriteLine("  ReplaceMatch PATCH: " + replaceBody);
        Check("纠正刮削成功", ra.Success);
        Check("新 Btv 链接写入（subject/1）", replaceBody?.Contains("subject/1") == true);
        Check("旧错误链接被剔除（subject/999）", replaceBody?.Contains("subject/999") == false);
        Check("旧 TitleLock 被解锁、新标题写入", replaceBody?.Contains("\"title\":\"新正确标题\"") == true);
        Check("旧 tags 保留（合并语义不变）", replaceBody?.Contains("旧标签") == true);

        // 5b. 对照（不纠正）：锁仍尊重（title 不重写），但新-first 合并也换掉了旧链接
        replaceBody = null;
        _ = await scrape.ScrapeAsync("s1", 1,
            new ScrapeOptions { SyncType = SyncType.Meta, DryRun = false }, wrong, null);
        Console.WriteLine("  默认 PATCH: " + replaceBody);
        Check("不纠正时 title 仍被锁压制（回归基线）", replaceBody?.Contains("\"title\":\"新正确标题\"") == false);
        Check("但新链接同样会顶替旧链接（合并顺序修复）",
              replaceBody?.Contains("subject/1") == true && replaceBody?.Contains("subject/999") == false);
    }

    // ---- 小封面不再被拒收（原来 <60KB/30KB 直接跳过）----
    Console.WriteLine("\n==== 6. 小封面不再被拒收 ====");
    var smallCoverPosts = 0;
    const string smallCoverSubjectJson =
        "{\"id\":1,\"name\":\"Test Original\",\"name_cn\":\"小封面测试\",\"platform\":\"漫画\"," +
        "\"summary\":\"小封面测试简介。\",\"volumes\":1,\"nsfw\":false,\"age_rating\":0,\"tags\":[]," +
        "\"image\":\"http://127.0.0.1:25702/img.jpg\"}";
    var smallImg = new string('X', 2048);
    using (var bgmMock = new MockHttp(25702, (method, path, _) =>
           {
               if (path.StartsWith("/v0/subjects/1/subjects")) return (200, "[]");
               if (path.StartsWith("/v0/subjects/")) return (200, smallCoverSubjectJson);
               if (path.StartsWith("/img.jpg")) return (200, smallImg);
               return (404, "{}");
           }))
    using (var komgaMock = new MockHttp(25703, (method, path, body) =>
    {
        if (method == "PATCH" && path.StartsWith("/api/v1/series/")) return (204, "");
        if (method == "GET" && path.StartsWith("/api/v1/collections"))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        if (method == "POST" && path.StartsWith("/api/v1/collections"))
            return (200, "{\"id\":\"c1\",\"name\":\"手动匹配\",\"seriesIds\":[]}");
        if (method == "PATCH" && path.StartsWith("/api/v1/collections")) return (204, "");
        if (method == "GET" && path.Contains("/thumbnails")) return (200, "[]");
        if (method == "POST" && path.Contains("/thumbnails")) { Interlocked.Increment(ref smallCoverPosts); return (204, ""); }
        if (method == "POST" && path.StartsWith("/api/v1/books/list"))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        return (200, "{}");
    }))
    {
        using var komga = new KomgaClient("http://127.0.0.1:25703", "k", null, null);
        using var bangumi = new BangumiClient(null, "http://127.0.0.1:25702");
        bangumi.MinRequestIntervalMs = 0;
        var scrape = new ScrapeService(komga, bangumi, new ConsoleLogger());
        var known = new SeriesDto
        {
            Id = "s2", Name = "小封面测试", LibraryId = "lib1",
            Metadata = new SeriesMetadataDto { Title = "小封面测试" },
        };
        var r = await scrape.ScrapeAsync("s2", 1,
            new ScrapeOptions { SyncType = SyncType.All, DryRun = false }, known, null);
        Console.WriteLine($"  结果: success={r.Success} 封面上传 {smallCoverPosts} 次（期望 1：2KB 也能传）");
        Check("小封面(2KB)也上传成功", smallCoverPosts == 1);
    }

    Console.WriteLine($"\n==== moxtest 完成：{fail} FAIL ====");
    if (fail > 0) Environment.ExitCode = 1;
}

// BookWalker「导入本地文件」流程测试：sqlite / zst / 非法文件 三种输入
async Task TestBookWalkerImport()
{
    var logger = new ConsoleLogger();
    var svc = new BookWalkerService(logger);
    var refDb = @"D:\WorkProgram\komgabangumidocker\reference\bkwk-db.sqlite";
    var work = @"D:\WorkProgram\komgabangumidocker\bwtest";
    Directory.CreateDirectory(work);
    var idxFromSqlite = Path.Combine(work, "idx-from-sqlite.json.gz");
    var idxFromZst = Path.Combine(work, "idx-from-zst.json.gz");
    var dbOut = Path.Combine(work, "bkwk-db.sqlite");
    if (!File.Exists(refDb))
    {
        Console.WriteLine($"[跳过] 未找到测试 sqlite: {refDb}");
        return;
    }

    // A. 直接导入已解压的 sqlite
    await svc.ImportLocalAsync(refDb, dbOut, idxFromSqlite);
    var bwA = new BookWalkerIndex(idxFromSqlite);
    Console.WriteLine($"A 导入 sqlite => 条目 {bwA.Count:N0}");

    // B. 压成 zst 再导入（模拟用户用浏览器下载到的官方压缩包）
    var zst = Path.Combine(work, "bkwk-db.sqlite.zst");
    using (var src = File.OpenRead(refDb))
    using (var dst = File.Create(zst))
    using (var enc = new ZstdSharp.CompressionStream(dst, 1))
        src.CopyTo(enc);
    Console.WriteLine($"   已生成测试 zst: {new FileInfo(zst).Length / 1024 / 1024} MB");
    await svc.ImportLocalAsync(zst, dbOut, idxFromZst);
    var bwB = new BookWalkerIndex(idxFromZst);
    Console.WriteLine($"B 导入 zst    => 条目 {bwB.Count:N0}  (与 A 一致: {bwA.Count == bwB.Count})");

    // C. 非法文件必须报友好错误，而不是崩溃
    var bogus = Path.Combine(work, "bogus.bin");
    File.WriteAllText(bogus, "not a bookwalker db");
    try
    {
        await svc.ImportLocalAsync(bogus, dbOut, Path.Combine(work, "x.json.gz"));
        Console.WriteLine("C 非法文件 => [失败] 竟然没报错");
    }
    catch (Exception e)
    {
        var m = e.Message.Length > 50 ? e.Message[..50] : e.Message;
        Console.WriteLine($"C 非法文件 => 正确报错: {m}…");
    }

    foreach (var probe in new[] { "One Piece", "ワンピース", "Berserk" })
    {
        var hit = bwB.FindExact(probe);
        Console.WriteLine($"  FindExact('{probe}') => {(hit != null ? $"{hit.Title} / 作者:{string.Join("/", hit.Authors.Select(a => a.Name))} / 出版社:{hit.Publisher} / 卷数:{hit.VolumeCount}" : "未命中")}");
    }
}

// 版本/格式标记 + MACHINE 打标测试（纯函数 + FolderTagger 打标链，全 mock）
// ---------------- 卷号对齐（乱马1/2 每卷封面错位修复的回归测试） ----------------
// 真实案例：Bangumi 关联单行本 name_cn 是空字符串，旧逻辑退化成「Bangumi subject ID 序」当卷号，
// 乱马 38 卷全部错位（Vol.02 拿到第 27 卷封面）。这里用乱马真实书名/关联名验证两条链都按卷号对齐。
async Task TestVolumeMatchAsync()
{
    var fail = 0;
    void Check(string what, bool ok)
    {
        if (!ok) fail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }

    Console.WriteLine("==== volmatchtest：卷号解析（乱马1/2 案例） ====");

    Console.WriteLine("  -- 1. Bangumi 关联单行本名（name_cn 为空 → 用原名）--");
    Check("らんま1/2 (01) → 1", TitleParser.ExtractVolumeNumber("らんま1/2 (01)") == 1);
    Check("乱马1/2 (01) → 1", TitleParser.ExtractVolumeNumber("乱马1/2 (01)") == 1);
    Check("乱马1/2（01）全角括号 → 1", TitleParser.ExtractVolumeNumber("乱马1/2（01）") == 1);
    Check("乱马1/2（56）全角 → 56", TitleParser.ExtractVolumeNumber("乱马1/2（56）") == 56);
    Check("乱马1/2 本体不误判（null）", TitleParser.ExtractVolumeNumber("乱马1/2") == null);
    Check("らんま1/2 01 尾部数字 → 1", TitleParser.ExtractVolumeNumber("らんま1/2 01") == 1);

    Console.WriteLine("  -- 2. Komga 书名（乱马四份拷贝的真实命名）--");
    Check("[..][join].Vol.01 → 1", TitleParser.ExtractBookVolumeNumber("[高橋留美子][亂馬1／2][join].Vol.01", "卷 01", null) == 1);
    Check("[..][C.C]Vol_35 → 35", TitleParser.ExtractBookVolumeNumber("[Comic][亂馬１／２][高橋留美子][文傳][C.C]Vol_35", "卷 35", null) == 35);
    Check("[..][bili]Vol.38-Vol.01?（首位卷号）", TitleParser.ExtractBookVolumeNumber("[Comic][亂馬１／２][高橋留美子][文傳]Vol_01", "卷 01", null) == 1);
    Check("中文卷名 '卷 07' → 7", TitleParser.ExtractBookVolumeNumber("", "卷 07", null) == 7);
    Check("文件名正则失败 → number 字段兜底", TitleParser.ExtractBookVolumeNumber("特别篇", "番外", "04") == 4);
    Check("第3卷特别篇（正则命中）→ 3", TitleParser.ExtractBookVolumeNumber("第3卷特别篇", "", null) == 3);
    Check("number 带小数 '05.5' → 5", TitleParser.ExtractBookVolumeNumber("", "", "05.5") == 5);
    Check("全都解析不出 → 0", TitleParser.ExtractBookVolumeNumber("单行本", "", "") == 0);

    Console.WriteLine("  -- 3. 非行尾/新写法加固 --");
    Check("… Vol.03 [东立] 非行尾 → 3", TitleParser.ExtractVolumeNumber("一拳超人 Vol.03 [东立]") == 3);
    Check("第12卷 非行尾 → 12", TitleParser.ExtractVolumeNumber("咒术回战 第12卷 豪华版") == 12);
    Check("v01 结尾 → 1", TitleParser.ExtractVolumeNumber("药屋少女的呢喃 v01") == 1);
    Check("Vol_01 结尾 → 1", TitleParser.ExtractVolumeNumber("乱马1/2 Vol_01") == 1);
    Check("第3卷（话数不误判）", TitleParser.ExtractVolumeNumber("第3话") == null);
    Check("Fate (1) 中段括号 → 1", TitleParser.ExtractVolumeNumber("Fate/stay night (1) 上") == 1);

    Console.WriteLine(fail == 0 ? "\n==== volmatchtest 全部通过 ====" : $"\n==== volmatchtest {fail} FAIL ====");
    if (fail > 0) Environment.ExitCode = 1;
}

// ---------------- Dry-run 实时改动预览（ScrapeChange 事件） ----------------
// mock Bangumi 关联条目故意按 subject ID 乱序（900=卷3, 902=卷1, 907=卷2）且 name_cn 全空，
// 验证：①事件逐条发射（系列/每卷）；②每卷细节按「卷号」对齐而不是按列表序号；③Dry-run 不发 PATCH。
async Task TestPreviewAsync()
{
    var fail = 0;
    void Check(string what, bool ok)
    {
        if (!ok) fail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }

    const string searchJson = "{\"data\":[{\"id\":5239,\"name\":\"らんま1/2\",\"name_cn\":\"乱马1/2\",\"platform\":\"漫画\"}]}";
    const string subject5239 = "{\"id\":5239,\"name\":\"らんま1/2\",\"name_cn\":\"乱马1/2\",\"platform\":\"漫画\"," +
        "\"summary\":\"主人公乱马遇到冷水就变女生…\",\"volumes\":38,\"nsfw\":false,\"age_rating\":0," +
        "\"tags\":[{\"name\":\"高桥留美子\",\"count\":88},{\"name\":\"漫画\",\"count\":66}]}";
    // 关联单行本：乱序 id + name_cn 空串（复刻乱马1/2 的真实数据形态）
    const string relatedJson = "{\"id\":5239,\"name\":\"らんま1/2\",\"name_cn\":\"乱马1/2\",\"relation\":\"主条目\"}," +
        "{\"id\":900,\"name\":\"らんま1/2 (03)\",\"name_cn\":\"\",\"relation\":\"单行本\"}," +
        "{\"id\":902,\"name\":\"らんま1/2 (01)\",\"name_cn\":\"\",\"relation\":\"单行本\"}," +
        "{\"id\":907,\"name\":\"らんま1/2 (02)\",\"name_cn\":\"\",\"relation\":\"单行本\"}";
    string SubjectJson(int id, string isbn, string date) =>
        "{\"id\":" + id + ",\"name\":\"らんま1/2 (" + id + ")\",\"name_cn\":\"\",\"platform\":\"漫画\"," +
        "\"summary\":\"第" + id + "卷简介\",\"infobox\":[{\"key\":\"ISBN\",\"value\":\"" + isbn + "\"}," +
        "{\"key\":\"发售日\",\"value\":\"" + date + "\"}]}";

    // Komga：1 系列 3 卷（书名乱马 join 版），dry-run 下不该有 PATCH
    var bookPatches = new Dictionary<string, string>();
    var seriesPatches = new List<string>();
    using var komgaMock = new MockHttp(25714, (method, path, body) =>
    {
        if (method == "GET" && path == "/api/v1/series/s20")
            return (200, "{\"id\":\"s20\",\"name\":\"[高橋留美子][亂馬1／2][join]38全\",\"libraryId\":\"L1\",\"booksCount\":3," +
                "\"metadata\":{\"title\":\"\",\"tags\":[],\"genres\":[]},\"url\":\"/comics/[高橋留美子][亂馬1／2][join]38全\"}");
        if (method == "GET" && path == "/api/v1/series/s20/metadata")
            return (200, "{\"status\":\"\",\"title\":\"\",\"tags\":[],\"genres\":[]}");
        if (method == "POST" && path.StartsWith("/api/v1/books/list"))
            return (200, "{\"content\":[" +
                "{\"id\":\"b1\",\"name\":\"[高橋留美子][亂馬1／2][join].Vol.01\",\"metadata\":{\"number\":\"01\",\"numberSort\":1.0,\"title\":\"\",\"summary\":\"\"}}," +
                "{\"id\":\"b2\",\"name\":\"[高橋留美子][亂馬1／2][join].Vol.02\",\"metadata\":{\"number\":\"02\",\"numberSort\":2.0,\"title\":\"\",\"summary\":\"\"}}," +
                "{\"id\":\"b3\",\"name\":\"[高橋留美子][亂馬1／2][join].Vol.03\",\"metadata\":{\"number\":\"03\",\"numberSort\":3.0,\"title\":\"\",\"summary\":\"\"}}],\"totalElements\":3,\"numberOfElements\":3}");
        if (method == "GET" && path.Contains("/thumbnails")) return (200, "[]");
        if (method == "PATCH" && path.Contains("/books/") && path.Contains("/metadata"))
            { lock (bookPatches) bookPatches[path] = body; return (204, ""); }
        if (method == "PATCH" && path.EndsWith("/metadata"))
            { lock (seriesPatches) seriesPatches.Add(body); return (204, ""); }
        return (200, "{}");
    });
    using var bgmMock = new MockHttp(25715, (method, path, body) =>
    {
        if (path.StartsWith("/v0/search/subjects")) return (200, searchJson);
        if (path.StartsWith("/v0/subjects/5239/subjects")) return (200, "[" + relatedJson + "]");
        if (path.Contains("/v0/subjects/900")) return (200, SubjectJson(900, "ISBN-C", "2020-03-01"));
        if (path.Contains("/v0/subjects/902")) return (200, SubjectJson(902, "ISBN-A", "2020-01-01"));
        if (path.Contains("/v0/subjects/907")) return (200, SubjectJson(907, "ISBN-B", "2020-02-01"));
        if (path.StartsWith("/v0/subjects/5239")) return (200, subject5239);
        return (404, "{}");
    });

    using var komga = new KomgaClient("http://127.0.0.1:25714", "k", null, null);
    using var bangumi = new BangumiClient(null, "http://127.0.0.1:25715");
    bangumi.MinRequestIntervalMs = 0;
    var scrape = new ScrapeService(komga, bangumi, new ConsoleLogger());
    var changes = new List<ScrapeChange>();
    scrape.ChangeObserved += c => { lock (changes) changes.Add(c); };

    var series = new SeriesDto
    {
        Id = "s20",
        Name = "[高橋留美子][亂馬1／2][join]38全",
        LibraryId = "L1",
        Url = "/comics/[高橋留美子][亂馬1／2][join]38全",
        Metadata = new SeriesMetadataDto { Title = "", Tags = [], Genres = [] },
    };
    var r = await scrape.ScrapeAsync("s20", 5239,
        new ScrapeOptions { SyncType = SyncType.All, FetchVolumeData = true, DryRun = true }, series, null);

    Check("Dry-run 刮削成功", r.Success);
    Check("发射了改动预览事件", changes.Count > 0);
    Check("有系列元数据预览行", changes.Any(c => c.Kind == ScrapeChangeKind.SeriesMeta));
    Check("系列预览行带旧→新细节", changes.Where(c => c.Kind == ScrapeChangeKind.SeriesMeta)
        .Any(c => c.Detail.Contains("→", StringComparison.Ordinal)));
    var bookRows = changes.Where(c => c.Kind == ScrapeChangeKind.BookMeta).ToList();
    Check("每卷各发一条预览（3 卷）", bookRows.Count == 3);
    // 并发派发下事件到达顺序不定，按卷号分组断言（不依赖顺序）
    string RowOf(string vol) => bookRows.FirstOrDefault(c => c.Target.Contains($"卷 {vol}", StringComparison.Ordinal))?.Detail ?? "";
    Check("预览行覆盖卷01/02/03", new[] { "01", "02", "03" }.All(v => RowOf(v).Length > 0));
    // 关键对齐断言：乱序的 Bangumi 关联条目必须按「卷号」取 ISBN，而不是按列表序
    Check("卷01 的 ISBN 预览 = ISBN-A（乱序ID修复）", RowOf("01").Contains("ISBN-A", StringComparison.Ordinal));
    Check("卷02 的 ISBN 预览 = ISBN-B", RowOf("02").Contains("ISBN-B", StringComparison.Ordinal));
    Check("卷03 的 ISBN 预览 = ISBN-C", RowOf("03").Contains("ISBN-C", StringComparison.Ordinal));
    Check("Dry-run 不发任何卷 PATCH", bookPatches.Count == 0);
    Check("Dry-run 不发系列 PATCH", seriesPatches.Count == 0);

    // 真实模式：新开一个 ScrapeService（事件只能 += / -=，不能整体清空），同样对齐且语气变为"已…"
    var changes2 = new List<ScrapeChange>();
    var scrape2 = new ScrapeService(komga, bangumi, new ConsoleLogger());
    scrape2.ChangeObserved += c => { lock (changes2) changes2.Add(c); };
    var r2 = await scrape2.ScrapeAsync("s20", 5239,
        new ScrapeOptions { SyncType = SyncType.All, FetchVolumeData = true, DryRun = false }, series, null);
    Check("真实模式也发射事件（写入留痕）", changes2.Count > 0);
    Check("真实模式真的写了卷元数据", bookPatches.Count >= 3);
    Check("真实模式卷对齐不变（b1=ISBN-A）", bookPatches.TryGetValue("/api/v1/books/b1/metadata", out var b1body) &&
        b1body.Contains("ISBN-A", StringComparison.Ordinal));
    Check("真实模式 MACHINE 必打", seriesPatches.Any(p => p.Contains("MACHINE", StringComparison.Ordinal)));

    Console.WriteLine(fail == 0 ? "\n==== previewtest 全部通过 ====" : $"\n==== previewtest {fail} FAIL ====");
    if (fail > 0) Environment.ExitCode = 1;
}

// ---------------- 编辑器 PATCH 体构建 ----------------
async Task TestEditorPatchAsync()
{
    var fail = 0;
    void Check(string what, bool ok)
    {
        if (!ok) fail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }

    Console.WriteLine("==== editortest：SeriesEditorPatch ====");
    // 默认 encoder 会把 CJK 转成 \uXXXX，这里用宽松 encoder 保证断言是字面中文
    var jopt = new System.Text.Json.JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o, o.GetType(), jopt);
    var json = (Func<object, string>)Json;

    // 1. 只发改动字段 + MACHINE 必打 + 锁原样回传
    var current = new SeriesMetadataDto
    {
        Title = "乱马1/2", TitleSort = "乱马1/2", Status = "ENDED", Summary = "旧简介",
        Tags = ["旧标签", "MACHINE"], Genres = ["喜剧"], Publisher = "旧社",
        TagsLock = true, TitleLock = false, SummaryLock = true,
    };
    var body = SeriesEditorPatch.BuildSeriesPatch(current, new SeriesEditInput
    {
        Summary = "新简介",
        Tags = ["新标签"],          // 重建全量标签
    });
    var s = json(body);
    Check("简介改动进 PATCH", s.Contains("\"summary\":\"新简介\""));
    Check("未改动的标题不发", !s.Contains("\"title\""));
    Check("未改动的状态不发", !s.Contains("\"status\""));
    Check("未改动的出版社不发", !s.Contains("\"publisher\""));
    Check("未改动的题材不发", !s.Contains("\"genres\""));
    Check("标签重建（用户编辑的全量）", s.Contains("\"tags\"") && s.Contains("新标签"));
    Check("MACHINE 自动补进标签（必打）", s.Contains("MACHINE"));
    Check("锁字段原样回传", s.Contains("\"tagsLock\":true") && s.Contains("\"summaryLock\":true") &&
        s.Contains("\"titleLock\":false"));

    // 2. tags 未动 + 已有 MACHINE → 不发 tags
    var body2 = SeriesEditorPatch.BuildSeriesPatch(current, new SeriesEditInput { Summary = "另一简介" });
    Check("tags 未动且已有 MACHINE → 不发 tags", !json(body2).Contains("\"tags\""));

    // 3. tags 未动 + 没有 MACHINE → 补一条 tags PATCH（必打）
    var current3 = new SeriesMetadataDto { Title = "x", Tags = ["别的"] };
    var body3 = SeriesEditorPatch.BuildSeriesPatch(current3, new SeriesEditInput());
    Check("无 MACHINE 时只补 tags（单键 PATCH）", body3.Count == 1 && body3.ContainsKey("tags"));
    Check("tags 里含 MACHINE", json(body3).Contains("MACHINE"));

    // 4. ageRating=0 = 清除分级（合法值，会发送）
    var body4 = SeriesEditorPatch.BuildSeriesPatch(current, new SeriesEditInput { AgeRating = 0 });
    Check("ageRating 0 显式发送（清除分级）", json(body4).Contains("\"ageRating\":0"));
    Check("totalBookCount 0 被拒绝（不发）", !json(SeriesEditorPatch.BuildSeriesPatch(
        current, new SeriesEditInput { TotalBookCount = 0 })).Contains("totalBookCount"));

    // 5. 卷 PATCH：只发改动字段 + 锁回传
    var bbody = SeriesEditorPatch.BuildBookPatch(
        new BookMetadataDto { Title = "卷 01", Summary = "旧", Isbn = "111" },
        new BookEditInput { Summary = "新简介" });
    var bs = json(bbody);
    Check("卷简介改动进 PATCH", bs.Contains("\"summary\":\"新简介\""));
    Check("卷 ISBN 未动不发", !bs.Contains("\"isbn\""));
    Check("卷标题未动不发", !bs.Contains("\"title\""));

    // 6. 清空字段：空串真实写入（编辑器语义）
    var body6 = SeriesEditorPatch.BuildSeriesPatch(current, new SeriesEditInput { Summary = "" });
    Check("空串 = 清空字段（会发送空 summary）", json(body6).Contains("\"summary\":\"\""));

    Console.WriteLine(fail == 0 ? "\n==== editortest 全部通过 ====" : $"\n==== editortest {fail} FAIL ====");
    if (fail > 0) Environment.ExitCode = 1;
}

async Task TestMarkerAsync()
{
    var fail = 0;
    void Check(string what, bool ok)
    {
        if (!ok) fail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }

    Console.WriteLine("  -- 1. Detect 纯函数 --");
    var d1 = VersionMarker.Detect("[CLAMP][Chobits 爱藏版][Vol.01-Vol.08][完结][东立][电子版]");
    Check("爱藏版+电子版（出版社段后无供应商段）", d1.Contains("爱藏版") && d1.Contains("电子版") && d1.Count == 2);

    var d2 = VersionMarker.Detect("[Boichi][最强不良传说][东立][ZD×Nearfly][25完]");
    Check("出版社段后的汉化组段被采为来源标记", d2.SequenceEqual(["ZD×Nearfly"]));

    var d3 = VersionMarker.Detect("苍蓝钢铁的琶音（BW原档）");
    Check("尾部全角括号标注", d3.SequenceEqual(["BW原档"]));

    var d4 = VersionMarker.Detect("[gyuo][机魂（境外版）][bili][Vol.01-Vol.06][未完]");
    Check("嵌套括号（境外版）+ b漫版", d4.Contains("境外版") && d4.Contains("b漫版"));

    var d5 = VersionMarker.Detect("[CLAMP][xxxHOLiC][19完][东立][zsliming&boo3000]");
    Check("无出版社段时命中提示词的段被采为标记", d5.SequenceEqual(["zsliming&boo3000"]));

    var d6 = VersionMarker.Detect("[小島あきら] まほらば 全12卷 +白、碧、蒼、朱");
    Check("纯卷数段不产生标记", d6.Count == 0);

    var d7 = VersionMarker.Detect("[CLAMP][东京BABYLON 完全版][Vol.01-Vol.03][角川][电子版]");
    Check("完全版+电子版双标记", d7.Contains("完全版") && d7.Contains("电子版"));

    Console.WriteLine("  -- 2. ApplySuffixes 纯函数 --");
    var t1 = VersionMarker.ApplySuffixes("人形电脑天使心", ["爱藏版", "电子版"], out var c1);
    Check("双后缀追加且 changed（[] 方括号格式）", t1 == "人形电脑天使心 [爱藏版][电子版]" && c1);
    var t2 = VersionMarker.ApplySuffixes("人形电脑天使心（爱藏版）", ["爱藏版", "电子版"], out var c2);
    Check("旧（）后缀原位归一为 [] + 追加", t2 == "人形电脑天使心 [爱藏版][电子版]" && c2);
    var t3 = VersionMarker.ApplySuffixes("苍蓝钢铁的琶音（爱藏版）（电子版）", ["爱藏版", "电子版"], out var c3);
    Check("全部已含（旧格式）→ 只归一不重复追加", t3 == "苍蓝钢铁的琶音 [爱藏版][电子版]" && c3);
    var t4 = VersionMarker.ApplySuffixes("海贼王", ["b漫版"], out var c4);
    Check("b漫版词原样进标题（标签才转 bili）", t4 == "海贼王 [b漫版]" && c4);

    Console.WriteLine("  -- 3. FolderName（oneshot 取父目录）--");
    Check("普通系列取尾目录", VersionMarker.FolderName(new SeriesDto
        { Name = "x", Url = "/comics/[Boichi][海贼王][东立][电子版]" }) == "[Boichi][海贼王][东立][电子版]");
    Check("oneshot 取 zip 父目录", VersionMarker.FolderName(new SeriesDto
        { Name = "x", Oneshot = true, Url = "/comics/单行本集/海贼王 1.zip" }) == "单行本集");
    Check("无 url 回退 Name", VersionMarker.FolderName(new SeriesDto { Name = "兜底名" }) == "兜底名");

    Console.WriteLine("  -- 4. FolderTagger 打标链（全 mock）--");
    var patches = new Dictionary<string, string>();
    using var mock = new MockHttp(25720, (method, path, body) =>
    {
        var m = System.Text.RegularExpressions.Regex.Match(path, @"/api/v1/series/(s\d+)/metadata");
        if (method == "PATCH" && m.Success) { patches[m.Groups[1].Value] = body; return (204, ""); }
        return (200, "{}");
    });
    using var komga = new KomgaClient("http://127.0.0.1:25720", "k", null, null);
    var tagger = new FolderTagger(komga, new ConsoleLogger());
    var list = new List<SeriesDto>
    {
        new()  // 标题已被刮平 + 无标签 → 需要恢复标记 + MACHINE
        {
            Id = "s9",
            Name = "[CLAMP][Chobits 爱藏版][东立][C.C][8完]",
            Metadata = new SeriesMetadataDto { Title = "人形电脑天使心", Tags = [] },
        },
        new()  // 已打标过（tags 含 MACHINE + 标题已带后缀）→ 不重复
        {
            Id = "s10",
            Name = "[CLAMP][Chobits 爱藏版][东立][电子版]",
            Metadata = new SeriesMetadataDto
            {
                Title = "人形电脑天使心（爱藏版）（电子版）",
                Tags = ["爱藏版", "电子版", "MACHINE"],
                Publisher = "东立",
            },
        },
        new()  // tags 被锁定：版本标记词不追加，但 MACHINE 必打（用户要求：打标不是选择是必须）
        {
            Id = "s11",
            Name = "[CLAMP][Chobits 爱藏版][东立][C.C][8完]",
            Metadata = new SeriesMetadataDto
            {
                Title = "人形电脑天使心", Tags = ["已有"], TagsLock = true, Publisher = "旧社",
            },
        },
    };
    var n = await tagger.TagAsync(list, dryRun: false);
    Check("三个系列都处理", n == 3);
    Check("PATCH 重建完整标题+双后缀（爱藏版+来源段C.C，方括号格式）",
        patches.TryGetValue("s9", out var p9) && p9.Contains("人形电脑天使心 [爱藏版][C.C]"));
    Check("PATCH 带 MACHINE 标签", p9?.Contains("MACHINE") == true);
    Check("PATCH 带出版社标签 东立", p9?.Contains("东立") == true);
    Check("PATCH publisher 填充 东立", p9?.Contains("\"publisher\":\"东立\"") == true);
    Check("锁 tags：版本标记词不追加（tags 数组只有 已有+MACHINE）",
        patches.TryGetValue("s11", out var p11) && p11.Contains("\"tags\":[\"已有\",\"MACHINE\"]"));
    Check("锁 tags：仍带 MACHINE（必打）", p11?.Contains("MACHINE") == true);
    Check("锁 tags：publisher 不填充", p11?.Contains("publisher") == false);
    Console.WriteLine(fail == 0 ? "\n==== marktest 全部通过 ====" : $"\n==== marktest {fail} FAIL ====");
    if (fail > 0) Environment.ExitCode = 1;
}

// ---------------- 版本标记黑白名单（批次⑰：白名单扩识别 / 黑名单丢弃+清旧后缀 / [] 命名格式） ----------------
async Task TestTagRuleAsync()
{
    var fail = 0;
    void Check(string what, bool ok)
    {
        if (!ok) fail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }

    Console.WriteLine("  -- 1. 黑名单：识别到的标记被丢弃 --");
    var m1 = VersionMarker.Detect("[高桥留美子][乱马1/2][大然][join]38全", null, ["join"]);
    Check("join 进黑名单后标记为空", m1.Count == 0);
    var b1 = VersionMarker.DetectBlacklisted("[高桥留美子][乱马1/2][大然][join]38全", null, ["join"]);
    Check("DetectBlacklisted 报出被丢的标记", b1.SequenceEqual(["join"]));
    var m2 = VersionMarker.Detect("[x][完全版][东立]", null, ["完全版"]);
    Check("内置版本词也能被黑名单丢弃", m2.Count == 0);

    Console.WriteLine("  -- 2. 黑名单：StripSuffixes 清标题旧后缀 --");
    var s1 = VersionMarker.StripSuffixes("乱马1/2（join）", ["join"], out var st1);
    Check("旧全角括号后缀被清（乱马1/2（join）→ 乱马1/2）", st1 && s1 == "乱马1/2");
    var s2 = VersionMarker.StripSuffixes("乱马1/2 [join][大然]", ["join"], out var st2);
    Check("新方括号后缀被清、其它保留", st2 && s2 == "乱马1/2 [大然]");
    var s3 = VersionMarker.StripSuffixes("海贼王", ["join"], out var st3);
    Check("没有旧后缀则不动", !st3 && s3 == "海贼王");

    Console.WriteLine("  -- 3. 白名单：内置词表认不出的后缀也能落标签 --");
    var m3 = VersionMarker.Detect("[高桥留美子][乱马1/2][文传][C.C]38全", ["C.C"], null);
    Check("白名单命中整段 C.C（一个后缀一个标签）", m3.SequenceEqual(["C.C"]));
    var m4 = VersionMarker.Detect("乱马1/2 高清重制", ["高清"], null);
    Check("括号外命中 → 词本身作标记", m4.SequenceEqual(["高清"]));
    var m5 = VersionMarker.Detect("[x][乱马 完全版][东立]", ["完全版"], null);
    Check("白名单与内置词表去重（不重复落）", m5.Count == 1 && m5[0] == "完全版");
    var m5b = VersionMarker.Detect("[x][乱马 完全版][东立]", null, null);
    Check("空名单 = 只用内置词表（行为不变）", m5.Count == 1 && m5b.SequenceEqual(m5));

    Console.WriteLine("  -- 4. 出版社词独立取用（一个后缀一个标签的对齐口径）--");
    var pk = VersionMarker.PublisherKeyword("[高桥留美子][乱马1/2][大然][join]38全");
    Check("出版社词 大然 独立可取", pk == "大然");
    Check("出版社词被拉黑 → IsBlacklisted 命中", VersionMarker.IsBlacklisted("大然", ["大然"]));
    Check("MACHINE 不受黑名单影响（调用方已保护）", !VersionMarker.IsBlacklisted("MACHINE", ["无关词"]));

    Console.WriteLine("  -- 5. 命名格式（[] 代替（））--");
    var t1 = VersionMarker.ApplySuffixes("海贼王", ["爱藏版"], out var c1);
    Check("追加方括号 + 前置空格", c1 && t1 == "海贼王 [爱藏版]");
    var t2 = VersionMarker.ApplySuffixes("海贼王 [爱藏版]", ["电子版"], out var c2);
    Check("连续标记紧贴成一组", c2 && t2 == "海贼王 [爱藏版][电子版]");
    var t3 = VersionMarker.ApplySuffixes("海贼王（爱藏版）", ["爱藏版"], out var c3);
    Check("旧（）原位归一为 []", c3 && t3 == "海贼王 [爱藏版]");
    var t4 = VersionMarker.ApplySuffixes("海贼王 [爱藏版]", ["爱藏版"], out var c4);
    Check("已含（新格式）不重复追加", !c4 && t4 == "海贼王 [爱藏版]");

    Console.WriteLine(fail == 0 ? "\n==== tagruletest 全部通过 ====" : $"\n==== tagruletest {fail} FAIL ====");
    if (fail > 0) Environment.ExitCode = 1;
}

// 百度百科（最终兜底源）测试：
// 1) 真实接口：食物战线无异状 / 名侦探贵鸟同学（bgm 没有的冷门漫画）+ 查不到的词返回 null；
// 2) 纯函数：JpegCoverUrl；
// 3) 全 mock 兜底链：Bangumi 空 → BW/Mox/AI 均无 → 百科命中 → ScrapeFromBaikeAsync PATCH 断言。
async Task TestBaikeAsync()
{
    var fail = 0;
    void Check(string what, bool ok)
    {
        if (!ok) fail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }
    static bool HasText(string? s, string cjk, string escaped) =>
        s != null && (s.Contains(cjk, StringComparison.Ordinal) || s.Contains(escaped, StringComparison.Ordinal));

    // ================= 1. 真实接口 =================
    Console.WriteLine("  -- 1. 真实接口：食物战线无异状 --");
    try
    {
        using var bk = new BaikeClient();
        var c1 = await bk.SearchCardAsync("食物战线无异状");
        Check("食物战线无异状 命中词条", c1 != null);
        Check($"词条标题（实际:{c1?.Title}）", c1?.Title == "食物战线无异状");
        Check($"作画 アタモト（实际:{c1?.Field("作画")}）", c1?.Field("作画") == "アタモト");
        Check($"其他出版社 东立出版社（实际:{c1?.Field("其他出版社")}）", c1?.Field("其他出版社") == "东立出版社");
        Check($"原版名称保留日文原文（实际:{c1?.Field("原版名称")}）", c1?.Field("原版名称") == "ベモノ戦線異状ナシ");
        Check("摘要非空", (c1?.Abstract.Length ?? 0) > 0);
        Check("概述图 URL 非空", (c1?.ImageUrl.Length ?? 0) > 0);
        Check("词条页 https", c1?.EntryUrl.StartsWith("https://baike.baidu.com/", StringComparison.Ordinal) == true);

        Console.WriteLine("  -- 2. 真实接口：名侦探贵鸟同学 --");
        var c2 = await bk.SearchCardAsync("名侦探贵鸟同学");
        Check("名侦探贵鸟同学 命中词条", c2 != null);
        Check($"词条标题（实际:{c2?.Title}）", c2?.Title == "名侦探贵鸟同学");
        Check($"原版名称 名探偵キドリ（实际:{c2?.Field("原版名称")}）", c2?.Field("原版名称") == "名探偵キドリ");
        Check($"类型 推理喜剧（实际:{c2?.Field("类型")}）", c2?.Field("类型") == "推理喜剧");
        Check($"作画 马田伊助（百科简体卡，实际:{c2?.Field("作画")}）", c2?.Field("作画") == "马田伊助");
        Check("摘要含剧情描述", (c2?.Abstract.Contains("貴鳥同学") ?? false) || (c2?.Abstract.Contains("贵鸟同学") ?? false));

        Console.WriteLine("  -- 3. 真实接口：查不到的词 --");
        var c3 = await bk.SearchCardAsync("zxq不存在的词条9821k");
        Check("查不到的词返回 null（不抛异常）", c3 == null);
    }
    catch (Exception e)
    {
        Check($"真实接口网络失败（本环境需能直连百科）: {e.Message}", false);
    }

    Console.WriteLine("  -- 4. 纯函数：JpegCoverUrl --");
    Check("f_auto 换成 jpg",
        BaikeClient.JpegCoverUrl("https://bkimg.cdn.bcebos.com/pic/abc?x-bce-process=image/format,f_auto")
        == "https://bkimg.cdn.bcebos.com/pic/abc?x-bce-process=image/format,jpg");
    Check("无处理参数时追加 jpg",
        BaikeClient.JpegCoverUrl("https://bkimg.cdn.bcebos.com/pic/abc")
        == "https://bkimg.cdn.bcebos.com/pic/abc?x-bce-process=image/format,jpg");
    Check("已有显式格式时不动",
        BaikeClient.JpegCoverUrl("https://bkimg.cdn.bcebos.com/pic/abc?x-bce-process=image/resize,w_100")
        == "https://bkimg.cdn.bcebos.com/pic/abc?x-bce-process=image/resize,w_100");

    // ================= 5. 全 mock 兜底链 =================
    Console.WriteLine("  -- 5. mock 兜底链：Bangumi 空 → 百科命中 → 刮削 --");
    const string cardJson = "{\"id\":16885591,\"subLemmaId\":17825656,\"newLemmaId\":17700077," +
        "\"key\":\"食物战线无异状\",\"desc\":\"日本漫画家アタモト作画的漫画\",\"title\":\"食物战线无异状\"," +
        "\"card\":[" +
        "{\"key\":\"m21_bookname\",\"name\":\"中文名\",\"value\":[\"食物战线无异状\"]}," +
        "{\"key\":\"m21_originalname\",\"name\":\"原版名称\",\"value\":[\"ベモノ戦線異状ナシ\"]}," +
        "{\"key\":\"m21_paintering\",\"name\":\"作画\",\"value\":[\"アタモト\"]}," +
        "{\"key\":\"m21_bookregion\",\"name\":\"地区\",\"value\":[\"日本\"]}," +
        "{\"key\":\"m21_otherpress\",\"name\":\"其他出版社\",\"value\":[\"东立出版社\"]}]," +
        "\"image\":\"https://bkimg.cdn.bcebos.com/pic/faf2b211?x-bce-process=image/format,f_auto\"," +
        "\"abstract\":\"《食物战线无异状》是由日本漫画家アタモト作画的漫画。\"," +
        "\"url\":\"http://baike.baidu.com/item/%E9%A3%9F%E7%89%A9%E6%88%98%E7%BA%BF%E6%97%A0%E5%BC%82%E7%8A%B6\"}";

    using var baikeMock = new MockHttp(25798, (path, _) =>
        path.StartsWith("/api/openapi/BaikeLemmaCardApi", StringComparison.Ordinal) ? (200, cardJson) : (404, "{}"));
    using var bgmMock = new MockHttp(25797, (method, path, body) =>
    {
        if (path.StartsWith("/v0/search/subjects", StringComparison.Ordinal)) return (200, "{\"data\":[]}");
        return (404, "{}");
    });
    string? patch = null;
    using var komgaMock = new MockHttp(25796, (method, path, body) =>
    {
        if (method == "PATCH" && path == "/api/v1/series/s1/metadata") { patch = body; return (204, ""); }
        if (method == "GET" && path.Contains("/thumbnails", StringComparison.Ordinal)) return (200, "[]");
        if (method == "POST" && path.StartsWith("/api/v1/books/list", StringComparison.Ordinal))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        if (method == "GET" && path.StartsWith("/api/v1/collections", StringComparison.Ordinal))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        if (method == "POST" && path.StartsWith("/api/v1/collections", StringComparison.Ordinal))
            return (200, "{\"id\":\"c1\",\"name\":\"手动匹配\",\"seriesIds\":[]}");
        if (method == "PATCH" && path.StartsWith("/api/v1/collections", StringComparison.Ordinal)) return (204, "");
        return (200, "{}");
    });

    using var komga = new KomgaClient("http://127.0.0.1:25796", "k", null, null);
    using var bangumi = new BangumiClient(null, "http://127.0.0.1:25797");
    bangumi.MinRequestIntervalMs = 0;
    using var baike = new BaikeClient("http://127.0.0.1:25798");
    var batch = new BatchMatcher(komga, bangumi, new ConsoleLogger()) { Baike = baike };
    var series = new SeriesDto
    {
        Id = "s1",
        Name = "[食物战线无异状][Vol.01-Vol.03]",
        LibraryId = "lib1",
        Url = "[食物战线无异状][Vol.01-Vol.03]",
        Metadata = new SeriesMetadataDto { Links = [] },   // Title 为空 → 词条标题/排序标题才需要写入
    };
    var r = await batch.PreciseMatchAsync(series, SyncType.Meta, forceRefreshBtv: false);
    Console.WriteLine($"  => success={r.Success} matched={r.MatchedTitle}");
    Check("百科兜底链匹配成功", r.Success);
    Check("匹配到百科词条《食物战线无异状》", r.MatchedTitle == "食物战线无异状");
    Check("PATCH 写入中文标题", HasText(patch, "食物战线无异状", "\\u98df\\u7269\\u6218\\u7ebf\\u65e0\\u5f02\\u72b6"));
    Check("PATCH 写入出版社 东立出版社", HasText(patch, "东立出版社", "\\u4e1c\\u7acb\\u51fa\\u7248\\u793e"));
    Check("PATCH 写入作者 アタモト(penciller)", patch?.Contains("アタモト", StringComparison.Ordinal) == true);
    Check("PATCH 写入百科链接", patch?.Contains("baike.baidu.com", StringComparison.Ordinal) == true);
    Check("PATCH 带 MACHINE 标签", patch?.Contains("\"MACHINE\"", StringComparison.Ordinal) == true);
    Check("PATCH 阅读方向 RTL（词条描述提到漫画）", patch?.Contains("RIGHT_TO_LEFT", StringComparison.Ordinal) == true);
    Check("PATCH titleSort 无后缀净标题",
        HasText(patch, "\"titleSort\":\"食物战线无异状\"", "\"titleSort\":\"\\u98df\\u7269\\u6218\\u7ebf\\u65e0\\u5f02\\u72b6\""));
    Check("百科 mock 被调用过", baikeMock.Requests > 0);

    Console.WriteLine(fail == 0 ? "\n==== baiketest 全部通过 ====" : $"\n==== baiketest {fail} FAIL ====");
    if (fail > 0) Environment.ExitCode = 1;
}

// MangaDex 源（免密钥公开 API）：真实接口探针 + mock 兜底链端到端
async Task TestMangaDexAsync()
{
    var fail = 0;
    void Check(string what, bool ok)
    {
        if (!ok) fail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }
    static bool HasText(string? s, string cjk, string escaped) =>
        s != null && (s.Contains(cjk, StringComparison.Ordinal) || s.Contains(escaped, StringComparison.Ordinal));

    // ================= 1. 真实接口 =================
    Console.WriteLine("  -- 1. 真实接口：one piece --");
    try
    {
        using var md = new MangaDexClient();
        var l1 = await md.SearchAsync("one piece", 5);
        Check("one piece 命中", l1.Count > 0);
        var one = l1.FirstOrDefault(m => m.OriTitle.Contains("ワンピース", StringComparison.Ordinal))
                  ?? l1.FirstOrDefault();
        Check($"封面 uploads.mangadex.org（实际:{one?.CoverUrl}）",
            one?.CoverUrl.StartsWith("https://uploads.mangadex.org/covers/", StringComparison.Ordinal) == true);
        Check("标签非空（英文 tag）", one is { Tags.Count: > 0 });

        Console.WriteLine("  -- 2. 真实接口：中文别名搜索（海贼王）--");
        var l2 = await md.SearchAsync("海贼王", 5);
        Check("中文别名命中", l2.Count > 0);
        Check("命中 One Piece 相关条目", l2.Any(m =>
            m.Title.Contains("One Piece", StringComparison.OrdinalIgnoreCase) ||
            m.AltTitles.Any(a => a.Contains("ワンピース", StringComparison.Ordinal))));

        Console.WriteLine("  -- 3. 真实接口：查不到的词 --");
        var l3 = await md.SearchAsync("zxq不存在9821k", 5);
        Check("查不到返回空列表（不抛异常）", l3.Count == 0);
    }
    catch (Exception e)
    {
        Check($"真实接口网络失败（本环境需能直连 api.mangadex.org）: {e.Message}", false);
    }

    // ================= 2. mock 兜底链 =================
    Console.WriteLine("  -- 4. mock 兜底链：Bangumi 空 → MangaDex 命中 → 刮削 --");
    const string mdJson = "{\"result\":\"ok\",\"data\":[{\"id\":\"md-uuid-1\",\"type\":\"manga\",\"attributes\":{" +
        "\"title\":{\"ja\":\"ワンピース\",\"en\":\"One Piece\",\"zh-Hans\":\"海贼王\"}," +
        "\"altTitles\":[{\"ja\":\"ワンピース\"},{\"zh-Hans\":\"航海王\"},{\"en\":\"One Piece\"}]," +
        "\"description\":{\"en\":\"A pirate adventure manga.\"}," +
        "\"originalLanguage\":\"ja\",\"status\":\"ongoing\"," +
        "\"tags\":[{\"attributes\":{\"name\":{\"en\":\"Action\"}}},{\"attributes\":{\"name\":{\"en\":\"Fantasy\"}}}]}," +
        "\"relationships\":[{\"id\":\"cover-uuid\",\"type\":\"cover_art\",\"attributes\":{\"fileName\":\"abc.jpg\"}}]}]}";
    using var mdMock = new MockHttp(25785, (method, path, _) =>
    {
        if (path.StartsWith("/manga?title=", StringComparison.Ordinal)) return (200, mdJson);
        if (path.StartsWith("/manga/md-uuid-1", StringComparison.Ordinal)) return (200, mdJson);
        return (404, "{}");
    });
    using var bgmMock = new MockHttp(25786, (method, path, body) =>
    {
        if (path.StartsWith("/v0/search/subjects", StringComparison.Ordinal)) return (200, "{\"data\":[]}");
        return (404, "{}");
    });
    string? patch = null;
    using var komgaMock = new MockHttp(25787, (method, path, body) =>
    {
        if (method == "PATCH" && path == "/api/v1/series/s1/metadata") { patch = body; return (204, ""); }
        if (method == "GET" && path.Contains("/thumbnails", StringComparison.Ordinal)) return (200, "[]");
        if (method == "POST" && path.StartsWith("/api/v1/books/list", StringComparison.Ordinal))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        if (method == "GET" && path.StartsWith("/api/v1/collections", StringComparison.Ordinal))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        if (method == "POST" && path.StartsWith("/api/v1/collections", StringComparison.Ordinal))
            return (200, "{\"id\":\"c1\",\"name\":\"手动匹配\",\"seriesIds\":[]}");
        if (method == "PATCH" && path.StartsWith("/api/v1/collections", StringComparison.Ordinal)) return (204, "");
        return (200, "{}");
    });

    using var komga = new KomgaClient("http://127.0.0.1:25787", "k", null, null);
    using var bangumi = new BangumiClient(null, "http://127.0.0.1:25786");
    bangumi.MinRequestIntervalMs = 0;
    using var mdMockClient = new MangaDexClient("http://127.0.0.1:25785");
    var batch = new BatchMatcher(komga, bangumi, new ConsoleLogger()) { MangaDex = mdMockClient };
    var series = new SeriesDto
    {
        Id = "s1",
        Name = "[海贼王][Vol.01-Vol.03]",
        LibraryId = "lib1",
        Url = "[海贼王][Vol.01-Vol.03]",
        Metadata = new SeriesMetadataDto { Links = [] },
    };
    var r = await batch.PreciseMatchAsync(series, SyncType.Meta, forceRefreshBtv: false);
    Console.WriteLine($"  => success={r.Success} matched={r.MatchedTitle}");
    Check("MD 兜底链匹配成功", r.Success);
    Check("匹配到 zh-Hans 标题《海贼王》", r.MatchedTitle == "海贼王");
    Check("PATCH 写入中文标题", HasText(patch, "海贼王", "\\u6d77\\u8d3c\\u738b"));
    Check("PATCH 写入别名（航海王）", patch?.Contains("航海王", StringComparison.Ordinal) == true);
    Check("PATCH 写入类型（Action/Fantasy）", patch?.Contains("Action", StringComparison.Ordinal) == true);
    Check("PATCH 写入 MangaDex 链接", patch?.Contains("mangadex.org/title", StringComparison.Ordinal) == true);
    Check("PATCH 带 MACHINE 标签", patch?.Contains("\"MACHINE\"", StringComparison.Ordinal) == true);
    Check("PATCH 阅读方向 RTL（originalLanguage=ja）", patch?.Contains("RIGHT_TO_LEFT", StringComparison.Ordinal) == true);
    Check("MD mock 被调用过", mdMock.Requests > 0);

    Console.WriteLine(fail == 0 ? "\n==== mdtest 全部通过 ====" : $"\n==== mdtest {fail} FAIL ====");
    if (fail > 0) Environment.ExitCode = 1;
}

// AI 识别驱动的匹配回退链端到端测试（全 mock）：
// 本地中文名直接搜 Bangumi 只返回噪声候选 → 问 AI 拿日文原名 → 原名重搜 → AI 从候选里挑中正确项 → 刮削写库
async Task TestMatchChainAsync()
{
    var fail = 0;
    void Check(string what, bool ok)
    {
        if (!ok) fail++;
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    }

    var aiCalls = 0;
    using var aiMock = new MockHttp(25710, (path, body) =>
    {
        if (path != "/v1/chat/completions") return (404, "{}");
        Interlocked.Increment(ref aiCalls);
        // 识别请求（提示词里有 confident 关键字）
        if (body.Contains("confident", StringComparison.Ordinal))
            return (200, "{\"choices\":[{\"message\":{\"content\":\"{\\\"jp\\\":\\\"ワンピース\\\",\\\"cn\\\":\\\"海贼王\\\",\\\"en\\\":\\\"ONE PIECE\\\",\\\"confident\\\":true}\"}}]}");
        // 选择候选：挑《海贼王》所在行号（body 里中文被转义成 \uXXXX，十六进制是大写）
        var pm = System.Text.RegularExpressions.Regex.Match(
            body, "(\\d+)\\. \\\\u300a\\\\u6d77\\\\u8d3c\\\\u738b\\\\u300b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (pm.Success && int.TryParse(pm.Groups[1].Value, out var pi))
            return (200, "{\"choices\":[{\"message\":{\"content\":\"{\\\"index\\\": " + pi + "}\"}}]}");
        return (200, "{\"choices\":[{\"message\":{\"content\":\"{\\\"index\\\": -1}\"}}]}");
    });

    const string junkSearch = "{\"data\":[" +
        "{\"id\":300,\"name\":\"NARUTO\",\"name_cn\":\"火影忍者\",\"platform\":\"漫画\"}," +
        "{\"id\":301,\"name\":\"BLEACH\",\"name_cn\":\"死神\",\"platform\":\"漫画\"}]}";
    const string jpSearch = "{\"data\":[" +
        "{\"id\":778,\"name\":\"ONE PIECE LOG\",\"name_cn\":\"海贼王画集\",\"platform\":\"漫画\"}," +
        "{\"id\":777,\"name\":\"ONE PIECE\",\"name_cn\":\"海贼王\",\"platform\":\"漫画\"}]}";
    const string subject777 = "{\"id\":777,\"name\":\"ONE PIECE\",\"name_cn\":\"海贼王\",\"platform\":\"漫画\"," +
        "\"summary\":\"路飞与草帽海贼团的冒险故事。\",\"volumes\":100,\"nsfw\":false,\"age_rating\":0,\"tags\":[]}";

    var bgmSearches = 0;
    using var bgmMock = new MockHttp(25711, (method, path, body) =>
    {
        if (path.StartsWith("/v0/search/subjects"))
        {
            Interlocked.Increment(ref bgmSearches);
            // BangumiClient 的请求体是字面 CJK（不做 \u 转义），按字面关键词路由
            return (200, body.Contains("ワンピース", StringComparison.Ordinal) ? jpSearch : junkSearch);
        }
        if (path.StartsWith("/v0/subjects/777/subjects")) return (200, "[]");
        if (path.StartsWith("/v0/subjects/")) return (200, subject777);
        return (404, "{}");
    });

    string? patch = null;
    using var komgaMock = new MockHttp(25712, (method, path, body) =>
    {
        if (method == "PATCH" && path == "/api/v1/series/s1/metadata") { patch = body; return (204, ""); }
        if (method == "GET" && path.Contains("/thumbnails")) return (200, "[]");
        if (method == "POST" && path.StartsWith("/api/v1/books/list"))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        if (method == "GET" && path.StartsWith("/api/v1/collections"))
            return (200, "{\"content\":[],\"totalElements\":0,\"numberOfElements\":0}");
        if (method == "POST" && path.StartsWith("/api/v1/collections"))
            return (200, "{\"id\":\"c1\",\"name\":\"手动匹配\",\"seriesIds\":[]}");
        if (method == "PATCH" && path.StartsWith("/api/v1/collections")) return (204, "");
        return (200, "{}");
    });

    using var komga = new KomgaClient("http://127.0.0.1:25712", "k", null, null);
    using var bangumi = new BangumiClient(null, "http://127.0.0.1:25711");
    bangumi.MinRequestIntervalMs = 0;
    using var ai = new AiClient("http://127.0.0.1:25710", "k", "mock");
    var statePath = Path.Combine(AppContext.BaseDirectory, $"state-match-{Guid.NewGuid():N}.json");
    var state = new ScrapeStateStore(statePath);
    var batch = new BatchMatcher(komga, bangumi, new ConsoleLogger()) { Ai = ai, State = state };
    var series = new SeriesDto
    {
        Id = "s1",
        Name = "海贼王",
        LibraryId = "lib1",
        // 文件夹名带版本/来源标注：爱藏版（版本词）+ C.C（出版社段后面的供应商段）
        Url = "[尾田荣一郎][海贼王 爱藏版][Vol.01-Vol.99][东立][C.C][99完]",
        Metadata = new SeriesMetadataDto { Title = "[尾田荣一郎][海贼王][1-100完][汉化]", Links = [] },
    };
    var r = await batch.PreciseMatchAsync(series, SyncType.Meta, forceRefreshBtv: false);
    Console.WriteLine($"  => success={r.Success} matched={r.MatchedTitle} ai 调用 {aiCalls} 次，bangumi 搜索 {bgmSearches} 次");
    Check("AI 识别链匹配成功", r.Success);
    Check("匹配到正确条目（海贼王=ONE PIECE 的中文名）", r.MatchedTitle == "海贼王");
    Check("PATCH 写入 Btv 链接 subject/777", patch?.Contains("subject/777") == true);
    // PATCH 请求体可能是字面 CJK 也可能是 \u 转义，两种都认
    static bool HasText(string? s, string cjk, string escaped) =>
        s != null && (s.Contains(cjk, StringComparison.Ordinal) || s.Contains(escaped, StringComparison.Ordinal));
    Check("PATCH 含中文标题", HasText(patch, "海贼王", "\\u6d77\\u8d3c\\u738b"));
    Check("PATCH 含简介", HasText(patch, "冒险故事", "\\u5192\\u9669\\u6545\\u4e8b"));
    // 批次⑭：程序打标 = MACHINE 标签 + 文件夹名版本/格式标记（批次⑰起标题后缀用 [] 方括号）
    Check("PATCH 标题带 [爱藏版] 后缀", HasText(patch, "[爱藏版]", "[\\u7231\\u85cf\\u7248]"));
    Check("PATCH 标题带 [C.C] 来源后缀", patch?.Contains("[C.C]") == true);
    Check("PATCH 带 MACHINE 标签", patch?.Contains("\"MACHINE\"") == true);
    Check("PATCH 带 爱藏版 标签", patch?.Contains("爱藏版") == true);
    Check("PATCH titleSort 是无后缀的净标题", patch?.Contains("\"titleSort\":\"海贼王\"") == true);
    Check("识别请求只发一次", aiCalls >= 1);
    Check("状态库记下简介已刮", state.IsSummaryKnown("s1"));
    state.SaveNow();
    try { File.Delete(statePath); } catch { }
    Console.WriteLine(fail == 0 ? "\n==== matchtest 全部通过 ====" : $"\n==== matchtest {fail} FAIL ====");
    if (fail > 0) Environment.ExitCode = 1;
}

sealed class ConsoleLogger : IAppLogger
{
    public void Info(string m) => Console.WriteLine($"[INFO] {m}");
    public void Warn(string m) => Console.WriteLine($"[WARN] {m}");
    public void Error(string m) => Console.WriteLine($"[ERR ] {m}");
    public void Progress(int c, int t, string m) => Console.WriteLine($"[{c}/{t}] {m}");
}

/// <summary>极简本地 HTTP mock（裸 TCP，避免 HttpListener 需要 http.sys 权限）。</summary>
sealed class MockHttp : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, string, string, (int Status, string Body)> _handler;
    private readonly object _handlerLock = new();
    private int _requests;
    private readonly List<string> _calls = new();          // "ticks method path"（时间戳用于节流测试）
    private readonly List<long> _callTicks = new();

    public int Requests => Volatile.Read(ref _requests);

    /// <summary>服务端记录到的全部请求（"环境tick method path"），按到达顺序。</summary>
    public IReadOnlyList<string> Calls { get { lock (_handlerLock) return _calls.ToArray(); } }

    /// <summary>各请求到达时刻（Environment.TickCount64 毫秒），用于验证节流间隔。</summary>
    public IReadOnlyList<long> CallTicks { get { lock (_handlerLock) return _callTicks.ToArray(); } }

    /// <summary>统计以 method 开头且 path 含片段的请求数。</summary>
    public int Count(string method, string pathPart) =>
        Calls.Count(c => c.StartsWith(method + " ", StringComparison.Ordinal) && c.Contains(pathPart, StringComparison.Ordinal));

    /// <summary>不关心 HTTP 方法的简化构造。</summary>
    public MockHttp(int port, Func<string, string, (int Status, string Body)> handler)
        : this(port, (_, path, body) => handler(path, body)) { }

    /// <summary>(method, path, body) → (status, body)。</summary>
    public MockHttp(int port, Func<string, string, string, (int Status, string Body)> handler)
    {
        _handler = handler;
        _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { break; }
                _ = Task.Run(() => HandleAsync(client));
            }
        });
    }

    private async Task HandleAsync(System.Net.Sockets.TcpClient client)
    {
        try
        {
            using var c = client;
            using var ns = c.GetStream();
            var buf = new byte[4096];
            var raw = new List<byte>();
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = await ns.ReadAsync(buf);
                if (n <= 0) return;
                raw.AddRange(buf.Take(n));
                headerEnd = FindHeaderEnd(raw);
            }
            var header = System.Text.Encoding.ASCII.GetString(raw.ToArray(), 0, headerEnd);
            var parts = header.Split(' ');
            var method = parts.ElementAtOrDefault(0) ?? "GET";
            var path = parts.ElementAtOrDefault(1) ?? "/";
            int contentLength = 0;
            bool chunked = false;
            foreach (var line in header.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line[15..].Trim(), out contentLength);
                if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
            }

            var bodyBytes = raw.Skip(headerEnd + 4).ToList();
            if (chunked)
            {
                while (!EndsWithChunkTerminator(bodyBytes))
                {
                    int n = await ns.ReadAsync(buf);
                    if (n <= 0) break;
                    bodyBytes.AddRange(buf.Take(n));
                }
                bodyBytes = DecodeChunked(bodyBytes);
            }
            else
            {
                while (bodyBytes.Count < contentLength)
                {
                    int n = await ns.ReadAsync(buf);
                    if (n <= 0) break;
                    bodyBytes.AddRange(buf.Take(n));
                }
            }
            var body = System.Text.Encoding.UTF8.GetString(bodyBytes.ToArray());

            Interlocked.Increment(ref _requests);
            int status;
            string resp;
            // 处理器串行执行：测试闭包里的 List/变量不是线程安全的（并行刮削会并发打进来）
            lock (_handlerLock)
            {
                _callTicks.Add(Environment.TickCount64);
                _calls.Add($"{method} {path}");
                (status, resp) = _handler(method, path, body);
            }
            var payload = System.Text.Encoding.UTF8.GetBytes(resp);
            var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                       "Content-Type: application/json; charset=utf-8\r\n" +
                       $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await ns.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head));
            await ns.WriteAsync(payload);
            await ns.FlushAsync();
        }
        catch { /* 测试辅助，忽略 */ }
    }

    private static int FindHeaderEnd(List<byte> b)
    {
        for (int i = 3; i < b.Count; i++)
            if (b[i - 3] == 13 && b[i - 2] == 10 && b[i - 1] == 13 && b[i] == 10) return i - 3;
        return -1;
    }

    private static bool EndsWithChunkTerminator(List<byte> b) =>
        b.Count >= 5 && b[^5] == '0' && b[^4] == 13 && b[^3] == 10 && b[^2] == 13 && b[^1] == 10;

    private static List<byte> DecodeChunked(List<byte> b)
    {
        var outBytes = new List<byte>();
        int i = 0;
        while (i < b.Count)
        {
            int eol = i;
            while (eol + 1 < b.Count && !(b[eol] == 13 && b[eol + 1] == 10)) eol++;
            if (eol + 1 >= b.Count) break;
            var sizeText = System.Text.Encoding.ASCII.GetString(b.ToArray(), i, eol - i).Trim();
            int semi = sizeText.IndexOf(';');
            if (semi >= 0) sizeText = sizeText[..semi];
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var size) || size == 0) break;
            int start = eol + 2;
            for (int k = start; k < Math.Min(start + size, b.Count); k++) outBytes.Add(b[k]);
            i = start + size + 2;
        }
        return outBytes;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}