using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KomgaScraper.Core.Komga;

/// <summary>
/// 假 Komga（纯 TcpListener 实现的极简 HTTP/1.1）：只实现重复清理用到的 4 条接口 + DELETE 记录，
/// 用来断言删除请求打到了哪个库、哪个接口。不用 HttpListener——部分受限环境里 HTTP.sys 起不来。
/// </summary>
sealed class MockKomga : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public string BaseUrl { get; }
    public List<LibraryDto> Libraries { get; } = [];
    public List<SeriesDto> Series { get; } = [];
    public List<BookDto> Books { get; } = [];
    public ConcurrentBag<string> Deletes { get; } = [];

    public MockKomga()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}";
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => HandleSafeAsync(client));
        }
    }

    private async Task HandleSafeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (method, path, body) = await ReadRequestAsync(stream);
                var (status, json) = Route(method, path, body);
                await WriteResponseAsync(stream, status, json);
            }
            catch
            {
                // 客户端提前断开 / 读取异常：忽略，不影响测试
            }
        }
    }

    private (int Status, string Json) Route(string method, string path, string body)
    {
        switch (path)
        {
            case "/api/v1/libraries":
                return (200, JsonSerializer.Serialize(Libraries, JsonOpts));
            case "/api/v1/series/list":
            {
                var lib = ConditionValue(body, "libraryId");
                var list = Series.Where(s => lib == null || s.LibraryId == lib).ToList();
                return (200, JsonSerializer.Serialize(new { content = list, totalElements = list.Count }, JsonOpts));
            }
            case "/api/v1/books/list":
            {
                var lib = ConditionValue(body, "libraryId");
                var sid = ConditionValue(body, "seriesId");
                var list = Books
                    .Where(b => (lib == null || b.LibraryId == lib) && (sid == null || b.SeriesId == sid))
                    .ToList();
                return (200, JsonSerializer.Serialize(new { content = list, totalElements = list.Count }, JsonOpts));
            }
        }

        if (method == "DELETE" && path.StartsWith("/api/v1/series/", StringComparison.Ordinal) &&
            path.EndsWith("/file", StringComparison.Ordinal))
        {
            Deletes.Add("series:" + path["/api/v1/series/".Length..^"/file".Length]);
            return (202, "");
        }
        if (method == "DELETE" && path.StartsWith("/api/v1/books/", StringComparison.Ordinal) &&
            path.EndsWith("/file", StringComparison.Ordinal))
        {
            Deletes.Add("book:" + path["/api/v1/books/".Length..^"/file".Length]);
            return (202, "");
        }

        return (404, JsonSerializer.Serialize(new { error = $"no route {method} {path}" }, JsonOpts));
    }

    private static string? ConditionValue(string body, string field)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("condition", out var cond)) return null;
        if (!cond.TryGetProperty("allOf", out var all)) return null;
        foreach (var item in all.EnumerateArray())
        {
            if (item.TryGetProperty(field, out var f) && f.TryGetProperty("value", out var value))
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        return null;
    }

    // ---------------- 极简 HTTP 读写 ----------------

    private static async Task<(string Method, string Path, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk);
            if (read <= 0) throw new IOException("连接提前关闭");
            buffer.Write(chunk, 0, read);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
        }

        var all = buffer.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var method = requestLine[0];
        var path = requestLine[1].Split('?')[0];   // 丢掉 query，只按 path 路由

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            if (line[..idx].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line[(idx + 1)..].Trim());
        }

        var bodyStart = headerEnd + 4;
        var body = new MemoryStream();
        var already = all.Length - bodyStart;
        if (already > 0) body.Write(all, bodyStart, already);
        while (body.Length < contentLength)
        {
            var read = await stream.ReadAsync(chunk);
            if (read <= 0) break;
            body.Write(chunk, 0, read);
        }

        return (method, path, Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>在 bytes[0..length) 里找 pattern，返回起始下标；找不到 -1。</summary>
    private static int IndexOf(byte[] haystack, int length, ReadOnlySpan<byte> pattern)
    {
        for (var i = 0; i + pattern.Length <= length; i++)
        {
            var hit = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (haystack[i + j] != pattern[j]) { hit = false; break; }
            }
            if (hit) return i;
        }
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var reason = status switch { 200 => "OK", 202 => "Accepted", _ => "Not Found" };
        var head = $"HTTP/1.1 {status} {reason}\r\n" +
                   "Content-Type: application/json; charset=utf-8\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        if (body.Length > 0) await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
