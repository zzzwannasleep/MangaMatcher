<div align="center">

# KomgaBangumi · Docker/Web 版

**Komga 漫画元数据刮削 · 五源匹配链 · Go + Vue 3 容器化**

[![Docker Build](https://img.shields.io/github/actions/workflow/status/zzzwannasleep/MangaMatcher/docker.yml?branch=main&style=flat-square&label=docker%20build&color=2088FF)](https://github.com/zzzwannasleep/MangaMatcher/actions/workflows/docker.yml)
[![CI](https://img.shields.io/github/actions/workflow/status/zzzwannasleep/MangaMatcher/ci.yml?branch=main&style=flat-square&label=tests&color=4CAF50)](https://github.com/zzzwannasleep/MangaMatcher/actions/workflows/ci.yml)
[![Image](https://img.shields.io/badge/image-ghcr.io%2Fmangamatcher-2088FF?logo=docker&logoColor=white)](https://github.com/zzzwannasleep/MangaMatcher/pkgs/container/mangamatcher)
[![Size](https://img.shields.io/badge/image%20size-%3C30MB-success?style=flat-square)](#-部署)
[![Platforms](https://img.shields.io/badge/platform-amd64%20%7C%20arm64-blue?style=flat-square)](#-部署)
[![Go](https://img.shields.io/badge/Go-1.27-00ADD8?logo=go&logoColor=white&style=flat-square)](server/go.mod)
[![Vue](https://img.shields.io/badge/Vue-3-42B883?logo=vuedotjs&logoColor=white&style=flat-square)](web)

</div>

---

桌面版 KomgaScraper（C#/Avalonia）的 **Docker/Web 版**：同一套五源匹配链，换成 **Go 单二进制**（无 CGO）+ **Vue 3 前端**（`go:embed` 内嵌）+ **distroless 运行镜像**。容器化思路参考 [MoviePilot](https://github.com/jxxghp/MoviePilot)。

> 💡 **为什么选 Docker 版**：常驻 NAS / 服务器 7×24 刮削、多账号分权（每人只看自己授权的库）、桌面版不开机也能在浏览器里纠错补全。

## 🚀 快速开始

**① 启动**

```bash
mkdir komgabangumi && cd komgabangumi
curl -fsSL https://raw.githubusercontent.com/zzzwannasleep/MangaMatcher/main/docker-compose.yml -o docker-compose.yml
docker compose up -d
```

**② 登录**

打开 **`http://localhost:8080`**，默认账号 `admin / admin123`——**首次登录后立即改密码**。

**③ 接上你的 Komga**

```text
系统设置 → 填 Komga 地址 + API Key → 测试连接 → 保存
（API Key 在 Komga 网页 → 账户 → API Key；可选填 Bangumi Token 支持 NSFW 搜索）
```

完成。选库 → 勾选系列 → 刮削选中，去任务中心看结果。

## ✨ 功能

| | |
|---|---|
| 👥 **账号系统** | `admin` / `master` 双角色，JWT 认证，bcrypt 密码，库级授权 |
| 🔍 **五源匹配链** | Bangumi → BookWalker → AI 识别 → AI 智能选择 → Mox → AI 译日文查 BW → MangaDex → 百度百科，宁缺毋滥精确匹配，失败进「待手动匹配」收藏夹 |
| 🤖 **AI 加持** | OpenAI 兼容接口（默认 DeepSeek）：识别作品 / 智能选择 / 简介翻译 / 补写简介，进程内缓存 |
| 📚 **BookWalker 目录库** | 官方 zst 下载或本地导入 → v2 索引（系列级 + 每卷 ISBN/发售日/单卷封面 + 类型题材标签），跨源补空 + 防串卷 25% |
| 🎨 **封面卡片墙** | 封面瀑布墙 + 懒加载分批渲染，千卷大库不卡顿；6 种刮削状态筛选 + 全库搜索 |
| 🔎 **纠正匹配** | 直接粘贴 **Bangumi / 百度百科链接**（或纯 ID）→ 后端解析标题/作者/简介/封面 → **预览确认**后替换落库 |
| ✏️ **就地编辑** | 详情大窗直接改：系列字段 + 逐卷（标题/卷号/日期/ISBN/简介/封面上传）、只提交改动字段、锁字段透传、重刮每卷 |
| 📋 **任务中心** | 卡片式任务列表；详情逐系列展示**数据源 / 匹配对象 / Bangumi 直链 / 字段级差异** |
| 🖼 **卷级元数据** | 系列封面之外，每卷独立封面 + 简介，逐卷对齐 Bangumi 单行本 |
| 🏷 **版本标记** | 爱藏版/完全版等后缀 → 标签 + 标题后缀，黑白名单可自定义；一键打标（MACHINE 标签可追溯） |
| 📦 **Mylar 导出** | series.json ZIP 下载（相对目录结构 + oneshot 特殊名 + 可选封面），或直写 comicsDir |
| 🌐 **代理** | HTTP/HTTPS/SOCKS4/4a/5 + 账密（纯 Go），Bangumi/封面/BookWalker 共用；Komga 永远直连 |
| 🧭 **管理面板** | 可折叠侧边栏 / 系统设置网格 / 用户管理 / 系统日志页（在线查看 + 过滤 + 一键导出） |

## ⚙️ 配置参考

环境变量都是**首次启动种子**——之后在 Web 设置页改的配置存进 SQLite，优先级更高。

| 变量 | 说明 | 默认 |
|------|------|------|
| `PORT` | 监听端口 | `8080` |
| `DATA_DIR` | 数据目录（SQLite + JWT secret） | `/data` |
| `ADMIN_USERNAME` / `ADMIN_PASSWORD` | admin 引导账号（**仅在库内无用户时创建**） | `admin` / `admin123` |
| `KOMGA_URL` | Komga 地址种子 | 空 |
| `KOMGA_API_KEY` | Komga API Key 种子（`X-API-Key`） | 空 |
| `BANGUMI_TOKEN` | Bangumi Access Token（NSFW 搜索需要） | 空 |
| `BANGUMI_API_BASE` | Bangumi API 基址（反代场景可改） | `https://api.bgm.tv` |
| `MOX_API_BASE` / `MANGADEX_API_BASE` / `BAIKE_API_BASE` | 第三方源基址（自建反代场景；空 = 官方站） | 空 |
| `MOX_PACE_MS` | Mox 请求间隔毫秒数 | 空 |
| `TZ` | 时区 | `Asia/Shanghai` |

> 🔒 `ADMIN_PASSWORD` 只在首次引导时生效；API Key 等敏感配置在 `DATA_DIR` 的 SQLite 里，注意备份权限。

### 与 Komga 同 compose 网络（推荐）

Komga 也是容器时，同网络互通后 `KOMGA_URL` 直接填服务名，流量不出宿主机：

```yaml
services:
  komgabangumi:
    image: ghcr.io/zzzwannasleep/mangamatcher:latest
    restart: unless-stopped
    ports: ["8080:8080"]
    environment:
      ADMIN_USERNAME: admin
      ADMIN_PASSWORD: admin123
      KOMGA_URL: http://komga:25600
      KOMGA_API_KEY: your-komga-api-key
      TZ: Asia/Shanghai
    volumes:
      - ./data:/data
    networks: [komga]

  komga:
    image: gotson/komga:latest
    ports: ["25600:25600"]
    volumes:
      - /path/to/comics:/data:ro
      - ./komga:/config
    networks: [komga]

networks:
  komga:
```

不想用 compose，`docker run` 等价：

```bash
docker run -d --name komgabangumi \
  -p 8080:8080 -v ./data:/data \
  -e ADMIN_USERNAME=admin -e ADMIN_PASSWORD=admin123 \
  -e KOMGA_URL=http://192.168.1.10:25600 -e KOMGA_API_KEY=your-key \
  -e TZ=Asia/Shanghai --restart unless-stopped \
  ghcr.io/zzzwannasleep/mangamatcher:latest
```

### 🛠 运维手册

```bash
# 升级（数据在 /data 卷，无损）
docker compose pull && docker compose up -d

# 健康检查（distroless 无 shell，用编排层探活）
curl -f http://localhost:8080/api/health

# 日志
docker logs -f komgabangumi
# 或网页端：admin → 系统日志 → 过滤 / 一键导出

# 备份
tar czf komgabangumi-backup.tgz ./data
```

> ⚠️ **bind mount 权限**：运行层是 `nonroot`（uid 65532）。Linux 上 `./data` 报 Permission denied 时：`sudo chown -R 65532:65532 ./data`
>
> 🌏 **海外部署**：海外 VPS 访问 Bangumi 如遇风控/超时，在设置页配 HTTP/SOCKS 代理（纯 Go 实现，零额外依赖）。
>
> 🔁 **反向代理**：Nginx/Traefik 反代注意 `client_max_body_size` ≥ 32m（封面上传），纯 REST 无 WebSocket。

## 🧭 使用流程

```text
刮削   选库 → 勾选系列 → 刮削选中（或全库覆盖匹配 / 一键打标 / Mylar 导出）
观察   任务中心卡片看进度；详情逐系列看：用了哪个数据源、匹配到谁、改了哪些字段
纠错   没刮到 / 刮错 → 详情 → 纠正匹配 → 粘贴 Bangumi 或百科链接 → 预览确认 → 落库
精修   详情大窗就地编辑：系列字段 + 逐卷封面/简介/ISBN，保存只提交改动字段
授权   admin 在用户管理给 master 分库；master 只能看到自己的库
```

## 🧪 本地开发 / Mock 测试

无需 Docker、无需真实 Komga，六件套 mock 起一个 122 系列的真实数据大库：

```powershell
# 1. mock 全家桶（Komga:25699 / Bangumi:25698 / AI:25697 / Mox:25696 / MangaDex:25695 / 百科:25694）
node tools/mock-komga.js
node tools/mock-bangumi.js
node tools/mock-ai.js
node tools/mock-mox.js
node tools/mock-mangadex.js
node tools/mock-baike.js

# 2. 后端（env 种子指向 mock）
$env:DATA_DIR='./server/data'; $env:PORT='8080'
$env:KOMGA_URL='http://127.0.0.1:25699'; $env:KOMGA_API_KEY='mock-key'
$env:BANGUMI_API_BASE='http://127.0.0.1:25698'
$env:MOX_API_BASE='http://127.0.0.1:25696'; $env:MANGADEX_API_BASE='http://127.0.0.1:25695'
$env:BAIKE_API_BASE='http://127.0.0.1:25694'; $env:MOX_PACE_MS='0'
go run ./server/cmd/server
# 浏览器打开 http://localhost:8080（admin/admin123）

# 3. Go 集成测试（httptest 内嵌 mock，无需起服务）
go test ./server/... -count=1

# 4. 前端开发模式
cd web && npm install && npm run dev
```

Go 集成测试覆盖：登录鉴权、master 越权 403、admin 建号授权、dry-run 零写入、执行后 PATCH 体校验、五源解析、回退链顺序到百科命中、AI 缓存命中、Mox 520 退避、BW 跨源补空+防串卷、跳过已刮零请求、overrideMatch 语义、编辑器只提交改动字段、Mylar zip 合法、打标 dry-run、状态筛选 6 项、admin 删除保护。

## 🏗 自己构建镜像

```bash
docker build -t komgabangumi .
```

多阶段：`node:22-alpine`（Vite build）→ `golang:1.27-alpine`（`CGO_ENABLED=0` 静态编译 + `go:embed` 前端）→ `gcr.io/distroless/static-debian12:nonroot`。推送到 `main` 或 `docker-v*` 标签时，GitHub Actions 自动构建 **linux/amd64 + linux/arm64** 双架构镜像发布到 GHCR（桌面版客户端 `src/` 不参与此构建）。

## 📐 架构

```text
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

## 🗺 Roadmap（v3 候选）

CoverCache 磁盘暂存 · 封面 WebP 413 压缩档 · SSE 实时任务日志 · 未记录系列的后台状态补查。

## 📄 许可

MIT · 逻辑移植自桌面版 KomgaScraper，原始逻辑来自 [dyphire/KomgaBangumi](https://github.com/dyphire/KomgaBangumi)。
