# Catch-up, links and busy files through the real exe on loopback: serve + proxy + --link copy. ASCII only.
# First pass, then changes on both sides, then a /SYNC pass. Writes only into its temp folder.
param([string]$Exe = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\Hopover.exe'), [string]$T = (Join-Path $env:TEMP "hopover-sync-test-$PID"))
$ErrorActionPreference = 'Stop'
$fails = 0
function Ok([string]$Name, [bool]$Cond, [string]$Info = '') {
    Write-Host ('{0} {1} {2}' -f $(if ($Cond) { 'PASS' } else { 'FAIL' }), $Name, $Info)
    if (-not $Cond) { $script:fails++ }
}
function Put([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Text)
}
function Read-One([string]$Path) { if (Test-Path -LiteralPath $Path) { [IO.File]::ReadAllText($Path) } else { $null } }

$src = "$T\src\a"; $dst = "$T\dst\a"; $hold = "$T\held"; $slog = "$T\synclog"; $store = "$T\store"
New-Item -ItemType Directory -Path $store, $slog -Force | Out-Null
Put "$src\keep.txt" 'keep'
Put "$src\changed-old.txt" 'v1'
Put "$src\changed-new.txt" 'v1'
Put "$src\both.txt" 'v1'
Put "$src\gone.txt" 'bye'
Put "$src\gonedir\sub\x.txt" 'bye'
Put "$src\gonedir\sub\y.txt" 'bye'
Put "$src\excluded\skip.txt" 'never'
Put "$src\locked.bin" 'locked'
Put "$src\packages\real\index.js" 'real'
Put "$src\same.txt" 'same'
Put "$src\config.json" 'my real settings'
Put "$src\busy.txt" 'from the old PC'
# The new PC is not empty: a fresh default (newer than the real file), the same file, a file of its own, a file in use.
Start-Sleep -Seconds 3
Put "$dst\config.json" 'fresh default'
Put "$dst\own.txt" 'only here'
Put "$dst\busy.txt" 'held open here'
Put "$dst\same.txt" 'same'
(Get-Item "$dst\same.txt").LastWriteTimeUtc = (Get-Item "$src\same.txt").LastWriteTimeUtc
New-Item -ItemType Directory -Path "$src\node_modules" -Force | Out-Null
$null = cmd /c mklink /J "$src\node_modules\pkg" "$src\packages\real"
$null = cmd /c mklink "$src\rel-link.txt" "keep.txt" 2>$null
$haveSym = Test-Path "$src\rel-link.txt"

$code = '4-alpha-bravo-cafe'; $port = 47870
$serve = Start-Process $Exe -ArgumentList "--link serve --store `"$store`" --code $code --tcp $port --udp $($port + 1) --rendezvous none --name TESTPC --root `"drive-z=$T\src`"" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$T\serve.txt" -RedirectStandardError "$T\serve.err"
Start-Sleep -Seconds 2
$proxy = Start-Process $Exe -ArgumentList "--link proxy --mode addr --addr 127.0.0.1:$port --code $code" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$T\proxy.txt" -RedirectStandardError "$T\proxy.err"
$until = (Get-Date).AddSeconds(30); $line = $null
while (-not $line -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 300; $line = Get-Content "$T\proxy.txt" -ErrorAction SilentlyContinue | Where-Object { $_ -like '##LINK proxy*' } | Select-Object -First 1 }
if (-not $line) { throw "no proxy: $(Get-Content "$T\proxy.txt" -Raw)" }
$p = $line -split ' '
$env:HOPOVER_LINK = "$($p[2]):$($p[3])"

try {
    # An app holds this file with no sharing at all.
    $lock = [IO.File]::Open("$src\locked.bin", 'Open', 'ReadWrite', 'None')
    # A preview first: the same decisions, and no file moves.
    $pv = "$T\pvlog"; New-Item -ItemType Directory -Path $pv -Force | Out-Null
    $before = @(Get-ChildItem $dst -Recurse -Force | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join "`n"
    & $Exe --link copy '\\link\hopover-z\a' $dst /E /XD excluded /MT:4 /R:0 /W:0 /L /PREVIEW /FIRST "/HOLD:$hold" "/SYNCLOG:$pv" | Out-Null
    $after = @(Get-ChildItem $dst -Recurse -Force | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join "`n"
    Ok 'preview: nothing moved' ($before -eq $after -and -not (Test-Path $hold))
    $pl = @(Get-ChildItem "$pv\*.txt" | ForEach-Object { Get-Content $_.FullName })
    Ok 'preview: names the file it would replace' (@($pl | Where-Object { $_ -like 'REPLACED*config.json' }).Count -eq 1) "$($pl.Count) lines"
    Ok 'preview: the same file is no replacement' (-not ($pl | Where-Object { $_ -like 'REPLACED*same.txt' }))
    Ok 'preview: names the links' (@($pl | Where-Object { $_ -like 'LINK*pkg*' }).Count -eq 1)
    $busy = [IO.File]::Open("$dst\busy.txt", 'Open', 'ReadWrite', 'None')
    $out = & $Exe --link copy '\\link\hopover-z\a' $dst /E /XD excluded /MT:4 /R:0 /W:0 /FIRST "/HOLD:$hold" "/SYNCLOG:$slog" | Out-String
    $rc = $LASTEXITCODE
    $busy.Close()
    Ok 'first pass: exit 1, no failure' ($rc -eq 1) "exit $rc"
    $b = "$hold\new-pc-before\" + $dst.Substring(0, 1) + $dst.Substring(2)
    Ok 'first pass: the old PC wins over a newer default' ((Read-One "$dst\config.json") -eq 'my real settings')
    Ok 'first pass: the default is held, not lost' ((Read-One "$b\config.json") -eq 'fresh default')
    Ok 'first pass: same file left alone' (-not (Test-Path "$b\same.txt"))
    Ok 'first pass: a file only here stays' ((Read-One "$dst\own.txt") -eq 'only here')
    Ok 'first pass: a file in use here does not fail the chunk' ($out -match 'in use on this PC' -and (Read-One "$dst\busy.txt") -eq 'held open here')
    Ok 'locked file: reported, chunk not failed' ($out -match 'ERROR not copied \(locked')
    Ok 'first pass: file arrived' ((Read-One "$dst\keep.txt") -eq 'keep')
    $j = Get-Item -LiteralPath "$dst\node_modules\pkg" -Force
    Ok 'junction made as junction' ($j.LinkType -eq 'Junction' -and "$($j.Target)" -like '*\src\a\packages\real') "$($j.LinkType) $($j.Target)"
    if ($haveSym) {
        $l = Get-Item -LiteralPath "$dst\rel-link.txt" -Force
        Ok 'relative symlink made as link' ($l.LinkType -eq 'SymbolicLink' -and "$($l.Target)" -eq 'keep.txt') "$($l.LinkType) $($l.Target)"
    } else { Write-Host 'SKIP symlink (needs admin or Developer Mode)' }
    $lock.Close()

    # Second run without changes: the link stays, nothing fails.
    Remove-Item "$slog\*" -Force
    & $Exe --link copy '\\link\hopover-z\a' $dst /E /XD excluded /MT:4 /R:0 /W:0 /FIRST "/HOLD:$hold" "/SYNCLOG:$slog" | Out-Null
    Ok 'second pass: exit below 8' ($LASTEXITCODE -lt 8) "exit $LASTEXITCODE"
    Ok 'second pass: the file in use now arrives' ((Read-One "$dst\busy.txt") -eq 'from the old PC')
    Ok 'second pass: the first run did not repeat' (@(Get-ChildItem "$b\config.json*").Count -eq 1)
    Remove-Item "$slog\*" -Force

    Start-Sleep -Seconds 3
    $base = (Get-Date).ToUniversalTime().Ticks
    Start-Sleep -Seconds 3

    # The old PC stays in use.
    Put "$src\changed-old.txt" 'v2-old'
    Put "$src\both.txt" 'v2-old'
    Put "$src\new-on-old.txt" 'new'
    Put "$src\newdir\repo\.git\HEAD" 'ref'
    Remove-Item "$src\gone.txt"
    Remove-Item "$src\gonedir" -Recurse
    # The new PC is in use too. Over 2 seconds later: same size and same time count as the same file.
    Start-Sleep -Seconds 3
    Put "$dst\changed-new.txt" 'v2-new'
    Put "$dst\both.txt" 'v2-new'
    Put "$dst\new-on-new.txt" 'mine'
    Put "$dst\gonedir\sub\y-new.txt" 'mine too'
    # An installer on the new PC lays down a file with an old date: created now, written long ago.
    Put "$dst\tarball.js" 'from a tarball'
    (Get-Item "$dst\tarball.js").LastWriteTime = [datetime]'1985-10-26'
    # What the old PC's scan leaves out stays too.
    Put "$dst\excluded\here.txt" 'stays'

    $pv2 = "$T\pvlog2"; New-Item -ItemType Directory -Path $pv2 -Force | Out-Null
    & $Exe --link copy '\\link\hopover-z\a' $dst /E /XD excluded /MT:4 /R:0 /W:0 /L /PREVIEW "/SYNC:$base" "/HOLD:$hold" "/SYNCLOG:$pv2" | Out-Null
    $pl = @(Get-ChildItem "$pv2\*.txt" | ForEach-Object { Get-Content $_.FullName })
    Ok 'catch-up preview: clash and removals listed, nothing moved' (@($pl | Where-Object { $_ -like 'CONFLICT*both.txt' }).Count -eq 1 -and @($pl | Where-Object { $_ -like 'REMOVED*' }).Count -ge 3 -and (Test-Path "$dst\gone.txt") -and (Test-Path "$dst\gonedir\sub\x.txt")) "$($pl.Count) lines"
    & $Exe --link copy '\\link\hopover-z\a' $dst /E /XD excluded /MT:4 /R:0 /W:0 "/SYNC:$base" "/HOLD:$hold" "/SYNCLOG:$slog" | Out-Null
    Ok 'catch-up: exit below 8' ($LASTEXITCODE -lt 8) "exit $LASTEXITCODE"
    Ok 'changed on old only: updates' ((Read-One "$dst\changed-old.txt") -eq 'v2-old')
    Ok 'changed on new only: stays' ((Read-One "$dst\changed-new.txt") -eq 'v2-new')
    Ok 'changed on both: new stays' ((Read-One "$dst\both.txt") -eq 'v2-new')
    $h = "$hold\old-pc-version\" + $dst.Substring(0, 1) + $dst.Substring(2)
    Ok 'changed on both: old version held' ((Read-One "$h\both.txt") -eq 'v2-old')
    Ok 'new on old: arrives' ((Read-One "$dst\new-on-old.txt") -eq 'new')
    Ok 'new repo on old: arrives' ((Read-One "$dst\newdir\repo\.git\HEAD") -eq 'ref')
    Ok 'new on new: stays' ((Read-One "$dst\new-on-new.txt") -eq 'mine')
    Ok 'old date, made here: stays' ((Read-One "$dst\tarball.js") -eq 'from a tarball')
    Ok 'excluded folder: stays' ((Read-One "$dst\excluded\here.txt") -eq 'stays')
    Ok 'gone on old: leaves the tree' (-not (Test-Path "$dst\gone.txt"))
    $r = "$hold\removed-on-old-pc\" + $dst.Substring(0, 1) + $dst.Substring(2)
    Ok 'gone on old: held, not deleted' ((Read-One "$r\gone.txt") -eq 'bye')
    Ok 'gone folder: files held' ((Read-One "$r\gonedir\sub\x.txt") -eq 'bye')
    Ok 'gone folder: a new file here keeps it' ((Read-One "$dst\gonedir\sub\y-new.txt") -eq 'mine too')
    Ok 'locked file: the catch-up fetches it' ((Read-One "$dst\locked.bin") -eq 'locked')
    Ok 'junction still there' ((Get-Item -LiteralPath "$dst\node_modules\pkg" -Force).LinkType -eq 'Junction')
    $log = @(Get-ChildItem "$slog\*.txt" | ForEach-Object { Get-Content $_.FullName })
    Ok 'log lists the clash and the removals' (@($log | Where-Object { $_ -like 'CONFLICT*both.txt' }).Count -eq 1 -and @($log | Where-Object { $_ -like 'REMOVED*' }).Count -ge 3) "$($log.Count) lines"

    # This server was started without --engine.
    $o = & $Exe --link refresh 2>&1 | Out-String
    Ok 'refresh without an engine: a clear error, no hang' ($LASTEXITCODE -ne 0 -and $o -match 'cannot prepare') $o.Trim()
} finally {
    foreach ($x in $proxy, $serve) { if ($x -and -not $x.HasExited) { & taskkill /PID $x.Id /T /F 2>$null | Out-Null } }
    if ($lock) { $lock.Dispose() }
}
Write-Host "##RESULT fails=$fails folder=$T"
if ($fails) { exit 1 }
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
