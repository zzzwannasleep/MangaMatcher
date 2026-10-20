# LANraragi 本子刮削插件生态 — 可复用资产清单

> **目的**：把 [Putarku/LANraragi-scripts](https://github.com/Putarku/LANraragi-scripts) 及 README 提到的全部周边仓库读透，判断哪些思路能复用到「Komga 刮削本子（同人志/成人向同人本）」。
> **产出性质**：只读梳理 + 实测，不含实现。所有结论带 `文件:行号` 或原始片段。
> **证据等级标注**（全文遵守）：
> - 🟢 **实测**：本次在 D:\WorkProgram\komgabangumidocker 用 pwsh/curl 真实请求得到的响应
> - 🔵 **代码**：从仓库源码直接读到的逻辑（行号可核）
> - 🟡 **推断**：由代码/数据推导，未经独立验证
>
> **相关文档**：网络可达性矩阵见 [source-probes.md](source-probes.md)；Komga 侧接入缝隙见 [komga-integration-seams.md](komga-integration-seams.md)。本文聚焦「插件逻辑与可复用资产」，与上述两篇互补不重复。
>
> 实测时间：2026-10-20（Asia/Shanghai）。直连 + 本地代理 `http://127.0.0.1:7890`。

---

## 0. 结论速览（TL;DR）

1. **E-Hentai 的 `gdata` API 是唯一「无需登录、结构化、字段最全」的本子元数据源**，且实测可直连（走代理）拿到 JSON：`POST https://api.e-hentai.org/api.php`，body `{"method":"gdata","gidlist":[[gid,token]],"namespace":1}`。🟢 无需任何 cookie。
2. **中文标签链路的正确答案是 EhTagTranslation 的 `db.text.json`**（13 个命名空间、约 4.1 万条翻译）。Putarku 那批 `translate_tag_to_cn()` 实现有 **3 个系统性缺陷**（见 §5.4），照搬会踩坑。
3. **最值得照搬的是「离线优先 + 网络兜底」的分层架构**：先读档案内嵌元数据（`meta.json` / `info.json` / `ComicInfo.xml` / `galleryinfo.txt` / `.ehviewer`，全部零网络），失败才走站点 API；站点之间再按 `source:` 标签瀑布式回退。这套「本地 → source → 多站瀑布」的顺序在 Putarku 仓库里有 5 个可复用的独立实现。
4. **`hdoujin.org` 是当前唯一实测直连可达的本子站 API**（Cloudflare 后，`X-Ratelimit-Limit: 5`），但它的两个端点对请求头要求不同：`/books` 需 `Origin`+`Referer` 同时存在，`/books/detail/*` 只需 `Origin`。🟢 这条是本次新发现，插件代码里**没有**记录这个差异。
5. **hentag.com 已停止服务**（首页为告别公告），`HentagOnline*.pm` 整个插件族已失效。🟢
6. **`ltn.hitomi.la` 实测不可达**（直连与代理均 `000`），Hitomi 插件只有代码证据、无实测响应。

---

## 1. 仓库地图

### 1.1 主仓库 Putarku/LANraragi-scripts 🟢

GitHub API 实测：`default_branch=main`、`size=994KB`、`pushed_at=2026-05-25`、`stars=43`、描述「自用LANraragi相关脚本」。仅 `main` 一个分支。完整文件树（28 个非图片 blob）：

```
  4810  ApplyCopyrightTags.py
 23822  ExHentai_Lanraragi_Checker.js
 14829  hdoujin.pm                      ← 根目录，新版 HDoujin 插件
  4448  Plugins/addEhentaiMetadata.pm
  4655  Plugins/addEtagCNMetadata.pm
  4169  Plugins/addhentag.pm
 11663  Plugins/ComicLooms.pm
  3273  Plugins/CopyTagsFromFilename.pm
 12956  Plugins/EHentai.pm
 15410  Plugins/EHentai_CN.pm
 11311  Plugins/Ehviewer_Color.pm
  9458  Plugins/Ehviewer_Green.pm
  4943  Plugins/ETagConverter.pm
 10565  Plugins/Eze_CN.pm
  8575  Plugins/hdoujincn.pm            ← 旧版 HDoujin 插件
 10484  Plugins/HentagOnline.pm
 13973  Plugins/HentagOnline_CN.pm
 15073  Plugins/Hitomi_CN.pm
  8036  Plugins/nHentai.pm
 12628  Plugins/nHentai_CN.pm
  7052  Plugins/nHentaiv2.pm
  2641  Plugins/Readme.md
 13014  Plugins/SourceBatch.pm
  1559  Plugins/油猴Ehentai进入详情页自动打开收藏选择.js
  5074  Plugins/油猴Ehentai用绿E下载数据高亮已下载的本.js
  8214  Plugins/油猴Lanraragi编辑元数据标签翻译及自动输入.js
  3938  README.md
 17043  search_keywords.txt
```

**注意**：任务书里列出的 `Plugins/EHentai_CN.pm` 等文件在 `Plugins/` 下，而 `hdoujin.pm` 在**仓库根目录**（不在 `Plugins/`）。

### 1.2 周边仓库 🟢

| 仓库 | 分支 | 大小 | 最近推送 | Stars | 与本文关系 |
|---|---|---|---|---|---|
| [zhy201810576/ETagCN](https://github.com/zhy201810576/ETagCN) | master, v0.8.9 | 2582KB | 2026-06-09 | 115 | 中文 EH 插件**原始实现**（比 Putarku 版更干净） |
| [zhy201810576/ETagConverter](https://github.com/zhy201810576/ETagConverter) | master | 960KB | 2023-03-24 | 68 | 全库批量英→中转换脚本 |
| [chu-shen/LANraragi-scripts](https://github.com/chu-shen/LANraragi-scripts) | gh-pages, main | 1891KB | 2026-10-15 | 34 | 插件集合 + 翻译库生成脚本 |
| [chu-shen/LANraragi](https://github.com/chu-shen/LANraragi) | **feat-ratingAndcomment**, master | 93MB | 2026-07-11 | 11 | LRR 本体 fork，含上游全部官方插件与**测试 fixture** |
| [chihiro830/LANraragiPlugin](https://github.com/chihiro830/LANraragiPlugin) | main | 12KB | 2024-12-09 | 5 | `FolderToCat` 增强版（与刮削无关） |
| [starP-W/HAtH-Plugin-CN](https://github.com/starP-W/HAtH-Plugin-CN) | master | 870KB | 2024-03-14 | 3 | 读 `galleryinfo.txt` |
| [Difegue/Tsukihi](https://github.com/Difegue/Tsukihi) | main | 514KB | 2024-04-18 | 58 | 浏览器扩展，把当前页 URL 推给 LRR |

> 🟡 **重要发现**：`chu-shen/LANraragi` fork 的 `tests/LANraragi/Plugin/Metadata/*.t` 里有**上游官方插件的 mock 响应**（EHentai/Hitomi/nHentai/HatH/Hentag/Eze/RegexParse 等），这是除实测外最好的接口形态证据来源。本文已下载但未逐一引用，建议后续需要时优先查这批 fixture。

---

## 2. 网络可达性实测 🟢

### 2.1 直连（无代理）

| 目标 | 结果 | 耗时 | 错误形态 |
|---|---|---|---|
| `https://api.e-hentai.org/api.php` | ❌ 000 | 16.2s | 无法连接到远程服务器 |
| `https://e-hentai.org/` | ❌ 000 | 0.23s | 基础连接已经关闭（TLS 快速重置，典型 SNI 阻断） |
| `https://exhentai.org/` | ❌ 000 | 21.3s | 操作超时 |
| `https://nhentai.net/api/gallery/1` | ❌ 000 | 20.1s | 操作超时 |
| `https://hitomi.la/` | ❌ 000 | 20.0s | 操作超时 |
| `https://hdoujin.org/` | ✅ 200 | 0.96s | 4000 bytes（SPA 外壳） |
| `https://api.github.com/repos/...` | ✅ 200 | 0.73s | — |

环境无 `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` 环境变量。

### 2.2 经本地代理 `http://127.0.0.1:7890`（端口探测 `True`）

| 目标 | 状态 | 说明 |
|---|---|---|
| `https://e-hentai.org/` | ✅ 200 | 0.29s |
| `https://api.e-hentai.org/api.php` | ✅ 200 | 0.82s |
| `https://exhentai.org/` | ⚠️ **302** | 无 cookie → 重定向（需 `ipb_*`） |
| `https://nhentai.net/api/v2/galleries/1` | ✅ 200 | 4762 bytes |
| `https://nhentai.net/api/v2/search?query=test` | ✅ 200 | 12048 bytes |
| `https://hitomi.la/` | ✅ 200 | **但仅 4971 bytes = SPA 外壳**，无服务端渲染内容 |
| `https://ltn.hitomi.la/` | ❌ 000 | 即使走代理也连不上（DNS/阻断） |
| `https://ltn.hitomi.la/galleries/{id}.js` | ❌ 000 | 同上，试了 4 个 id 全部 000 |
| `https://hitomi.la/doujinshi-all.html` | ❌ 404 | 555 bytes |
| `https://hentag.com/` | ⚠️ 200 | **站点已停运**（见 §4.4） |
| `https://hentag.com/api/v1/search/vault/id` | ❌ 404 | API 已下线 |

**未观察到任何 Cloudflare 挑战页（`cf-mitigated` / JS challenge）**。`api.hdoujin.org` 位于 Cloudflare 之后（`Server: cloudflare`）但返回的是应用层 400，不是挑战页。

---

## 3. E-Hentai 系（EHentai.pm / EHentai_CN.pm / ETagCN.pm）

这是整个生态最核心的一条链路，也是复用价值最高的一条。

### 3.1 搜索入口与回退顺序 🔵

`Plugins/EHentai.pm:50-115` 的 `get_tags` 按**四级优先级**决定 gID/gToken：

| 级 | 条件 | 代码位置 |
|---|---|---|
| 0 | 用户 oneshot 传入 URL | `EHentai.pm:67` `.*\/g\/([0-9]*)\/([0-z]*)\/*.*` |
| 1 | 已有 `source:` 标签（**跳过搜索**，`$hasSrc=1`） | `EHentai.pm:71` `.*source:\s*e(?:x\|-)hentai\.org\/g\/([0-9]*)\/([0-z]*)\/*.*` |
| 2 | 缩略图反向搜索 | `EHentai.pm:133` `?f_shash={thumbhash}&fs_similar=on&fs_covers=on` |
| 3 | 标题里的 gID | `EHentai.pm:151` `?f_search=gid:{n}` |
| 4 | 标题文本搜索（兜底） | `EHentai.pm:163` |

第 4 级的 URL 构造（`EHentai.pm:163`）是复用重点：

```perl
$URL = $domain . "?advsearch=1&f_sfu=on&f_sft=on&f_sfl=on" . "&f_search=" . uri_escape_utf8( qw(") . $title . qw(") );
```

- `advsearch=1` 开高级搜索
- `f_sfu=on`（禁用 Uploader 过滤）、`f_sft=on`（禁用 Tags 过滤）、`f_sfl=on`（禁用 Language 过滤）—— **关闭这三项是让搜索能命中的关键**，否则 EH 默认筛选会吃掉结果 🟡
- `f_search` 被**整体加双引号**（精确短语匹配）

### 3.2 作者名是否参与检索 🔵

**参与，但有硬条件**（`EHentai.pm:165-174`）：

```perl
if ( $tags =~ /.*artist:\s?([^,]*),*.*/gi ) {
    my $artist = $1;
    if ( $artist =~ /^[\x00-\x7F]*$/ ) {          # ← 只接受纯 ASCII
        $URL = $URL . "+" . uri_escape_utf8("artist:$artist");
        $has_artist = 1;
    }
}
```

即：**只有已有 `artist:` 标签且值全为 ASCII 时才附加作者名**。中文/日文作者名（如「愛上陸」）一律被丢弃 → 这是中文库的明显短板。`$has_artist` 赋值后从未被使用（死变量）🟡。

语言过滤在 `EHentai.pm:177-179`（`language:{lang}`），已删除图库搜索在 `EHentai.pm:182-184`（追加 `&f_sh=on`）。

### 3.3 结果解析与择优 🔵

`EHentai.pm:192-228` `ehentai_parse()`：

```perl
my $firstgal = $dom->at(".glink")->parent->attr('href');
my $url    = ( split( 'hentai.org/g/', $firstgal ) )[1];
my @values = ( split( '/',             $url ) );
$gID    = $values[0];
$gToken = $values[1];
```

- 用 **Mojo::DOM 的 CSS 选择器 `.glink`**，取第一个结果的父 `<a>` 的 `href`
- **没有相似度比较，直接取第一条**（与 Hentag 插件的 `most_similar` 择优形成对比，见 §4.4）
- 注释声明「This works in Minimal, Minimal+ and Compact modes」

🟢 **实测验证**：经代理抓 `https://e-hentai.org/?advsearch=1&f_sfu=on&f_sft=on&f_sfl=on&f_search=%22%28C100%29%22`，HTTP 200、72548 bytes，页面中 `class="glink"` 出现 **25 次**，正则 `href="(https://e-hentai\.org/g/(\d+)/([0-9a-z]+)/?)"` 成功提取出 gid/token（例：`4264303 / ffd224e2bf`、`4247907 / 3731c4b2f6`）。**该选择器在 2026-10 仍然有效。**

### 3.4 gdata API 与真实响应 🟢

`EHentai.pm:292-317`：

```perl
my $rep = $ua->post(
    $uri => json => {
        method    => "gdata",
        gidlist   => [ [ $gID, $gToken ] ],
        namespace => 1
    }
)->result;
...
if ( exists $jsonresponse->{"error"} ) { return; }
```

- 端点：`POST https://api.e-hentai.org/api.php`
- 头：`Content-Type: application/json`
- **无需任何鉴权/cookie** 🟢（本次请求未带任何 cookie 即 200）
- `namespace => 1` 让返回的 tags 带 `namespace:` 前缀

🟢 **本次实测真实响应**（gid=4264303, token=ffd224e2bf，HTTP 200，811 bytes）：

```json
{"gmetadata":[{"gid":4264303,"token":"ffd224e2bf",
"title":"(C100) [kibun tenkan (Eru Takagaki)] untitled (THE iDOLM@STER: Shiny Colors)",
"title_jpn":"(C100 [kibun tenkan (\u9ad8\u57a3\u3048\u308b)] untitled (\u30a2\u30a4\u30c9\u30eb\u30de\u30b9\u30bf\u30fc \u30b7\u30e3\u30a4\u30cb\u30fc\u30ab\u30e9\u30fc\u30ba)",
"category":"Non-H",
"thumb":"https://ehgt.org/w/02/726/79228-qj7qexko.webp",
"uploader":"panzzzzz","posted":"1792337691","filecount":"30","filesize":595902017,
"expunged":false,"rating":"4.70","torrentcount":"1",
"torrents":[{"hash":"ef54e28d429db1361a43e005664f31a3f7220d69","added":"1792338982",
"name":"(C100) [kibun tenkan (Eru Takagaki)] untitled (THE iDOLM@STER_ Shiny Colors).zip",
"tsize":"45847","fsize":"595959764"}],
"tags":["parody:the idolmaster","other:artbook","other:full color"]}]}
```

**字段清单与类型**（对 Komga 字段映射有直接价值）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `gid` / `token` | int / string | 画廊主键，构成 source URL |
| `title` / `title_jpn` | string | 英文（罗马音）标题 / 日文原标题 |
| `category` | string | **"Non-H"**（含连字符与大写），需 `lc` 后映射 |
| `thumb` | URL | 封面图，可直接作 Komga `thumbnailUrl` |
| `uploader` | string | 上传者 |
| `posted` | **字符串** unix 时间戳 | 需转 int |
| `filecount` / `filesize` | **字符串** | 页数 / 字节数 |
| `expunged` | bool | 已删除（expunge）标记 |
| `rating` | **字符串** `"4.70"` | 5 分制，需转 float |
| `torrentcount` / `torrents[]` | int / array | 种子信息 |
| `tags[]` | string array | 形如 `"parody:the idolmaster"`、`"other:artbook"`（已带命名空间、全小写、空格分隔） |

**注意**：`posted`、`filecount`、`filesize`、`rating`、`torrentcount` 都是**字符串**，Perl 代码里直接 `push(@tags, "timestamp:$ehtimestamp")`（`EHentai.pm:278-280`）不做类型转换——移植时需显式转型。

### 3.5 产出的元数据字段 🔵

`EHentai.pm:253-290` `get_tags_from_EH()`：

```perl
my @tags    = @{ @$data[0]->{"tags"} };
my $ehtitle = @$data[0]->{ ( $jpntitle ? "title_jpn" : "title" ) };
if ( $ehtitle eq "" && $jpntitle ) { $ehtitle = @$data[0]->{"title"}; }
my $ehcat = lc @$data[0]->{"category"};
push( @tags, "category:$ehcat" );
if ($additionaltags) {
    push( @tags, "uploader:$ehuploader" );
    push( @tags, "timestamp:$ehtimestamp" );
}
$ehtitle = html_unescape($ehtitle);
```

关键点：
- **`category` 被小写后作为 `category:` 命名空间注入**（`EHentai.pm:273,275`）——这是后面翻译链路出 bug 的根源（§5.3）
- 标题优先 `title_jpn`（若开启该选项），回退 `title`
- 标题做 `html_unescape`（`Mojo::Util`），因 EH 标题含 HTML 实体
- `source:` 在 `get_tags` 里拼接（`EHentai.pm:109`）：`source:e-hentai.org/g/$gID/$gToken` 或 `source:exhentai.org/...`

**没有**输出的字段：页数、评分、发布日期（只在 `timestamp:` 标签里）、社团/角色（在 tags 里）、封面 URL（**完全未用**）。🟡 对 Komga 而言 `thumb`/`filecount`/`rating`/`posted` 都是白捡的增量字段。

### 3.6 标签命名空间体系 🔵

EH 侧：标签由 API 直接给出 `namespace:value`（namespace 已由 `namespace:1` 参数展开），插件额外注入 `category:`、`uploader:`、`timestamp:`、`source:`。实测样本中出现的 EH 命名空间：`parody:`、`other:`。

LRR 侧的标准命名空间（由 `RegexParse.pm:52-57` 与 `Ehviewer_Color.pm:166-171` 归纳）：`artist:`、`group:`、`series:`/`parody:`、`character:`、`language:`、`female:`、`male:`、`other:`、`event:`、`source:`、`category:`。

### 3.7 风控与失败处理 🔵

| 现象 | 判定方式 | 处理 | 位置 |
|---|---|---|---|
| 响应体为空 | `$res->body eq ''` | 报错「igneous cookie 可能已过期」 | `EHentai.pm:238-241` |
| IP 被临时封禁 | `index($res->body, "Your IP address has been") != -1` | 报错 | `EHentai.pm:243-246` |
| 请求过多警告 | `index($dom->to_string, "You are opening") != -1` | **随机 sleep 15-50 秒** | `EHentai.pm:220-224` |

```perl
if ( index( $dom->to_string, "You are opening" ) != -1 ) {
    my $rand = 15 + int( rand( 51 - 15 ) );
    $logger->info("由于 EH 过多请求警告而休眠 $rand 秒");
    sleep($rand);
}
```

**注意**：这里的 sleep 发生在**解析出 gID 之后**（第 220 行在 216-218 的 eval 之后），也就是说警告页出现时它已经解析失败了，sleep 只是延迟后续请求。🟡 逻辑上「先睡再重试」更合理，但代码没有重试。

- `cooldown => 4`（`EHentai.pm:44`）— 插件级最小间隔 4 秒
- `max_redirects(5)`（`EHentai.pm:235`）
- 代理需求：🟢 本次实测**直连完全不通**（`e-hentai.org` TLS 重置、`api.e-hentai.org` 连接失败），必须走代理。这与 [source-probes.md](source-probes.md) 的矩阵一致。

### 3.8 登录/Cookie（exhentai 必需）🔵

`lib/LANraragi/Plugin/Login/EHentai.pm:23-26` 定义 4 个参数：

| 参数 | 说明（原文） |
|---|---|
| `ipb_member_id` | `ipb_member_id cookie`（int） |
| `ipb_pass_hash` | `ipb_pass_hash cookie` |
| `star` | `star cookie (optional, if present you can view fjorded content without exhentai)` |
| `igneous` | `igneous cookie(optional, if present you can view exhentai without Europe and America IP)` |

代码把 `ipb_member_id` / `ipb_pass_hash` / `igneous` **同时注册到 `exhentai.org` 与 `e-hentai.org` 两个域**（`Login/EHentai.pm:57-124`），另在 `forums.e-hentai.org` 域注册 `ipb_coppa`（`:129-133`）。

🟢 **实测印证**：无 cookie 时 `https://exhentai.org/` 返回 **302**（重定向到 sadpanda），说明表站/里站分离确实靠 cookie 区分。而 `api.e-hentai.org` 不需要 cookie。

---

## 4. 各站点插件逐个拆解

### 4.1 nHentai 系（nHentai.pm / nHentai_CN.pm / nHentaiv2.pm）

**两代接口并存** 🔵：

| 插件 | 搜索 | 详情 |
|---|---|---|
| `Plugins/nHentai.pm`（旧） | `https://nhentai.net/search/?q={title}`（`:103`，**抓 HTML**） | `https://nhentai.net/g/{id}/`（`:154`，**抓 HTML**） |
| `Plugins/nHentaiv2.pm` | `https://nhentai.net/api/v2/search?query={title}`（`:100`） | `https://nhentai.net/api/v2/galleries/{id}`（`:144`） |
| `Plugins/nHentai_CN.pm` | 同 v2（`:117`） | 同 v2（`:161`） |

**v2 API 真实结构** 🟢（`GET https://nhentai.net/api/v2/galleries/1`，HTTP 200，4762 bytes）：

```
top keys: ['id','media_id','title','cover','thumbnail','scanlator','upload_date','tags','num_pages','num_favorites','pages']
id=1
title={"english":"(C71) [Arisan-Antenna (Koari)] Eat The Rich! (Sukatto Golf Pangya)",
       "japanese":"(C71) [ありさんアンテナ (小蟻)] Eat The Rich! (スカッとゴルフ パンヤ)",
       "pretty":"Eat The Rich!"}
media_id=9  scanlator=""  upload_date=1403964737  num_pages=14
tags[16] 示例：
 {"id":33172,"type":"category","name":"doujinshi","slug":"doujinshi","url":"/category/doujinshi/","count":513808,"description":"..."}
 {"id":6346,"type":"language","name":"japanese","slug":"japanese","url":"/language/japanese/","count":342930}
 {"id":35762,"type":"tag","name":"sole female","slug":"sole-female","url":"/tag/sole-female/","count":206243,"is_community":true}
tag type 直方图: {'category':1,'language':1,'tag':10,'parody':1,'group':1,'artist':1,'character':1}
cover keys: ["path","width","height"]   thumbnail keys: ["path","width","height"]
```

**搜索响应结构** 🟢（`GET /api/v2/search?query=test`，HTTP 200，12048 bytes）：

```
top keys: ['result','num_pages','per_page','total']
result len=25
first item keys: ['id','media_id','english_title','japanese_title','thumbnail','thumbnail_width','thumbnail_height','num_pages','num_favorites','tag_ids','blacklisted']
```

**与插件代码的对应关系**（🔵 `nHentai_CN.pm:175-200`）：

```perl
my $namespace = $tag->{"type"};
my $name      = $tag->{"name"};
if ( $namespace eq "tag" ) { push( @tags, $name ); }        # type=tag → 裸标签
else                       { push( @tags, "$namespace:$name" ); }
...
return $json->{"title"}{"pretty"};                           # 用 pretty 标题
return $json->{"upload_date"};                               # upload_date
```

- 🟢 **完全吻合**：实测 `type` 取值恰为 `category/language/tag/parody/group/artist/character`，其中 `tag` 即无命名空间的普通标签，代码分支正确。
- 标题取 `title.pretty`（`nHentai_CN.pm:199`），注释解释（`:223`）：「Use NH's "pretty" names (romaji titles without extraneous data we already have like (Event)[Artist], etc)」——即刻意丢掉 `(C71)[作者]` 前缀，因为那部分已由文件名解析覆盖。**这个取舍对 Komga 值得借鉴**。
- 搜索只取第一条 `$results[0]->{"id"}`（`nHentai_CN.pm:148`），**无相似度择优**。
- 另有从文件名 `{123456}` 花括号中直取 gID 的捷径（`nHentai_CN.pm:138` `/\{(\d*)\}.*$/gm`）。
- `source:https://nhentai.net/g/$gID`（`nHentai_CN.pm:221`）。
- 🟢 搜索响应**不含** `tags`，只含 `tag_ids`；但插件只用 `id`，所以没问题 🟡。

### 4.2 Hitomi_CN.pm

🔵 端点与解析（`Plugins/Hitomi_CN.pm`）：

| 项 | 值 | 行 |
|---|---|---|
| source 标签正则 | `source:\s*hitomi\.la\/(?:manga\|doujinshi\|galleries\|cg)\/.*-?([0-9]*)\.html` | `:59` |
| 数据端点 | `https://ltn.hitomi.la/galleries/{gID}.js` | `:119` |
| 响应形态 | **JSONP 式 JS**，用正则剥出 JSON 字面量 | `:131-136` |
| 标签来源 | `json.tags[]` + `parodys`/`artists`/`groups`/`characters` 四个数组 | `:154-192` |
| 额外 | `push(@tags, "type:" . $json->{"type"})`、`"language:" . $json->{"language"}` | `:193-196` |
| source 输出 | `source:https://hitomi.la/galleries/$gID.html` | `:234` |

```perl
my @tags_list = @{ $json->{"tags"} };
foreach my $tag (@tags_list) { ... push( @tags, $name ); }      # :156-172
if ( defined $json->{"parodys"} )    { push( @tags, extract_tags( $logger, $json, "parodys",    "parody" ) ); }
if ( defined $json->{"artists"} )    { push( @tags, extract_tags( $logger, $json, "artists",    "artist" ) ); }
if ( defined $json->{"groups"} )     { push( @tags, extract_tags( $logger, $json, "groups",     "group" ) ); }
if ( defined $json->{"characters"} ) { push( @tags, extract_tags( $logger, $json, "characters", "character" ) ); }
```

`extract_tags`（`:201-213`）从 `$tag->{$namespace}` 取值 —— 即 Hitomi 的对象键名与命名空间**同名**（`artists[].artist`、`groups[].group`）。

🟢 **实测：不可达**。`ltn.hitomi.la` 直连与代理均 `000`（试 `galleries/1.js`、`2500000.js`、`2100000.js`、`2100010.js`、`1000000.js` 全失败）。`hitomi.la` 主站 200 但仅 4971 bytes SPA 外壳、无服务端渲染链接。**因此 Hitomi 的接口形态仅有代码证据，无实测响应** ⚠️。若 Komga 需要 Hitomi 源，必须先单独验证 `ltn.hitomi.la` 的可达性。

### 4.3 HDoujin 系（hdoujin.pm / hdoujincn.pm）— 本次实测最深入的站点

🟢 `hdoujin.org` 与 `api.hdoujin.org` 是**唯一直连可达**的本子站。README 说明（`README.md:26-31`）：

> 插件需要的 UUID 可以在站点注册后，在搜索页面用 f12 抓取，在标头的 `Authorization` 字段里，样子一般为 `Bearer adaddddd4-811e-4c09-8457-1dddddddae12`
> 目前 hdoujin.org 的搜索规则不太完善，使用 [作者名 画廊名] 检索时会没有返回数据。因此……仅会使用画廊的标题检索，并不会附带作者名。

#### 4.3.1 端点与鉴权 🔵

| 插件 | 搜索 | 详情 | 鉴权 |
|---|---|---|---|
| `Plugins/hdoujincn.pm`（旧） | `https://api.hdoujin.org/books?s={title_id\|title}&lang={lang}`（`:121`,`:131`） | `https://api.hdoujin.org/books/detail/{id}/{key}`（`:197`） | **无**（仅伪造头） |
| `hdoujin.pm`（新，根目录） | 同（`:135`,`:153`） | 同（`:238`） | `$tx->req->headers->header('authorization' => "Bearer $api_token")`（`:65`） |

参数：`{ type => "string", desc => "API认证令牌（必填，格式为UUID…）" }`（`hdoujin.pm:35`），`$lang = 12 if $lang eq ""`（`hdoujincn.pm:61`，12 = 中文）。

`hdoujincn.pm:145-159` 的伪造头（注释称「从 fatch 文件中提取的请求头」）：`Accept: */*`、`Accept-Language: zh-CN,zh;q=0.9`、`Sec-CH-UA-Mobile: ?1`、`Sec-CH-UA-Platform: "Android"`、`Sec-Fetch-Site: same-site`、Android Chrome 122 的 `User-Agent`、`Referer: https://hdoujin.org/`。

#### 4.3.2 🟢 实测：请求头要求比插件代码写的更严格

**这是本次最有价值的新发现。** A/B 实测（每格重复 2 次，结果稳定）：

| 请求头组合 | `/books?s=...`（搜索） | `/books/detail/{id}/{key}` |
|---|---|---|
| 无 Origin 无 Referer | **400** | **400** |
| 仅 `Referer` | **400** | **400** |
| 仅 `Origin` | **400** | **200** ✅ |
| `Origin` + `Referer` | **200** ✅ | **200** ✅ |

- **搜索端点要求 `Origin` 与 `Referer` 同时存在**；缺任一个都返回 400
- **详情端点只需 `Origin`**
- 400 响应体为空、`Content-Type` 缺失，属于应用层/WAF 拒绝，**不是** Cloudflare 挑战页

插件代码只显式设置了 `Referer`（`hdoujincn.pm:157`），`Origin` 仅在注释里提到「通常由浏览器自动添加，Mojo::UserAgent 可能不需要显式设置」（`hdoujincn.pm:158`）—— 🟢 **实测证明这个假设是错的：没有 `Origin` 时搜索必然 400**。这解释了为什么该插件在 LRR 里可能长期不工作。

#### 4.3.3 🟢 实测响应结构

搜索（`GET /books?s=gal&lang=12`，Origin+Referer，HTTP 200）：

```json
{"entries":[{"id":237450,"key":"dd6cceb0c5eb",
  "title":"[Saneyarou] Holy Winged Angel Luminastia henshin bōgai de dai pinchi [Chinese]",
  "subtitle":"[さね野郎] 聖翼天使ルミナスティアー 変身妨害で大ピンチ [DL版]",
  "language":8,"pages":43,
  "thumbnail":{"path":"https://erocdn.net/books/thumb/511546/.../xxx.webp","dimensions":[250,350]}}],
 "limit":80,"page":1,"total":129009,"errors":{"ambiguous":["gal"]}}
```

- `s=` 是**子串搜索**（`s=gal` 命中 `total=129009`），非精确匹配 → 高误配风险
- `errors.ambiguous` 会在查询词歧义时出现 🟡（插件**完全未处理**该字段）
- `language` 为数字码：实测 `4`=日语、`8`=中文、`6`=其他
- `fjords: true` 字段表示 fjord/expunged 类内容（搜索样本中出现）
- 分页：`limit`/`page`/`total`
- 响应头含 **`X-Ratelimit-Limit: 5`**、`X-Ratelimit-Remaining`、`X-Ratelimit-Reset`（unix 时间戳）、`Access-Control-Allow-Origin: *`、`Server: cloudflare`、`cf-cache-status: DYNAMIC`

详情（`GET /books/detail/237429/c45a5a7cd523`，Origin，HTTP 200）：

```json
{"id":237429,"key":"c45a5a7cd523","created_at":...,"category":2,
 "title":"[Rush! (Orio)] B.A.E (Jujutsu Kaisen)","title_short":...,
 "subtitle":"[Rush! (Orio)] B.A.E (呪術廻戦)","subtitle_short":...,
 "thumbnails":..., 
 "tags":[{"namespace":3,"name":"jujutsu kaisen","count":...},
         {"namespace":5,"name":"satoru gojo","count":...},
         {"namespace":7,"name":"Shishigami360","count":...},
         {"namespace":8,"name":"anal","count":...},
         {"namespace":11,"name":"japanese","count":...}]}
```

- 详情响应**不含** `language`/`pages`（搜索响应才有）
- `tags[]` 元素为 `{namespace:int, name:string, count:int}`

#### 4.3.4 🟢 实测：HDoujin 的 namespace 编号表（跨 10 个画廊聚合）

对 10 个真实画廊详情聚合统计：

| namespace | 出现次数 | 样例值 | 🟡 语义推断 |
|---|---|---|---|
| 1 | 6 | `gyoran.com`, `midoriiro`, `fan no hitori`, `akagi rio`, `torimogura` | 社团/团体（group/circle） |
| 2 | 1 | `daidai shikibu` | 作者（artist） |
| 3 | 5 | `jujutsu kaisen`, `original`, `yu-gi-oh` | 原作（parody） |
| 5 | 3 | `satoru gojo`, `suguru geto`, `sky striker ace - raye` | 角色（character） |
| 7 | 10 | `LuciferStarry`, `MotherEmperor`, `quanbuzhineng123`, `qq3870990` | 上传者（uploader） |
| 8 | 17 | `big penis`, `anal`, `muscle`, `yaoi`, `glasses` | **男性**（male） |
| 9 | 90 | `bukkake`, `school swimsuit`, `defloration`, `fingering` | **女性**（female） |
| 10 | 5 | `incest`, `group`, `mmf threesome`, `ffm threesome` | 混合（mixed） |
| 11 | 17 | `chinese`, `translated`, `japanese`, `textless narrative` | 语言（language） |
| 12 | 6 | `multi-work series`, `rough translation`, `full color`, `tankoubon` | 其他（other） |

- **namespace 4 与 6 未观测到**（10 个样本内无）
- namespace 8/9 的语义 🟢 **被插件代码独立印证**：`hdoujin.pm:295-301` 把 9 映射为 `female:`、8 映射为 `male:`，与实测样本完全一致 —— 两路证据交叉验证成立

#### 4.3.5 🔵 两个版本的标签提取差异（重要）

**旧版 `hdoujincn.pm` 只取 namespace 9，且不加前缀**（`:217-228`）：

```perl
# 提取namespace为9的标签
foreach my $tag (@{$jsonresponse->{tags}}) {
    if ($tag->{namespace} == 9) {
        push @namespace9_tags, $tag->{name};     # ← 裸标签，丢失命名空间
    }
}
```

**新版 `hdoujin.pm` 取 8 和 9，且正确加前缀**（`:287-311`）：

```perl
# 提取namespace为8和9的标签
foreach my $tag (@{$jsonresponse->{tags}}) {
    if ($tag->{namespace} == 9) {
        push @extracted_tags, "female:" . $tag->{name};
    } elsif ($tag->{namespace} == 8) {
        push @extracted_tags, "male:" . $tag->{name};
    }
}
```

🟡 **共同缺陷**：两版都**丢弃了 namespace 1/2/3/5/10/11/12**，即社团、作者、原作、角色、语言、其他全部扔掉。理由推测是「这些由 EH 插件提供」，但实测表明 HDoujin 的这些字段质量不差（见上表），**对 Komga 而言不应照搬这个裁剪**。

### 4.4 HentagOnline 系（已失效）🟢

🔵 三个端点（`Plugins/HentagOnline_CN.pm`）：

| 用途 | 端点 | 请求体 |
|---|---|---|
| 按标题 | `POST https://hentag.com/api/v1/search/vault/title`（`:165`） | `{title: $archive_title}`（`:168`） |
| 按 vault id | `POST https://hentag.com/api/v1/search/vault/id`（`:181`） | `{ids: [$vault_id]}`（`:184`） |
| 按 URL | `POST https://hentag.com/api/v1/search/vault/url`（`:197`） | `{urls: \@urls}`（`:199`） |

vault URL 解析：`https?:\/\/(?:www\.)?hentag\.com\/vault\/([a-zA-Z0-9]*)\/?.*`（`:155`）。

**该插件族唯一的「择优」实现**（`:258-270`）值得单独记下：

```perl
sub pick_best_hit ( $title_hint, @hits ) {
    if ( !defined($title_hint) ) { return $hits[0]; }
    $title_hint = lc( LANraragi::Utils::String::clean_title($title_hint) );
    my @titles;
    while ( my ( $index, $elem ) = each @hits ) {
        my ( $tags, $title ) = ...::tags_from_hentag_json($elem);
        $titles[$index] = lc( LANraragi::Utils::String::clean_title($title) );
    }
    return $hits[ LANraragi::Utils::String::most_similar( $title_hint, @titles ) ];
}
```

即「多结果 → 清洗标题 → 相似度取最优」。**这是整个仓库里唯一真正的相似度择优逻辑**，其余插件都是盲取第一条。

🟢 **实测：站点已停运**。`https://hentag.com/` HTTP 200 但内容是告别公告：

> **Hentag | A Final Update & Thank You**
> A quiet conclusion — For a long time, this site has been a labor of love. … I've reached a point where my interests have moved on, and it no longer feels right to maintain a system I'm no longer actively engaged with.
> **Your Works** — Your data has always belonged to you… If you would like an export of your works, please send an email from your registered account address.
> **Alternative Websites** — LANraragi / mihon / ComicRackCE / Stash / raindrop.io

且 `https://hentag.com/api/v1/search/vault/id` → **404**。

> ⚠️ 结论：`HentagOnline.pm`、`HentagOnline_CN.pm`、`Plugins/addhentag.pm` 以及 `SourceBatch.pm` 中引用 `hentagonlineplugin` 的配置**全部失效**。可复用的是 `pick_best_hit` 的算法思路，不是接口。
> 附带价值：停运公告本身把 LANraragi / mihon / ComicRackCE / Stash 列为替代品，可作为选型参考。

### 4.5 Eze_CN.pm（读档案内 `info.json`）🔵

`Plugins/Eze_CN.pm:52-70` 的文件发现策略（**两级**）：

```perl
my $path_in_archive  = is_file_in_archive( $lrr_info->{file_path}, "info.json" );
my $path_nearby_json = $path . $name . '.json';
if ($path_in_archive)   { $filepath = extract_file_from_archive(...); }
elsif ( -e $path_nearby_json ) { $filepath = $path_nearby_json; }   # 同目录同名 .json
else { return ( error => "No in-archive info.json or {archive_name}.json file found!" ); }
```

数据结构（`:115-195`）：

```perl
# Tags are in gallery_info -> tags -> one array per namespace
my $tags = $hash->{"gallery_info"}->{"tags"};
foreach my $namespace ( sort keys %$tags ) {
    my $members = $tags->{$namespace};
    foreach my $tag (@$members) {
        if($namespace eq "tag"){ $return .= $tag; }
        else { $return .= $namespace . ":" . $tag; }
    }
}
my $site   = $hash->{"gallery_info"}->{"source"}->{"site"};
my $gid    = $hash->{"gallery_info"}->{"source"}->{"gid"};
my $gtoken = $hash->{"gallery_info"}->{"source"}->{"token"};
my $language = $hash->{"gallery_info"}->{"language"};
...
$return .= ", source:$site.org/g/$gid/$gtoken";   # :192
```

- 兼容 EHentai 与 nHentai 两种 `info.json`
- 支持**同级目录 sidecar JSON**（`{archive_name}.json`）—— 对 Komga 很实用（不必改写压缩包）
- 描述原文（`:30`）：`从 eze 样式的 info.json 文件 ({'gallery_info': {xxx} } syntax)中收集元数据…这些文件嵌入在您的存档中或具有相同名称的同一文件夹中`

### 4.6 ComicLooms.pm（读档案内 `meta.json`）🔵 — Komga 复用价值最高的一个

`Plugins/ComicLooms.pm` 是为 [MapoMagpie/eh-view-enhance](https://github.com/MapoMagpie/eh-view-enhance) 下载器写的（`:30`），**完全不发网络请求**。

```perl
my $path_in_archive = is_file_in_archive( $lrr_info->{file_path}, "meta.json" );   # :50
if ($path_in_archive) {
    $filepath = extract_file_from_archive( $lrr_info->{file_path}, $path_in_archive );
    $delete_after_parse = 1;      # 解析后 unlink
} else {
    return ( error => "No in-archive meta.json or {archive_name}.json file found!" );
}
```

标签转换（`:105-168`）：

```perl
my $url = $hash->{"url"};
my $is_JM = 0;
if ($url =~ /^(https?:\/\/.*comic.*\/album\/\d+)/) { $url = $1; $is_JM = 1; }   # 禁漫
$return .= "source:" . $url;

my $tags = $hash->{"tags"};
foreach my $namespace ( sort keys %$tags ) {
    my $members = $tags->{$namespace};
    foreach my $tag (@$members) {
        if($is_JM) {                                   # 禁漫：中文标签，无法反向用翻译库
            if($namespace eq "works"){ $return .= "原作:" . $tag; }
            elsif($namespace eq "author"){ $return .= "艺术家:" . $tag; }
            elsif($namespace eq "actor"){ $return .= "角色:" . $tag; }
            elsif($namespace eq "tags"){ $return .= $tag; }
            else { $return .= $namespace . ":" . $tag; }
        } elsif($namespace eq "tag") { $return .= $tag; }
        else { $return .= $namespace . ":" . $tag; }
    }
}
my $title = $hash->{"title"};
if ( $origin_title && $hash->{"originTitle"} ) { $title = $hash->{"originTitle"}; }
```

**对 Komga 的三点价值**：
1. **零网络**：只要档案里有 `meta.json`，就能得到 source + 完整标签，不受站点封锁影响 —— 对当前「EH/NH/Hitomi 直连全断」的环境是最稳的一条路。
2. **禁漫（JM）分支直接产出中文命名空间**（`原作:`/`艺术家:`/`角色:`），说明中文标签不一定非要走 EhTagTranslation 翻译库。
3. `originTitle` 优先于 `title` —— 「保留原始标题」的开关模式（与 EHentai 的 `title_jpn` 开关同构）。

### 4.7 Ehviewer_Color.pm（读 `ComicInfo.xml`）🔵 — 与 Komga 原生能力直接相关

Komga 原生解析 `ComicInfo.xml`，所以这个插件的字段映射**必须**看清。

`Plugins/Ehviewer_Color.pm:52` 从压缩包内取 `ComicInfo.xml`，然后（`:82-117`）：

| ComicInfo 字段 | 插件内部变量 | 输出为 | 行 |
|---|---|---|---|
| `Genre` | `$genre` | 见下方拆分逻辑 | `:82` |
| `Web` | `$url` | `source:` | `:86`, `:166` |
| `Writer` | `$groups` | `group:` | `:90`, `:169` |
| `Penciller` | `$artists` | `artist:` | `:94`, `:170` |
| `LanguageISO` | `$lang` | `language:` | `:98`, `:167` |
| `Series` | `$title` | **标题** | `:102` |
| `Teams` | `$parodies` | `parody:` | `:106`, `:168` |
| `Characters` | `$characters` | `character:` | `:110`, `:171` |
| `AlternateSeries` | `$jpn_title` | 日文标题（可选替换） | `:114`, `:123-125` |

**非标准的字段语义** ⚠️：`Teams` → `parody:`、`Series` → 标题、`Penciller` → artist、`Writer` → group，这些**都不符合 ComicInfo 的常规约定**（通常 `Series` 是系列名、`Writer` 是编剧）。🟡 也就是说，彩E 导出的 CBZ 若直接交给 Komga 原生 ComicInfo 解析，**系列名/作者字段会错位**。

`Genre` 的三段拆分（`:174-184`）值得照搬：

```perl
my @genres = split(',', $genre);
foreach my $genre_tag (@genres) {
    $genre_tag = trim($genre_tag);
    if ($genre_tag =~ /^m:/)      { push(@found_tags, "male:"   . substr($genre_tag, 2)); }
    elsif ($genre_tag =~ /^f:/)   { push(@found_tags, "female:" . substr($genre_tag, 2)); }
    else                          { push(@found_tags, "other:"  . $genre_tag); }
}
```

ISO 语言码映射表（`:143-159`）：`ja→japanese, en→english, zh→chinese, nl→dutch, fr→french, de→german, hu→hungarian, it→italian, ko→korean, pl→polish, pt→portuguese, ru→russian, es→spanish, th→thai, vi→vietnamese` —— 可直接搬到 Komga。

另有 `only_title` / `only_source` 两个开关（`:129-140`），可只取标题或只取 source。

### 4.8 Ehviewer_Green.pm（读 `.ehviewer`）🔵

`Plugins/Ehviewer_Green.pm`：从压缩包内 `.ehviewer` 文件**第 3、4 行**读 gID 与 token（`:70`,`:88`），拼出 EH 链接后走 §3 的链路。优先级（`:61-81`）：

1. 已有 `source:e(x|-)hentai.org/g/(\d+)/([0-9a-z]+)`（`:62`）
2. 压缩包内 `.ehviewer` 的 gid/token（`:70`）

输出 `source:$host/g/$gallery_id/$gallery_token`（`:130`）。

🟡 这是「**从下载器留下的痕迹文件反推 gID，再走权威 API**」的模式 —— 比标题搜索可靠得多。对 Komga 很有参考价值。

### 4.9 HatH_CN.pm（读 `galleryinfo.txt`）🔵

`Plugins/HatH_CN.pm:52-84`：从压缩包内取 `galleryinfo.txt`（HentaiAtHome Downloader 产物），逐行正则解析：

```perl
while ( my $line = <$fh> ) {
    if ( $line =~ m/Title: (.*)/       && $use_jpn_title ) { ... }   # :66
    if ( $line =~ m/Uploaded By: (.*)/ && $add_uploader  ) { ... }   # :70
    if ( $line =~ m/Upload Time: (.*)/ && $add_uploadtime) { ... }   # :74
    if ( $line =~ m/Tags: (.*)/ ) {                                   # :78
        ...
        return ( tags => $cntag, title => $title );
    }
}
return ( error => "在galleryinfo.txt中找不到tag!" );
```

`Tags:` 行本身已是 `namespace:value, ...` 格式，按 `,` split 后逐条翻译（`:94`）。翻译用 `$element->{'namespace'} eq $namespace`（`:111`）—— **精确匹配，不支持别名**（见 §5.2 对比）。

### 4.10 三个 `add*` 批量补全脚本 🔵

三者是同一模式的三个实例（分别调用 `ehplugin` / `etagcn` / `hentagonlineplugin`）：

| 文件 | 调用插件 | 跳过条件 | 失败标记 | 重试机制 | 间隔 |
|---|---|---|---|---|---|
| `Plugins/addEhentaiMetadata.pm` | `use_plugin("ehplugin", …)`（`:58`） | `next if $old_tags =~ /\bsource\b/`（`:49`） | `source:nogalleryinehentai`（`:66`） | `oneshot_param eq "True"` 时重扫（`:89-128`） | `sleep(10)` ×3 处（`:68`,`:81`,`:87`）+ `$timeout` |
| `Plugins/addEtagCNMetadata.pm` | `use_plugin("etagcn", …, $db_path)`（`:59`） | 同（`:51`） | 同（`:78`） | 同 | `sleep($timeout)`（`:68`） |
| `Plugins/addhentag.pm` | `use_plugin("hentagonlineplugin", …)`（`:58`） | 同（`:49`） | 同（`:66`） | 同 | `sleep(5)`/`sleep(4)`（`:80`,`:83`） |

**核心设计模式**（可复用）：
- 用**哨兵标签** `source:nogalleryinehentai` 把「查过但没找到」持久化到库里，从而**只重试失败项**，不必每次全库扫描
- 重试时用正则替换哨兵：`$old_tags =~ s/\bsource:nogalleryinehentai\b/$ehentai_tags->{new_tags}/g`（`addEhentaiMetadata.pm:117`）
- 先跳过已有 `source` 的档案（省请求）

**缺陷** ⚠️：
- `addEhentaiMetadata.pm:65` 比较 `$ehentai_tags->{error} eq "No matching EH Gallery Found!"`，但 Putarku 版 EHentai 插件的错误文案已改成中文「未找到匹配的 EH 画廊！」（`EHentai.pm:98`）→ 🟡 **字符串比较永远不成立，哨兵标记逻辑失效**。这是中文化改造引入的真实回归。
- `sleep(10)` 硬编码 3 处，批量场景下极慢（README 称 10 分钟量级，`README.md:19`）

### 4.11 上游官方插件（`chu-shen/LANraragi` fork，供对照）

🟢 该 fork 的 `feat-ratingAndcomment` 分支含全部上游官方插件，可作对照基线：

```
lib/LANraragi/Plugin/Metadata/EHentai.pm (12219B)   Hitomi.pm (12345B)   nHentai.pm (7037B)
lib/LANraragi/Plugin/Metadata/HatH.pm (4082B)       Hentag.pm (6189B)    Eze.pm (7442B)
lib/LANraragi/Plugin/Metadata/HDoujin.pm (8853B)    RegexParse.pm (9339B)  Pixiv.pm (15954B)
lib/LANraragi/Plugin/Metadata/Chaika.pm  ChaikaFile.pm  ComicInfo.pm  CopyArchiveTags.pm  CopyTags.pm
lib/LANraragi/Plugin/Metadata/DateAdded.pm  EHDLInfo.pm  Fakku.pm  GalleryDL.pm  Koromo.pm
lib/LANraragi/Plugin/Metadata/Ksk.pm  MEMS.pm  Comment.pm  Rating.pm  TranslateTitleByAI.pm
lib/LANraragi/Plugin/Scripts/addEhentaiMetadata.pm  DuplicateFinder.pm  FolderToCat.pm
lib/LANraragi/Plugin/Scripts/nHentaiSourceConverter.pm  SourceFinder.pm
lib/LANraragi/Utils/Plugins.pm  lib/LANraragi/Utils/String.pm  lib/LANraragi/Utils/Tags.pm
tools/Documentation/plugin-docs/{index,metadata,scripts,download,login,code-examples}.md
tests/LANraragi/Plugin/Metadata/*.t   ← 含官方 mock 响应
```

- `chu-shen/LANraragi-scripts/plugins/Scripts/addEhentaiMetadata.pm` 与 fork 内同名文件**内容一致**（逐行比对 `use_plugin`/`sleep($interval)`/哨兵逻辑相同）🟢 —— 即 chu-shen 的版本用 `$interval` 变量而非硬编码 `sleep(10)`，比 Putarku 版好。
- 值得注意的上游插件：`RegexParse.pm`（§6）、`CopyArchiveTags.pm` / `CopyTags.pm`（复制标签，与 Putarku 的 `CopyTagsFromFilename.pm` 同族）、`DuplicateFinder.pm`、`nHentaiSourceConverter.pm`（把 nHentai source 转成 EH source）。
- `chu-shen` 另加了 `Comment.pm`（1145B）与 `Rating.pm`（1141B），配合 fork 名 `feat-ratingAndcomment` —— 即该 fork 给 LRR 加了**评分与评论**字段。Komga 无对应原生概念，🟡 复用价值低，但说明社区有「给阅读器补评分/评论」的诉求。
- `TranslateTitleByAI.pm`（3442B）：`POST {url}` 到 OpenAI 兼容 `/v1/chat/completions`，头 `Authorization: Bearer $api_key`，body `{model, messages:[{role:"user",content:"$prompt: $title"}], temperature, stream:false, thinking:{type}}`，取 `choices[0].message.content` 清洗后写成 `$tag_name:$result` 标签（`:60-91`）。🟡 对 Komga 有参考意义：可用 LLM 生成中文标题/简介。

---

## 5. 中文标签翻译链路（本文最重要的一节）

### 5.1 数据源与真实结构 🟢

数据源是 [EhTagTranslation/Database](https://github.com/EhTagTranslation/Database/releases) 的 `db.text.json`（各仓库内自带一份快照；`chu-shen/LANraragi-scripts` 里那份 5.97MB，`ETagCN` 里 4.70MB）。

🟢 **实测解析结果**（`zhy201810576/ETagCN/db.text.json`，4.70MB）：

```
top-level keys: ['repo', 'head', 'version', 'data']
head: {"sha":"fff956f0...","message":"添加 artist:fuwatoro - ふわとろ| 原始标签 | 名称 | 描述 | 外部链接 |...",
       "author":{"name":"Msopopop",...},"committer":{"name":"ehtagtranslation[bot]",...},
       "when":"2025-11-02T06:29:37.000Z"}
data: list, len = 13
```

**13 个命名空间全表** 🟢：

| `namespace` | `frontMatters.name` | `frontMatters.key` | `aliases` | 行数 |
|---|---|---|---|---|
| `rows` | 内容索引 | rows | — | 13 |
| `reclass` | 类别 | reclass | — | 11 |
| `language` | 语言 | language | `["lang"]` | 87 |
| `parody` | 原作 | parody | `["series"]` | 2499 |
| `character` | 角色 | character | `["char"]` | 6415 |
| `group` | 团队 | group | `["creator","circle"]` | 13315 |
| `artist` | 艺术家 | artist | — | 14125 |
| `cosplayer` | Coser | cosplayer | — | 146 |
| `male` | 男性 | male | — | 571 |
| `female` | 女性 | female | — | 609 |
| `mixed` | 混合 | mixed | — | 23 |
| `other` | 其他 | other | — | 60 |
| `location` | 地点 | location | — | 6 |

合计约 **41,000** 条标签翻译。

**行数据结构** 🟢（每个 `element`）：

```json
{
  "namespace": "female",
  "frontMatters": {"name":"女性","description":"女性角色相关的恋物标签。","key":"female"},
  "data": {
    "age progression": {"name":"年龄增长","intro":"年龄迅速增长。","links":""},
    "age regression":  {"name":"返老还童","intro":"幼化，返老还童，通常是调教中的一环。…","links":""},
    "infantilism":     {"name":"幼稚型","intro":"将成年人当作小孩对待。","links":""}
  }
}
```

即：**`frontMatters.name` = 命名空间的中文名；`data[<原始标签>].name` = 标签值的中文名**。翻译一个标签 = 两段替换。

`rows` 命名空间较特殊：它的 `data` 把命名空间键映射到中文名（`{"female":{"name":"女性",…},"male":{"name":"男性",…},"mixed":{"name":"混合",…}}`），是「命名空间索引」本身。

`language` 命名空间实例：`chinese→汉语`、`japanese→日语`、`translated→翻译`。

### 5.2 三套翻译实现的差异 🔵

| 实现 | 文件 | 命名空间匹配方式 | 特征 |
|---|---|---|---|
| **ETagCN**（原始） | `zhy201810576/ETagCN/ETagCN.pm:330` | `if ($element->{'namespace'} eq $namespace)` — **精确匹配 `namespace` 字段** | 最干净；`push(@tags, "reclass:$ehcat")`（`:266`）直接产 reclass；翻译函数接收**数组** `\@tags`（`:314`） |
| **EHentai_CN**（Putarku 改） | `Plugins/EHentai_CN.pm:358` | `my @namespace_aliases = ($element->{'frontMatters'}{'key'}, @{$element->{'frontMatters'}{'aliases'} \|\| []});` 再 `grep { $_ eq $namespace }` — **支持别名** | 比 ETagCN 多支持 `lang→language`、`series→parody`、`circle→group` 等别名 |
| **HatH_CN** | `starP-W/HAtH-Plugin-CN/HatH_CN.pm:111` | `$element->{'namespace'} eq $namespace` — 精确匹配 | 同 ETagCN，不支持别名 |

🟡 **结论**：Putarku 的别名版更健壮，**应照搬别名逻辑**（否则 `series:`/`lang:`/`circle:`/`char:` 这类别名标签会漏翻译）。

**各处的调用点**（`translate_tag_to_cn` 在 7 个文件里各有一份**复制粘贴**的实现）：

```
Plugins/EHentai_CN.pm:330        Plugins/HentagOnline_CN.pm:273
Plugins/Hitomi_CN.pm:243         Plugins/nHentai_CN.pm:232
Plugins/Eze_CN.pm:204            Plugins/ComicLooms.pm:171
Plugins/Ehviewer_Color.pm:217    hdoujin.pm:330
```

🟡 这是明显的重复代码，移植到 Komga 时应**抽成一个模块**。

### 5.3 `category:` → `reclass:` 的命名不一致（三重证据）

**问题**：EH API 返回的 `category` 值（`"Doujinshi"`、`"Artist CG"`、`"Non-H"`…）经 `lc` 后，与 EhTagTranslation 的 `reclass` 键名**不完全一致**。

🟢 **证据 1 — 真实 API 响应**：`"category":"Non-H"`（§3.4 实测）。

🟢 **证据 2 — 数据库真实键名**：

```
reclass keys: ["doujinshi","manga","artistcg","gamecg","non-h","imageset","western","cosplay","misc","asianporn","private"]
  artistcg -> "画师CG"
  gamecg   -> "游戏CG"
  imageset -> "图集"
```

🟢 **证据 3 — chu-shen 的 Python 脚本显式打补丁**（`chu-shen/LANraragi-scripts/getEhTagTranslationForLANraragi.py:15-22`）：

```python
if namespace == 'reclass':
    namespace = 'category'
    if tag_name=='artistcg':
        tag_name = 'artist cg'
    if tag_name=='gamecg':
        tag_name = 'game cg'
    if tag_name=='imageset':
        tag_name = 'image set'
```

即：EH API 用**带空格**的 `artist cg` / `game cg` / `image set`，而数据库用**不带空格**的 `artistcg` / `gamecg` / `imageset`。

⚠️ **Putarku 的 Perl 插件没有做这个转换**：`EHentai_CN.pm:344` 只做 `s/category:/reclass:/g`，然后 `if (exists $data->{$key})`（`:368`）用 `artist cg` 去查 `artistcg` → **查不到，该标签不被翻译**。🟡 这是一个可复现的真实 bug，移植时必须显式处理这 3 个类别。

### 5.4 已知缺陷清单（照搬会踩的坑）⚠️

| # | 缺陷 | 证据 | 影响 |
|---|---|---|---|
| 1 | **翻译库打开失败时崩溃**：`open(...) or $logger->debug(...)` 后直接 `<$json_fh>`，`$json_fh` 为 undef | `EHentai_CN.pm:334-339`、`ETagConverter.pm:50-55`、`HentagOnline_CN.pm:277-282`、`Hitomi_CN.pm:247-252`、`Eze_CN.pm:208-213`、`ComicLooms.pm:175-180`、`Ehviewer_Color.pm`、`hdoujin.pm:334-339` | 路径配错即 die。**只有 `nHentai_CN.pm:236-255` 做了正确防护**（见下） |
| 2 | **未锚定的正则替换**：`$item =~ s/$namespace/$name/` 与 `$item =~ s/$key/$value/` 没有 `\b`/`^` 锚定，也没有 `quotemeta` | `EHentai_CN.pm:364,370`（全仓库 8 处同构） | 若 key 含正则元字符（标签名里的 `(`、`)`、`+`）或短词出现在值中间，会误替换 |
| 3 | **`category` 类别名不匹配** | §5.3 | `artist cg`/`game cg`/`image set` 三类不翻译 |
| 4 | **`split(/,\s*/, $tags_str)` 简单切分** | `EHentai_CN.pm:347` | 标签值本身含逗号时会被切断（EH 标签一般不含，风险中） |
| 5 | **每个标签都遍历全部 13 个命名空间** | `EHentai_CN.pm:356-374` | O(标签数 × 13)，小库无感，全库批量（ETagConverter）时偏慢 |
| 6 | **`ETagConverter.pm` 的全局替换过宽**：`$tags =~ s/category/reclass/g`（**无冒号**）与 `$tags =~ s/Chinese/汉语/g` | `Plugins/ETagConverter.pm:66-67` | 会误伤任何含 `category`/`Chinese` 子串的标签值；且直接改写 Redis 全库，插件自身描述警告「使用前请一定要备份数据库」（`:22`） |

**`nHentai_CN.pm` 的正确写法**（唯一值得照搬的健壮实现，`:236-255`）：

```perl
my $json_text;
{
    open( my $json_fh, "<", $filename )
      or do {
        $logger->error("无法打开翻译数据库 $filename: $!");
        return $tags_str;              # ← 返回原文，不崩
      };
    local $/;
    $json_text = <$json_fh>;
}
if ( !defined $json_text || $json_text eq '' ) {
    $logger->error("翻译数据库为空: $filename");
    return $tags_str;
}
my $json = decode_json($json_text);
if ( !$json || !exists $json->{'data'} ) {
    $logger->error("翻译数据库 JSON 无效或缺少 data 字段: $filename");
    return $tags_str;
}
```

### 5.5 全库批量转换与备份方案 🔵

- `Plugins/ETagConverter.pm:36-121`：扫 Redis 里所有 40 位十六进制 key（`:44` `$redis->keys('????????????????????????????????????????')`），逐条 `hgetall` → 翻译 → `hset`（`:114`）→ `invalidate_cache()`（`:117`）。返回 `( modified => $count, message => ... )`。
- `chu-shen/LANraragi-scripts/getEhTagTranslationForLANraragi.py`：**离线方案**，不碰 Redis，而是
  1. 把 `db.text.json` 展平成 `namespace:tag -> 中文命名空间:中文标签` 的 `tags.txt`（`:12-29`）
  2. 读 LRR 导出的 `backup.json`，对每个 archive 的 `tags` 字符串按 `,` split 逐条查表替换（`:44-53`）
  3. 输出 `backup_chs.json`，在 LRR 里恢复（`:57-59`）
- 另有 `getLatestEhTagTranslationForLANraragi.py`（2906B）与 `sync_plugins.sh`（616B）+ GitHub Actions `sync.yml`，说明 chu-shen 用 CI 定期同步翻译库。

🟡 **对 Komga 的启示**：「翻译是纯函数，输入 = 标签字符串 + 数据库，输出 = 标签字符串」—— 完全可以做成 Komga 侧的离线批处理（读导出 → 转换 → 写回），比侵入式改 Redis 安全得多。ETagConverter 的 `s/Chinese/汉语/g` 这类全局替换是反面教材。

---

## 6. 文件名解析与匹配算法（Komga 复用价值极高）

### 6.1 同人志命名规范正则 🔵

`lib/LANraragi/Plugin/Metadata/RegexParse.pm:78` 的默认正则（上游官方插件，Putarku 未改）：

```regex
(\((?<event>[^([]+)\))?\s*(\[(?<artist>[^]]+)\])?\s*(?<title>[^([]+)\s*(\((?<series>[^([)]+)\))?\s*(\[(?<language>[^]]+)\])?(?<tail>.*)?
```

对应规范（`RegexParse.pm:40`）：**`(Event) [Artist] TITLE (Series) [Language]`**

| 捕获组 | 含义 | 输出 |
|---|---|---|
| `event` | `(C100)` 里的内容 | `event:` 前缀（`:167-169`） |
| `artist` | `[作者]` | 见 §6.2 |
| `title` | 标题（**必需**） | 设为档案标题（`:111`） |
| `series` | `(系列)` | `series:` 前缀 |
| `language` | `[语言]` | `language:` 前缀 |
| `tail` | 剩余部分 | 二次解析 |

### 6.2 circle/artist 拆分（最精巧的一段，强烈建议照搬）🔵

`RegexParse.pm:185-200`：

```perl
sub parse_artist_value {
    my ($artist) = @_;
    my @tags;
    #Special case for circle/artist sets:
    #If the string contains parenthesis, what's inside those is the artist name
    #the rest is the circle.
    if ( $artist =~ /(.*) \((.*)\)/ ) {
        push @tags, "group:" . trim($1);       # 括号外 = 社团
        $artist = trim($2);                    # 括号内 = 作者
    }
    push @tags, parse_captured_value_for_namespace( $artist, 'artist:' );
    return @tags;
}
```

即把 `[Rush! (Orio)]` 正确拆成 `group:Rush!` + `artist:Orio`。🟢 这与实测 HDoujin 数据中 `ns=1`（社团）与 `ns=2`（作者）分离的设计一致。

### 6.3 通用命名空间机制 🔵

`RegexParse.pm:152-183` 动态遍历所有命名捕获组，**组名即命名空间**：

```perl
for my $name (keys %captures) {
    next if $name eq 'title' || $name eq 'tail';
    my $value = trim($captures{$name});
    next unless $value;
    my $namespace = $name =~ s/\d+$//r;      # artist2 -> artist
    if    ($namespace eq 'tag')   { push @tags, map { trim($_) } split(/,/, $value); }  # 裸标签
    elsif ($namespace eq 'artist'){ push @tags, parse_artist_value($value); }
    elsif ($namespace eq 'event') { push @tags, "event:$value"; }
    else                          { push @tags, parse_captured_value_for_namespace($value, "$namespace:"); }
}
```

- **`(?<任意名>...)` → `任意名:` 前缀**，用户可自定义正则扩展命名空间
- 数字后缀支持同类多组：`artist2`、`tag3`（`:56`,`:160`）
- `tag` 组产出**裸标签**（无命名空间）
- 尾部花括号 `{...}` 可当标签列表（`:137-141`），`check_trailing_tags` 开关
- 括号内容可归入 `parsed:` 命名空间供后续 Tag Rules 清理（`:144-147`,`:175-180`）

### 6.4 过滤常见噪音值 🔵

`RegexParse.pm:18-27` + `:207-216`：

```perl
my %COMMON_EXTRANEOUS_VALUES = (
    'uncensored'=>1,'decensored'=>1,'ongoing'=>1,'pixiv'=>1,
    'twitter'=>1,'fanbox'=>1,'cosplay'=>1,'digital'=>1
);
sub _classify_item {
    my ( $item, $namespace ) = @_;
    if ( $namespace && $COMMON_EXTRANEOUS_VALUES{ lc $item } || looks_like_number($item) ) {
        return $PLUGIN_TAG_NS . $item;     # 降级为 parsed: 前缀，便于后续清理
    }
    return "${namespace}${item}";
}
```

🟡 思路可复用：**不直接丢弃噪音，而是降级命名空间**，把「丢不丢」的决定权交给下游规则。

### 6.5 标题清洗与相似度算法 🔵

`lib/LANraragi/Utils/String.pm`：

```perl
# :14-28
# Remove "junk" from titles, turning something like
# "(c12) [poop (butt)] hardcore handholding [monogolian] [recensored]" into "hardcore handholding"
sub clean_title ($title) {
    $title = trim($title);
    $title =~ s/^\([^)]*\)?\s?//g;     # 去掉开头 (c12)
    $title =~ s/^\[[^]]*\]?\s?//g;     # 去掉开头 [poop (butt)]
    $title =~ s/\s?\[[^]]*\]$//g;      # 去掉结尾 [mongolian]
    $title =~ s/\s?\[[^]]*\]$//g;      # 再去一次 [recensored]
    return $title;
}

# :69-85
sub most_similar ( $tested_string, @values ) {
    if ( !@values ) { return; }
    my $best_similarity = 0.0;
    my $best_index      = undef;
    while ( my ( $index, $elem ) = each @values ) {
        my $similarity = similarity( $tested_string, $elem );   # String::Similarity
        if ( !defined($best_index) || $similarity > $best_similarity ) {
            $best_similarity = $similarity;
            $best_index      = $index;
        }
    }
    return $best_index;
}
```

- `clean_title` 只剥离**首尾**的 `(...)`/`[...]`，中间内容保留
- `similarity` 来自 CPAN `String::Similarity`（🟡 算法本质是模糊字符串匹配，非编辑距离）；**平局取第一个**
- 🟡 注意 `clean_title` 的 `s/^\([^)]*\)?\s?//g` 对嵌套括号 `[poop (butt)]` 的首括号处理依赖 `\[[^]]*\]` 那条，实际能正确剥离

### 6.6 从文件名提取 gID 的两种模式 🔵

| 模式 | 正则 | 位置 |
|---|---|---|
| EH/绿E：数字开头 + 横杠 | `^(\d+)-(.+)` → 用 `gid:{n}` 搜，标题改为横杠后部分 | `EHentai.pm:145-147`,`:151` |
| nHentai：花括号 | `\{(\d*)\}.*$` | `nHentai_CN.pm:138` |

Putarku 对 EH 的改造说明（`Plugins/Readme.md:9-10`）：
> 修改2：其中利用GID搜索修改为匹配 Ehviewer 下载以数字开头横杠结束的 GID 格式的文件，例如 `123-(C100)ABC.zip`
> 修改3：文件名搜索，会过滤掉上一步匹配到的 GID，如 `123-(C100)ABC.zip` 最终会用 `(C100)ABC` 来搜索

### 6.7 档案名归一化去重（`CopyTagsFromFilename.pm`）🔵

`Plugins/CopyTagsFromFilename.pm` 的用途（`:21`）：**「使用当前画廊的名字在库中检索，并将其 tag 复制到当前画廊中，适用于更优质的版本替换的时候用」** —— 即「换更好版本时继承旧标签」。

`cleanup_archive_name`（`:74-93`）是本次见到最完整的噪音剥离规则：

```perl
$name = lc $name;
# 剥掉 [4K] [v2] [ver1.2] [rev3] [fixed] [updated] [repack] [rescanned] [complete]
#       [digital] [uncensored] [censored] [english] [japanese] [chinese] [translated] [ai upscaled]
$name =~ s/\[(?:\d+(?:p|x\d+)|v\d+(?:\.\d+)?|ver\s*\d+(?:\.\d+)?|rev\s*\d+|fix(?:ed)?|updated?|repack|rescanned?|complete|digital|uncensored|censored|english|japanese|chinese|translated?|ai[ -]?upscaled?)\]/ /gi;
$name =~ s/\((?:\d+(?:p|x\d+)|...)\)/ /gi;        # 同样一套，圆括号版
$name =~ s/\b\d{3,4}p\b/ /gi;                      # 720p/1080p
$name =~ s/\b\d+\s*(?:pages?|p)\b/ /gi;            # 24 pages / 24p
$name =~ s/\b(?:v|ver|rev)\s*\d+(?:\.\d+)?\b/ /gi; # v2 / ver 1.2 / rev3
$name =~ s/\b(?:fix(?:ed)?|updated?|repack|rescanned?|complete|digital|uncensored|censored|english|japanese|chinese|translated?|raw)\b/ /gi;
$name =~ s/[\[\](){}]/ /g;                         # 去掉所有括号
$name =~ s/[._-]+/ /g;                             # 分隔符归一为空格
$name =~ s/\s+/ /g;
$name =~ s/^\s+|\s+$//g;
```

匹配与合并逻辑（`:42-71`）：遍历 Redis 全部 key → 清洗候选名 → **要求清洗后完全相等**（`:55` `$clean_candidate_name ne $clean_archive_name` 则跳过）→ 收集标签、**用 `%seen_tags` 去重**（`:60`）、**排除 `date_added:`**（`:59`）、排除自身（`:48`）。

🟡 注意：这是**精确相等匹配**（不是相似度），所以 `cleanup_archive_name` 的覆盖度直接决定命中率。

---

## 7. 批量编排架构（`SourceBatch.pm` — 本次最值得借鉴的架构）

`Plugins/SourceBatch.pm`（v1.0.3，370 行）把「补 source」抽象成一个**可配置的瀑布式编排器**，是仓库里工程化程度最高的一个。

### 7.1 配置面 🔵（`:28-60`）

| 参数 | 类型 | 默认 | 作用 |
|---|---|---|---|
| `plugin_names` | string | `""` | 要**依次**运行的元数据插件 namespace，逗号分隔（例：`nhplugin_cn,nhentaiv2`） |
| `rounds` | string | `"1"` | 对每个仍缺 source 的档案，把插件列表**完整跑几轮** |
| `run_metadata_plugins` | bool | `1` | 关掉则只做 https 规范化 |
| `stop_when_source_found` | bool | `1` | 某插件写入 source 后，**同轮内不再跑后续插件**（省请求） |
| `normalize_https` | bool | `1` | 给 `source:` 补全协议 |
| `upgrade_http_to_https` | bool | `1` | `http://` → `https://` |

另有 `oneshot_arg`：只处理指定档案 ID（`:27`）。

### 7.2 核心循环 🔵（`:249-304`）

```perl
for my $id (@ids) {
    my $file = _local_get_archive_path( $redis, $id );
    unless ( defined $file && $file ne '' && -e $file ) { next; }   # 跳过文件不存在的

    my %h = $redis->hgetall($id);
    my $tags = LANraragi::Utils::Redis::redis_decode( $h{tags} // '' );
    $report{metadata_archives_scanned}++;
    next if archive_tags_have_source($tags);                        # 已有 source → 跳过

    for my $r ( 1 .. $rounds ) {
        %h = $redis->hgetall($id);                                  # 每轮重读
        $tags = LANraragi::Utils::Redis::redis_decode( $h{tags} // '' );
        last if archive_tags_have_source($tags);                     # 已拿到 → 退出轮次

        for my $ns (@namespaces) {
            my $plugin = LANraragi::Utils::Plugins::get_plugin($ns);
            unless ($plugin) { ...; next; }
            my %pluginfo = $plugin->plugin_info();
            unless ( ( $pluginfo{type} // '' ) eq 'metadata' ) { ...; next; }   # 类型校验
            my %settings = LANraragi::Utils::Plugins::get_plugin_parameters($ns);
            my %plugin_result = LANraragi::Model::Plugins::exec_metadata_plugin( $plugin, $id, %settings );
            $report{metadata_plugins_run}++;
            my ( $ok, $err ) = apply_metadata_plugin_result( $id, \%plugin_result );
            ...
            %h = $redis->hgetall($id);                              # 每插件后重读
            $tags = LANraragi::Utils::Redis::redis_decode( $h{tags} // '' );
            if ( $stop_on_source && archive_tags_have_source($tags) ) { last; }
        }
        last if archive_tags_have_source($tags);
    }
}
```

**值得照搬的四点**：
1. **瀑布 + 短路**：插件按序尝试，一旦拿到 source 立即停（`stop_when_source_found`）
2. **每步重读状态**：每个插件执行后重新 `hgetall`，因为插件可能改写了库
3. **可重入的多轮**：`rounds` 让「第一次没中、第二次可能中」的场景有解
4. **结构化报告**：`%report` 含 `archives_total / metadata_archives_scanned / metadata_plugins_run / metadata_errors / https_archives_changed / https_unchanged`（`:238-245`），最后 sprintf 成一行摘要（`:352-360`）

### 7.3 source 规范化 🔵（`:76-98`）

```perl
sub normalize_source_value {
    my ( $value, $upgrade_http ) = @_;
    $value = LANraragi::Utils::String::trim($value);
    return $value if $value eq '';
    if ( $value =~ /^https:\/\//i ) { return $value; }
    if ( $value =~ /^http:\/\//i )  { $value =~ s/^http:/https:/i if $upgrade_http; return $value; }
    if ( $value =~ /^\/\// )        { return 'https:' . $value; }        # 协议相对 URL
    if ( $value =~ /^(?:www\.)?[a-zA-Z0-9][a-zA-Z0-9.-]*\.[a-zA-Z]{2,}/ ) { return 'https://' . $value; }  # 裸域名
    return $value;
}
```

🟡 这套「协议补全」对 Komga 的价值在于**去重**：`e-hentai.org/g/1/x`、`http://e-hentai.org/...`、`//e-hentai.org/...` 归一后才能判重。

### 7.4 并发/锁处理 🔵（`:128-137`）

```perl
# 旧版 LANraragi 的 Generic.pm 可能没有 exec_with_lock_pure；与侧载脚本一致时直接写库。
sub _exec_with_archive_lock {
    my ( $id, $code ) = @_;
    my $generic = 'LANraragi::Utils::Generic';
    if ( $generic->can('exec_with_lock_pure') ) {
        return LANraragi::Utils::Generic::exec_with_lock_pure( ["archive-write:$id"], $code );
    }
    my $response = eval { $code->(); };
    die $@ if $@;
    return ( 1, $response );
}
```

以及顶部的兼容性注释（`:8-11`）：
> 上传时 LRR 会 require 本文件并立刻调用 plugin_info()。顶层不要 use LANraragi::*：否则 require 阶段会拉一整条依赖链，易失败。也不要顶层 use LANraragi::Utils::Plugins（会与 Module::Pluggable 循环加载）。所有 LRR 依赖在 run_script 开头 require

🟡 这是 LRR 插件生态的**已知坑**（延迟 `require` 避免循环依赖），对 Komga 无直接意义，但说明「按需加载」在插件热插拔场景的重要性。

---

## 8. 前端/周边脚本资产

### 8.1 `ExHentai_Lanraragi_Checker.js`（23.8KB，用户脚本）🔵

- 元数据：`@match https://exhentai.org/*` 与 `https://e-hentai.org/*`、`@grant GM_xmlhttpRequest`、`@grant GM_addStyle`、`@version 1.8`、`@namespace https://github.com/Putarku`
- 功能（`@description`）：`Checks if galleries on ExHentai/E-Hentai are already in your Lanraragi library (urlfinder + /api/search fallback) and marks them by inserting a span at the beginning of the title.`
- **两级判定**（这正是 README 里 `LRR ✔` 绿标 / `LRR！` 紫标的来源，`README.md:13`）：
  1. 首选 `LRR_SERVER_URL + '/api/plugins/use?plugin=urlfinder&arg=' + encodeURIComponent(galleryUrl)`（`:376`），头 `Authorization`（`:379`）
  2. 回退 `GET /api/search?filter=...`（`:415`），同样带 `Authorization`（`:419`）
- **工程细节**（值得借鉴）：
  - `localStorage` 缓存 + `MAX_CACHE_ITEMS = 2000` 上限（`:61`）+ 过期清理 + 定期全量清理（`:210-275`）
  - 相同 `filter` 的并发请求**自动合并为一次**（`:25` 注释）
  - 并发池限流（`:480`）
  - `/api/search` 返回 **204** 视为「搜索索引未就绪」，2 秒后重试一次（`:433-447`）

🟡 对 Komga 的映射：Komga 没有 `urlfinder` 这类插件端点，但可以用 `GET /api/v1/series?search=` 或 `GET /api/v1/books?search=` 做同样的「URL → 是否已入库」判定；204 那种「索引未就绪」的语义 Komga 也有（扫描中）。

### 8.2 `Difegue/Tsukihi`（浏览器扩展）🔵

| 动作 | 端点 | 方法 | 头 |
|---|---|---|---|
| 检查当前页是否已入库 | `/api/plugins/use?plugin=urlfinder&arg={tab.url}`（`background.js:115`） | **POST** | `Authorization: Bearer ` + `btoa(apiKey)`（`:251`） |
| 让服务端下载当前页 | `/api/download_url`（`:211`） | POST，`formData.append('url', tab.url)`（`:205`） | 同上 |
| 轮询任务 | `/api/minion/{job}/detail`（`:278`） | GET | 同上 |

🟡 与 Checker.js 对比：Tsukihi 用 **POST**，Checker.js 用 **GET** —— 说明 LRR 的 `plugin/use` 端点两者都接受。`Authorization` 值是 **base64 编码的 API key**（`btoa(apiKey)`）。

🟡 对 Komga 的启示：「浏览器看到的本子 → 一键推给服务端」是真实需求，Komga 侧可用 `POST /api/v1/libraries/{id}/scan` 或外部下载器 + 扫描触发来对应。

### 8.3 `ApplyCopyrightTags.py` + `search_keywords.txt` 🔵

用途（`README.md:16-19`）：
> 在 LANraragi 中标记版权作者的所有画廊，由于没有确切的版权本数据来源，因此脚本会将存在版权作者的所有画廊均打上 `其他:版权本` 标记
> 使用前提：LANraragi 的画廊元数据中包含 `艺术家:xx` 或 `群组:xx` 的标签

流程（`ApplyCopyrightTags.py`）：
1. `POST {BASE}/login`，`data={"password": ADMIN_PASSWORD}`，从 `response.cookies.get_dict()` 取会话（`:18-28`）
2. 逐行读 `search_keywords.txt`（`:113-114`）
3. `GET {BASE}/api/search?filter={keyword}`，从 `data[].arcid` 取 ID（`:43-58`）
4. `GET {BASE}/api/archives/{id}/metadata` 取现有 tags（`:77-82`）
5. 已含则跳过（`:87-89`），否则 `PUT {BASE}/api/archives/{id}/metadata`，`data={"tags": updated_tags}`（`:96-101`）

`search_keywords.txt` 🟢 实测：**714 行**，UTF-8，每行一个 `命名空间:值` 形式的 LRR 搜索过滤器：

```
艺术家:愛上陸
艺术家:みつや
艺术家:あお色一号
...
群组:Fatalpulse
群组:oilan
群组:Soft Thigh
...
```

🟡 对 Komga 的复用价值：这是**「用一份人工维护的商业作者/社团清单做批量打标」**的模式。Komga 侧可用同思路做「商业志 vs 同人志」自动分类或加 `commercial` 标签/集合。但注意它是「版权作者的所有作品一律标记」的**粗粒度**做法（README 自己承认「没有确切的版权本数据来源」）。

### 8.4 三个油猴脚本（未细读）🔵

`Plugins/Readme.md:42-44` 说明：
- `油猴Ehentai进入详情页自动打开收藏选择.js` — 「顾名思义」
- `油猴Ehentai用绿E下载数据高亮已下载的本.js` — 「在 EH 右上角添加绿E数据库导入框，用来高亮已下载的本」
- `油猴Lanraragi编辑元数据标签翻译及自动输入.js` — 「用于在 Lanraragi 单独编辑元数据时候，对标签进行翻译，还有支持把一整串标签直接导入」

🟡 第三个与本任务相关：**在编辑界面做实时标签翻译 + 批量粘贴导入**。Komga 侧若要做中文标签，这是「人工兜底」路径的参考。

---

## 9. 对 Komga 的复用判断

前提：本仓库现状是 **Go 服务（`server/`）+ C# WPF 客户端（`src/KomgaScraper.App`、`src/KomgaScraper.Core`）**，现有刮削源是 **Bangumi**（`KomgaScraper.Core/Scraping/BatchMatcher.cs`、`KomgaScraper.Core/Text/TagRules.cs`、`KomgaScraper.Core/Mylar/FolderTagger.cs` 等）。以下按「照搬 / 改造 / 放弃」三档分类。

### 9.1 建议照搬（逻辑可直接移植）✅

| 资产 | 出处 | 为什么 |
|---|---|---|
| **同人志命名正则** `(Event) [Artist] TITLE (Series) [Language]` | `RegexParse.pm:78` | 标准且成熟，直接可用；命名捕获组 → 命名空间的机制很优雅 |
| **`parse_artist_value` 的 circle/artist 拆分** | `RegexParse.pm:185-200` | 一行正则解决 `[Circle (Artist)]`，与 HDoujin 的 ns=1/2 分离设计一致 🟢 |
| **档案名归一化去噪规则表** | `CopyTagsFromFilename.pm:74-93` | 覆盖 `[v2]/[DL版]/[中国翻訳]/1080p/24p/raw/uncensored` 等，是「同本不同版本归并」的现成规则 |
| **`clean_title` + `most_similar` 相似度择优** | `Utils/String.pm:14-28`,`:69-85` | 唯一的相似度实现；`HentagOnline_CN.pm:258-270` 有完整调用范式 |
| **EH `gdata` 调用方式** | `EHentai.pm:292-317` + §3.4 实测 | 无需鉴权、字段最全（含 `rating`/`posted`/`filecount`/`thumb`），是最优元数据源 |
| **EH 搜索 URL 构造**（`advsearch=1` + `f_sfu/f_sft/f_sfl=on` + 引号短语） | `EHentai.pm:163` | 实测可用（§3.3），照抄即可 |
| **`source:` 标签作为「已刮削」哨兵 + 协议规范化** | `SourceBatch.pm:70-117` | 幂等重跑的关键；三种 URL 形态归一后才好判重 |
| **`source:nogalleryinehentai` 失败哨兵 + 只重试失败项** | `addEhentaiMetadata.pm:66`,`:117` | 大批量补全的标准做法，避免全库重复扫描 |
| **瀑布式插件编排**（有序插件列表 + rounds + stop_when_source_found + 结构化报告） | `SourceBatch.pm:249-304`,`:238-245` | 与 Komga「多 provider 回退」需求同构，是最该抄的架构 |
| **ISO 语言码 → 全称映射表** | `Ehviewer_Color.pm:143-159` | 15 个映射，直接可用 |
| **`Genre` 的 `m:`/`f:`/other 三段拆分** | `Ehviewer_Color.pm:174-184` | 彩E 的性别标签约定 |
| **EhTagTranslation 翻译函数（带别名匹配版）** | `EHentai_CN.pm:358-361` | 用 `frontMatters.key + aliases` 匹配，比精确匹配更全 |
| **翻译库结构认知**（`data[].frontMatters.{key,name,aliases}` + `data[].data[k].name`） | §5.1 实测 | 13 命名空间 / ~41k 条的完整规格 |
| **`nHentai_CN.pm` 的健壮翻译实现**（打开失败返回原文、空库检查、JSON 校验） | `nHentai_CN.pm:236-255` | 全仓库唯一不崩的写法 |

### 9.2 需要改造（思路可用，实现要重写）🔧

| 资产 | 改造点 |
|---|---|
| `hdoujin` 插件 | **必须补 `Origin` 头**（🟢 实测搜索端点缺它必 400，插件代码假设错了）；**别只取 ns=8/9**，ns=1/2/3/5/10/11/12 都有价值（§4.3.4）；处理 `errors.ambiguous`；注意 `X-Ratelimit-Limit: 5` |
| `HDoujin` 鉴权 | README 说 UUID「必填」，🟢 实测**匿名也能搜/取详情**（只影响限速），故可做成「可选 token 提升配额」 |
| 各插件的 `translate_tag_to_cn` | 抽成**单一模块**（现散落 8 份）；修 3 个 bug：加 `quotemeta`/锚定、处理 `artist cg`→`artistcg` 三类别、打开失败返回原文 |
| 中文命名空间策略 | Putarku 是**把命名空间整体换成中文**（`artist:xxx` → `艺术家:xxx`）。Komga 的 tags/authors/genres 是分字段的，🟡 更合理的是**保留英文命名空间做字段路由，中文只作为显示名**，否则后续按 `artist:` 过滤会失效 |
| `CopyTagsFromFilename` | 「同名继承标签」思路照搬，但 LRR 的 Redis key 扫描 → 换成 Komga 的 book/series 查询 |
| `add*` 批量脚本 | 换成 Komga 侧的队列 + 断点续跑；`sleep(10)` 硬编码换成可配置退避；修 `error eq "No matching EH Gallery Found!"` 的中英文不匹配 bug（`addEhentaiMetadata.pm:65` vs `EHentai.pm:98`） |
| `ComicLooms` / `Eze` / `Ehviewer_*` / `HatH` | 「读档案内嵌元数据」的思路**高度可用**，但 `is_file_in_archive`/`extract_file_from_archive` 是 LRR API，需换成 .NET 的 zip 读取；且 `unlink $filepath`（`ComicLooms.pm:85`）要注意**不要删到用户原文件** |
| `ExHentai_Lanraragi_Checker.js` | 判定端点从 LRR `/api/plugins/use?plugin=urlfinder` 换成 Komga 的 `GET /api/v1/series?search=` 等；缓存/去重/并发池/204 重试的工程细节可直接沿用 |
| `Tsukihi` | `Authorization: Bearer btoa(apiKey)` 的鉴权形式、任务轮询（`/api/minion/{job}/detail`）可参考；Komga 侧需找对应的扫描/任务 API |
| `ApplyCopyrightTags.py` | 流程（读关键词 → 搜索 → 合并标签 → PUT）可照搬，但 LRR 的 `/login` + `/api/search?filter=` + `/api/archives/{id}/metadata` 要换成 Komga 的认证与 API |

### 9.3 不可复用 / 应放弃 ❌

| 资产 | 原因 |
|---|---|
| **全部 Perl 插件外壳** | `LANraragi::Model::Plugins`、`LANraragi::Utils::Database`、`LANraragi::Utils::Archive`、`LANraragi::Utils::Redis`、`get_plugin_logger`、`use_plugin`、`exec_metadata_plugin`、`plugin_info()` 契约 —— 全是 LRR 专有，且 LRR 用 Redis 做元数据存储，与 Komga 的 SQLite/JPA 模型完全不同 |
| **LRR 的 tag 字符串模型** | LRR 把 tags 存成**单个逗号分隔字符串**（`redis_encode`/`redis_decode`），所有逻辑围绕字符串替换（`s/category:/reclass:/g`、`join(', ', @tags)`）。Komga 的 tags/authors/genres/publishers 是**结构化字段**，字符串拼接思路不适用 |
| **`ETagConverter.pm` 的全局字符串替换** | `s/category/reclass/g`（无冒号）+ `s/Chinese/汉语/g`（`ETagConverter.pm:66-67`）会误伤；且直接改 Redis 全库、自己警告要备份 |
| **HentagOnline 全族** | 🟢 站点已停运，API 404（§4.4） |
| **`folderToCatCO.pm`（chihiro830）** | 与刮削无关（子文件夹 → 静态分类），且属 LRR 归档管理 |
| **`chu-shen` fork 的 Rating/Comment 插件** | LRR fork 专有的评分/评论扩展；Komga 无对应原生字段 |
| **`Plugins/油猴*.js` 前两个** | 纯 EH 站点 UI 增强，与元数据无关 |

### 9.4 与 Komga 现状的衔接建议（🟡 推断）

1. **优先级 1 — 离线优先**：先实现「读档案内嵌元数据」的 provider（`meta.json` / `info.json` / `ComicInfo.xml` / `galleryinfo.txt` / `.ehviewer`）。理由：🟢 当前 EH/NH/Hitomi 直连全断、必须代理；而离线路径零网络、零封禁风险，且用户的库很可能已经带有这些文件（尤其是从 eh-view-enhance、彩E、绿E、H@H 下载的）。
2. **优先级 2 — EH `gdata` 走代理**：字段最全且无需鉴权。注意 `category` 需 `lc` + 处理 `Non-H` 这类连字符值，并做 §5.3 的类别名映射。
3. **优先级 3 — HDoujin**：🟢 唯一直连可达，但必须正确设置 `Origin`（+ 搜索端点还要 `Referer`）、遵守 `X-Ratelimit-Limit: 5`。可作为 EH 不可用时的兜底。
4. **中文标签**：引入 EhTagTranslation `db.text.json`（约 4.7MB，可随程序分发或定期从 releases 更新），用「别名匹配 + 正确错误处理」的实现；**保留英文命名空间做字段路由**，中文命名空间名单独存显示层。
5. **匹配策略**：建议采用 `clean_title` + 相似度择优（而非 LRR 多数插件的「盲取第一条」），因为 🟢 HDoujin 的 `s=` 是子串搜索（`s=gal` → `total=129009`），盲取第一条必然大量误配。
6. **幂等与断点**：`source:` 哨兵 + 失败哨兵 + 结构化报告（`SourceBatch.pm:238-245` 的 6 个计数字段）是现成的验收口径。

---

## 10. 未证实 / 失败记录（如实记录）

| 项 | 状态 | 说明 |
|---|---|---|
| `ltn.hitomi.la` 全端点 | ❌ **不可达** | 直连与代理均 `000`；试过 `/`、`/galleries/{1,1000000,2100000,2100010,2500000}.js`、`/index-chinese.html`。Hitomi 接口形态**仅有代码证据** |
| `hitomi.la` 内容 | ⚠️ 仅外壳 | HTTP 200 但 4971 bytes SPA 外壳，无服务端渲染画廊链接，无法取真实 gid |
| `hitomi.la/doujinshi-all.html` | ❌ 404 | 555 bytes |
| hentag.com API | ❌ 404 | 站点停运（公告已抓取，见 §4.4） |
| HDoujin namespace 4 / 6 | ⚠️ **未观测** | 10 个样本中未出现，语义未知 |
| HDoujin namespace 1/2/7 的语义 | 🟡 **推断** | 基于样例值命名（社团/作者/上传者），**未被插件代码印证**（代码只印证了 8=male、9=female） |
| EH `expunged` / `f_sh=on` 行为 | ⚠️ 未实测 | 仅读代码（`EHentai.pm:182-184`） |
| EH `f_shash` 缩略图反查 | ⚠️ 未实测 | 需要真实缩略图 SHA1 |
| EH 的 `You are opening` 限流页 | ⚠️ 未触发 | 本次请求量小，未复现该页面 |
| HDoujin `errors.ambiguous` 的触发条件 | 🟡 仅观察到字段存在 | `s=gal` 时返回 `{"ambiguous":["gal"]}`，未测试其是否影响结果集 |
| exhentai 登录后行为 | ⚠️ 未实测 | 无 `ipb_*` cookie，仅验证到 302 |
| `chu-shen/LANraragi` 的 `tests/*.t` fixture | ⚠️ 已下载未细读 | 是后续补充接口形态证据的最佳来源 |
| Cloudflare 挑战页 | ✅ **未出现** | 所有可达站点均未返回 JS challenge / `cf-mitigated` |
| `hdoujin.org` 主站搜索页 `?q=` | ⚠️ 200 但仅 SPA 外壳 | 4000 bytes，无服务端结果 |

**代理说明**：本次所有「可达」结论均基于 `http://127.0.0.1:7890`。直连下 EH/NH/Hitomi 全部不通（见 §2.1）。与 [source-probes.md](source-probes.md) 的矩阵结论一致。

---

## 11. 本地证据文件清单（`.scratch/`，可复查）

```
.scratch/Putarku/                      28 个文件（Putarku 仓库全部文本文件）
.scratch/zhy201810576__ETagCN/         ETagCN.pm, main.pl, README.md, source/EHent.pm,
                                       source/EHentai.pm, db.text.json (4.7MB)
.scratch/zhy201810576__ETagConverter/  ETagConverter.pm, main.pl, README.md
.scratch/chu-shen__LANraragi-scripts/  README.md, addEhentaiMetadata.pm/.py,
                                       getEhTagTranslationForLANraragi.py,
                                       getLatestEhTagTranslationForLANraragi.py,
                                       plugins/Metadata/{Comment,Rating,TranslateTitleByAI}.pm,
                                       plugins/Scripts/{addEhentaiMetadata,DuplicateFinder}.pm,
                                       sync_plugins.sh
.scratch/chu-shen__LANraragi/          上游官方插件 + tests fixture + Utils/{String,Tags,Plugins}.pm
.scratch/chihiro830__LANraragiPlugin/  FolderToCatCO.pm, README.md
.scratch/starP-W__HAtH-Plugin-CN/      HatH_CN.pm, main.pl, README.md
.scratch/Difegue__Tsukihi/             background.js, popup.js, manifest.json, README.md
.scratch/eh/search.html                EH 搜索页真实 HTML（72548 bytes，含 25 个 .glink）
.scratch/eh/gdata.json                 EH gdata 真实响应
.scratch/eh/nh.json                    nHentai v2 gallery 真实响应
.scratch/eh/nh_search.json             nHentai v2 search 真实响应
.scratch/eh/ex.html                    exhentai 302 响应
.scratch/db_report.txt                 db.text.json 结构解析报告
.scratch/reclass.txt                   reclass / language 键名核验
.scratch/nh_report.txt                 nHentai JSON 结构解析报告
.scratch/inspect_db.py                 结构解析脚本
```

**复现命令要点**（本次使用的关键命令）：

```powershell
# EH 搜索页（经代理）
curl.exe -sS -x http://127.0.0.1:7890 -A "<UA>" -o search.html `
  'https://e-hentai.org/?advsearch=1&f_sfu=on&f_sft=on&f_sfl=on&f_search=%22%28C100%29%22'

# EH gdata（body 写入文件后 --data-binary）
'{"method":"gdata","gidlist":[[4264303,"ffd224e2bf"]],"namespace":1}' | Out-File req.json -Encoding ascii -NoNewline
curl.exe -sS -x http://127.0.0.1:7890 -X POST -H "Content-Type: application/json" `
  --data-binary "@req.json" https://api.e-hentai.org/api.php

# HDoujin：搜索需 Origin+Referer，详情只需 Origin
curl.exe -sS -H "Origin: https://hdoujin.org" -H "Referer: https://hdoujin.org/" `
  "https://api.hdoujin.org/books?s=gal&lang=12"
curl.exe -sS -H "Origin: https://hdoujin.org" `
  "https://api.hdoujin.org/books/detail/237429/c45a5a7cd523"
```

---

## 12. 一句话总结

这套插件的**代码**大多不可复用（Perl + LRR Redis + 专有 tag 字符串模型），但其中的**知识**非常值钱：EH `gdata` 的字段与免鉴权特性、EhTagTranslation 的 13 命名空间结构、同人志文件名正则与 circle/artist 拆分、`clean_title`+相似度择优、以及「离线内嵌元数据 → source 哨兵 → 多站瀑布 → 结构化报告」的编排范式。把这四块知识搬到 Komga 的 provider 层，比移植任何一个 `.pm` 文件都有价值。
