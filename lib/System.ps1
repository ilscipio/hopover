# Apps, env vars, settings, Wi-Fi, drives, printers, certificates, WSL, Docker, scheduled tasks.
# Each Export-* writes into the store on the old PC; each Import-* reads it on the new PC.

# ---------- apps ----------
function Export-Apps([string]$Store, $Config) {
    $json = Join-Path $Store 'apps\winget.json'
    New-Item -ItemType Directory -Path (Split-Path $json) -Force | Out-Null
    # winget prints the packages it cannot map; keep them for the checklist.
    $msg = winget export -o $json --accept-source-agreements 2>&1
    $msg | Where-Object { "$_" -match 'not available|nicht verf' } | Set-Content (Join-Path $Store 'apps\winget-unmatched.txt') -Encoding UTF8
    Write-Json @(Get-UnmatchedNames $msg) (Join-Path $Store 'apps\unmatched.json')
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    Get-ItemProperty $keys -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -and -not $_.SystemComponent -and -not $_.ParentKeyName } |
        Select-Object DisplayName, DisplayVersion, Publisher, InstallLocation, URLInfoAbout |
        Sort-Object DisplayName -Unique |
        ConvertTo-Json | Set-Content (Join-Path $Store 'apps\registry.json') -Encoding UTF8
    $ids = @((Read-Json $json).Sources | ForEach-Object { $_.Packages.PackageIdentifier })
    # Apps that winget export cannot match (Chrome, NVM, ...): appMap names their winget id.
    $names = @((Read-Json (Join-Path $Store 'apps\registry.json')) | ForEach-Object { $_.DisplayName })
    $extra = @(foreach ($p in $Config.appMap.PSObject.Properties) {
        if ($ids -notcontains $p.Value -and ($names | Where-Object { $_ -match $p.Name })) { $p.Value }
    }) | Sort-Object -Unique
    Write-Json @($extra) (Join-Path $Store 'apps\extra.json')
    # Python installed for this user only: the venvs point at %LOCALAPPDATA%\Programs\Python, so the new PC
    # needs the same scope (an elevated winget would pick C:\Program Files).
    $user = @($ids | Where-Object { $_ -match '^Python\.Python\.3\.(\d+)$' -and (Test-Path "$env:LOCALAPPDATA\Programs\Python\Python3$($Matches[1])") })
    Write-Json @($user) (Join-Path $Store 'apps\user-scope.json')
    Write-Note "winget packages: $($ids.Count), extra from appMap: $($extra.Count) ($($extra -join ', ')), user scope: $($user -join ', ')"
    # Chocolatey keeps its own list; on this kind of PC half of the command-line tools live there.
    if (Get-Command choco -ErrorAction SilentlyContinue) {
        $pk = @(choco list --limit-output 2>$null | ForEach-Object {
            $p = "$_" -split '\|'
            if ($p.Count -eq 2 -and $p[0] -notmatch $Config.chocoSkip) { [pscustomobject]@{ Id = $p[0]; Version = $p[1] } }
        })
        Write-Json @($pk) (Join-Path $Store 'apps\choco.json')
        Write-Note "chocolatey packages: $($pk.Count)"
    }
    # Visual Studio: winget installs the shell only. The workloads come from an exported .vsconfig.
    $vs = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
    if (Test-Path "$vs\vswhere.exe") {
        $n = 0
        $meta = @(& "$vs\vswhere.exe" -all -products * -format json | Out-String | ConvertFrom-Json | ForEach-Object { $_ } | ForEach-Object {
            $n++
            $cfg = Join-Path $Store "apps\vs-$n.vsconfig"
            Start-Process "$vs\setup.exe" -ArgumentList @('export', '--installPath', "`"$($_.installationPath)`"", '--config', "`"$cfg`"", '--quiet', '--noUpdateInstaller') -Wait -WindowStyle Hidden
            if (Test-Path $cfg) { [pscustomobject]@{ Path = $_.installationPath; Config = "vs-$n.vsconfig" } }
        })
        Write-Json @($meta) (Join-Path $Store 'apps\vs.json')
        Write-Note "Visual Studio workload lists: $($meta.Count)"
    }
}

function Import-Choco([string]$Store, [string[]]$Flags) {
    $pk = @(Read-Json (Join-Path $Store 'apps\choco.json') | Where-Object { $_ })
    if (-not $pk) { return @() }
    $choco = Join-Path $env:ProgramData 'chocolatey\bin\choco.exe'
    if (-not (Test-Path $choco)) {
        Write-Note 'install Chocolatey'
        if (-not (Install-WingetId 'Chocolatey.Chocolatey' $Flags) -or -not (Test-Path $choco)) { return @($pk | ForEach-Object { "choco $($_.Id)" }) }
    }
    $have = @(& $choco list --limit-output 2>$null | ForEach-Object { ("$_" -split '\|')[0] })
    $failed = @()
    foreach ($p in $pk) {
        if ($have -contains $p.Id) { continue }
        Write-Note "choco install $($p.Id) $($p.Version)"
        & $choco install $p.Id --version $p.Version --yes --no-progress --limit-output 2>&1 | Out-Null
        # An old version can be gone from the feed: then the current one.
        if ($LASTEXITCODE -notin 0, 1641, 3010) { & $choco install $p.Id --yes --no-progress --limit-output 2>&1 | Out-Null }
        if ($LASTEXITCODE -in 0, 1641, 3010) { Send-Event APP "choco.$($p.Id)|$($p.Id)" }
        else { Write-Warn "choco install $($p.Id) failed (exit $LASTEXITCODE)"; $failed += "choco $($p.Id)" }
    }
    $failed
}

function Import-AppsByName([string]$Store, $Config, [string[]]$Flags) {
    # Store apps and installs without an id in the export: one exact name search each. Programs whose folder is copied stay out.
    $copied = @(Read-Json (Join-Path $Store 'apps\programs.json') | Where-Object { $_ } | ForEach-Object { $_.Name }) +
              @(Read-Json (Join-Path $Store 'apps\covered.json') | Where-Object { $_ })
    $triedFile = Join-Path $Store 'apps\tried.json'
    $tried = @(Read-Json $triedFile | Where-Object { $_ })
    $map = @($Config.appMap.PSObject.Properties | ForEach-Object { $_.Name })
    $missing = @()
    foreach ($raw in @(Read-Json (Join-Path $Store 'apps\unmatched.json') | Where-Object { $_ })) {
        $name = ($raw -replace '\s+v?\d[\w.+\-]*$', '').Trim()
        if (-not $name -or $name -match $Config.appSearchSkip) { continue }
        if ($copied | Where-Object { $raw.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) { continue }
        if ($map | Where-Object { $name -match $_ }) { continue }
        if ($tried -contains $name) { $missing += $name; continue }
        $tried += $name
        winget install --name $name --exact @Flags 2>&1 | Out-Null
        $code = $LASTEXITCODE
        if ($code -eq 0 -or $code -eq -1978335189 -or $code -eq -1978335135) { Write-Note "installed by name: $name"; Send-Event APP "$name|$name"; $tried = @($tried | Where-Object { $_ -ne $name }) }
        else { $missing += $name }
    }
    Write-Json @($tried) $triedFile
    $missing | Sort-Object -Unique
}

function Import-VsConfig([string]$Store) {
    $vs = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\setup.exe"
    foreach ($m in @(Read-Json (Join-Path $Store 'apps\vs.json') | Where-Object { $_ })) {
        if (-not (Test-Path $vs) -or -not (Test-Path -LiteralPath $m.Path)) { Write-Warn "Visual Studio $($m.Path) is not here: workloads not added"; continue }
        Write-Note "Visual Studio workloads: $($m.Path)"
        Start-Process $vs -ArgumentList @('modify', '--installPath', "`"$($m.Path)`"", '--config', "`"$Store\apps\$($m.Config)`"", '--quiet', '--norestart', '--noUpdateInstaller') -Wait -WindowStyle Hidden
    }
}

function Install-WingetId([string]$Id, [string[]]$Flags) {
    winget install --id $Id -e @Flags 2>&1 | ForEach-Object { "$_".Trim() } | Where-Object { $_ -match 'install|error|fail|Fehler' } | Select-Object -Last 3 | ForEach-Object { Write-Note "  $_" }
    # 0x8A15002B: no newer version, 0x8A150061: already installed.
    $ok = $LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq -1978335189 -or $LASTEXITCODE -eq -1978335135
    # 3010, 1641, 0x8A150109, 0x8A15010A: installed, but Windows must restart to finish.
    if (@(3010, 1641, -1978334967, -1978334966) -contains $LASTEXITCODE) { $ok = $true; $script:RestartPending = $true }
    if ($ok) { Send-Event APP "$Id|" } else { Write-Warn "winget install $Id failed (exit $LASTEXITCODE)" }
    $ok
}

function Assert-Winget {
    if (Get-Command winget -ErrorAction SilentlyContinue) { return }
    # On a fresh Windows, App Installer registers for the user some minutes after the first sign-in.
    Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction SilentlyContinue
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw 'winget is missing. Update "App Installer" in the Microsoft Store, then start Hopover again.'
    }
}

# What the other installers, the files and the WSL import lean on. It runs before the big copy, so a restart costs little.
function Import-Base([string]$Store, $Config, [switch]$WhatIf) {
    $flags = @('--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
    $ids = @(@((Read-Json (Join-Path $Store 'apps\winget.json')).Sources | ForEach-Object { $_.Packages.PackageIdentifier }) +
             @(Read-Json (Join-Path $Store 'apps\extra.json')) | Where-Object { $_ } | Sort-Object -Unique)
    # User-scope packages stay with Import-Apps: it installs them for this user only, like on the old PC.
    $userScope = @(Read-Json (Join-Path $Store 'apps\user-scope.json') | Where-Object { $_ })
    $want = New-Object System.Collections.Generic.List[string]
    foreach ($re in @($Config.baseApps)) {
        foreach ($id in ($ids | Where-Object { $_ -match $re })) {
            if ($Config.appSkip -notcontains $id -and $userScope -notcontains $id -and -not $want.Contains($id)) { $want.Add($id) }
        }
    }
    $distros = @(Read-Json (Join-Path $Store 'wsl\distros.json') | Where-Object { $_ })
    $needWsl = $distros.Count -gt 0 -or $ids -contains 'Microsoft.WSL' -or $ids -contains 'Docker.DockerDesktop'
    if ($WhatIf) {
        Write-Note "first, with a stop if Windows needs a restart: $(@(@(if ($needWsl) { 'WSL' }) + @($want)) -join ', ')"
        return
    }
    Assert-Winget
    if ($needWsl -and -not (Test-WslReady)) {
        Write-Note 'wsl --install --no-distribution'
        wsl.exe --install --no-distribution 2>&1 | Out-Null
        if (-not (Test-WslReady)) { $script:RestartPending = $true }
    }
    foreach ($id in $want) {
        Write-Note "install $id"
        [void](Install-WingetId $id $flags)
    }
}

# A Windows feature stays "EnablePending" until the restart, also over a new start of Hopover. WSL and Docker do not work before it.
function Test-RestartPending {
    if ($script:RestartPending) { return $true }
    @(Get-WindowsOptionalFeature -Online -ErrorAction SilentlyContinue | Where-Object { "$($_.State)" -eq 'EnablePending' }).Count -gt 0
}

function Import-Apps([string]$Store, $Config, [switch]$WhatIf) {
    $json = Join-Path $Store 'apps\winget.json'
    $flags = @('--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
    $userScope = @(Read-Json (Join-Path $Store 'apps\user-scope.json') | Where-Object { $_ })
    $extra = @(Read-Json (Join-Path $Store 'apps\extra.json') | Where-Object { $_ })
    if ($WhatIf) {
        $ids = @((Read-Json $json).Sources | ForEach-Object { $_.Packages.PackageIdentifier } | Where-Object { $Config.appSkip -notcontains $_ })
        Write-Note "winget, first: $(@($Config.priorityApps) -join ', ')"
        Write-Note "winget, by id: $(@($userScope + $extra + $ids | Sort-Object -Unique) -join ', ')"
        Write-Note "not installed (appSkip): $(@($Config.appSkip) -join ', ')"
        Write-Note "Chocolatey: $(@(Read-Json (Join-Path $Store 'apps\choco.json') | Where-Object { $_ } | ForEach-Object { "$($_.Id) $($_.Version)" }) -join ', ')"
        Write-Note "copied as a folder: $(@(Read-Json (Join-Path $Store 'apps\programs.json') | Where-Object { $_ } | ForEach-Object { "$($_.Name) ($($_.Path))" }) -join '; ')"
        Write-Note "Visual Studio workloads: $(@(Read-Json (Join-Path $Store 'apps\vs.json') | Where-Object { $_ } | ForEach-Object { $_.Path }) -join ', ')"
        return
    }
    Assert-Winget
    $failed = @()
    # Password managers and sync clients first, so sign-in can start while the rest installs.
    foreach ($id in $Config.priorityApps) {
        Write-Note "install $id"
        if (-not (Install-WingetId $id $flags)) { $failed += $id }
    }
    Write-Note 'Sign in to 1Password and OneDrive now. The other apps install in the meantime.' 'Green'
    foreach ($id in $userScope) {
        Write-Note "install $id (this user only, like on the old PC)"
        if (-not (Install-WingetId $id ($flags + '--scope', 'user'))) { $failed += $id }
    }
    foreach ($id in $extra) {
        Write-Note "install $id (not in the winget export)"
        if (-not (Install-WingetId $id $flags)) { $failed += $id }
    }
    # winget import has no per-package scope: the user-scope packages are done above, so leave them out.
    # appSkip: packages that make no sense on another PC or wait for a click.
    $w = Read-Json $json
    foreach ($s in $w.Sources) { $s.Packages = @($s.Packages | Where-Object { $userScope -notcontains $_.PackageIdentifier -and $Config.appSkip -notcontains $_.PackageIdentifier }) }
    $json = Join-Path $Store 'apps\winget-import.json'
    Write-Json $w $json
    # Line by line into the log: the progress bars of winget would fill the memory of a runspace.
    $cur = @{ Id = $null; Name = '' }
    $rc = Invoke-Watched 'winget' (@('import', '-i', "`"$json`"", '--ignore-unavailable', '--ignore-versions') + @($flags[0..1])) { param($line)
        $l = "$line".Trim()
        # Drop spinner and progress-bar lines (block characters U+2580-U+259F). ASCII only in
        # this file: PowerShell 5.1 reads a file without BOM as ANSI.
        # One event per finished app for the GUI checkpoint: "##APP <id>|<name>". Matches the English and German winget text.
        if ($l -match '\[([A-Za-z0-9][\w.+-]*\.[\w.+-]+)\]') {
            $cur.Id = $Matches[1]
            $cur.Name = (($l -replace '\s*\[.*$', '') -replace '^(Found|Gefunden)\s+', '').Trim()
        } elseif ($cur.Id -and $l -match 'Successfully installed|Erfolgreich installiert|already installed|bereits installiert') {
            Send-Event APP "$($cur.Id)|$($cur.Name)"
            $cur.Id = $null
        }
        if ($l -and $l -notmatch '^[\s\-\\|/]+$|^\d+\s*%$|[\u2580-\u259F]') { Write-Note $l }
    }
    if ($rc -gt 0 -or $rc -lt -1) { Write-Warn "winget import: some packages did not install (exit $rc). The log lists them." }
    $failed += @(Import-Choco $Store $flags)
    $notFound = @(Import-AppsByName $Store $Config $flags)
    Import-VsConfig $Store
    if ($failed) { Write-Warn "Not installed: $($failed -join ', '). Install them by hand." }
    Write-Json @($failed) (Join-Path $Store 'apps\failed.json')
    Write-Json @($notFound) (Join-Path $Store 'apps\not-found.json')
    # Not "done": the next start tries the missing apps again.
    if ($rc -eq -1) { throw 'winget import stood still and Hopover stopped it. Start the move again for the missing apps.' }
}

# Runs a program with its output in a file and hands over each new line. Returns its exit code, or -1 when it stood still.
# Still = no new output, no child process and no CPU time for $StallMinutes. winget import once sat like this for 5 hours,
# and the WSL, Docker and database steps waited behind it. A long download uses CPU, and an installer is a child process.
function Invoke-Watched([string]$Exe, [string[]]$ArgList, [scriptblock]$OnLine, [double]$StallMinutes = 15) {
    $out = Join-Path $env:TEMP "hopover-watched-$PID-$([guid]::NewGuid().ToString('N')).txt"
    $p = Start-Process $Exe -ArgumentList $ArgList -RedirectStandardOutput $out -RedirectStandardError "$out.err" -PassThru -WindowStyle Hidden
    $null = $p.Handle   # keeps the exit code readable after the process ends
    $seen = 0; $cpu = 0.0; $last = Get-Date; $stood = $false
    while (-not $p.HasExited) {
        Start-Sleep -Seconds 2
        $lines = @(Get-Content -LiteralPath $out -Encoding UTF8 -ErrorAction SilentlyContinue)
        # The last line can still be growing.
        while ($seen -lt $lines.Count - 1) { & $OnLine $lines[$seen]; $seen++; $last = Get-Date }
        if ($p.HasExited) { break }
        $now = $p.TotalProcessorTime.TotalSeconds
        if ($now - $cpu -ge 1) { $cpu = $now; $last = Get-Date }
        # conhost belongs to the hidden console and does not count.
        if (Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id) AND Name<>'conhost.exe'" -ErrorAction SilentlyContinue) { $last = Get-Date }
        if (((Get-Date) - $last).TotalMinutes -ge $StallMinutes) {
            Write-Warn "$Exe stood still for $StallMinutes minutes: Hopover stops it and goes on."
            $stood = $true
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            break
        }
    }
    $p.WaitForExit(5000) | Out-Null
    $lines = @(Get-Content -LiteralPath $out -Encoding UTF8 -ErrorAction SilentlyContinue)
    while ($seen -lt $lines.Count) { & $OnLine $lines[$seen]; $seen++ }
    Remove-Item -LiteralPath $out, "$out.err" -ErrorAction SilentlyContinue
    if ($stood) { return -1 }
    $p.ExitCode
}

# ---------- environment variables ----------
function Export-EnvVars([string]$Store) {
    $user = Get-RawEnv 'User'
    $machine = Get-RawEnv 'Machine'
    Write-Json ([pscustomobject]@{ User = $user; Machine = $machine }) (Join-Path $Store 'env.json')
    Write-Note "user vars: $(@($user.PSObject.Properties).Count), machine vars: $(@($machine.PSObject.Properties).Count)"
}

function Import-EnvVars([string]$Store, [string]$OldProfile, [switch]$WhatIf) {
    $vars = Read-Json (Join-Path $Store "env.json")
    if (-not $OldProfile) { throw 'old profile path unknown (manifest missing)' }
    $fix = { param($v) if ($v) { $v -replace [regex]::Escape($OldProfile), $env:USERPROFILE.Replace('$', '$$') } else { $v } }
    $plain = { param($e) [Environment]::ExpandEnvironmentVariables($e).TrimEnd('\') }
    # env-base.json = what the last import applied. Value here = that base: only the old PC changed it, so it updates.
    $base = Read-Json (Join-Path $Store 'env-base.json')
    $conflicts = @()
    $before = @()
    foreach ($scope in 'User', 'Machine') {
        foreach ($p in $vars.$scope.PSObject.Properties) {
            $name = $p.Name; $old = & $fix $p.Value
            $cur = Get-RawEnvValue $scope $name
            if ($name -ieq 'Path') {
                # Merge: keep every entry the new PC has, append the missing ones. %VAR% entries stay %VAR%.
                $have = @("$cur" -split ';' | Where-Object { $_ })
                $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
                foreach ($e in $have) { [void]$seen.Add((& $plain $e)) }
                $missing = @($old -split ';' | Where-Object { $_ -and $seen.Add((& $plain $_)) })
                if ($missing) {
                    Write-Note "$scope PATH +$($missing.Count) entries"
                    if (-not $WhatIf) { Set-RawEnvValue $scope 'Path' (($have + $missing) -join ';') }
                }
                continue
            }
            if ($scope -eq 'Machine' -and $name -match '^(OS|PROCESSOR_.*|NUMBER_OF_PROCESSORS|ComSpec|PATHEXT|windir|DriverData|PSModulePath|TMP|TEMP|USERNAME)$') { continue }
            if ($null -eq $cur) {
                Write-Note "$scope $name"
                if (-not $WhatIf) { Set-RawEnvValue $scope $name $old }
            } elseif ($cur -ne $old) {
                $was = if ($base) { & $fix $base.$scope.$name } else { $null }
                # First import: a restore or an installer set this value, not the user's work: the old PC wins. env-before.json keeps it.
                if (-not $base) {
                    Write-Note "$scope $name replaced (it stood here already)"
                    $before += [pscustomobject]@{ Scope = $scope; Name = $name; Value = $cur }
                    if (-not $WhatIf) { Set-RawEnvValue $scope $name $old }
                } elseif ($null -ne $was -and $cur -eq $was) {
                    Write-Note "$scope $name changed on the old PC"
                    if (-not $WhatIf) { Set-RawEnvValue $scope $name $old }
                } else { $conflicts += "$scope $name" }
            }
        }
    }
    if (-not $WhatIf) {
        Send-EnvChanged
        Copy-Item (Join-Path $Store 'env.json') (Join-Path $Store 'env-base.json') -Force
        if ($before) { Write-Json @($before) (Join-Path $Store 'env-before.json') }
    }
    foreach ($c in $conflicts) { Write-Warn "kept the new value of $c (differs from the old PC)" }
    $conflicts
}

# ---------- system: hosts, Windows features, machine switches, saved logins, VPN ----------
function Export-System([string]$Store, $Config) {
    $dir = Join-Path $Store 'system'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item "$env:SystemRoot\System32\drivers\etc\hosts" "$dir\hosts" -Force -ErrorAction SilentlyContinue
    $on = @(Get-WindowsOptionalFeature -Online -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Enabled' -and $_.FeatureName -notmatch $Config.featureSkip } | ForEach-Object { $_.FeatureName })
    $caps = @(Get-WindowsCapability -Online -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Installed' -and $_.Name -match '^(OpenSSH|Rsat)' } | ForEach-Object { $_.Name })
    $values = [ordered]@{}
    foreach ($p in $Config.registryMachineValues.PSObject.Properties) {
        $key = Get-Item ('Registry::' + $p.Name) -ErrorAction SilentlyContinue
        if (-not $key) { continue }
        $one = [ordered]@{}
        foreach ($n in $p.Value) {
            $v = $key.GetValue($n, $null)
            if ($null -ne $v) { $one[$n] = [pscustomobject]@{ Value = $v; Kind = "$($key.GetValueKind($n))" } }
        }
        $values[$p.Name] = [pscustomobject]$one
    }
    Write-Json ([pscustomobject]@{ Features = $on; Capabilities = $caps; Values = [pscustomobject]$values }) "$dir\system.json"
    # Tokens of git, gh, docker and others. The store is locked to this user; the link is encrypted.
    $creds = @([Hopover.Cred]::ReadAll() | Where-Object { $_.Target -notmatch $Config.credSkip })
    Write-Json @($creds) "$dir\logins.json"
    Write-Note "features: $($on.Count), saved logins: $($creds.Count)"
    # Only the addresses: the new PC opens each sign-in with the right account filled in. Tokens stay on this PC, by design.
    $acc = @(Get-ChildItem 'HKCU:\Software\Microsoft\OneDrive\Accounts' -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath
        if ($p.UserEmail) { [pscustomobject]@{ Email = $p.UserEmail; Business = ($_.PSChildName -like 'Business*') } }
    })
    Write-Json @($acc) "$dir\accounts.json"
    Export-WireGuard $dir
}

function Export-WireGuard([string]$Dir) {
    # WireGuard encrypts its tunnels for SYSTEM on this PC only: read them as SYSTEM once, into the locked store.
    $src = Join-Path $env:ProgramFiles 'WireGuard\Data\Configurations'
    if (-not (Test-Path "$src\*.dpapi")) { return }
    $to = Join-Path $Dir 'wireguard'
    New-Item -ItemType Directory -Path $to -Force | Out-Null
    $code = "Add-Type -AssemblyName System.Security; Get-ChildItem '$src\*.dpapi' | ForEach-Object { [IO.File]::WriteAllBytes('$to\' + `$_.BaseName, [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes(`$_.FullName), `$null, 'CurrentUser')) }"
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
    $name = 'Hopover-WireGuard-Export'
    try {
        $a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -EncodedCommand $enc"
        Register-ScheduledTask -TaskName $name -Action $a -User 'NT AUTHORITY\SYSTEM' -RunLevel Highest -Force -ErrorAction Stop | Out-Null
        Start-ScheduledTask -TaskName $name -ErrorAction Stop
        $until = (Get-Date).AddSeconds(30)
        while ((Get-ScheduledTask -TaskName $name).State -eq 'Running' -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 500 }
    } catch { Write-Warn "WireGuard tunnels not exported: $($_.Exception.Message)" }
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    Write-Note "WireGuard tunnels: $(@(Get-ChildItem "$to\*.conf" -ErrorAction SilentlyContinue).Count)"
}

function Import-System([string]$Store, [switch]$WhatIf) {
    $dir = Join-Path $Store 'system'
    $hosts = "$env:SystemRoot\System32\drivers\etc\hosts"
    if (Test-Path "$dir\hosts") {
        # Docker writes its own lines with this PC's address.
        $norm = { param($l) ($l.Trim() -replace '\s+', ' ') }
        $have = @(Get-Content $hosts -ErrorAction SilentlyContinue | ForEach-Object { & $norm $_ })
        $add = @(Get-Content "$dir\hosts" | Where-Object { $_.Trim() -and $_.Trim() -notmatch '^#' -and $_ -notmatch 'docker\.internal' -and $have -notcontains (& $norm $_) })
        if ($add) {
            Write-Note "hosts +$($add.Count) lines"
            if (-not $WhatIf) { try { Add-Content $hosts (@('', '# from the old PC (Hopover)') + $add) -Encoding ASCII -ErrorAction Stop } catch { Write-Warn "hosts file: $($_.Exception.Message)" } }
        }
    }
    $s = Read-Json "$dir\system.json"
    if ($s) {
        foreach ($k in $s.Values.PSObject.Properties) {
            foreach ($v in $k.Value.PSObject.Properties) {
                Write-Note "$($k.Name)\$($v.Name) = $($v.Value.Value)"
                if ($WhatIf) { continue }
                $path = 'Registry::' + $k.Name
                if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
                New-ItemProperty -Path $path -Name $v.Name -Value $v.Value.Value -PropertyType $v.Value.Kind -Force -ErrorAction SilentlyContinue | Out-Null
            }
        }
        # Windows Home has no Hyper-V: a feature this edition lacks goes to the checklist; WSL and Docker work without it.
        $all = @{}
        Get-WindowsOptionalFeature -Online -ErrorAction SilentlyContinue | ForEach-Object { $all[$_.FeatureName] = "$($_.State)" }
        $absent = @(); $restart = $false
        foreach ($f in @($s.Features)) {
            if (-not $f) { continue }
            if (-not $all.ContainsKey($f)) { $absent += $f; continue }
            if ($all[$f] -eq 'Enabled') { continue }
            Write-Note "Windows feature $f"
            if ($WhatIf) { continue }
            try {
                $r = Enable-WindowsOptionalFeature -Online -FeatureName $f -All -NoRestart -WarningAction SilentlyContinue -ErrorAction Stop
                if ($r.RestartNeeded) { $restart = $true }
            } catch { Write-Warn "Windows feature ${f}: $($_.Exception.Message)"; $absent += $f }
        }
        foreach ($c in @($s.Capabilities)) {
            if (-not $c) { continue }
            $now = Get-WindowsCapability -Online -Name $c -ErrorAction SilentlyContinue
            if ($now -and $now.State -ne 'Installed' -and -not $WhatIf) {
                Write-Note "Windows capability $c"
                Add-WindowsCapability -Online -Name $c -ErrorAction SilentlyContinue | Out-Null
            }
        }
        Write-Json @($absent) "$dir\features-absent.json"
        if ($restart) { Write-Note 'Windows features need a restart: Hopover stops for it after the first installs.' }
    }
    $have = @([Hopover.Cred]::ReadAll() | ForEach-Object { $_.Target })
    $n = 0
    foreach ($c in @(Read-Json "$dir\logins.json" | Where-Object { $_ })) {
        if ($have -contains $c.Target) { continue }
        $n++
        if ($WhatIf) { continue }
        if (-not [Hopover.Cred]::Write($c.Target, $c.User, $c.Comment, [int]$c.Persist, $c.Blob)) { Write-Warn "saved login $($c.Target) not written" }
    }
    Write-Note "saved logins: $n new"
}

function Import-UserUninstall([string]$Store, [string]$OldProfile, $Plan) {
    # The apps in %LOCALAPPDATA%\Programs arrive as files. Their uninstall keys tell winget that they are there:
    # otherwise it installs them a second time, into the folder that is being copied.
    $f = Join-Path $Store 'settings\user-uninstall.reg'
    if (-not (Test-Path $f) -or -not ($Plan | Where-Object { $_.Group -like '*\AppData\Local\Programs' })) { return }
    if (-not (Import-RegFile $f $OldProfile)) { Write-Warn 'user uninstall keys not imported: winget may install some apps twice' }
}

# ---------- registry settings, Wi-Fi, drives, printers ----------
function Export-Settings([string]$Store, $Config) {
    $dir = Join-Path $Store 'settings'
    New-Item -ItemType Directory -Path "$dir\reg", "$dir\wifi" -Force | Out-Null
    $i = 0
    foreach ($k in $Config.registry) {
        $i++
        reg export $k (Join-Path "$dir\reg" ('win-{0:D2}.reg' -f $i)) /y 2>$null | Out-Null
    }
    # App settings: every vendor key under HKCU\Software, except Windows, drivers and browsers.
    $vendors = @(Get-ChildItem 'HKCU:\Software' -ErrorAction SilentlyContinue |
        Where-Object { -not (Test-NameExcluded $_.PSChildName @($Config.registryVendorsExclude)) })
    $n = 0
    foreach ($v in $vendors) {
        # The index keeps "Foo Bar" and "Foo_Bar" apart.
        $n++
        $file = ('app-{0:D3}-' -f $n) + ($v.PSChildName -replace '[^\w.\-]', '_') + '.reg'
        reg export "HKCU\Software\$($v.PSChildName)" (Join-Path "$dir\reg" $file) /y 2>$null | Out-Null
    }
    # Single values from keys that also hold PC-specific data (DPI, monitors).
    $values = [ordered]@{}
    foreach ($p in $Config.registryValues.PSObject.Properties) {
        $item = Get-ItemProperty ('Registry::' + $p.Name) -ErrorAction SilentlyContinue
        if (-not $item) { continue }
        $values[$p.Name] = [ordered]@{}
        foreach ($n in $p.Value) { if ($null -ne $item.$n) { $values[$p.Name][$n] = "$($item.$n)" } }
    }
    $win = [pscustomobject]@{
        Values    = [pscustomobject]$values
        Languages = @(Get-WinUserLanguageList | ForEach-Object { $_.LanguageTag })
        Culture   = (Get-Culture).Name
        GeoId     = (Get-WinHomeLocation).GeoId
        UiLanguage = "$((Get-WinUILanguageOverride).Name)"
        Computer  = $env:COMPUTERNAME
        TimeZone  = (Get-TimeZone).Id
        Power     = $(((powercfg /getactivescheme) -join ' ') -replace '.*?([0-9a-f]{8}-[0-9a-f-]{27}).*', '$1')
    }
    if ($win.Power -match '^[0-9a-f-]{36}$') { powercfg /export "$dir\power.pow" $win.Power | Out-Null }
    Write-Json $win "$dir\windows.json"
    Write-Note "app registry keys: $($vendors.Count) ($(($vendors.PSChildName) -join ', '))"
    reg export 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall' "$dir\user-uninstall.reg" /y 2>$null | Out-Null
    # Machine-wide vendor keys. The import takes a key only when no installer made one here.
    New-Item -ItemType Directory -Path "$dir\hklm", "$dir\uninstall" -Force | Out-Null
    $skip = @($Config.registryVendorsExclude) + @($Config.registryMachineExclude)
    $list = @(); $m = 0
    foreach ($root in 'SOFTWARE', 'SOFTWARE\WOW6432Node') {
        foreach ($v in Get-ChildItem "HKLM:\$root" -ErrorAction SilentlyContinue | Where-Object { -not (Test-NameExcluded $_.PSChildName $skip) }) {
            $m++
            $file = ('m{0:D3}-' -f $m) + ($v.PSChildName -replace '[^\w.\-]', '_') + '.reg'
            $key = "HKLM\$root\$($v.PSChildName)"
            reg export $key "$dir\hklm\$file" /y 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { $list += [pscustomobject]@{ Key = $key; File = $file } }
        }
    }
    Write-Json @($list) "$dir\hklm.json"
    # Programs that move as a folder keep their entry in Settings > Apps.
    $u = 0
    $keys = @(foreach ($p in @(Read-Json (Join-Path $Store 'apps\programs.json') | Where-Object { $_ })) {
        $u++
        $file = "u$u.reg"
        reg export $p.Key "$dir\uninstall\$file" /y 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { [pscustomobject]@{ Path = $p.Path; File = $file } }
    })
    Write-Json @($keys) "$dir\uninstall.json"
    Write-Note "machine registry keys: $($list.Count), uninstall entries: $($keys.Count)"
    netsh wlan export profile key=clear folder="$dir\wifi" 2>$null | Out-Null
    $drives = Get-ChildItem HKCU:\Network -ErrorAction SilentlyContinue | ForEach-Object {
        [pscustomobject]@{ Letter = $_.PSChildName; Remote = (Get-ItemProperty $_.PSPath).RemotePath }
    }
    $printers = Get-Printer -ErrorAction SilentlyContinue | Select-Object Name, Type, DriverName, PortName, Shared
    $shell = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders' |
        Select-Object Desktop, Personal, 'My Pictures', 'My Music', 'My Video', '{374DE290-123F-4565-9164-39C4925E467B}'
    $creds = cmdkey /list | Where-Object { $_ -match '(Target|Ziel):' } | ForEach-Object { ($_ -split ':', 2)[1].Trim() }
    Write-Json ([pscustomobject]@{ Drives = @($drives); Printers = @($printers); ShellFolders = $shell; Credentials = @($creds) }) "$dir\settings.json"
    Write-Note "registry keys: $i, wifi: $((Get-ChildItem "$dir\wifi").Count), mapped drives: $(@($drives).Count), printers: $(@($printers).Count)"
}

function Import-RegFile([string]$Path, [string]$OldProfile, [string]$Drop) {
    # .reg files are UTF-16 and write paths as C:\\Users\\name: point them at this profile.
    $text = Get-Content $Path -Raw -Encoding Unicode
    foreach ($pair in @(@($OldProfile, $env:USERPROFILE), @($OldProfile.Replace('\', '\\'), $env:USERPROFILE.Replace('\', '\\')))) {
        $text = $text -replace [regex]::Escape($pair[0]), $pair[1].Replace('$', '$$')
    }
    if ($Drop) { $text = (@([regex]::Split($text, '(?m)(?=^\[)') | Where-Object { $_ -notmatch $Drop })) -join '' }
    $tmp = Join-Path $env:TEMP "hopover-$(Split-Path $Path -Leaf)"
    [IO.File]::WriteAllText($tmp, $text, [Text.Encoding]::Unicode)
    reg import $tmp 2>$null | Out-Null
    $ok = $LASTEXITCODE -eq 0
    Remove-Item $tmp -ErrorAction SilentlyContinue
    $ok
}

function Import-Settings([string]$Store, [string]$OldProfile, $Config, [switch]$ChangedOnly, [switch]$WhatIf) {
    $dir = Join-Path $Store 'settings'
    # A catch-up takes only what changed on the old PC: this PC's own settings stay otherwise.
    $hashFile = Join-Path $dir 'applied.json'
    $applied = @{}
    $seen = Read-Json $hashFile
    if ($seen) { foreach ($p in $seen.PSObject.Properties) { $applied[$p.Name] = $p.Value } }
    $failedReg = @()
    foreach ($f in Get-ChildItem "$dir\reg\*.reg" -ErrorAction SilentlyContinue) {
        $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm MD5).Hash
        if ($ChangedOnly -and $applied[$f.Name] -eq $hash) { continue }
        Write-Note "reg import $($f.Name)"
        if ($WhatIf) { continue }
        if (Import-RegFile $f.FullName $OldProfile $Config.registryDropSections) { $applied[$f.Name] = $hash } else { $failedReg += $f.Name }
    }
    # Machine keys: only when no installer made the key here.
    foreach ($k in @(Read-Json "$dir\hklm.json" | Where-Object { $_ })) {
        if (Test-Path ('Registry::' + $k.Key)) { continue }
        Write-Note "reg import $($k.Key)"
        if (-not $WhatIf -and -not (Import-RegFile "$dir\hklm\$($k.File)" $OldProfile)) { $failedReg += $k.Key }
    }
    foreach ($k in @(Read-Json "$dir\uninstall.json" | Where-Object { $_ })) {
        if (-not $WhatIf -and (Test-Path -LiteralPath $k.Path)) { [void](Import-RegFile "$dir\uninstall\$($k.File)" $OldProfile) }
    }
    if (-not $WhatIf) {
        Write-Json ([pscustomobject]$applied) $hashFile
        Write-Json @($failedReg) "$dir\failed.json"
    }
    foreach ($f in Get-ChildItem "$dir\wifi\*.xml" -ErrorAction SilentlyContinue) {
        Write-Note "wifi $($f.BaseName)"
        if (-not $WhatIf) { netsh wlan add profile filename="$($f.FullName)" user=all 2>$null | Out-Null }
    }
    $s = Read-Json "$dir\settings.json"
    foreach ($d in $s.Drives | Where-Object { $_ }) {
        if (Test-Path "$($d.Letter):\") { continue }
        Write-Note "map $($d.Letter): $($d.Remote)"
        if (-not $WhatIf) { net use "$($d.Letter):" $d.Remote /persistent:yes 2>$null | Out-Null }
    }
    foreach ($p in $s.Printers | Where-Object { $_.Type -eq 'Connection' }) {
        Write-Note "printer $($p.Name)"
        if (-not $WhatIf) { Add-Printer -ConnectionName $p.Name -ErrorAction SilentlyContinue }
    }
    Import-WindowsSettings $dir $OldProfile -WhatIf:$WhatIf
}

function Import-WindowsSettings([string]$Dir, [string]$OldProfile, [switch]$WhatIf) {
    $w = Read-Json "$Dir\windows.json"
    if (-not $w) { return }
    foreach ($k in $w.Values.PSObject.Properties) {
        foreach ($v in $k.Value.PSObject.Properties) {
            $val = $v.Value -replace [regex]::Escape($OldProfile), $env:USERPROFILE.Replace('$', '$$')
            Write-Note "$($k.Name)\$($v.Name)"
            if (-not $WhatIf) { Set-ItemProperty -Path ('Registry::' + $k.Name) -Name $v.Name -Value $val -ErrorAction SilentlyContinue }
        }
    }
    if ($w.Languages) {
        Write-Note "languages: $($w.Languages -join ', ')"
        if (-not $WhatIf) {
            # Add the missing languages to the list this PC has: a new list would drop the
            # keyboard layouts set up here.
            $list = Get-WinUserLanguageList
            $have = @($list | ForEach-Object { $_.LanguageTag })
            $missing = @($w.Languages | Where-Object { $have -notcontains $_ })
            foreach ($l in $missing) { $list.Add($l) }
            if ($missing) { Set-WinUserLanguageList $list -Force -ErrorAction SilentlyContinue }
        }
    }
    # Region and display language. A pack this Windows lacks is a checklist line, no failure.
    if ($w.Culture -and (Get-Culture).Name -ne $w.Culture) {
        Write-Note "regional format: $($w.Culture)"
        if (-not $WhatIf) { Set-Culture $w.Culture -ErrorAction SilentlyContinue }
    }
    if ($w.GeoId -and (Get-WinHomeLocation).GeoId -ne $w.GeoId) {
        Write-Note "home location: $($w.GeoId)"
        if (-not $WhatIf) { Set-WinHomeLocation -GeoId $w.GeoId -ErrorAction SilentlyContinue }
    }
    if ($w.UiLanguage -and "$((Get-WinUILanguageOverride).Name)" -ne $w.UiLanguage) {
        Write-Note "display language: $($w.UiLanguage)"
        if (-not $WhatIf) {
            try {
                if (Get-Command Install-Language -ErrorAction SilentlyContinue) { Install-Language $w.UiLanguage -ErrorAction Stop | Out-Null }
                Set-WinUILanguageOverride -Language $w.UiLanguage -ErrorAction Stop
            } catch { Write-Warn "display language $($w.UiLanguage): $($_.Exception.Message)" }
        }
    }
    if ($w.TimeZone -and (Get-TimeZone).Id -ne $w.TimeZone) {
        Write-Note "time zone: $($w.TimeZone)"
        if (-not $WhatIf) { Set-TimeZone -Id $w.TimeZone -ErrorAction SilentlyContinue }
    }
    if (Test-Path "$Dir\power.pow") {
        Write-Note 'power plan'
        if (-not $WhatIf) {
            # /import gives the plan a new GUID; read it back and make it active.
            $out = (powercfg /import "$Dir\power.pow") -join ' '
            if ($out -match '([0-9a-f]{8}-[0-9a-f-]{27})') {
                powercfg /setactive $Matches[1] | Out-Null
                if ($LASTEXITCODE -ne 0) { Write-Warn "power plan: Windows did not make it active (exit $LASTEXITCODE)" }
            } else { Write-Warn "power plan: import failed: $out" }
        }
    }
    # Wallpaper, mouse and keyboard values apply now; Explorer, taskbar and theme at the next sign-in.
    if (-not $WhatIf) { rundll32.exe user32.dll, UpdatePerUserSystemParameters 1, True }
}

# ---------- certificates ----------
function Export-Certs([string]$Store, [securestring]$Password) {
    $dir = Join-Path $Store 'certs'
    $certs = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) }
    if (-not $certs) { Write-Note 'no personal certificates'; return @() }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $failed = @()
    foreach ($c in $certs) {
        try { Export-PfxCertificate -Cert $c -FilePath "$dir\$($c.Thumbprint).pfx" -Password $Password -ErrorAction Stop | Out-Null }
        catch { $failed += "$($c.Subject) ($($c.Thumbprint)) - key not exportable" }
    }
    Write-Note "certificates: $(@($certs).Count - $failed.Count) exported, $($failed.Count) not exportable"
    $failed | Set-Content "$dir\not-exported.txt" -Encoding UTF8
}

function Import-Certs([string]$Store, [securestring]$Password, [switch]$WhatIf) {
    foreach ($f in Get-ChildItem (Join-Path $Store 'certs\*.pfx') -ErrorAction SilentlyContinue) {
        Write-Note "certificate $($f.BaseName)"
        # -ErrorAction Stop: a wrong password must fail the step, not pass it quietly.
        if (-not $WhatIf) { Import-PfxCertificate -FilePath $f.FullName -CertStoreLocation Cert:\CurrentUser\My -Password $Password -Exportable -ErrorAction Stop | Out-Null }
    }
}

# ---------- WSL ----------
function Export-Wsl([string]$Store, $Config) {
    $dir = Join-Path $Store 'wsl'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $meta = @()
    # A later catch-up takes what changed after this moment. Clock of the old PC, in UTC.
    $at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    foreach ($d in Get-WslDistros | Where-Object { $Config.wslSkip -notcontains $_ }) {
        $user = (Invoke-Wsl @('-d', $d, '-e', 'whoami') | Select-Object -First 1)
        # The tar is about as large as the used space of the distro. A full disk here would hurt the PC that is still in use.
        $used = [int64](@(Invoke-WslRoot $d "df -B1 --output=used / | tail -1") | Select-Object -Last 1)
        $free = (Get-Item $dir).PSDrive.Free
        if ($used -gt $free - 10GB) {
            throw ("{0} needs about {1:N0} GB in the store and only {2:N0} GB are free on {3}. Free some space, or switch WSL off here and run it again later." -f $d, ($used / 1GB), ($free / 1GB), $dir.Substring(0, 2))
        }
        Write-Note "wsl --export $d (user $user) - this stops the distro"
        $out = @(Invoke-WslWatched @('--export', $d, "$dir\$d.tar") "$dir\$d.tar" $used 'wsl')
        $tar = Get-Item "$dir\$d.tar" -ErrorAction SilentlyContinue
        if ($script:WslExit -ne 0 -or -not $tar -or $tar.Length -eq 0) { throw "wsl --export $d failed: $($out -join ' ')" }
        Write-Note ("{0}.tar: {1:N1} GB" -f $d, ($tar.Length / 1GB))
        Remove-Item "$dir\$d-delta.tgz" -ErrorAction SilentlyContinue
        $meta += [pscustomobject]@{ Name = $d; User = $user; Tar = "$d.tar"; ExportedAt = $at }
    }
    Copy-Item (Join-Path $env:USERPROFILE '.wslconfig') $dir -ErrorAction SilentlyContinue
    Write-Json $meta "$dir\distros.json"
}

function Export-WslDelta([string]$Store) {
    # The whole distro again would take an hour and stop it: only the files changed since the export. No files that
    # were deleted, and no /tmp, /run, logs and package downloads.
    $dir = Join-Path $Store 'wsl'
    foreach ($m in @(Read-Json "$dir\distros.json" | Where-Object { $_ })) {
        if (@(Get-WslDistros) -notcontains $m.Name -or -not $m.ExportedAt) { continue }
        $out = ConvertTo-WslPath "$dir\$($m.Name)-delta.tgz"
        $sh = "cd / && find . -xdev \( -path ./tmp -o -path ./run -o -path ./var/tmp -o -path ./var/cache -o -path ./var/log \) -prune -o \( -type f -o -type l \) -newermt '$($m.ExportedAt)' -print0 | tar --null --no-recursion -T - -czf '$out'"
        Write-Note "wsl changes in $($m.Name) since $($m.ExportedAt)"
        Invoke-WslRoot $m.Name $sh | Select-Object -Last 2 | ForEach-Object { Write-Note "  $_" }
        $f = Get-Item "$dir\$($m.Name)-delta.tgz" -ErrorAction SilentlyContinue
        if (-not $f) { throw "wsl delta of $($m.Name) failed" }
        Write-Note ("{0}-delta.tgz: {1:N1} MB" -f $m.Name, ($f.Length / 1MB))
    }
}

function Import-Wsl([string]$Store, [switch]$WhatIf) {
    $dir = Join-Path $Store 'wsl'
    $meta = Read-Json "$dir\distros.json"
    if (-not $meta) { return }
    if (-not (Test-WslReady)) {
        Write-Note 'wsl --install --no-distribution'
        if ($WhatIf) { return }
        wsl.exe --install --no-distribution
        # The import works only after the reboot; the step stays open and runs again on the next start.
        throw 'WSL is installed now. Restart the PC, then start Hopover again: the WSL step goes on.'
    }
    $have = @(Get-WslDistros)
    # Old distro name -> name here. A distro of the same name that Hopover did not make (a restored Store app, a first
    # launch) stays untouched: yours arrives beside it, and it becomes the default.
    $doneFile = Join-Path $dir 'imported.json'
    $names = @{}
    $seen = Read-Json $doneFile
    if ($seen) { foreach ($p in $seen.PSObject.Properties) { $names[$p.Name] = $p.Value } }
    foreach ($m in @($meta)) {
        if ($names[$m.Name] -and $have -contains $names[$m.Name]) { Write-Note "$($names[$m.Name]) was imported earlier"; continue }
        $name = $m.Name
        if ($have -contains $name) {
            $name = "$($m.Name)-moved"
            Write-Warn "A distro $($m.Name) exists here already: it stays, yours arrives as $name"
        }
        if ($have -contains $name) { $names[$m.Name] = $name; continue }
        $target = Join-Path $env:LOCALAPPDATA "wsl\$name"
        Write-Note "wsl --import $name $target"
        if ($WhatIf) { continue }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        $out = @(Invoke-WslWatched @('--import', $name, $target, "$dir\$($m.Tar)") "$target\ext4.vhdx" (Get-Item "$dir\$($m.Tar)").Length 'wsl')
        if (@(Get-WslDistros) -notcontains $name) { throw "wsl --import $name failed: $($out -join ' ')" }
        $names[$m.Name] = $name
        Write-Json ([pscustomobject]$names) $doneFile
        if ($m.User -and $m.User -ne 'root') {
            # An imported distro starts as root; wsl.conf restores the default user.
            $sh = "grep -q '^\[user\]' /etc/wsl.conf 2>/dev/null || printf '\n[user]\ndefault=$($m.User)\n' >> /etc/wsl.conf"
            Invoke-WslRoot $name $sh | Out-Null
            Invoke-Wsl @('--terminate', $name) | Out-Null
        }
    }
    if ($WhatIf) { return }
    Write-Json ([pscustomobject]$names) $doneFile
    $def = $names[@($meta)[0].Name]
    if ($def) { Invoke-Wsl @('--set-default', $def) | Out-Null }
    foreach ($m in @($meta)) {
        if (-not $names[$m.Name] -or -not (Test-Path "$dir\$($m.Name)-delta.tgz")) { continue }
        # --keep-newer-files: a file changed in this distro since then stays.
        Write-Note "wsl changes into $($names[$m.Name])"
        $src = ConvertTo-WslPath "$dir\$($m.Name)-delta.tgz"
        Invoke-WslRoot $names[$m.Name] "tar -C / --keep-newer-files -xzpf '$src' 2>/dev/null; true" | Out-Null
    }
}

# ---------- Docker named volumes ----------
function Assert-DockerUp([string]$Then) {
    docker info --format '{{.ServerVersion}}' *> $null
    if ($LASTEXITCODE -ne 0) { throw "Docker Desktop does not run. Start it and wait until it is ready, then $Then" }
}

function Export-DockerVolumes([string]$Store) {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return }
    # Without the engine, 'docker volume ls' prints nothing and the volumes would be lost without a word.
    Assert-DockerUp 'prepare again (only the Docker step).'
    $dir = Join-Path $Store 'docker'
    # Anonymous volumes have 64-hex names and belong to throwaway containers.
    $vols = @(docker volume ls --format '{{.Name}}' 2>$null | Where-Object { $_ -and $_ -notmatch '^[0-9a-f]{64}$' })
    if (-not $vols) { Write-Note 'no named docker volumes'; return }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $failed = @()
    $n = 0
    foreach ($v in $vols) {
        $n++
        Send-Event DETAIL "docker $n of $($vols.Count): $v"
        Write-Note "docker volume $v"
        docker run --rm -v "${v}:/v:ro" -v "${dir}:/b" alpine tar -C /v -czf "/b/$v.tgz" . | Out-Null
        if ($LASTEXITCODE -ne 0) { $failed += $v; Remove-Item "$dir\$v.tgz" -ErrorAction SilentlyContinue }
    }
    if ($failed) { throw "docker volumes not saved: $($failed -join ', ')" }
}

function Import-DockerVolumes([string]$Store, [switch]$WhatIf) {
    $files = Get-ChildItem (Join-Path $Store 'docker\*.tgz') -ErrorAction SilentlyContinue
    if (-not $files) { return }
    # Throw, not return: the step stays open, and the next start of the move does it again.
    if ($WhatIf) { foreach ($f in $files) { Write-Note "docker volume $($f.BaseName) ($([int]($f.Length / 1MB)) MB)" }; return }
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'docker is missing: install Docker Desktop, start it once, then start the move again (only the Docker step).' }
    if (-not $WhatIf) { Assert-DockerUp 'start the move again (only the Docker step).' }
    $dir = Join-Path $Store 'docker'
    $failed = @()
    $existing = @(if (-not $WhatIf) { docker volume ls -q 2>$null })
    # What each volume looked like after the last restore. Same look now: nothing here used it, so the newer data can replace it.
    $appliedFile = Join-Path $dir 'applied.json'
    $applied = @{}
    $seen = Read-Json $appliedFile
    if ($seen) { foreach ($p in $seen.PSObject.Properties) { $applied[$p.Name] = $p.Value } }
    $look = { param($v) (docker run --rm -v "${v}:/v:ro" alpine sh -c 'cd /v && find . -type f -exec stat -c "%n %s %Y" {} + | sort | sha256sum' 2>$null | Select-Object -First 1) }
    $kept = @()
    foreach ($f in $files) {
        Write-Note "docker volume $($f.BaseName)"
        if ($WhatIf) { continue }
        # A volume that has data already (a compose stack started on this PC) is left alone.
        if ($existing -contains $f.BaseName) {
            $inside = docker run --rm -v "$($f.BaseName):/v" alpine sh -c 'ls -A /v | head -1' 2>$null
            if ($inside) {
                if ($applied[$f.BaseName] -and $applied[$f.BaseName] -eq (& $look $f.BaseName)) {
                    # Unchanged since the restore, so the same data is still in the older archive of the old PC: nothing unique goes. Without
                    # the clean-up, old and new database files would mix.
                    Write-Note "  $($f.BaseName): unchanged here, newer data from the old PC"
                    docker run --rm -v "$($f.BaseName):/v" alpine sh -c 'find /v -mindepth 1 -delete' | Out-Null
                } else {
                    Write-Note "  $($f.BaseName) changed on this PC: kept"
                    $kept += $f.BaseName
                    continue
                }
            }
        }
        docker volume create $f.BaseName | Out-Null
        docker run --rm -v "$($f.BaseName):/v" -v "${dir}:/b" alpine tar -C /v -xzf "/b/$($f.Name)" | Out-Null
        if ($LASTEXITCODE -ne 0) { $failed += $f.BaseName } else { $applied[$f.BaseName] = & $look $f.BaseName }
    }
    Write-Json ([pscustomobject]$applied) $appliedFile
    Write-Json @($kept) (Join-Path $dir 'kept.json')
    if ($failed) { throw "docker volumes not restored: $($failed -join ', ')" }
}

# ---------- PostgreSQL ----------
function Get-PgServices {
    Get-CimInstance Win32_Service -Filter "Name LIKE 'postgresql%'" -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.PathName -match '-D\s+"([^"]+)"') { [pscustomobject]@{ Name = $_.Name; Data = $Matches[1]; Running = ($_.State -eq 'Running') } }
    }
}

function Export-Databases([string]$Store) {
    # A database folder is consistent only with the server stopped. One archive: a later catch-up has no old files to mix in.
    $dir = Join-Path $Store 'postgres'
    foreach ($s in Get-PgServices) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Note "$($s.Name): stops for a moment, $($s.Data)"
        Stop-Service $s.Name -Force -ErrorAction Stop
        try {
            tar.exe -czf "$dir\$($s.Name).tgz" -C "$($s.Data)" . 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)" }
        } finally { if ($s.Running) { Start-Service $s.Name -ErrorAction SilentlyContinue } }
    }
}

function Import-Databases([string]$Store, [switch]$WhatIf) {
    $dir = Join-Path $Store 'postgres'
    $failed = @()
    foreach ($f in Get-ChildItem "$dir\*.tgz" -ErrorAction SilentlyContinue) {
        $mark = "$dir\$($f.BaseName).applied"
        # This server may hold new work by now. Its data stays; the checklist names the newer archive.
        if (Test-Path $mark) { Write-Note "$($f.BaseName): restored earlier, this PC's data stays"; continue }
        if ($WhatIf) { Write-Note "$($f.BaseName): the data of the old PC replaces the data of a fresh install (which is kept beside it)"; continue }
        $s = @(Get-PgServices | Where-Object { $_.Name -eq $f.BaseName })[0]
        if (-not $s) { $failed += $f.BaseName; continue }
        Write-Note "$($s.Name): data from the old PC into $($s.Data)"
        if ($WhatIf) { continue }
        Stop-Service $s.Name -Force -ErrorAction Stop
        $aside = "$($s.Data).hopover-fresh-$(Get-Date -Format yyyyMMdd-HHmmss)"
        Rename-Item -LiteralPath $s.Data -NewName (Split-Path $aside -Leaf) -ErrorAction Stop
        New-Item -ItemType Directory -Path $s.Data -Force | Out-Null
        tar.exe -xzf $f.FullName -C "$($s.Data)" 2>&1 | Out-Null
        # The service runs as NETWORK SERVICE (S-1-5-20).
        icacls "$($s.Data)" /grant '*S-1-5-20:(OI)(CI)F' /T /C /Q | Out-Null
        Start-Service $s.Name -ErrorAction SilentlyContinue
        if ((Get-Service $s.Name).Status -eq 'Running') { Set-Content $mark (Get-Date).ToString('s') }
        else { $failed += "$($s.Name) (it does not start: its old data is in $aside)" }
    }
    if ($failed) { throw "PostgreSQL not restored: $($failed -join '; '). Is PostgreSQL installed here?" }
}

# ---------- scheduled tasks ----------
function Export-Tasks([string]$Store, $Config) {
    $dir = Join-Path $Store 'tasks'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $tasks = Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\Microsoft\*' -and ($_.TaskPath + $_.TaskName) -notmatch $Config.taskExclude }
    $meta = foreach ($t in $tasks) {
        $file = ($t.TaskPath.Trim('\') + '_' + $t.TaskName) -replace '[\\/:*?"<>|]', '_'
        $file = $file.TrimStart('_') + '.xml'
        Export-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath | Set-Content "$dir\$file" -Encoding Unicode
        [pscustomobject]@{ Name = $t.TaskName; Path = $t.TaskPath; File = $file; Enabled = ($t.State -ne 'Disabled') }
    }
    Write-Json @($meta) "$dir\tasks.json"
    Write-Note "scheduled tasks: $(@($meta).Count)"
}

function Import-Tasks([string]$Store, [string]$OldProfile, [string]$OldUser, [switch]$WhatIf) {
    $dir = Join-Path $Store 'tasks'
    $me = "$env:USERDOMAIN\$env:USERNAME"
    $failed = @()
    foreach ($m in @(Read-Json "$dir\tasks.json")) {
        if (-not $m) { continue }
        if (Get-ScheduledTask -TaskName $m.Name -TaskPath $m.Path -ErrorAction SilentlyContinue) { Write-Note "exists: $($m.Name)"; continue }
        $xml = Get-Content "$dir\$($m.File)" -Raw -Encoding Unicode
        $xml = $xml -replace [regex]::Escape($OldProfile), $env:USERPROFILE.Replace('$', '$$')
        # The old user's SID or PC\name means nothing here: run those as the current user.
        # SYSTEM, LOCAL SERVICE and NETWORK SERVICE (S-1-5-18/19/20) stay as they are.
        $xml = [regex]::Replace($xml, '<UserId>([^<]*)</UserId>', {
            param($u)
            $v = $u.Groups[1].Value
            if ($v -match '^S-1-5-21-' -or $v -ieq $OldUser -or $v -match "\\$([regex]::Escape($OldUser))$") { "<UserId>$me</UserId>" } else { $u.Value }
        })
        # Import switched off: the task starts only after 'hopover.ps1 enable-tasks'.
        $xml = $xml -replace '(<Settings>[\s\S]*?)<Enabled>true</Enabled>', '$1<Enabled>false</Enabled>'
        if ($xml -notmatch '<Settings>[\s\S]*?<Enabled>') { $xml = $xml -replace '<Settings>', '<Settings><Enabled>false</Enabled>' }
        Write-Note "task $($m.Path)$($m.Name) (disabled)"
        if ($WhatIf) { continue }
        try { Register-ScheduledTask -Xml $xml -TaskName $m.Name -TaskPath $m.Path -ErrorAction Stop | Out-Null }
        catch { $failed += "$($m.Path)$($m.Name): $($_.Exception.Message)"; Write-Warn "task $($m.Name): $($_.Exception.Message)" }
    }
    $failed
}

function Enable-MigratedTasks([string]$Store) {
    foreach ($m in @(Read-Json (Join-Path $Store 'tasks\tasks.json'))) {
        if (-not $m -or -not $m.Enabled) { continue }
        Enable-ScheduledTask -TaskName $m.Name -TaskPath $m.Path -ErrorAction SilentlyContinue | Out-Null
        Write-Note "enabled $($m.Path)$($m.Name)"
    }
}
