# CHECKLIST.md (what no script can move) and the verify report.

function Write-Checklist([string]$Store, $Manifest, [hashtable]$Notes) {
    $s = Read-Json "$Store\settings\settings.json"
    $L = New-Object System.Collections.Generic.List[string]
    $L.Add("# Migration checklist - $($Manifest.Computer) -> $env:COMPUTERNAME")
    $L.Add('')
    $L.Add('Tick each line. The script did everything that is not on this list.')
    $L.Add('')
    $L.Add('## Sign in (the apps are installed)')
    $L.Add('Click **Sign in to your accounts** in Hopover: it opens each one in this order, password managers first.')
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
    $others = @($Manifest.CloudRoots | Where-Object { $_ -and -not (Test-UnderAny $_ @($Manifest.OneDrive)) })
    if ($others) {
        $L.Add('- [ ] Other cloud folders sync after you sign in to their app. Hopover does not copy them:')
        foreach ($o in $others) { $L.Add("  - ``$o``") }
    }
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
    $L.Add('- [ ] Sign out and in once: the taskbar, Start pins, Explorer and theme settings apply at sign-in. Restart once if Hopover moved Windows features or a key remap.')
    $L.Add("- [ ] PC name: the old PC was $($Manifest.Computer), this one is $env:COMPUTERNAME. Rename it in Settings > System > About if you want the old name.")
    $L.Add('- [ ] Lock screen picture and account picture: set them in Settings > Personalization (Windows keeps them outside the profile).')
    $L.Add('- [ ] Default apps (browser, PDF, mail): Windows protects them; set them in Settings > Apps > Default apps.')
    $L.Add('- [ ] Display scale and monitor layout: set them in Settings > System > Display (they belong to the hardware).')
    $L.Add('')
    $L.Add('## Saved logins that Windows does not give out (domain passwords, Microsoft sign-ins)')
    $moved = @(Read-Json "$Store\system\logins.json" | Where-Object { $_ } | ForEach-Object { $_.Target })
    $rest = @($s.Credentials | Where-Object { $_ } | Sort-Object -Unique | Where-Object { $t = $_; -not ($moved | Where-Object { $t -like "*$_*" }) })
    if ($rest) { foreach ($c in $rest) { $L.Add("- [ ] $c") } } else { $L.Add('- none') }
    $L.Add('')
    $L.Add('## Programs that came as a folder, not through an installer')
    $L.Add('Start each one once. A program with a licence, a driver or a service can ask to be installed again.')
    $prog = @(Read-Json "$Store\apps\programs.json" | Where-Object { $_ })
    foreach ($p in $prog) { $L.Add("- [ ] $($p.Name) (``$($p.Path)``)") }
    if (-not $prog) { $L.Add('- none') }
    $L.Add('')
    $L.Add('## Apps that no package source has')
    $nf = @(Read-Json "$Store\apps\not-found.json" | Where-Object { $_ })
    foreach ($u in $nf) { $L.Add("- [ ] $u") }
    foreach ($u in @(Read-Json "$Store\apps\failed.json" | Where-Object { $_ })) { $L.Add("- [ ] $u (install failed)") }
    if (-not $nf) { $L.Add('- none') }
    $L.Add("- [ ] Compare with the full list of the old PC: ``$Store\apps\registry.json``")
    $L.Add('')
    $L.Add('## Windows and network')
    foreach ($f in @(Read-Json "$Store\system\features-absent.json" | Where-Object { $_ })) { $L.Add("- [ ] Windows feature $f is not available on this edition (Windows Home has no Hyper-V; WSL and Docker work without it).") }
    foreach ($w in @(Get-ChildItem "$Store\system\wireguard\*.conf" -ErrorAction SilentlyContinue)) {
        $L.Add("- [ ] WireGuard: Import tunnel from file ``$($w.FullName)``. Stop the tunnel on the old PC first: one key, one PC at a time.")
    }
    foreach ($f in @(Read-Json "$Store\settings\failed.json" | Where-Object { $_ })) { $L.Add("- [ ] Registry import failed: $f") }
    $L.Add('- [ ] Docker images come back with `docker-compose up` or a pull. Hopover moves named volumes, not the Docker disk (a running disk cannot be copied).')
    $L.Add('- [ ] Steam games: Steam > Settings > Storage shows them if you kept the steamapps line in the list. Epic, GOG, EA and Ubisoft download again.')
    $wslMap = Read-Json "$Store\wsl\imported.json"
    if ($wslMap) { foreach ($p in $wslMap.PSObject.Properties | Where-Object { $_.Name -ne $_.Value }) { $L.Add("- [ ] WSL: a distro $($p.Name) stood here already and stays. Yours is $($p.Value), the default now.") } }
    foreach ($v in @(Read-Json "$Store\docker\kept.json" | Where-Object { $_ })) { $L.Add("- [ ] Docker volume $v changed on this PC and stays. The old PC's newer data: ``$Store\docker\$v.tgz``.") }
    foreach ($p in @(Get-ChildItem "$Store\postgres\*.applied" -ErrorAction SilentlyContinue)) { $L.Add("- [ ] PostgreSQL $($p.BaseName): this PC's data stays. The old PC's latest data: ``$Store\postgres\$($p.BaseName).tgz``.") }
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
        $busy = @(Get-Content "$Store\logs\robocopy-errors.log" -ErrorAction SilentlyContinue | Where-Object { $_ -like 'ERROR not copied*' })
        if ($busy) { $L.Add("- [ ] $($busy.Count) files were locked or kept changing on the old PC (list: ``logs\robocopy-errors.log``). Close the apps there and catch up.") }
        foreach ($e in @(Read-Json "$Store\env-before.json" | Where-Object { $_ })) { $L.Add("- [ ] Env var $($e.Scope) $($e.Name) stood here already: the old PC's value replaced it. The earlier value: ``env-before.json``.") }
        if ($Notes.Hold -and $Notes.Replaced.Count) {
            $L.Add("- [ ] $($Notes.Replaced.Count) files stood on this PC already (restored backup, fresh installs). The old PC's version replaced them. The earlier files: ``$($Notes.Hold)\new-pc-before``.")
        }
        if ($Notes.Hold -and ($Notes.Conflicts.Count -or $Notes.Removed.Count)) {
            $L.Add("- [ ] Catch-up: $($Notes.Conflicts.Count) files changed on both PCs. This PC's version stays. The old PC's version is in ``$($Notes.Hold)\old-pc-version``.")
            $L.Add("- [ ] Catch-up: $($Notes.Removed.Count) files are gone on the old PC. They moved to ``$($Notes.Hold)\removed-on-old-pc``. The list is ``$($Notes.Hold)\list.txt``.")
        }
        $L.Add('')
    }
    $L.Add('## Last steps')
    $L.Add('- [ ] Open a new terminal (new PATH). Check `git`, `python`, `java -version`, `node`, `ssh -T git@github.com`.')
    $L.Add('- [ ] Sales dashboard: `cd C:\java\workspace\aivory-sales-manager; docker-compose up -d`, then `curl http://localhost:7777/api/sales`.')
    $L.Add('- [ ] Stop the scheduled tasks on the old PC first. Then `hopover.ps1 enable-tasks` here (they arrive switched off), or they run twice.')
    $L.Add('- [ ] Last catch-up: stop working on the old PC, click **Catch up** here, then work only here.')
    $L.Add('- [ ] `hopover.ps1 verify` shows no gaps.')
    $L.Add("- [ ] When all works: click **Clean up** on both PCs. That deletes ``$Store`` (Wi-Fi passwords, tokens, saved logins). Then look through ``C:\hopover-held``.")
    $path = Join-Path $Store 'CHECKLIST.md'
    $L | Set-Content $path -Encoding UTF8
    $path
}

# The preview: what a real run would do, as text to read before it starts. It changes nothing and holds no secret
# (names of env vars and Wi-Fi networks, no values).
function Write-Preview([string]$Store, $Manifest, [hashtable]$Notes, [string]$AppsLog) {
    $L = New-Object System.Collections.Generic.List[string]
    $section = {
        param($Title, $Items, [string]$Empty = 'none')
        $L.Add(''); $L.Add("=== $Title"); $L.Add('')
        if (@($Items | Where-Object { $_ }).Count) { foreach ($i in $Items) { $L.Add("$i") } } else { $L.Add($Empty) }
    }
    $L.Add("Hopover preview: $($Manifest.Computer) -> $env:COMPUTERNAME, $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
    $L.Add('Nothing changed on either PC. The lists show the state of this moment; a real run decides again.')
    $kind = if (Test-Path "$Store\baseline.json") { 'catch-up: changes on this PC win, a clash keeps both' } else { 'first move: the old PC wins, what stands here moves to C:\hopover-held' }
    $L.Add("Rule for this run: $kind")
    $L.Add('')
    $L.Add("Files that would be replaced: $(@($Notes.Replaced).Count)")
    $L.Add("Files changed on both PCs:    $(@($Notes.Conflicts).Count)")
    $L.Add("Files gone on the old PC:     $(@($Notes.Removed).Count)")
    $L.Add("Junctions and links to make:  $(@($Notes.Links).Count)")
    & $section 'Data per folder (only what is missing or different)' @(Get-Content "$Store\preview-files.txt" -Encoding UTF8 -ErrorAction SilentlyContinue)
    & $section 'Stands here already: the old PC replaces it, the earlier file goes to C:\hopover-held' @($Notes.Replaced | Sort-Object)
    & $section 'Changed on both PCs: this PC keeps its version, the old version goes to C:\hopover-held' @($Notes.Conflicts | Sort-Object)
    & $section 'Gone on the old PC: moves to C:\hopover-held' @($Notes.Removed | Sort-Object)
    & $section 'Junctions and links' @($Notes.Links | Sort-Object)
    & $section 'Apps' @(if ($AppsLog) { Get-Content $AppsLog -Encoding UTF8 -ErrorAction SilentlyContinue })
    & $section 'All steps in order' @(Get-Content $script:Log -Encoding UTF8 -ErrorAction SilentlyContinue)
    & $section 'What Hopover leaves out' @(
        'Browser profiles (they sync after sign-in), OneDrive and SeaDrive folders (they sync after sign-in)',
        'The Docker disk with images (a running disk cannot be copied; images come back with a pull)',
        'Temp folders, Windows own folders, drivers, Epic, GOG, EA and Ubisoft games',
        'Passwords that Windows does not give out: domain passwords, Microsoft sign-ins, Windows Hello')
    $path = Join-Path $Store 'preview.txt'
    $L | Set-Content $path -Encoding UTF8
    Send-Event PREVIEW $path
    Write-Host "Preview: $path" -ForegroundColor Green
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
    foreach ($id in $was | Where-Object { $now -notcontains $_ -and $Config.appSkip -notcontains $_ }) { $gaps.Add("app $id") }
    $choco = Join-Path $env:ProgramData 'chocolatey\bin\choco.exe'
    $wanted = @(Read-Json "$Store\apps\choco.json" | Where-Object { $_ })
    $has = @(if ($wanted -and (Test-Path $choco)) { & $choco list --limit-output 2>$null | ForEach-Object { ("$_" -split '\|')[0] } })
    foreach ($p in $wanted | Where-Object { $has -notcontains $_.Id }) { $gaps.Add("choco $($p.Id)") }

    $vars = Read-Json "$Store\env.json"
    foreach ($p in $vars.User.PSObject.Properties) {
        if ($null -eq [Environment]::GetEnvironmentVariable($p.Name, 'User')) { $gaps.Add("user env $($p.Name)") }
    }
    foreach ($p in $vars.Machine.PSObject.Properties) {
        if ($null -eq [Environment]::GetEnvironmentVariable($p.Name, 'Machine')) { $gaps.Add("machine env $($p.Name)") }
    }
    foreach ($s in @(Get-ChildItem "$Store\postgres\*.tgz" -ErrorAction SilentlyContinue)) {
        if (-not (Test-Path "$Store\postgres\$($s.BaseName).applied")) { $gaps.Add("database $($s.BaseName)") }
    }
    foreach ($it in $m.Plan) {
        $dst = ConvertTo-TargetPath $it.Path $m.Profile
        if (-not (Test-Path -LiteralPath $dst)) { $gaps.Add("$($it.Kind) $dst") }
    }
    foreach ($j in $m.Junctions) { if (-not (Test-Path -LiteralPath $j.Path)) { $gaps.Add("junction $($j.Path)") } }
    $have = @(Get-WslDistros)
    $wslMap = Read-Json "$Store\wsl\imported.json"
    foreach ($d in @(Read-Json "$Store\wsl\distros.json")) {
        if (-not $d) { continue }
        $n = if ($wslMap -and $wslMap.($d.Name)) { $wslMap.($d.Name) } else { $d.Name }
        if ($have -notcontains $n) { $gaps.Add("wsl $n") }
    }
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
