using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Scraping;
using KomgaScraper.Core.Text;

namespace KomgaScraper.Core.Mylar;

/// <summary>
/// 版本标记打标（升级自 KomgaMylar 的 tag_by_foldername）：
/// 文件夹名标注（爱藏版/完全版/电子版/BW原档/汉化组…）→ 标签 + 标题后缀；
/// 出版社关键词 → 标签 + publisher 填充；
/// 全部处理过的系列追加 MACHINE 标签（程序整理的可追溯标记）。
/// 用于一键修复"之前刮削把标题刮平了"的库：不动简介/链接，只补标记。
/// </summary>
public sealed partial class FolderTagger(KomgaClient komga, IAppLogger log)
{
    /// <summary>逐条库写入改动流（Dry-run 预览面板订阅；同 ScrapeService.ChangeObserved）。</summary>
    public event Action<ScrapeChange>? ChangeObserved;

    private void Observe(string seriesId, string target, string desc, string detail = "") =>
        ChangeObserved?.Invoke(new ScrapeChange(ScrapeChangeKind.SeriesMeta, seriesId, target, desc, detail));

    /// <summary>版本/格式标记的黑白名单（settings.json 注入；null/空 = 只用内置词表）。</summary>
    public List<string>? MarkerWhitelist { get; set; }
    public List<string>? MarkerBlacklist { get; set; }

    public async Task<int> TagAsync(IReadOnlyList<SeriesDto> seriesList, bool dryRun, CancellationToken ct = default)
    {
        int updated = 0;
        foreach (var series in seriesList)
        {
            ct.ThrowIfCancellationRequested();
            var metadata = series.Metadata;
            var seriesTitle = metadata?.Title ?? series.Name ?? "";
            if (seriesTitle.Length == 0) continue;
            var folderName = VersionMarker.FolderName(series);

            var markers = VersionMarker.Detect(folderName, MarkerWhitelist, MarkerBlacklist);
            var blacklisted = VersionMarker.DetectBlacklisted(folderName, MarkerWhitelist, MarkerBlacklist);
            var tagNames = new List<string>(metadata?.Tags ?? []);
            if (MarkerBlacklist is { Count: > 0 })
            {
                // 黑名单命中的旧标签一并丢掉（用户口径：黑名单里的标签都丢掉）；MACHINE 是程序留痕，永不丢
                var removed = tagNames.RemoveAll(t => t != VersionMarker.MachineTag &&
                                                      VersionMarker.IsBlacklisted(t, MarkerBlacklist));
                if (removed > 0) log.Info($"[打标] '{seriesTitle}' 丢弃黑名单标签 -{removed}");
            }

            // 标签：版本/格式标记 + 出版社关键词（一个后缀一个标签；尊重 tags 锁）+ MACHINE（必打，无视锁）
            var publisherKeyword = VersionMarker.PublisherKeyword(folderName);
            if (publisherKeyword != null && VersionMarker.IsBlacklisted(publisherKeyword, MarkerBlacklist))
                publisherKeyword = null;   // 出版社词也被拉黑 → 不落标签、不填 publisher
            var tagsToAdd = new List<string>();
            if (metadata?.TagsLock != true)
            {
                foreach (var t in VersionMarker.TagForms(markers))
                    if (!tagNames.Contains(t, StringComparer.Ordinal) && !tagsToAdd.Contains(t, StringComparer.Ordinal))
                        tagsToAdd.Add(t);
                if (publisherKeyword != null && !tagNames.Contains(publisherKeyword, StringComparer.Ordinal) &&
                    !tagsToAdd.Contains(publisherKeyword, StringComparer.Ordinal))
                    tagsToAdd.Add(publisherKeyword);
            }

            var patch = new Dictionary<string, object?>();
            if (tagsToAdd.Count > 0 || tagNames.Count != (metadata?.Tags?.Count ?? 0) ||
                !tagNames.Contains(VersionMarker.MachineTag, StringComparer.Ordinal))
            {
                var newTags = new List<string>(tagNames);
                newTags.AddRange(tagsToAdd);
                if (!newTags.Contains(VersionMarker.MachineTag, StringComparer.Ordinal))
                    newTags.Add(VersionMarker.MachineTag);
                patch["tags"] = newTags;
            }

            // publisher 为空时用识别到的出版社关键词填充（原脚本行为）；tags 锁住时跳过
            if (publisherKeyword != null && string.IsNullOrEmpty(metadata?.Publisher) &&
                metadata?.TagsLock != true)
                patch["publisher"] = publisherKeyword;

            // 标题：先把黑名单残留的旧后缀清掉，再按方括号格式补标记（标题已含该标记时不重复）
            var newTitle = seriesTitle;
            if (blacklisted.Count > 0)
                newTitle = VersionMarker.StripSuffixes(newTitle, blacklisted, out var stripped);
            newTitle = VersionMarker.ApplySuffixes(newTitle, markers, out var titleChanged);
            if (titleChanged || newTitle != seriesTitle) patch["title"] = newTitle;

            if (patch.Count == 0) continue;

            log.Info($"[打标] '{seriesTitle}' 文件夹='{folderName}' 更新=" +
                     string.Join(",", patch.Keys) + (dryRun ? " [Dry-run]" : ""));
            if (!dryRun)
            {
                var meta = new SeriesMetadataDto
                {
                    Tags = patch.TryGetValue("tags", out var t) ? (List<string>?)t : null,
                    Publisher = patch.TryGetValue("publisher", out var p) ? (string?)p : null,
                    Title = patch.TryGetValue("title", out var ti) ? (string?)ti : null,
                };
                try
                {
                    await komga.PatchSeriesMetadataAsync(series.Id, meta, ct);
                    Observe(series.Id, $"《{seriesTitle}》",
                        "已打标（版本标记+MACHINE）", string.Join("、", patch.Keys));
                    updated++;
                }
                catch (Exception e)
                {
                    log.Error($"更新系列 '{seriesTitle}' 元数据失败: {e.Message}");
                }
            }
            else
            {
                // Dry-run 预览：逐项说明将写入什么（标签列出新加的，标题给旧→新）
                var details = new List<string>();
                if (patch.TryGetValue("tags", out var tv) && tv is List<string> nt)
                {
                    var added = nt.Where(t => !tagNames.Contains(t, StringComparer.Ordinal)).ToList();
                    if (added.Count > 0) details.Add($"标签 +{added.Count}: {string.Join("/", added)}");
                }
                if (patch.TryGetValue("publisher", out var pv) && pv is string ps) details.Add($"出版社: {ps}");
                if (patch.TryGetValue("title", out var nv) && nv is string ns) details.Add($"标题: {seriesTitle} → {ns}");
                Observe(series.Id, $"《{seriesTitle}》", "将打标（版本标记+MACHINE）", string.Join("；", details));
                updated++;
            }
        }
        log.Info($"共更新 {updated} 个系列元数据");
        return updated;
    }
}