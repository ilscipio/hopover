# End-to-end test of the link: serve + proxy + the real copy path (Copy-Plan -> RoboPool -> Hopover.exe --link copy).
# Runs once over 127.0.0.1 and once over this PC's LAN IP. Needs no admin rights and no second PC.
# Test data lives in %TEMP%\hopover-linktest and is deleted at the end. ASCII only (PS 5.1 reads it as ANSI).
param([int]$BigMB = 3072, [string]$RepoDir = 'C:\java\workspace\aivory-sales-manager', [switch]$KeepData, [switch]$SkipResume)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$Exe = Join-Path $Root 'dist\Hopover.exe'
$T = Join-Path $env:TEMP 'hopover-linktest'
$Fails = 0
function Ok([string]$Name, [bool]$Cond, [string]$Info = '') {
    if ($Cond) { Write-Host "PASS $Name $Info" -ForegroundColor Green } else { Write-Host "FAIL $Name $Info" -ForegroundColor Red; $script:Fails++ }
}
function LongP([string]$p) { if ($p.StartsWith('\\?\')) { $p } else { '\\?\' + $p } }

# ---------- test data ----------
function New-TestData([string]$Src) {
    New-Item -ItemType Directory -Path $Src -Force | Out-Null
    # 1. A real repo (small parts only): about 0.5 GB of real files.
    $repo = Join-Path $Src 'repo'
    $null = robocopy $RepoDir $repo /E /XD .git video-ads tmp_work venv .playwright-mcp node_modules __pycache__ /XJ /R:0 /W:0 /NFL /NDL /NJH /NJS /NP
    # 2. A file of random-looking data, every 1 MiB block different.
    $big = Join-Path $Src 'big.bin'
    $rnd = New-Object byte[] (1MB)
    (New-Object Random 42).NextBytes($rnd)
    $fs = [IO.File]::Create($big)
    for ($i = 0; $i -lt $BigMB; $i++) { [BitConverter]::GetBytes([int64]$i).CopyTo($rnd, 0); $fs.Write($rnd, 0, $rnd.Length) }
    $fs.Close()
    # 3. Long path (over 260 characters).
    $deep = $Src
    1..7 | ForEach-Object { $deep = Join-Path $deep ('segment-number-' + $_ + '-with-a-long-name-0123456789') }
    [IO.Directory]::CreateDirectory((LongP $deep)) | Out-Null
    [IO.File]::WriteAllText((LongP (Join-Path $deep 'deep-file.txt')), 'deep content')
    Ok 'data: long path' ((Join-Path $deep 'deep-file.txt').Length -gt 260) "($((Join-Path $deep 'deep-file.txt').Length) chars)"
    # 4. Unicode names (made from code points: this script stays ASCII).
    $u1 = [regex]::Unescape('\u00dcbergr\u00f6\u00dfen-\u00e4\u00f6\u00fc.txt')
    $u2 = [regex]::Unescape('\u65e5\u672c\u8a9e\u30d5\u30a1\u30a4\u30eb.txt')
    $ud = Join-Path $Src ([regex]::Unescape('\u30c7\u30a3\u30ec\u30af\u30c8\u30ea'))
    New-Item -ItemType Directory -Path $ud -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $ud $u1), 'unicode one')
    [IO.File]::WriteAllText((Join-Path $ud $u2), 'unicode two')
    # 5. Read-only and hidden files, an empty folder, a junction.
    $special = Join-Path $Src 'special'
    New-Item -ItemType Directory -Path $special, (Join-Path $special 'empty') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $special 'readonly.txt'), 'ro'); (Get-Item (Join-Path $special 'readonly.txt')).IsReadOnly = $true
    [IO.File]::WriteAllText((Join-Path $special 'hidden.txt'), 'hid'); (Get-Item (Join-Path $special 'hidden.txt')).Attributes = 'Hidden'
    [IO.File]::WriteAllText((Join-Path $special 'old-time.txt'), 'old'); (Get-Item (Join-Path $special 'old-time.txt')).LastWriteTime = [datetime]'2019-03-04 05:06:07'
    $null = cmd /c mklink /J (Join-Path $special 'junction') (Join-Path $repo 'data')
    [IO.File]::WriteAllText((Join-Path $Src 'root-file.txt'), 'root file')
}

function Get-FileList([string]$Base) {
    # Every file below Base, without following junctions. Relative path -> FileInfo.
    $map = @{}
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($Base)
    while ($stack.Count) {
        $d = $stack.Pop()
        $di = New-Object IO.DirectoryInfo (LongP $d)
        foreach ($e in $di.EnumerateFileSystemInfos()) {
            if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            if ($e.Attributes -band [IO.FileAttributes]::Directory) { $stack.Push($e.FullName.Replace('\\?\', '')) }
            else { $map[$e.FullName.Replace('\\?\', '').Substring($Base.Length)] = $e }
        }
    }
    $map
}

function Get-Sha([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $fs = New-Object IO.FileStream (LongP $Path), 'Open', 'Read', 'ReadWrite', 1MB
    try { [BitConverter]::ToString($sha.ComputeHash($fs)) } finally { $fs.Close() }
}

function Compare-Trees([string]$Src, [string]$Dst, [string]$Label, [string[]]$IgnoreDst = @()) {
    $a = Get-FileList $Src
    $b = Get-FileList $Dst
    $missing = 0; $badHash = 0; $badTime = 0; $badAttr = 0; $bytes = [int64]0
    foreach ($k in $a.Keys) {
        if ($k -like '*.hopover-part*') { continue }
        if (-not $b.ContainsKey($k)) { $missing++; Write-Host "  missing: $k"; continue }
        $s = $a[$k]; $d = $b[$k]
        $bytes += $s.Length
        if ($s.LastWriteTimeUtc.Ticks -ne $d.LastWriteTimeUtc.Ticks) { $badTime++; Write-Host "  time differs: $k" }
        $mask = [IO.FileAttributes]::ReadOnly -bor [IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System
        if (($s.Attributes -band $mask) -ne ($d.Attributes -band $mask)) { $badAttr++; Write-Host "  attributes differ: $k" }
        if ($s.Length -ne $d.Length -or (Get-Sha $s.FullName.Replace('\\?\', '')) -ne (Get-Sha $d.FullName.Replace('\\?\', ''))) { $badHash++; Write-Host "  hash differs: $k" }
    }
    Ok "$Label hashes" ($missing -eq 0 -and $badHash -eq 0) ("$($a.Count) files, {0:N2} GB, missing=$missing badHash=$badHash" -f ($bytes / 1GB))
    Ok "$Label timestamps" ($badTime -eq 0) "badTime=$badTime"
    Ok "$Label attributes (read-only, hidden)" ($badAttr -eq 0) "badAttr=$badAttr"
}

# ---------- link processes ----------
function Start-Quiet([string]$Args2, [string]$Out) {
    $q = Start-Process -FilePath $Exe -ArgumentList $Args2 -RedirectStandardOutput $Out -RedirectStandardError "$Out.err" -WindowStyle Hidden -PassThru; $null = $q.Handle; $q
}
function Wait-Line([string]$File, [string]$Pattern, [int]$Seconds = 30) {
    $until = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $until) {
        if (Test-Path $File) { $m = Select-String -Path $File -Pattern $Pattern | Select-Object -First 1; if ($m) { return $m.Line } }
        Start-Sleep -Milliseconds 200
    }
    $null
}
function Stop-Tree($p) { if ($p -and -not $p.HasExited) { & taskkill /PID $p.Id /T /F | Out-Null } }

function Start-Link([string]$Ip, [int]$Port, [string]$Tag) {
    $code = '3-alpha-bravo-cafe'
    $out = Join-Path $T "serve-$Tag.txt"
    $script:serve = Start-Quiet "--link serve --store `"$T\store`" --code $code --tcp $Port --udp $($Port + 1) --rendezvous none --name TESTPC" $out
    $null = Wait-Line $out 'listening'
    $pout = Join-Path $T "proxy-$Tag.txt"
    $script:proxy = Start-Quiet "--link proxy --mode addr --addr ${Ip}:$Port --code $code" $pout
    $line = Wait-Line $pout '##LINK proxy'
    if (-not $line) { throw "no proxy: $(Get-Content $pout -Raw) $(Get-Content "$pout.err" -Raw -ErrorAction SilentlyContinue)" }
    $parts = $line.Split(' ')
    $env:HOPOVER_LINK = "$($parts[2]):$($parts[3])"
    $env:HOPOVER_EXE = $Exe
    $path = Select-String -Path $pout -Pattern '##LINK path' | Select-Object -First 1
    Write-Host "  link up: $($path.Line)"
}
function Stop-Link { Stop-Tree $script:proxy; Stop-Tree $script:serve }

function Start-Copy([string]$Dst, [string]$Tag, [switch]$Recopy) {
    $out = Join-Path $T "copy-$Tag.txt"
    $a = "-NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\link-copy-run.ps1`" -Src `"$T\src`" -Dst `"$Dst`" -Store `"$T\store-copy-$Tag`" -BigFile `"$T\src\big.bin`""
    if ($Recopy) { $a += ' -Recopy' }
    $p = Start-Process powershell.exe -ArgumentList $a -RedirectStandardOutput $out -RedirectStandardError "$out.err" -WindowStyle Hidden -PassThru; $null = $p.Handle
    [pscustomobject]@{ Proc = $p; Out = $out; Clock = [Diagnostics.Stopwatch]::StartNew() }
}
function Wait-Copy($c, [int]$Seconds = 1800) {
    if (-not $c.Proc.WaitForExit($Seconds * 1000)) { Stop-Tree $c.Proc; throw 'copy timed out' }
    $c.Clock.Stop()
}
function Get-RunBytes($c) {
    $last = Select-String -Path $c.Out -Pattern '^##PROGRESS' | Select-Object -Last 1
    if ($last) { [int64]$last.Line.Split(' ')[3] } else { -1 }
}

# ---------- run ----------
if (Test-Path $T) { Remove-Item $T -Recurse -Force }
New-Item -ItemType Directory -Path $T, "$T\store" | Out-Null
try {
    Write-Host "Data: building test data in $T ..."
    $sw = [Diagnostics.Stopwatch]::StartNew()
    New-TestData "$T\src"
    $srcFiles = Get-FileList "$T\src"
    $srcBytes = [int64](($srcFiles.Values | Measure-Object Length -Sum).Sum)
    Write-Host ("  {0} files, {1:N2} GB in {2:N0} s" -f $srcFiles.Count, ($srcBytes / 1GB), $sw.Elapsed.TotalSeconds)
    [IO.File]::WriteAllText("$T\store\manifest.json", '{"Computer":"TESTPC"}')

    $lan = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -match '^(192\.168|10\.|172\.(1[6-9]|2\d|3[01]))\.' -and $_.InterfaceAlias -notmatch 'vEthernet|WSL|Loopback|Docker' } | Select-Object -First 1).IPAddress
    Write-Host "LAN IP: $lan"
    $round = 0
    foreach ($ip in @('127.0.0.1', $lan)) {
        if (-not $ip) { continue }
        $round++
        $tag = "r$round"
        $port = 47810 + 4 * $round
        Write-Host "`n===== transport via $ip ====="
        Start-Link $ip $port $tag
        try {
            $dst = Join-Path $T "dst-$tag"
            # Run 1: everything.
            $c = Start-Copy $dst "$tag-1"
            Wait-Copy $c
            $rb = Get-RunBytes $c
            Ok "[$ip] run 1 exit code" ($c.Proc.ExitCode -eq 0) "exit $($c.Proc.ExitCode)"
            $mbs = if ($c.Clock.Elapsed.TotalSeconds -gt 0) { $rb / 1MB / $c.Clock.Elapsed.TotalSeconds } else { 0 }
            Write-Host ("  copied {0:N2} GB in {1:N1} s = {2:N0} MB/s" -f ($rb / 1GB), $c.Clock.Elapsed.TotalSeconds, $mbs)
            Compare-Trees "$T\src" $dst "[$ip] run 1"
            Ok "[$ip] junction not followed" (-not (Test-Path (Join-Path $dst 'special\junction'))) ''
            Ok "[$ip] empty folder created" (Test-Path (Join-Path $dst 'special\empty')) ''
            Ok "[$ip] no part files left" (-not (Get-ChildItem $dst -Recurse -Force -Filter '*.hopover-part*' -ErrorAction SilentlyContinue)) ''

            # Run 2: nothing to do.
            $c = Start-Copy $dst "$tag-2" -Recopy
            Wait-Copy $c
            Ok "[$ip] second run copies 0 bytes" ((Get-RunBytes $c) -eq 0 -and $c.Proc.ExitCode -eq 0) "runBytes=$(Get-RunBytes $c) in $([math]::Round($c.Clock.Elapsed.TotalSeconds,1)) s"

            # Run 3: a newer file on the new PC stays, an extra file stays, an older source file is replaced.
            $keep = Join-Path $dst 'root-file.txt'
            [IO.File]::WriteAllText($keep, 'edited on the new PC'); (Get-Item $keep).LastWriteTime = (Get-Date).AddDays(1)
            [IO.File]::WriteAllText((Join-Path $dst 'extra.txt'), 'only here')
            $stale = Join-Path $dst 'special\old-time.txt'
            (Get-Item $stale).IsReadOnly = $false; [IO.File]::WriteAllText($stale, 'stale and older'); (Get-Item $stale).LastWriteTime = [datetime]'2001-01-01'
            $c = Start-Copy $dst "$tag-3" -Recopy
            Wait-Copy $c
            Ok "[$ip] newer file on the new PC is kept" ([IO.File]::ReadAllText($keep) -eq 'edited on the new PC') ''
            Ok "[$ip] extra file on the new PC survives" (Test-Path (Join-Path $dst 'extra.txt')) ''
            Ok "[$ip] older destination file is replaced" ([IO.File]::ReadAllText($stale) -eq 'old') ''
        } finally { Stop-Link }
    }

    if (-not $SkipResume) {
        Write-Host "`n===== resume after a kill (loopback) ====="
        $port = 47830
        Start-Link '127.0.0.1' $port 'resume'
        try {
            $dst = Join-Path $T 'dst-resume'
            $c = Start-Copy $dst 'k1'
            $partMeta = Join-Path $dst 'big.bin.hopover-part.meta'
            $until = (Get-Date).AddMinutes(10)
            $offset = 0
            while ((Get-Date) -lt $until -and -not $c.Proc.HasExited) {
                if (Test-Path $partMeta) { try { $offset = [int64](Get-Content $partMeta)[2] } catch { $offset = 0 } }
                if ($offset -ge 256MB) { break }
                Start-Sleep -Milliseconds 100
            }
            Ok 'kill mid-file: sidecar shows progress before the kill' ($offset -ge 256MB) ("offset " + [math]::Round($offset / 1MB) + ' MB')
            Start-Sleep -Seconds 12   # lets the plan's other chunks finish and files-done.json be written
            Stop-Tree $c.Proc
            Stop-Tree $script:proxy     # the link dies with the copy: the client must cope
            Start-Sleep -Milliseconds 500
            $partLen = (Get-Item (LongP (Join-Path $dst 'big.bin.hopover-part'))).Length
            $doneBefore = @(Get-Content "$T\store-copy-k1\files-done.json" -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json).Count
            Write-Host ("  killed: part file {0:N0} MB, {1} chunks recorded done" -f ($partLen / 1MB), $doneBefore)
            Ok 'kill left a part file and no final big.bin' ($partLen -gt 0 -and -not (Test-Path (Join-Path $dst 'big.bin'))) ''
            # Restart the proxy (same serve), rerun with the SAME store dir (files-done.json) but without -Recopy.
            $pout = Join-Path $T 'proxy-resume2.txt'
            $script:proxy = Start-Quiet "--link proxy --mode addr --addr 127.0.0.1:$port --code 3-alpha-bravo-cafe" $pout
            $line = Wait-Line $pout '##LINK proxy'
            $parts = $line.Split(' ')
            $env:HOPOVER_LINK = "$($parts[2]):$($parts[3])"
            $out2 = Join-Path $T 'copy-k2.txt'
            $a = "-NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\link-copy-run.ps1`" -Src `"$T\src`" -Dst `"$dst`" -Store `"$T\store-copy-k1`" -BigFile `"$T\src\big.bin`""
            $p = Start-Process powershell.exe -ArgumentList $a -RedirectStandardOutput $out2 -RedirectStandardError "$out2.err" -WindowStyle Hidden -PassThru; $null = $p.Handle
            $c2 = [pscustomobject]@{ Proc = $p; Out = $out2; Clock = [Diagnostics.Stopwatch]::StartNew() }
            Wait-Copy $c2
            $rb = Get-RunBytes $c2
            Ok 'resume: exit 0' ($c2.Proc.ExitCode -eq 0) "exit $($c2.Proc.ExitCode)"
            $skipNote = Select-String -Path $out2 -Pattern 'chunks were done in an earlier run' | Select-Object -First 1
            Ok 'resume: finished chunks were skipped' ([bool]$skipNote) ($skipNote.Line)
            $bigMB = $BigMB
            Ok 'resume: the part file continued (less than the whole file was sent again)' ($rb -ge 0 -and $rb -lt ($bigMB * 1MB - 128MB)) ("runBytes {0:N0} MB of {1:N0} MB" -f ($rb / 1MB), $bigMB)
            Compare-Trees "$T\src" $dst '[resume]'
        } finally { Stop-Link }
    }

    # LAN discovery by UDP broadcast (typed code only, no address).
    Write-Host "`n===== LAN discovery (UDP broadcast) ====="
    $out = Join-Path $T 'serve-disc.txt'
    $srv = Start-Quiet "--link serve --store `"$T\store`" --code 5-alpha-bravo-cafe --tcp 47850 --udp 47851 --rendezvous none --name DISCPC" $out
    $null = Wait-Line $out 'listening'
    $pout = Join-Path $T 'proxy-disc.txt'
    $px = Start-Quiet "--link proxy --mode lan --udp 47851 --code 5-alpha-bravo-cafe" $pout
    $line = Wait-Line $pout '##LINK (proxy|failed)' 40
    Ok 'discovery: the new PC finds the old PC by code' ($line -and $line -match 'proxy') ($line)
    Stop-Tree $px; Stop-Tree $srv
}
finally {
    Stop-Link
    if (-not $KeepData -and (Test-Path $T)) {
        # Only our own test data: %TEMP%\hopover-linktest. cmd rd removes a junction without touching its target.
        if (Test-Path "$T\src\special\junction") { cmd /c rmdir "$T\src\special\junction" | Out-Null }
        Remove-Item -LiteralPath (LongP $T) -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "test data removed: $T"
    }
}
Write-Host ''
if ($Fails -eq 0) { Write-Host 'LINK TEST OK' -ForegroundColor Green; exit 0 } else { Write-Host "LINK TEST FAILED ($Fails)" -ForegroundColor Red; exit 1 }
