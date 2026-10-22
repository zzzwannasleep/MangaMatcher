# 刮削优化记录（2026-10-22）

依据：桌面版一次真实任务日志 `KomgaScraper-日志-20261022-141919.txt`（v0.0.20）。
按任务窗口 `13:33:08 → 13:59:33` 切出 33,409 行做全量统计。

## 任务底座（实测）

| 指标 | 数值 |
|---|---|
| 规模 / 耗时 | 1217 系列 / 26 分 25 秒（≈1.3s/系列） |
| 成功来源 | Bangumi 948 / 百度百科 71 / MangaDex 16 |
| 写库 | 系列元数据 1035 条、卷信息 13,803 条 |
| 失败 | 182（其中 180 条能在日志里定位原因） |
| 失败原因 | **无精确匹配 176 / 搜索无结果 4** |

结论：97.8% 的失败不是「搜不到」，是「搜到了但没通过精确匹配闸门」。
所以优化方向不是加源，是修闸门与搜索词。

## 修了什么

| # | 问题 | 证据 | 改动 |
|---|---|---|---|
| 1 | **182 个失败系列一个都没进「手动匹配」收藏夹** | 日志「已加入」0 次、「已使用系列…创建」0 次 | `BatchMatcher.PreciseMatchAsync` 里 3 条失败 `return` 写在 `try` 内，绕过了方法末尾的收藏夹分支 → 收口到 `FailAsync`（含 catch 路径） |
| 2 | 收藏夹不存在时每个系列都全量拉一次 collections | 1036 次 `[收藏夹] "手动匹配" 不存在`（占日志 3%） | 加「已确认不存在」短路缓存；创建后走 `colID` 分支。Go 侧 `manualmatch.go` 同步 |
| 3 | 中文 2~5 字标题永远过不了反向包含 | 66/180 条失败词长 <6（家庭教师/我推的孩子/迷宫干/LIFE…） | 闸门 6 → 3，并加「候选名必须以搜索词开头 + 取最短候选」两道约束防张冠李戴；原「包含即可」仍只在 ≥6 时保留 |
| 4 | 无方括号的繁体文件夹名不转简 | 26 条（齊木楠雄的災難/魔法少女小圓/紅粉野球隊…） | 最小处理标题先过 T2S；Go 侧 `extraT2S` 补了内置表缺的 銃/滸 |
| 5 | 搜索词排序把作者/出版社排到标题前 | `词=天野洋一`、`词=东贩` | 排序改为「无假名 → 段长降序 → 拉丁占比」；出版社词表补 东贩/天下/时报/尚禾 等 |
| 6 | 版本后缀与长副标题不剥 | 港版完全版/铳梦 完全版/RE-TAKE全年龄版/一般コミック/全12卷/Fate Grand Order-… | 剥版本后缀 + 附加卷尾；长标题截出主标题当附加候选 |
| 7 | 卷号解析不出就按列表序号兜底（会撞车） | 252 次；`X (第18巻)`、`100億の男 1 人生を売った男`、`コヨーテ I`、`うせもの宿 (三)`、`… FILE2`、`ドラえもん 0巻` | 卷号字补 巻/冊/集；新增 副标题/罗马数字/汉字数字/英文前缀编号/数字紧跟卷字 五类模式；仍解析不出的按**最小未占用卷号**补齐 |
| 8 | 百度百科 `newLemmaId` 回字符串时整条兜底失败 | 3 次 `The JSON value could not be converted to … Path: $.newLemmaId` | C# 加宽容转换器；Go 用 `flexInt64` |

## 验证

- **C# 验证台**（`.scratch/verify`，未入库）：`PASS 60 / FAIL 0`，含
  - 日志里 160 个卷号解析失败名：修复后 **136/160** 可解析（其余 24 个是 上/中/下、`X (18.5)` 这类真没数字的）
  - 180 个失败系列的搜索词逐个复查（作者/出版社不再当首词、繁体转简、版本后缀剥掉、`【】` 保留名字）
  - 归一化等价、`FindPreciseMatch` 命中/不命中双向用例、百科 5 种 JSON 形态
  - 假 Komga（本机 TCP 假服务）实测：`EnsureExistsAsync × 25` → 只发 **1** 次 `GET /v1/collections`（修复前 25 次）
- **桌面版构建**：`dotnet build` 0 error（仅剩 8 条改动前就有的 nullable/XAML 警告）
- **Go**：`go build ./...` / `go vet ./...` / `go test ./...` 全绿（新增 `internal/text` 与 `internal/sources` 用例）

## 桌面版（exe）接入 E-Hentai 表站

原先只给 Go/Docker 版接了 EH，桌面版没接 —— 现补齐，语义与 Go 版一致。

| 文件 | 作用 |
|---|---|
| `src/KomgaScraper.Core/Sources/EhClient.cs`（新） | 表站客户端：搜索页 HTML 解析 → gid/token；gdata JSON 元数据；15s 超时、瞬态重试 3 次、**连接层不重试**、1100ms 礼貌节流、连接层连续失败 2 次**熔断 3 分钟** |
| `src/KomgaScraper.Core/Sources/EhTags.cs`（新） | EhTagTranslation 中文词库：GitHub latest release 直链（免 API 配额）→ gunzip → 原子落盘，7 天新鲜期，失败回落英文标签且不影响刮削 |
| `src/KomgaScraper.Core/Scraping/ScrapeService.cs` | `ScrapeFromEhAsync` 写库路径 + `EhTagNames`/`EhArtistAuthors`/`EhLanguageCode`/`EhPostedDate`；封面下载补 EH Referer |
| `src/KomgaScraper.Core/Scraping/BatchMatcher.cs` | 回退链插入 **4.6 步（MangaDex 之后、百度百科之前）**：文本搜索前 3 词 + 文件名方括号段做 `artist:` 标签搜索，按 EhID 合并去重，保守命中判定 |
| `Settings.cs` / `SettingsWindow.cs` / `MainWindow.Logic.cs` | 设置项 `ehEnabled`（默认开）+ 「E-Hentai（表站）」开关 + 客户端装配 |

**与其它源的关键差异**：EH 没有简介字段 → 刻意**不调** `ClearStaleSeriesAsync`（否则会把用户已有简介当脏数据清掉）；
画师/社团写 **book 级** `authors`（Komga 的 series PATCH 不吃 authors）；标签按命名空间优先级 + 上限 12 条。

**验证**（`.scratch/verify-eh`，未入库）：

- 离线 **64/64 PASS**：真实词库样本（44,668 条）翻译断言；假 EH 站喂真实 `search.html`（解析出 25 条）+ 真实 `gdata2.json`（53 标签、posted/filesize 字符串形态）；假 Komga 数 PATCH 请求体 —— 恰好 12 条 EH 标签 + MACHINE、优先级正确、写 EH 链接、**不写 summary**、系列 PATCH 恰好 1 次、`tagsLock` 时只保留旧标签 + MACHINE、画师/社团进 book 级 authors（`朝凪/Artist`、`Fatalpulse/Artist`）
- 真站联调 **69/69 PASS**（走 127.0.0.1:7890）：`artist:asanagi` → 25 条真实画廊；gdata 取到 11 标签并译成
  `艺术家:asanagi sion | 原作:闪乱神乐 | 角色:日影 | 女性:巨乳 | …`；book 级作者 `asanagi sion`；封面直链 ehgt.org
- 已知限制（与 Go 版一致，实测复现）：表站**纯文本搜索是罗马字取向**，`雌教師` 这类 CJK 词 0 命中 ——
  所以自动匹配靠「文件名方括号段 → `artist:` 标签搜索」兜精度，纯 CJK 命名的本子仍可能落到「待手动匹配」。

## 明确没做

- **BookWalker / Mox / AI 识别**：整场 BookWalker 0 次提及、Mox 2 次、AI 183 行日志换来 1 次命中 —— 属配置与策略问题（不用就关掉省时间），不是代码缺陷。
