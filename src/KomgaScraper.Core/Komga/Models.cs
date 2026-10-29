using System.Text.Json.Serialization;

namespace KomgaScraper.Core.Komga;

// ========== DTO：与 Komga REST API 对齐 ==========

public sealed class LibraryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("root")] public string? Root { get; set; }
}

public sealed class SeriesDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("libraryId")] public string LibraryId { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("oneshot")] public bool Oneshot { get; set; }
    [JsonPropertyName("metadata")] public SeriesMetadataDto? Metadata { get; set; }
    [JsonPropertyName("booksMetadata")] public BooksMetadataDto? BooksMetadata { get; set; }
    [JsonPropertyName("booksCount")] public int BooksCount { get; set; }
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }
}

public sealed class BooksMetadataDto
{
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("authors")] public List<AuthorDto>? Authors { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
}

public sealed class SeriesMetadataDto
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("titleSort")] public string? TitleSort { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("readingDirection")] public string? ReadingDirection { get; set; }
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
    [JsonPropertyName("ageRating")] public int? AgeRating { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("genres")] public List<string>? Genres { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("totalBookCount")] public int? TotalBookCount { get; set; }
    [JsonPropertyName("alternateTitles")] public List<AlternateTitleDto>? AlternateTitles { get; set; }
    [JsonPropertyName("authors")] public List<AuthorDto>? Authors { get; set; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("links")] public List<LinkDto>? Links { get; set; }

    // 更新请求中使用的锁字段（GET 时 Komga 也返回）
    [JsonPropertyName("statusLock")] public bool? StatusLock { get; set; }
    [JsonPropertyName("titleLock")] public bool? TitleLock { get; set; }
    [JsonPropertyName("titleSortLock")] public bool? TitleSortLock { get; set; }
    [JsonPropertyName("summaryLock")] public bool? SummaryLock { get; set; }
    [JsonPropertyName("publisherLock")] public bool? PublisherLock { get; set; }
    [JsonPropertyName("ageRatingLock")] public bool? AgeRatingLock { get; set; }
    [JsonPropertyName("genresLock")] public bool? GenresLock { get; set; }
    [JsonPropertyName("tagsLock")] public bool? TagsLock { get; set; }
    [JsonPropertyName("totalBookCountLock")] public bool? TotalBookCountLock { get; set; }
    [JsonPropertyName("alternateTitlesLock")] public bool? AlternateTitlesLock { get; set; }
    [JsonPropertyName("authorsLock")] public bool? AuthorsLock { get; set; }
    [JsonPropertyName("linksLock")] public bool? LinksLock { get; set; }
    [JsonPropertyName("languageLock")] public bool? LanguageLock { get; set; }
    [JsonPropertyName("readingDirectionLock")] public bool? ReadingDirectionLock { get; set; }
    [JsonPropertyName("releaseDateLock")] public bool? ReleaseDateLock { get; set; }
}

public sealed class AlternateTitleDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
}

public sealed class AuthorDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
}

public sealed class LinkDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public sealed class BookDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("seriesId")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }
    [JsonPropertyName("metadata")] public BookMetadataDto? Metadata { get; set; }
    [JsonPropertyName("oneshot")] public bool Oneshot { get; set; }

    /// <summary>Komga 1.x 的书籍所属库 id（books/list 未过滤时用于区分来源）。</summary>
    [JsonPropertyName("libraryId")] public string? LibraryId { get; set; }
    /// <summary>书籍所属系列标题（现代 Komga 在 books/list 里直接返回，省一次 series 查询）。</summary>
    [JsonPropertyName("seriesTitle")] public string? SeriesTitle { get; set; }
    /// <summary>文件大小（字节）。Komga 1.x 字段名是 sizeBytes。</summary>
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    /// <summary>旧版/部分版本返回的 fileSize（仅作兜底，正常读 SizeBytes）。</summary>
    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
    /// <summary>文件哈希（库设置开启 hashFiles 时才有；重复清理的严格校验用）。</summary>
    [JsonPropertyName("fileHash")] public string? FileHash { get; set; }
    [JsonPropertyName("media")] public MediaDto? Media { get; set; }

    /// <summary>统一取文件大小：优先 sizeBytes，老版本没有时回落 fileSize。</summary>
    public long EffectiveSizeBytes => SizeBytes > 0 ? SizeBytes : FileSize;
}

public sealed class MediaDto
{
    [JsonPropertyName("pagesCount")] public int PagesCount { get; set; }
    [JsonPropertyName("mediaType")] public string? MediaType { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class BookMetadataDto
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("titleSort")] public string? TitleSort { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("number")] public string? Number { get; set; }
    [JsonPropertyName("numberSort")] public float? NumberSort { get; set; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("isbn")] public string? Isbn { get; set; }
    [JsonPropertyName("authors")] public List<AuthorDto>? Authors { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("links")] public List<LinkDto>? Links { get; set; }

    [JsonPropertyName("titleLock")] public bool? TitleLock { get; set; }
    [JsonPropertyName("titleSortLock")] public bool? TitleSortLock { get; set; }
    [JsonPropertyName("summaryLock")] public bool? SummaryLock { get; set; }
    [JsonPropertyName("numberLock")] public bool? NumberLock { get; set; }
    [JsonPropertyName("numberSortLock")] public bool? NumberSortLock { get; set; }
    [JsonPropertyName("releaseDateLock")] public bool? ReleaseDateLock { get; set; }
    [JsonPropertyName("isbnLock")] public bool? IsbnLock { get; set; }
    [JsonPropertyName("authorsLock")] public bool? AuthorsLock { get; set; }
    [JsonPropertyName("tagsLock")] public bool? TagsLock { get; set; }
    [JsonPropertyName("linksLock")] public bool? LinksLock { get; set; }
}

public sealed class PageDto<T>
{
    [JsonPropertyName("content")] public List<T> Content { get; set; } = [];
    [JsonPropertyName("totalElements")] public long TotalElements { get; set; }
    [JsonPropertyName("numberOfElements")] public int NumberOfElements { get; set; }
}

public sealed class ThumbnailDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("selected")] public bool Selected { get; set; }
    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
}

public sealed class CollectionDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("seriesIds")] public List<string>? SeriesIds { get; set; }
}

/// <summary>series.list / books.list 的查询条件（仅实现用到的 is / isFalse）。</summary>
public static class KomgaQuery
{
    public static object SeriesInLibrary(string libraryId) => new
    {
        condition = new
        {
            allOf = new object[]
            {
                new { libraryId = new { @operator = "is", value = libraryId } },
                new { deleted = new { @operator = "isFalse" } }
            }
        }
    };

    public static object BooksInSeries(string seriesId) => new
    {
        condition = new
        {
            allOf = new object[]
            {
                new { seriesId = new { @operator = "is", value = seriesId } },
                new { deleted = new { @operator = "isFalse" } }
            }
        }
    };

    /// <summary>整库书籍（重复清理/跨库比对用）。</summary>
    public static object BooksInLibrary(string libraryId) => new
    {
        condition = new
        {
            allOf = new object[]
            {
                new { libraryId = new { @operator = "is", value = libraryId } },
                new { deleted = new { @operator = "isFalse" } }
            }
        }
    };
}
