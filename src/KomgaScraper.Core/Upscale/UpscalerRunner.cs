using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace KomgaScraper.Core.Upscale;

/// <summary>一次单页超分的参数。</summary>
public sealed record UpscaleInvocation(
    string ExePath,
    string ModelDir,
    string ModelName,
    int Scale,
    int TileSize,
    bool Tta,
    int? GpuIndex,
    string InputPath,
    string OutputPath);

public interface IUpscalerRunner
{
    Task RunAsync(UpscaleInvocation invocation, Action<string>? log, CancellationToken ct);
}

/// <summary>超分引擎进程返回非 0 退出码。</summary>
public sealed class UpscaleProcessException(int exitCode, string stderrTail)
    : Exception($"超分引擎运行失败（退出码 {exitCode}）。{(stderrTail.Trim().Length > 0 ? "\n" + stderrTail.Trim() : "")}")
{
    public int ExitCode { get; } = exitCode;
    public string StderrTail { get; } = stderrTail;
}

/// <summary>
/// 调用 realesrgan-ncnn-vulkan.exe（一次一张图）。参数全部走 ArgumentList，不拼命令行，
/// 路径带空格/中文也不会出错；取消时杀掉整个进程树。
/// </summary>
public sealed class ProcessUpscalerRunner : IUpscalerRunner
{
    public static List<string> BuildArguments(UpscaleInvocation inv)
    {
        var args = new List<string>
        {
            "-i", inv.InputPath,
            "-o", inv.OutputPath,
            "-s", inv.Scale.ToString(),
            "-n", inv.ModelName,
            "-m", inv.ModelDir,
        };
        if (inv.TileSize > 0)
        {
            args.Add("-t");
            args.Add(inv.TileSize.ToString());
        }
        if (inv.Tta) args.Add("-x");
        if (inv.GpuIndex is { } gpu)
        {
            args.Add("-g");
            args.Add(gpu.ToString());
        }
        args.Add("-v");
        return args;
    }

    public async Task RunAsync(UpscaleInvocation inv, Action<string>? log, CancellationToken ct)
    {
        if (!File.Exists(inv.ExePath))
            throw new FileNotFoundException($"超分引擎不存在：{inv.ExePath}");

        var psi = new ProcessStartInfo
        {
            FileName = inv.ExePath,
            WorkingDirectory = Path.GetDirectoryName(inv.ExePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in BuildArguments(inv)) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke(e.Data.Trim());
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data.Trim());
                if (stderr.Length > 8000) stderr.Remove(0, stderr.Length - 6000);
            }
        };

        if (!process.Start())
            throw new UpscaleProcessException(-1, "无法启动超分引擎进程。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { /* 进程可能已经退出 */ }
        });

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            process.WaitForExit();   // 等异步事件把 stderr 读完
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { /* 忽略 */ }
            throw;
        }

        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

        string tail;
        lock (stderr) tail = stderr.ToString();
        if (process.ExitCode != 0)
            throw new UpscaleProcessException(process.ExitCode, tail);

        var output = new FileInfo(inv.OutputPath);
        if (!output.Exists || output.Length == 0)
            throw new UpscaleProcessException(process.ExitCode,
                "引擎报告成功，但没有生成输出文件。" + tail);
    }
}

public sealed record VulkanDevice(int Index, string Name);

/// <summary>
/// 探测 Vulkan 设备编号与名称。做法：让引擎带 <c>-g 999</c> 跑一次（0.3 秒内退出），
/// 它会在校验 GPU 编号前把设备表打到 stderr：
/// <c>[0 Intel(R) UHD Graphics 770]  queueC=...</c>。
/// 必须知道真实编号：默认编号 0 往往是核显（笔记本上 NVIDIA 常在 1），不显式指定就会跑在核显上。
/// </summary>
public static class VulkanProbe
{
    private static readonly Regex DeviceLine = new(@"^\[(\d+)\s+([^\]]+?)\]\s+queueC=", RegexOptions.Compiled);

    public static IReadOnlyList<VulkanDevice> Parse(string? stderr)
    {
        var devices = new List<VulkanDevice>();
        if (string.IsNullOrWhiteSpace(stderr)) return devices;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart();
            var m = DeviceLine.Match(line);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out var index)) continue;
            var name = m.Groups[2].Value.Trim();
            if (name.Length == 0) continue;
            if (devices.Any(d => d.Index == index)) continue;
            devices.Add(new VulkanDevice(index, name));
        }
        return devices;
    }

    public static async Task<IReadOnlyList<VulkanDevice>> ProbeAsync(string exePath, CancellationToken ct = default)
    {
        if (!File.Exists(exePath)) return [];
        var tempOut = Path.Combine(Path.GetTempPath(), $"komga-vulkan-probe-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(exePath);      // 存在即可：校验 GPU 编号发生在解码之前
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(tempOut);
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add("999");

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return [];
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* 忽略 */ }
                if (ct.IsCancellationRequested) throw;
                return [];
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
            return Parse(stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
        finally
        {
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { /* 忽略 */ }
        }
    }

    /// <summary>在设备表里找名字最匹配独显的编号；找不到返回 null（由调用方决定用哪个）。</summary>
    public static int? FindBestDevice(IReadOnlyList<VulkanDevice> devices, string? preferredName)
    {
        if (devices.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var best = devices.FirstOrDefault(d =>
                d.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            if (best is not null) return best.Index;
            // 名字有细微差别（驱动版本后缀等）时按包含关系找
            var loose = devices.FirstOrDefault(d =>
                d.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase) ||
                preferredName.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
            if (loose is not null) return loose.Index;
        }
        return null;
    }
}
