using System.Net;

namespace KomgaScraper.Core.Sources;

/// <summary>
/// 代理配置（协议 + 主机 + 端口 + 可选账号密码）。
/// .NET 6+ 的 <see cref="SocketsHttpHandler"/> 原生支持 http / https / socks4 / socks4a / socks5，
/// SOCKS5 的用户名口令鉴权（RFC 1929）通过 <see cref="WebProxy.Credentials"/> 传入。
/// 注意：MTProxy 是 Telegram 专用协议，不能代理普通 HTTP 流量，故本程序不支持。
/// </summary>
/// <remarks>
/// 刻意用普通 sealed class 而不是 record：record 会生成单参数拷贝构造函数，
/// 于是 <c>new ProxyConfig(null)</c> 会绑到拷贝构造上（编译通过、运行期 NRE）。
/// </remarks>
public sealed class ProxyConfig
{
    public ProxyConfig(string? url, string? user = null, string? password = null)
    {
        Url = url;
        User = user;
        Password = password;
    }

    public string? Url { get; }
    public string? User { get; }
    public string? Password { get; }

    public static ProxyConfig None { get; } = new(null);

    public bool IsSet => !string.IsNullOrWhiteSpace(Url);

    /// <summary>日志/界面用；不会显示密码。</summary>
    public string Display => IsSet
        ? Url + (string.IsNullOrEmpty(User) ? "" : $"（账号 {User}）")
        : "不使用";
}

public static class ProxyProtocols
{
    /// <summary>下拉项：空串 = 不使用代理。顺序即界面顺序。</summary>
    public static readonly (string Scheme, string Label)[] All =
    [
        ("", "不使用代理"),
        ("http", "HTTP"),
        ("https", "HTTPS"),
        ("socks4", "SOCKS4"),
        ("socks4a", "SOCKS4a"),
        ("socks5", "SOCKS5"),
    ];

    public static int DefaultPort(string? scheme) => (scheme ?? "").ToLowerInvariant() switch
    {
        "socks4" or "socks4a" or "socks5" => 1080,
        "https" => 443,
        _ => 7890,
    };

    public static string Label(string? scheme) =>
        All.FirstOrDefault(x => string.Equals(x.Scheme, scheme ?? "", StringComparison.OrdinalIgnoreCase)).Label
        ?? "HTTP";

    public static string NormalizeScheme(string? scheme)
    {
        var s = (scheme ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "http" or "https" or "socks4" or "socks4a" or "socks5" => s,
            _ => "",
        };
    }

    /// <summary>
    /// 校验界面上的代理配置；返回错误信息，null = 可用（协议为空表示不使用代理，永远合法）。
    /// </summary>
    public static string? Validate(string? scheme, string? host, int port)
    {
        var s = NormalizeScheme(scheme);
        if (s.Length == 0) return null;
        var h = (host ?? "").Trim();
        if (h.Length == 0) return "代理地址不能为空；不需要代理请把『协议』选成「不使用代理」。";
        if (port is < 1 or > 65535) return "代理端口必须在 1–65535 之间。";
        var isIp = IPAddress.TryParse(h, out _);
        if (h.Contains(' ') || h.Contains('/') || h.Contains('\\') || (h.Contains(':') && !isIp))
        {
            return $"代理地址格式不对：\"{h}\"。\n只填主机名或 IP（例：127.0.0.1 或 proxy.example.com），" +
                   "不要带 http://、端口号或路径。";
        }
        return null;
    }

    /// <summary>
    /// 只清洗「主机」这一个字段：把误填进来的 "http://"、"socks5:"、"user@"、"路径/"、"[::1]" 括号
    /// 都剥掉，**不动端口**（端口以界面上的端口输入框为准）。
    /// </summary>
    public static string SanitizeHost(string? host) => Parse(host).Host;

    /// <summary>组装 "scheme://host:port"；主机为空返回 null。IPv6 会自动补方括号。</summary>
    public static string? Build(string? scheme, string? host, int port)
    {
        var h = (host ?? "").Trim();
        if (h.Length == 0) return null;
        var s = NormalizeScheme(scheme);
        if (s.Length == 0) s = "http";
        if (port <= 0) port = DefaultPort(s);
        var literal = IPAddress.TryParse(h, out var ip) &&
                      ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{h}]"
            : h;
        return $"{s}://{literal}:{port}";
    }

    /// <summary>
    /// 解析各种写法："127.0.0.1:7890"、"http://127.0.0.1:7890"、
    /// "socks5://user:pass@127.0.0.1:1080"、"127.0.0.1"、"socks5://[::1]:1080"。
    /// 解析不出来就返回空主机（= 不使用代理）。
    /// </summary>
    public static (string Scheme, string Host, int Port, string User, string Password) Parse(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return ("", "", 0, "", "");

        var scheme = "";
        var idx = text.IndexOf("://", StringComparison.Ordinal);
        if (idx > 0)
        {
            scheme = NormalizeScheme(text[..idx]);
            text = text[(idx + 3)..];
        }
        else
        {
            // 容错 "http:127.0.0.1:7890"（漏了 //）、"socks5:127.0.0.1:1080" 这类写法
            var colon = text.IndexOf(':');
            if (colon > 0)
            {
                var maybe = NormalizeScheme(text[..colon]);
                if (maybe.Length > 0)
                {
                    scheme = maybe;
                    text = text[(colon + 1)..].TrimStart('/');
                }
            }
        }
        if (scheme.Length == 0) scheme = "http";

        var user = "";
        var pass = "";
        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = text[..at];
            text = text[(at + 1)..];
            var colon = userinfo.IndexOf(':');
            if (colon >= 0) { user = userinfo[..colon]; pass = userinfo[(colon + 1)..]; }
            else user = userinfo;
        }

        // 去掉路径/查询
        foreach (var sep in new[] { '/', '?', '#' })
        {
            var p = text.IndexOf(sep);
            if (p >= 0) text = text[..p];
        }

        var host = text;
        var port = 0;
        if (text.StartsWith('['))                       // IPv6: [::1]:1080
        {
            var close = text.IndexOf(']');
            if (close > 0)
            {
                host = text[1..close];
                var rest = text[(close + 1)..].TrimStart(':');
                if (rest.Length > 0) int.TryParse(rest, out port);
            }
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                host = text[..colon];
                if (!int.TryParse(text[(colon + 1)..], out port)) port = 0;
            }
        }

        host = host.Trim();
        if (host.Length == 0) return ("", "", 0, "", "");
        if (port <= 0) port = DefaultPort(scheme);
        return (scheme, host, port, user, pass);
    }
}

/// <summary>
/// HttpClient 工厂：统一「可选代理 + 超时 + UA」，代理协议由 <see cref="ProxyConfig.Url"/> 的 scheme 决定。
/// </summary>
public static class HttpFactory
{
    public static HttpClient Create(ProxyConfig? proxy, TimeSpan timeout, string? userAgent = null,
        bool keepReferrerToBookWalker = false, Action<string>? warn = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (proxy is { IsSet: true })
        {
            try
            {
                var p = new WebProxy(proxy.Url!);
                if (!string.IsNullOrEmpty(proxy.User))
                    p.Credentials = new NetworkCredential(proxy.User, proxy.Password ?? "");
                handler.Proxy = p;
                handler.UseProxy = true;
            }
            catch (Exception e)
            {
                // 代理地址写错不该让整个程序打不开：退回直连并把原因说清楚
                warn?.Invoke($"代理地址无效（{proxy.Url}）：{e.Message}；本次改为直连。" +
                             "请到 设置 → 代理 检查协议/地址/端口。");
            }
        }

        var http = new HttpClient(handler) { Timeout = timeout };
        if (!string.IsNullOrEmpty(userAgent))
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        if (keepReferrerToBookWalker)
        {
            http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            http.DefaultRequestHeaders.Referrer = new Uri("https://bookwalker.jp/");
        }
        return http;
    }
}
