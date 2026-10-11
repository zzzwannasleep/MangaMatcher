# KomgaBangumi Docker 版 — 交付规格（v1）

> 桌面版 KomgaScraper（C#/Avalonia）的 Docker/Web 化。参考 MoviePilot 的容器化思路，但后端用 **Go** 追求性能。
> Go 后端单二进制 + go:embed 前端 + SQLite（纯 Go 驱动 modernc.org/sqlite，无 CGO）→ distroless 镜像 < 30MB。

## 环境铁律（两个 Agent 都必须遵守，实测踩坑）

1. 本机 pwsh 沙箱**禁止在工作区外创建目录**（%TEMP%、%APPDATA% 都会 Access denied）。
2. 所有 `go` 命令必须先设环境变量（目录已建好、已实测可用）：
   ```powershell
   $env:GOTOOLCHAIN='local'
   $env:GOTMPDIR='D:\WorkProgram\komgabangumidocker\.toolchain\tmp'
   $env:GOCACHE='D:\WorkProgram\komgabangumidocker\.toolchain\gocache'
   $env:GOPATH='D:\WorkProgram\komgabangumidocker\.toolchain\gopath'
   & 'D:\WorkProgram\komgabangumidocker\.toolchain\go\bin\go.exe' <command>
   ```
3. Go 工具链：`.toolchain\go\bin\go.exe`（go1.27.2，已就绪）。npm 已就绪；无 pnpm，前端用 npm。
4. 本地无 Docker：不要尝试 docker 命令，用 mock 测试（见下）。

## 角色与权限

| 角色 | 权限 |
|------|------|
| `admin` | 用户管理（增删改 master/admin）、给每个 master 分配可刮削的库、全局设置（Komga 地址/API Key、Bangumi token、并发数）、任意库刮削 |
| `master` | 仅查看/刮削被分配的库，创建刮削任务（dry-run/执行），查看任务进度与日志 |

- 首次启动 bootstrap：无用户时按环境变量 `ADMIN_USERNAME`（默认 `admin`）/`ADMIN_PASSWORD`（默认 `admin123`，日志中打印警告）创建 admin。
- 认证：`POST /api/auth/login` 签发 JWT（HS256，secret 存 data 目录首次生成）；后续请求 `Authorization: Bearer`。
- 密码 bcrypt 哈希。admin 不能删除自己/最后一个 admin。

## REST API 契约（前端按此开发，后端按此实现）

统一前缀 `/api`，JSON。错误格式 `{ "error": "中文可读消息" }`，状态码 400/401/403/404/500。

### 认证
- `POST /api/auth/login` `{username, password}` → `{token, user:{id, username, role}}`
- `GET /api/me` → `{id, username, role, libraries: [libraryId]}`（master 返回被分配的库 id；admin 返回 `["*"]`）

### 用户管理（仅 admin）
- `GET /api/users` → `[{id, username, role, libraries:[libraryId], createdAt}]`
- `POST /api/users` `{username, password, role: "admin"|"master", libraries: [libraryId]}`
- `PATCH /api/users/:id` `{password?, role?, libraries?}`（字段均可选）
- `DELETE /api/users/:id`
- `GET /api/users/name-index`（内部用：登录页无需）

### 库与系列（Komga 代理 + 权限过滤）
- `GET /api/libraries` → `[{id, name, ...}]`（master 仅见被分配的库）
- `GET /api/libraries/:id/series?page=0&size=50` → Komga `POST /api/v1/series/list` 代理结果（Page 结构 `{content, totalElements}`），校验 master 库权限
- `GET /api/series/:id` → `{...series, booksCount, metadata, books:[...]}`（校验权限）
- `GET /api/series/:id/thumbnail` → 图片二进制代理（Accept: image/jpeg）
- `POST /api/settings/test-komga` → `{ok: true, version: "x.y.z"}` 或 400

### 刮削任务
- `POST /api/jobs` `{libraryId, seriesIds: string[]（空=全库）, dryRun: bool}` → `{id}`；master 的 libraryId 必须在授权列表内（403 拒绝）
- `GET /api/jobs` → `[{id, kind:"scrape", libraryId, total, done, ok, failed, status:"running|done|canceled", dryRun, startedAt, finishedAt}]`
- `GET /api/jobs/:id` → `{...上述, items: [{seriesId, seriesName, status:"ok|failed|skipped|manual", message, subjectId?, matchTitle?, changed?: {字段: [旧, 新]}}]}`
- `POST /api/jobs/:id/cancel`

### 设置（仅 admin）
- `GET /api/settings` → `{komgaUrl, komgaApiKey: "****"（打码）, komgaUser?, bangumiToken: 打码, concurrency, dryRunDefault, manualCollection}` 
- `PATCH /api/settings` 同结构；`komgaApiKey`/`bangumiToken` 传 `"****"` 或空表示不修改
- 内部：KOMGA_URL / KOMGA_API_KEY 环境变量作为首次种子，DB 设置优先

### 其他
- `GET /api/health`（免认证）→ `{ok, version}`

## 刮削管线（v1 移植自 Core 层，Bangumi 主链）

对每个系列：
1. `TitleParser` 清洗标题（去除 `[组名]`、`Vol.xx`、括号尾巴等），`VersionMarker` 把 爱藏版/完全版/盒装 等后缀转为标签
2. Bangumi `POST /v0/search/subjects`（filter:type=1/book_series，限速 ≥350ms/请求，429/网络错误退避重试）→ 取首个高匹配（标题精确/别名/原名，宁缺毋滥）
3. `GET /v0/subjects/{id}` 取详情：name_cn→title，summary→summary，infobox 作者→authors（Writer/Artist），tags（count≥阈值）→tags，连载状态→status（ONGOING/COMPLETED），volumes→totalBookCount
4. `GET /v0/subjects/{id}/subjects` 取单行本关系，按卷号对齐 → 每卷 PATCH title/number/releaseDate/ISBN
5. 封面：下载 subject images.large → multipart 上传系列封面（selected=true），有则处理单卷封面
6. 失败 → 加入 Komga 收藏夹 `待手动匹配`（可配置开关）
7. `dryRun=true` 时只产出 changed 差异，不发任何 PATCH/上传

**v1 不做**（接口留桩，roadmap）：BookWalker/Mox/MangaDex/百科多源回退、AI 翻译补写、Mylar 导出。

## 前端（web/）

Vue 3 + Vite + 原生 CSS（design tokens），深色漫画审美、克制、动效优美（页面过渡、卡片 hover 浮起、任务进度条、toast）。视图：
1. 登录页
2. 库面板（图书馆网格，master只见授权库）
3. 系列浏览（表格/卡片 + 多选 + 刮削按钮 + dry-run 预览）
4. 任务中心（进度条 + 每系列结果列表）
5. 管理端（仅 admin）：用户管理（角色/密码/库授权勾选）、设置页
- 401 自动跳登录；路由守卫按角色。

## 目录分工

```
server/   Go 后端（backend agent 独占）
web/      Vue 前端（frontend agent 独占）
Dockerfile / docker-compose.yml / .github/workflows / README-Docker.md  （Lead 独占）
docs/SPEC.md  本文件
tools/mock-komga.js / mock-bangumi.js  复用的 mock（端口 25699 / 25698）
```

## Mock 测试（本地无 Docker）

- Go `httptest` 起 mock Komga（对齐 tools/mock-komga.js 的路由/行为）+ mock Bangumi（对齐 mock-bangumi.js）跑集成测试：登录→权限拒绝（master 刮未授权库 403）→dry-run 差异正确→执行后 mock 收到正确 PATCH 体→失败系列进 manual collection。
- 手动浏览器测试：node tools/mock-*.js + 本地 go server（指向 mock）跑通 UI。

## Docker（Lead 交付）

多阶段构建：`node:22-alpine` 构建 web → `golang:1.27-alpine` 构建 server（embed web/dist）→ 运行层 `gcr.io/distroless/static`（或 alpine）。卷 `/data`。`docker-compose.yml` 示例 + GitHub Actions 多架构发布 workflow。
