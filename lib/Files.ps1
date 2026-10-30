# Files: build the copy plan on the old PC, pull it with robocopy on the new PC.

function Test-NameExcluded([string]$Name, [string[]]$Patterns) {
    foreach ($p in $Patterns) { if ($Name -like $p) { return $true } }
    $false
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
        $items.Add([pscustomobject]@{ Path = $it.FullName; Kind = $(if ($it.PSIsContainer) { 'dir' } else { 'file' }); Why = $Why })
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
    $items
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

function Measure-PlanItem($Item, [string[]]$Excludes) {
    # robocopy /L lists what a copy would move, with the same excludes, and writes nothing.
    $probe = Join-Path $env:TEMP 'hopover-probe-does-not-exist'
    if ($Item.Kind -eq 'dir') {
        $out = robocopy $Item.Path $probe /L /E /XJ /BYTES /NFL /NDL /NJH /R:0 /W:0 @Excludes
    } else {
        $out = robocopy (Split-Path $Item.Path) $probe (Split-Path $Item.Path -Leaf) /L /XJ /BYTES /NFL /NDL /NJH /R:0 /W:0
    }
    # Summary rows are Dirs, Files, Bytes (labels are localised, so match by position).
    $rows = @($out | Where-Object { $_ -match '^\s*[^:]+:\s+\d+\s+\d+' })
    if ($rows.Count -lt 3) { return [pscustomobject]@{ Files = $null; Bytes = $null } }
    [pscustomobject]@{
        Files = [int64](($rows[1] -split ':', 2)[1].Trim() -split '\s+')[0]
        Bytes = [int64](($rows[2] -split ':', 2)[1].Trim() -split '\s+')[0]
    }
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

function ConvertTo-SourcePath([string]$OldPath, [string]$From) {
    # Old "D:\x" is served by the share \\OLDPC\hopover-d.
    $drive = $OldPath.Substring(0, 1).ToLower()
    "\\$From\hopover-$drive" + $OldPath.Substring(2)
}

function Copy-Plan($Plan, [string]$From, [string]$OldProfile, [string[]]$Excludes, [string]$LogDir, [switch]$WhatIf) {
    # The excludes were expanded on the old PC; full paths in them must point at its share.
    $ex = @($Excludes | ForEach-Object { if ($_ -match '^[A-Za-z]:\\') { ConvertTo-SourcePath $_ $From } else { $_ } })
    $log = Join-Path $LogDir 'robocopy-errors.log'
    $failed = @()
    $i = 0
    $copied = [int64]0
    $lastEvent = [DateTime]::MinValue
    foreach ($it in $Plan) {
        $i++
        $src = ConvertTo-SourcePath $it.Path $From
        $dst = ConvertTo-TargetPath $it.Path $OldProfile
        Send-Event ITEM "$i $(@($Plan).Count) $([int64]$it.Bytes) $($it.Path)"
        Write-Note "$($it.Why): $($it.Path) -> $dst"
        # File lines stay on (no /NFL): their byte counts drive the progress bar.
        # /XO: a re-run never overwrites a file that is newer on this PC.
        $common = @('/COPY:DAT', '/DCOPY:DAT', '/MT:16', '/R:1', '/W:1', '/XJ', '/XO', '/NP', '/NDL', '/NJH', '/NJS', '/BYTES')
        if ($WhatIf) { $common += '/L' }
        if ($it.Kind -eq 'dir') { $rc = @($src, $dst, '/E') + $common + $ex }
        else { $rc = @((Split-Path $src), (Split-Path $dst), (Split-Path $src -Leaf)) + $common }
        robocopy @rc | ForEach-Object {
            if ($_ -match '^\t[^\t*]*\t+\s*(\d+)\t') {
                $copied += [int64]$Matches[1]
                if (([DateTime]::Now - $lastEvent).TotalSeconds -ge 1) { Send-Event BYTES $copied; $lastEvent = [DateTime]::Now }
            } elseif ($_ -match 'ERROR|FEHLER') {
                Add-Content -Path $log -Value $_ -Encoding UTF8
            }
        }
        if ($LASTEXITCODE -ge 8) { Write-Warn "robocopy exit $LASTEXITCODE for $($it.Path) (see $log)"; $failed += $it.Path }
    }
    Send-Event BYTES $copied
    if (-not $WhatIf) { Repair-SshAcl }
    $failed
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
