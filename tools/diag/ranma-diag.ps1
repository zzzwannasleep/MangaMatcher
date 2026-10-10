# PS5.1-compatible, ASCII-only source. CJK built via [char] codes. READ-ONLY: only GET requests.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$out = New-Object System.Text.StringBuilder
function Log([string]$s) { [void]$out.AppendLine($s) }

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.Proxy = New-Object System.Net.WebProxy('http://127.0.0.1:7890')
$handler.UseProxy = $true
$k = New-Object System.Net.Http.HttpClient($handler)
$k.Timeout = [TimeSpan]::FromSeconds(120)
$k.DefaultRequestHeaders.Add('X-API-Key', 'f15598270ba94d6480a6cd415163bbcf')

function GetJson([System.Net.Http.HttpClient]$c, [string]$url) {
    $resp = $c.GetAsync($url).GetAwaiter().GetResult()
    $bytes = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    return ([System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
}

# 1. find ranma series (search query uses unicode escapes)
$ma = [char]0x4E71  # ma
$query = [string][char]0x4E71 + [char]0x9A6C
$url = 'https://comic.wenjian.de/api/v1/series?search=' + [System.Uri]::EscapeDataString($query) + '&size=40&page=0'
$r = GetJson $k $url
Log ('SEARCH-HITS=' + $r.totalElements)
foreach ($s in $r.content) {
    $title = ''
    if ($s.metadata -and $s.metadata.title) { $title = $s.metadata.title }
    $lib = ''
    if ($s.libraryId) { $lib = $s.libraryId }
    Log ('HIT id=' + $s.id + ' library=' + $lib + ' books=' + $s.booksCount + ' name=' + $s.name + ' ||| title=' + $title)
}
$out.ToString() | Set-Content -Path 'KomgaScraper\tools\diag\ranma-step1.txt' -Encoding ASCII
Write-Host 'STEP1 done'
