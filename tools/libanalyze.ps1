# PS5.1-compatible, ASCII-only source. CJK built via [char] codes.
# Pulls all Komga series, analyzes bracket tokens / flattened titles / existing tags.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$out = New-Object System.Text.StringBuilder
function Log([string]$s) { [void]$out.AppendLine($s) }

$LP = [char]0xFF08; $RP = [char]0xFF09
$pattern = '[(' + $LP + ']([^(' + $LP + $RP + ')]{1,40})[)' + $RP + ']'

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.Proxy = New-Object System.Net.WebProxy('http://127.0.0.1:7890')
$handler.UseProxy = $true
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(180)
$client.DefaultRequestHeaders.Add('X-API-Key', 'f15598270ba94d6480a6cd415163bbcf')

$all = New-Object System.Collections.ArrayList
$page = 0
while ($true) {
    $resp = $client.GetAsync(("https://comic.wenjian.de/api/v1/series?page=$page&size=500&sort=name,asc")).GetAwaiter().GetResult()
    $bytes = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    $json = [System.Text.Encoding]::UTF8.GetString($bytes)
    $r = $json | ConvertFrom-Json
    foreach ($c in $r.content) { [void]$all.Add($c) }
    $page++
    if ($page -ge $r.totalPages) { break }
}
Log ("TOTAL=" + $all.Count)

# bracket tokens overall
$tokens = @{}
foreach ($s in $all) {
    $n = $s.name
    if (-not $n) { continue }
    $ms = [regex]::Matches($n, $pattern)
    foreach ($m in $ms) {
        $t = $m.Groups[1].Value.Trim()
        if ($t.Length -gt 0) {
            if ($tokens.ContainsKey($t)) { $tokens[$t]++ } else { $tokens[$t] = 1 }
        }
    }
}
Log ("BRACKET-TOKENS-UNIQUE=" + $tokens.Count)
Log "--- TOP 140 bracket tokens ---"
$tokens.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 140 | ForEach-Object {
    Log ('{0,5}  {1}' -f $_.Value, $_.Key)
}

# flattened titles
$diff = @($all | Where-Object { $_.metadata.title -and $_.metadata.title -ne $_.name })
Log ("TITLE-DIFF-COUNT=" + $diff.Count)
Log "--- first 60 name ||| title ---"
$i = 0
foreach ($d in $diff) { if ($i -ge 60) { break }; Log ($d.name + ' ||| ' + $d.metadata.title); $i++ }

# existing tags
$tags = @{}
$withTags = 0
foreach ($s in $all) {
    $t2 = $s.metadata.tags
    if ($t2 -and $t2.Count -gt 0) {
        $withTags++
        foreach ($t in $t2) {
            if ($tags.ContainsKey($t)) { $tags[$t]++ } else { $tags[$t] = 1 }
        }
    }
}
Log ("SERIES-WITH-TAGS=$withTags UNIQUE-TAGS=" + $tags.Count)
Log "--- TOP 60 tags ---"
$tags.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 60 | ForEach-Object {
    Log ('{0,5}  {1}' -f $_.Value, $_.Key)
}

# tail tokens (non-bracket trailing annotation)
Log "--- top 60 non-bracket tail tokens ---"
$tail = @{}
foreach ($s in $all) {
    $n = $s.name
    if (-not $n) { continue }
    $stripped = [regex]::Replace($n, $pattern, ' ')
    $stripped = [regex]::Replace($stripped, '\[[^\]]*\]', ' ')
    $parts = ($stripped.Trim() -split '\s+')
    if ($parts.Count -ge 2) {
        $last = $parts[$parts.Count - 1]
        if ($last -notmatch '^(?:[0-9]+|vol)' -and $last.Length -ge 2 -and $last.Length -le 10) {
            if ($tail.ContainsKey($last)) { $tail[$last]++ } else { $tail[$last] = 1 }
        }
    }
}
$tail.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 60 | ForEach-Object {
    Log ('{0,5}  {1}' -f $_.Value, $_.Key)
}

# per-library counts
Log "--- libraries ---"
$libs = @{}
foreach ($s in $all) {
    if ($libs.ContainsKey($s.libraryId)) { $libs[$s.libraryId]++ } else { $libs[$s.libraryId] = 1 }
}
$libs.GetEnumerator() | ForEach-Object { Log ('{0}  {1}' -f $_.Key, $_.Value) }

$enc = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText('D:\WorkProgram\komgabangumidocker\KomgaScraper\tools\libanalyze-report.txt', $out.ToString(), $enc)
'DONE'