# PS5.1-compatible, ASCII-only source. READ-ONLY: only GET/POST-query requests.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$out = New-Object System.Text.StringBuilder
function Log([string]$s) { [void]$out.AppendLine($s) }

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.Proxy = New-Object System.Net.WebProxy('http://127.0.0.1:7890')
$handler.UseProxy = $true
$k = New-Object System.Net.Http.HttpClient($handler)
$k.Timeout = [TimeSpan]::FromSeconds(180)
$k.DefaultRequestHeaders.Add('X-API-Key', 'f15598270ba94d6480a6cd415163bbcf')

function GetJson([System.Net.Http.HttpClient]$c, [string]$url) {
    $resp = $c.GetAsync($url).GetAwaiter().GetResult()
    $bytes = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    return ([System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
}
function PostBooksList([System.Net.Http.HttpClient]$c, [string]$seriesId) {
    $payload = '{"condition": {"allOf": [{"seriesId": {"operator": "is", "value": "' + $seriesId + '"}}, {"deleted": {"operator": "isFalse"}}]}}'
    $content = New-Object System.Net.Http.StringContent($payload, [System.Text.Encoding]::UTF8, 'application/json')
    $resp = $c.PostAsync('https://comic.wenjian.de/api/v1/books/list?unpaged=true&sort=metadata.numberSort,asc', $content).GetAwaiter().GetResult()
    $bytes = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    return ([System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
}

$ids = @('0RVS45T1SZZZ2','0RVS45QZ1ZW8T','0RVS45NP9ZKR5','0RVS45WEXZSSC')
foreach ($id in $ids) {
    Log ('=== SERIES ' + $id)
    $bp = PostBooksList $k $id
    Log ('    books=' + $bp.content.Count)
    $i = 0
    foreach ($b in $bp.content) {
        $mt = ''; if ($b.metadata) { $mt = $b.metadata.title }
        Log ('    book[' + $i + '] number=' + $b.metadata.number + ' numberSort=' + $b.metadata.numberSort + ' name=' + $b.name + ' | mtitle=' + $mt)
        $th = GetJson $k ('https://comic.wenjian.de/api/v1/books/' + $b.id + '/thumbnails')
        if ($th.Count -gt 0) {
            foreach ($t in $th) { Log ('        thumb type=' + $t.type + ' selected=' + $t.selected + ' size=' + $t.fileSize) }
        }
        $i++
        if ($i -ge 40) { Log '    ... (truncated at 40)'; break }
    }
}
$out.ToString() | Set-Content -Path 'KomgaScraper\tools\diag\ranma-step3.txt' -Encoding UTF8
Write-Host 'STEP3 done'
