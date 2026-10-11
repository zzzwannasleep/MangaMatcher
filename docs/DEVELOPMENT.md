# 开发指南（Docker/Web 版）

无需 Docker、无需真实 Komga：自带六件套 mock，起一个 **122 个系列 / 730+ 本（真实 Bangumi 数据）** 的假库跑全套环境。

## 1. Mock 全家桶

| 服务 | 端口 | 说明 |
|------|------|------|
| `tools/mock-komga.js` | 25699 | Komga 模拟：经 `127.0.0.1:7890` 代理从真实 Bangumi API 拉数据建库（真封面/真简介/真标签），支持真实分页、封面上传/下载、收藏夹 |
| `tools/mock-bangumi.js` | 25698 | Bangumi v0 API：搜索/条目详情/关联单行本/卷详情，SVG 封面 |
| `tools/mock-ai.js` | 25697 | OpenAI 兼容接口（识别/选择/翻译/补写） |
| `tools/mock-mox.js` | 25696 | Mox 站点模拟（含 520 退避场景） |
| `tools/mock-mangadex.js` | 25695 | MangaDex API 模拟 |
| `tools/mock-baike.js` | 25694 | 百度百科词条卡片模拟 |
| `tools/mock-github.js` | 25693 | GitHub tags/releases/资产 zip 模拟（离线自测「检查更新」链路，见 §6） |

```powershell
node tools/mock-komga.js
node tools/mock-bangumi.js
node tools/mock-ai.js
node tools/mock-mox.js
node tools/mock-mangadex.js
node tools/mock-baike.js
node tools/mock-github.js
```

> mock-komga 首次启动会经代理拉取真实 Bangumi 数据建库，控制台出现 `[MOCK] library ready` 即就绪；代理不可用时自动降级为内置兜底数据。

## 2. 启动后端（env 种子指向 mock）

```powershell
$env:DATA_DIR='./server/data'; $env:PORT='8080'
$env:KOMGA_URL='http://127.0.0.1:25699'; $env:KOMGA_API_KEY='mock-key'
$env:BANGUMI_API_BASE='http://127.0.0.1:25698'
$env:MOX_API_BASE='http://127.0.0.1:25696'; $env:MANGADEX_API_BASE='http://127.0.0.1:25695'
$env:BAIKE_API_BASE='http://127.0.0.1:25694'; $env:MOX_PACE_MS='0'
go run ./server/cmd/server
# 浏览器打开 http://localhost:8080（admin / admin123）
```

## 3. 前端开发模式

```powershell
cd web
npm install
npm run dev
```

生产构建：`npm run build`，产物 `web/dist` 由 `go:embed` 嵌入（CI 的 docker 工作流自动完成）。

## 4. 测试

```bash
# Go 全量（httptest 内嵌 mock，无需起任何服务）
go test ./server/... -count=1

# 静态检查
go vet ./server/...
```

集成测试覆盖：登录鉴权、master 越权 403、admin 建号授权、dry-run 零写入、执行后 PATCH 体校验、五源解析、回退链顺序到百科命中、AI 缓存命中、Mox 520 退避、BW 跨源补空+防串卷、跳过已刮零请求、overrideMatch 语义、编辑器只提交改动字段、Mylar zip 合法、打标 dry-run、状态筛选 6 项、admin 删除保护。

## 5. 前端改动 → 生效链路

```text
web/src 改动 → node node_modules/vite/bin/vite.js build
           → 拷贝 web/dist → server/internal/webdist/dist（先删旧目录）
           → go build ./cmd/server（重新嵌入）
           → 重启后端进程
```

> 直接改前端跑 `npm run dev` 时代理到后端 8080，无需重嵌。

## 6. 本地构建 + 在线更新自测

本地构建随功能更新重建（前端产物嵌入 + 版本注入），一条命令：

```powershell
.\tools\build.ps1                          # Docker 版 → bin\komgabangumi.exe（默认 version=dev，检查更新不误报）
.\tools\build.ps1 -SkipWeb                 # web/src 没改时跳过前端构建
.\tools\build.ps1 -Run                     # 构建完直接启动（http://localhost:8080）
.\tools\build.ps1 -Target desktop          # 桌面版 → bin\desktop-win-x64\（默认 version=最新 v* tag）
.\tools\build.ps1 -Target all -Version 0.0.15
```

版本号决定更新检查的表现：`dev` / `0.0.0` 不提示；显式传更小的数字（如 `-Version 0.0.1`）→ 「发现新版本」；传更大的（如 `9.9.9`）→ 「已是最新」。

### Docker/Web 版全链路（离线，走 mock）

```powershell
node tools/mock-github.js                                        # 假 GitHub：docker-v9.9.9 / v9.9.9 + 资产 zip
$env:GITHUB_TAGS_API='http://127.0.0.1:25693'                    # 后端检查指向 mock（裸 base 自动补 /repos/... 路径）
.\tools\build.ps1 -Version 0.0.1 -Run                            # 注入低版本并启动
# Web 设置页 → 软件更新 → 立即检查 → 「发现新版本 v9.9.9」横幅 + compose pull 引导
```

改 `MOCK_LATEST_DOCKER` 控制返回的最新 tag；不设 `GITHUB_TAGS_API` 则请求真实 GitHub API。

### 桌面版检查链路（离线，走 mock）

```powershell
node tools/mock-github.js
$env:KOMGASCRAPE_RELEASES_API='http://127.0.0.1:25693'
.\tools\build.ps1 -Target desktop -Version 0.0.15
# 运行 bin\desktop-win-x64\KomgaScraper.exe → 设置 → 软件更新 → 检查更新 → 发现 v9.9.9
```

> ⚠ mock 资产 zip 只含标记文件（不是可运行程序）：走 mock 时只测「检查/提示」，别点「下载并安装」。
> 桌面版**完整自更新**（下载 → 原地替换 → 重启）用真实 GitHub 测：`-Version 0.0.15`（低于线上最新 v0.0.16）
> 构建后直接运行，检查更新 → 下载并安装 → 程序自动重启为 v0.0.16，数据目录不受影响。
