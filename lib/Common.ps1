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
    $text | ConvertFrom-Json
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
    $LASTEXITCODE -eq 0
}

function Test-UnderAny([string]$Path, [string[]]$Roots) {
    foreach ($r in $Roots) {
        if (-not $r) { continue }
        $r = $r.TrimEnd('\')
        if ($Path -ieq $r -or $Path.StartsWith("$r\", [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    $false
}
