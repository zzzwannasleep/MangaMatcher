# Analysis 2: library 0RVS31RFXZVV1 (daily manga) flattening damage + trailing bracket coverage
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
    $r = [System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    foreach ($c in $r.content) { [void]$all.Add($c) }
    $page++
    if ($page -ge $r.totalPages) { break }
}

$lib = '0RVS31RFXZVV1'
$inLib = @($all | Where-Object { $_.libraryId -eq $lib })
Log ("IN-LIB=" + $inLib.Count)

# trailing full-width bracket coverage over ALL libraries
$trailCount = 0
foreach ($s in $all) {
    if ($s.name -match ($pattern + '$')) { $trailCount++ }
}
Log ("ALL-LIBS-TRAILING-BRACKET=" + $trailCount)

# title set / dup analysis in daily lib
$titled = @($inLib | Where-Object { $_.metadata.title })
Log ("TITLED=" + $titled.Count)
$groups = $titled | Group-Object { $_.metadata.title } | Where-Object { $_.Count -gt 1 }
Log ("DUP-TITLE-GROUPS=" + @($groups).Count + " SERIES-IN-DUPS=" + (($groups | Measure-Object -Sum Count).Sum))
Log "--- first 25 dup groups (folder names) ---"
foreach ($g in ($groups | Select-Object -First 25)) {
    Log ('[' + $g.Count + 'x] title=' + $g.Name)
    foreach ($m in $g.Group) { Log ('    folder: ' + $m.name) }
}

# sample name ||| title
Log "--- 60 samples name ||| title (daily lib) ---"
$i = 0
foreach ($d in ($titled | Where-Object { $_.metadata.title -ne $_.name })) {
    if ($i -ge 60) { break }
    Log ($d.name + ' ||| ' + $d.metadata.title)
    $i++
}

$enc = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText('D:\WorkProgram\komgabangumidocker\KomgaScraper\tools\libanalyze2-report.txt', $out.ToString(), $enc)
'DONE'