# 本地一键构建 —— 前端产物嵌入 + 版本注入，随时随功能更新重建，用于本地自测（含在线更新链路）。
#
# 用法：
#   .\tools\build.ps1                          # Docker 版：web 构建 → 嵌入 → go build → bin\komgabangumi.exe（version=dev）
#   .\tools\build.ps1 -Version 0.0.1           # 同上，注入 0.0.1（配合 mock-github 测「发现新版本」提示）
#   .\tools\build.ps1 -Target desktop          # 桌面版：dotnet publish → bin\desktop-win-x64\（version=最新 v* tag）
#   .\tools\build.ps1 -Target all -Version 0.0.15
#   .\tools\build.ps1 -SkipWeb                 # web/src 没改时跳过前端构建（更快）
#   .\tools\build.ps1 -Run                     # 构建完立即启动 Docker 版后端
#
# 版本语义（与两端更新检查对齐）：
#   dev / 0.0.0            开发构建 → 检查更新时「不提示」（除非显式 -Version 传了可比数字）
#   -Version 0.0.1         比 mock/线上的最新版低 → 出「发现新版本」
#   -Version 9.9.9         比 mock/线上的最新版高 → 出「已是最新」
param(
    [ValidateSet('docker', 'desktop', 'all')][string]$Target = 'docker',
    [string]$Version = '',
    [switch]$SkipWeb,
    [switch]$Run
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# ---------- 0. 工具链定位（go 不在 PATH 时用仓库自带 .toolchain） ----------
$goEnv = @{}
$go = (Get-Command go -ErrorAction SilentlyContinue).Source
if (-not $go -and (Test-Path "$root\.toolchain\go\bin\go.exe")) {
    $go = "$root\.toolchain\go\bin\go.exe"
    $goEnv['GOROOT'] = "$root\.toolchain\go"
    $goEnv['GOCACHE'] = "$root\.toolchain\gocache"
    $goEnv['GOPATH'] = "$root\.toolchain\gopath"
    $goEnv['GOPROXY'] = 'off'
    Write-Host "使用仓库自带工具链: $go"
}
if (-not $go -and $Target -in 'docker', 'all') { throw "找不到 go（PATH 无 go 且 .toolchain\go 不存在）" }

# ---------- 1. 前端构建 + 嵌入（docker 目标） ----------
if ($Target -in 'docker', 'all') {
    # dev = 开发构建 → 更新检查不误报；要测提示请显式传数字版本（不动 $Version，供 desktop 目标继续推导）
    $dockerVersion = if ($Version) { $Version } else { 'dev' }
    if (-not $SkipWeb) {
        Step '前端构建（npm run build）'
        Push-Location "$root\web"
        if (-not (Test-Path node_modules)) {
            Write-Host 'node_modules 缺失，先 npm install …'
            npm install --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'npm install 失败' }
        }
        npm run build
        $code = $LASTEXITCODE
        Pop-Location
        if ($code -ne 0) { throw 'web 构建失败' }
    } else {
        Step '跳过前端构建（-SkipWeb）'
        if (-not (Test-Path "$root\web\dist\index.html")) { throw 'web\dist 不存在，不能 -SkipWeb' }
    }

    Step '嵌入前端产物（web/dist → server/internal/webdist/dist）'
    Remove-Item "$root\server\internal\webdist\dist" -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item "$root\web\dist" "$root\server\internal\webdist\dist" -Recurse

    Step "Go 构建（version=$dockerVersion）"
    Push-Location "$root\server"
    foreach ($k in $goEnv.Keys) { Set-Item -Path "env:$k" -Value $goEnv[$k] }
    $env:GOTMPDIR = "$root\.toolchain\tmp"   # 系统 Temp 可能被安全软件/策略拒绝，用仓库内 tmp
    New-Item -ItemType Directory -Force -Path $env:GOTMPDIR | Out-Null
    & $go build -trimpath -ldflags "-s -w -X main.version=$dockerVersion" -o "$root\bin\komgabangumi.exe" ./cmd/server
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { throw 'go build 失败' }
    Write-Host "Docker 版已构建: bin\komgabangumi.exe (version=$dockerVersion)"

    if ($Run) {
        Step '启动后端（DATA_DIR=./server/data, PORT=8080）'
        $env:DATA_DIR = './server/data'; $env:PORT = '8080'
        Start-Process -FilePath "$root\bin\komgabangumi.exe" -WorkingDirectory $root
        Write-Host '已启动 → http://localhost:8080（admin / admin123）'
    }
}

# ---------- 2. 桌面版 publish（desktop 目标） ----------
if ($Target -in 'desktop', 'all') {
    if (-not $Version) {
        # 默认 = 仓库最新 v* tag（与线上最新一致 → 检查显示「已是最新」）
        $t = git -C $root tag | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.TrimStart('v') } -Descending | Select-Object -First 1
        $Version = if ($t) { $t.TrimStart('v') } else { '0.0.0' }
    }
    Step "桌面版 publish（version=$Version → bin\desktop-win-x64）"
    dotnet publish "$root\src\KomgaScraper.App\KomgaScraper.App.csproj" `
        -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
        -p:Version=$Version -p:InformationalVersion=$Version `
        -o "$root\bin\desktop-win-x64"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }
    Write-Host "桌面版已构建: bin\desktop-win-x64\KomgaScraper.exe (version=$Version)"
}

$sw.Stop()
Write-Host "`n✔ 完成，用时 $([int]$sw.Elapsed.TotalSeconds)s" -ForegroundColor Green
