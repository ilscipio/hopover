# Shared helpers. Dot-sourced by hopover.ps1.

$script:Log = $null

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
    if ($script:Log) { Add-Content -Path $script:Log -Value "==> $Text" -Encoding UTF8 }
}

function Write-Note([string]$Text, [string]$Color = 'Gray') {
    Write-Host "    $Text" -ForegroundColor $Color
    if ($script:Log) { Add-Content -Path $script:Log -Value "    $Text" -Encoding UTF8 }
}

function Write-Warn([string]$Text) { Write-Note "WARN $Text" 'Yellow' }

# Lines that start with ## are events for the GUI: "##STEP apps start", "##ITEM 3 245 1048576 C:\java".
function Send-Event([string]$Kind, [string]$Data) { Write-Host "##$Kind $Data" }

function Read-Json([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    $text = Get-Content -Path $Path -Raw -Encoding UTF8
    if (-not $text -or -not $text.Trim()) { return $null }
    # PS 5.1 returns a JSON array as ONE object; unroll it so @(Read-Json x) is the items.
    $r = $text | ConvertFrom-Json
    if ($r -is [array]) { foreach ($i in $r) { $i } } else { $r }
}

function Write-Json($Object, [string]$Path) {
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    # -InputObject keeps an empty or one-item array an array; the pipeline would unroll it.
    ConvertTo-Json -InputObject $Object -Depth 8 | Set-Content -Path $Path -Encoding UTF8
}

function Protect-Folder([string]$Path) {
    # The store holds Wi-Fi keys, env tokens and .pfx files: only this user, admins and SYSTEM.
    # SIDs, not names: "Administrators" is "Administratoren" on a German Windows.
    $me = "$env:USERDOMAIN\$env:USERNAME"
    icacls $Path /inheritance:r /grant:r "${me}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' /C /Q | Out-Null
}

function Test-GuiChild { [bool]$env:HOPOVER_GUI }

function Expand-PathVars([string]$Path) {
    # Expands %VAR% and leaves unknown vars empty, so "%OneDrive%" on a PC without OneDrive becomes "".
    $out = [regex]::Replace($Path, '%([^%]+)%', {
        param($m) [Environment]::GetEnvironmentVariable($m.Groups[1].Value)
    })
    if ($out -match '^\\' -or $out -eq '') { return $null }
    $out
}

function Get-MigrationConfig([string]$Root) {
    Read-Json (Join-Path $Root 'config\include.json')
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Wsl([string[]]$Arguments) {
    # wsl.exe writes UTF-16 unless WSL_UTF8 is set.
    $env:WSL_UTF8 = '1'
    $out = & wsl.exe @Arguments 2>&1
    ($out | ForEach-Object { "$_" -replace "`0", '' }) | Where-Object { $_.Trim() -ne '' }
}

# wsl --export and --import say nothing for an hour: the size of the growing file is the progress. Sets $script:WslExit.
function Invoke-WslWatched([string[]]$Arguments, [string]$Watch, [int64]$Total, [string]$Step) {
    $env:WSL_UTF8 = '1'
    $o = [IO.Path]::GetTempFileName()
    $e = [IO.Path]::GetTempFileName()
    $line = ($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $p = Start-Process wsl.exe -ArgumentList $line -PassThru -WindowStyle Hidden -RedirectStandardOutput $o -RedirectStandardError $e
    $null = $p.Handle        # without this, ExitCode stays empty after the wait
    while (-not $p.WaitForExit(3000)) {
        $f = Get-Item -LiteralPath $Watch -Force -ErrorAction SilentlyContinue
        if ($f) {
            $text = '{0:N1} GB' -f ($f.Length / 1GB)
            if ($Total -gt 0) { $text += ' of about {0:N0} GB' -f ($Total / 1GB) }
            Send-Event DETAIL "$Step $text"
        }
    }
    $script:WslExit = $p.ExitCode
    Send-Event DETAIL "$Step "
    @(Get-Content $o, $e -ErrorAction SilentlyContinue) | ForEach-Object { "$_" -replace "`0", '' } | Where-Object { $_.Trim() -ne '' }
    Remove-Item $o, $e -ErrorAction SilentlyContinue
}

function Invoke-WslRoot([string]$Distro, [string]$Script) {
    # As base64: no quoting of the script survives the way through wsl.exe and a shell.
    $b = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Script.Replace("`r`n", "`n")))
    Invoke-Wsl @('-d', $Distro, '-u', 'root', '-e', 'sh', '-c', "echo $b | base64 -d | sh")
}

function Get-WslDistros {
    # Without WSL, or with no distro, wsl.exe prints localised help text and fails: no distros then.
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { return @() }
    $env:WSL_UTF8 = '1'
    $out = & wsl.exe -l -q 2>$null
    if ($LASTEXITCODE -ne 0) { return @() }
    $out | ForEach-Object { ("$_" -replace "`0", '').Trim() } | Where-Object { $_ -match '^[\w.\-]+$' }
}

function Test-WslReady {
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { return $false }
    & wsl.exe --version *> $null
    if ($LASTEXITCODE -ne 0) { return $false }
    # winget installs the WSL app, but WSL 2 also needs the Virtual Machine Platform feature (and a restart).
    $vmp = Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -ErrorAction SilentlyContinue
    -not $vmp -or $vmp.State -eq 'Enabled'
}

# Environment variables as the registry holds them: %JAVA_HOME%\bin stays %JAVA_HOME%\bin. The .NET calls expand them
# on read and write REG_SZ, so a PATH with %SystemRoot% in it would stop working.
function Open-EnvKey([string]$Scope, [bool]$Write = $false) {
    if ($Scope -eq 'User') { return [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $Write) }
    [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment', $Write)
}

function Get-RawEnv([string]$Scope) {
    $k = Open-EnvKey $Scope
    $h = [ordered]@{}
    foreach ($n in $k.GetValueNames()) { if ($n) { $h[$n] = "$($k.GetValue($n, $null, 'DoNotExpandEnvironmentNames'))" } }
    $k.Close()
    [pscustomobject]$h
}

function Get-RawEnvValue([string]$Scope, [string]$Name) {
    $k = Open-EnvKey $Scope
    $v = $k.GetValue($Name, $null, 'DoNotExpandEnvironmentNames')
    $k.Close()
    $v
}

function Set-RawEnvValue([string]$Scope, [string]$Name, [string]$Value) {
    $k = Open-EnvKey $Scope $true
    $kind = if ($Value -match '%[^%]+%') { 'ExpandString' } else { 'String' }
    $k.SetValue($Name, $Value, $kind)
    $k.Close()
}

# Windows hears about the new values through this call (it broadcasts WM_SETTINGCHANGE).
function Send-EnvChanged { [Environment]::SetEnvironmentVariable('HOPOVER_ENV_TOUCH', $null, 'User') }

function ConvertTo-WslPath([string]$Path) {
    '/mnt/' + $Path.Substring(0, 1).ToLower() + '/' + $Path.Substring(3).Replace('\', '/')
}

function Test-UnderAny([string]$Path, [string[]]$Roots) {
    foreach ($r in $Roots) {
        if (-not $r) { continue }
        $r = $r.TrimEnd('\')
        if ($Path -ieq $r -or $Path.StartsWith("$r\", [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    $false
}
