# 用 LANraragi 脚本生态给 Komga 刮削本子：可行性评估与落地方案

> 结论先行版。证据细节见三份子报告：
> [lanraragi-plugins.md](lanraragi-plugins.md)（插件逐行拆解，1360 行）·
> [komga-integration-seams.md](komga-integration-seams.md)（我们这边的接入缝隙，406 行）·
> [source-probes.md](source-probes.md)（网络/接口实测日志）。
> 实测时间 2026-10-20，直连 + 本地代理 `127.0.0.1:7890`。

---

## 0. 结论

**有我们需要的，但能复用的是「知识、数据源、算法」，不是代码。**
那个仓库是 Perl + LANraragi(Redis) 插件，和我们的 Go/Docker + C# 桌面版没有一行可以直接跑；
但里面有 4 样东西价值极高，正好补上我们现在做不了本子的短板：

| # | 可复用资产 | 它解决我们的什么问题 | 复用方式 |
|---|---|---|---|
| 1 | **E-Hentai `gdata` API** | 我们现在五个源（Bangumi/BookWalker/Mox/MangaDex/百科）对同人志几乎全空 | 直接做成第 6 个源，免鉴权、字段最全 |
| 2 | **EhTagTranslation `db.text.json`** | 中文界面里一堆 `female:ahegao` 没法看，用户看不懂 | 落库时转成 `属性:阿黑颜`，13 命名空间约 4.1 万条 |
| 3 | **同人志文件名正则 + circle/artist 拆分** | 我们的 `text` 包是按「商业志」命名的，本子文件名会解析错 | 移植 `(C103) [社团 (画师)] 标题 (原作) [汉化]` 解析 |
| 4 | **「离线内嵌元数据 → source 哨兵 → 多站瀑布」编排** | 本子源全在墙外，且必须可断点续跑 | 抄 `SourceBatch.pm` 的短路 + 多轮 + 结构化报告 |

**反过来，真正卡住我们的不是脚本能解决的 4 件事**（详见 §3）：本子源 100% 在墙外、exhentai 必须登录 cookie、
Komga 系列级没有 authors 字段、我们现有的「精确匹配」对同人志文件名基本失效（已用 Go 单测实测）。

所以路线是：**别移植插件，抄它的 4 块知识 + 接 2 个 JSON API（EH gdata、nHentai v2），
匹配走「候选面板 + AI 择优 + 粘贴画廊链接」，不要指望自动精确匹配。**

---

## 1. 那个仓库里到底有什么（逐项判定）

20 个 `Plugins/*.pm` + 根目录 4 个脚本，按「能不能用」分三档（完整拆解见 [lanraragi-plugins.md](lanraragi-plugins.md) §9）：

### 1.1 值得照搬（逻辑直接移植）✅

| 资产 | 出处 | 落到我们哪里 |
|---|---|---|
| 同人志命名正则 `(Event) [Artist] TITLE (Series) [Language]` | `RegexParse.pm:78` | 新建 `server/internal/text/doujin.go` |
| `[社团 (画师)]` 一行拆分 → `group:` + `artist:` | `RegexParse.pm:185-200` | 同上（这是全场最精巧的一段） |
| 版本噪音剥离规则表（`[v2]`/`[DL版]`/`[中国翻訳]`/`1080p`/`raw`/`uncensored`…） | `CopyTagsFromFilename.pm:74-93` | 同人志档案去重（同本不同版归并） |
| `clean_title` + `most_similar` 相似度择优 | `Utils/String.pm:14-28,69-85` | EH 搜索结果择优（比我们现在的 `FindPreciseMatch` 更适合脏标题） |
| EH 搜索 URL 构造：`advsearch=1&f_sfu=on&f_sft=on&f_sfl=on&f_search="短语"` | `EHentai.pm:163` | 实测可用，这三个 `f_s*` 开关是能不能命中的关键 |
| EH `gdata` 调用方式（含 `namespace=1`、25 个一批） | `EHentai.pm:292-317` | 新源 `sources/ehentai.go` |
| `source:` 标签当「已刮」哨兵 + 三种 URL 形态归一 | `SourceBatch.pm:70-117` | 我们有 `IsSourceLink`，同一思路 |
| 失败哨兵 `source:nogalleryinehentai` + 只重试失败项 | `addEhentaiMetadata.pm:66,117` | 大批量补全的幂等做法 |
| 瀑布编排：有序插件 + `rounds` + `stop_when_source_found` + 6 字段报告 | `SourceBatch.pm:238-304` | 与现有 `RunChain` 同构，加 provider 即可 |
| ISO 语言码 → 全称映射（15 条）、`Genre` 的 `m:/f:/other` 拆分 | `Ehviewer_Color.pm:143-184` | 语言/性别标签映射表直接抄 |

### 1.2 需要改造（思路可用，实现要重写）🔧

| 资产 | 必须改什么 |
|---|---|
| `hdoujin.pm` | **实测搜索端点缺 `Origin` 必 400**（插件注释假设反了）；搜索还要 `Referer`，详情只要 `Origin`；限速 `X-Ratelimit-Limit: 5`；别只取 ns=8/9，ns=1/2/3/5/10/11/12 都有用 |
| 中文翻译函数（8 份复制粘贴的 `translate_tag_to_cn`） | 抽成一个模块，并修 3 个真 bug：`s///` 未 `quotemeta`/未锚定；`artist cg`↔`artistcg` 三个类别名不匹配；文件打开失败直接崩（只有 `nHentai_CN.pm:236-255` 写对了） |
| 中文命名空间策略（`artist:x` → `艺术家:x`） | **别整体换中文**：Komga 的 tags/authors/genres 是分字段的，保留英文命名空间做字段路由，中文只做显示名，否则按 `artist:` 过滤会失效 |
| `CopyTagsFromFilename`（同名继承标签） | 「更好的版本替换后继承旧标签」思路直接用；Redis key 扫描换成 Komga series/book 查询 |
| 离线读取插件（`ComicLooms`/`Eze`/`Ehviewer_*`/`HatH`） | 思路价值最高（零网络），但 `extract_file_from_archive` 是 LRR API，要换成 Go 的 zip 读取；`ComicLooms.pm:85` 的 `unlink` 在 Komga 侧**千万别删用户原文件** |
| `ExHentai_Lanraragi_Checker.js` / `Tsukihi` | 「EH 页面标出哪些已入库」的工程细节（2000 条 LRU 缓存、同 filter 请求合并、并发池、204 重试）可抄；判定端点换成 `GET /api/v1/series?search=` |

### 1.3 直接放弃 ❌

| 资产 | 原因 |
|---|---|
| 全部 Perl 插件外壳 | `LANraragi::Model::Plugins` / Redis 元数据模型与 Komga 完全不同 |
| LRR 的 tag 字符串模型 | LRR 是**一个逗号分隔字符串**，Komga 是结构化字段，字符串拼接逻辑不适用 |
| `ETagConverter.pm` 全局替换 | `s/category/reclass/g`、`s/Chinese/汉语/g` 未锚定，会误伤标签值 |
| **HentagOnline 全族 + `addhentag.pm`** | 🟢 **hentag.com 已停运**，API 404，插件族整体失效 |
| `chihiro830/LANraragiPlugin` | 与刮削无关（子文件夹→静态分类） |
| 两个 EH 站点 UI 油猴脚本 | 纯 UI 增强 |

### 1.4 README 里提到的周边脚本，价值排序

1. **`EhTagTranslation/Database`**（ETagCN 的数据源，README 间接指向）——**最高价值**，见 §2.2。
2. **`addEhentaiMetadata.pm`**（chu-shen/LANraragi fork）——「给所有没有 source 的条目触发一次 EH 刮削」，
   就是我们要的「批量补漏」；注意它有个真 bug：拿英文错误串比较已中文化的插件返回值（`:65`），哨兵逻辑实际失效。
3. **`chu-shen/LANraragi-scripts`**——里面的 `getEhTagTranslationForLANraragi.py` 是翻译库生成脚本，
   我们做「翻译库定期更新」时可以照抄流程（从 GitHub release 拉 → 解 gz → 转内部格式）。
4. **`starP-W/HAtH-Plugin-CN`**——读 `galleryinfo.txt`（H@H 下载的库自带），离线路径的现成格式参考。
5. **`hdoujin.org`**——🟢 唯一**直连可达**的本子站（不用代理！），可做兜底源，但要正确发 `Origin`。
6. `chihiro830/LANraragiPlugin`、`Difegue/Tsukihi`——一个无关、一个后期可选。

---

## 2. 四个可复用资产的具体形态（实测）

### 2.1 E-Hentai：搜索（HTML）→ 元数据（JSON）

```text
① 搜索：GET https://e-hentai.org/?advsearch=1&f_sfu=on&f_sft=on&f_sfl=on&f_search="<标题>"
   → 200，25 条/页，正则抓 /g/(\d+)/([0-9a-f]+)/ 得到 gid + token
② 元数据：POST https://api.e-hentai.org/api.php
   {"method":"gdata","gidlist":[[gid,token],...],"namespace":1}   ← 一次最多 25 个
   → 免鉴权、免 cookie
```

`gdata` 返回：`gid, token, title, title_jpn, category, thumb, uploader, posted(unix), filecount,
filesize, expunged, rating, torrentcount, torrents[], tags[], parent_gid, first_gid…`
（`rating`/`posted`/`filecount`/`filesize` 都是**字符串**，别当数字反序列化。）

`tags[]` 自带命名空间，实测样例：`parody:azur lane`、`character:ganyu`、`artist:asanagi`、
`group:...`、`female:ahegao`、`male:...`、`mixed:...`、`language:chinese`、`other:...`、`reclass:...`。

**这是本子刮削的性价比之王**：一次请求拿全标题/日文名/分类/评分/日期/封面/几十个结构化标签。

### 2.2 中文标签：EhTagTranslation `db.text.json`

- Release 产物：`db.text.json.gz`（1.29MB → 4.29MB），13 命名空间、约 4.1 万条，**今天还在更新**（实测 `v7.29064.1`，2026-10-20 05:38 发布）。
- 结构：`data[] = { namespace, data: { "<标签>": { name: 中文名, intro: 中文简介, links: 外链 } } }`
- 实测命中：

| 原始标签 | 中文名 | 简介 |
|---|---|---|
| `artist:asanagi` | 朝凪 | 纯爱画师，轻小说《半兽人英雄物语》插画作者… |
| `parody:azur lane` | 碧蓝航线 | 《碧蓝航线》是一款… |
| `female:ahegao` | 阿黑颜 | 夸张的面部表情… |
| `language:chinese` | 汉语 | 汉语，又称中文… |
| `character:ganyu` | 甘雨 | 所属作品：原神 |

**落库建议**：`artist:asanagi` → tags 写 `画师:朝凪`（英文原名也留一条 `artist:asanagi` 方便过滤），
`parody:azur lane` → `原作:碧蓝航线`，`female:ahegao` → `属性:阿黑颜`。
未收录的回落英文原名。**注意** `category` 有个坑：API 给 `artist cg`，库里键名是 `artistcg`（三个类别名不一致）。

### 2.3 nHentai v2：官方 JSON API（比 HTML 抓取省事）

```text
GET https://nhentai.net/api/v2/search?query=artist:jinja-eru$ language:chinese&page=1   → 200
GET https://nhentai.net/api/v2/galleries/{id}                                          → 200
```

返回 `title{english,japanese,pretty}`、`cover/thumbnail{path,width,height}`、`upload_date`(unix)、
`num_pages`、`num_favorites`、`scanlator`，以及**带类型的** `tags[{type,name,slug,count,description}]`，
`type ∈ {category, language, tag, parody, artist, character, group}` —— 正好是我们的字段路由。
有 OpenAPI（`/api/v2/openapi.json`）和 `/api/v2/pow`、`/api/v2/captcha` 端点（未来可能上人机验证，要容错）。

### 2.4 Hitomi：能用，但要处理域名轮换

`https://ltn.<当前域名>/galleries/<id>.js` 返回 `var galleryinfo = {...}`，tags 带 `female/male` 标记。
⚠️ 子报告里「Hitomi 不可达」的结论**不准确**：它测的是 `ltn.hitomi.la`（不可达），
而本机实测 `ltn.gold-usergeneratedcontent.net` **走代理 200**。真正的成本是**数据域名会轮换**，
要从首页 JS 里取当前域名 —— 这就是 Hitomi 主要维护成本，优先级放最后。

---

## 3. 四个硬约束（脚本解决不了，必须我们这边设计）

### 3.1 网络：本子源 100% 在墙外

| 站点 | 直连 | 走代理 |
|---|---|---|
| `api.e-hentai.org` | ❌ 连接重置 / 超时 | ✅ 200 |
| `e-hentai.org` | ❌ TLS 被 reset（SNI 阻断） | ✅ 200 |
| `exhentai.org` | ❌ | ⚠️ 302（要登录 cookie） |
| `nhentai.net/api/v2` | ❌ 超时 | ✅ 200 |
| `hitomi.la` / 数据域 | ❌ | ✅ 200 |
| `hdoujin.org` / `api.hdoujin.org` | ✅ **200** | ✅ 200 |

我们已有完整的代理能力（`sources/proxy.go`，HTTP/SOCKS，Mox 已经按这套走），**但**：
新源必须显式走 `NewHTTPClient(p, ...)`，且「本子源单独一个代理开关」比「全局代理」更合理
（Bangumi 直连、本子走代理，两者需求相反）。

### 3.2 exhentai 必须登录

`exhentai.org` 匿名一律 302。表站 `e-hentai.org` 能匿名，但内容池小得多。
所以要么只做表站，要么在设置里加 `ipb_member_id` / `ipb_pass_hash` / `igneous` 三个 cookie 输入项
（`igneous` 会过期，要引导用户重抓 + 做失败提示）。**这是一个产品决策，不是技术难点。**

### 3.3 Komga 系列级没有 authors 字段（本次新发现，影响落库设计）

对照 Komga 主分支源码：

- `SeriesMetadataUpdateDto.kt`（PATCH 接受的全集）**没有 `authors`**；
- `SeriesDto.kt:30-63` 的 `SeriesMetadataDto`（读回）也没有 `authors`；
- 系列页显示的作者来自 `booksMetadata.authors`（`SeriesDto.kt:65-66`），即**从各册 book 级 authors 聚合**；
- Komga 的 Jackson 只配了 `FAIL_ON_NULL_FOR_PRIMITIVES`，未知字段默认忽略 → 往 series PATCH 里塞 `authors` 是**静默无效**。

**对本子的含义**：画师/社团必须落到 **book 级 `authors`**（一个画廊一册的 oneshot 场景完全可行），
或退化成 tags 里的 `画师:朝凪`。顺手也解释了现有链路为什么要写 `PatchBookMetadata{Authors}`（`service.go:456`）。

> 这一条建议实测复核一次（起一个 Komga，PATCH 带 authors 看是否 400/忽略），我把它列进 P0 验收项。

### 3.4 我们现有的「精确匹配」对同人志基本失效（已实测）

我用现有 `text` 包跑了真实本子文件名（临时单测，已删）：

```text
输入: [Asanagi] Mesu Kyoushi 3 (Azur Lane) [Chinese] [無邪気漢化組] [Digital]
  候选: ["Mesu Kyoushi 3  Azur Lane"  "Asanagi"  "Chinese"  "Digital"]
输入: (C103) [MTSP (Jin)] Baishun-Gakuen 2 (Original) [Chinese] [Decensored]
  候选: ["C103   Baishun-Gakuen 2  Original"  "MTSP (Jin)"  "Chinese"  "Decensored"]
```

三个问题：
1. **原作/parody 被拼进标题**（`Mesu Kyoushi 3  Azur Lane`），拿去搜必然搜不到；
2. `C103`（会场）、`Chinese`/`DL版`（语言版本）、汉化组**都没被识别成噪音**（`BoilerplateMarkers` 里只有中文商业志那套词）；
3. `FindPreciseMatch` 的包含匹配遇到拉丁残留就判 junk（`titleparser.go:242-250`），而 EH 标题**全是拉丁/罗马字** → 包含路径几乎全灭。

**所以**：本子不要挂进现有 `RunChain` 当第 6 级回退（会大量误配或全部落到「待手动匹配」）。
建议两条独立路径（见 §4.2）。

---

## 4. 建议方案

### 4.1 数据源分四层（离线优先，网络兜底）

```text
L1 离线内嵌元数据（零网络，最高优先级）
   meta.json / info.json / ComicInfo.xml / galleryinfo.txt / .ehviewer
   ↑ 从彩E、绿E、H@H 下载的库大多已经带这些，命中即完事，还免封禁
L2 E-Hentai gdata（主源，免鉴权，字段最全）
L3 nHentai v2（JSON API，画师/parody/角色齐，作为 EH 查不到时的第二站）
L4 HDoujin（唯一可直连，兜底；可让用户配 Bearer UUID 提配额）
   ❌ HentagOnline 已停运，不接；Hitomi 域名轮换，最后再说
```

编排抄 `SourceBatch.pm`：**有序瀑布 + 命中即停 + 可配轮次 + 失败哨兵 + 结构化计数**。
「离线 → L2 → L3 → L4」这个顺序本身就是 Putarku 仓库里最值钱的架构，而且天然处理「墙外源不稳定」。

### 4.2 匹配走两条路，不做「自动精确匹配」

- **路径 A（推荐先做）：粘贴画廊链接** —— 用户在 EH 页面复制 `https://e-hentai.org/g/123456/abcdef/`，
  贴进现有「纠正匹配」弹窗 → 预览 → 写入。改动集中在 `handleMatchResolve` 的正则 + `fetchMatchDetail` 一个 case，
  风险最低、语义最干净（对照 [komga-integration-seams.md](komga-integration-seams.md) §3.4）。
- **路径 B：候选面板 + AI 择优** —— 文件名清洗 → EH/nH 搜索 → `RankCandidates`（bigram Jaccard，现成的）
  → 候选卡片墙让用户点，或交给现有 AI `PickBest`。**不自动写库**，避免脏标题把库刮坏。

### 4.3 字段映射表（EH/nH → Komga）

| 来源 | Komga 字段 | 备注 |
|---|---|---|
| `title`（清洗后） | `series.title` + `titleSort` | 清洗规则用 §1.1 那张表 |
| `title_jpn` / `title.japanese` | `alternateTitles` | 现有的日文原名 |
| `artist:` | **book 级 `authors`**（role=Artist）+ tag `画师:中文名` | 见 §3.3，series 级写了无效 |
| `group:` / `circle` | **book 级 `authors`**（role=Circle，若 Komga 版本支持）或 tag `社团:中文名` | 同上 |
| `parody:` | tag `原作:中文名` | 最常用的筛选维度，别塞 genres |
| `character:` | tag `角色:中文名` | 数量大，建议可配「只写前 N 个」 |
| `female:/male:/mixed:/other:` | tag `属性:中文名` | 一个画廊常 30+ 条，**必须先截断**（照 Mox 的 12 条上限） |
| `language:` | `series.language`（BCP47：ja/zh/en）+ tag `语言:汉语` | 现有链路完全不写 language，本子正好用得上 |
| `category:` | tag `分类:同人志`（或 `genres`） | 注意 `artist cg`↔`artistcg` 键名差异 |
| `posted` / `upload_date` | `series.releaseDate`（unix → `YYYY-MM-DD`） | |
| `rating` | tag `评分:4.32` 或写进 `summary` 首行 | Komga 无评分字段 |
| `filecount` / `num_pages` | 不写（Komga 自己知道册数/页数），可进 `summary` | |
| `thumb` / `cover` | `series` 封面（multipart 上传，413 降级已有） | EH 图有防盗链，要在 `downloadImage` 加 referer |
| 画廊 URL | `links{label:"EH", url}` | **同时**把 host 加进 `IsSourceLink` 白名单，否则「已刮跳过」失效、重复刮 |

### 4.4 中文显示：英文命名空间做路由，中文做显示

不要照 Putarku 那样把 `artist:` 整个换成 `艺术家:`（后面按 `artist:` 过滤就全废）。
推荐双写：`artist:asanagi`（路由/过滤用）+ `画师:朝凪`（给人看）。

---

## 5. 落地路线

| 阶段 | 内容 | 验收标准 | 预估改动 |
|---|---|---|---|
| **P0 打通** | ① 实测复核「series 级 authors 无效」；② 新建 `sources/ehentai.go`（搜索 + gdata + 代理 + 520/509 退避）；③ 粘贴画廊链接 → 预览 → 写入 | 贴一个 EH 链接，Komga 里标题/画师/标签/封面对得上；重复跑不重复写 | 约 8 个文件 |
| **P1 中文与离线** | ④ 打包 `db.text.json` + 翻译模块；⑤ 离线内嵌元数据 provider（ComicInfo.xml / info.json / galleryinfo.txt）；⑥ 标签截断与去重策略 | 标签全中文；先把用户库里本来就带元数据的那批零网络刮完 | 约 5 个文件 |
| **P2 自动化** | ⑦ 同人志文件名解析 + 候选面板 + AI 择优（不自动写）；⑧ nHentai v2 作第二站；⑨ 失败哨兵 + 断点续跑 | 批量跑 200 本，误配率可接受、可断点、可重试失败项 | 约 10 个文件 |

**必踩的坑（写代码前先看）**：
- Go 侧有**两份重复的源构建**（`api/server.go:275-345` 与 `api/handlers_v2.go:1301-1333`），只改一处会「任务能用、候选面板没有」；
- 前端有**三份来源标签 map**（`CorrectMatchModal.vue`、`JobDetailView.vue`、`labels.js`），后者还缺 `genres/links/alternateTitles` 的中文名；
- tags/links 是**整数组覆盖写**，必须读-改-写，否则会把 Bangumi 写的标签冲掉；
- `CleanMeta` 会吞掉空字符串，想「源里没有就清空旧值」必须走 `PatchSeriesMetadataRaw`。

---

## 6. 需要你拍板的 6 件事

1. **只做表站还是要 exhentai？** 要 ex 就得在设置里加三个 cookie 输入（`ipb_member_id`/`ipb_pass_hash`/`igneous`），
   并接受「cookie 会过期、要重抓」的运维成本。表站免登录但内容少很多。
2. **自动写库还是只给候选？** 我倾向：链接路径可以直接写，自动路径只出候选让人点（本子脏标题太多）。
3. **标签灌多少？** EH 一个画廊常 30-60 个标签，全灌会让 Komga 侧栏爆炸。建议照 Mox 的 12 条上限 + 可按命名空间配权重。
4. **中文标签怎么存？** 我建议双写（英文命名空间 + 中文显示名），你接受吗？
5. **画师落哪？** 先按「book 级 authors + tag `画师:`」双保险（因为 series 级 authors 经查是无效字段）。
6. **要不要同步桌面版（C#）？** 只改 Go/Docker 侧能自洽；桌面版要另做一遍（它的设置是 typed class，不共享 Go 的 blob）。

---

## 7. 证据与临时文件

| 文件 | 内容 |
|---|---|
| [lanraragi-plugins.md](lanraragi-plugins.md) | 20 个插件 + 7 个周边仓库逐行拆解，结论带 `文件:行号`，标注 🟢实测/🔵代码/🟡推断 |
| [komga-integration-seams.md](komga-integration-seams.md) | 我们这边的接入缝隙：源契约/链路/落库/配置/任务/测试 + 21 行最小改动清单 |
| [source-probes.md](source-probes.md) | 网络矩阵、EH gdata、nHentai v2、Hitomi、翻译库的原始请求与响应 |
| `.scratch/`（10.8MB，未入库） | 抓下来的插件源码、EH/HDoujin 真实响应、`db.text.json` 副本，可复查；**随时可删** |
