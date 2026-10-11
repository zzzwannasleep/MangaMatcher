using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KomgaScraper.Core.Sources;

namespace KomgaScraper.Core.Update;

/// <summary>一次更新检查的结果快照。</summary>
public sealed record UpdateStatus(
    string Current,
    string Latest,
    bool HasUpdate,
    string ReleaseUrl,
    string AssetUrl,
    string PublishedAt,
    DateTime CheckedAt,
    string Error);

/// <summary>
/// 桌面版自动更新（GitHub Releases）。
/// - 检查：api.github.com 的最新 v* Release（排除 docker-v* 与 prerelease），与当前程序集版本比较；
/// - 下载：平台对应资产（KomgaScraper-{ver}-win-x64.zip / macos-arm64 / macos-x64）落 DataDir\update\；
/// - 安装：解压到 pending → 改名旧 exe → 拷新文件 → 重启新进程；下次启动清理 .old。
/// GitHub 在国内可能直连不通：检查与下载共用设置里的统一代理。
/// </summary>
public sealed partial class UpdateService
{
    [GeneratedRegex(@"^v(\d+\.\d+\.\d+)$")]
    private static partial Regex TagRe();

    [GeneratedRegex(@"^v\d+\.\d+")]
    private static partial Regex DesktopTagPrefixRe();

    // GitHub Releases API 基址：环境变量 KOMGASCRAPE_RELEASES_API 可覆盖（本地用 tools/mock-github.js 离线自测检查链路）。
    private static readonly string RepoApi =
        Environment.GetEnvironmentVariable("KOMGASCRAPE_RELEASES_API")
        ?? "https://api.github.com/repos/zzzwannasleep/MangaMatcher/releases?per_page=30";
    public const string RepoPage = "https://github.com/zzzwannasleep/MangaMatcher/releases";

    private readonly IAppLogger _log;
    private readonly Func<ProxyConfig> _proxy;
    private HttpClient? _http;

    public UpdateService(IAppLogger log, Func<ProxyConfig> proxy)
    {
        _log = log;
        _proxy = proxy;
    }

    /// <summary>当前程序版本（CI 经 -p:Version 注入，形如 0.0.N；本地构建为 0.0.0 = 开发版）。</summary>
    public static string CurrentVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        return v is null || v.Build < 0 ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private HttpClient Http()
    {
        if (_http != null) return _http;
        _http = HttpFactory.Create(_proxy(), TimeSpan.FromSeconds(30),
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            warn: m => _log.Warn(m));
        return _http;
    }

    /// <summary>检查 GitHub 最新桌面版 Release（网络调用；失败返回带 Error 的快照，不抛异常）。</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        var current = CurrentVersion();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, RepoApi);
            req.Headers.Add("Accept", "application/vnd.github+json");
            using var resp = await Http().SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var releases = await resp.Content.ReadFromJsonAsync<List<GhRelease>>(ct).ConfigureAwait(false) ?? [];

            foreach (var r in releases)
            {
                if (r.Draft) continue;
                if (!DesktopTagPrefixRe().IsMatch(r.TagName ?? "")) continue;   // 排除 docker-v*
                if (r.Prerelease) continue;
                var m = TagRe().Match(r.TagName ?? "");
                if (!m.Success) continue;

                var latest = m.Groups[1].Value;
                var has = Version.TryParse(current, out var cur) && Version.TryParse(latest, out var lat)
                    ? lat > cur
                    : false;   // 本地开发版（0.0.0）不提示更新
                var asset = PickAsset(r, latest);
                _log.Info(has
                    ? $"发现新版本 v{latest}（当前 v{current}）：{r.HtmlUrl}"
                    : $"已是最新版本 v{current}");
                return new UpdateStatus(current, latest, has, r.HtmlUrl ?? RepoPage,
                    asset?.Url ?? "", r.PublishedAt ?? "", DateTime.Now, "");
            }
            _log.Warn("GitHub Releases 里没有找到桌面版（v* 正式发布）");
            return new UpdateStatus(current, "", false, RepoPage, "", "", DateTime.Now, "未找到桌面版发布");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _log.Warn($"检查更新失败: {e.Message}（GitHub 可能需要代理：设置 → 代理）");
            return new UpdateStatus(current, "", false, RepoPage, "", "", DateTime.Now, e.Message);
        }
    }

    /// <summary>挑平台资产：win-x64 / macos-arm64 / macos-x64。</summary>
    private static GhAsset? PickAsset(GhRelease r, string ver)
    {
        string suffix;
        if (OperatingSystem.IsWindows()) suffix = "win-x64";
        else if (OperatingSystem.IsMacOS())
            suffix = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "macos-arm64" : "macos-x64";
        else return null;   // 桌面版未发 Linux 资产
        var want = $"KomgaScraper-{ver}-{suffix}.zip";
        return r.Assets?.FirstOrDefault(a => string.Equals(a.Name, want, StringComparison.OrdinalIgnoreCase))
               ?? r.Assets?.FirstOrDefault(a => a.Name?.Contains(suffix, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>下载平台资产 zip 到 DataDir\update\（校验 zip 完整），返回 zip 路径。</summary>
    public async Task<string> DownloadAsync(UpdateStatus status, CancellationToken ct)
    {
        if (status.AssetUrl.Length == 0) throw new InvalidOperationException("该平台没有可下载的安装包");
        using var req = new HttpRequestMessage(HttpMethod.Get, status.AssetUrl);
        req.Headers.Add("Accept", "application/octet-stream");
        using var resp = await Http().SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var dir = Path.Combine(AppSettings.DataDir, "update");
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, $"KomgaScraper-{status.Latest}.zip");
        await using (var fs = File.Create(zipPath))
        {
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
        // 完整性冒烟：能打开 zip 即通过（GitHub 传输中断通常会留下打不开的文件）
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            if (zip.Entries.Count == 0) throw new InvalidDataException("下载的压缩包为空");
        }
        _log.Info($"新版本 v{status.Latest} 已下载：{zipPath}");
        return zipPath;
    }

    /// <summary>解压下载好的 zip 到 pending 目录（供安装）。</summary>
    public string ExtractPending(string zipPath)
    {
        var pendingDir = Path.Combine(AppSettings.DataDir, "update", "pending");
        if (Directory.Exists(pendingDir)) Directory.Delete(pendingDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, pendingDir, overwriteFiles: true);
        return pendingDir;
    }

    /// <summary>
    /// 安装并重启：旧 exe 改名 .old → 新文件拷到程序目录 → 启动新进程 → 退出当前。
    /// 下次启动时清理 .old（<see cref="CleanupOldFiles"/>）。
    /// </summary>
    public static void InstallAndRestart(string pendingDir)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("取不到当前程序路径（无法自更新）");
        var appDir = Path.GetDirectoryName(exePath)!;

        var files = Directory.GetFiles(pendingDir, "*", SearchOption.AllDirectories);
        if (files.Length == 0) throw new InvalidOperationException("待安装目录为空");

        var oldPath = exePath + ".old";
        if (File.Exists(oldPath)) { try { File.Delete(oldPath); } catch { } }
        File.Move(exePath, oldPath);   // Windows 允许改名运行中的 exe，改名即腾出原路径

        foreach (var src in files)
        {
            var rel = Path.GetRelativePath(pendingDir, src);
            var dst = Path.Combine(appDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            WorkingDirectory = appDir,
        });
        Environment.Exit(0);
    }

    /// <summary>启动时清理上次更新残留的 .old 与 update 临时目录（静默）。</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var old = exePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            var pending = Path.Combine(AppSettings.DataDir, "update", "pending");
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
        }
        catch { /* 清不掉不影响使用 */ }
    }

    // ---------- GitHub DTO ----------

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GhAsset>? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? Url { get; set; }
    }
}
