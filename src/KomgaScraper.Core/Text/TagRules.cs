namespace KomgaScraper.Core.Text;

/// <summary>
/// 标签/关键词规则集，对齐 KomgaBangumi 脚本（tagLabels/equalLabels/statusTags/adultTags/
/// publisherKeywords/authorRoles）与 KomgaMylar 的出版社关键词。
/// </summary>
public static class TagRules
{
    /// <summary>标签白名单（源自脚本 tagLabels 常量，见 CjkData.cs）。</summary>
    public static readonly HashSet<string> TagWhitelist;

    /// <summary>等价标签组（源自脚本 equalLabels），归一时取组内第一个。</summary>
    private static readonly Dictionary<string, string> EqualLabelMap;

    /// <summary>状态类标签（不进 tags，仅用于状态判定/清理）。</summary>
    public static readonly string[] StatusTags = ["连载", "连载中", "完结", "已完结", "腰斩", "停刊", "长期休载", "停止连载", "休刊"];

    /// <summary>作品产地类标签（语言/阅读方向判定用）。</summary>
    public static readonly string[] ChineseOriginTags = ["国产", "国漫", "国创", "港漫", "中漫", "内地漫画"];
    /// <summary>网页条漫类标签（webtoon/条漫，从左往右阅读）。</summary>
    public static readonly string[] WebtoonTags = ["条漫", "webtoon", "WEBTOON"];

    /// <summary>成人向标签词表（ageRating 兜底推断）。</summary>
    public static readonly string[] AdultTags =
    [
        "エロ", "官能", "乱交", "SM", "触手", "鬼畜", "催眠", "扶她", "母系", "熟女",
        "调教", "恶堕", "幼女", "R18", "成年コミック", "成人漫画", "アダルトコミック",
        "18X", "無修正", "無修", "无修", "H本", "A书", "黄漫",
    ];

    /// <summary>文件夹名出版社/来源关键词（脚本 fetchBtvSubjectByUrlAPI 内 publisherKeywords）。</summary>
    public static readonly string[] PublisherKeywords =
    [
        "台湾角川", "台湾东贩", "尖端", "青文", "东立", "长鸿", "尚禾", "大然", "龙成",
        "群英", "未来数位", "新视界", "玉皇朝", "天下", "传信", "天闻角川", "bili",
        "bilibili", "哔哩哔哩", "汉化", "生肉", "日版", "原版", "正版", "官方",
        "中文版", "简中", "繁中", "简体中文", "繁体中文", "简体", "繁体",
    ];

    /// <summary>infobox 角色键 → Komga 作者角色。</summary>
    public static readonly Dictionary<string, string> AuthorRoles = new()
    {
        ["作者"] = "writer",
        ["原作"] = "writer",
        ["分镜"] = "writer",
        ["脚本·分镜"] = "writer",
        ["脚本"] = "writer",
        ["漫画家"] = "writer",
        ["作画"] = "penciller",
        ["铅稿"] = "penciller",
        ["插图"] = "illustrator",
        ["插画家"] = "illustrator",
        ["人物原案"] = "conceptor",
        ["人物设定"] = "designer",
        ["原案"] = "story",
        ["系列构成"] = "scriptwriter",
        ["上色"] = "colorist",
    };

    static TagRules()
    {
        TagWhitelist = new HashSet<string>(CjkData.TagLabels.Split(',', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        EqualLabelMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in CjkData.EqualLabels)
        {
            var labels = group.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var label in labels)
                EqualLabelMap[label] = labels[0];
        }
    }

    /// <summary>标签是否在白名单内且不是状态标签。</summary>
    public static bool IsValidTag(string tag) =>
        TagWhitelist.Contains(tag) && !StatusTags.Contains(tag, StringComparer.Ordinal);

    /// <summary>等价标签归一（如 治癒→治愈），无匹配原样返回。</summary>
    public static string NormalizeEqualLabel(string tag) =>
        EqualLabelMap.TryGetValue(tag, out var normalized) ? normalized : tag;
}