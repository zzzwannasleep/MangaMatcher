using System.Text.Json.Serialization;

namespace KomgaScraper.Core.Sources;

// ========== DTO：Bangumi v0 API ==========

/// <summary>搜索结果条目（/v0/search/subjects）。</summary>
public sealed class BangumiSearchItemDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("name_cn")] public string? NameCn { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("images")] public BangumiImagesDto? Images { get; set; }
    [JsonPropertyName("infobox")] public List<InfoboxItemDto>? Infobox { get; set; }
    [JsonPropertyName("tags")] public List<BangumiTagDto>? Tags { get; set; }
    [JsonPropertyName("nsfw")] public bool? Nsfw { get; set; }
}

public sealed class BangumiImagesDto
{
    [JsonPropertyName("large")] public string? Large { get; set; }
    [JsonPropertyName("medium")] public string? Medium { get; set; }
    [JsonPropertyName("common")] public string? Common { get; set; }
    [JsonPropertyName("small")] public string? Small { get; set; }
    [JsonPropertyName("grid")] public string? Grid { get; set; }
}

public sealed class InfoboxItemDto
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    /// <summary>多态：string / {"v":"..","k":".."} / [ ... ]，由 SubjectMapper.ParseInfobox 解析。</summary>
    [JsonPropertyName("value")] public System.Text.Json.JsonElement? Value { get; set; }
}

public sealed class BangumiSubjectDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("name_cn")] public string? NameCn { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("volumes")] public int? Volumes { get; set; }
    [JsonPropertyName("eps")] public int? Eps { get; set; }
    [JsonPropertyName("total_episodes")] public int? TotalEpisodes { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("images")] public BangumiImagesDto? Images { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("nsfw")] public bool? Nsfw { get; set; }
    [JsonPropertyName("age_rating")] public int? AgeRating { get; set; }
    [JsonPropertyName("tags")] public List<BangumiTagDto>? Tags { get; set; }
    [JsonPropertyName("rating")] public BangumiRatingDto? Rating { get; set; }
    [JsonPropertyName("infobox")] public List<InfoboxItemDto>? Infobox { get; set; }
}

public sealed class BangumiTagDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class BangumiRatingDto
{
    [JsonPropertyName("score")] public double? Score { get; set; }
}

/// <summary>关联条目（/v0/subjects/{id}/subjects，用于抓单行本）。</summary>
public sealed class BangumiRelatedSubjectDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("name_cn")] public string? NameCn { get; set; }
    [JsonPropertyName("relation")] public string? Relation { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("images")] public BangumiImagesDto? Images { get; set; }
}

public sealed class BangumiSearchResponseDto
{
    [JsonPropertyName("data")] public List<BangumiSearchItemDto>? Data { get; set; }
}

// ========== UI/服务层使用的归一化模型 ==========

/// <summary>搜索候选（对应脚本 fetchBookByName 的返回项）。Source: "Bgm" / "Bw" / "Mox" / "MangaDex" / "Baike"。</summary>
public sealed record SearchCandidate(
    int Id,
    string Title,
    string OriTitle,
    string Author,
    string Aliases,
    string? CoverUrl,
    string Source = "Bgm",
    string? BwId = null,
    string? MoxId = null,
    string? MdId = null);

/// <summary>单行本信息（对应脚本 volumeMates）。</summary>
public sealed class VolumeMate
{
    public string Num { get; set; } = "";
    public string Summary { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string Isbn { get; set; } = "";
    public List<string> CoverUrls { get; set; } = [];
}