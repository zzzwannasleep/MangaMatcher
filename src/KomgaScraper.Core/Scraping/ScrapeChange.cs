namespace KomgaScraper.Core.Scraping;

/// <summary>一次库写入动作的种类（Dry-run 预览面板的行类型）。</summary>
public enum ScrapeChangeKind
{
    /// <summary>系列元数据 PATCH（标题/简介/标签/出版社…）。</summary>
    SeriesMeta,
    /// <summary>系列封面上传。</summary>
    SeriesCover,
    /// <summary>单卷元数据 PATCH（卷简介/发售日/ISBN/标题）。</summary>
    BookMeta,
    /// <summary>单卷封面上传。</summary>
    BookCover,
    /// <summary>收藏夹调整（失败系列加入"手动匹配"收藏夹）。</summary>
    Collection,
}

/// <summary>
/// 单条「将对库做什么 / 对库做了什么」的改动记录。
/// Dry-run 时逐条实时发给 UI（订阅 ScrapeService.ChangeObserved），
/// 让"预览模式"在界面里真正可见，而不是只有日志里一行字。
/// </summary>
/// <param name="Kind">动作种类。</param>
/// <param name="SeriesId">所在系列的 Komga ID。</param>
/// <param name="Target">显示对象：《名》 / 《名》卷 05。</param>
/// <param name="Description">动作描述（将更新系列元数据 / 将上传卷封面 / …）。</param>
/// <param name="Detail">细节摘要（字段与值预览，可空）。</param>
public sealed record ScrapeChange(
    ScrapeChangeKind Kind,
    string SeriesId,
    string Target,
    string Description,
    string Detail = "")
{
    public DateTime At { get; init; } = DateTime.Now;
}

/// <summary>ScrapeChange 类型的中文短标签（预览面板"类型"列）。</summary>
public static class ScrapeChangeKindText
{
    public static string Of(ScrapeChangeKind kind) => kind switch
    {
        ScrapeChangeKind.SeriesMeta => "系列信息",
        ScrapeChangeKind.SeriesCover => "系列封面",
        ScrapeChangeKind.BookMeta => "卷信息",
        ScrapeChangeKind.BookCover => "卷封面",
        ScrapeChangeKind.Collection => "收藏夹",
        _ => "改动",
    };
}
