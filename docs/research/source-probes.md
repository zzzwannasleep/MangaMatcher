# 本子源可行性实测记录（2026-10-20）

> 本文件是**实测日志**，不是结论。所有数字都是在本机（Windows, 直连 + 本地代理 `127.0.0.1:7890`）跑出来的。
> 复现命令都写在对应小节里，结论汇总见 [doujin-scraping-assessment.md](doujin-scraping-assessment.md)。

## 1. 网络可达性矩阵

| 站点 | 直连 | 走本地代理 | 说明 |
|---|---|---|---|
| `api.e-hentai.org/api.php` | ❌ 000（15s 后连接被重置） | ✅ 200 | 公开 JSON API，无需登录 |
| `e-hentai.org`（搜索页/画廊页） | ❌ 000（TLS 握手被 reset） | ✅ 200 | Cloudflare 前置，必须带浏览器 UA |
| `exhentai.org` | ❌ 000 | ⚠️ 302 | 未登录必然跳转，**必须** `ipb_member_id` + `ipb_pass_hash` + `igneous` cookie |
| `nhentai.net/api/v2/*` | ❌ 000 | ✅ 200 | 新版 v2 API，见 §3 |
| `nhentai.net/api/gallery/<id>`（旧接口） | ❌ 000 | ❌ 403 | 返回纯文本 `Use new API https://nhentai.net/api/v2/docs`，**已废弃** |
| `hitomi.la` | ❌ 000 | ✅ 200 | 搜索页 3687B（极简壳，结果靠 JS） |
| `ltn.gold-usergeneratedcontent.net`（hitomi 数据域） | ❌ 000 | ✅ 200 | `galleries/<id>.js` 返回 `var galleryinfo = {...}` |
| `hdoujin.org` | ✅ 200 | ✅ 200 | 唯一直连可通的，但要注册 + 抓 Bearer UUID |
| `api.bgm.tv` | ❌ 000（超时） | ✅ 200 | 对照项：说明本机直连本身就受限 |

**结论：本子源（EH/nH/hitomi）在国区网络下 100% 需要代理。** 这与仓库现有的
`sources/proxy.go` 代理能力天然契合——不是新增负担，而是已有能力必须覆盖到新源。

## 2. E-Hentai：搜索（HTML）→ 元数据（JSON）两段式

### 2.1 搜索页

```powershell
curl.exe -s -x http://127.0.0.1:7890 -A "<浏览器UA>" `
  -H "Accept-Language: en-US,en;q=0.9" "https://e-hentai.org/?f_search=asanagi"
```

实测：`200`，`69371` 字节，标题 `E-Hentai Galleries - ...`，正则
`href="(https://e-hentai\.org/g/(\d+)/([0-9a-f]+)/)"` 命中 **25** 条（一页 25 条，分页靠 `?page=N`）。
`gid` + `token` 直接从链接里拿，**这就是 api.php 的入参**。

支持的搜索语法（与 LANraragi 插件同源）：`artist:xxx$`、`language:chinese`、`parody:"xxx"`、
`f_cats=`（分类过滤）、`f_sname=on`（只搜标题）、`f_stags=on`（搜标签）。

### 2.2 gdata 元数据 API

```powershell
# 注意：body 必须用 --data-binary @file 提交，PowerShell 内联引号会让服务端回 "Invalid JSON Request"
curl.exe -s -x http://127.0.0.1:7890 -X POST "https://api.e-hentai.org/api.php" `
  -H "Content-Type: application/json" --data-binary "@req.json"
# req.json: {"method":"gdata","gidlist":[[4268188,"2e7abf5638"]],"namespace":1}
```

实测返回字段（`gmetadata[0]`）：

```
gid, token, title, title_jpn, category, thumb, uploader, posted(unix), filecount, filesize,
expunged, rating("4.32"), torrentcount, torrents[], tags[], parent_gid, parent_key, first_gid, first_key
```

`tags` 是**扁平字符串数组，自带命名空间**，53 条样例：

```
parody:azur lane
parody:genshin impact
character:ganyu
character:miku hatsune
female:ahegao
artist:asanagi
group:...
language:chinese
male:...
mixed:...
other:...
reclass:...
```

- 单次批量上限 **25 个 gid**（与搜索结果一页数量正好对齐）。
- `namespace=1` 表示返回英文标签（`namespace=0` 为原始标签和日文）。
- `thumb` 可直接当封面源（`ehgt.org` 域）。
- **没有** 作者/社团的结构化字段，只能从 `artist:` / `group:` 标签里取。

### 2.3 关键坑

- **exhentai 不可匿名访问**：无 cookie 一律 302。要么只支持 e-hentai（SFW 池子小得多），
  要么让用户填 `ipb_member_id` / `ipb_pass_hash` / `igneous`（`igneous` 会过期，需要引导用户重抓）。
- EH 对高频请求会返回 **509 Bandwidth Exceeded** 或临时 IP ban；搜索页请求必须串行 + 间隔。
- Search 页对某些内容有「Content Warning」中间页，需要额外一次 POST 确认。

## 3. nhentai：官方 v2 API（最省事的本子源）

```powershell
curl.exe -s -x http://127.0.0.1:7890 -A "<UA>" -H "Accept: application/json" `
  "https://nhentai.net/api/v2/search?query=artist:jinja-eru%24%20language:chinese&page=1"
```

- `https://nhentai.net/api/v2/openapi.json` 是完整 OpenAPI（244KB），`/api/v2/docs` 是 Swagger UI。
- `GET /api/v2/search?query=<语法>&page=N` → `{"result":[...]}`，**无需鉴权**。
  复杂查询语法实测有效：`artist:jinja-eru$ language:chinese` 命中 4 条。
- `GET /api/v2/galleries/{id}` 返回单本详情，实测 `687731`：

```jsonc
{
  "id": 687731, "media_id": "4239143",
  "title": { "english": "...", "japanese": "...", "pretty": "お散歩朝凪ちゃん" },
  "cover":     { "path": "galleries/4239143/cover.jpg.webp", "width": 350, "height": 184 },
  "thumbnail": { "path": "galleries/4239143/thumb.jpg.webp",  "width": 250, "height": 131 },
  "scanlator": "", "upload_date": 1792007998, "num_pages": 3, "num_favorites": 91,
  "tags": [ { "id": 33172, "type": "category", "name": "doujinshi", "slug": "doujinshi",
              "url": "/category/doujinshi/", "count": 513967, "description": "..." }, ... ],
  "pages": [...]
}
```

`tags[].type` 实测出现：`category` / `language` / `tag` / `parody` / `artist` / `character`
（nhentai 没有 `circle`/`group` 独立命名空间，社团常常并进 `artist` 或 `group` 标签）。

- 列表接口 `search` 只给 `tag_ids`（数字），要么再调 `/api/v2/tags/ids?ids=...` 反解，要么直接对命中项调
  `/api/v2/galleries/{id}` 拿带名字的标签（本仓库刮削量下，后者更简单，N+1 但 N 很小）。
- 有 `/api/v2/pow`、`/api/v2/captcha` 端点，说明**未来可能上 PoW/人机验证**；现在实测未触发，但要把失败当常态处理。
- 无 cookie 时**看不到**部分内容（blacklist/年龄确认），`blacklisted` 字段要尊重。

## 4. hitomi.la

- 画廊元数据：`https://ltn.<随机域名>/galleries/<id>.js`，返回 `var galleryinfo = {...}`：

```jsonc
{"blocked":1,"characters":null,"language_url":"/index-english.html",
 "related":[5479,...],"scene_indexes":[],"type":"anime",
 "tags":[{"url":"/tag/female%3Aahegao-all.html","male":"","female":1,"tag":"ahegao"},...]}
```

- 搜索：`https://hitomi.la/search.html?<query>-`（query 里 `language:chinese artist:asanagi` 用 `%20` 连接），
  返回 3687B 的极简 HTML 壳，真正的列表在页面内 JS 变量里——解析成本比 EH 高。
- 数据域名会轮换（`ltn.gold-usergeneratedcontent.net` → 其他 `*.gold-usergeneratedcontent.net`），
  不能硬编码，要从首页 JS 里取当前域。**这是 hitomi 最主要的维护成本。**

## 5. 中文标签数据源：EhTagTranslation/Database（可用）

- 仓库：`https://github.com/EhTagTranslation/Database`，机器可读产物挂在 **Release**：
  `db.text.json.gz`（1.29MB → 4.29MB）、`db.html.json`、`db.ast.json`、`db.text.js` 等。
- 实测下载 `v7.29064.1`（2026-10-20 05:38 发布，**今天还在更新**，bot 自动发版）。
- 结构：

```jsonc
{
  "version": 7,
  "head": { "sha": "48e0589...", "message": "添加 character:komaro michinoku ...", "committer": {...} },
  "data": [
    { "namespace": "artist", "data": { "asanagi": { "name": "朝凪", "intro": "纯爱画师，轻小说…插画作者…", "links": "X pixiv" } } },
    { "namespace": "parody", "data": { "azur lane": { "name": "碧蓝航线", "intro": "《碧蓝航线》是一款…", "links": "萌娘百科 维基百科" } } }
  ]
}
```

- 命名空间实测：`rows, reclass, language, parody, character, group, artist, cosplayer, male, female, mixed, other, location`
- 实测命中样例：

| 原始标签 | 中文名 | 简介 |
|---|---|---|
| `artist:jinja eru` | 神社えーる | （空） |
| `artist:asanagi` | 朝凪 | 纯爱画师，轻小说《半兽人英雄物语 忖度列传》插画作者… |
| `parody:azur lane` | 碧蓝航线 | 《碧蓝航线》是一款… |
| `female:ahegao` | 阿黑颜 | 夸张的面部表情表示高兴… |
| `language:chinese` | 汉语 | 汉语，又称中文… |
| `character:ganyu` | 甘雨 | 所属作品：原神(genshin impact) |

→ 这张表足以支撑「Komga 里显示中文标签」：`artist:asanagi` 落库时写成 `画师:朝凪`，未收录的回落英文原名。

## 6. Komga 侧写入门槛（对照用，详见 komga-integration-seams.md）

`SeriesMetadata`（`gotson/komga` `domain/model/SeriesMetadata.kt`）可写：
`status, title, titleSort, summary, readingDirection, publisher, ageRating, language, genres, tags,
totalBookCount, sharingLabels, links, alternateTitles` — **没有 authors**。

`BookMetadata` 才有 `authors / notableEntities / tags / links / isbn / releaseDate`。

→ 本子的画师/社团要落到 **book 级 authors**（一册一个系列时可行），或退化成
`tags` 里的 `画师:朝凪`。
