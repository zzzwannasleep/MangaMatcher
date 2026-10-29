using System.Text;

namespace KomgaScraper.Core.Upscale;

/// <summary>图片顺序：按文件名自然排序（默认，符合阅读器习惯）或压缩包内原始顺序。</summary>
public enum ImageSortMode
{
    NaturalName,
    ArchiveOrder,
}

/// <summary>
/// 图片排序/过滤/文件名清洗。超分最怕页序错乱，所以这里用「自然排序」（2 在 10 前面），
/// 并把 __MACOSX、.DS_Store 这类垃圾过滤掉。
/// </summary>
public static class ImageOrder
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff",
    };

    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db", "desktop.ini", ".gitkeep",
    };

    public static bool IsImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return ImageExtensions.Contains(Path.GetExtension(path));
    }

    /// <summary>压缩包里的系统垃圾/隐藏文件（不是页面，直接忽略）。</summary>
    public static bool IsJunk(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return true;
        var name = Path.GetFileName(relativePath);
        if (JunkNames.Contains(name)) return true;
        if (name.StartsWith("._", StringComparison.Ordinal)) return true;   // macOS 资源叉
        foreach (var seg in relativePath.Split('/', '\\'))
        {
            if (seg.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 自然排序：数字段按数值比较（1 &lt; 2 &lt; 10），其余按忽略大小写的字符比较。
    /// 完全相等时用 Ordinal 兜底，保证排序稳定、可复现。
    /// </summary>
    public static int CompareNatural(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        var i = 0;
        var j = 0;
        while (i < a.Length && j < b.Length)
        {
            var ca = a[i];
            var cb = b[j];
            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var cmp = na.SequenceCompareTo(nb);
                if (cmp != 0) return cmp;
                // 数值相同（1 与 001）时，位数少的排前面，避免 "001" 抢到 "1" 前面
                var ra = i - si;
                var rb = j - sj;
                if (ra != rb) return ra.CompareTo(rb);
                continue;
            }

            var la = char.ToUpperInvariant(ca);
            var lb = char.ToUpperInvariant(cb);
            if (la != lb) return la.CompareTo(lb);
            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>按指定模式排序；ArchiveOrder 保持传入顺序（压缩包条目顺序）。</summary>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, string> pathOf, ImageSortMode mode)
    {
        var list = items.ToList();
        if (mode == ImageSortMode.ArchiveOrder) return list;

        var indexed = list.Select((item, index) => (item, index)).ToList();
        indexed.Sort((p, q) =>
        {
            var pa = pathOf(p.item);
            var pb = pathOf(q.item);
            var cmp = CompareNatural(pa, pb);
            if (cmp != 0) return cmp;
            cmp = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(pa, pb);
            return cmp != 0 ? cmp : p.index.CompareTo(q.index);
        });
        return indexed.Select(x => x.item).ToList();
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Windows 文件名清洗：非法字符换成下划线，去掉首尾点/空格，限长，避开设备名。</summary>
    public static string SanitizeFileName(string? title, string fallback = "未命名")
    {
        var raw = (title ?? "").Trim();
        if (raw.Length == 0) raw = fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (Array.IndexOf(invalid, ch) >= 0 || ch is '\r' or '\n' or '\t') sb.Append('_');
            else sb.Append(ch);
        }

        var name = sb.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0) name = fallback;
        if (name.Length > 100) name = name[..100].TrimEnd('.', ' ');
        if (name.Length == 0) name = fallback;
        // CON / NUL / COM1 这类设备名在 Windows 上无法作为文件名
        if (ReservedNames.Contains(name)) name += "_";
        return name;
    }

    /// <summary>把压缩包内的相对路径规范成 '/' 分隔，并拒绝绝对路径/上跳（zip slip）。</summary>
    public static bool TryNormalizeRelativePath(string? raw, out string normalized)
    {
        normalized = "";
        var text = (raw ?? "").Replace('\\', '/').Trim();
        while (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];
        if (text.Length == 0) return false;
        if (text.StartsWith('/') || text.Contains(':') || text.StartsWith("//", StringComparison.Ordinal)) return false;
        var segments = text.Split('/');
        foreach (var seg in segments)
        {
            if (seg.Length == 0 || seg == "." || seg == "..") return false;
        }
        normalized = string.Join('/', segments);
        return true;
    }
}
