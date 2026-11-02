# Apps, env vars, settings, Wi-Fi, drives, printers, certificates, WSL, Docker, scheduled tasks.
# Each Export-* writes into the store on the old PC; each Import-* reads it on the new PC.

# ---------- apps ----------
function Export-Apps([string]$Store, $Config) {
    $json = Join-Path $Store 'apps\winget.json'
    New-Item -ItemType Directory -Path (Split-Path $json) -Force | Out-Null
    # winget prints the packages it cannot map; keep them for the checklist.
    $msg = winget export -o $json --accept-source-agreements 2>&1
    $msg | Where-Object { "$_" -match 'not available|nicht verf' } | Set-Content (Join-Path $Store 'apps\winget-unmatched.txt') -Encoding UTF8
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
}

function Install-WingetId([string]$Id, [string[]]$Flags) {
    winget install --id $Id -e @Flags 2>&1 | ForEach-Object { "$_".Trim() } | Where-Object { $_ -match 'install|error|fail|Fehler' } | Select-Object -Last 3 | ForEach-Object { Write-Note "  $_" }
    # 0x8A15002B: no newer version, 0x8A150061: already installed.
    $ok = $LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq -1978335189 -or $LASTEXITCODE -eq -1978335135
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

function Import-Apps([string]$Store, $Config, [switch]$WhatIf) {
    $json = Join-Path $Store 'apps\winget.json'
    $flags = @('--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
    $userScope = @(Read-Json (Join-Path $Store 'apps\user-scope.json') | Where-Object { $_ })
    $extra = @(Read-Json (Join-Path $Store 'apps\extra.json') | Where-Object { $_ })
    if ($WhatIf) {
        Write-Note "would install: $(@($Config.priorityApps) + $userScope + $extra -join ', ')"
        Write-Note "would run: winget import -i $json --ignore-unavailable"
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
    if ($userScope) {
        $w = Read-Json $json
        foreach ($s in $w.Sources) { $s.Packages = @($s.Packages | Where-Object { $userScope -notcontains $_.PackageIdentifier }) }
        $json = Join-Path $Store 'apps\winget-import.json'
        Write-Json $w $json
    }
    # Line by line into the log: the progress bars of winget would fill the memory of a runspace.
    winget import -i $json --ignore-unavailable --ignore-versions @($flags[0..1]) 2>&1 | ForEach-Object {
        $l = "$_".Trim()
        # Drop spinner and progress-bar lines (block characters U+2580-U+259F). ASCII only in
        # this file: PowerShell 5.1 reads a file without BOM as ANSI.
        # One event per finished app for the GUI checkpoint: "##APP <id>|<name>". Matches the English and German winget text.
        if ($l -match '\[([A-Za-z0-9][\w.+-]*\.[\w.+-]+)\]') {
            $curId = $Matches[1]
            $curName = (($l -replace '\s*\[.*$', '') -replace '^(Found|Gefunden)\s+', '').Trim()
        } elseif ($curId -and $l -match 'Successfully installed|Erfolgreich installiert|already installed|bereits installiert') {
            Send-Event APP "$curId|$curName"
            $curId = $null
        }
        if ($l -and $l -notmatch '^[\s\-\\|/]+$|^\d+\s*%$|[▀-▟]') { Write-Note $l }
    }
    if ($LASTEXITCODE -ne 0) { Write-Warn "winget import: some packages did not install (exit $LASTEXITCODE). The log lists them." }
    if ($failed) { Write-Warn "Not installed: $($failed -join ', '). Install them by hand." }
    Write-Json @($failed) (Join-Path $Store 'apps\failed.json')
}

# ---------- environment variables ----------
function Export-EnvVars([string]$Store) {
    $user = [Environment]::GetEnvironmentVariables('User')
    $machine = [Environment]::GetEnvironmentVariables('Machine')
    Write-Json ([pscustomobject]@{ User = $user; Machine = $machine }) (Join-Path $Store 'env.json')
    Write-Note "user vars: $($user.Count), machine vars: $($machine.Count)"
}

function Import-EnvVars([string]$Store, [string]$OldProfile, [switch]$WhatIf) {
    $vars = Read-Json (Join-Path $Store "env.json")
    if (-not $OldProfile) { throw 'old profile path unknown (manifest missing)' }
    $fix = { param($v) if ($v) { $v -replace [regex]::Escape($OldProfile), $env:USERPROFILE.Replace('$', '$$') } else { $v } }
    $conflicts = @()
    foreach ($scope in 'User', 'Machine') {
        foreach ($p in $vars.$scope.PSObject.Properties) {
            $name = $p.Name; $old = & $fix $p.Value
            $cur = [Environment]::GetEnvironmentVariable($name, $scope)
            if ($name -ieq 'Path') {
                # Merge: keep every entry the new PC has, append the missing ones.
                $have = @($cur -split ';' | Where-Object { $_ })
                $missing = @($old -split ';' | Where-Object { $_ -and ($have -notcontains $_) })
                if ($missing) {
                    Write-Note "$scope PATH +$($missing.Count) entries"
                    if (-not $WhatIf) { [Environment]::SetEnvironmentVariable('Path', (($have + $missing) -join ';'), $scope) }
                }
                continue
            }
            if ($scope -eq 'Machine' -and $name -match '^(OS|PROCESSOR_|NUMBER_OF_PROCESSORS|ComSpec|PATHEXT|windir|DriverData|PSModulePath|TMP|TEMP|USERNAME)') { continue }
            if ($null -eq $cur) {
                Write-Note "$scope $name"
                if (-not $WhatIf) { [Environment]::SetEnvironmentVariable($name, $old, $scope) }
            } elseif ($cur -ne $old) { $conflicts += "$scope $name" }
        }
    }
    foreach ($c in $conflicts) { Write-Warn "kept the new value of $c (differs from the old PC)" }
    $conflicts
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
        TimeZone  = (Get-TimeZone).Id
        Power     = $(((powercfg /getactivescheme) -join ' ') -replace '.*?([0-9a-f]{8}-[0-9a-f-]{27}).*', '$1')
    }
    if ($win.Power -match '^[0-9a-f-]{36}$') { powercfg /export "$dir\power.pow" $win.Power | Out-Null }
    Write-Json $win "$dir\windows.json"
    Write-Note "app registry keys: $($vendors.Count) ($(($vendors.PSChildName) -join ', '))"
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

function Import-Settings([string]$Store, [string]$OldProfile, [switch]$WhatIf) {
    $dir = Join-Path $Store 'settings'
    foreach ($f in Get-ChildItem "$dir\reg\*.reg" -ErrorAction SilentlyContinue) {
        Write-Note "reg import $($f.Name)"
        if ($WhatIf) { continue }
        # .reg files are UTF-16 and write paths as C:\\Users\\name: point them at this profile.
        $text = Get-Content $f.FullName -Raw -Encoding Unicode
        foreach ($pair in @(@($OldProfile, $env:USERPROFILE), @($OldProfile.Replace('\', '\\'), $env:USERPROFILE.Replace('\', '\\')))) {
            $text = $text -replace [regex]::Escape($pair[0]), $pair[1].Replace('$', '$$')
        }
        $tmp = Join-Path $env:TEMP "hopover-$($f.Name)"
        Set-Content $tmp $text -Encoding Unicode
        reg import $tmp 2>$null | Out-Null
        Remove-Item $tmp -ErrorAction SilentlyContinue
    }
    foreach ($f in Get-ChildItem "$dir\wifi\*.xml" -ErrorAction SilentlyContinue) {
        Write-Note "wifi $($f.BaseName)"
        if (-not $WhatIf) { netsh wlan add profile filename="$($f.FullName)" user=all 2>$null | Out-Null }
    }
    $s = Read-Json "$dir\settings.json"
    foreach ($d in $s.Drives) {
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
    if ($w.TimeZone -and (Get-TimeZone).Id -ne $w.TimeZone) {
        Write-Note "time zone: $($w.TimeZone)"
        if (-not $WhatIf) { Set-TimeZone -Id $w.TimeZone -ErrorAction SilentlyContinue }
    }
    if (Test-Path "$Dir\power.pow") {
        Write-Note 'power plan'
        if (-not $WhatIf) {
            # /import gives the plan a new GUID; read it back and make it active.
            $out = (powercfg /import "$Dir\power.pow") -join ' '
            if ($out -match '([0-9a-f]{8}-[0-9a-f-]{27})') { powercfg /setactive $Matches[1] | Out-Null }
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
    foreach ($d in Get-WslDistros | Where-Object { $Config.wslSkip -notcontains $_ }) {
        $user = (Invoke-Wsl @('-d', $d, '-e', 'whoami') | Select-Object -First 1)
        Write-Note "wsl --export $d (user $user) - this stops the distro"
        $out = Invoke-Wsl @('--export', $d, "$dir\$d.tar")
        $tar = Get-Item "$dir\$d.tar" -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0 -or -not $tar -or $tar.Length -eq 0) { throw "wsl --export $d failed: $($out -join ' ')" }
        Write-Note ("{0}.tar: {1:N1} GB" -f $d, ($tar.Length / 1GB))
        $meta += [pscustomobject]@{ Name = $d; User = $user; Tar = "$d.tar" }
    }
    Copy-Item (Join-Path $env:USERPROFILE '.wslconfig') $dir -ErrorAction SilentlyContinue
    Write-Json $meta "$dir\distros.json"
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
    foreach ($m in @($meta)) {
        if ($have -contains $m.Name) { Write-Note "$($m.Name) exists"; continue }
        $target = Join-Path $env:LOCALAPPDATA "wsl\$($m.Name)"
        Write-Note "wsl --import $($m.Name) $target"
        if ($WhatIf) { continue }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        $out = Invoke-Wsl @('--import', $m.Name, $target, "$dir\$($m.Tar)")
        if (@(Get-WslDistros) -notcontains $m.Name) { throw "wsl --import $($m.Name) failed: $($out -join ' ')" }
        if ($m.User -and $m.User -ne 'root') {
            # An imported distro starts as root; wsl.conf restores the default user.
            $sh = "grep -q '^\[user\]' /etc/wsl.conf 2>/dev/null || printf '\n[user]\ndefault=$($m.User)\n' >> /etc/wsl.conf"
            Invoke-Wsl @('-d', $m.Name, '-u', 'root', '--', 'sh', '-c', $sh) | Out-Null
            Invoke-Wsl @('--terminate', $m.Name) | Out-Null
        }
    }
    $def = @($meta)[0].Name
    if (-not $WhatIf -and $def) { Invoke-Wsl @('--set-default', $def) | Out-Null }
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
    foreach ($v in $vols) {
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
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'docker is missing: install Docker Desktop, start it once, then start the move again (only the Docker step).' }
    if (-not $WhatIf) { Assert-DockerUp 'start the move again (only the Docker step).' }
    $dir = Join-Path $Store 'docker'
    $failed = @()
    $existing = @(if (-not $WhatIf) { docker volume ls -q 2>$null })
    foreach ($f in $files) {
        Write-Note "docker volume $($f.BaseName)"
        if ($WhatIf) { continue }
        # A volume that has data already (a compose stack started on this PC) is left alone.
        if ($existing -contains $f.BaseName) {
            $inside = docker run --rm -v "$($f.BaseName):/v" alpine sh -c 'ls -A /v | head -1' 2>$null
            if ($inside) { Write-Note "  $($f.BaseName) has data on this PC: kept"; continue }
        }
        docker volume create $f.BaseName | Out-Null
        docker run --rm -v "$($f.BaseName):/v" -v "${dir}:/b" alpine tar -C /v -xzf "/b/$($f.Name)" | Out-Null
        if ($LASTEXITCODE -ne 0) { $failed += $f.BaseName }
    }
    if ($failed) { throw "docker volumes not restored: $($failed -join ', ')" }
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
