# 本子（同人志）数据源接入缝隙地图

> 目的：评估新增「本子/同人志」数据源（E-Hentai / nHentai / Hitomi 等）需要动哪些缝隙。
> 范围：只读梳理 + 结论，不含实现。所有关键结论带 `文件路径:行号` 证据。
> 结论口径：**Komga 侧对同人志标签体系的承载能力足够**（tags / links / authors / genres / alternateTitles 全部真实可写），真正的接入成本在「源客户端 + 匹配规范化 + 来源枚举扩散」三处，不在落库。

---

## 0. 结论速览（TL;DR）

| 问题 | 结论 | 证据 |
|---|---|---|
| 能否写 tags（同人志的社团/CP/属性体系） | **能**，且是「整数组覆盖写」，必须读-改-写 | `server/internal/komga/models.go:121`、`server/internal/komga/client.go:423-430` |
| 能否写 links（指向 EH 画廊） | **能**，label+url 结构，无客户端长度校验 | `server/internal/komga/models.go:99-102,126` |
| 能否写 authors / genres / alternateTitles | **能**，三者都有真实写入调用路径 | `server/internal/scrape/scrapev2.go:333-339,371-381,344-364` |
| 有无 PATCH 字段白名单 | **无显式白名单**；有效写入面 = DTO 的 json tag 集合 + `CleanMeta` 规则；`-raw` 端点（编辑器）可传任意键 | `server/internal/komga/client.go:442-471`、`server/internal/api/handlers_v2.go:318-350` |
| 封面写入 | multipart，字段名 `file`，`?selected=true`；有 413 降级 | `server/internal/komga/client.go:272-275,301-326`、`server/internal/scrape/service.go:587-595` |
| 书籍级元数据 | 可写，但**刮削链路只写 authors/title/number/summary/releaseDate/isbn**；book 的 tags/links 只有 raw 端点能写 | `server/internal/scrape/service.go:455-513`、`server/internal/scrape/editorpatch.go:134-156`、`server/internal/api/handlers_v2.go:398-416` |
| tags 长度/字符限制 | 仓库内**无全局校验**；只有 BW 路径丢空值+>40 字（`runes`），Mox 路径最多追加 12 个 | `server/internal/scrape/scrapev2.go:555-566,664-680` |
| 新增源要不要改两处（Go + C#） | **不必强制**：Go 是 C# 的移植，但 Docker/Web 版已明显超前（多用户/任务/编辑器/缓存）。只接 Go 侧即可自洽，C# 桌面版会缺该源 | `server/internal/komga/models.go:1`、`server/internal/scrape/changes.go:1-2`、`docs/SPEC-v2.md:3` |

**一句话**：同人志标签体系（社团、原作、CP、属性、会场、汉化组）能完整落到 Komga —— tags 承载自由标签、links 承载画廊回链、authors 承载 circle/artist、alternateTitles 承载日/中/英名；瓶颈是源客户端与「整数组覆盖 + 锁字段」的合并纪律。

---

## 1. 两版关系：谁是谁的源

- Go 侧包注释直接写「移植自」：`server/internal/komga/models.go:1`（Komga DTO）、`server/internal/sources/proxy.go:1-2`（Sources）、`server/internal/scrape/changes.go:1-2`（Scraping）、`server/internal/komga/client.go:30`（KomgaClient.cs）。
- 因此**接口形状高度同构**：C# `KomgaClient` 与 Go `komga.Client` 的方法一一对应（对照 `src/KomgaScraper.Core/Komga/KomgaClient.cs:168-177,214-223,302-311,368-381` ↔ `server/internal/komga/client.go:196-202,233-239,297-299,205-210`）。
- 但**功能已经分叉**：Docker/Web 版独有 多用户/权限、任务引擎、Dry-run 差异、版本打标任务、库缓存、编辑器、`/api/match/resolve` 链接解析；C# 侧没有这些（`server/internal/api/server.go:138-201` 的路由表在 `src/` 无对应物）。
- 判定：**新增源只需要改 Go 侧**（Docker 版）；C# 桌面版要同步则属于「二次移植」，不是强制项。C# 侧 `AppSettings` 与设置窗口是独立的一套字段（`src/KomgaScraper.Core/Settings.cs:47,49`、`src/KomgaScraper.App/SettingsWindow.cs:46,100-101,589-590`），不共享 Go 的 `Settings` DTO。

---

## 2. 源客户端契约（`server/internal/sources/`）

### 2.1 公共类型：新源必须产出 `Candidate`

- `Candidate` 结构（跨源候选面板 + 匹配链共用）：`server/internal/sources/common.go:12-24`
  - 字段：`BgmID / Title / OriTitle / Author / Aliases(" / " 连接) / CoverURL / Source / BwID / MoxID / MdID / BaikeID`
  - **注意**：`Candidate` 无 json tag，是内部结构；前端 wire 由 api 层转换（`server/internal/api/handlers_v2.go:558-585`）。新增源若要携带自己的 ID，需要在 `Candidate` 加字段 + `candidateToWire` 加一个 case。
- 共用算法（**必须复用，不要另写一套**）：
  - `FindPreciseMatch`：`server/internal/sources/common.go:47-86`（精确相等 → 包含+残留检查 → 反向包含）
  - `RankCandidates`：`server/internal/sources/common.go:89-135`（bigram Jaccard，默认截断 12 条再给 AI）
  - `CandidateNames`：`server/internal/sources/common.go:27-43`
  - `FirstNonEmpty`：`server/internal/sources/common.go:150-157`
  - `LooksKoreanMox`：`server/internal/sources/common.go:160-162`

### 2.2 现有各源的对外签名（对照表）

| 源 | 构造函数 | 搜索 | 详情 | 特殊 |
|---|---|---|---|---|
| Bangumi | `bangumi.New(token, base, minIntervalMs)` `server/internal/bangumi/client.go:46-59` | `SearchSubjects(term, matchType, limit)` | `GetSubject(id)` / `GetRelatedSubjects(id)` | 节拍器 ≥350ms `:21-22,62-77`；退避 500ms/2s/4s `:88` |
| BookWalker | `sources.LoadBookWalkerIndex(gzPath)` `server/internal/sources/bookwalker.go:149` | `FindExact(...titles)` `:287` / `SearchFuzzy(term,max)` `:257` | `FindByID(id)` `:239` | **本地索引**，无网络；`ToCandidate()` `:303` |
| Mox | `sources.NewMoxClient(pc, baseURL, log, minIntervalMs)` `server/internal/sources/mox.go:65-79` | `Search(ctx, name, limit)` `:171` | `GetSeries(ctx, moxID)` `:223` | `GetVolumeCoverURLs(ctx, frameURL)` `:343`；串行节流+520 退避 `:98-153` |
| MangaDex | `sources.NewMangaDexClient(pc, baseURL, japaneseOnly)` `server/internal/sources/mangadex.go:36-46` | `Search(ctx, keyword, limit)` `:85` | `Get(ctx, id)` `:101` | 服务端 `originalLanguage` 过滤 + 客户端兜底 `server/internal/scrape/chain.go:263-272` |
| 百度百科 | `sources.NewBaikeClient(baseURL)` `server/internal/sources/baike.go:88-98` | `SearchCard(ctx, keyword)` `:115` | 无 id，按词条名重查 | 卡片校验 `LooksLikeMangaWork` `:42-79`；**不走代理** `:89` |
| AI | `sources.NewAiClient(baseURL, apiKey, model, pc, timeout)` `server/internal/sources/ai.go:53` | — | `IdentifySeries` `:178` / `PickBest` `:252` / `TranslateTitleToJapanese` `:284` / `GenerateSynopsis` `:316` / `TranslateToChinese` `:408` | 进程内缓存 `:36-51` |

### 2.3 新源「最少要实现什么」

进链路的最小面（3 个函数 + 1 个结构）：

1. `Search(ctx, term string, limit int) ([]XxxSearchResult, error)` —— 供候选面板 `groups["xxx"]` 与回退链。
2. `GetSeries(ctx, id string) (*XxxSeries, error)` —— 返回归一的详情结构（标题/原名/简介/作者/标签/出版社/状态/成人标记/封面 URL 组）。
3. `XxxSeries` → `komga.SeriesMetadataDto` 的映射函数（或直接照抄 `scrapeFromMox` 的写法，见 §4.9）。
4. 构造函数 + `Logger` 注入（`sources.Logger` 接口：`server/internal/sources/mox.go:57-62`）。

**不进链路也需要**：`proxy.NewHTTPClient` 复用代理（§2.4）、`SearchCard` 式「无 id 定位」若源没有稳定 id（百科就属于这种，`server/internal/api/handlers_v2.go:845-846` 用词条名当 id）。

### 2.4 代理与限速的既有约定（必须遵守）

- **代理工厂唯一入口**：`sources.NewHTTPClient(p ProxyConfig, timeout, userAgent, referrer)` `server/internal/sources/proxy.go:92-119`
  - http/https 走 `tp.Proxy`，socks4/4a/5 用纯 Go 握手 `:112-115,137-169`
  - 注释明确「Komga 永远直连——本工厂只服务 bangumi/封面下载/BW 下载/五源请求」`:91`
  - 百科是**唯一例外**：`NewBaikeClient` 显式传 `ProxyConfig{}`（国内直连）`server/internal/sources/baike.go:89-90`
  - → **新源若在墙外（EH/nHentai/Hitomi 都属于），必须走 `NewHTTPClient(p, ...)`，不能直连。**
- **限速两套现成范式**：
  - Bangumi 节拍器（所有并发共享 `nextStart`）：`server/internal/bangumi/client.go:62-77`，硬性 ≥350ms `:21-22`
  - Mox 串行 + 瞬态退避：`scheduleStart` `server/internal/sources/mox.go:81-96`（默认 500ms，`minIntervalMs<0` 时回落到 500 `:74-76`）；重试 3 次、`attempt*1.5s` 退避 `:129-139`；瞬态码集合含 **520/521/522/523/524**（Cloudflare 系）`:147-153`
  - → EH/Hitomi 同样挂在 Cloudflare 后面，**直接照抄 Mox 的 `isTransientStatus` + 退避**是最省事的做法。
- 测试注入基址：`server/internal/api/server.go:349-372`（`moxBaseOverride / mangaDexBaseOverride / baikeBaseOverride / moxPaceOverrideMs` + `SetSourceBaseOverrides`）。新源需要再加一组 override 变量（否则集成测试无法指向 httptest）。

---

## 3. 链路与匹配（`server/internal/scrape/`）

### 3.1 回退顺序与插入点

`RunChain` 的实测顺序（`server/internal/scrape/chain.go:56-62` 的注释与 `:63-343` 的代码一致）：

```
1 Bangumi 精确(:70-101) → 2 BookWalker 索引(:103-119) → 3 AI 识别(:121-130)
  → 3a AI 三名重搜 Bangumi→AI 挑(:147-186) → 3b BW 查日/英原名(:188-198) → 3c Mox 查 AI 中文名(:200-215)
→ 4 Mox 直搜兜底(:218-228) → 5 AI 译日文再查 BW(:230-247) → 6 MangaDex(:249-291)
→ 7 百度百科(:293-335) → 失败(:337-342)
```

**插入新源最自然的 3 个位置**（按代价从低到高）：

| 位置 | 代码位置 | 说明 |
|---|---|---|
| A. MangaDex 之后、百科之前 | `chain.go:291-293` 之间 | 本子多为日文原名、在 Bangumi/BW/百科查不到但 EH 有；插在百科前可拦住「百科误命中人物词条」 |
| B. 百科之后（最终兜底） | `chain.go:335-337` 之间 | 改动最小，绝不干扰现有命中率；但失败系列已被送进「待手动匹配」收藏夹，收益偏低 |
| C. 作为「只按链接命中」的旁路 | 不进 `RunChain` | 只挂 `handleMatchResolve`/`handleMatchApply`（§3.4），靠用户粘贴画廊链接触发。**风险最低、语义最清晰**（同人志标题几乎无法自动匹配，见 §3.3） |

- 链是纯顺序 `return`，每个分支独立 `return &MatchOutcome{...}`，**插入一个 `if s.EHentai != nil { ... }` 块即可，无需改结构**。
- 单源失败不中断整链是既成约定（`chain.go:61`、`:257-259` 的 Warn+continue）。
- 注意 `RunChain` 的 `terms` 有上限：`text.ExtractSeriesTitles(seriesName, 4)` `chain.go:64`。

### 3.2 `MatchOutcome` 与落库分发

- 结构：`server/internal/scrape/chain.go:46-54`（`Source / SubjectID / BwID / MoxID / MdID / BaikeTitle / MatchTitle`）
- → 新源需要在 `MatchOutcome` 加一个 ID 字段（如 `EhGid string`），**且必须改 `ScrapeOutcome` 的 switch**（`chain.go:422-458`，`:457` 是 `未知匹配来源` 兜底）。
- 源标签常量在 `chain.go:14-19`（`SourceBgm/Mox/Baike/MangaDex`），BW 的常量在 `sources/bookwalker.go:68`（`BwSource = "Bw"`）。**新增源要加常量，且注意大小写风格不统一**（`Bgm` vs `Bw`）——前端两套 map 都认（§6.1）。

### 3.3 匹配规范化的硬伤（对同人志最关键的风险）

同人志命名 `(C103) [サークル (作家)] タイトル (原作) [中国翻訳]` 与现有规范化高度冲突：

- `ExtractSeriesTitles` 把**第一个方括号段当标题、第二段当作者**：`server/internal/text/titleparser.go:296-302`。对同人志命名，`[サークル (作家)]` 会被当标题 → 搜索词直接错。
- `ResidueLooksLikeJunk` 对残留里**任何拉丁字母都判 junk**：`server/internal/text/titleparser.go:242-250`。EH 标题普遍带罗马字/英文副标题 → 包含匹配几乎全灭。
- `ExtractAndNormalizeTitle` 走 T2S + 标点替换 + 小写：`titleparser.go:193-202`；日文假名不参与任何转换（只做繁→简），所以日文原名可原样比对，但拉丁化命名不行。
- `BoilerplateMarkers` 里没有 `C103/例大祭/コミティア/中国翻訳/無修正` 这类同人志标记：`titleparser.go:210-214`；`PublisherTitleMarkers` 同理 `:217-220`。
- → 结论：**同人志源不应指望自动回退链命中**。合理设计是「精确名命中才写，否则进候选面板/手动链接」，或单独给 EH 做一套标题清洗（把 `(C103)`、`[サークル]`、`[中国翻訳]` 剥离后再比对）。

### 3.4 手动纠正匹配 → 可扩展为「粘贴 e-hentai 画廊链接」

这是**改动最集中、语义最干净**的接入缝：

- 路由：`POST /api/match/resolve` `server/internal/api/server.go:167`，`POST /api/series/{id}/match/apply` `:168`
- 解析器当前只认两种：纯数字 Bangumi ID、`bgm.tv/subject/N`、`baike.baidu.com/item/xxx`
  - 正则/分支：`server/internal/api/handlers_v2.go:833-850`；百度百科正则 `:814-815`
  - 失败文案硬编码在 `:848`（「支持 Bangumi 条目链接/ID 或百度百科词条链接」）
- 详情分发：`fetchMatchDetail(source, id)` `server/internal/api/handlers_v2.go:697-802`，`switch source` 里 `default` 直接 `未知来源` `:798-800`
- 应用：`handleMatchApply` `:860-914` → `jobs.SubmitApply(series, source, id, dryRun, ...)` `:908-910`
- 任务侧把 API 源名映射为内部标签：`server/internal/jobs/engine.go:103-128`（`bangumi→Bgm`、`bookwalker→Bw`、`mox/mangadex/baike` 原样）——**新源必须在这里加 case，否则 `ScrapeOutcome` 的 switch 落空**。
- 前端粘贴框已存在：`web/src/components/CorrectMatchModal.vue:191-206`（placeholder 与 hint 文案 `:198,206` 需同步改）
- → 同人志接入的推荐路径：**加一个 `hentaiItemRe = galleries/(\d+)/...` 正则 + `fetchMatchDetail` 的 `case "ehentai"` + engine 的 case**，即可实现「粘贴画廊链接 → 预览 → ReplaceMatch 写入」。

### 3.5 「已刮跳过」判定也吃来源枚举

- `hasSourceLink`：`server/internal/scrape/runner.go:80-93`（先判 Btv，再判 `IsSourceLink`）
- `IsSourceLink` 的 host 白名单：`server/internal/scrape/scrapev2.go:184-193`（`bangumi.tv/bgm.tv/chii.in/bookwalker/bookof.moe/kox.moe/mangadex.org/baike.baidu.com`）
- → **不改这里，新源写入的 links 不会被识别为「已刮」**，系列会被反复重刮；`ReplaceMatch` 也剔不掉旧链接（`ClearedForReplace` 只保留 tags/genres/阅读方向/日期/语言，links 由 `BuildSeriesMeta` 顶替，`scrapev2.go:162-182`）。
- 前端状态筛选也用同一判定：`server/internal/api/handlers_v2.go:166`。

---

## 4. 落库能力边界（`server/internal/komga/`）★ 核心

### 4.1 PATCH 请求体：真实字段清单（逐项确认）

`PatchSeriesMetadata` → `PATCH /v1/series/{id}/metadata`，body = `CleanMeta(meta)`：

- 端点与调用：`server/internal/komga/client.go:196-202`
- DTO 定义（**这就是能写的全集**）：`server/internal/komga/models.go:111-143`

| 字段 | DTO 行号 | 刮削链路实际写入？ | 证据 |
|---|---|---|---|
| `status` | `models.go:112` | ✅ Bangumi/Mox/MD | `scrapev2.go:627-630`、`:320` |
| `title` | `:113` | ✅ 全部源 | `scrapev2.go:599-603` |
| `titleSort` | `:114` | ✅ 随 title | `service.go:109-119` |
| `summary` | `:115` | ✅ 全部源（含 AI 补写） | `service.go:105-130` |
| `readingDirection` | `:116` | ✅ BW/MD/百科（RTL） | `scrapev2.go:393-395,864-866,1026-1028` |
| `publisher` | `:117` | ✅ Mox/百科 | `scrapev2.go:618-621` |
| `ageRating` | `:118` | ✅ Mox（`IsAdult`→18） | `scrapev2.go:622-626` |
| `language` | `:119` | ⚠️ DTO 支持，但**刮削链路不写**（只出现在 `ClearedForReplace` 透传 `:179`） | `bangumi/mapper.go:181-198` 只做判定不落库 |
| `genres` | `:120` | ✅ BW/MD（`BwWriteTags` 开时） | `scrapev2.go:370-381`、`:846-859` |
| `tags` | `:121` | ✅ Mox/BW + MACHINE + 版本标记 | `scrapev2.go:664-680`、`:385-389`、`:1155-1210` |
| `totalBookCount` | `:122` | ✅ BW 补卷数（仅完结系列） | `service.go:183-186` |
| `alternateTitles` | `:123` | ✅ Mox/MD/百科 | `scrapev2.go:344-364,637-663,823-842,990-1002` |
| `authors` | `:124` | ✅ Bangumi/Mox/百科 | `scrapev2.go:333-339,631-636,969-986` |
| `releaseDate` | `:125` | ✅ BW（取各卷最早发售日，只在旧值为空且未锁时写） | `scrapev2.go:406-411`、`diff.go:27` |
| `links` | `:126` | ✅ 每个源都写自己的回链 | `scrapev2.go:684-706,873-884,1035-1046` |
| 15 个 `*Lock` | `:128-142` | ⚠️ 只在编辑器路径原样回传 | `editorpatch.go:116-129` |

- **`collections` 不是 series metadata 字段**：收藏夹走独立 API —— `GetCollections` `client.go:363-369`、`CreateCollection` `:372-379`、`UpdateCollectionSeries` `:382-385`；业务封装在 `scrape/manualmatch.go:11-127`。

### 4.2 「有没有字段白名单」

- **没有显式白名单**。`CleanMeta` 用反射遍历 DTO 的 json tag：`server/internal/komga/client.go:442-471`
- 有效规则（`cleanMetaValue` `:398-439`，文件头注释 `:400` 也已写明）：
  - `nil` 不发；空串**不发**；整数 `0` 不发（`ageRating` 例外，`:413-417`）；浮点 `0` 不发
  - `nil` slice 不发；**空 slice 照发**（用于清空字段，`:423-430`）
  - `bool` 照发（锁字段可显式传 false）
- 推论（对同人志接入很关键）：
  1. **无法用 `PatchSeriesMetadata` 清空字符串字段**（空串被吞）。清空必须走 `PatchSeriesMetadataRaw`（`client.go:205-210`），现有用法见 `service.go:296-304`（清简介）。
  2. **数组是整数组覆盖写**：写 tags 必须自己读 `series.Metadata.Tags` 再合并（这也是所有源都 `append(komgaMeta.Tags...)` 的原因，`scrapev2.go:665,372,847`）。
  3. 想「合法置 0」只能 raw（`totalBookCount=0` 会被 CleanMeta 吞掉，注释也解释了 Komga 对 0 会 400：`src/KomgaScraper.Core/Komga/KomgaClient.cs:228-231`）。
- **raw 端点完全无白名单**：编辑器 `handleSeriesMetadataRaw` 把用户 body 原样透传（`server/internal/api/handlers_v2.go:336-346`），唯一加工是补 MACHINE 标签（`ensureMachineInPatch` `:353-387`）；书籍版 `handleBookMetadataRaw` 更是裸转发（`:398-416`）。

### 4.3 tags 的写入路径与限制

三条写入路径：

1. **刮削合并**（推荐照抄）：读 `komgaMeta.Tags` → 追加 → 去重 → 交给 `PatchSeriesMetadata`
   - Mox：`scrapev2.go:664-680`（`!GetTagsLock()` 才写；最多追加 12 个 `:667-670`；`containsFoldStr` 大小写不敏感去重 `:671`；`dedup` `:1222-1234`）
   - BW：`buildBwTags` `scrapev2.go:555-579`（空值跳过、**>40 rune 跳过** `:559`、含 `漫画/小说` 类型标记 `:573-577`）+ 合并 `:385-389`
   - 打标：`applyScrapeMarksV2` `scrapev2.go:1155-1210`（黑名单剔除 `:1168-1182`、**MACHINE 无视锁必打** `:1193-1195`）
2. **兜底补 MACHINE**：`ensureMachineTag` `scrapev2.go:1133-1152`（本次没走字段 PATCH 但动过库时补一条）
3. **编辑器**：`BuildSeriesPatch` `editorpatch.go:100-113`（提交 tags 或缺 MACHINE 时都补）

约束小结：
- **锁**：`tagsLock` 为 true 时程序化写入应跳过（各处都判 `GetTagsLock()` `models.go:67`）；但 **MACHINE 是例外，无锁也打**（`scrapev2.go:1193-1195` 在 `if !tagsLocked` 之外）。
- **长度**：仓库内仅 BW 路径有 `>40 rune` 丢弃；**无全局上限**。同人志常见的长标签（如 `原作:東方Project`）不会被拦。
- **字符**：无过滤（不加引号转义也可，因为走 json.Marshal）。
- **数量**：仅 Mox 路径自限 12；无硬上限。
- 未验证：Komga 服务端自身对 tag 长度/数量是否有校验（本仓库不含 Komga 源码，客户端也不做校验）。

### 4.4 links 的写入路径与限制

- 结构：`LinkDto{Label, URL}` `server/internal/komga/models.go:99-102`；DTO 字段 `Links []LinkDto` `:126`
- 写法固定为「读旧 → 判 host 是否已存在 → append → 整数组 PATCH」：
  - Mox 写两条（Bof + kox）：`scrapev2.go:684-706`（`hasBoF/hasKox` 用 `strings.Contains(lower(URL), "bookof.moe")` 判定，`:688-695`）
  - MangaDex：`:873-884`；百科：`:1035-1046`
  - Bangumi 侧：`MergeWithExisting` 里 links 合并 + 新链接优先（`server/internal/bangumi/mapper.go:631-633`）
- **无长度/字符限制**（label 与 url 都是裸 string）。label 惯例是短英文源名（`Btv/Bw/Mox/Baike`），前端按 label 展示（`diff.go:273-277` 只取 label 打日志）。
- **替换语义依赖 host 白名单**：`IsSourceLink` `scrapev2.go:184-193` —— 新源必须把 host 加进去，否则：① 旧链接剔不掉；② 「已刮跳过」判不出。
- 封面下载侧的 Referer 是按 host 硬编码的：`downloadImage` `scrapev2.go:131-160`（`bookwalker` → 强制 `https://bookwalker.jp/`，`:144-150`）。**有防盗链的源（EH/Hitomi 都属于）要在这里加规则**，或给新源单独的 `CoverReferer`/下载器。

### 4.5 封面 multipart 上传

- 系列：`UploadSeriesCover(seriesID, image, fileName)` → `POST /v1/series/{id}/thumbnails?selected=true` `server/internal/komga/client.go:272-275`
- 单册：`UploadBookCover(bookID, ...)` → `POST /v1/books/{id}/thumbnails?selected=true` `client.go:296-299`
- 实现要点：`uploadCover` `client.go:301-326`
  - `multipart.NewWriter` + `CreateFormFile("file", fileName)` `:304`（**字段名必须是 `file`**）
  - 原始字节体 + `w.FormDataContentType()`（含 boundary），**绝不 json.Marshal** `:314`
- 上传前后处理：
  - 清旧：只删 `Type == "USER_UPLOADED"` 的非选中项 `service.go:565-574`；无封面源时全清 `service.go:307-321`
  - 413 降级：换更小尺寸候选 `service.go:587-595`（系列）、`:641-649`（单册）；错误分类在 `client.go:97-100`
  - 单册去重：按 `FileSize` 相同跳过 `service.go:627-640`
- 取图（对比/回显）：`GetSeriesThumbnail` 必须带 `Accept: image/jpeg`，否则 Komga 恒 406（**注释里的历史事故**）`client.go:328-342`、`:344-358`

### 4.6 书籍级（book）元数据

- 可写端点：`PatchBookMetadata` / `PatchBookMetadataRaw` `client.go:233-247`；DTO `models.go:167-189`（含 `tags`/`links`）
- **但刮削链路只写这几个字段**：`updateBooks` `service.go:419-520`
  - 触发条件：`updateAuthors = len(bookAuthors) > 0 && ifUpdateBook(books, bookAuthors)` `service.go:141-142`；`ifUpdateBook` 是一堆启发式 `:659-685`（末卷缺 title/summary/releaseDate/isbn、标题带 `[]`、带 `.zip/.cbz`、标题无卷前缀、无 authors → 需要更新）
  - 写入内容：`Authors`（必）+ `Title/Number`（书名匹配卷号正则时）+ `Summary/ReleaseDate/Isbn`（有 volumeMate 时）`:455-494`
  - **只有当 `updateAuthors` 为真才写书**：`:455` 的 `if updateAuthors {` 包住了整块；`bookUpdateNeeded` 另判 `:428-431`
- 书籍级限制：
  - 册数 ≥500 直接跳过整系列的书处理 `service.go:424-427`
  - 匹配源无该卷数据 → 主动清空旧 summary/releaseDate/isbn（走 raw，空串真写）：`:473-494`
  - **book 的 tags/links 在任何刮削/编辑器路径都不写**（`BuildBookPatch` 只有 title/number/releaseDate/isbn/summary + 锁：`editorpatch.go:134-156`；C# 同 `SeriesEditorPatch.cs` 被 `SeriesEditorWindow.axaml.cs:391` 调用）。要写只能 raw（`handlers_v2.go:398-416`）。
- → 对同人志的含义：若一个画廊 = 一册（oneshot），书级字段基本用不上；`series.Oneshot` 分支已有 `service.go:439-442`（oneshot 取 `volumeMates[0]`）。

### 4.7 编辑器 PATCH（另一条「精确写入」路径）

- `BuildSeriesPatch` `editorpatch.go:67-131`：**只发用户改过的字段**（`setIf` `:55-63`，靠 `*Set` 标记区分「没提交」与「提交空串=清空」`：13-38`）
- 锁字段原样回传避免翻转 `:116-129`
- 这条路径证明：**清空字符串 + 写 tags/links/genres/alternateTitles/authors 全都有现成范式可抄**。

### 4.8 差异表达（Dry-run / 任务结果）

- `diffSeriesMeta` `server/internal/scrape/diff.go:12-80`：与 `CleanMeta` 同口径（空串/0 不出现在差异里 `:11`）
  - 数组字段：`tags` 全量对比 `:49-51`；`genres` 大小写不敏感 `:52-54`；`authors` 仅「旧空→新非空」才记 `:55-57`；`links` 只记新增 `:58-75`；`alternateTitles` 仅「旧空→新非空」`:76-78`
- `diffBookMeta` `:83-103`
- `Change` / `ChangeKind`：`changes.go:8-25`；中文标签 `:27-43`（`seriesMeta/seriesCover/bookMeta/bookCover/collection`）
- `ChangeKindText` 的 default 分支是「改动」，所以新源不需要加 Kind；但若想区分「本子源」动作，需要加 Kind + 前端映射。
- `SeriesItem.Source` 是自由字符串 `runner.go:55`，无后端枚举校验。

### 4.9 最省事的落库模板

直接抄 `scrapeFromMox`（`scrapev2.go:582-774`）——它是「只补空缺、不覆盖已有值」的完整范式：

- 字段都 `if komgaMeta.GetXxx() == "" && src.Xxx != ""` 才填 `:599-663`
- 数组都先 `append(komgaMeta.Tags/Genres/Links/AlternateTitles...)` 再判 `added > 0` `:637-706`
- 结尾 `s.applyScrapeMarksV2` `:682` + `ensureMachineTag` `:768`
- 封面严格串行（bookof.moe 不能并发）`:748`；上传逻辑 `:749-767`

---

## 5. 配置与开关

### 5.1 Go 侧设置模型（单一 JSON blob）

- 存储：`settings` 表 key/value `server/internal/store/store.go:72-75,342-365`；整体 blob 读写 `api/settings.go:217-223`
- DTO：`Settings` `server/internal/api/settings.go:48-78`
  - 已有源开关：`BaikeEnabled` `:66`、`MangaDexEnabled` `:67`、`BookWalker.WriteExtras` `:68`（`BW` 与 `Mox` **没有开关**，恒启用：`server.go:305-312`）
  - 代理 `Proxy` `:64`、AI `AI` `:65`、Bangumi token `:53`、并发 `:56`、Dry-run 默认 `:57`
- 新增源必须同时改 **4 个位置**（都在同一文件）：
  1. 结构字段：`settings.go:48-78`
  2. 默认值：`LoadSettings` `:83-103`（若默认开，还要照 `baikeEnabled/mangaDexEnabled` 的**老设置兼容写法**：`rawHasKey` 判「没存过」→ 置 true `:140-149`）
  3. 读回合并：`:104-170`
  4. PATCH 合并：`ApplyPatch` `:248-426`（照百科/MangaDex 开关那段 `:367-372`）
- 客户端构建有两份**重复实现**，必须都改：
  - `newRunner`（任务路径）：`server/internal/api/server.go:275-345`（`:305-322` 建 BW/Mox/MD/Baike）
  - `buildSources`（候选/详情路径）：`server/internal/api/handlers_v2.go:1301-1333`
- 基址 override（测试/自建反代）：`server/internal/api/server.go:349-372`
- `main.go` 环境变量种子：`store.SeedSettingIfAbsent` `store.go:359-365`（`komgaUrl/komgaApiKey/bangumiToken/bangumiApiBase`，见 `settings.go:171-185` 的回退读取）

### 5.2 Web 前端

- 设置页表单默认值：`web/src/views/SettingsView.vue:33-36`（`baikeEnabled/mangaDexEnabled/bookwalker`）
- 读回（**注意 `!== false` 的默认开语义**）：`:91-96`
- 保存体：`:189-196`
- 模板 checkbox：`:726-730`（BW）、`:765`（百科）、`:773`（MangaDex）
- 来源标签展示有 **3 份各自维护的 map**（新增源必须三处都加，否则前端显示原始字符串）：
  - `web/src/components/CorrectMatchModal.vue:21-29`（分组 Tab 的 label/bg + `sourceLabel`）
  - `web/src/views/JobDetailView.vue:24-32`
  - `web/src/utils/labels.js:2-14` 的 `FIELD_LABELS` —— **缺 `genres/links/alternateTitles/readingDirection/language/publisher/ageRating`**（新源若写这些字段，差异面板会显示英文 key）
- 粘贴链接入口与文案：`CorrectMatchModal.vue:191-206`

### 5.3 C# 侧对应设置

- `AppSettings` `src/KomgaScraper.Core/Settings.cs:14-75`：`BaikeEnabled` `:47`、`MangaDexEnabled` `:49`、`BwWriteTags` `:45`、`JapaneseOnly` `:51`、代理 `:24-30`、AI `:34-39`
- 设置窗口：`src/KomgaScraper.App/SettingsWindow.cs:46`（CheckBox 定义）、`:100-101`（读）、`:270`（布局）、`:589-590`（写）
- 客户端装配：`src/KomgaScraper.App/MainWindow.Logic.cs:128-155`（`_mox/_scrape.Mox/_batch.Mox`、`BaikeEnabled` 分支）
- 候选面板分组：`src/KomgaScraper.App/MatchWindow.axaml.cs:54-58`
- 桌面版**没有**「粘贴链接解析」（`src/KomgaScraper.App/ManualInputWindow.cs:9-15,36` 只接受 bgm.tv 链接/纯数字）——若要桌面版也支持，需要新增窗口逻辑。

---

## 6. 任务引擎与「用了哪个源」

- 任务类型只有两种：`Kind` = `scrape`（默认）/ `tag` `server/internal/jobs/engine.go:22-27,71-78`；`tag` 分支走 `FolderTagger` `:174-201`
- 单系列任务（纠正匹配）走 `SubmitApply`：`engine.go:86-142`，**源名字符串 → 内部标签的 switch 在 `:103-128`**，`ScrapeOutcome(series, outcome, true)` 硬编码 ReplaceMatch `:129`
- 进度/结果：`Job`（`Total/Done/OK/Failed/Status`）`engine.go:30-46`；`appendItemLocked` `:286-295`
- 差异字段汇总：`collectChanges` `runner.go:216-229`（书级字段加 `books.<bookId>.` 前缀）
- 新源只需：`MatchOutcome.Source` 用新常量（无枚举校验）+ `engine.go:103-128` 加 case + 前端 3 处 label map。**不需要改任务 Kind。**

---

## 7. 测试与 mock 现状

### 7.1 单元测试（`server/internal/sources/*_test.go`）

- 范式：mock 结构体 + `httptest.NewServer` + 解析断言。样例：
  - `mockMoxServer`（含 `first520` 开关）`server/internal/sources/sources_test.go:179-223`
  - `TestMox520BackoffAndParse` `:226-279`（断言退避 >1s + 解析字段全集）
  - `TestMoxSearchParse`（S2T 查询 / T2S 结果 / 逗号陷阱）`:282-304`
  - 代理：`TestProxyTransportHTTP/SOCKS5/ConfigValidate` `:506-581`；SOCKS 假服务端 `testutil_test.go:45+`
  - BW 索引：`buildBwTestSqlite` `:25-79`、`TestBWIndexBuildLoadMatch` `:80`
- → 新源必补：① mock 服务端 + 解析测试（字段全集断言）；② 瞬态码退避测试；③ 若走代理，复用 `TestProxyTransportHTTP`。
- `infotest_test.go` 是调试用（`TestMoxInfoFieldDebug`），不是回归测试。

### 7.2 集成测试（`server/internal/api/`）

- `newTestEnv` `server/internal/api/integration_test.go:431`（起 mock Komga + mock Bangumi + api server）
- mock Komga 的 series metadata 初始态含 `genres/tags/links/alternateTitles/authors` 全空数组 `integration_test.go:59`；单册 `:67,72`
- 源 mock 三件套 + 基址注入：`mockMoxV2` `v2_test.go:84-120`、`mockMangaDexV2` `:122-147`、`mockBaikeV2`、`setSourceOverrides` `:239-246`
- 回退链顺序测试（断言命中源 + 落库字段）：`TestV2ChainFallbackToBaike` `v2_test.go:291-390`
  - 断言 tags 含 MACHINE `:378-386`、links 含 Baike 且无旧 Btv `:365-377`、readingDirection `:387-389`
- 打标/差异断言：`scenarios_test.go:118-123`（changed 含 `title/status/totalBookCount/tags/authors`）、`:174-181`（PATCH tags 内容）
- → 新源必补：一个「改成 mock EH → 断言 PATCH 了 tags/links/authors/genres」的集成用例（照 `:291-390` 抄）。**这套测试实际上就是「字段能落库」的回归护栏**。

### 7.3 浏览器联调 mock（`tools/mock-*.js`）

- `tools/mock-mox.js:1-9`（头部注释写明「强制 520 一次再成功（测试退避路径）」）；`mock-mangadex.js`/`mock-baike.js`/`mock-ai.js`/`mock-bangumi.js` 同构
- `tools/mock-komga.js` 会打印收到的 PATCH 体，便于肉眼验收：
  - `PATCH /series/{id}/metadata` → `Object.assign` + 日志含 `tags/authors/links`：`tools/mock-komga.js:344-347`
  - 书级 PATCH：`:358-362`；缩略图 list/upload：`:364-380`；收藏夹：`:397`
- C# 侧冒烟：`tests/KomgaSmokeTest/Program.cs`、`tests/BaikeSmokeTest`、`tests/AiSmokeTest`（控制台直接打真站，不是 CI）
- → 新源建议：`tools/mock-ehentai.js`（含一次 5xx 退避 + 采样标题含 `[サークル]`/`(C103)`），并在 `mock-komga.js:344-347` 的日志基础上验收 tags/links。

---

## 8. 接入新源的「最小改动清单」（按文件）

> 假设：只接 Go/Docker 侧，源名为 `EH`（E-Hentai），支持「自动候选 + 粘贴画廊链接」两条路。

| # | 文件 | 要改什么 | 必要性 |
|---|---|---|---|
| 1 | `server/internal/sources/ehentai.go`（新建） | 客户端：`NewEHClient(p ProxyConfig, baseURL, log, minIntervalMs)`；`Search(ctx, term, limit)`；`GetGallery(ctx, gid)`；`GetCoverURLs`（防盗链 referer）；照抄 Mox 的 `scheduleStart`+`isTransientStatus` 含 520 | 必须 |
| 2 | `server/internal/sources/common.go` | `Candidate` 加 `EhID string`（`common.go:12-24`）；`Candidate.Source` 注释补枚举（`:22`） | 必须 |
| 3 | `server/internal/sources/proxy.go` | 通常**不用改**（直接用 `NewHTTPClient`）；仅当 EH 需要特殊 UA/Referer 时在构造函数里传 | 视源 |
| 4 | `server/internal/scrape/chain.go` | ① 加 `SourceEH = "EH"`（`:14-19`）；② `MatchOutcome` 加 `EhID`（`:46-54`）；③ `RunChain` 插一个分支（建议 :291-293 之间，或只在「链接命中」时用）；④ `ScrapeOutcome` 加 `case SourceEH`（`:422-458`） | 必须 |
| 5 | `server/internal/scrape/scrapev2.go` | ① 新增 `scrapeFromEH`（抄 `scrapeFromMox:582-774` 的「只补空缺」结构）；② `SourceClients` 加 `EH *sources.EHClient`（`:38-45`）+ `WithSources`（`:48-51`）；③ `IsSourceLink` host 白名单加 `e-hentai.org/exhentai.org`（`:184-193`）；④ `downloadImage` 加防盗链 referer 规则（`:144-150`）；⑤ 若需自带标题清洗，加在 `applyScrapeMarksV2` 之前 | 必须 |
| 6 | `server/internal/scrape/service.go` | `Service` 加 `EH *sources.EHClient` 字段（`:31-56`） | 必须 |
| 7 | `server/internal/scrape/runner.go` | `Runner.Sources` 透传（`:31-45`，`newService` `:65-77` 把 `EH` 挂上） | 必须 |
| 8 | `server/internal/text/titleparser.go` | 若要自动匹配：加同人志标记（`C103`/`例大祭`/`コミティア`/`中国翻訳`/`無修正`）到 `BoilerplateMarkers`（`:210-214`）；否则**跳过自动匹配**（推荐） | 推荐 |
| 9 | `server/internal/api/settings.go` | `Settings` 加 `EHEnabled bool`（`:66` 旁）；默认值（`:93-94` 旁）；读回（`:137-149` 旁，照**老设置兼容**写法）；`ApplyPatch`（`:367-372` 旁） | 必须 |
| 10 | `server/internal/api/server.go` | `newRunner` 装配 EH 客户端（`:305-322` 旁）+ Runner 字段（`:324-344` 旁）；基址 override 变量与 `SetSourceBaseOverrides`（`:349-372`） | 必须 |
| 10b | `server/cmd/server/main.go` | 新增 `EH_API_BASE` 环境变量注入（照 `MOX_API_BASE` 写法 `:79-92`）；如需 token/API key 再照 `:65-77` 的 `SeedSettingIfAbsent` 加 | 推荐（否则 mock/自建反代无法注入） |
| 11 | `server/internal/api/handlers_v2.go` | ① `buildSources` 装配（`:1317-1330` 旁）；② `handleMatchCandidates` 加 `groups["ehentai"]`（`:653-673` 旁）；③ `candidateToWire` case（`:569-585`）；④ `fetchMatchDetail` 加 `case "ehentai"`（`:798` 前）；⑤ `handleMatchResolve` 加画廊链接正则 + 分支 + 改文案（`:814-815,833-850`） | 必须（走链接路则 ⑤ 是核心） |
| 12 | `server/internal/jobs/engine.go` | `SubmitApply` 的 source switch 加 `case "ehentai"` → `outcome.EhID = id`（`:103-128`） | 必须 |
| 13 | `web/src/views/SettingsView.vue` | 默认值（`:33-36`）、读（`:91-96`）、保存（`:189-196`）、checkbox（`:765` 旁） | 必须 |
| 14 | `web/src/components/CorrectMatchModal.vue` | `SOURCE_META` 加 `ehentai`（`:21-26`）+ `sourceLabel`（`:28`）+ 粘贴提示文案（`:198,206`） | 必须 |
| 15 | `web/src/views/JobDetailView.vue` | `sourceLabel` map（`:25-32`） | 必须 |
| 16 | `web/src/utils/labels.js` | `FIELD_LABELS` 补 `genres/links/alternateTitles/readingDirection/publisher/language/ageRating`（`:2-14`） | 推荐 |
| 17 | `server/internal/sources/ehentai_test.go`（新建） | mock 服务 + 解析/退避/出错断言（照 `sources_test.go:179-304`） | 必须 |
| 18 | `server/internal/api/v2_test.go` 或新文件 | 集成用例：mock EH 命中 → 断言 Komga 收到 tags/links/authors/ageRating（照 `v2_test.go:291-390`） | 必须 |
| 19 | `tools/mock-ehentai.js`（新建） | 浏览器联调 mock（照 `tools/mock-mox.js:1-9` 的 5xx 开关） | 推荐 |
| 20 | `docs/SPEC-v2.md` | 「五源」→「六源」、回退链图、`match/candidates` 的 groups 示例（`:9,90,92,109-110`） | 推荐 |

**不需要改的**（避免误伤）：

- `server/internal/komga/*` —— 字段/上传能力已足够，无需扩 DTO。
- `server/internal/scrape/diff.go` —— 现有 `tags/genres/links/authors/alternateTitles` 分支已覆盖。
- `server/internal/scrape/editorpatch.go` —— 编辑器与本子源无关。
- 任务 `Kind` 枚举（`jobs/engine.go:22-27`）—— `scrape` 够用。
- C# 侧（`src/`）—— 除非明确要求桌面版同步。

---

## 9. 未验证项 / 风险清单

1. **Komga 服务端对 tags/links 的长度与数量校验**：本仓库不含 Komga 源码，客户端零校验（`server/internal/komga/client.go:398-439` 只管空值）。若 EH 标签体系直接全量灌入（EH 一个画廊常有 30+ tag），需实测 Komga 是否会拒或 UI 爆炸——**建议先按 Mox 的「最多 12 个」收敛（`scrapev2.go:667-670`）**。
2. **`ageRating` 与成人库里其他工具的关系**：Mox 路径 `IsAdult → ageRating=18`（`scrapev2.go:622-626`）；同人志几乎全是 R18，需确认是否要把 18 写成默认还是仅在源标记 R18 时写。
3. **`language` 字段刮削链路完全不写**（DTO 有、`ClearedForReplace` 透传，但无赋值）。若同人志要区分 `ja/zh/en/translated`，需要新写这条路径（Komga 语言值是 ISO 码，`models.go:119` 无校验）。
4. **自动匹配对同人志基本不可用**（§3.3）：`ResidueLooksLikeJunk` 的「任何拉丁字母即 junk」会否掉绝大多数 EH 标题。若不改 `text/` 就上线自动链，会产生大量误匹配或全量进「待手动匹配」。
5. **`IsSourceLink` 漏改的静默后果**：新源写入的 links 不会被 `hasSourceLink`（`runner.go:80-93`）识别 → 每轮任务重刮；`ReplaceMatch` 也剔不掉（`scrapev2.go:184-193`）。这类 bug 无报错，只有「怎么又刮了一遍」的现象。
6. **两次客户端构建**（`server.go:275-345` 与 `handlers_v2.go:1301-1333`）是重复实现，只改一处会出现「任务里能用、候选面板里没有」的割裂。
7. **`CleanMeta` 吞空串**：新源若想「源里没有这个字段就清空旧值」，必须显式用 `PatchSeriesMetadataRaw`（`client.go:205-210`），否则旧脏数据留存（现有清简介先例 `service.go:296-304`）。
8. **Dry-run 一致性**：所有写入都必须走 `sink.observe(Change{...})`（`changes.go:16-25`）否则 Dry-run 预览看不到；新源复制 `scrapeFromMox` 时这条天然满足，自写路径容易漏。
