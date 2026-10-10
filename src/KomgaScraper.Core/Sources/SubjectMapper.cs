using System.Text.RegularExpressions;
using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Sources;
using KomgaScraper.Core.Text;

namespace KomgaScraper.Core.Sources;

/// <summary>
/// Bangumi 条目 → Komga 系列元数据映射，逐条对齐 KomgaBangumi 脚本 fetchBtvSubjectByUrlAPI。
/// </summary>
public static partial class SubjectMapper
{
    // ---------- infobox 解析 ----------

    /// <summary>从 infobox 提取指定 key 的值；数组以 、 连接（对齐脚本 parseInfobox）。</summary>
    public static string? ParseInfobox(List<InfoboxItemDto>? infobox, string targetKey)
    {
        if (infobox == null) return null;
        var item = infobox.FirstOrDefault(i => i.Key == targetKey);
        if (item is null || item.Value is null) return null;
        var v = item.Value.Value;
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => v.GetString(),
            System.Text.Json.JsonValueKind.Array => string.Join('、',
                v.EnumerateArray().Select(e => e.ValueKind == System.Text.Json.JsonValueKind.String
                    ? e.GetString()
                    : e.TryGetProperty("v", out var inner) && inner.ValueKind == System.Text.Json.JsonValueKind.String
                        ? inner.GetString()
                        : null)
                    .Where(s => s is not null)),
            System.Text.Json.JsonValueKind.Object => v.TryGetProperty("v", out var val)
                ? (val.ValueKind == System.Text.Json.JsonValueKind.String ? val.GetString() : val.ToString())
                : null,
            _ => null,
        };
    }

    /// <summary>提取别名（别名/別名/版本名，含嵌套），以 " / " 连接（对齐脚本 extractAliases）。</summary>
    public static string ExtractAliases(List<InfoboxItemDto>? infobox)
    {
        if (infobox == null) return "";
        var aliases = new LinkedHashSet();

        foreach (var item in infobox)
        {
            bool isAliasKey = item.Key is "别名" or "別名" or "版本名";
            if (isAliasKey && item.Value is { } v)
            {
                switch (v.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.String:
                        Add(aliases, v.GetString());
                        break;
                    case System.Text.Json.JsonValueKind.Array:
                        foreach (var e in v.EnumerateArray())
                        {
                            if (e.ValueKind == System.Text.Json.JsonValueKind.String) Add(aliases, e.GetString());
                            else if (e.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                     e.TryGetProperty("v", out var inner) &&
                                     inner.ValueKind == System.Text.Json.JsonValueKind.String)
                                Add(aliases, inner.GetString());
                        }
                        break;
                    case System.Text.Json.JsonValueKind.Object:
                        if (v.TryGetProperty("v", out var ov) && ov.ValueKind == System.Text.Json.JsonValueKind.String)
                            Add(aliases, ov.GetString());
                        break;
                }
            }

            if (item.Value is { ValueKind: System.Text.Json.JsonValueKind.Array } arr)
            {
                foreach (var sub in arr.EnumerateArray())
                {
                    if (sub.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    bool isSubAlias = sub.TryGetProperty("k", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String &&
                                      (k.GetString() == "别名" || k.GetString() == "別名" || k.GetString() == "版本名");
                    if (!isSubAlias) continue;
                    if (sub.TryGetProperty("v", out var sv))
                    {
                        if (sv.ValueKind == System.Text.Json.JsonValueKind.String) Add(aliases, sv.GetString());
                        else if (sv.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var a in sv.EnumerateArray())
                                Add(aliases, a.ValueKind == System.Text.Json.JsonValueKind.String
                                    ? a.GetString()
                                    : a.ValueKind == System.Text.Json.JsonValueKind.Object && a.TryGetProperty("v", out var av)
                                        ? av.GetString() : null);
                    }
                }
            }
        }
        return aliases.Join(" / ");

        static void Add(LinkedHashSet set, string? s)
        {
            if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
        }
    }

    /// <summary>保持插入顺序去重的字符串集合。</summary>
    private sealed class LinkedHashSet
    {
        private readonly List<string> _items = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public void Add(string s) { if (_seen.Add(s)) _items.Add(s); }
        public string Join(string sep) => string.Join(sep, _items);
    }

    // ---------- 搜索候选作者 ----------

    [GeneratedRegex(@"[《【（\[\(][^》】）\]\)]*[》】）\]\)]\s*$")]
    private static partial Regex TrailingBracketRegex();

    /// <summary>搜索候选作者：取第一个名字并去掉尾部括注（对齐脚本 fetchBtvSubjectByNameAPI）。</summary>
    public static string FirstAuthor(string authorValue)
    {
        var first = Regex.Split(authorValue, "[、→・×]")[0];
        first = TrailingBracketRegex().Replace(first, "").Trim();
        return first.Length > 0 ? first : authorValue.Trim();
    }

    // ---------- 分级 ----------

    /// <summary>ageRating 组合判定（对齐脚本 mapBangumiAgeRating）。</summary>
    public static int? MapAgeRating(int? apiAgeRating, bool? nsfw, List<string> tags)
    {
        if (apiAgeRating.HasValue)
        {
            return apiAgeRating.Value switch
            {
                1 => 15,
                2 => 18,
                0 => 0,
                _ => null,
            };
        }
        if (nsfw == true) return 18;
        if (nsfw == false) return null;
        return tags.Any(t => TagRules.AdultTags.Contains(t)) ? 18 : null;
    }

    // ---------- 主映射 ----------

    /// <summary>构建系列元数据（未合并 Komga 现有数据；合并由 MergeWithExisting 完成）。</summary>
    public static SeriesMetadataDto BuildSeriesMeta(BangumiSubjectDto btv, SeriesDto komgaSeries)
    {
        var meta = new SeriesMetadataDto
        {
            Status = "",
            StatusLock = false,
            Title = "",
            TitleLock = false,
            TitleSort = "",
            TitleSortLock = false,
            Tags = [],
            TagsLock = false,
            Links = [new LinkDto { Label = "Btv", Url = $"{BangumiClient.LegacyBase}/subject/{btv.Id}" }],
            LinksLock = false,
            Publisher = "",
            PublisherLock = false,
            Summary = "",
            SummaryLock = false,
            AlternateTitles = [],
            Authors = [],
            AuthorsLock = false,
            AgeRating = null,
            AgeRatingLock = false,
        };

        // title：中文名优先，繁→简（OpenCC）；无中文名时若原名是纯中文（含繁体）才转简，
        // 日文原名绝不过繁简转换（新字体汉字会被误转，如 進撃の巨人）
        var title = !string.IsNullOrEmpty(btv.NameCn) ? CJK.T2S(btv.NameCn)
            : AiClient.IsHanOnly(btv.Name) ? CJK.T2S(btv.Name!)
            : btv.Name ?? "";
        title = TitleParser.StripCopySuffix(title);   // 副本后缀（" (1)"）是污染不是标题
        meta.Title = title;
        meta.TitleSort = title;
        // 旧标题带副本后缀（" (1)"）→ 中文名这次必须写入顶替（MergeWithExisting 会按锁字段裁决）
        if (TitleParser.HasCopySuffix(komgaSeries.Metadata?.Title)) meta.TitleLock = false;
        if (!string.IsNullOrEmpty(btv.Name) && btv.Name != meta.Title)
            meta.AlternateTitles!.Add(new AlternateTitleDto { Label = "原名", Title = TitleParser.Capitalize(btv.Name) });

        // summary：逐行 trim（含全角空格）；纯中文（含繁体）→ 繁→简统一；日文原样（翻译链路处理）
        var rawSummary = CleanMultiline(btv.Summary);
        meta.Summary = AiClient.IsHanOnly(rawSummary) ? CJK.T2S(rawSummary) : rawSummary;

        var infobox = btv.Infobox ?? [];

        // status 先算：totalBookCount 的写入以完结状态为前提
        meta.Status = BuildStatus(btv, meta.Tags, infobox);

        // tags：白名单过滤 + count 排序 + 动态阈值（language/阅读方向判定依赖 tags，须在两者之前算好）
        var tags = BuildTags(btv, komgaSeries.Name ?? "");
        meta.Tags = tags;

        // totalBookCount：完结系列的刊行总卷数（Komga 语义 = 出全时的总册数，界面据此显示「缺多少本」）。
        // 仅完结时写：连载中的 volumes 是「已出版卷数」而非全卷数，写进去会误导；
        // eps/totalEpisodes 是话数（章节），对漫画不是册数，不再兜底（写错语义）。
        meta.TotalBookCount = string.Equals(meta.Status, "ENDED", StringComparison.Ordinal)
            ? (btv.Volumes is > 0 ? btv.Volumes : null)
            : null;
        meta.TotalBookCountLock = false;

        // 语言 + 阅读方向（别家插件刮得更齐的两块）：
        // 产地由题材标签判——韩漫 → ko、国漫/港漫 → zh，其余 → ja（韩漫候选在匹配层已被过滤，这里是双保险）；
        // 日漫从右往左；条漫/韩漫/国漫（网页条漫习惯）从左往右。小说不动阅读方向。
        meta.Language = DetectLanguage(meta.Tags);
        meta.LanguageLock = false;
        if (string.Equals(btv.Platform, "漫画", StringComparison.Ordinal))
        {
            var ltr = meta.Tags.Any(t => TagRules.WebtoonTags.Contains(t, StringComparer.OrdinalIgnoreCase)) ||
                      meta.Tags.Any(t => TagRules.ChineseOriginTags.Contains(t, StringComparer.Ordinal)) ||
                      meta.Tags.Any(t => OriginFilter.KoreanTags.Contains(t, StringComparer.OrdinalIgnoreCase));
            meta.ReadingDirection = ltr ? "LEFT_TO_RIGHT" : "RIGHT_TO_LEFT";
            meta.ReadingDirectionLock = false;
        }

        // 系列首发日：Bangumi date = 连载/出版开始日（仅完整 yyyy-MM-dd 写入，部分 Komga 校验严格）
        if (!string.IsNullOrEmpty(btv.Date) && btv.Date.Length >= 10)
            meta.ReleaseDate = btv.Date;
        meta.ReleaseDateLock = false;

        // genres：保留现有 + platform（去重：platform 每次都会加，不去重会越刮越多）
        meta.Genres = new List<string>(komgaSeries.Metadata?.Genres ?? []);
        if (!string.IsNullOrEmpty(btv.Platform) &&
            !meta.Genres.Contains(btv.Platform, StringComparer.OrdinalIgnoreCase))
        {
            meta.Genres.Add(btv.Platform);
        }

        meta.AgeRating = MapAgeRating(btv.AgeRating, btv.Nsfw, meta.Tags);

        // publisher：infobox 出版社 → 连载杂志 → 制作
        var publisherVal = ParseInfobox(infobox, "出版社")
                           ?? ParseInfobox(infobox, "连载杂志")
                           ?? ParseInfobox(infobox, "制作");
        var seriesNameS2T = CJK.T2S(komgaSeries.Name ?? "");
        var matchedKeyword = TagRules.PublisherKeywords.FirstOrDefault(k => seriesNameS2T.Contains(k, StringComparison.Ordinal));
        if (publisherVal != null)
        {
            meta.Publisher = CJK.T2S(Regex.Split(publisherVal, "[/／、_→×&,，]")[0].Trim());
        }
        else if (matchedKeyword != null && string.IsNullOrEmpty(meta.Publisher))
        {
            meta.Publisher = matchedKeyword;
        }

                // authors：infobox 角色映射
        meta.Authors = BuildAuthors(btv);

        // 别名 → alternateTitles
        var aliasStr = ExtractAliases(infobox);
        if (!string.IsNullOrEmpty(aliasStr))
        {
            foreach (var alias in aliasStr.Split(" / ", StringSplitOptions.RemoveEmptyEntries))
            {
                var aliasLower = alias.ToLowerInvariant();
                var titleLower = meta.Title?.ToLowerInvariant() ?? "";
                var oriLower = btv.Name?.ToLowerInvariant() ?? "";
                if (aliasLower != titleLower && aliasLower != oriLower &&
                    !meta.AlternateTitles!.Any(at => at.Title.ToLowerInvariant() == aliasLower))
                {
                    meta.AlternateTitles.Add(new AlternateTitleDto { Label = "别名", Title = TitleParser.Capitalize(alias) });
                }
            }
        }

        // status
        //（在 summary/totalBookCount/language 段已计算；这里保留位置注释）

        return meta;
    }

    /// <summary>按题材标签判作品产地语言（Komga language 字段）：韩漫 → ko，国漫/港漫 → zh，默认 → ja。</summary>
    private static string DetectLanguage(List<string> tags)
    {
        if (tags.Any(t => OriginFilter.KoreanTags.Contains(t, StringComparer.OrdinalIgnoreCase))) return "ko";
        if (tags.Any(t => TagRules.ChineseOriginTags.Contains(t, StringComparer.Ordinal))) return "zh";
        return "ja";
    }

    /// <summary>summary 清洗：换行统一、逐行去首尾空白（含全角空格）。</summary>
    public static string CleanMultiline(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return string.Join('\n', text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.Trim(' ', '\t', '\u3000')))
            .Trim();
    }

    /// <summary>
    /// BookWalker 的简介是 HTML（<c>&lt;p&gt;…&lt;strong&gt;</c> 与 <c>&amp;rsquo;</c> 之类实体），
    /// Komga 的 summary 是纯文本 → 去标签、块级标签转换行、还原常见实体。
    /// </summary>
    public static string StripHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = HtmlBrRegex().Replace(text, "\n");
        s = HtmlBlockEndRegex().Replace(s, "\n");
        s = HtmlTagRegex().Replace(s, "");
        s = s.Replace("&nbsp;", " ")
             .Replace("&lt;", "<")
             .Replace("&gt;", ">")
             .Replace("&quot;", "\"")
             .Replace("&#39;", "'")
             .Replace("&apos;", "'")
             .Replace("&rsquo;", "\u2019")
             .Replace("&lsquo;", "\u2018")
             .Replace("&ldquo;", "\u201c")
             .Replace("&rdquo;", "\u201d")
             .Replace("&mdash;", "\u2014")
             .Replace("&ndash;", "\u2013")
             .Replace("&hellip;", "\u2026")
             .Replace("&amp;", "&");
        return CleanMultiline(s);
    }

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBrRegex();

    [GeneratedRegex(@"</\s*(?:p|div|li|ul|ol|h[1-6]|tr|blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlockEndRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    private static List<string> BuildTags(BangumiSubjectDto btv, string komgaSeriesName)
    {
        var tags = new List<string>();
        if (btv.Tags is { Count: > 0 })
        {
            var rawApiTags = btv.Tags
                .Select(t => new { t.Name, t.Count })
                .Where(t => TagRules.IsValidTag(t.Name))
                .OrderByDescending(t => t.Count)
                .ToList();

            if (rawApiTags.Count > 0)
            {
                var maxTagCount = Math.Max(1, rawApiTags.Max(t => t.Count));
                var threshold = 3;
                if (maxTagCount > 200) threshold = 35;
                else if (maxTagCount > 125) threshold = 25;
                else if (maxTagCount > 60) threshold = 15;
                else if (maxTagCount > 30) threshold = 10;
                else if (maxTagCount > 10) threshold = 5;

                var finalTags = rawApiTags.Where(t => t.Count >= threshold).Select(t => t.Name).ToList();
                if (finalTags.Count < 10) finalTags = rawApiTags.Take(10).Select(t => t.Name).ToList();
                tags.AddRange(finalTags);
            }
        }

        // 文件夹名出版社/来源关键词追加
        var seriesNameS2T = CJK.T2S(komgaSeriesName);
        var matchedKeyword = TagRules.PublisherKeywords.FirstOrDefault(k => seriesNameS2T.Contains(k, StringComparison.Ordinal));
        if (matchedKeyword != null && !tags.Contains(matchedKeyword, StringComparer.Ordinal))
            tags.Add(matchedKeyword);

        // 评分标签
        if (btv.Rating?.Score is > 0)
            tags.Add($"{(int)Math.Round(btv.Rating.Score.Value)}分");

        // 状态标签清理：完结/已完结 只保留一个
        if (tags.Contains("已完结", StringComparer.Ordinal) || tags.Contains("完结", StringComparer.Ordinal))
        {
            var keepTag = tags.Contains("已完结", StringComparer.Ordinal) ? "已完结" : "完结";
            tags = tags.Where(t => !TagRules.StatusTags.Contains(t, StringComparer.Ordinal) || t == keepTag).ToList();
        }

        return tags;
    }

    private static string BuildStatus(BangumiSubjectDto btv, List<string> tags, List<InfoboxItemDto> infobox)
    {
        var statusVal = ParseInfobox(infobox, "状态")
                        ?? ParseInfobox(infobox, "连载状态")
                        ?? ParseInfobox(infobox, "刊行状态");
        if (statusVal == null)
        {
            statusVal = tags.FirstOrDefault(t => TagRules.StatusTags.Contains(t, StringComparer.Ordinal));
        }

        string? status = null;
        if (statusVal != null)
        {
            var s = CJK.T2S(statusVal.ToLowerInvariant());
            if (s.Contains("休刊") || s.Contains("停刊") || s.Contains("停止连载") || s.Contains("长期休载"))
                status = "HIATUS";
            else if (s.Contains("完结") || s.Contains("已完结") || s.Contains("腰斩"))
                status = "ENDED";
            else if (s.Contains("连载"))
                status = "ONGOING";
        }

        // 显式「连载中」优先（曾被卷数>0 强推 ENDED 误杀连载系列）；无显式状态时，
        // infobox 有「结束/完结」或 volumes>0（已刊行成卷且无人喊连载中）→ 推断完结。
        if (status == "ONGOING") return status;
        if (ParseInfobox(infobox, "结束") != null || ParseInfobox(infobox, "完结") != null ||
            (btv.Volumes is > 0))
        {
            status = "ENDED";
        }
        return status ?? "";
    }

    [GeneratedRegex(@"[《【（\[\(（][^》】）\]\)）]*[》】）\]\)）]")]
    private static partial Regex AuthorBracketRegex();

    private static List<AuthorDto> BuildAuthors(BangumiSubjectDto btv)
    {
        var infobox = btv.Infobox ?? [];
        var authors = new List<AuthorDto>();

        foreach (var (key, role) in TagRules.AuthorRoles)
        {
            var val = ParseInfobox(infobox, key);
            if (val == null) continue;
            var cleaned = AuthorBracketRegex().Replace(val, "");
            foreach (var name in Regex.Split(cleaned, "[/／、_→・:×&,，]"))
            {
                var trimmed = name.Trim();
                if (trimmed.Length == 0) continue;
                if (!authors.Any(a => a.Name == trimmed && a.Role == role))
                    authors.Add(new AuthorDto { Name = CJK.T2S(trimmed), Role = role });
            }
        }

        var hasWriter = authors.Any(a => a.Role == "writer");
        var hasPenciller = authors.Any(a => a.Role == "penciller");
        if (!hasWriter)
        {
            foreach (var p in authors.Where(a => a.Role == "penciller").ToList())
                if (!authors.Any(a => a.Name == p.Name && a.Role == "writer"))
                    authors.Add(new AuthorDto { Name = p.Name, Role = "writer" });
        }
        if (!hasPenciller && btv.Platform == "漫画")
        {
            foreach (var w in authors.Where(a => a.Role == "writer").ToList())
                if (!authors.Any(a => a.Name == w.Name && a.Role == "penciller"))
                    authors.Add(new AuthorDto { Name = w.Name, Role = "penciller" });
        }
        return authors;
    }

    // ---------- 合并现有元数据（对齐 filterSeriesMeta）----------

    /// <summary>
    /// 与 Komga 现有元数据合并：links 合并（新链接优先：同 label 冲突时这次刮到的链接顶替旧的，
    /// 否则"纠正匹配"永远换不掉旧的错误链接）；tags 合并+t2s+等价归一+去重；
    /// alternateTitles 合并去重（原名优先）；锁字段跳过。
    /// <paramref name="replaceSourceLinks"/> = true 时把旧的 Btv/Bw/Bof/Mox 来源链接全部剔除。
    /// </summary>
    public static SeriesMetadataDto MergeWithExisting(SeriesMetadataDto meta, SeriesMetadataDto? komgaMeta,
        bool replaceSourceLinks = false)
    {
        if (komgaMeta == null) return meta;

        // links：新链接排前面（同 label 以新为准），再并入旧链接里 label 不冲突的部分
        var existingLinks = (IEnumerable<LinkDto>)(komgaMeta.Links ?? []);
        if (replaceSourceLinks)
            existingLinks = existingLinks.Where(l => IsSourceLinkLabel(l.Label) == false);
        var combinedLinks = new List<LinkDto>(meta.Links ?? []);
        combinedLinks.AddRange(existingLinks);
        meta.Links = combinedLinks
            .Where(l => !string.IsNullOrEmpty(l.Label))
            .GroupBy(l => l.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // tags
        var combinedTags = new List<string>();
        combinedTags.AddRange(komgaMeta.Tags ?? []);
        combinedTags.AddRange(meta.Tags ?? []);
        combinedTags = combinedTags
            .Select(CJK.T2S)
            .Select(TagRules.NormalizeEqualLabel)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        meta.Tags = combinedTags;

        // alternateTitles
        var altTitles = new List<AlternateTitleDto>(komgaMeta.AlternateTitles ?? []);
        altTitles.AddRange(meta.AlternateTitles ?? []);
        meta.AlternateTitles = altTitles
            .Where(t => !string.IsNullOrEmpty(t.Title))
            .OrderBy(t => t.Label == "原名" ? 0 : t.Label == "别名" ? 2 : 1)
            .GroupBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // 锁字段：删除对应值
        if (komgaMeta.StatusLock == true) { meta.Status = null; meta.StatusLock = null; }
        if (komgaMeta.TitleLock == true) { meta.Title = null; meta.TitleLock = null; }
        if (komgaMeta.TitleSortLock == true) { meta.TitleSort = null; meta.TitleSortLock = null; }
        if (komgaMeta.SummaryLock == true) { meta.Summary = null; meta.SummaryLock = null; }
        if (komgaMeta.PublisherLock == true) { meta.Publisher = null; meta.PublisherLock = null; }
        if (komgaMeta.AgeRatingLock == true) { meta.AgeRating = null; meta.AgeRatingLock = null; }
        if (komgaMeta.TagsLock == true) { meta.Tags = null; meta.TagsLock = null; }
        if (komgaMeta.LinksLock == true) { meta.Links = null; meta.LinksLock = null; }
        if (komgaMeta.AlternateTitlesLock == true) { meta.AlternateTitles = null; meta.AlternateTitlesLock = null; }
        if (komgaMeta.AuthorsLock == true) { meta.Authors = null; meta.AuthorsLock = null; }
        if (komgaMeta.TotalBookCountLock == true) { meta.TotalBookCount = null; meta.TotalBookCountLock = null; }
        if (komgaMeta.LanguageLock == true) { meta.Language = null; meta.LanguageLock = null; }
        if (komgaMeta.ReadingDirectionLock == true) { meta.ReadingDirection = null; meta.ReadingDirectionLock = null; }
        if (komgaMeta.ReleaseDateLock == true) { meta.ReleaseDate = null; meta.ReleaseDateLock = null; }

        return meta;
    }

    /// <summary>来源链接标签（Btv=Bangumi / Bw=BookWalker / Bof+Mox=bookof.moe）；纠正匹配时会被新链接顶替。</summary>
    public static bool IsSourceLinkLabel(string? label) =>
        label is not null &&
        (label.Trim().ToUpperInvariant() is "BTV" or "BW" or "BOF" or "MOX" ||
         label.Trim().Equals("Bangumi", StringComparison.OrdinalIgnoreCase));

    /// <summary>系列封面 URL 组（image 最优先，其次 large/medium/common）。</summary>
    public static List<string> GetSeriesCoverUrls(BangumiSubjectDto btv)
    {
        var urls = new List<string>();
        if (!string.IsNullOrEmpty(btv.Image)) urls.Add(btv.Image);
        if (btv.Images != null)
        {
            if (!string.IsNullOrEmpty(btv.Images.Large)) urls.Add(btv.Images.Large);
            if (!string.IsNullOrEmpty(btv.Images.Medium)) urls.Add(btv.Images.Medium);
            if (!string.IsNullOrEmpty(btv.Images.Common)) urls.Add(btv.Images.Common);
        }
        return urls.Distinct(StringComparer.Ordinal).ToList();
    }
}