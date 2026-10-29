using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KomgaScraper.Core.Sources;

namespace KomgaScraper.Core.Upscale;

public readonly record struct UpscaleDownloadProgress(long Received, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;
}

/// <summary>下载/安装引擎包失败（界面据此引导用户去下载页/手动导入）。</summary>
public sealed class UpscalePackException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    public const string DownloadPage = UpscaleRuntime.DownloadPageUrl;
}

/// <summary>
/// 超分引擎（Real-ESRGAN ncnn-vulkan Windows 便携版）与模型的安装/管理。
/// 引擎和模型都不随软件分发：首次使用点「一键下载」（约 43 MB，含 3 个官方模型），
/// 下不动就打开下载页手动下载 zip，再「导入引擎包」；也支持导入自己的 ncnn 模型。
/// </summary>
public sealed class UpscaleRuntime
{
    public const string PackVersion = "0.2.5.0-20220424";
    public const string PackUrl =
        "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip";
    public const long PackBytes = 45_474_481;
    /// <summary>官方 Windows 便携包的 SHA-256（2026-10 实测）；下载后必须校验，防止被替换成可执行木马。</summary>
    public const string PackSha256 = "ABC02804E17982A3BE33675E4D471E91EA374E65B70167ABC09E31ACB412802D";
    public const string DownloadPageUrl = "https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0";
    public const string ModelDatabaseUrl = "https://openmodeldb.info/";
    public const string ProjectUrl = "https://github.com/xinntao/Real-ESRGAN";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public UpscaleRuntime(string? rootDir = null)
    {
        Root = string.IsNullOrWhiteSpace(rootDir) ? Path.Combine(AppSettings.DataDir, "upscale") : rootDir;
    }

    public string Root { get; }
    public string EngineDir => Path.Combine(Root, "engine");
    public string ModelsDir => Path.Combine(Root, "models");
    public string ExePath => Path.Combine(EngineDir, "realesrgan-ncnn-vulkan.exe");

    /// <summary>引擎是 Real-ESRGAN 的 Windows 便携版；其它系统直接提示不支持，避免白下 43MB。</summary>
    public static bool PlatformSupported => OperatingSystem.IsWindows();

    public bool EngineReady => File.Exists(ExePath);

    public string InstalledVersion
    {
        get
        {
            try
            {
                var marker = Path.Combine(EngineDir, ".pack");
                if (File.Exists(marker))
                {
                    var v = File.ReadAllText(marker).Trim();
                    if (v.Length > 0) return v;
                }
            }
            catch { /* 读取失败按未知版本处理 */ }
            return EngineReady ? "未知版本" : "未安装";
        }
    }

    /// <summary>允许环境变量覆盖下载地址（镜像/离线部署），默认官方 GitHub Release。</summary>
    public static string ResolvePackUrl()
    {
        var custom = Environment.GetEnvironmentVariable("KOMGASCRAPE_UPSCALE_PACK_URL");
        return string.IsNullOrWhiteSpace(custom) ? PackUrl : custom;
    }

    /// <summary>校验下载包；官方地址用固定哈希，自定义镜像可用 KOMGASCRAPE_UPSCALE_PACK_SHA256 覆盖（设为 skip 跳过）。</summary>
    public static string ResolvePackSha256()
    {
        var custom = Environment.GetEnvironmentVariable("KOMGASCRAPE_UPSCALE_PACK_SHA256");
        return string.IsNullOrWhiteSpace(custom) ? PackSha256 : custom.Trim();
    }

    /// <summary>模型 id 安全校验：只允许小写字母/数字/点/下划线/短横线，禁止路径分隔与上跳。</summary>
    public static bool IsSafeModelId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        if (id.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var ch in id)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-') continue;
            return false;
        }
        return char.IsLetterOrDigit(id[0]);
    }

    // ================= 模型文件 =================

    /// <summary>
    /// ncnn 从 <c>-m</c> 目录按 <c>-n</c> 找文件：轻量模型带 <c>-x{scale}</c> 后缀（官方包里 2x/3x/4x 各一套），
    /// 其余模型只有一套原生 4x 权重。
    /// </summary>
    public static IReadOnlyList<string> ModelFileNames(UpscaleModelInfo model, int scale)
    {
        if (model.Kind != UpscaleModelKind.Custom &&
            model.Id.Equals(UpscaleModelCatalog.LightweightId, StringComparison.OrdinalIgnoreCase))
        {
            return [$"{model.Id}-x{scale}.param", $"{model.Id}-x{scale}.bin"];
        }
        return [$"{model.Id}.param", $"{model.Id}.bin"];
    }

    public IReadOnlyList<string> MissingModelFiles(UpscaleModelInfo model, int scale)
    {
        if (model.Kind != UpscaleModelKind.Custom && !model.SupportsScale(scale))
            return [$"（{model.DisplayName} 只支持 {model.ScalesText}，当前选择 {scale}x）"];
        if (model.Kind == UpscaleModelKind.Custom && !model.SupportsScale(scale))
            return [$"（自定义模型 {model.DisplayName} 声明为 {model.ScalesText}，当前选择 {scale}x）"];

        var missing = new List<string>();
        foreach (var name in ModelFileNames(model, scale))
        {
            var path = Path.Combine(ModelsDir, name);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) missing.Add(name);
        }
        return missing;
    }

    public bool IsModelReady(UpscaleModelInfo model, int scale) =>
        model.SupportsScale(scale) && MissingModelFiles(model, scale).Count == 0;

    public IReadOnlyList<UpscaleModelInfo> AllModels() =>
        UpscaleModelCatalog.Official.Concat(CustomModels()).ToList();

    /// <summary>读取用户导入的自定义模型（models/*.model.json + 同名 .param/.bin）。</summary>
    public IReadOnlyList<UpscaleModelInfo> CustomModels()
    {
        if (!Directory.Exists(ModelsDir)) return [];
        var list = new List<UpscaleModelInfo>();
        foreach (var json in Directory.EnumerateFiles(ModelsDir, "*.model.json"))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<CustomModelMeta>(File.ReadAllText(json), JsonOpts);
                if (meta is null || meta.Scale is < 2 or > 4) continue;
                // 侧车文件可能是手工/第三方放进去的：id 必须过白名单，且不能顶替官方模型
                if (!IsSafeModelId(meta.Id) || UpscaleModelCatalog.Find(meta.Id) is not null) continue;
                var param = Path.Combine(ModelsDir, meta.Id + ".param");
                var bin = Path.Combine(ModelsDir, meta.Id + ".bin");
                if (!File.Exists(param) || !File.Exists(bin)) continue;
                list.Add(new UpscaleModelInfo(
                    meta.Id,
                    string.IsNullOrWhiteSpace(meta.DisplayName) ? meta.Id : meta.DisplayName,
                    UpscaleModelKind.Custom,
                    $"自定义模型（原生 {meta.Scale}x）" +
                    (string.IsNullOrWhiteSpace(meta.SourceParam) ? "" : $"；来源：{Path.GetFileName(meta.SourceParam)}"),
                    [meta.Scale],
                    new FileInfo(param).Length + new FileInfo(bin).Length,
                    meta.SourceParam ?? ""));
            }
            catch { /* 单个坏文件不影响其它模型 */ }
        }
        return list.OrderBy(m => m.DisplayName, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// 导入用户自己的 ncnn 模型（.param + .bin）。.pth/.onnx 不能直接用，需先用 chaiNNer 等工具转成 ncnn。
    /// </summary>
    public async Task<UpscaleModelInfo> ImportCustomModelAsync(
        string paramPath,
        string binPath,
        string displayName,
        int scale,
        CancellationToken ct = default)
    {
        if (scale is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(scale), "模型倍率只能是 2 / 3 / 4。");
        if (!File.Exists(paramPath)) throw new FileNotFoundException("找不到 .param 文件", paramPath);
        if (!File.Exists(binPath)) throw new FileNotFoundException("找不到 .bin 文件", binPath);
        if (new FileInfo(binPath).Length < 1024)
            throw new InvalidDataException("选择的 .bin 权重文件太小，不像是 ncnn 模型。");
        if (!string.Equals(Path.GetFileNameWithoutExtension(paramPath),
                Path.GetFileNameWithoutExtension(binPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("选择的 .param 与 .bin 文件名不一致，请选同一模型的两个文件。");

        string head;
        await using (var fs = new FileStream(paramPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
        {
            var buf = new byte[64];
            var n = await fs.ReadAsync(buf, ct).ConfigureAwait(false);
            head = Encoding.ASCII.GetString(buf, 0, n).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        }
        if (!head.StartsWith("7767517", StringComparison.Ordinal))
            throw new InvalidDataException(
                "这不是 ncnn 模型：.param 文件应以「7767517」开头。\n" +
                "PyTorch 的 .pth / .onnx 不能直接导入，需要先用 chaiNNer 等工具转成 ncnn 的 .param + .bin。");

        Directory.CreateDirectory(ModelsDir);
        var id = MakeUniqueId(displayName);
        var paramDest = Path.Combine(ModelsDir, id + ".param");
        var binDest = Path.Combine(ModelsDir, id + ".bin");
        File.Copy(paramPath, paramDest, true);
        File.Copy(binPath, binDest, true);

        var meta = new CustomModelMeta
        {
            Id = id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim(),
            Scale = scale,
            SourceParam = paramPath,
            ImportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        await File.WriteAllTextAsync(Path.Combine(ModelsDir, id + ".model.json"),
            JsonSerializer.Serialize(meta, JsonOpts), ct).ConfigureAwait(false);

        return new UpscaleModelInfo(
            meta.Id, meta.DisplayName, UpscaleModelKind.Custom,
            $"自定义模型（原生 {scale}x）；来源：{Path.GetFileName(paramPath)}",
            [scale], new FileInfo(paramDest).Length + new FileInfo(binDest).Length, paramPath);
    }

    public bool RemoveCustomModel(string id)
    {
        if (!IsSafeModelId(id) || UpscaleModelCatalog.Find(id) is not null) return false;
        var removed = false;
        foreach (var path in new[]
                 {
                     Path.Combine(ModelsDir, id + ".param"),
                     Path.Combine(ModelsDir, id + ".bin"),
                     Path.Combine(ModelsDir, id + ".model.json"),
                 })
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); removed = true; }
            }
            catch { /* 文件被占用时忽略，界面会提示失败 */ }
        }
        return removed;
    }

    private string MakeUniqueId(string displayName)
    {
        var sb = new StringBuilder();
        foreach (var ch in (displayName ?? "").Trim())
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch is ' ' or '(' or ')' or '（' or '）') sb.Append('_');
        }
        var baseId = sb.ToString().Trim('_', '.', '-');
        if (baseId.Length == 0) baseId = "custom";
        if (baseId.Length > 60) baseId = baseId[..60];

        var id = baseId;
        // 不能和官方模型同名，否则会覆盖随引擎包下载的权重
        for (var i = 2; File.Exists(Path.Combine(ModelsDir, id + ".param")) || UpscaleModelCatalog.Find(id) is not null; i++)
        {
            id = $"{baseId}-{i}";
            if (i > 99) { id = $"{baseId}-{Guid.NewGuid():N}"[..Math.Min(70, baseId.Length + 9)]; break; }
        }
        return id;
    }

    // ================= 引擎安装 =================

    /// <summary>一键下载并安装引擎包（含 3 个官方模型）。失败抛 <see cref="UpscalePackException"/>。</summary>
    public async Task DownloadAndInstallAsync(
        IProgress<UpscaleDownloadProgress>? progress,
        ProxyConfig? proxy,
        IAppLogger log,
        CancellationToken ct)
    {
        var url = ResolvePackUrl();
        if (!PlatformSupported)
            throw new UpscalePackException("图片超分目前只支持 Windows：引擎是 Real-ESRGAN 的 Windows 便携版（Vulkan）。");
        var tempDir = Path.Combine(Path.GetTempPath(), "KomgaScraper-upscale");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, $"engine-{Guid.NewGuid():N}.zip");
        try
        {
            log.Info($"[超分] 开始下载引擎包：{url}");
            using var http = HttpFactory.Create(proxy, TimeSpan.FromMinutes(30),
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) KomgaScraper-Upscale");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new UpscalePackException($"下载引擎包失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

            var total = resp.Content.Headers.ContentLength ?? PackBytes;
            long received = 0;
            var lastReport = 0.0;
            await using (var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    var frac = total > 0 ? (double)received / total : 0;
                    if (frac - lastReport >= 0.005 || received == total)
                    {
                        lastReport = frac;
                        progress?.Report(new UpscaleDownloadProgress(received, total));
                    }
                }
            }

            if (received < 40_000_000)
                throw new UpscalePackException(
                    $"下载的文件不完整（{ComicArchiveReader.FormatBytes(received)}，正常约 43 MB）。" +
                    "可能是网络被中断，请重试或手动下载 zip 后导入。");
            if (resp.Content.Headers.ContentLength is { } declaredLength && received != declaredLength)
                throw new UpscalePackException(
                    $"下载被截断（收到 {received} / {declaredLength} 字节），请重试或手动下载 zip 后导入。");

            // 这个 zip 里的 exe 会被执行：必须校验官方 SHA-256（自定义镜像可用环境变量覆盖）
            var expectedHash = ResolvePackSha256();
            if (!expectedHash.Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                string actualHash;
                await using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                {
                    actualHash = Convert.ToHexString(await System.Security.Cryptography.SHA256
                        .HashDataAsync(fs, ct).ConfigureAwait(false));
                }
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpscalePackException(
                        "引擎包 SHA-256 校验失败：文件可能被篡改或下载到了错误的版本。\n" +
                        $"实际：{actualHash}\n期望：{expectedHash}\n" +
                        $"请从官方下载页重新下载：{DownloadPageUrl}");
                }
            }

            log.Info($"[超分] 引擎包下载完成（{ComicArchiveReader.FormatBytes(received)}，SHA-256 校验通过），正在安装…");
            await InstallPackFromZipAsync(zipPath, log, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UpscalePackException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new UpscalePackException(
                $"下载引擎包失败：{e.Message}\n" +
                "可以直接打开下载页手动下载，再用「导入引擎包」安装。", e);
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* 忽略 */ }
        }
    }

    /// <summary>安装本地 zip（官方 Windows 便携包，或用户手动下载的同一个包）。</summary>
    public async Task InstallPackFromZipAsync(string zipPath, IAppLogger log, CancellationToken ct = default)
    {
        if (!PlatformSupported)
            throw new UpscalePackException("图片超分目前只支持 Windows：引擎是 Real-ESRGAN 的 Windows 便携版（Vulkan）。");
        if (!File.Exists(zipPath))
            throw new UpscalePackException($"找不到引擎包文件：{zipPath}");

        var tmp = Path.Combine(Path.GetTempPath(), "KomgaScraper-upscale-pack", Guid.NewGuid().ToString("N"));
        string? staging = null;
        string? backup = null;
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(tmp);
                ZipFile.ExtractToDirectory(zipPath, tmp, overwriteFiles: true);
            }, ct).ConfigureAwait(false);

            var exe = Directory.EnumerateFiles(tmp, "realesrgan-ncnn-vulkan.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (exe is null)
                throw new UpscalePackException(
                    "这个 zip 不是 Real-ESRGAN 的 Windows 便携包（里面找不到 realesrgan-ncnn-vulkan.exe）。\n" +
                    $"请从下载页下载：{DownloadPageUrl}");

            var packDir = Path.GetDirectoryName(exe)!;
            var packModelsDir = Path.Combine(packDir, "models");
            if (!Directory.Exists(packModelsDir))
                throw new UpscalePackException("便携包里没有 models 文件夹，可能下载到了错误的文件。");

            var modelFiles = Directory.EnumerateFiles(packModelsDir)
                .Where(f => f.EndsWith(".param", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (modelFiles.Count == 0)
                throw new UpscalePackException("便携包的 models 文件夹里没有 .param/.bin 模型文件。");

            // 模型拷到独立目录（自定义模型也在那，不受引擎替换影响）；官方文件与旧版同名，覆盖即可
            Directory.CreateDirectory(ModelsDir);
            foreach (var file in modelFiles)
                File.Copy(file, Path.Combine(ModelsDir, Path.GetFileName(file)), true);

            // 组装新引擎到 staging，全部校验通过后才替换 EngineDir：坏包/截断包不会把旧引擎弄坏
            Directory.CreateDirectory(Root);
            staging = Path.Combine(Root, "engine.new-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var dlls = 0;
            foreach (var file in Directory.EnumerateFiles(packDir))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(staging, name), true);
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) dlls++;
            }
            if (!File.Exists(Path.Combine(staging, "realesrgan-ncnn-vulkan.exe")))
                throw new UpscalePackException("引擎 exe 复制失败，请检查软件目录写入权限。");
            await File.WriteAllTextAsync(Path.Combine(staging, ".pack"), PackVersion, ct).ConfigureAwait(false);

            if (Directory.Exists(EngineDir))
            {
                backup = Path.Combine(Root, "engine.old-" + Guid.NewGuid().ToString("N"));
                Directory.Move(EngineDir, backup);
            }
            try
            {
                Directory.Move(staging, EngineDir);
                staging = null;
            }
            catch
            {
                if (backup is not null && !Directory.Exists(EngineDir))
                {
                    try { Directory.Move(backup, EngineDir); backup = null; } catch { /* finally 再兜底 */ }
                }
                throw;
            }

            if (backup is not null)
            {
                try { Directory.Delete(backup, true); backup = null; }
                catch (Exception e) { log.Warn($"[超分] 旧引擎备份清理失败（可手动删）：{backup}：{e.Message}"); }
            }

            if (!EngineReady)
                throw new UpscalePackException("安装后仍找不到 realesrgan-ncnn-vulkan.exe，请检查软件目录写入权限。");

            log.Info($"[超分] 引擎安装完成：{EngineDir}（{dlls} 个 DLL，{modelFiles.Count} 个模型文件）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UpscalePackException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new UpscalePackException($"安装引擎包失败：{e.Message}", e);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* 忽略 */ }
            if (staging is not null)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* 忽略 */ }
            }
            if (backup is not null)
            {
                // 替换失败时尽量把旧引擎还原，保证「导入坏包不弄坏可用引擎」
                if (!Directory.Exists(EngineDir))
                {
                    try { Directory.Move(backup, EngineDir); } catch { /* 忽略 */ }
                }
                else
                {
                    try { Directory.Delete(backup, true); } catch { /* 忽略 */ }
                }
            }
        }
    }

    // ================= 打开链接 =================

    public static bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class CustomModelMeta
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
        [JsonPropertyName("scale")] public int Scale { get; set; }
        [JsonPropertyName("sourceParam")] public string SourceParam { get; set; } = "";
        [JsonPropertyName("importedAt")] public string ImportedAt { get; set; } = "";
    }
}
