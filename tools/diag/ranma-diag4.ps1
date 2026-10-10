# PS5.1-compatible, ASCII-only source. READ-ONLY: only GET requests (Bangumi public API).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$out = New-Object System.Text.StringBuilder
function Log([string]$s) { [void]$out.AppendLine($s) }

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.Proxy = New-Object System.Net.WebProxy('http://127.0.0.1:7890')
$handler.UseProxy = $true
$b = New-Object System.Net.Http.HttpClient($handler)
$b.Timeout = [TimeSpan]::FromSeconds(60)
$b.DefaultRequestHeaders.Add('User-Agent', 'KomgaScraper/1.0 (diag)')
$b.DefaultRequestHeaders.Add('Accept', 'application/json')

function GetJson([System.Net.Http.HttpClient]$c, [string]$url) {
    $resp = $c.GetAsync($url).GetAwaiter().GetResult()
    $bytes = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    return ([System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
}

$rel = GetJson $b 'https://api.bgm.tv/v0/subjects/5239/subjects'
Log ('RELATED=' + $rel.Count)
$i = 0
foreach ($r in $rel) {
    Log ('rel[' + $i + '] relation=' + $r.relation + ' id=' + $r.id + ' name_cn=' + $r.name_cn + ' name=' + $r.name + ' img=' + $r.images.large)
    $i++
}
# main subject too
$main = GetJson $b 'https://api.bgm.tv/v0/subjects/5239'
Log ('MAIN name_cn=' + $main.name_cn + ' name=' + $main.name + ' img=' + $main.images.large)
$out.ToString() | Set-Content -Path 'KomgaScraper\tools\diag\ranma-step4.txt' -Encoding UTF8
Write-Host 'STEP4 done'
