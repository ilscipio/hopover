# CHECKLIST.md (what no script can move) and the verify report.

function Write-Checklist([string]$Store, $Manifest, [hashtable]$Notes) {
    $s = Read-Json "$Store\settings\settings.json"
    $L = New-Object System.Collections.Generic.List[string]
    $L.Add("# Migration checklist - $($Manifest.Computer) -> $env:COMPUTERNAME")
    $L.Add('')
    $L.Add('Tick each line. The script did everything that is not on this list.')
    $L.Add('')
    $L.Add('## Sign in (the apps are installed)')
    $L.Add('- [ ] 1Password: sign in with the account mail, password and Secret Key (Emergency Kit).')
    if ($Manifest.OnePassSsh) {
        $L.Add('- [ ] 1Password: Settings > Developer > turn on "Use the SSH agent" (the old PC used it).')
    }
    $L.Add('- [ ] 1Password: Settings > Developer > "Integrate with 1Password CLI", if you use `op`.')
    $L.Add('- [ ] 1Password: install the browser extension in each browser.')
    $L.Add('- [ ] KeePass: open each database once and check the key file:')
    foreach ($k in $Manifest.KeePass) {
        $where = if (Test-UnderAny $k $Manifest.OneDrive) { 'syncs by OneDrive' } else { 'copied by the script' }
        $L.Add("  - ``$k`` ($where)")
    }
    $L.Add('- [ ] OneDrive: sign in to each account. Wait for the first sync before you open the files:')
    foreach ($o in $Manifest.OneDrive) { $L.Add("  - ``$o``") }
    if ($s.ShellFolders) {
        $kfm = $s.ShellFolders.PSObject.Properties | Where-Object { $_.Value -and (Test-UnderAny ([Environment]::ExpandEnvironmentVariables($_.Value)) $Manifest.OneDrive) }
        if ($kfm) { $L.Add("- [ ] OneDrive: Settings > Sync and back up > Manage back up: turn on $(($kfm.Name -join ', ')) (the old PC kept them in OneDrive).") }
    }
    $L.Add('- [ ] SeaDrive: sign in to drive.ilscipio.com. Then check that the C:\Seafile folders open.')
    $L.Add('- [ ] Browsers (Chrome, Edge, Firefox): sign in to sync. Passwords and extensions come with the sync.')
    $L.Add('- [ ] Microsoft 365 / Outlook: sign in; add the shared mailboxes again.')
    $L.Add('- [ ] JetBrains Toolbox: sign in; it installs the IDEs; licences come with the account.')
    $L.Add('- [ ] Docker Desktop: start it once, sign in, then run `hopover.ps1 import -Only docker` if it was not running.')
    $L.Add('- [ ] Claude Code: run `claude` and sign in. The `.claude` folder and `.claude.json` are copied.')
    $L.Add('- [ ] GitHub CLI: `gh auth login`. Git Credential Manager asks again on the first push.')
    $L.Add('- [ ] Windows Hello (face/PIN/fingerprint): set it up in Settings > Accounts.')
    $L.Add('- [ ] Sign out and in once: the taskbar, Explorer and theme settings apply at sign-in.')
    $L.Add('- [ ] Default apps (browser, PDF, mail): Windows protects them; set them in Settings > Apps > Default apps.')
    $L.Add('- [ ] Display scale and monitor layout: set them in Settings > System > Display (they belong to the hardware).')
    $L.Add('')
    $L.Add('## Saved logins in Windows Credential Manager (secrets cannot be exported)')
    if ($s.Credentials) { foreach ($c in $s.Credentials | Sort-Object -Unique) { $L.Add("- [ ] $c") } } else { $L.Add('- none') }
    $L.Add('')
    $L.Add('## Apps that winget cannot install')
    $um = Get-Content "$Store\apps\winget-unmatched.txt" -ErrorAction SilentlyContinue
    if ($um) { foreach ($u in $um) { $L.Add("- [ ] $($u.Trim())") } } else { $L.Add('- none') }
    $L.Add("- [ ] Compare with the full list of the old PC: ``$Store\apps\registry.json``")
    $L.Add('')
    $L.Add('## Hardware and local devices')
    foreach ($p in $s.Printers | Where-Object { $_.Type -ne 'Connection' -and $_.PortName -notmatch '^(PORTPROMPT|nul|SHRFAX|XPS|OneNote|PDF)' -and $_.Name -notmatch 'PDF|XPS|OneNote|Fax' }) {
        $L.Add("- [ ] Printer $($p.Name) ($($p.DriverName), port $($p.PortName))")
    }
    $L.Add('- [ ] GPU driver (NVIDIA App), audio interface, webcam and mic drivers.')
    $L.Add('')
    if ($Notes.Count) {
        $L.Add('## Problems from this run')
        foreach ($c in $Notes.EnvConflicts) { $L.Add("- [ ] Env var differs, new value kept: $c") }
        foreach ($c in $Notes.CopyFailed) { $L.Add("- [ ] Copy failed (see logs\robocopy-errors.log): $c") }
        foreach ($c in $Notes.TaskFailed) { $L.Add("- [ ] Task not imported: $c") }
        foreach ($c in $Notes.JunctionFailed) { $L.Add("- [ ] Junction not made; run as admin after SeaDrive sign-in: ``$c``") }
        foreach ($c in Get-Content "$Store\certs\not-exported.txt" -ErrorAction SilentlyContinue) { $L.Add("- [ ] Certificate: $c") }
        $L.Add('')
    }
    $L.Add('## Last steps')
    $L.Add('- [ ] Open a new terminal (new PATH). Check `git`, `python`, `java -version`, `node`, `ssh -T git@github.com`.')
    $L.Add('- [ ] Sales dashboard: `cd C:\java\workspace\aivory-sales-manager; docker-compose up -d`, then `curl http://localhost:7777/api/sales`.')
    $L.Add('- [ ] `hopover.ps1 enable-tasks` (the scheduled tasks are imported switched off).')
    $L.Add('- [ ] `hopover.ps1 verify` shows no gaps.')
    $L.Add("- [ ] Old PC: ``hopover.ps1 cleanup``, then delete ``$Store`` on both PCs (it holds Wi-Fi passwords).")
    $path = Join-Path $Store 'CHECKLIST.md'
    $L | Set-Content $path -Encoding UTF8
    $path
}

function Invoke-Verify {
    Write-Step 'Verify the new PC against the manifest'
    $m = Read-Json "$Store\manifest.json"
    if (-not $m) { Write-Warn "no manifest in $Store"; return @() }
    $script:DriveLabels = $m.DriveLabels
    $gaps = New-Object System.Collections.Generic.List[string]

    $tmp = Join-Path $env:TEMP 'hopover-verify-winget.json'
    winget export -o $tmp --accept-source-agreements 2>&1 | Out-Null
    $now = @((Read-Json $tmp).Sources.Packages.PackageIdentifier)
    $was = @((Read-Json "$Store\apps\winget.json").Sources.Packages.PackageIdentifier)
    foreach ($id in $was | Where-Object { $now -notcontains $_ }) { $gaps.Add("app $id") }

    $vars = Read-Json "$Store\env.json"
    foreach ($p in $vars.User.PSObject.Properties) {
        if ($null -eq [Environment]::GetEnvironmentVariable($p.Name, 'User')) { $gaps.Add("user env $($p.Name)") }
    }
    foreach ($it in $m.Plan) {
        $dst = ConvertTo-TargetPath $it.Path $m.Profile
        if (-not (Test-Path -LiteralPath $dst)) { $gaps.Add("$($it.Kind) $dst") }
    }
    foreach ($j in $m.Junctions) { if (-not (Test-Path -LiteralPath $j.Path)) { $gaps.Add("junction $($j.Path)") } }
    $have = @(Get-WslDistros)
    foreach ($d in @(Read-Json "$Store\wsl\distros.json")) { if ($d -and $have -notcontains $d.Name) { $gaps.Add("wsl $($d.Name)") } }
    foreach ($t in @(Read-Json "$Store\tasks\tasks.json")) {
        if ($t -and -not (Get-ScheduledTask -TaskName $t.Name -TaskPath $t.Path -ErrorAction SilentlyContinue)) { $gaps.Add("task $($t.Path)$($t.Name)") }
    }

    $out = Join-Path $Store 'verify.txt'
    $gaps | Set-Content $out -Encoding UTF8
    if ($gaps.Count) {
        $gaps | Select-Object -First 40 | ForEach-Object { Write-Warn $_ }
        Write-Note "$($gaps.Count) gaps. Full list: $out" 'Yellow'
    } else { Write-Note 'No gaps.' 'Green' }
    $gaps
}
