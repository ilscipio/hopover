<#
.SYNOPSIS
  Hopover: moves a Windows PC to a new one over the LAN. Apps, settings, keys, WSL, tasks, files.
  The GUI (Hopover.cmd) drives this script; it also runs alone.

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
    [switch]$WhatIf
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Root = $PSScriptRoot
. (Join-Path $Root 'lib\Common.ps1')
. (Join-Path $Root 'lib\Files.ps1')
. (Join-Path $Root 'lib\System.ps1')
. (Join-Path $Root 'lib\Report.ps1')

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

function Invoke-Inventory {
    Write-Step 'Inventory'
    Send-Event STEP 'inventory start'
    $plan = @(Get-CopyPlan $Config)
    if ($Sizes -or $Command -eq 'inventory') {
        $ex = Get-RobocopyExcludes $Config
        $i = 0
        foreach ($it in $plan) {
            $i++
            Send-Event SCAN "$i $($plan.Count) $($it.Path)"
            $m = Measure-PlanItem $it $ex
            $it | Add-Member -NotePropertyName Bytes -NotePropertyValue ([int64]$m.Bytes) -Force
            $it | Add-Member -NotePropertyName Files -NotePropertyValue ([int64]$m.Files) -Force
        }
        $total = ($plan | Measure-Object Bytes -Sum).Sum
        $plan | Sort-Object Bytes -Descending |
            Select-Object @{ n = 'GB'; e = { [math]::Round($_.Bytes / 1GB, 2) } }, Files, Why, Path |
            Format-Table -AutoSize | Out-String -Width 300 | Set-Content "$Store\sizes.txt" -Encoding UTF8
        Write-Note ("Total to copy: {0:N1} GB in {1} items. Full list: $Store\sizes.txt" -f ($total / 1GB), $plan.Count) 'Green'
    }
    $manifest = [pscustomobject]@{
        Computer   = $env:COMPUTERNAME
        User       = $env:USERNAME
        Profile    = $env:USERPROFILE
        Created    = (Get-Date).ToString('s')
        OneDrive   = @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer | Where-Object { $_ } | Sort-Object -Unique)
        Plan       = $plan
        Excludes   = @(Get-RobocopyExcludes $Config)
        Junctions  = @(Get-Junctions)
        Distros    = @(Get-WslDistros)
        KeePass    = @(Get-KeePassFiles)
        OnePassSsh = [bool]((Test-Path "$env:USERPROFILE\.config\1Password\ssh\agent.toml") -or
                     ((Get-Content "$env:USERPROFILE\.ssh\config" -ErrorAction SilentlyContinue) -match 'openssh-ssh-agent|1Password'))
        Certs      = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) }).Count
        DriveLabels = $(
            $h = [ordered]@{}
            Get-Volume | Where-Object { $_.DriveType -eq 'Fixed' -and $_.DriveLetter } | ForEach-Object { $h["$($_.DriveLetter)"] = $_.FileSystemLabel }
            [pscustomobject]$h
        )
    }
    Write-Json $manifest "$Store\manifest.json"
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
        $m.Plan = @($m.Plan | Where-Object { $off -notcontains $_.Path })
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
    Assert-Admin
    Write-Step 'Share the store and the drives (read-only, this user only)'
    $me = "$env:USERDOMAIN\$env:USERNAME"
    $shares = @{ 'hopover-store' = $Store }
    Get-Volume | Where-Object { $_.DriveType -eq 'Fixed' -and $_.DriveLetter } |
        ForEach-Object { $shares["hopover-$("$($_.DriveLetter)".ToLower())"] = "$($_.DriveLetter):\" }
    foreach ($n in $shares.Keys) {
        if (Get-SmbShare -Name $n -ErrorAction SilentlyContinue) { continue }
        New-SmbShare -Name $n -Path $shares[$n] -ReadAccess $me | Out-Null
        Write-Note "\\$env:COMPUTERNAME\$n -> $($shares[$n])"
    }
    # "File and Printer Sharing" rules, by resource id so it works on a German Windows too.
    # Never on the Public profile; cleanup switches off again what this step switched on.
    $rules = @(Get-NetFirewallRule -Group '@FirewallAPI.dll,-28502' -ErrorAction SilentlyContinue |
        Where-Object { $_.Enabled -eq 'False' -and "$($_.Profile)" -notmatch 'Public|Any' })
    if ($rules) {
        $rules | Enable-NetFirewallRule
        $prev = @(Read-Json "$Store\firewall-enabled.json" | Where-Object { $_ })
        Write-Json @($prev + @($rules.Name) | Sort-Object -Unique) "$Store\firewall-enabled.json"
    }
    $ips = @(Get-LanAddresses)
    Send-Event SHARE "$env:COMPUTERNAME|$me|$($ips -join ',')"
    Write-Host ''
    Write-Host 'On the NEW PC, start Hopover.cmd from OneDrive\Hopover, or run in an administrator PowerShell:' -ForegroundColor Green
    Write-Host "  net use \\$env:COMPUTERNAME\IPC`$ /user:$me" -ForegroundColor Green
    Write-Host "  powershell -ExecutionPolicy Bypass -File \\$env:COMPUTERNAME\hopover-store\tool\hopover.ps1 import -From $env:COMPUTERNAME" -ForegroundColor Green
    Write-Host "PC name: $env:COMPUTERNAME  IP: $($ips -join ', ')  user: $me"
}

function Publish-Tool {
    robocopy $Root "$Store\tool" /E /XD .git /NFL /NDL /NJH /NJS | Out-Null
    # The tool holds no secrets, so a copy in OneDrive is the easy way onto the new PC.
    $od = @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($od) {
        robocopy $Root "$od\Hopover" /E /XD .git /NFL /NDL /NJH /NJS | Out-Null
        Write-Note "Tool copied to $od\Hopover"
    }
}

function Invoke-Export {
    Assert-Admin
    # Export only reads and shares; a dry run would still stop WSL and open shares.
    if ($WhatIf) { throw 'export has no dry run. Run inventory to see what would move.' }
    $m = Get-ExportManifest
    Invoke-Phase 'apps'     'Apps'                  { Export-Apps $Store }
    Invoke-Phase 'env'      'Environment variables' { Export-EnvVars $Store }
    Invoke-Phase 'settings' 'Settings, Wi-Fi, drives, printers' { Export-Settings $Store $Config }
    Invoke-Phase 'certs'    'Personal certificates' {
        if ($m.Certs) { Export-Certs $Store (Get-CertPassword 'Password for the certificate export (you type it again on the new PC)') }
        else { Write-Note 'none' }
    }
    Invoke-Phase 'tasks'    'Scheduled tasks'       { Export-Tasks $Store $Config }
    Invoke-Phase 'wsl'      'WSL distros (each distro stops while it exports)' { Export-Wsl $Store $Config }
    Invoke-Phase 'docker'   'Docker named volumes'  { Export-DockerVolumes $Store }
    Write-Step 'Copy the tool into the store'
    Publish-Tool
    Write-Checklist $Store $m @{} | Out-Null
    Invoke-Share
}

function Connect-OldPc {
    if (-not $From) { throw 'Give the old PC name: -From OLDPC' }
    if (Test-Path "\\$From\hopover-store\manifest.json") { return }
    if (Test-GuiChild) { throw "Cannot read \\$From\hopover-store. Connect again in Hopover." }
    Write-Note "Log in to \\$From (user name of the old PC, e.g. $From\$env:USERNAME or your Microsoft account mail)"
    net use "\\$From\IPC$" /persistent:no
    if (-not (Test-Path "\\$From\hopover-store\manifest.json")) { throw "Cannot read \\$From\hopover-store. Run 'hopover.ps1 export' on the old PC first." }
}

function Invoke-Import {
    Assert-Admin
    Connect-OldPc
    Write-Step "Copy the store from \\$From to $Store"
    robocopy "\\$From\hopover-store" $Store /E /XD logs tool /XF 'state-*.json' firewall-enabled.json deselected.json /MT:8 /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copy of \\$From\hopover-store failed (robocopy $LASTEXITCODE)" }
    $m = Read-Json "$Store\manifest.json"
    if (-not $m -or -not $m.Profile) { throw "$Store\manifest.json is missing or empty" }
    $old = $m.Profile
    $script:DriveLabels = $m.DriveLabels
    if ($m.User -ne $env:USERNAME) { Write-Warn "user name changed: $($m.User) -> $env:USERNAME. Paths in configs that name $old need a check." }
    $notes = @{}

    Invoke-Phase 'apps'     'Apps (winget, this takes a while)' { Import-Apps $Store $Config -WhatIf:$WhatIf }
    Invoke-Phase 'env'      'Environment variables' { $notes.EnvConflicts = @(Import-EnvVars $Store $old -WhatIf:$WhatIf) }
    Invoke-Phase 'files'    "Files from \\$From ($(@($m.Plan).Count) items)" {
        $notes.CopyFailed = @(Copy-Plan $m.Plan $From $old @($m.Excludes) "$Store\logs" -WhatIf:$WhatIf)
        $notes.JunctionFailed = @(Restore-Junctions $m.Junctions $old -WhatIf:$WhatIf)
        # Not "done" while items failed: the next start copies again (robocopy skips what is there).
        if ($notes.CopyFailed.Count) { throw "$($notes.CopyFailed.Count) items did not copy completely" }
    }
    Invoke-Phase 'settings' 'Settings, Wi-Fi, drives, printers' { Import-Settings $Store $old -WhatIf:$WhatIf }
    Invoke-Phase 'certs'    'Personal certificates' {
        if (Test-Path "$Store\certs\*.pfx") { Import-Certs $Store (Get-CertPassword 'Certificate export password from the old PC') -WhatIf:$WhatIf }
        else { Write-Note 'none' }
    }
    Invoke-Phase 'wsl'      'WSL distros'           { Import-Wsl $Store -WhatIf:$WhatIf }
    Invoke-Phase 'docker'   'Docker named volumes'  { Import-DockerVolumes $Store -WhatIf:$WhatIf }
    Invoke-Phase 'tasks'    'Scheduled tasks (imported switched off)' { $notes.TaskFailed = @(Import-Tasks $Store $old $m.User -WhatIf:$WhatIf) }

    $list = Write-Checklist $Store $m $notes
    $null = Invoke-Verify
    Write-Host ''
    Write-Host "Done. Open the checklist: $list" -ForegroundColor Green
    Write-Host 'When the repos and the venvs work, run: hopover.ps1 enable-tasks' -ForegroundColor Green
}

function Invoke-Cleanup {
    Assert-Admin
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
    Write-Note "The store stays in $Store. It holds Wi-Fi passwords and env values: delete it when the new PC works."
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
