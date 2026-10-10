using System.Text.RegularExpressions;
using KomgaScraper.Core.Komga;

namespace KomgaScraper.Core.Text;

/// <summary>
/// 版本/格式标记（对齐 KomgaMylar 的 tag_by_foldername：文件夹名关键词 → 标签 + 标题后缀）。
/// 刮削会用刮到的标题整体替换 Komga 标题，用户文件夹里区分版本的标注
/// （爱藏版/完全版/电子版/汉化组/BW原档…）会被抹平 → 多版本系列在列表里全清一色。
/// 这里从文件夹名提取这些标注，写回标题后缀 + 标签（一个后缀一个标签），保证多版本可分辨、可追溯。
/// 2026-10：① 标题后缀改用方括号（乱马1/2 [b漫版]，连续标记紧贴成一组），旧的全角/半角括号后缀会原位归一；
///           ② 支持应用层黑白名单（settings.json 的 tagWhitelist/tagBlacklist，App 层注入；
///              白名单 = 命中的后缀强制保留为标签，黑名单 = 命中的标记连同标题旧后缀一起丢弃）。
/// </summary>
public static partial class VersionMarker
{
    /// <summary>程序整理打标：凡本程序写入过元数据的系列都带此标签（用户要求 MACHINE）。</summary>
    public const string MachineTag = "MACHINE";

    /// <summary>版本词：原始写法（含繁体）→ 标题后缀词 / 标签词（对齐原脚本 FolderTagRules 并扩充实测词表）。</summary>
    private static readonly (string[] Raw, string Word, string Tag)[] VersionWords =
    [
        (["爱藏版", "愛藏版"], "爱藏版", "爱藏版"),
        (["完全版"], "完全版", "完全版"),
        (["新装版", "新裝版"], "新装版", "新装版"),
        (["典藏版"], "典藏版", "典藏版"),
        (["文库版", "文庫版"], "文库版", "文库版"),
        (["豪华版", "豪華版"], "豪华版", "豪华版"),
        (["全彩", "彩色版", "全彩版"], "全彩版", "全彩版"),
        (["漫画版", "漫畫版"], "漫画版", "漫画版"),
        (["重修版"], "重修版", "重修版"),
        (["无修", "無修", "无修正", "無修正", "无修版", "無修版"], "无修版", "无修版"),
        (["合订本", "合訂本"], "合订本", "合订本"),
        (["复刻版", "復刻版"], "复刻版", "复刻版"),
        (["电子版", "電子版"], "电子版", "电子版"),
        (["境外版", "境外"], "境外版", "境外版"),
        (["b漫版", "b漫", "bili", "bilibili", "哔哩"], "b漫版", "bili"),
    ];

    /// <summary>来源/格式提示词：命中该词的括号段视为「来源/格式标记」（BW原档、汉化组、水印…）。</summary>
    private static readonly string[] FormatHints =
    [
        "原档", "提取档", "压缩档", "汉化", "嵌", "移植", "水印",
        "电子版", "境外版", "PDF", "bili", "生肉",
    ];

    /// <summary>尾部全角括号段（用户库里最常见的标注位：…（BW原档））。</summary>
    [GeneratedRegex(@"[（]([^（()]{1,40})[）]\s*$")]
    private static partial Regex TrailBracketRegex();

    /// <summary>段内嵌套的全角括号（[机魂（境外版）] → 境外版）。</summary>
    [GeneratedRegex(@"[（]([^（()]{1,40})[）]")]
    private static partial Regex InnerBracketRegex();

    /// <summary>卷号/完结度等纯元信息段（10完、Vol.01-Vol.25、第12卷、番外、1-100完…）不是版本标记。</summary>
    [GeneratedRegex(@"番外|外传|外傳|特典|特别篇|特別篇|vol(?:ume)?s?|chap(?:ter)?\.?|连载|連載|[\d\s\-~～.．+×話话卷巻冊册完未全已结]",
        RegexOptions.IgnoreCase)]
    private static partial Regex VolumeJunkStripRegex();

    // ================= 黑白名单（应用层规则；null/空 = 只用内置词表） =================

    /// <summary>从文件夹名提取版本/格式标记（内置规则，行为与旧版一致）。</summary>
    public static List<string> Detect(string folderName) => DetectCore(folderName, null);

    /// <summary>
    /// 从文件夹名提取版本/格式标记（内置规则 + 应用层黑白名单）。
    /// 白名单：括号段含白名单词 → 整段保留为标记；括号外命中 → 词本身保留为标记；
    /// 黑名单：标记含黑名单词（或被其包含）→ 丢弃（连标题旧后缀一起清，见 <see cref="StripSuffixes"/>）。
    /// </summary>
    public static List<string> Detect(string folderName, IEnumerable<string>? whitelist, IEnumerable<string>? blacklist)
    {
        var markers = DetectCore(folderName, whitelist);
        return markers.Where(m => !IsBlacklisted(m, blacklist)).ToList();
    }

    /// <summary>被黑名单丢掉的标记（调用方用它把标题里残留的旧后缀一并清掉）。</summary>
    public static List<string> DetectBlacklisted(string folderName, IEnumerable<string>? whitelist, IEnumerable<string>? blacklist)
    {
        if (blacklist == null) return [];
        var markers = DetectCore(folderName, whitelist);
        return markers.Where(m => IsBlacklisted(m, blacklist)).ToList();
    }

    /// <summary>标记是否被黑名单命中（双向包含：黑名单词是标记的子串、或标记是黑名单词的子串都算）。</summary>
    public static bool IsBlacklisted(string marker, IEnumerable<string>? blacklist) =>
        blacklist != null && blacklist.Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => CJK.T2S(b.Trim()))
            .Any(b => marker.Contains(b, StringComparison.Ordinal) || b.Contains(marker, StringComparison.Ordinal));

    /// <summary>文件夹名中的出版社/来源关键词（TagRules.PublisherKeywords 命中第一个；供"出版社词也落一个标签"用）。</summary>
    public static string? PublisherKeyword(string folderName)
    {
        if (string.IsNullOrEmpty(folderName)) return null;
        var simplified = CJK.T2S(folderName);
        return TagRules.PublisherKeywords.FirstOrDefault(k => simplified.Contains(k, StringComparison.Ordinal));
    }

    private static List<string> DetectCore(string folderName, IEnumerable<string>? whitelist)
    {
        var markers = new List<string>();
        if (string.IsNullOrWhiteSpace(folderName)) return markers;

        var simplified = CJK.T2S(folderName);

        // ① 版本词（爱藏版/完全版/电子版…）
        foreach (var (raw, word, _) in VersionWords)
            if (raw.Any(w => simplified.Contains(w, StringComparison.Ordinal)))
                AddContained(markers, word);

        // ② 出版社段后面的供应商/汉化组段（[标题][作者][出版社][汉化组][卷数] 惯例）；
        //    没有出版社段时，退而收集命中来源/格式提示词的段
        var segs = TitleParser.ExtractBracketParts(folderName)
            .Select(p => CJK.T2S(p.Trim()))
            .Where(p => p.Length > 0)
            .ToList();
        var pubIdx = LastIndex(segs, p => !p.Contains("汉化", StringComparison.Ordinal) &&
                                          TagRules.PublisherKeywords.Any(k => p.Contains(k, StringComparison.Ordinal)));
        if (pubIdx >= 0)
        {
            if (pubIdx + 1 < segs.Count) AcceptSupplier(markers, segs[pubIdx + 1]);
        }
        else
        {
            foreach (var seg in segs)
                if (FormatHints.Any(h => seg.Contains(h, StringComparison.Ordinal)))
                    AcceptSupplier(markers, seg);
        }

        // ③ 尾部全角括号（…（BW原档）/（HM重嵌））
        var trail = TrailBracketRegex().Match(folderName);
        if (trail.Success) AcceptSupplier(markers, CJK.T2S(trail.Groups[1].Value.Trim()));

        // ④ 白名单扩展：内置词表认不出的后缀（如 高清重制/翡翠台）也能按用户口径落标签。
        //    括号段命中 → 整段作为标记（"一个后缀一个框"）；括号外命中 → 词本身作为标记。
        if (whitelist != null)
        {
            foreach (var rawWord in whitelist)
            {
                if (string.IsNullOrWhiteSpace(rawWord)) continue;
                var w = CJK.T2S(rawWord.Trim());
                if (w.Length == 0) continue;
                if (markers.Any(m => m.Contains(w, StringComparison.Ordinal))) continue;   // 已有标记覆盖该词
                var seg = segs.FirstOrDefault(s => s.Contains(w, StringComparison.Ordinal) && !IsVolumeJunk(s));
                if (seg != null) AddContained(markers, seg);
                else if (simplified.Contains(w, StringComparison.Ordinal)) AddContained(markers, w);
            }
        }

        return markers;
    }

    /// <summary>标记 → 标签词（b漫版 → bili，与库内已有标签一致；其余原样）。</summary>
    public static List<string> TagForms(IReadOnlyList<string> markers) =>
        markers.Select(m => m == "b漫版" ? "bili" : m).ToList();

    /// <summary>
    /// 标题补写标记后缀（方括号格式：乱马1/2 [b漫版]；连续标记紧贴成一组，标题与首个标记之间一个空格）。
    /// 旧版写入的全角/半角括号后缀（乱马1/2（join））会被原位归一成方括号；
    /// 标题已含该标记（任意括号格式）时不再追加。返回 newTitle；changed=true 表示标题被改写。
    /// </summary>
    public static string ApplySuffixes(string title, IReadOnlyList<string> markers, out bool changed)
    {
        var t = title;
        var normalized = CJK.T2S(t);
        changed = false;
        foreach (var word in markers)
        {
            var suffix = "[" + word + "]";
            var converted = false;
            foreach (var old in (string[])["（" + word + "）", "(" + word + ")"])
            {
                if (!t.Contains(old, StringComparison.Ordinal)) continue;
                t = t.Replace(old, suffix);
                normalized = normalized.Replace(old, suffix);
                changed = converted = true;
            }
            if (converted) continue;                                            // 已归一，无需追加
            if (normalized.Contains(word, StringComparison.Ordinal)) continue;  // 已带标记（任意格式）
            var sep = t.EndsWith("]", StringComparison.Ordinal) ? "" : " ";     // 连续标记紧贴成一组
            t += sep + suffix;
            normalized += sep + suffix;
            changed = true;
        }
        if (changed)
        {
            // 归一产生的括号是紧贴原标题的（乱马1/2[join]）→ 补一个空格（乱马1/2 [join]）
            var idx = t.IndexOf('[');
            if (idx > 0 && !char.IsWhiteSpace(t[idx - 1]))
                t = t[..idx] + " " + t[idx..];
        }
        return t;
    }

    /// <summary>黑名单命中的标记：把标题里残留的旧后缀（（word）/ [word] / (word) 三种写法）一并清掉。</summary>
    public static string StripSuffixes(string title, IReadOnlyList<string> words, out bool changed)
    {
        changed = false;
        var t = title;
        foreach (var word in words)
            foreach (var s in (string[])["（" + word + "）", "[" + word + "]", "(" + word + ")"])
            {
                if (!t.Contains(s, StringComparison.Ordinal)) continue;
                t = t.Replace(s, "");
                changed = true;
            }
        if (!changed) return title;
        while (t.Contains("  ", StringComparison.Ordinal)) t = t.Replace("  ", " ");
        return t.Trim();
    }

    /// <summary>系列所在文件夹名（oneshot 的 url 指向文件 → 取其父目录）。</summary>
    public static string FolderName(SeriesDto series)
    {
        var url = series.Url;
        if (!string.IsNullOrWhiteSpace(url))
        {
            var trimmed = url.TrimEnd('/', '\\');
            if (trimmed.Length > 0)
            {
                var name = series.Oneshot
                    ? Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "")
                    : Path.GetFileName(trimmed);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        return series.Name ?? "";
    }

    private static void AddContained(List<string> list, string item)
    {
        if (list.Any(m => m.Contains(item, StringComparison.Ordinal) ||
                          item.Contains(m, StringComparison.Ordinal))) return;
        list.Add(item);
    }

    /// <summary>供应商/格式段接受规则：长度 2–40、不是纯卷号完结度、与已有标记无包含关系；段内若嵌套（…）优先取内层。</summary>
    private static void AcceptSupplier(List<string> markers, string seg)
    {
        if (seg.Length is < 2 or > 40) return;

        if (seg.Contains('（'))
        {
            foreach (var m in InnerBracketRegex().Matches(seg).Select(x => x.Groups[1].Value.Trim()))
            {
                if (m.Length is < 2 or > 40 || IsVolumeJunk(m)) continue;
                AddContained(markers, m);
                return;
            }
            return;
        }

        if (IsVolumeJunk(seg)) return;
        AddContained(markers, seg);
    }

    private static bool IsVolumeJunk(string seg) =>
        VolumeJunkStripRegex().Replace(seg, "").Length == 0;

    private static int LastIndex(List<string> list, Func<string, bool> predicate)
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (predicate(list[i])) return i;
        return -1;
    }
}
