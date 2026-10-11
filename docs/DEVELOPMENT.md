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

```powershell
node tools/mock-komga.js
node tools/mock-bangumi.js
node tools/mock-ai.js
node tools/mock-mox.js
node tools/mock-mangadex.js
node tools/mock-baike.js
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
