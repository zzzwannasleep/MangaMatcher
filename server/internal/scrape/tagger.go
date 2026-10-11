package scrape

import (
	"komgabangumi/internal/komga"
	"komgabangumi/internal/text"
)

// FolderTagger 版本标记打标（移植 FolderTagger.cs，升级自 KomgaMylar tag_by_foldername）：
// 文件夹名标注（爱藏版/完全版/电子版/BW原档/汉化组…）→ 标签 + 标题后缀；
// 出版社关键词 → 标签 + publisher 填充；全部处理过的系列追加 MACHINE 标签。
// 用于一键修复"之前刮削把标题刮平了"的库：不动简介/链接，只补标记。
type FolderTagger struct {
	Komga      *komga.Client
	Log        Logger
	Whitelist  []string
	Blacklist  []string
	Observe    func(ch Change) // Dry-run 预览面板（可选）
}

// Tag 对系列列表执行打标；dryRun=true 只预览（items 列出"将…"），返回处理数量。
func (t *FolderTagger) Tag(seriesList []komga.SeriesDto, dryRun bool) int {
	updated := 0
	for _, series := range seriesList {
		metadata := series.Metadata
		seriesTitle := series.Name
		if metadata != nil && metadata.Title != "" {
			seriesTitle = metadata.Title
		}
		if seriesTitle == "" {
			continue
		}
		folderName := text.FolderName(series.URL, series.Oneshot, series.Name)

		markers := text.Detect(folderName, t.Whitelist, t.Blacklist)
		blacklisted := text.DetectBlacklisted(folderName, t.Whitelist, t.Blacklist)
		tagNames := []string{}
		if metadata != nil {
			tagNames = append(tagNames, metadata.Tags...)
		}
		if len(t.Blacklist) > 0 {
			// 黑名单命中的旧标签一并丢掉（用户口径：黑名单里的标签都丢掉）；MACHINE 是程序留痕，永不丢
			var kept []string
			removed := 0
			for _, tag := range tagNames {
				if tag != text.MachineTag && text.IsBlacklisted(tag, t.Blacklist) {
					removed++
					continue
				}
				kept = append(kept, tag)
			}
			tagNames = kept
			if removed > 0 {
				t.Log.Info("[打标] '" + seriesTitle + "' 丢弃黑名单标签 -" + itoa(removed))
			}
		}

		// 标签：版本/格式标记 + 出版社关键词（一个后缀一个标签；尊重 tags 锁）+ MACHINE（必打，无视锁）
		publisherKeyword := text.PublisherKeyword(folderName)
		if publisherKeyword != "" && text.IsBlacklisted(publisherKeyword, t.Blacklist) {
			publisherKeyword = "" // 出版社词也被拉黑 → 不落标签、不填 publisher
		}
		tagsLocked := metadata != nil && metadata.TagsLock != nil && *metadata.TagsLock
		var tagsToAdd []string
		if !tagsLocked {
			for _, tag := range text.TagForms(markers) {
				if !containsStr(tagNames, tag) && !containsStr(tagsToAdd, tag) {
					tagsToAdd = append(tagsToAdd, tag)
				}
			}
			if publisherKeyword != "" && !containsStr(tagNames, publisherKeyword) && !containsStr(tagsToAdd, publisherKeyword) {
				tagsToAdd = append(tagsToAdd, publisherKeyword)
			}
		}

		patch := map[string]any{}
		if len(tagsToAdd) > 0 || len(tagNames) != len(metadataTags(metadata)) || !containsStr(tagNames, text.MachineTag) {
			newTags := append([]string{}, tagNames...)
			newTags = append(newTags, tagsToAdd...)
			if !containsStr(newTags, text.MachineTag) {
				newTags = append(newTags, text.MachineTag)
			}
			patch["tags"] = newTags
		}

		// publisher 为空时用识别到的出版社关键词填充；tags 锁住时跳过
		if publisherKeyword != "" && metadata != nil && metadata.Publisher == "" && !tagsLocked {
			patch["publisher"] = publisherKeyword
		}

		// 标题：先把黑名单残留的旧后缀清掉，再按方括号格式补标记（标题已含该标记时不重复）
		newTitle := seriesTitle
		if len(blacklisted) > 0 {
			newTitle, _ = text.StripSuffixes(newTitle, blacklisted)
		}
		newTitle, titleChanged := text.ApplySuffixes(newTitle, markers)
		if titleChanged || newTitle != seriesTitle {
			patch["title"] = newTitle
		}

		if len(patch) == 0 {
			continue
		}

		t.Log.Info("[打标] '" + seriesTitle + "' 文件夹='" + folderName + "' 更新=" + joinPatchKeys(patch) +
			dryTag(dryRun))
		if !dryRun {
			meta := &komga.SeriesMetadataDto{}
			if v, ok := patch["tags"].([]string); ok {
				meta.Tags = v
			}
			if v, ok := patch["publisher"].(string); ok {
				meta.Publisher = v
			}
			if v, ok := patch["title"].(string); ok {
				meta.Title = v
			}
			if err := t.Komga.PatchSeriesMetadata(series.ID, meta); err != nil {
				t.Log.Error("更新系列 '" + seriesTitle + "' 元数据失败: " + err.Error())
				continue
			}
			t.emit(Change{Kind: ChangeSeriesMeta, SeriesID: series.ID, Target: "《" + seriesTitle + "》",
				Desc: "已打标（版本标记+MACHINE）", Detail: joinPatchKeys(patch)})
			updated++
		} else {
			// Dry-run 预览：逐项说明将写入什么（标签列出新加的，标题给旧→新）
			var details []string
			if v, ok := patch["tags"].([]string); ok {
				var added []string
				for _, tag := range v {
					if !containsStr(tagNames, tag) {
						added = append(added, tag)
					}
				}
				if len(added) > 0 {
					details = append(details, "标签 +"+itoa(len(added))+": "+joinStr(added, "/"))
				}
			}
			if v, ok := patch["publisher"].(string); ok {
				details = append(details, "出版社: "+v)
			}
			if v, ok := patch["title"].(string); ok {
				details = append(details, "标题: "+seriesTitle+" → "+v)
			}
			t.emit(Change{Kind: ChangeSeriesMeta, SeriesID: series.ID, Target: "《" + seriesTitle + "》",
				Desc: "将打标（版本标记+MACHINE）", Detail: joinStr(details, "；")})
			updated++
		}
	}
	t.Log.Info("共更新 " + itoa(updated) + " 个系列元数据")
	return updated
}

func (t *FolderTagger) emit(ch Change) {
	if t.Observe != nil {
		t.Observe(ch)
	}
}

func metadataTags(m *komga.SeriesMetadataDto) []string {
	if m == nil {
		return nil
	}
	return m.Tags
}

func joinPatchKeys(patch map[string]any) string {
	var keys []string
	for _, k := range []string{"tags", "publisher", "title"} {
		if _, ok := patch[k]; ok {
			keys = append(keys, k)
		}
	}
	return joinStr(keys, ",")
}
