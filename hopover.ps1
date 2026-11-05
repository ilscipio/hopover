<#
.SYNOPSIS
  Hopover: moves a Windows PC to a new one over the LAN. Apps, settings, keys, WSL, tasks, files.
  Hopover.exe embeds and drives this script; it also runs alone.

.EXAMPLE
  # Old PC (admin PowerShell):
  .\hopover.ps1 export
  # New PC (admin PowerShell):
  net use \\OLDPC\IPC$ /user:OLDPC\pp
  \\OLDPC\hopover-store\tool\hopover.ps1 import -From OLDPC
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet('inventory', 'export', 'share', 'import', 'enable-tasks', 'verify', 'cleanup')]
    [string]$Command,
    [string]$From,
    [string]$Store = 'C:\hopover-store',
    [string[]]$Only,
    [string[]]$Skip,
    [switch]$Sizes,
    [ValidateSet('gentle', 'balanced', 'full')]
    [string]$Speed = 'balanced',
    [switch]$Recopy,
    [switch]$WhatIf,
    # export -Refresh: the old PC only updates what changed (no scan with sizes, no certificates, no link start).
    [switch]$Refresh,
    # import -Catchup: the new PC fetches what changed on the old PC since the last pass.
    [switch]$Catchup,
    # cleanup -Purge: also delete the store. Only Hopover's own working copy: C:\hopover-held stays for the user.
    [switch]$Purge
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Root = $PSScriptRoot
. (Join-Path $Root 'lib\Common.ps1')
. (Join-Path $Root 'lib\Files.ps1')
. (Join-Path $Root 'lib\System.ps1')
. (Join-Path $Root 'lib\Report.ps1')
if (-not ('Hopover.RoboPool' -as [type])) { Add-Type -Path (Join-Path $Root 'lib\Native.cs') }
# Behind the user's own programs: lower CPU and I/O priority. robocopy, winget and wsl
# start as children and inherit the lower CPU class; the pool lowers their I/O too.
[Hopover.Native]::Lower([Diagnostics.Process]::GetCurrentProcess(), $Speed)

# -Only a,b arrives as one string when the GUI starts this script with -File.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Skip = @($Skip | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$Config = Get-MigrationConfig $Root
$fresh = -not (Test-Path $Store)
New-Item -ItemType Directory -Path "$Store\logs" -Force | Out-Null
if ($fresh -or $Command -in 'export', 'import') { Protect-Folder $Store }
$script:Log = Join-Path $Store "logs\$Command-$(Get-Date -Format yyyyMMdd-HHmmss).log"
$script:FailedPhases = @()

function Use-Step([string]$Name) {
    if ($Only.Count -and $Only -notcontains $Name) { return $false }
    if ($Skip -contains $Name) { return $false }
    $true
}

function Assert-Admin {
    if (-not (Test-Admin)) { throw 'Run this in an administrator PowerShell.' }
}

# Runs one step: skips it when not selected, reports start/done/failed to the GUI,
# and records it in state-<command>.json so a restart can skip what is done.
function Invoke-Phase([string]$Name, [string]$Title, [scriptblock]$Body) {
    if (-not (Use-Step $Name)) { Send-Event STEP "$Name skipped"; return }
    Write-Step $Title
    Send-Event STEP "$Name start"
    try {
        & $Body
        Send-Event STEP "$Name done"
        if (-not $WhatIf) { Save-StepDone $Name }
    } catch {
        Write-Warn "$Title failed: $($_.Exception.Message)"
        Send-Event STEP "$Name failed"
        $script:FailedPhases += $Name
    }
}

function Save-StepDone([string]$Name) {
    $path = Join-Path $Store "state-$Command.json"
    $state = Read-Json $path
    $done = @(@(if ($state) { $state.Done }) + $Name | Where-Object { $_ } | Sort-Object -Unique)
    Write-Json ([pscustomobject]@{ Done = $done; Updated = (Get-Date).ToString('s') }) $path
}

function Get-CertPassword([string]$Prompt) {
    # The GUI passes the password in the environment of this process only.
    if ($env:HOPOVER_CERT_PW) { return ConvertTo-SecureString $env:HOPOVER_CERT_PW -AsPlainText -Force }
    # A hidden child of the GUI cannot answer a prompt; it would wait forever.
    if (Test-GuiChild) { throw 'no certificate password given' }
    Read-Host $Prompt -AsSecureString
}

# In one step: a measuring that gets stopped must not leave half a file.
function Save-Manifest($Manifest) {
    $tmp = "$Store\manifest.tmp"
    Write-Json $Manifest $tmp
    Move-Item -LiteralPath $tmp -Destination "$Store\manifest.json" -Force
}

function Invoke-Inventory {
    Write-Step 'Inventory'
    # ready.json = the store is complete. A new scan means it is not, until export ends. The new PC waits for this file.
    if (-not $Refresh) { Remove-Item "$Store\ready.json" -ErrorAction SilentlyContinue }
    Send-Event STEP 'inventory start'
    $plan = @(Get-CopyPlan $Config)
    $manifest = [pscustomobject]@{
        Computer   = $env:COMPUTERNAME
        User       = $env:USERNAME
        Profile    = $env:USERPROFILE
        Created    = (Get-Date).ToString('s')
        OneDrive   = @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer | Where-Object { $_ } | Sort-Object -Unique)
        CloudRoots = @(Get-CloudRoots)
        Plan       = $plan
        Excludes   = @(Get-RobocopyExcludes $Config)
        Junctions  = @(Get-Junctions)
        Distros    = @(Get-WslDistros)
        KeePass    = @(Get-KeePassFiles)
        OnePassSsh = [bool]((Test-Path "$env:USERPROFILE\.config\1Password\ssh\agent.toml") -or
                     ((Get-Content "$env:USERPROFILE\.ssh\config" -ErrorAction SilentlyContinue) -match 'openssh-ssh-agent|1Password'))
        Certs      = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) }).Count
        # nvm-windows: "C:\Program Files\nodejs" is a link to the active node version in %APPDATA%\nvm.
        NodeLink   = $(
            $l = @([Environment]::GetEnvironmentVariable('NVM_SYMLINK', 'Machine'), [Environment]::GetEnvironmentVariable('NVM_SYMLINK', 'User')) | Where-Object { $_ } | Select-Object -First 1
            $it = if ($l) { Get-Item -LiteralPath $l -Force -ErrorAction SilentlyContinue }
            if ($it -and $it.LinkType -and $it.Target) { [pscustomobject]@{ Path = $it.FullName; Target = @($it.Target)[0] } }
        )
        DriveLabels = $(
            $h = [ordered]@{}
            Get-Volume | Where-Object { $_.DriveType -eq 'Fixed' -and $_.DriveLetter } | ForEach-Object { $h["$($_.DriveLetter)"] = $_.FileSystemLabel }
            [pscustomobject]$h
        )
    }
    # A refresh runs while the user works on this PC: the sizes of the last scan are good enough.
    if ($Refresh) {
        $before = @{}
        foreach ($p in @((Read-Json "$Store\manifest.json").Plan)) { if ($p) { $before["$($p.Kind)|$($p.Path)"] = $p } }
        foreach ($p in $plan) {
            $o = $before["$($p.Kind)|$($p.Path)"]
            $p | Add-Member -NotePropertyName Bytes -NotePropertyValue $(if ($o) { [int64]$o.Bytes } else { [int64]0 }) -Force
            $p | Add-Member -NotePropertyName Files -NotePropertyValue $(if ($o) { [int64]$o.Files } else { [int64]0 }) -Force
        }
    }
    # Export measures too when it has to scan again: without sizes the progress bar and the
    # largest-first order break on the new PC.
    elseif ($Sizes -or $Command -in 'inventory', 'export') {
        # The list is ready now; only the sizes are missing. The GUI shows it and fills the sizes in as they arrive.
        # The manifest is complete already: the user can prepare the move at any moment, which stops the measuring.
        Save-Manifest $manifest
        Send-Event PLANREADY "$($plan.Count)"
        Write-Note "$($plan.Count) chunks; measuring them in parallel"
        Measure-Plan $plan @(Get-RobocopyExcludes $Config) $Speed { Save-Manifest $manifest }
        $total = ($plan | Measure-Object Bytes -Sum).Sum
        $plan | Group-Object Group | ForEach-Object {
            [pscustomobject]@{
                GB = [math]::Round((($_.Group | Measure-Object Bytes -Sum).Sum) / 1GB, 2)
                Files = ($_.Group | Measure-Object Files -Sum).Sum
                Chunks = $_.Count
                Why = $_.Group[0].Why
                Path = $_.Name
            }
        } | Sort-Object GB -Descending | Format-Table -AutoSize | Out-String -Width 300 | Set-Content "$Store\sizes.txt" -Encoding UTF8
        Write-Note ("Total to copy: {0:N1} GB in {1} chunks. Full list: $Store\sizes.txt" -f ($total / 1GB), $plan.Count) 'Green'
    }
    Save-Manifest $manifest
    Write-Note "Manifest: $Store\manifest.json ($($plan.Count) copy items)"
    Send-Event STEP 'inventory done'
    $manifest
}

function Get-ExportManifest {
    # Reuse the scan from the GUI (it has the sizes and the user's choices) when it is fresh.
    $m = Read-Json "$Store\manifest.json"
    $fresh = $m -and $m.Computer -eq $env:COMPUTERNAME -and ((Get-Date) - [datetime]$m.Created).TotalHours -lt 24
    if (-not $fresh) { $m = Invoke-Inventory }
    $off = @(Read-Json "$Store\deselected.json" | Where-Object { $_ })
    if ($off.Count) {
        # The GUI lists whole groups (C:\java); a group leaves out every chunk in it.
        $m.Plan = @($m.Plan | Where-Object { $off -notcontains $_.Group -and $off -notcontains $_.Path })
        Write-Note "$($off.Count) items left out by choice"
        Write-Json $m "$Store\manifest.json"
    }
    $m
}

function Get-LanAddresses {
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' -and $_.InterfaceAlias -notmatch 'vEthernet|WSL|Loopback|Docker' } |
        ForEach-Object { $_.IPAddress }
}

function Invoke-Share {
    # No SMB shares. The old PC serves its files through Hopover.exe --link serve (the GUI starts it), guarded
    # by the pairing code. Windows Firewall needs one allow rule for the program: TCP 47800 and UDP 47801.
    Assert-Admin
    Write-Step 'Open the link (firewall rule for Hopover.exe, pairing code on screen)'
    $exe = if ($env:HOPOVER_EXE) { $env:HOPOVER_EXE } else { $null }
    Remove-NetFirewallRule -Group 'Hopover' -ErrorAction SilentlyContinue
    $common = @{ Direction = 'Inbound'; Action = 'Allow'; Group = 'Hopover'; Profile = 'Any' }
    if ($exe) { $common.Program = $exe }
    New-NetFirewallRule -DisplayName 'Hopover link (TCP)' -Protocol TCP -LocalPort 47800 @common | Out-Null
    New-NetFirewallRule -DisplayName 'Hopover link (UDP discovery)' -Protocol UDP -LocalPort 47801 @common | Out-Null
    Write-Note 'Firewall: TCP 47800 and UDP 47801 open for Hopover.exe. Cleanup removes the rules.'
    $ips = @(Get-LanAddresses)
    Send-Event SHARE "$env:COMPUTERNAME|$env:USERDOMAIN\$env:USERNAME|$($ips -join ',')"
    Write-Host ''
    if (Test-GuiChild) { return }
    Write-Host 'Now start the link on this PC (it prints the pairing code):' -ForegroundColor Green
    Write-Host "  `"$exe`" --link serve --store `"$Store`"" -ForegroundColor Green
    Write-Host 'On the NEW PC, start Hopover.exe, choose "This is the new PC" and type the code.' -ForegroundColor Green
}

function Publish-Tool {
    # Hopover.exe carries the engine inside; without it (CLI use) the scripts move instead.
    # The tool holds no secrets, so a copy in OneDrive is the easy way onto the new PC.
    $od = @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    $targets = @("$Store\tool") + @(if ($od) { "$od\Hopover" })
    foreach ($t in $targets) {
        # A failed copy (the exe runs from this very folder, OneDrive busy) must not stop the export before the link opens.
        try {
            New-Item -ItemType Directory -Path $t -Force | Out-Null
            if ($env:HOPOVER_EXE -and (Test-Path $env:HOPOVER_EXE)) {
                $dst = Join-Path $t 'Hopover.exe'
                if ([IO.Path]::GetFullPath($env:HOPOVER_EXE) -ieq [IO.Path]::GetFullPath($dst)) { Write-Note "Tool already in $t"; continue }
                Copy-Item $env:HOPOVER_EXE $dst -Force -ErrorAction Stop
            }
            else { robocopy $Root $t /E /XD .git dist app /NFL /NDL /NJH /NJS | Out-Null }
            Write-Note "Tool copied to $t"
        } catch { Write-Warn "Tool not copied to ${t}: $($_.Exception.Message)" }
    }
}

# winget runs beside the file copy: both take hours, together they take the longer of the two.
function Start-BackgroundApps {
    $log = Join-Path $Store "logs\apps-$(Get-Date -Format yyyyMMdd-HHmmss).log"
    $ps = [powershell]::Create()
    [void]$ps.AddScript({
        param($Root, $Store, $Log, $DryRun)
        . "$Root\lib\Common.ps1"
        . "$Root\lib\System.ps1"
        $script:Log = $Log
        Import-Apps $Store (Get-MigrationConfig $Root) -WhatIf:$DryRun
    }).AddArgument($Root).AddArgument($Store).AddArgument($log).AddArgument([bool]$WhatIf)
    Write-Step 'Apps (winget) start in the background'
    Send-Event STEP 'apps start'
    Write-Note "winget log: $log"
    [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke(); Reported = $false; Seen = 0; Log = $log }
}

# The background runspace has no console: its Write-Host lines (##APP events) arrive in its information stream.
function Send-BackgroundEvents($Bg) {
    if (-not $Bg) { return }
    $info = $Bg.PS.Streams.Information
    while ($Bg.Seen -lt $info.Count) {
        $line = "$($info[$Bg.Seen].MessageData)"
        $Bg.Seen++
        if ($line.StartsWith('##APP ')) { Write-Host $line }
    }
}

function Test-BackgroundApps($Bg) {
    Send-BackgroundEvents $Bg
    if (-not $Bg -or $Bg.Reported -or -not $Bg.Handle.IsCompleted) { return }
    $Bg.Reported = $true
    try {
        [void]$Bg.PS.EndInvoke($Bg.Handle)
        if ($Bg.PS.HadErrors) { foreach ($e in $Bg.PS.Streams.Error) { Write-Warn "apps: $e" } }
        Send-Event STEP 'apps done'
        if (-not $WhatIf) { Save-StepDone 'apps' }
        Write-Note 'Apps: winget finished'
    } catch {
        Write-Warn "apps failed: $($_.Exception.Message)"
        Send-Event STEP 'apps failed'
        $script:FailedPhases += 'apps'
    }
    $Bg.PS.Dispose()
}

function Wait-BackgroundApps($Bg) {
    if (-not $Bg -or $Bg.Reported) { return }
    Write-Step 'Wait for the app installs to finish'
    # A winget installer that waits for a click would hold the move forever: 4 hours at most.
    $until = (Get-Date).AddHours(4)
    while (-not $Bg.Handle.IsCompleted -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2; Send-BackgroundEvents $Bg }
    if (-not $Bg.Handle.IsCompleted) {
        Write-Warn 'winget still runs after 4 hours: Hopover goes on. Check the apps log and install the rest by hand.'
        Send-Event STEP 'apps failed'
        $script:FailedPhases += 'apps'
        $Bg.Reported = $true
        [void]$Bg.PS.BeginStop($null, $null)  # no wait: a hung installer would block Stop()
        return
    }
    Test-BackgroundApps $Bg
}

function Invoke-Export {
    Assert-Admin
    # Export only reads and shares; a dry run would still stop WSL and open shares.
    if ($WhatIf) { throw 'export has no dry run. Run inventory to see what would move.' }
    if (-not $Refresh) { Remove-Item "$Store\ready.json" -ErrorAction SilentlyContinue }
    if ($Refresh) { $null = Invoke-Inventory }
    $m = Get-ExportManifest
    Invoke-Phase 'apps'     'Apps'                  { Export-Apps $Store $Config }
    Invoke-Phase 'env'      'Environment variables' { Export-EnvVars $Store }
    Invoke-Phase 'system'   'System: hosts, Windows features, saved logins, VPN' { Export-System $Store $Config }
    Invoke-Phase 'settings' 'Settings, Wi-Fi, drives, printers' { Export-Settings $Store $Config }
    if (-not $Refresh) {
        Invoke-Phase 'certs' 'Personal certificates' {
            if ($m.Certs) { Export-Certs $Store (Get-CertPassword 'Password for the certificate export (you type it again on the new PC)') }
            else { Write-Note 'none' }
        }
    }
    Invoke-Phase 'tasks'    'Scheduled tasks'       { Export-Tasks $Store $Config }
    Invoke-Phase 'wsl'      'WSL distros (each distro stops while it exports)' {
        if ($Refresh) { Export-WslDelta $Store } else { Export-Wsl $Store $Config }
    }
    Invoke-Phase 'docker'   'Docker named volumes'  { Export-DockerVolumes $Store }
    Invoke-Phase 'databases' 'PostgreSQL'           { Export-Databases $Store }
    if ($Refresh) { return }
    Write-Step 'Copy the tool into the store'
    Publish-Tool
    Write-Checklist $Store $m @{} | Out-Null
    Write-Json ([pscustomobject]@{ Time = (Get-Date).ToString('s') }) "$Store\ready.json"
    Invoke-Share
}

function Connect-OldPc {
    # The files come through the link (pairing code), not through a share. The name in -From is ignored.
    $script:From = 'link'
    $exe = Get-LinkExe
    $out = & $exe --link info 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "The link to the old PC is down. Connect again in Hopover. $out" }
    Write-Note "Linked to the old PC: $($out.Trim())"
}

function Request-OldPcRefresh {
    # The old PC runs its own "export -Refresh"; its output comes back through the link.
    Write-Step 'Ask the old PC for its changes'
    Send-Event STEP 'refresh start'
    & (Get-LinkExe) --link refresh | ForEach-Object { Write-Note "old PC: $_" }
    $rc = $LASTEXITCODE
    if ($rc -eq 0) { Send-Event STEP 'refresh done'; return }
    Send-Event STEP 'refresh failed'
    # 2: some of its steps failed; the rest of the catch-up still makes sense.
    if ($rc -ne 2) { throw "The old PC could not prepare its changes (exit $rc). Is Hopover still open there?" }
    Write-Warn 'Some steps on the old PC failed: the catch-up takes what is there.'
}

function Get-SyncNotes([string]$Dir, [string]$Hold, [hashtable]$Notes, [switch]$Preview) {
    # Each copy process keeps its own list. Together they tell the user what to look at.
    $lines = @(Get-ChildItem "$Dir\*.txt" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Encoding UTF8 })
    $Notes.Conflicts = @($lines | Where-Object { $_ -like 'CONFLICT*' } | ForEach-Object { ($_ -split "`t", 2)[1] })
    $Notes.Removed = @($lines | Where-Object { $_ -like 'REMOVED*' } | ForEach-Object { ($_ -split "`t", 2)[1] })
    $Notes.Replaced = @($lines | Where-Object { $_ -like 'REPLACED*' } | ForEach-Object { ($_ -split "`t", 2)[1] })
    $Notes.Hold = $Hold
    $Notes.Links = @($lines | Where-Object { $_ -like 'LINK*' } | ForEach-Object { ($_ -split "`t", 2)[1] })
    if ($lines -and -not $Preview) {
        New-Item -ItemType Directory -Path $Hold -Force | Out-Null
        $lines | Sort-Object | Set-Content "$Hold\list.txt" -Encoding UTF8
    }
    Write-Note "$($Notes.Replaced.Count) files stood here already (old PC's version wins), $($Notes.Conflicts.Count) changed on both PCs (this PC's version stays), $($Notes.Removed.Count) gone on the old PC"
}

function Invoke-Import {
    Assert-Admin
    Connect-OldPc
    $baseline = Read-Json "$Store\baseline.json"
    if ($Catchup) {
        if (-not $baseline) { throw 'Finish the first copy of the files, then catch up.' }
        Request-OldPcRefresh
    }
    Write-Step "Copy the store from the old PC to $Store"
    Send-Event STEP 'store start'
    # A preview needs the lists, not the WSL and database archives.
    $big = @(if ($WhatIf) { '*.tar', '*.tgz' })
    $rc = Invoke-LinkCopy (@('\\link\hopover-store', $Store, '/E', '/XD', 'logs', 'tool', '/XF', 'state-*.json', 'firewall-enabled.json', 'deselected.json') + $big + @('/MT:8', '/R:1', '/W:1'))
    if ($rc -ge 8) { Send-Event STEP 'store failed'; throw "copy of the store from the old PC failed (exit $rc)" }
    Send-Event STEP 'store done'
    $m = Read-Json "$Store\manifest.json"
    if (-not $m -or -not $m.Profile) { throw "$Store\manifest.json is missing or empty" }
    $old = $m.Profile
    $script:DriveLabels = $m.DriveLabels
    if ($m.User -ne $env:USERNAME) { Write-Warn "user name changed: $($m.User) -> $env:USERNAME. Paths in configs that name $old need a check." }
    $notes = @{}

    # Order: env first (installers then add to the merged PATH), apps in the background,
    # files and WSL meanwhile; settings, Docker and tasks wait for the apps they belong to.
    Invoke-Phase 'env'      'Environment variables' { $notes.EnvConflicts = @(Import-EnvVars $Store $old -WhatIf:$WhatIf) }
    Invoke-Phase 'system'   'System: hosts, Windows features, saved logins' {
        Import-System $Store -WhatIf:$WhatIf
        if (-not $WhatIf) { Import-UserUninstall $Store $old $m.Plan }
    }
    $bg = $null
    if (Use-Step 'apps') { $bg = Start-BackgroundApps } else { Send-Event STEP 'apps skipped' }
    Invoke-Phase 'files'    "Files from the old PC ($(@($m.Plan).Count) chunks)" {
        $stamp = Get-Date -Format yyyyMMdd-HHmmss
        # The hold folder is outside the store: the store gets deleted, and this is the user's data.
        # Before the first complete pass this PC holds no work yet (a restored backup, defaults of fresh installs): the old PC wins.
        # After it, this PC's changes win. Neither way loses a file. A preview takes the same decisions and moves nothing.
        $sync = @{ Hold = "C:\hopover-held\$stamp"; SyncLog = "$Store\logs\sync-$stamp" }
        if ($baseline) { $sync.SyncBase = [int64]$baseline.Ticks } else { $sync.First = $true }
        New-Item -ItemType Directory -Path $sync.SyncLog -Force | Out-Null
        $notes.CopyFailed = @(Copy-Plan $m.Plan $From $old @($m.Excludes) $Store $Speed { Test-BackgroundApps $bg } -Recopy:($Recopy -or $Catchup) -WhatIf:$WhatIf @sync)
        $notes.JunctionFailed = @(Restore-Junctions $m.Junctions $old -WhatIf:$WhatIf)
        Get-SyncNotes $sync.SyncLog $sync.Hold $notes -Preview:$WhatIf
        # Not "done" while chunks failed: the next start copies only those again.
        if ($notes.CopyFailed.Count) { throw "$($notes.CopyFailed.Count) chunks did not copy completely" }
        # From here on a change on this PC is a change of the user: a catch-up protects it. Only after a complete
        # pass: an older mark protects more, never less.
        if (-not $WhatIf) { Write-Json ([pscustomobject]@{ Ticks = "$((Get-Date).ToUniversalTime().Ticks)"; Time = (Get-Date).ToString('s') }) "$Store\baseline.json" }
    }
    if (-not $Catchup) {
        Invoke-Phase 'certs' 'Personal certificates' {
            if (Test-Path "$Store\certs\*.pfx") { Import-Certs $Store (Get-CertPassword 'Certificate export password from the old PC') -WhatIf:$WhatIf }
            else { Write-Note 'none' }
        }
    }
    Wait-BackgroundApps $bg
    # The installers changed PATH (docker, git, node): this process still has the old one.
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    # WSL after the apps: winget installs Microsoft.WSL, and wsl --install beside it would collide.
    Invoke-Phase 'wsl'      'WSL distros'           { Import-Wsl $Store -WhatIf:$WhatIf }
    # After the installs: an installer would overwrite app settings imported before it.
    Invoke-Phase 'settings' 'Windows and app settings, Wi-Fi, drives, printers' {
        Import-Settings $Store $old $Config -ChangedOnly:$Catchup -WhatIf:$WhatIf
        Restore-NodeLink $m.NodeLink $old -WhatIf:$WhatIf
    }
    Invoke-Phase 'docker'   'Docker named volumes'  { Import-DockerVolumes $Store -WhatIf:$WhatIf }
    Invoke-Phase 'databases' 'PostgreSQL'           { Import-Databases $Store -WhatIf:$WhatIf }
    Invoke-Phase 'tasks'    'Scheduled tasks (imported switched off)' { $notes.TaskFailed = @(Import-Tasks $Store $old $m.User -WhatIf:$WhatIf) }

    if ($WhatIf) {
        $bgLog = if ($bg) { $bg.Log } else { $null }
        Write-Preview $Store $m $notes $bgLog
        return
    }
    $list = Write-Checklist $Store $m $notes
    $null = Invoke-Verify
    Write-Host ''
    Write-Host "Done. Open the checklist: $list" -ForegroundColor Green
    Write-Host 'When the repos and the venvs work, run: hopover.ps1 enable-tasks' -ForegroundColor Green
}

function Invoke-Cleanup {
    Assert-Admin
    if (Get-NetFirewallRule -Group 'Hopover' -ErrorAction SilentlyContinue) {
        Remove-NetFirewallRule -Group 'Hopover' -ErrorAction SilentlyContinue
        Write-Note 'removed the Hopover firewall rules'
    }
    # Shares from an older Hopover version, if any.
    Get-SmbShare -Name 'hopover-*' -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-SmbShare -Name $_.Name -Force
        Write-Note "removed share $($_.Name)"
    }
    $rules = @(Read-Json "$Store\firewall-enabled.json" | Where-Object { $_ })
    if ($rules) {
        $rules | ForEach-Object { Disable-NetFirewallRule -Name $_ -ErrorAction SilentlyContinue }
        Remove-Item "$Store\firewall-enabled.json"
        Write-Note "switched off $($rules.Count) firewall rules again"
    }
    if (-not $Purge) {
        Write-Note "The store stays in $Store. It holds Wi-Fi passwords and env values: delete it when the new PC works."
        return
    }
    # A wrong -Store must never empty another folder.
    if ((Split-Path $Store -Leaf) -ne 'hopover-store') { throw "not a Hopover store: $Store" }
    Write-Note "deleting $Store"
    $script:Log = $null        # the log file lives in the store
    Remove-Item -LiteralPath $Store -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $Store) { throw "$Store is still there: a file in it is in use. Close the other programs and try again." }
    Write-Note 'Cleaned up.'
}

try {
    switch ($Command) {
        'inventory'    { $null = Invoke-Inventory }
        'export'       { Invoke-Export }
        'share'        { Invoke-Share }
        'import'       { Invoke-Import }
        'enable-tasks' { Assert-Admin; Write-Step 'Enable migrated tasks'; Enable-MigratedTasks $Store }
        'verify'       { $null = Invoke-Verify }
        'cleanup'      { Invoke-Cleanup }
    }
    if ($script:FailedPhases.Count) {
        Send-Event END "$Command partial $($script:FailedPhases -join ',')"
        exit 2
    }
    Send-Event END "$Command ok"
} catch {
    Write-Warn $_.Exception.Message
    Send-Event END "$Command failed $($_.Exception.Message)"
    exit 1
}
