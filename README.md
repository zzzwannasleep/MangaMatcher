<div align="center">

# KomgaScraper

**Komga 漫画服务器元数据刮削器 · Windows 桌面版 · 基于 Bangumi**

[![Release](https://img.shields.io/github/v/release/zzzwannasleep/MangaMatcher?style=flat-square&color=8f7fff)](https://github.com/zzzwannasleep/MangaMatcher/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/zzzwannasleep/MangaMatcher/total?style=flat-square&color=4caf50)](https://github.com/zzzwannasleep/MangaMatcher/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/zzzwannasleep/MangaMatcher/release.yml?style=flat-square&branch=main)](https://github.com/zzzwannasleep/MangaMatcher/actions/workflows/release.yml)
[![Stars](https://img.shields.io/github/stars/zzzwannasleep/MangaMatcher?style=flat-square&color=ffd700)](https://github.com/zzzwannasleep/MangaMatcher/stargazers)
[![License](https://img.shields.io/github/license/zzzwannasleep/MangaMatcher?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue?style=flat-square)](#)
[![Platform](https://img.shields.io/badge/Platform-macOS-lightgrey?style=flat-square)](#)
[![UI](https://img.shields.io/badge/UI-Avalonia%2011-9B4F96?style=flat-square)](#)
[![.NET](https://img.shields.io/badge/.NET-8.0-512bd4?style=flat-square)](#)
[![Language](https://img.shields.io/badge/C%23-12-178600?style=flat-square)](#)

![screenshot](docs/screenshot-main.png)

</div>

---

## ✨ 功能

| | |
|---|---|
| 🔍 **多源刮削** | Bangumi → BookWalker → Mox → MangaDex → 百度百科 五源回退链，全链精确匹配、宁缺毋滥 |
| 🤖 **AI 加持** | 作品识别 / 多候选智能选择 / 非中文简介自动翻译 / 三源无简介时补写（OpenAI 兼容接口） |
| 🖼 **封面抓取** | 系列封面 + 每卷单行本封面，按卷号对齐，多尺寸候选自动降档 |
| 🚀 **全自动批量** | 并行刮削 + 礼貌节流，Dry-run 预览先行，失败自动进"手动匹配"收藏夹 |
| ✏️ **编辑器** | 系列信息 / 标签·别名 / 每卷（标题、卷号、发售日、ISBN、封面）可视化编辑 |
| 🏷 **版本标记** | 爱藏版/完全版等后缀自动落标签，多版本系列不再刮平 |
| 📦 **Mylar 导出** | `series.json` 导出 + 文件夹打标（MACHINE 标签可追溯） |
| 💚 **绿色便携** | 不写注册表不写 %APPDATA%，整目录复制即迁移 |
| 🎨 **现代 UI** | Avalonia 跨平台界面：hover 浮起 / 窗口淡入 / 忙碌脉冲 / Toast 通知全套动效 |

## 🚀 快速开始

1. 从 [Releases](https://github.com/zzzwannasleep/MangaMatcher/releases/latest) 下载 zip，**解压后**运行 `KomgaScraper.exe`
2. 填入 Komga 地址（`http://localhost:25600`）+ API Key → **测试连接**
3. 选库 → 加载系列 → 勾选 → **刮削元数据**（Dry-run 预览 → 关闭 Dry-run 执行）

> NSFW 搜索需在设置中填入 [Bangumi Access Token](https://bgm.tv/settings/api)；部分网络需在设置中配置代理。

---

<div align="center">

## 🐳 Docker/Web 版

**同一套五源匹配链的 Go + Vue3 容器版** · distroless 镜像 **< 30MB** · `amd64` / `arm64`

[![Docker Build](https://img.shields.io/github/actions/workflow/status/zzzwannasleep/MangaMatcher/docker.yml?branch=main&style=flat-square&label=docker%20build&color=2088FF)](https://github.com/zzzwannasleep/MangaMatcher/actions/workflows/docker.yml)
[![Tests](https://img.shields.io/github/actions/workflow/status/zzzwannasleep/MangaMatcher/ci.yml?branch=main&style=flat-square&label=tests&color=4CAF50)](https://github.com/zzzwannasleep/MangaMatcher/actions/workflows/ci.yml)
[![Image](https://img.shields.io/badge/image-ghcr.io%2Fmangamatcher-2088FF?logo=docker&logoColor=white&style=flat-square)](https://github.com/zzzwannasleep/MangaMatcher/pkgs/container/mangamatcher)
[![Go](https://img.shields.io/badge/Go-1.27-00ADD8?logo=go&logoColor=white&style=flat-square)](server/go.mod)
[![Vue](https://img.shields.io/badge/Vue-3-42B883?logo=vuedotjs&logoColor=white&style=flat-square)](web)

</div>

### 三步部署

```bash
mkdir komgabangumi && cd komgabangumi
curl -fsSL https://raw.githubusercontent.com/zzzwannasleep/MangaMatcher/main/docker-compose.yml -o docker-compose.yml
docker compose up -d
```

打开 `http://localhost:8080`（默认 `admin / admin123`，首次登录后立即改密码）→ 系统设置里填 Komga 地址 + API Key → 完成。

### Docker/Web 版独有

| | |
|---|---|
| 👥 **多角色** | `admin` / `master` 双角色，库级授权——家人只看自己授权的库 |
| 🖥 **Web UI** | Vue 3 深色审美：封面卡片墙 / 就地编辑大窗 / 贴链接纠正匹配 / 任务差异详情 / 系统日志 |
| 🏠 **常驻运行** | NAS / 服务器 7×24 刮削，桌面版不开机也能在浏览器里纠错补全 |

📖 **完整部署教程**（compose / docker run / 升级备份 / 权限排障 / 海外代理）：[README-Docker.md](README-Docker.md)
📋 API 契约：[docs/SPEC.md](docs/SPEC.md) · [docs/SPEC-v2.md](docs/SPEC-v2.md)
⚙️ CI：镜像构建 [docker.yml](.github/workflows/docker.yml)（`docker-v*` 标签 → GHCR，不构建桌面客户端）· 测试门禁 [ci.yml](.github/workflows/ci.yml)

## 📖 文档

详细设置项、BookWalker 数据源导入（403 问题）、代理配置、测试驱动等说明见 [完整文档](docs/README-full.md)。

## 🛠 构建（桌面版）

```powershell
dotnet publish src/KomgaScraper.App/KomgaScraper.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

推送到 `main` 自动触发云端构建：Windows / macOS 双平台产物随构建自动步进小版本（`0.0.1`、`0.0.2` …）——Windows 版发布到 [Releases](https://github.com/zzzwannasleep/MangaMatcher/releases)，macOS 版在 Actions Artifacts 产出（arm64 + x64）。UI 基于 Avalonia 11，三端同一份代码。

## 📄 许可

[MIT](LICENSE) · 逻辑移植自 [dyphire/KomgaBangumi](https://github.com/dyphire/KomgaBangumi) 与 [dyphire/KomgaMylar](https://github.com/dyphire/KomgaMylar)
