using System.Text.RegularExpressions;

namespace KomgaScraper.Core.Text;

/// <summary>
/// 标题/卷号/日期解析，对齐 KomgaBangumi 脚本的 extractSeriesTitles、extractAndNormalizeTitle、
/// extractVolumeNumber、normalizeDate、capitalize。
/// </summary>
public static partial class TitleParser
{
    /// <summary>书名/文件名 → 卷号（Vol.x / Vol_x / (x) / （x） / 尾数字 / 第x卷 / v01）。</summary>
    [GeneratedRegex(@"vol(?:ume)?[._\s]*v?(\d{1,4})(?:完|end)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VolNumRegex();
    [GeneratedRegex(@"[（(](\d{1,4})[）)]\s*(?:完)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ParenNumRegex();
    [GeneratedRegex(@"\s(\d{1,4})$")]
    private static partial Regex TrailingNumRegex();
    [GeneratedRegex(@"第\s*(\d{1,4})\s*[卷册]\s*(?:完)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ChineseVolRegex();
    [GeneratedRegex(@"(?:^|[^\w/])v[._\s]?(\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex VPrefixNumRegex();

    public static int? ExtractVolumeNumber(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        Match m;
        if ((m = VolNumRegex().Match(name)).Success) return int.Parse(m.Groups[1].Value);
        if ((m = ParenNumRegex().Match(name)).Success) return int.Parse(m.Groups[1].Value);
        if ((m = ChineseVolRegex().Match(name)).Success) return int.Parse(m.Groups[1].Value);
        if ((m = VPrefixNumRegex().Match(name)).Success) return int.Parse(m.Groups[1].Value);
        if ((m = TrailingNumRegex().Match(name)).Success) return int.Parse(m.Groups[1].Value);

        // 非行尾兜底：卷号不在结尾（"乱马 Vol.01 [东立]"）→ 取串中最后一次出现的卷号模式。
        // 括号/vol 模式取最后一个匹配；裸数字不兜底（"乱马1/2" 的 2 会被误当卷 2）。
        var paren = ParenAnywhereRegex().Matches(name);
        if (paren.Count > 0) return int.Parse(paren[^1].Groups[1].Value);
        var volAnywhere = VolAnywhereRegex().Matches(name);
        if (volAnywhere.Count > 0) return int.Parse(volAnywhere[^1].Groups[1].Value);
        var cn = ChineseVolAnywhereRegex().Matches(name);
        if (cn.Count > 0) return int.Parse(cn[^1].Groups[1].Value);
        return null;
    }

    [GeneratedRegex(@"[（(](\d{1,4})[）)]", RegexOptions.IgnoreCase)]
    private static partial Regex ParenAnywhereRegex();
    [GeneratedRegex(@"vol(?:ume)?[._\s]*v?(\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex VolAnywhereRegex();
    [GeneratedRegex(@"第\s*(\d{1,4})\s*[卷册]", RegexOptions.IgnoreCase)]
    private static partial Regex ChineseVolAnywhereRegex();

    public static string NormalizeVolNum(string raw)
    {
        var num = ExtractVolumeNumber(raw);
        return num.HasValue ? num.Value.ToString() : "";
    }

    /// <summary>书名卷号模式（用于批量匹配中的卷识别，命名组与脚本一致）。</summary>
    [GeneratedRegex(@"(?:vol(?:ume)?s?|巻|卷|册|冊)[\W_]*?(?<volNum>\d+)|第\s*(?<volNum2>\d+)\s*(?:巻|卷|册|冊|集)|(?<volNum3>\d+)\s*(?:巻|卷|册|冊|集)",
        RegexOptions.IgnoreCase)]
    public static partial Regex VolumeTitleRegex();

    /// <summary>
    /// Komga 书 → 卷号：书名/元数据标题里的卷号模式 → 元数据 number 字段 → 0（由调用方决定是否用序号兜底）。
    /// 卷号是「把单行本封面/简介对齐到本地册」的唯一键，解析必须兜住各种命名（乱马1/2 (01)、Vol_01、第01卷…）。
    /// </summary>
    public static int ExtractBookVolumeNumber(string? bookName, string? bookTitle, string? metaNumber)
    {
        foreach (var text in new[] { bookName, bookTitle })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var match = VolumeTitleRegex().Match(text);
            if (!match.Success)
            {
                // VolumeTitleRegex 抓不到时再用行尾/括号模式（(01) 结尾、乱马 01 等）
                var n = ExtractVolumeNumber(text);
                if (n.HasValue) return n.Value;
                continue;
            }
            var volStr = match.Groups["volNum"].Success ? match.Groups["volNum"].Value
                : match.Groups["volNum2"].Success ? match.Groups["volNum2"].Value
                : match.Groups["volNum3"].Value;
            if (volStr.Length > 0 && int.TryParse(volStr, out var n2)) return n2;
        }
        // ③ Komga 自己解析的 number 字段（"01"、"3.5"→3）：文件名复杂时它通常是对的
        if (!string.IsNullOrWhiteSpace(metaNumber))
        {
            var dot = metaNumber!.IndexOf('.');
            var intPart = dot > 0 ? metaNumber[..dot] : metaNumber;
            if (int.TryParse(intPart, out var n3)) return n3;
        }
        return 0;
    }

    /// <summary>日期归一：YYYY年M月D日 / YYYY年M月 / YYYY-M-D / YYYY-M → YYYY-MM-DD(-01)。</summary>
    public static string? NormalizeDate(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return null;
        var s = dateStr.Trim();
        Match m;
        if ((m = Regex.Match(s, @"^(\d{4})年(\d{1,2})月(\d{1,2})日$")).Success)
            return $"{m.Groups[1].Value}-{m.Groups[2].Value.PadLeft(2, '0')}-{m.Groups[3].Value.PadLeft(2, '0')}";
        if ((m = Regex.Match(s, @"^(\d{4})年(\d{1,2})月$")).Success)
            return $"{m.Groups[1].Value}-{m.Groups[2].Value.PadLeft(2, '0')}-01";
        if ((m = Regex.Match(s, @"^(\d{4})-(\d{1,2})-(\d{1,2})$")).Success)
            return $"{m.Groups[1].Value}-{m.Groups[2].Value.PadLeft(2, '0')}-{m.Groups[3].Value.PadLeft(2, '0')}";
        if ((m = Regex.Match(s, @"^(\d{4})-(\d{1,2})$")).Success)
            return $"{m.Groups[1].Value}-{m.Groups[2].Value.PadLeft(2, '0')}-01";
        return null;
    }

    public static string Capitalize(string str) =>
        string.IsNullOrEmpty(str)
            ? str
            : Regex.Replace(str, @"\b[a-z]", c => c.Value.ToUpperInvariant());

    // ---------- Windows 复制副本后缀（"标题 (1)" / "标题（2）" / "标题 - 副本"） ----------

    [GeneratedRegex(@"\s*[-_－—]*\s*副本(?:\s*[（(]\d{1,3}[)）])?$")]
    private static partial Regex CopySuffixCnRegex();
    [GeneratedRegex(@"\s*[（(]\d{1,3}[)）]$")]
    private static partial Regex CopySuffixNumRegex();

    /// <summary>标题是否带复制副本后缀（旧工具把文件夹名原样写库留下的污染，如 "海贼王 (1)"）。</summary>
    public static bool HasCopySuffix(string? title) =>
        !string.IsNullOrWhiteSpace(title) &&
        (CopySuffixCnRegex().IsMatch(title) || CopySuffixNumRegex().IsMatch(title));

    /// <summary>
    /// 剥掉标题尾部的复制副本后缀："海贼王 (1)" → "海贼王"、"海贼王 - 副本 (2)" → "海贼王"。
    /// 循环剥（"XXX (1) (2)" 连环复制）；只用于程序写库的标题，不动搜索词/卷号解析。
    /// </summary>
    public static string StripCopySuffix(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title ?? "";
        var s = title.TrimEnd();
        string prev;
        do
        {
            prev = s;
            s = CopySuffixCnRegex().Replace(s, "").TrimEnd();
            s = CopySuffixNumRegex().Replace(s, "").TrimEnd();
        } while (s != prev && s.Length > 0);
        return s;
    }

    /// <summary>匹配用规范化（对齐脚本 extractAndNormalizeTitle 的替换部分）：候选词首个 → 去标点 → 去全部空白 → 小写。</summary>
    public static string ExtractAndNormalizeTitle(string str)
    {
        var candidates = ExtractSeriesTitles(str, 1);
        var title = candidates.Count > 0 ? candidates[0] : "";
        return Regex.Replace(title, @"[:：•·․,，。、'’?？!！~⁓～]", " ")
            .Replace("／", "/")
            .Trim()
            .ToLowerInvariant();
    }

    /// <summary>轻量规范化（不去空白、不小写），仅用于显示层预处理。</summary>
    public static string NormalizeForMatch(string title) =>
        Regex.Replace(title, @"[:：•·․,，。、'’?？!！~⁓～]", " ")
            .Replace("／", "/")
            .Trim();

    /// <summary>
    /// 从系列名（文件夹名/标题）提取候选搜索词，移植脚本 extractSeriesTitles：
    /// [] 分段 → 含 × 的段为作者段；title 取第一个非作者段（_ 再拆分）；
    /// 作者段按 × 拆分追加进候选；候选统一 t2s + 标点清洗 + 去重；"最小处理标题" 兜底置首。
    ///
    /// 脚本只考虑日文惯用的 [标题][作者][出版社]，但中文压制组普遍是
    /// [作者][标题][出版社][汉化组][状态]——按脚本的规则「真正的标题」根本进不了候选
    /// （例：[Boichi][最强不良传说][东立][ZD×Nearfly][25完] → 候选只剩 Boichi/ZD/Nearfly）。
    /// 因此额外把「像标题的方括号段」提到候选前面：跳过汉化组（含 ×）、跳过完整度/汉化/扫描等元信息段，
    /// 去掉纯作者段的影响；多标题用 、 和 / 再拆（例：[苍蓝钢铁战舰、苍蓝钢铁的琶音] → 两个都能搜）。
    /// </summary>
    public static List<string> ExtractSeriesTitles(string input, int limitCount = 3)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        var parts = ExtractBracketParts(input);

        string? title = null;
        List<string> authors = [];

        var authorCandidate = parts.FirstOrDefault(p => p.Contains('×'));
        if (authorCandidate != null)
        {
            authors = authorCandidate.Split('×').Select(s => CJK.T2S(s.Trim())).ToList();
            foreach (var p in parts)
            {
                if (p != authorCandidate && string.IsNullOrEmpty(title))
                    title = CJK.T2S(p.Trim());
            }
        }
        else
        {
            if (parts.Count > 0) title = CJK.T2S(parts[0].Trim());
            if (parts.Count > 1) authors = [CJK.T2S(parts[1].Trim())];
        }

        var titleParts = title != null ? Expand(title).ToList() : [];
        var combinedParts = new List<string>(titleParts);
        combinedParts.AddRange(authors);

        var cleanedTitles = combinedParts.Select(t => CJK.T2S(t.Trim())).ToList();

        // 去重 + 标点清洗
        var portedTitles = cleanedTitles
            .Select(CleanCandidate)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 方括号段兜底候选（中文压制组命名）。排序「宁缺毋滥」优先级：
        // ① 无假名的段排前（纯汉字段几乎总是中文标题；含假名的段多半是日文作者/原名，
        //    例 [アタモ卜][食物战线无异状] → 食物战线无异状 当第一词，作者名只作备选）；
        // ② 同组内按拉丁占比升序（作者/汉化组多是罗马字）。
        // 含水印/汉化/扫描等元信息的段直接剔除（zero有水印 曾被当搜索词去搜，撞名刮错）。
        var bracketTitles = new List<string>();
        foreach (var p in parts)
        {
            if (p.Contains('×')) continue;                       // 汉化组/作者组
            foreach (var seg in Expand(CJK.T2S(p.Trim())))
            {
                var c = CleanCandidate(seg);
                if (c.Length is 0 or > 60) continue;
                if (IsBoilerplateSegment(c)) continue;
                if (bracketTitles.Contains(c, StringComparer.Ordinal)) continue;
                bracketTitles.Add(c);
            }
        }
        bracketTitles = bracketTitles
            .OrderBy(HasKana)
            .ThenBy(LatinRatio)
            .ToList();

        var finalTitles = new List<string>();
        void Push(string t)
        {
            if (t.Length > 0 && !finalTitles.Contains(t, StringComparer.Ordinal)) finalTitles.Add(t);
        }

        var minimalProcessedTitle = MinimalProcessTitle(input);
        if (!IsBoilerplateSegment(minimalProcessedTitle))    // 剥完 [] 剩下的 "38全" 之类不当搜索词
            Push(minimalProcessedTitle);
        foreach (var t in bracketTitles) Push(t);
        foreach (var t in portedTitles) Push(t);
        finalTitles.RemoveAll(t => t.Length == 0);

        if (limitCount <= 0) return [];
        return finalTitles.Take(limitCount).ToList();
    }

    /// <summary>整段 + 下划线/顿号/斜杠/＆ 再拆分；整段始终排第一，避免把 "Fate/stay night" 拆碎后丢掉原名。</summary>
    private static IEnumerable<string> Expand(string segment)
    {
        var t = segment.Trim();
        if (t.Length == 0) yield break;
        yield return t;
        foreach (var p in t.Split(['_', '、', '／', '/', '＆'],
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.Equals(p, t, StringComparison.Ordinal)) yield return p;
        }
    }

    /// <summary>
    /// 提取 [] 分段（手写扫描，支持嵌套）。
    /// 正则 \[([^\[\]]+)\] 遇到 "[Fate/stay night [Unlimited Blade Works]]" 只能抓到内层段，
    /// 外层的 "Fate/stay night" 会整个丢掉 → 搜索词只剩 "Unlimited Blade Works" 这类残片。
    /// </summary>
    public static List<string> ExtractBracketParts(string input)
    {
        var parts = new List<string>();
        var depth = 0;
        var cur = new System.Text.StringBuilder();
        foreach (var ch in input)
        {
            if (ch == '[')
            {
                if (depth == 0) cur.Clear();
                else { parts.Add(cur.ToString()); cur.Clear(); }
                depth++;
            }
            else if (ch == ']')
            {
                if (depth > 0) { depth--; parts.Add(cur.ToString()); cur.Clear(); }
            }
            else if (depth > 0) cur.Append(ch);
        }
        return parts.Where(p => p.Trim().Length > 0).ToList();
    }

    private static string CleanCandidate(string t) =>
        Regex.Replace(t, @"[:：•·․,，。'’?？!！~⁓～]", " ").Replace("／", "/").Trim();

    /// <summary>明显是完整度/汉化/扫描/水印/格式等元信息的段，不要拿去当搜索词。</summary>
    private static readonly string[] BoilerplateMarkers =
    [
        "未完", "完结", "已完", "连载", "汉化", "扫描", "原档", "pdf", "高清",
        "合集", "生肉", "熟肉", "简中", "繁中", "补档", "搬运", "境外版", "单行本",
        "水印", "嵌字", "修图", "扫图", "置换", "缺页",
    ];

    /// <summary>
    /// 出版社/平台/汉化组标签（实测日志里出现过的垃圾搜索词）：
    /// 东立/台湾角川/角川/青文/长鸿/大然/尖端/bili/电子版……拿去搜只会返回整站噪声。
    /// 短段（≤8 字）包含这些词 → 元信息；长标题段不受影响。
    /// </summary>
    private static readonly string[] PublisherMarkers =
    [
        "东立", "台湾角川", "角川", "青文", "长鸿", "大然", "尖端", "玉皇朝",
        "文化传信", "文传", "文苑", "电子版", "bili", "zzgundam", "汉化组",
    ];

    private static bool IsBoilerplateSegment(string s)
    {
        var low = s.ToLowerInvariant();
        if (BoilerplateMarkers.Any(m => low.Contains(m))) return true;
        if (s.Length <= 8 && PublisherMarkers.Any(m => low.Contains(m))) return true;
        // 纯卷号/卷数区间：25、第12卷、3完、38全、Vol.01、Vol.01-Vol.18、1-100完
        return Regex.IsMatch(s,
            @"^(?:第)?\d+(?:[-~～]\d+)?[卷话話册集完全]?$|^vol\.?\s*\d+(?:\s*[-~～]\s*(?:vol\.?\s*)?\d+)?$",
            RegexOptions.IgnoreCase);
    }

    /// <summary>拉丁字母占「字母+汉字+假名」的比例；纯符号/数字返回 0.5（不参与排序偏向）。</summary>
    private static double LatinRatio(string s)
    {
        int latin = 0, cjk = 0;
        foreach (var ch in s)
        {
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
            else if (ch >= '\u3040' && ch <= '\u30ff') cjk++;        // 平假名/片假名
            else if (ch >= '\u3400' && ch <= '\u9fff') cjk++;        // 汉字
        }
        var total = latin + cjk;
        return total == 0 ? 0.5 : (double)latin / total;
    }

    /// <summary>段里是否含假名（平/片假名）。含假名的段多半是日文作者/原名，搜索词排序时靠后。</summary>
    private static bool HasKana(string s)
    {
        foreach (var ch in s)
            if (ch is >= '\u3040' and <= '\u30ff') return true;
        return false;
    }

    /// <summary>
    /// 包含匹配的残留检查（宁缺毋滥）：搜索词比候选名多出来的部分若含拉丁字母或
    /// 水印/汉化/出版社等垃圾词 → 视为误配（zero有水印 ⊃ zero 这种）。纯卷号（1/2、608话）、
    /// 版本词（爱藏版）、另一段中文标题（苍蓝钢铁的琶音）都放行。
    /// </summary>
    public static bool ResidueLooksLikeJunk(string residue)
    {
        if (string.IsNullOrEmpty(residue)) return false;
        foreach (var ch in residue)
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z') return true;
        var low = residue.ToLowerInvariant();
        if (BoilerplateMarkers.Any(m => low.Contains(m))) return true;
        if (PublisherMarkers.Any(m => low.Contains(m))) return true;
        return false;
    }

    /// <summary>最小处理标题：去 [境外版]/[单行本]/N卷/[]/【】、括号转空格、尾部标点（对齐脚本）。</summary>
    private static string MinimalProcessTitle(string input)
    {
        var s = Regex.Replace(input, @"[\(\[【（]?境外版[\)\]】）]?\s*", "");
        s = Regex.Replace(s, @"[\(\[【（]?单行本[\)\]】）]?\s*$", "");
        s = Regex.Replace(s, @"[\(\[【（]?\d+卷[\)\]】）]?\s*$", "");
        // 循环剥最内层 []：非贪婪 \[.*?\] 遇到嵌套 "[Fate/stay night [UBW]]" 只能剥一层，
        // 残留的 "]" 会变成垃圾搜索词（实测日志词:"]"）
        while (Regex.IsMatch(s, @"\[[^\[\]]*\]"))
            s = Regex.Replace(s, @"\[[^\[\]]*\]", "");
        s = s.Replace("[", "").Replace("]", "");
        s = Regex.Replace(s, @"【.*?】", "");
        s = Regex.Replace(s, @"[（()）]", " ");
        s = Regex.Replace(s, @"[_-]?\s*$", "");
        s = s.Trim();
        // 标点清洗（与 finalTitles 一致）
        s = Regex.Replace(s, @"[:：•·․,，。'’?？!！~⁓～]", " ").Replace("／", "/").Trim();
        return s;
    }
}
