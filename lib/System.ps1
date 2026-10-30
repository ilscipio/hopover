# Apps, env vars, settings, Wi-Fi, drives, printers, certificates, WSL, Docker, scheduled tasks.
# Each Export-* writes into the store on the old PC; each Import-* reads it on the new PC.

# ---------- apps ----------
function Export-Apps([string]$Store) {
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
    $n = (Read-Json $json).Sources | ForEach-Object { $_.Packages.Count } | Measure-Object -Sum
    Write-Note "winget packages: $($n.Sum)"
}

function Import-Apps([string]$Store, $Config, [switch]$WhatIf) {
    $json = Join-Path $Store 'apps\winget.json'
    $flags = @('--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
    # Password managers and sync clients first, so sign-in can start while the rest installs.
    foreach ($id in $Config.priorityApps) {
        Write-Note "install $id"
        if (-not $WhatIf) { winget install --id $id -e @flags | Out-Null }
    }
    if ($WhatIf) { Write-Note "would run: winget import -i $json --ignore-unavailable"; return }
    Write-Note 'Sign in to 1Password and OneDrive now. The other apps install in the meantime.' 'Green'
    winget import -i $json --ignore-unavailable --ignore-versions @($flags[0..1])
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
        reg export $k (Join-Path "$dir\reg" ('{0:D2}.reg' -f $i)) /y 2>$null | Out-Null
    }
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
        if (-not $WhatIf) { Import-PfxCertificate -FilePath $f.FullName -CertStoreLocation Cert:\CurrentUser\My -Password $Password -Exportable | Out-Null }
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
        Invoke-Wsl @('--export', $d, "$dir\$d.tar") | Out-Null
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
        Invoke-Wsl @('--import', $m.Name, $target, "$dir\$($m.Tar)") | Out-Null
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
function Export-DockerVolumes([string]$Store) {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return }
    $dir = Join-Path $Store 'docker'
    # Anonymous volumes have 64-hex names and belong to throwaway containers.
    $vols = @(docker volume ls --format '{{.Name}}' 2>$null | Where-Object { $_ -and $_ -notmatch '^[0-9a-f]{64}$' })
    if (-not $vols) { Write-Note 'no named docker volumes'; return }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    foreach ($v in $vols) {
        Write-Note "docker volume $v"
        docker run --rm -v "${v}:/v:ro" -v "${dir}:/b" alpine tar -C /v -czf "/b/$v.tgz" . | Out-Null
    }
}

function Import-DockerVolumes([string]$Store, [switch]$WhatIf) {
    $files = Get-ChildItem (Join-Path $Store 'docker\*.tgz') -ErrorAction SilentlyContinue
    if (-not $files) { return }
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Write-Warn 'docker missing: start Docker Desktop once, then run: hopover.ps1 import -Only docker'; return }
    $dir = Join-Path $Store 'docker'
    foreach ($f in $files) {
        Write-Note "docker volume $($f.BaseName)"
        if ($WhatIf) { continue }
        docker volume create $f.BaseName | Out-Null
        docker run --rm -v "$($f.BaseName):/v" -v "${dir}:/b" alpine tar -C /v -xzf "/b/$($f.Name)" | Out-Null
    }
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
