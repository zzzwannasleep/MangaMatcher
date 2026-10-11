using System.Text.Json;
using System.Text.Json.Serialization;
using KomgaScraper.Core.Sources;

namespace KomgaScraper.Core;

/// <summary>
/// 应用设置。**便携式存储**：设置、BookWalker 数据库与索引一律放在程序 exe 同目录的
/// <c>data</c> 子目录里（绿色软件），不写 %APPDATA%、%TEMP% 或任何系统目录，
/// 因此不需要任何额外目录权限，删掉文件夹即完全卸载。
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("komgaUrl")] public string KomgaUrl { get; set; } = "http://localhost:25600";
    [JsonPropertyName("komgaApiKey")] public string KomgaApiKey { get; set; } = "";
    [JsonPropertyName("komgaUsername")] public string KomgaUsername { get; set; } = "";
    [JsonPropertyName("komgaPassword")] public string KomgaPassword { get; set; } = "";
    [JsonPropertyName("bangumiAccessToken")] public string BangumiAccessToken { get; set; } = "";
    /// <summary>Bangumi API 基址覆盖（留空用官方 https://api.bgm.tv；测试可指向 mock）。</summary>
    [JsonPropertyName("bangumiApiBase")] public string BangumiApiBase { get; set; } = "";
    /// <summary>旧版单字段代理（"127.0.0.1:7890"）；只为兼容老配置读取，新写入走下面的 proxy* 字段。</summary>
    [JsonPropertyName("bangumiProxy")] public string LegacyBangumiProxy { get; set; } = "";
    /// <summary>代理协议：""（不使用）/ http / https / socks4 / socks4a / socks5。</summary>
    [JsonPropertyName("proxyScheme")] public string ProxyScheme { get; set; } = "";
    /// <summary>代理主机（域名或 IP，不含端口）。</summary>
    [JsonPropertyName("proxyHost")] public string ProxyHost { get; set; } = "";
    [JsonPropertyName("proxyPort")] public int ProxyPort { get; set; } = 7890;
    /// <summary>代理账号（可选，支持 HTTP Basic 与 SOCKS5 用户名口令）。</summary>
    [JsonPropertyName("proxyUser")] public string ProxyUser { get; set; } = "";
    [JsonPropertyName("proxyPassword")] public string ProxyPassword { get; set; } = "";
    /// <summary>BookWalker 目录库下载地址。</summary>
    [JsonPropertyName("bkwkUrl")] public string BkwkUrl { get; set; } = "https://data.bookwalker.com/bkwk-db.sqlite.zst";
    /// <summary>启用 AI 匹配（OpenAI 兼容接口，候选模糊时智能选择）。</summary>
    [JsonPropertyName("aiEnabled")] public bool AiEnabled { get; set; }
    [JsonPropertyName("aiBaseUrl")] public string AiBaseUrl { get; set; } = "https://api.deepseek.com";
    [JsonPropertyName("aiApiKey")] public string AiApiKey { get; set; } = "";
    [JsonPropertyName("aiModel")] public string AiModel { get; set; } = "deepseek-chat";
    /// <summary>AI 简介翻译：刮削时把非中文简介（日文/英文）翻译成简体中文。</summary>
    [JsonPropertyName("aiTranslateSummary")] public bool AiTranslateSummary { get; set; }
    [JsonPropertyName("matchType")] public string MatchType { get; set; } = "漫画";
    [JsonPropertyName("fetchVolumeData")] public bool FetchVolumeData { get; set; }
    /// <summary>批量刮削时同时处理的系列数量（1=逐个，默认 3，最大 8；Bangumi 请求仍按节流间隔排队）。</summary>
    [JsonPropertyName("scrapeConcurrency")] public int ScrapeConcurrency { get; set; } = 3;
    /// <summary>BookWalker 推断字段：类型(genres)/题材(tags)/出版品牌/阅读方向 是否写入 Komga。</summary>
    [JsonPropertyName("bwWriteTags")] public bool BwWriteTags { get; set; } = true;
    /// <summary>百度百科兜底源：其它源都查不到时按中文名查词条（国内直连，不走代理）。</summary>
    [JsonPropertyName("baikeEnabled")] public bool BaikeEnabled { get; set; } = true;
    /// <summary>MangaDex 源（免密钥公开 API）：bgm/目录站/百科都没有但英文区收录的作品（走代理）。</summary>
    [JsonPropertyName("mangadexEnabled")] public bool MangaDexEnabled { get; set; } = true;
    /// <summary>只匹配日本漫画：韩漫/美漫/国漫候选在匹配阶段一律剔除（本地库只有日漫）。</summary>
    [JsonPropertyName("japaneseOnly")] public bool JapaneseOnly { get; set; } = true;
    /// <summary>
    /// Komga 连接是否走代理。默认关：Komga 通常在本机/内网（localhost），走代理反而绕远甚至被拒。
    /// Komga 部署在境外/跨网段且直连超时时打开（走下面的统一代理）。
    /// </summary>
    [JsonPropertyName("komgaProxyEnabled")] public bool KomgaProxyEnabled { get; set; }
    [JsonPropertyName("forceRefreshBtv")] public bool ForceRefreshBtv { get; set; }
    [JsonPropertyName("dryRun")] public bool DryRun { get; set; } = true;
    [JsonPropertyName("lastLibraryId")] public string LastLibraryId { get; set; } = "";
    [JsonPropertyName("mylarOutputDir")] public string MylarOutputDir { get; set; } = "";
    /// <summary>界面主题："light" / "dark"（状态栏月亮/太阳按钮切换，重启保留）。</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "light";

    /// <summary>本地库缓存：打开软件即出库数据，不重复拉全库；后台按间隔自动对齐 Komga。</summary>
    [JsonPropertyName("cacheEnabled")] public bool CacheEnabled { get; set; } = true;
    /// <summary>库缓存自动对齐间隔（分钟，0=关闭自动对齐；元数据写入后总会即时回源）。</summary>
    [JsonPropertyName("cacheRefreshMinutes")] public int CacheRefreshMinutes { get; set; } = 60;

    /// <summary>标记白名单：文件夹名后缀命中即保留为标签（内置词表认不出的也能按用户口径落）。</summary>
    [JsonPropertyName("tagWhitelist")] public List<string> TagWhitelist { get; set; } = [];
    /// <summary>标记黑名单：命中的后缀不落标签，标题里残留的旧后缀也会被清掉。</summary>
    [JsonPropertyName("tagBlacklist")] public List<string> TagBlacklist { get; set; } = [];

    // ================= 便携式路径（全部在 exe 同目录下） =================

    /// <summary>数据目录：&lt;exe 所在目录&gt;\data。单文件发布时即 exe 旁边的 data 文件夹。</summary>
    public static string DataDir { get; } = ResolveDataDir();

    /// <summary>设置文件完整路径。</summary>
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>BookWalker sqlite 数据库存放路径（下载后解压到这里）。</summary>
    public static string BkwkDbPath => Path.Combine(DataDir, "bkwk-db.sqlite");

    /// <summary>BookWalker 索引路径（由应用从 sqlite 构建）。</summary>
    public static string BkwkIndexPath => Path.Combine(DataDir, "bkwk-index.json.gz");

    /// <summary>库缓存路径（Komga 库/系列数据快照，打开软件即用）。</summary>
    public static string LibraryCacheFile => Path.Combine(DataDir, "library-cache.json");

    /// <summary>最近一次读写失败原因（成功时为 null）——用于把"静默失败"变成界面上可见的错误。</summary>
    public static string? LastError { get; private set; }

    private static string ResolveDataDir()
    {
        string baseDir;
        try
        {
            // 单文件发布下 AppContext.BaseDirectory 就是 exe 所在目录
            baseDir = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        }
        catch
        {
            baseDir = Directory.GetCurrentDirectory();
        }
        try
        {
            return Path.Combine(baseDir, "data");
        }
        catch
        {
            return baseDir;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读取设置；不存在或损坏时回退默认值，失败原因记录在 <see cref="LastError"/>。</summary>
    public static AppSettings Load()
    {
        LastError = null;
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                s.ScrapeConcurrency = Math.Clamp(s.ScrapeConcurrency, 1, 8);
                s.MigrateLegacyProxy();
                return s;
            }
        }
        catch (Exception e)
        {
            LastError = $"读取设置失败（{SettingsFile}）：{e.Message}";
        }
        return new AppSettings();
    }

    /// <summary>
    /// 兼容 + 自愈：
    /// ① 老版本只有一个 "127.0.0.1:7890" 字符串（有人写成 "http:127.0.0.1:7890" 也要认）；
    /// ② 早期版本把 "http:127.0.0.1" 这种带协议前缀的值写进过主机字段，这里自动修回来；
    /// ③ 修不好就清空，绝不让一个坏地址导致启动即崩（历史事故：Invalid URI: Invalid port specified）。
    /// </summary>
    public void MigrateLegacyProxy()
    {
        if (string.IsNullOrWhiteSpace(ProxyHost) && !string.IsNullOrWhiteSpace(LegacyBangumiProxy))
        {
            var (scheme, host, port, user, pass) = ProxyProtocols.Parse(LegacyBangumiProxy);
            ProxyScheme = scheme;
            ProxyHost = host;
            ProxyPort = port > 0 ? port : ProxyProtocols.DefaultPort(scheme);
            if (user.Length > 0) ProxyUser = user;
            if (pass.Length > 0) ProxyPassword = pass;
        }

        if (string.IsNullOrWhiteSpace(ProxyHost)) return;

        // 主机字段里可能混进了协议前缀 / 用户信息 / 路径（早期版本写坏过）→ 就地清洗
        var clean = ProxyProtocols.SanitizeHost(ProxyHost);
        if (clean.Length > 0) ProxyHost = clean;

        // 端口也顺手夹回合法区间（手改过 json 的情况）
        if (ProxyPort is < 1 or > 65535) ProxyPort = ProxyProtocols.DefaultPort(ProxyScheme);

        if (ProxyProtocols.Validate(ProxyScheme, ProxyHost, ProxyPort) != null)
        {
            ProxyScheme = "";
            ProxyHost = "";
            ProxyPort = 7890;
            ProxyUser = "";
            ProxyPassword = "";
        }
    }

    /// <summary>组装代理 URL（scheme://host:port，不含账密）；未启用返回 null。</summary>
    public string? ProxyUrl => ProxyProtocols.Build(ProxyScheme, ProxyHost, ProxyPort);

    /// <summary>Bangumi API、外部封面下载、BookWalker 目录库下载共用的代理。</summary>
    public ProxyConfig Proxy => new(ProxyUrl, ProxyUser, ProxyPassword);

    /// <summary>保存设置。成功返回 true；失败返回 false，原因见 <see cref="LastError"/>。</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
            LastError = null;
            return true;
        }
        catch (Exception e)
        {
            LastError = $"保存设置失败（{SettingsFile}）：{e.Message}";
            return false;
        }
    }
}
