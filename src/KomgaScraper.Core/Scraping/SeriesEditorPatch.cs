using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Text;

namespace KomgaScraper.Core.Scraping;

/// <summary>
/// 编辑器保存时收集的字段。约定：null = 用户没动（不写入 PATCH），
/// 非 null = 用户明确要的值（与当前值相同则跳过）。空串对文本字段 = 想清空。
/// </summary>
public sealed record SeriesEditInput
{
    public string? Title { get; init; }
    public string? TitleSort { get; init; }
    /// <summary>ONGOING / ENDED / ABANDONED / HIATUS（空串表示清空 → Komga 不接受，会跳过）。</summary>
    public string? Status { get; init; }
    /// <summary>RIGHT_TO_LEFT / LEFT_TO_RIGHT。</summary>
    public string? ReadingDirection { get; init; }
    public string? Publisher { get; init; }
    public string? Language { get; init; }
    public string? ReleaseDate { get; init; }
    public string? Summary { get; init; }
    /// <summary>null=不改；0=清掉分级（Komga 合法值）。</summary>
    public int? AgeRating { get; init; }
    /// <summary>null=不改；只能设置正整数（Komga 校验 must be greater than 0）。</summary>
    public int? TotalBookCount { get; init; }
    /// <summary>最终标签列表（用户编辑后的全量）；Build 时自动补 MACHINE。</summary>
    public List<string>? Tags { get; init; }
    public List<string>? Genres { get; init; }
    public List<AuthorDto>? Authors { get; init; }
    public List<AlternateTitleDto>? AlternateTitles { get; init; }
    public List<LinkDto>? Links { get; init; }
}

/// <summary>单卷编辑输入（封面字节由对话框直接上传，不进 PATCH 体）。</summary>
public sealed record BookEditInput
{
    public string? Title { get; init; }
    /// <summary>显示卷号（"05"）。</summary>
    public string? Number { get; init; }
    public string? ReleaseDate { get; init; }
    public string? Isbn { get; init; }
    public string? Summary { get; init; }
}

/// <summary>
/// 单部编辑器的 PATCH 体构建（纯逻辑，可测）：
/// ① 只发用户改过的字段；② 锁字段原样回传避免翻转；③ MACHINE 标签必打 ——
/// 编辑器保存本身就是程序对库的写入，遵守"凡程序写入必打标（不是选择是必须）"。
/// </summary>
public static class SeriesEditorPatch
{
    public static Dictionary<string, object?> BuildSeriesPatch(SeriesMetadataDto current, SeriesEditInput input)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        void SetText(string key, string? newValue, string? cur)
        {
            if (newValue == null) return;
            if (string.Equals(cur ?? "", newValue, StringComparison.Ordinal)) return;
            body[key] = newValue;
        }
        SetText("title", input.Title, current.Title);
        SetText("titleSort", input.TitleSort, current.TitleSort);
        SetText("status", input.Status, current.Status);
        SetText("readingDirection", input.ReadingDirection, current.ReadingDirection);
        SetText("publisher", input.Publisher, current.Publisher);
        SetText("language", input.Language, current.Language);
        SetText("releaseDate", input.ReleaseDate, current.ReleaseDate);
        SetText("summary", input.Summary, current.Summary);
        if (input.AgeRating.HasValue && input.AgeRating.Value != current.AgeRating)
            body["ageRating"] = input.AgeRating.Value;
        if (input.TotalBookCount is > 0 && input.TotalBookCount.Value != current.TotalBookCount)
            body["totalBookCount"] = input.TotalBookCount.Value;
        if (input.Genres != null && !SameList(input.Genres, current.Genres))
            body["genres"] = input.Genres;
        if (input.Authors != null && !SameAuthors(input.Authors, current.Authors))
            body["authors"] = input.Authors;
        if (input.AlternateTitles != null && !SameAlt(input.AlternateTitles, current.AlternateTitles))
            body["alternateTitles"] = input.AlternateTitles;
        if (input.Links != null && !SameLinks(input.Links, current.Links))
            body["links"] = input.Links;

        // 标签：编辑器保存 = 程序写入 → MACHINE 必打（TagsLock 时也尝试随 PATCH 发，与 EnsureMachineTag 一致）
        if (input.Tags != null)
        {
            var finalTags = new List<string>(input.Tags);
            if (!finalTags.Contains(VersionMarker.MachineTag, StringComparer.Ordinal))
                finalTags.Add(VersionMarker.MachineTag);
            if (!SameList(finalTags, current.Tags)) body["tags"] = finalTags;
        }
        else if (current.Tags is not { Count: > 0 } cur ||
                 !cur.Contains(VersionMarker.MachineTag, StringComparer.Ordinal))
        {
            var t = new List<string>(current.Tags ?? []);
            t.Add(VersionMarker.MachineTag);
            body["tags"] = t;
        }

        // 锁：当前值原样回传（PATCH 不会翻转锁状态）
        foreach (var p in typeof(SeriesMetadataDto).GetProperties())
            if (p.PropertyType == typeof(bool?) && p.Name.EndsWith("Lock", StringComparison.Ordinal) &&
                p.GetValue(current) is bool bv)
                body[JsonName(p)] = bv;
        return body;
    }

    public static Dictionary<string, object?> BuildBookPatch(BookMetadataDto current, BookEditInput input)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        void SetText(string key, string? newValue, string? cur)
        {
            if (newValue == null) return;
            if (string.Equals(cur ?? "", newValue, StringComparison.Ordinal)) return;
            body[key] = newValue;
        }
        SetText("title", input.Title, current.Title);
        SetText("number", input.Number, current.Number);
        SetText("releaseDate", input.ReleaseDate, current.ReleaseDate);
        SetText("isbn", input.Isbn, current.Isbn);
        SetText("summary", input.Summary, current.Summary);
        foreach (var p in typeof(BookMetadataDto).GetProperties())
            if (p.PropertyType == typeof(bool?) && p.Name.EndsWith("Lock", StringComparison.Ordinal) &&
                p.GetValue(current) is bool bv)
                body[JsonName(p)] = bv;
        return body;
    }

    private static string JsonName(System.Reflection.PropertyInfo p) =>
        p.GetCustomAttributes(true).OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
            .FirstOrDefault()?.Name ?? p.Name;

    private static bool SameList(List<string> a, List<string>? b)
    {
        b ??= [];
        return a.Count == b.Count && a.Zip(b, (x, y) => string.Equals(x, y, StringComparison.Ordinal)).All(z => z);
    }

    private static bool SameAuthors(List<AuthorDto> a, List<AuthorDto>? b)
    {
        b ??= [];
        return a.Count == b.Count && a.Zip(b, (x, y) => x.Name == y.Name && x.Role == y.Role).All(z => z);
    }

    private static bool SameAlt(List<AlternateTitleDto> a, List<AlternateTitleDto>? b)
    {
        b ??= [];
        return a.Count == b.Count && a.Zip(b, (x, y) => x.Title == y.Title && x.Label == y.Label).All(z => z);
    }

    private static bool SameLinks(List<LinkDto> a, List<LinkDto>? b)
    {
        b ??= [];
        return a.Count == b.Count && a.Zip(b, (x, y) => x.Url == y.Url && x.Label == y.Label).All(z => z);
    }
}