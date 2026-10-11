# KomgaBangumi Docker 版

桌面版 KomgaScraper（C#/Avalonia，独立仓库）的 **Docker/Web 版**：Komga 漫画元数据刮削（基于 Bangumi），带多角色账号系统。后端 **Go**（单二进制、无 CGO），前端 Vue 3 由 `go:embed` 内嵌，运行镜像基于 distroless，**最终镜像 < 30MB**。容器化思路参考 [MoviePilot](https://github.com/jxxghp/MoviePilot)，但以 Go 追求更低内存占用与启动速度。

## ✨ 功能（v2 · 与桌面版对齐）

| | |
|---|---|
| 👥 **账号系统** | `admin` / `master` 双角色，JWT 认证，bcrypt 密码，库级授权 |
| 🔍 **五源匹配链** | Bangumi → BookWalker → AI 识别 → AI 智能选择 → Mox → AI 译日文查 BW → MangaDex → 百度百科，宁缺毋滥精确匹配，失败进「待手动匹配」 |
| 🤖 **AI 加持** | OpenAI 兼容接口（默认 DeepSeek）：识别作品 / 智能选择 / 简介翻译 / 补写简介，进程内缓存 |
| 📚 **BookWalker 目录库** | 官方 zst 下载或本地导入 → v2 索引（系列级 + 每卷 ISBN/发售日/单卷封面 + 类型题材标签），跨源补空 + 防串卷 25% |
| ✏️ **系列编辑器** | 三页签（系列信息/标签·别名/每卷）、换封面、只提交改动字段、锁字段回传、重刮每卷 |
| 🎯 **纠正/覆盖匹配** | 候选按源分组 + 详情区 + 手动输入 BGM 链接/ID；覆盖匹配忽略旧数据重走全链，成功自动移出收藏夹 |
| 🏷 **版本标记** | 爱藏版/完全版等后缀 → 标签 + 标题后缀，黑白名单可自定义；一键打标修复旧库（MACHINE 可追溯） |
| 📦 **Mylar 导出** | series.json ZIP 下载（相对目录结构 + oneshot 特殊名 + 可选封面），或直写 comicsDir |
| 🧪 **Dry-run** | 字段级差异预览（旧→新），确认后执行；手动匹配应用也遵循 |
| 🌐 **代理** | HTTP/HTTPS/SOCKS4/4a/5 + 账密（纯 Go），Bangumi/封面/BW 下载共用；Komga 永远直连 |
| 🖥 **Web 界面** | Vue 3 深色审美：图书馆网格 / 系列浏览（✓✗ 状态 + 6 种筛选 + 搜索）/ 任务中心 / 全局搜索 / 手动匹配页 / 设置中心 |
| 📄 **日志** | stdout + 按日滚动文件，`GET /api/logs` 导出 |

## 🚀 快速开始（Docker）

```bash
docker compose up -d
# 打开 http://localhost:8080
# 默认账号 admin / admin123（首次登录后请立即改密码）
```

`docker-compose.yml` 环境变量：

| 变量 | 说明 | 默认 |
|------|------|------|
| `PORT` | 监听端口 | `8080` |
| `DATA_DIR` | 数据目录（SQLite + JWT secret） | `/data` |
| `ADMIN_USERNAME` / `ADMIN_PASSWORD` | 首次启动 admin 引导账号（仅在无用户时创建） | `admin` / `admin123` |
| `KOMGA_URL` | Komga 地址种子（之后可在 Web 设置页改，DB 优先） | 空 |
| `KOMGA_API_KEY` | Komga API Key 种子（X-API-Key） | 空 |
| `BANGUMI_TOKEN` | Bangumi Access Token（NSFW 搜索需要） | 空 |
| `BANGUMI_API_BASE` | Bangumi API 基址（默认 `https://api.bgm.tv`；反代/mock 场景可改） | 空 |
| `MOX_API_BASE` / `MANGADEX_API_BASE` / `BAIKE_API_BASE` / `MOX_PACE_MS` | 第三方源基址与 Mox 限速（自建反代场景；空 = 官方站） | 空 |
| `TZ` | 时区 | `Asia/Shanghai` |

## 🧭 使用流程

1. admin 登录 → **设置页**填 Komga 地址 + API Key → 测试连接（可选：代理 / AI / BookWalker 索引）
2. admin 在 **用户管理** 新建 master 并勾选授权库
3. master 登录 → 选库 → 勾选系列 → **Dry-run 预览** → **执行刮削**（也可覆盖匹配/一键打标/Mylar 导出）
4. 任务中心看逐系列结果；失败系列在 **手动匹配页** 纠正匹配（候选分组 / 手动输 BGM 链接），成功后自动移出收藏夹
5. 任意系列可进 **编辑器** 精修（三页签 + 换封面 + 重刮每卷）

## 🧪 本地开发 / Mock 测试（无需 Docker / 无需真实 Komga）

```powershell
# 1. 起 mock 全家桶（Komga:25699 / Bangumi:25698 / AI:25697 / Mox:25696 / MangaDex:25695 / 百科:25694）
node tools/mock-komga.js
node tools/mock-bangumi.js
node tools/mock-ai.js
node tools/mock-mox.js
node tools/mock-mangadex.js
node tools/mock-baike.js

# 2. 起后端（env 种子指向 mock）
$env:DATA_DIR='./server/data'; $env:PORT='8080'
$env:KOMGA_URL='http://127.0.0.1:25699'; $env:KOMGA_API_KEY='mock-key'
$env:BANGUMI_API_BASE='http://127.0.0.1:25698'
$env:MOX_API_BASE='http://127.0.0.1:25696'; $env:MANGADEX_API_BASE='http://127.0.0.1:25695'
$env:BAIKE_API_BASE='http://127.0.0.1:25694'; $env:MOX_PACE_MS='0'
go run ./server/cmd/server
# 浏览器打开 http://localhost:8080（admin/admin123）

# 3. Go 集成测试（httptest mock 内嵌，无需起任何服务）
go test ./server/... -count=1

# 4. 前端开发模式
cd web && npm install && npm run dev
```

Go 集成测试覆盖：登录鉴权、master 越权 403、admin 建号授权、dry-run 零写入、执行后 PATCH 体校验、五源解析、回退链顺序到百科命中、AI 缓存命中、Mox 520 退避、BW 跨源补空+防串卷、跳过已刮零请求、overrideMatch 语义、编辑器只提交改动字段、Mylar zip 合法、打标 dry-run、状态筛选 6 项、admin 删除保护。

## 🐳 镜像构建

```bash
docker build -t komgabangumi .
```

多阶段：`node:22-alpine`（Vite build）→ `golang:1.27-alpine`（CGO_ENABLED=0 静态编译 + go:embed 前端）→ `gcr.io/distroless/static-debian12:nonroot`。推送到 `main`/tag 时 GitHub Actions 自动构建并发布 `linux/amd64 + linux/arm64` 镜像到 GHCR。

> **bind mount 权限提示**：运行层是 `nonroot`（uid 65532）。Linux 主机上 bind mount `./data` 时若遇 Permission denied，先 `chown -R 65532:65532 ./data`（或在 compose 里加 `user: "${UID}:${GID}"` 并保证目录属主匹配）。distroless 无 shell，不做内置 HEALTHCHECK，编排层可用 `GET /api/health` 探活。

## 📐 架构

```
server/  Go 后端
  cmd/server/           入口（版本注入、日志双写、env 种子）
  internal/auth/        JWT + bcrypt + 角色中间件
  internal/store/       SQLite（users / user_libraries / settings / scrape_state）
  internal/komga/       Komga REST 客户端（X-API-Key，CleanMeta PATCH 规则，multipart 封面）
  internal/bangumi/     Bangumi v0 客户端（≥350ms 限速节拍器）
  internal/sources/     五源客户端：BookWalker 索引 / Mox(串行+520退避) / MangaDex / 百度百科 / AI(缓存) / 代理(纯Go SOCKS)
  internal/scrape/      匹配回退链 / 刮削管线 / 编辑器补丁 / Mylar / 打标 / 差异
  internal/jobs/        worker pool 任务引擎（进度 / 取消 / apply 任务）
  internal/api/         HTTP 路由与 DTO（v1+v2 全端点）
  internal/webdist/     go:embed 前端产物
web/     Vue 3 前端（Vite）
tools/   mock 全家桶（komga/bangumi/ai/mox/mangadex/baike）
docs/SPEC.md · SPEC-v2.md  API 契约与角色权限规格
```

## 📄 许可与致谢

MIT · 逻辑移植自桌面版 KomgaScraper，原始逻辑来自 [dyphire/KomgaBangumi](https://github.com/dyphire/KomgaBangumi)。

**Roadmap（v3 候选）**：CoverCache 磁盘暂存、封面 WebP 413 压缩档、SSE 实时任务日志、未记录系列的后台状态补查、日志查看页。