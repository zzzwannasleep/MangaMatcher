using System.Text.RegularExpressions;
using System.Management;

namespace KomgaScraper.Core.Upscale;

public enum GpuClass
{
    Discrete,
    Integrated,
    None,
    Unknown,
}

public sealed record GpuAdapterInfo(
    string Name,
    GpuClass Class,
    string Vendor,
    long? AdapterRamBytes,
    string DriverVersion,
    string PnpId,
    bool IsVirtual);

/// <summary>显卡检测结果。</summary>
public sealed record GpuReport(
    IReadOnlyList<GpuAdapterInfo> Adapters,
    IReadOnlyList<GpuAdapterInfo> Physical,
    GpuClass Best,
    string Summary,
    string Advice,
    bool NeedsWarning,
    string Error)
{
    public bool HasDiscrete => Physical.Any(a => a.Class == GpuClass.Discrete);

    public string? PreferredName => Physical.FirstOrDefault(a => a.Class == GpuClass.Discrete)?.Name;
}

/// <summary>
/// Windows 显卡检测（WMI Win32_VideoController）。只做「独显 / 核显 / 没有 / 不确定」四档判断，
/// 用来在核显机器上提醒用户「超分会极慢」。真正的 Vulkan 设备编号由 <see cref="VulkanProbe"/> 探测。
/// </summary>
public static class GpuDetector
{
    private static readonly Regex VenRe = new(@"VEN_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    public static GpuReport Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new GpuReport([], [], GpuClass.Unknown,
                "显卡：无法检测（当前系统不是 Windows）",
                "超分引擎是 Windows 便携版，当前平台无法运行。", false, "非 Windows 平台");
        }

        try
        {
            var adapters = new List<GpuAdapterInfo>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, AdapterRAM, DriverVersion FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                var name = (mo["Name"] as string ?? "").Trim();
                if (name.Length == 0) continue;
                var pnp = (mo["PNPDeviceID"] as string ?? "").Trim();
                var driver = (mo["DriverVersion"] as string ?? "").Trim();
                long? ram = null;
                try
                {
                    var raw = mo["AdapterRAM"];
                    if (raw is not null) ram = Convert.ToInt64(raw);
                }
                catch { /* AdapterRAM 在某些驱动上读不出来 */ }

                var vendor = VendorOf(pnp, name);
                var isVirtual = LooksVirtual(name);
                var cls = isVirtual ? GpuClass.None : Classify(name, vendor);
                adapters.Add(new GpuAdapterInfo(name, cls, vendor, ram, driver, pnp, isVirtual));
            }

            if (adapters.Count == 0)
            {
                return new GpuReport([], [], GpuClass.None,
                    "显卡：未检测到显示适配器",
                    "没有检测到任何显卡。超分引擎依赖 Vulkan，继续运行大概率会直接失败；" +
                    "请先安装显卡驱动，或在有独立显卡的机器上使用。",
                    true, "");
            }

            var physical = adapters.Where(a => !a.IsVirtual).ToList();
            var best = physical.Any(a => a.Class == GpuClass.Discrete)
                ? GpuClass.Discrete
                : physical.Any(a => a.Class == GpuClass.Integrated)
                    ? GpuClass.Integrated
                    : physical.Count > 0 ? GpuClass.Unknown : GpuClass.None;

            var discrete = physical.Where(a => a.Class == GpuClass.Discrete).ToList();
            var integrated = physical.Where(a => a.Class == GpuClass.Integrated).ToList();
            var summaryParts = new List<string>();
            if (discrete.Count > 0) summaryParts.Add("独显：" + string.Join(" / ", discrete.Select(a => a.Name)));
            if (integrated.Count > 0) summaryParts.Add("核显：" + string.Join(" / ", integrated.Select(a => a.Name)));
            if (summaryParts.Count == 0) summaryParts.Add("未检测到物理显卡（只有虚拟显示适配器）");
            if (physical.Any(a => a.Class == GpuClass.Unknown))
                summaryParts.Add("未知：" + string.Join(" / ", physical.Where(a => a.Class == GpuClass.Unknown).Select(a => a.Name)));

            string advice;
            var needsWarning = false;
            switch (best)
            {
                case GpuClass.Discrete:
                    advice = $"将优先使用独立显卡（{discrete[0].Name}）。若机器有多块显卡，可在「高级设置」里手动指定 GPU 编号。";
                    break;
                case GpuClass.Integrated:
                    needsWarning = true;
                    advice = $"当前设备没有独立显卡，只有集成显卡（{string.Join(" / ", integrated.Select(a => a.Name))}）。\n" +
                             "核显超分速度极慢（实测约为独显的十几分之一），并且会大量占用内存与 CPU；" +
                             "一部 200 页的漫画可能要跑几个小时。\n\n仍要继续吗？";
                    break;
                case GpuClass.None:
                    needsWarning = true;
                    advice = "没有检测到可用显卡（可能只有虚拟显示适配器，或驱动未安装）。" +
                             "超分依赖 Vulkan，继续运行大概率会失败。\n\n仍要继续吗？";
                    break;
                default:
                    advice = "无法确认显卡类型；如果这不是独立显卡，超分会非常慢。";
                    break;
            }

            return new GpuReport(adapters, physical, best,
                "显卡：" + string.Join("；", summaryParts), advice, needsWarning, "");
        }
        catch (Exception e)
        {
            return new GpuReport([], [], GpuClass.Unknown,
                "显卡：检测失败",
                $"无法检测显卡类型（{e.Message}）。如果不是独立显卡，超分会非常慢。", false, e.Message);
        }
    }

    /// <summary>按名称 + 厂商判断独显/核显（可单元测试，不依赖 WMI）。</summary>
    public static GpuClass Classify(string name, string? pnpDeviceId)
    {
        var vendor = VendorOf(pnpDeviceId, name);
        var n = (name ?? "").ToLowerInvariant();

        switch (vendor)
        {
            case "nvidia":
                return GpuClass.Discrete;
            case "intel":
                // Intel Arc A/B 系列是独显；Arc(TM) Graphics / Iris / UHD 都是核显
                if (n.Contains("arc") && Regex.IsMatch(n, @"\b[ab]\d{3}\b")) return GpuClass.Discrete;
                return GpuClass.Integrated;
            case "amd":
                if (n.Contains("radeon rx") || n.Contains("radeon pro") || n.Contains("firepro") ||
                    n.Contains("instinct") || n.Contains("radeon vii") || n.Contains("rx-"))
                    return GpuClass.Discrete;
                if (n.Contains("radeon") || n.Contains("vega") || n.Contains("ryzen")) return GpuClass.Integrated;
                return GpuClass.Unknown;
            default:
                if (n.Contains("nvidia")) return GpuClass.Discrete;
                if (n.Contains("intel") || n.Contains("uhd graphics") || n.Contains("hd graphics") || n.Contains("iris"))
                    return GpuClass.Integrated;
                if (n.Contains("amd") || n.Contains("ati ") || n.Contains("radeon")) return GpuClass.Integrated;
                return GpuClass.Unknown;
        }
    }

    /// <summary>"PCI\VEN_10DE&DEV_2D19..." → nvidia / amd / intel / other。</summary>
    public static string VendorOf(string? pnpDeviceId, string? name = null)
    {
        var m = VenRe.Match(pnpDeviceId ?? "");
        if (m.Success)
        {
            return m.Groups[1].Value.ToUpperInvariant() switch
            {
                "10DE" => "nvidia",
                "1002" or "1022" => "amd",
                "8086" => "intel",
                "1414" => "microsoft",
                _ => "other",
            };
        }

        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("nvidia")) return "nvidia";
        if (n.Contains("intel")) return "intel";
        if (n.Contains("amd") || n.Contains("ati ") || n.Contains("radeon")) return "amd";
        return "other";
    }

    /// <summary>虚拟显示器 / 远程 / 采集卡：不参与独显核显判断。</summary>
    public static bool LooksVirtual(string name)
    {
        var n = (name ?? "").ToLowerInvariant();
        string[] keys =
        [
            "virtual", "basic display", "remote display", "parsec", "gameviewer", "hyper-v",
            "citrix", "vmware", "virtualbox", "displaylink", "spacedesk", "sunshine", "idd",
            "usb display", "meta display",
        ];
        return keys.Any(n.Contains);
    }
}
