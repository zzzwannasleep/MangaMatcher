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
