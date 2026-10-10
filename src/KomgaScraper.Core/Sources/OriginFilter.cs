namespace KomgaScraper.Core.Sources;

/// <summary>
/// 作品来源过滤：本地库只收录日本漫画（用户口径：日漫刮成韩漫 = 匹配错误）。
/// 三道闸门：① Bangumi 搜索层（谚文标题/韩漫标签/韩国平台连载）；
/// ② MangaDex（originalLanguage 服务端过滤 + 客户端兜底）；③ Mox 候选（同 Bangumi 规则）。
/// </summary>
public static class OriginFilter
{
    /// <summary>只匹配日漫（设置里可关）。默认开：本地库只有日漫。</summary>
    public static bool JapaneseOnly { get; set; } = true;

    /// <summary>文本里是否含谚文（韩文字母 AC00–D7A3）。标题/别名含谚文基本就是韩漫。</summary>
    public static bool ContainsHangul(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s)
            if (ch is >= '\uac00' and <= '\ud7a3') return true;
        return false;
    }

    /// <summary>韩漫类标签词表（语言判定也用它）。</summary>
    public static readonly string[] KoreanTags =
        ["韩漫", "韓漫", "韩国漫画", "韓國漫畫", "韩网漫", "韓网漫", "Korean"];

    private static readonly string[] KoreanPlatforms =
        ["naver", "lezhin", "kakao", "toomics", "bomtoon", "ridibooks", "ridi", "munpia", "mrblue", "comico", "joara"];

    /// <summary>
    /// 疑似韩漫条目：谚文标题/别名、韩漫类标签、韩国平台连载杂志（infobox「连载杂志」）。
    /// </summary>
    public static bool LooksKorean(string? title, string? oriTitle, string? aliases,
        IEnumerable<string>? tags, string? infoboxMagazine)
    {
        if (ContainsHangul(title) || ContainsHangul(oriTitle) || ContainsHangul(aliases)) return true;
        if (tags != null)
            foreach (var t in tags)
                if (KoreanTags.Contains(t, StringComparer.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(infoboxMagazine))
        {
            var m = infoboxMagazine.ToLowerInvariant();
            foreach (var p in KoreanPlatforms)
                if (m.Contains(p, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
