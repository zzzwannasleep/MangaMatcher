# KomgaBangumi Docker 版 — v2 规格：桌面端功能全集对齐

> v1 已交付（Bangumi 主链 + 账号系统 + Web UI）。v2 的验收标准：**桌面版 KomgaScraper 的功能全部出现在 Docker/Web 版**（对齐桌面版仓库 docs/README-full.md 的功能清单）。v1 的裁剪决定作废，多源/AI/Mylar/编辑器全部补齐。

## 0. v2 新增总览

| 桌面版功能 | v2 落地 |
|---|---|
| 五源回退链（Bangumi→BW→AI识别→AI选择→Mox→AI译日文查BW→MangaDex→百度百科） | 后端 match chain |
| BookWalker 目录库（下载/导入 zst/sqlite → v2 索引 → 系列级+每卷级字段+跨源补空+防串卷） | 后端 + 设置 UI |
| AI（OpenAI 兼容）：识别作品/智能选择/简介翻译/补写简介，进程内缓存 | 后端 + 设置 UI |
| Mox 中文源（串行 500ms、520 退避、搜索行封面、每卷封面、只补缺失） | 后端 |
| MangaDex（公开 API、zh-Hans 标题、AI 翻译简介） | 后端 |
| 百度百科兜底（开放卡片接口、概述图） | 后端 |
| 系列编辑器（三页签：系列信息/标签·别名/每卷，只提交改动字段，MACHINE 自动补，锁字段回传） | 后端 API + 前端对话框 |
| 纠正匹配（候选面板按源分组+详情区大封面完整简介+手动输入 BGM 链接/ID，ReplaceMatch 语义） | 后端 API + 前端 Modal |
| 覆盖匹配（重走全链、忽略旧链接、搜索词只用文件夹名、成功后移出手动匹配收藏夹） | job 选项 overrideMatch |
| 批量跳过已刮（列表元数据 links 判断，零额外请求；forceRefresh 可选） | job 选项 forceRefresh |
| 刮削状态（封面/简介 ✓✗，state 持久化，6 种筛选） | scrape_state 表 + API filter |
| 全库跨库搜索 + 编辑单部 | GET /api/search + 前端 |
| Mylar 导出 series.json（库内相对目录结构、oneshot 特殊名、可选封面） | comicsDir 直写 或 ZIP 下载 |
| 版本标记打标（一键修复旧库：文件夹标记→标签+标题后缀、出版社词标签、publisher 填充、MACHINE；Dry-run 先预览） | job kind=tag |
| 版本标记黑白名单（设置，逗号/每行一个词） | 设置字段 + 打标/刮削共用 |
| 代理（HTTP/HTTPS/SOCKS4/4a/5 + 账密；Bangumi API+封面下载+BW 下载共用；不影响 Komga） | 设置 + Go http.Transport |
| 匹配类型（漫画/小说） | 设置 matchType |
| 抓取单行本详情（每卷简介/发售日/ISBN） | 已有 fetchVolumes ✅ |
| 日志自动写文件 + 导出 | DATA_DIR/logs/log-日期.log + GET /api/logs |
| 手动匹配收藏夹管理（查看收藏夹内系列 → 纠正匹配 → 成功后自动移出） | 前端 /manual 页 |

## 1. Settings v2（GET/PATCH /api/settings，沿用打码规则）

```jsonc
{
  // v1 已有: komgaUrl, komgaApiKey(****), komgaUser, komgaPassword(****), bangumiToken(****),
  //          bangumiApiBase, bangumiMinIntervalMs, concurrency, dryRunDefault,
  //          manualCollection, fetchVolumes, withCovers
  "matchType": "漫画",                    // 漫画|小说（Bangumi platform 过滤）
  "proxy": { "protocol": "none", "host": "", "port": 0, "username": "", "password": "" },
  "ai": {
    "enabled": false, "baseURL": "https://api.deepseek.com", "apiKey": "", "model": "deepseek-chat",
    "identify": true,        // 识别作品（搜不到时问 AI 日/中/英三名再重搜）
    "smartPick": true,       // 多候选时 AI 智能选择
    "translateSummary": false, // 非中文简介→简体中文
    "writeSummary": false    // 三源无简介时 AI 补写（80-160字）
  },
  "baikeEnabled": true,
  "mangaDexEnabled": true,
  "bookwalker": {
    "downloadURL": "https://data.bookwalker.com/bkwk-db.sqlite.zst",
    "writeExtras": true      // 类型/题材/出版品牌/阅读方向 写入 Komga
  },
  "batchForceRefresh": false,  // 已有 Btv 链接的系列是否重刮
  "versionTagWhitelist": [],   // 每词一项（前端每行一个/逗号分隔）
  "versionTagBlacklist": [],
  "comicsDir": ""              // Mylar 直写目录（容器内路径）；空 = 只支持 ZIP 下载
}
```
- `ai.apiKey` 打码 `****`，PATCH 传 `****`/空 = 不改。`proxy.password` 同。
- BW 索引存 `DATA_DIR/bkwk-index.json.gz`。

### BookWalker 索引管理
- `POST /api/settings/bw/download` → 202 `{ok:true}` 后台下载官方 zst→解压→建索引（进度/结果查 status）
- `POST /api/settings/bw/import`（multipart `file`，支持 `.zst` / `.sqlite` / `.db`，按文件头识别）→ `{ok, seriesCount, volumeCount}`
- `GET /api/settings/bw/status` → `{indexed, building, seriesCount, volumeCount, formatVersion:2, lastError}`

## 2. 刮削状态与筛选

- SQLite 表 `scrape_state(series_id PK, has_cover INT, has_summary INT, links TEXT, checked_at TEXT)`。
- `GET /api/libraries/:id/series?page=&size=&filter=&q=`
  filter: `all|unscraped(无链接)|scraped(有链接)|no_cover|no_summary|incomplete(封面或简介缺)|complete(两者都有)`
  q: 名称/原名 LIKE。
  content 项增加 `"state": {"hasCover":bool,"hasSummary":bool}`；master 限授权库。
- 状态判定（后台补查并发≤3，写 state）：封面 = 系列有已上传缩略图（GET /thumbnails 有 selected 项）；简介 = metadata.summary 非空。列表优先用已有 state，未记录的系列前端显示 `…`（后台异步确认，结果写 state）。

## 3. 全库搜索

- `GET /api/search?q=&size=50` → `[{id,name,libraryId,libraryName,booksCount,metadata:{title},state}]`；master 限授权库。

## 4. 系列详情与编辑器

- `GET /api/series/:id` → `{series:{...}, books:[{id,name,number,metadata:{title,number,summary,releaseDate,isbn,authors},thumbnailUrl?}], state}`（校验库权限）
- `PATCH /api/series/:id/metadata-raw` body=任意 Komga SeriesMetadataUpdateDto 子集（前端只提交改动字段；后端自动补 MACHINE 标签；锁字段原样回传不会翻转）
- `PATCH /api/books/:id/metadata-raw` 同上
- `POST /api/series/:id/cover`（multipart image）→ selected=true 上传
- `POST /api/books/:id/cover` 同
- `POST /api/series/:id/rescrapevolumes` `{dryRun}` → 重刮每卷（按卷号对齐；遵循 Dry-run）→ 同步返回 `{ok, changed:[...]}`（小任务直接同步执行，进度日志照常）

## 5. 纠正匹配 / 手动匹配

- `GET /api/series/:id/match/candidates?q=` → `{"groups":{"bangumi":[cand], "bookwalker":[...], "mox":[...], "mangadex":[...], "baike":[...]}}`（空源不返回；cand={id,title,oriTitle,author,aliases,coverUrl,source}）
- `GET /api/match/detail?source=&id=` → `{title,oriTitle,author,aliases,summary,coverUrl}`（详情区完整简介）
- `POST /api/series/:id/match/apply` `{source:"bangumi|bookwalker|mox|mangadex|baike", id, dryRun}` → ReplaceMatch 语义（旧 Btv/Bw/Mox/MangaDex/Baike 链接剔除、锁字段解锁重写、全部卷强制重抓）→ 返回 `{jobId}`（单系列任务，前端跳任务详情）
- `POST /api/series/:id/match/apply` `{manual:"https://bgm.tv/subject/600001"|"600001"}` → 解析出 Bangumi ID 直接应用（source=bangumi）
- 手动匹配收藏夹（前端 /manual 页数据源）：
  - `GET /api/manual-collection` → `{name:"待手动匹配", series:[{id,name,libraryId,...}]}`（收藏夹不存在 → 空）
  - apply/override 成功后自动从收藏夹移除（对齐桌面版）

## 6. Jobs v2

- `POST /api/jobs` `{libraryId, seriesIds?, dryRun, forceRefresh?, overrideMatch?, kind?:"scrape"|"tag"}`
  - 默认（kind=scrape，无 overrideMatch）：已有 Btv 链接的系列**跳过零请求**（用列表元数据 links 判断，不再逐个 GET metadata）；forceRefresh=true 才重刮
  - `overrideMatch:true`：忽略旧链接与元数据标题，搜索词只用文件夹名（文件夹名 = Komga series.url 最后一段），命中即 ReplaceMatch；没匹配到的保持原样、不进手动匹配收藏夹；成功者自动移出收藏夹
  - `kind:"tag"`：版本标记打标（不动简介/链接）：文件夹标记→标签+标题后缀、出版社关键词标签、publisher 为空时填充、MACHINE；dryRun 先预览（items 列出"将…"）
- `GET /api/jobs` / `GET /api/jobs/:id` 增加 `kind` 字段

## 7. 匹配回退链（全自动，顺序对齐桌面版）

```
Bangumi精确 → BookWalker索引 → AI识别(拿不到时问AI作品名 jp/cn/en → 三名各重搜 Bangumi/BW/Mox)
→ AI智能选择(多候选) → Mox → AI中文译日文再查BW → MangaDex → 百度百科 → 失败进"待手动匹配"
```
- 单源失败不中断整链；AI 结果进程内缓存（同一系列不重复问）；候选>12 先按相似度截断再给 AI。
- 全链路共用精确匹配标准（规范名相等 / 包含+残留垃圾词检查，宁缺毋滥）。
- BW 跨源补空：Bangumi 命中时用 BW 补 ISBN/发售日/单卷封面（语言无关字段），不覆盖中文简介；防串卷：两边册数差 >25%（且 ≥2 册）跳过每卷对齐。

## 8. Mylar 导出

- `GET /api/libraries/:id/mylar?withCovers=1` → `application/zip` 下载（库内相对目录结构；oneshot → `xxx.oneshot.json`）
- 若 `comicsDir` 非空且可写：`POST /api/libraries/:id/mylar/export {withCovers}` → 直写 series.json + 返回 `{written:n}`（master 限授权库）

## 9. 日志

- slog 双写：stdout + `DATA_DIR/logs/log-YYYYMMDD.log`
- `GET /api/logs?lines=500` → `text/plain` 最近日志（admin only）

## 9b. 本地库缓存（打开即用 + 自动对齐）

- 双层：内存（读路径零 Komga 请求）+ SQLite（`cache_series` / `cache_libraries` 表，重启恢复）；封面单独磁盘缓存 `DATA_DIR/covers/` + ETag/304
- 设置：`cache.enabled`（默认 true）+ `cache.refreshMinutes`（默认 60，0=关闭自动对齐）
- `GET /api/cache/status` → `{enabled, refreshMinutes, ready, refreshing, seriesCount, libraryCount, lastSync, ageMinutes}`（admin only）
- `POST /api/cache/refresh` → 立即全量对齐（异步，`{ok, already?}`）（admin only）
- 失效链：编辑器 PATCH 元数据 → 单系列回源；封面上传 → 删封面缓存 + 回源；任务完成 → 逐系列回源（jobs OnDone 钩子）；Komga 连接三要素变更 → 缓存清空重建（防串库）
- 桌面版同语义：`data/library-cache.json` + 设置 cacheEnabled/cacheRefreshMinutes（设置 → 本地缓存）；启动恢复上次库视图，加载系列命中缓存秒出 + 后台单库对齐

## 10. Mock（node，tools/，浏览器联调用；Go httptest 版在 server 测试内）

- `mock-ai.js` :25697 — OpenAI 兼容 `/v1/chat/completions`，按 prompt 关键词返回罐头 JSON（识别/选择/翻译/补写）
- `mock-mox.js` :25696 — 搜索（行内封面 URL）/详情/封面帧页；强制 520 一次再成功（测试退避）
- `mock-mangadex.js` :25695 — `GET /manga?title=`（zh-Hans 标题）
- `mock-baike.js` :25694 — 卡片接口 + 概述图
- Komga/Bangumi mock 沿用 25699/25698

## 11. 前端 v2 新增视图/组件

1. SeriesView：状态筛选下拉（6 项）+ 搜索框 + 封面/简介 ✓✗ 状态图标（…=确认中）+ 按钮组新增「覆盖匹配」「纠正匹配」「Mylar 导出」「一键打标」+ forceRefresh 无需显式（设置控制）
2. CorrectMatchModal：候选按源分组 Tab、右侧详情区（大封面+完整简介）、手动输入 BGM 链接/ID、应用 → 跳任务
3. SeriesEditor（路由 /series/:id 或大对话框）：三页签（系列信息/标签·别名/每卷）、换封面（file input→POST cover）、每卷行内编辑（标题/卷号/发售日/ISBN/简介+换封面）、「重刮每卷」、保存=只提交改动字段
4. /manual 手动匹配页：待手动匹配收藏夹列表 + 每行「纠正匹配」
5. AppShell 搜索框：全局搜索 → /search?q= 结果列表（带库名徽章）→ 点击进编辑器/纠正匹配
6. SettingsView 新分区：代理 / AI / BookWalker（下载+导入+状态显示）/ 百科·MangaDex 开关 / 批量强制刷新 / 匹配类型 / 版本黑白名单（textarea 每行一词）/ Mylar comicsDir
7. JobsView 显示 kind（刮削/打标）徽章

## 12. 验收（红线）

- 后端：`go vet ./...` + `go test ./... -count=1` 全绿，新测试覆盖：五源各自解析、回退链顺序（Bangumi 空时逐级回退到百科命中）、AI 缓存命中、Mox 520 退避、BW 跨源补空+防串卷、跳过已刮零请求、override 语义、编辑器只提交改动字段、Mylar zip 字节有效、打标 dry-run、状态筛选。
- 前端：`npm run build` 双绿。
- Lead 集成：浏览器实测纠正匹配 Modal、编辑器、设置新分区、/manual、覆盖匹配全链路（node mocks + go server）。

## 13. E-Hentai（表站）元数据源

- **只接表站** `https://e-hentai.org`（免登录）：不接里站 exhentai/lilith，不发任何 cookie。`exhentai.org` 全仓只出现在三处**字符串匹配**（链接解析、来源链接识别、封面下载 Referer 判定），不是请求目标。
- 客户端 `server/internal/sources/ehentai.go`，源标签 `EhSource = "EH"`：
  - 搜索 `GET {base}/?f_search=…&advsearch=1&f_sfu=on&f_sft=on&f_sfl=on`（行内 `<div class="glink">` + 缩略图）
  - 元数据 `POST {base}/api.php` `{"method":"gdata","gidlist":[[gid,token]],"namespace":1}` → `gmetadata[0]`；`filesize/rating/filecount` 兼容「字符串」和「裸数字」两种形态
  - 非默认 base 时 gdata 走 `{base}/api.php`；默认 base 走 `https://api.e-hentai.org/api.php`
  - id 形状 `gid/token`（`sources.EhID`）；只给 gid 时 `GET {base}/g/{gid}/` 从规范链接补 token，补不出则明确报错（不静默成功）
  - 节流 1100ms/请求、超时 15s；瞬态状态（429/5xx）重试 3 次（1.5s/3s 退避），连接层错误不重试
  - **熔断（按 HTTP 层失败计）**：连接失败**或**瞬态状态重试 3 次耗尽 → 各计一次失败，连续 2 次 → 该客户端实例熔断 3 分钟（错误串含「熔断」），期间所有 EH 请求立即失败、链继续走下一源；gdata 逻辑错误（画廊已删除/无元数据）**不算**失败（站点是通的）。动机：一次 500 会让每个系列白等 ~14s，熔断后每 3 分钟只付一次。
- **链路位置**：Bangumi → BW → AI → Mox → MangaDex → **EH（第 7 步）** → 百科 → 待手动匹配。EH 挂掉（500/超时/连不上）不影响整链：任务仍 `done`，条目落 `manual`。
- **标签**：EhTagTranslation 词库（`db.text.json`）中文翻译；**上限 12 条**（唯一定义 `scrape.EhTagLimit`），按命名空间优先级 `artist>group>parody>character>female>male>mixed>other>language>category` 取满即止；未收录的 tag 回落原始名（命名空间仍译中文）；`language:chinese` 另外写 `language=zh`，不占 12 条额度；库里已有标签不算额度也不被删。
- **作者**：`artist`/`group` 标签 → **book 级** `authors`（`role=Artist`，画师在前、社团在后，最多 6 个）；series 级 PATCH **不含** `authors` 字段。
- 其余字段：`posted`(unix 秒) → `releaseDate`（YYYY-MM-DD，UTC）；`title_jpn` → `alternateTitles[{label:"原名"}]`；画廊页 → `links[{label:"EH", url:…}]`；缩略图 → 系列封面（`updateSeriesCover`）。
- 尊重 `tagsLock`：锁定系列不写 EH 标签，但仍追加 `MACHINE` 打标。
- 环境变量（非持久化，调试/演示用）：`EH_API_BASE`（默认官方表站）、`EH_PACE_MS`（0/空 = 默认 1100ms，>0 覆盖，例如 `EH_PACE_MS=50`）。两个都只在**进程启动时读一次**，不走设置页。
- 本地联调（不翻墙、不配代理）：`node tools/mock-ehentai.js` → 默认 :25693，启动时打印端口与接入命令；再接 `EH_API_BASE=http://127.0.0.1:25693`（想快点就加 `EH_PACE_MS=50`）起后端即可跑通候选/详情/应用/刮削整条链。（§10 的 mock 清单尚未收录本文件。）
- 验证：`server/internal/api/v2_eh_test.go` —— 候选 wire / 详情映射 / 12 条上限与优先级 / 书级作者 / dry-run 零写 / tagsLock / 失败即继续，以及空 tags、空 title、只有 gid、gdata error、filesize 裸数字等对抗边界；全部指向本地假站，零出网。

### 13.1 命中率实测（2026-10-20，真站 + 代理）

**EH 的 `f_search` 是罗马字取向的：日文/中文标题词基本搜不到。**

| 查询 | 结果 |
|---|---|
| `f_search=mesu kyoushi` | 25 条命中 |
| `f_search="mesu kyoushi 3"` | 10 条命中（短语匹配有效） |
| `f_search=雌教師`（CJK） | **0 条**（页面是正常的 "No hits found"，不是解析失败） |
| `f_search=artist:asanagi` | 全是该画师作品（标签语法精确） |

用真实本子文件夹名跑完整策略（3 个样本）的结果：

- `[MTSP (Jin)] 橘さん家ノ男性事情 [中国翻訳]` → **命中**（精确匹配，`artist:Jin` 也命中）；同作品在 EH 有多个版本（合集/单话/AI 生成），命中的可能是其中一个章节版本。默认流程只补标签/作者/日期/链接、不改标题，所以实际影响很小；开了「覆盖标题」才会把章节标题写进去。
- `[朝凪] 雌教師3 (アズールレーン) [DL版]` → **不命中**：日文标题词 0 命中；`朝凪` 作为文本词只搜到同名角色（如 "Umi Asanagi"）；`artist:朝凪` 也是 0 条，因为 **EH 的标签是罗马字**（该画师在 EH 叫 `asanagi`）。
- `[Fatalpulse (朝凪)] 淫らな制服 [中国翻訳]` → **不命中**：同上（`淫らな制服` 0 命中、`artist:` 用 CJK 名 0 命中）。

**结论与用法**：

1. 文件夹里带**罗马字**画师/社团名（`[Fatalpulse (Asanagi)] Mesu Kyoushi 3 [Chinese]`）或罗马字标题时，EH 命中率高；纯 CJK 命名的本子大概率匹配不上，随后按链路落到「待手动匹配」。
2. 匹配不上**不会写错数据**（保守策略：短词/低相似度一律拒绝），只是没刮到。
3. 这类本子的正确用法是**贴画廊链接**：纠正匹配 → 粘贴 `https://e-hentai.org/g/<gid>/<token>/` → 解析预览（中文标签 + 12 条上限）→ 应用。这条路径与自动匹配写库同源。
4. `artist:` 标签搜索只在方括号段本身就是罗马字时有效（`ehArtistTag` 不做 CJK→罗马字转换）；CJK 名字会白跑一次请求（无害，受熔断保护）。
