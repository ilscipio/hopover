# Files: build the copy plan on the old PC, pull it with parallel robocopy on the new PC.

function Test-NameExcluded([string]$Name, [string[]]$Patterns) {
    foreach ($p in $Patterns) { if ($Name -like $p) { return $true } }
    $false
}

function Get-SpeedProfile([string]$Speed) {
    # Workers = robocopy processes at once; Mt = threads inside each one.
    switch ($Speed) {
        'gentle' { [pscustomobject]@{ Workers = 2; Mt = 4 } }
        'full'   { [pscustomobject]@{ Workers = 8; Mt = 16 } }
        default  { [pscustomobject]@{ Workers = 4; Mt = 8 } }
    }
}

function Get-CopyPlan($Config) {
    $profileDir = $env:USERPROFILE
    $skip = @($Config.skipUnder | ForEach-Object { Expand-PathVars $_ } | Where-Object { $_ })
    $items = New-Object System.Collections.Generic.List[object]
    $add = {
        param([string]$Path, [string]$Why)
        if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return }
        if (Test-UnderAny $Path $skip) { return }
        $it = Get-Item -LiteralPath $Path -Force
        if ($it.Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
        $items.Add([pscustomobject]@{ Path = $it.FullName; Kind = $(if ($it.PSIsContainer) { 'dir' } else { 'file' }); Why = $Why; Group = $it.FullName })
    }

    # 1. Everything in the user profile except AppData and the exclusions.
    Get-ChildItem -LiteralPath $profileDir -Force | Where-Object { -not (Test-NameExcluded $_.Name $Config.profileExclude) } |
        ForEach-Object { & $add $_.FullName 'profile' }

    # 2. Selected AppData folders (copying all of AppData breaks apps).
    foreach ($rel in $Config.appDataRoaming) { & $add (Join-Path $env:APPDATA $rel) 'appdata-roaming' }
    foreach ($rel in $Config.appDataLocal) { & $add (Join-Path $env:LOCALAPPDATA $rel) 'appdata-local' }

    # 3. Own top-level folders on C:\ (C:\java and the like).
    Get-ChildItem -LiteralPath 'C:\' -Force -ErrorAction SilentlyContinue |
        Where-Object { -not (Test-NameExcluded $_.Name $Config.cRootExclude) -and -not ($_.Attributes -band [IO.FileAttributes]::System) } |
        ForEach-Object { & $add $_.FullName 'c-root' }

    # 4. Every other fixed drive, whole.
    if ($Config.otherDrives -eq 'all-fixed') {
        Get-Volume | Where-Object { $_.DriveType -eq 'Fixed' -and $_.DriveLetter -and $_.DriveLetter -ne 'C' } |
            ForEach-Object { & $add "$($_.DriveLetter):\" 'drive' }
    }

    # 5. KeePass databases and key files that KeePass remembers, wherever they live.
    foreach ($f in Get-KeePassFiles) {
        if (-not ($items | Where-Object { Test-UnderAny $f @($_.Path) })) { & $add $f 'keepass' }
    }

    # 6. Chunks: big trees split into their subfolders. Small jobs run in parallel,
    #    give a finer progress bar, and a restart skips every chunk that is done.
    $exNames = @($Config.excludeDirs | Where-Object { $_ -notmatch '[\\%]' })
    $exPaths = @(Get-RobocopyExcludes $Config | Where-Object { $_ -match '^[A-Za-z]:\\' }) + $skip
    foreach ($it in $items) {
        $depth = [int]$Config.chunkDepth.($it.Why)
        Split-PlanItem $it $depth $exNames $exPaths
    }
}

function Split-PlanItem($Item, [int]$Depth, [string[]]$ExNames, [string[]]$ExPaths) {
    if ($Item.Kind -ne 'dir' -or $Depth -le 0) { return $Item }
    $subs = @(Get-ChildItem -LiteralPath $Item.Path -Directory -Force -ErrorAction SilentlyContinue | Where-Object {
        -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
        -not (Test-NameExcluded $_.Name $ExNames) -and -not (Test-UnderAny $_.FullName $ExPaths)
    })
    # More than 200 subfolders: one chunk. The "files" chunk must exclude each split subfolder
    # by full path, and thousands of them would pass robocopy's 32k command-line limit.
    if ($subs.Count -lt 2 -or $subs.Count -gt 200) { return $Item }
    # This folder minus its split subfolders is one chunk (/E, so a subfolder made after the
    # scan still moves); each subfolder is split further.
    [pscustomobject]@{ Path = $Item.Path; Kind = 'files'; Why = $Item.Why; Group = $Item.Group; Skip = @($subs.FullName) }
    foreach ($s in $subs) {
        Split-PlanItem ([pscustomobject]@{ Path = $s.FullName; Kind = 'dir'; Why = $Item.Why; Group = $Item.Group }) ($Depth - 1) $ExNames $ExPaths
    }
}

function Get-KeePassFiles {
    $cfg = Join-Path $env:APPDATA 'KeePass\KeePass.config.xml'
    if (-not (Test-Path $cfg)) { return @() }
    [xml]$x = Get-Content $cfg -Raw -Encoding UTF8
    $paths = @()
    $paths += $x.SelectNodes('//Path') | ForEach-Object { $_.InnerText }
    $paths += $x.SelectNodes('//KeyFilePath') | ForEach-Object { $_.InnerText }
    # KeePass stores paths relative to KeePass.exe when they sit on the same drive.
    $exeDir = @("$env:ProgramFiles\KeePass Password Safe 2", "${env:ProgramFiles(x86)}\KeePass Password Safe 2") |
        Where-Object { Test-Path "$_\KeePass.exe" } | Select-Object -First 1
    $paths | Where-Object { $_ } | ForEach-Object {
        $p = [Environment]::ExpandEnvironmentVariables($_)
        if ($p -notmatch '^[A-Za-z]:\\|^\\\\' -and $exeDir) { $p = [IO.Path]::GetFullPath((Join-Path $exeDir $p)) }
        $p
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Sort-Object -Unique
}

function Get-Junctions {
    # C:\Seafile\* junctions point into SeaDrive; recreate them after SeaDrive signs in.
    $roots = @('C:\Seafile', 'C:\') | Where-Object { Test-Path $_ }
    foreach ($r in $roots) {
        Get-ChildItem -LiteralPath $r -Force -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint -and $_.LinkType -eq 'Junction' -and $_.Target } |
            ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Target = @($_.Target)[0] } }
    }
}

function Get-RobocopyExcludes($Config) {
    $xd = @($Config.excludeDirs | ForEach-Object { if ($_ -match '%') { Expand-PathVars $_ } else { $_ } } | Where-Object { $_ })
    $xd += @($Config.skipUnder | ForEach-Object { Expand-PathVars $_ } | Where-Object { $_ })
    $list = @()
    if ($xd) { $list += "/XD"; $list += $xd }
    if ($Config.excludeFiles) { $list += "/XF"; $list += @($Config.excludeFiles) }
    $list
}

function ConvertTo-RoboArg([string]$Arg) {
    # A quoted path that ends in \ escapes the quote: "D:\" breaks robocopy. Use D:\. and \\pc\share.
    $a = $Arg
    if ($a -match '[\\/]$' -and $a.Length -gt 3) { $a = $a.TrimEnd('\', '/') }
    if ($a -match '^[A-Za-z]:\\?$') { $a = $a.Substring(0, 2) + '\.' }
    if ($a -eq '' -or $a -match '\s') { return '"' + $a + '"' }
    $a
}

function New-RoboJob([int]$Index, $Item, [string]$Src, [string]$Dst, [string[]]$Options, [string[]]$Excludes, [scriptblock]$MapPath, [string]$Exe, [string[]]$ExeArgs) {
    if ($Item.Kind -eq 'file') {
        $list = @((Split-Path $Src), (Split-Path $Dst), (Split-Path $Src -Leaf)) + $Options
    } elseif ($Item.Kind -eq 'files') {
        # The folder with /E, minus its split subfolders (their own chunks), by full source path:
        # a name alone would also drop same-named folders deeper down.
        $skip = @($Item.Skip | Where-Object { $_ } | ForEach-Object { if ($MapPath) { & $MapPath $_ } else { $_ } })
        $list = @($Src, $Dst, '/E') + $Options + $Excludes + @(if ($skip) { '/XD' } ) + $skip
    } else {
        $list = @($Src, $Dst, '/E') + $Options + $Excludes
    }
    $j = New-Object Hopover.RoboJob
    $j.Index = $Index
    $j.Path = $Item.Path
    $j.Planned = [int64]$Item.Bytes
    $j.Arguments = (@($ExeArgs | Where-Object { $_ }) + $list | ForEach-Object { ConvertTo-RoboArg $_ }) -join ' '
    if ($Exe) { $j.Exe = $Exe }
    $j
}

function Wait-Pool($Pool, [scriptblock]$OnStarted, [scriptblock]$OnDone, [scriptblock]$OnTick) {
    $last = [DateTime]::MinValue
    while ($true) {
        $j = $null
        while ($Pool.Started.TryDequeue([ref]$j)) { & $OnStarted $j }
        while ($Pool.Done.TryDequeue([ref]$j)) {
            # A job enqueues Started before Done: draining Started here again guarantees
            # that its start event goes out before its done event.
            $s = $null
            while ($Pool.Started.TryDequeue([ref]$s)) { & $OnStarted $s }
            & $OnDone $j
        }
        if (([DateTime]::Now - $last).TotalMilliseconds -ge 800) { & $OnTick; $last = [DateTime]::Now }
        if ($Pool.Finished -and $Pool.Done.IsEmpty) { break }
        Start-Sleep -Milliseconds 250
    }
    & $OnTick
}

function Measure-Plan($Plan, [string[]]$Excludes, [string]$Speed) {
    # robocopy /L lists what a copy would move, with the same excludes, and writes nothing.
    $probe = Join-Path $env:TEMP 'hopover-probe-does-not-exist'
    $opts = @('/L', '/XJ', '/BYTES', '/NFL', '/NDL', '/NJH', '/R:0', '/W:0')
    $jobs = New-Object 'System.Collections.Generic.List[Hopover.RoboJob]'
    for ($i = 0; $i -lt $Plan.Count; $i++) { $jobs.Add((New-RoboJob $i $Plan[$i] $Plan[$i].Path $probe $opts $Excludes)) }
    # Scanning is light: more workers than copying.
    $pool = [Hopover.RoboPool]::new($jobs.ToArray(), ((Get-SpeedProfile $Speed).Workers + 2), $Speed, $true)
    $pool.Start()
    $script:measured = 0
    Wait-Pool $pool {} {
        param($j)
        $Plan[$j.Index] | Add-Member -NotePropertyName Bytes -NotePropertyValue $j.Bytes -Force
        $Plan[$j.Index] | Add-Member -NotePropertyName Files -NotePropertyValue $j.Files -Force
        $script:measured++
        Send-Event SCAN "$($script:measured) $($Plan.Count) $($j.Path)"
    } {}
}

function ConvertTo-TargetPath([string]$OldPath, [string]$OldProfile) {
    if (Test-UnderAny $OldPath @($OldProfile)) { return $env:USERPROFILE + $OldPath.Substring($OldProfile.TrimEnd('\').Length) }
    $drive = $OldPath.Substring(0, 1).ToUpper()
    if ($drive -eq 'C') { return $OldPath }
    # Same letter is not enough: D: here can be another disk, a DVD or a recovery partition.
    # Only a fixed volume with the old label counts as the same drive.
    $vol = Get-Volume -DriveLetter $drive -ErrorAction SilentlyContinue
    $oldLabel = if ($script:DriveLabels) { $script:DriveLabels.$drive } else { $null }
    if ($vol -and $vol.DriveType -eq 'Fixed' -and $oldLabel -and $vol.FileSystemLabel -eq $oldLabel) { return $OldPath }
    "C:\Drives\$drive" + $OldPath.Substring(2)
}

function Get-LinkExe {
    # Hopover.exe --link copy is the robocopy of the link. The GUI (or --link proxy) sets HOPOVER_LINK and HOPOVER_EXE.
    if (-not $env:HOPOVER_LINK) { throw 'No link to the old PC. Connect with the pairing code in Hopover first.' }
    if (-not $env:HOPOVER_EXE -or -not (Test-Path -LiteralPath $env:HOPOVER_EXE)) { throw 'HOPOVER_EXE is not set: start the move from Hopover.exe.' }
    $env:HOPOVER_EXE
}

function Invoke-LinkCopy([string[]]$CopyArgs) {
    # A GUI-subsystem exe returns at once unless its output is piped: Out-Null waits for it.
    & (Get-LinkExe) --link copy @CopyArgs | Out-Null
    $LASTEXITCODE
}

function ConvertTo-SourcePath([string]$OldPath, [string]$From) {
    # Old "D:\x" is the virtual root \\link\hopover-d on the old PC (the name before the share is ignored).
    $drive = $OldPath.Substring(0, 1).ToLower()
    "\\$From\hopover-$drive" + $OldPath.Substring(2)
}

function Copy-Plan($Plan, [string]$From, [string]$OldProfile, [string[]]$Excludes, [string]$Store, [string]$Speed,
                   [scriptblock]$Heartbeat, [switch]$Recopy, [switch]$WhatIf) {
    # The excludes were expanded on the old PC; full paths in them must point at its share.
    $ex = @($Excludes | ForEach-Object { if ($_ -match '^[A-Za-z]:\\') { ConvertTo-SourcePath $_ $From } else { $_ } })
    $log = Join-Path $Store 'logs\robocopy-errors.log'
    $doneFile = Join-Path $Store 'files-done.json'
    $done = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    if (-not $Recopy) { foreach ($p in @(Read-Json $doneFile)) { if ($p) { [void]$done.Add($p) } } }
    $sp = Get-SpeedProfile $Speed
    $exe = Get-LinkExe
    # /XO: a re-run never overwrites a file that is newer on this PC. File lines stay on
    # (no /NFL): their byte counts drive the progress bar.
    $opts = @('/COPY:DAT', '/DCOPY:DAT', "/MT:$($sp.Mt)", '/R:1', '/W:1', '/XJ', '/XO', '/NP', '/NDL', '/NJH', '/NJS', '/BYTES')
    if ($WhatIf) { $opts += '/L' }

    $total = [int64](($Plan | Measure-Object Bytes -Sum).Sum)
    # Resume key = kind and path: a folder that was a "files" chunk in an earlier run and is a
    # whole tree now (other chunkDepth) must copy again.
    $skipped = [int64](($Plan | Where-Object { $done.Contains("$($_.Kind)|$($_.Path)") } | Measure-Object Bytes -Sum).Sum)
    # Largest first: the long jobs start early, so the end has no single straggler.
    $todo = @($Plan | Where-Object { -not $done.Contains("$($_.Kind)|$($_.Path)") } | Sort-Object { [int64]$_.Bytes } -Descending)
    if ($skipped) { Write-Note ("{0} chunks were done in an earlier run ({1:N1} GB): skipped" -f ($Plan.Count - $todo.Count), ($skipped / 1GB)) }

    $jobs = New-Object 'System.Collections.Generic.List[Hopover.RoboJob]'
    for ($i = 0; $i -lt $todo.Count; $i++) {
        $it = $todo[$i]
        $jobs.Add((New-RoboJob $i $it (ConvertTo-SourcePath $it.Path $From) (ConvertTo-TargetPath $it.Path $OldProfile) $opts $ex { param($p) ConvertTo-SourcePath $p $From } $exe @('--link', 'copy')))
    }
    $pool = [Hopover.RoboPool]::new($jobs.ToArray(), $sp.Workers, $Speed, $false)
    Write-Note "$($todo.Count) chunks, $($sp.Workers) at once, $($sp.Mt) threads each ($Speed)"
    $pool.Start()

    $state = @{ DoneBytes = $skipped; DoneCopied = [int64]0; Failed = @(); LastSave = [DateTime]::Now }
    Wait-Pool $pool {
        param($j)
        Send-Event ITEM "$($j.Index) $($todo.Count) $($j.Planned) $($j.Path)"
    } {
        param($j)
        $state.DoneBytes += $j.Planned
        $state.DoneCopied += $j.Copied
        Send-Event ITEMDONE "$($j.Index) $($j.Planned) $($j.ExitCode)"
        if ($j.ExitCode -ge 8) { Write-Warn "copy exit $($j.ExitCode) for $($j.Path) (see $log)"; $state.Failed += $j.Path }
        elseif (-not $WhatIf) { [void]$done.Add("$($todo[$j.Index].Kind)|$($j.Path)") }
        if (([DateTime]::Now - $state.LastSave).TotalSeconds -ge 10) { Write-Json @($done) $doneFile; $state.LastSave = [DateTime]::Now }
    } {
        # Done chunks count with their planned size; running ones with what they copied so far.
        $now = $state.DoneBytes + [math]::Max(0, $pool.Copied - $state.DoneCopied)
        Send-Event PROGRESS "$now $total $($pool.Copied)"
        if (-not $WhatIf -and (([DateTime]::Now - $state.LastSave).TotalSeconds -ge 10)) { Write-Json @($done) $doneFile; $state.LastSave = [DateTime]::Now }
        $e = $null
        while ($pool.Errors.TryDequeue([ref]$e)) { Add-Content -Path $log -Value $e -Encoding UTF8 }
        # Not named OnTick: inside Wait-Pool that name is this very block.
        if ($Heartbeat) { & $Heartbeat }
    }
    if (-not $WhatIf) { Write-Json @($done) $doneFile; Repair-SshAcl }
    $state.Failed
}

function Repair-SshAcl {
    # OpenSSH refuses private keys that other users can read.
    $ssh = Join-Path $env:USERPROFILE '.ssh'
    if (-not (Test-Path $ssh)) { return }
    $me = "$env:USERDOMAIN\$env:USERNAME"
    icacls $ssh /inheritance:r /grant:r "${me}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warn "icacls on $ssh failed ($LASTEXITCODE): ssh may refuse the keys" }
    else { Write-Note "Fixed .ssh permissions for $me" }
}

function Restore-Junctions($Junctions, [string]$OldProfile, [switch]$WhatIf) {
    $failed = @()
    foreach ($j in $Junctions) {
        if (Test-Path -LiteralPath $j.Path) { continue }
        $target = $j.Target -replace [regex]::Escape($OldProfile), $env:USERPROFILE.Replace('$', '$$')
        Write-Note "junction $($j.Path) -> $target"
        if (-not $WhatIf) {
            New-Item -ItemType Directory -Path (Split-Path $j.Path) -Force | Out-Null
            cmd /c mklink /J "$($j.Path)" "$target" | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Warn "junction $($j.Path) failed"; $failed += "mklink /J `"$($j.Path)`" `"$target`"" }
        }
    }
    $failed
}
