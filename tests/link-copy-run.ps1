# Test helper: copies a source tree through the real engine path (Copy-Plan + RoboPool + Hopover.exe --link copy).
# Needs HOPOVER_LINK and HOPOVER_EXE in the environment (tests\link-test.ps1 sets them). ASCII only.
param([Parameter(Mandatory)][string]$Src, [Parameter(Mandatory)][string]$Dst, [Parameter(Mandatory)][string]$Store,
      [string]$BigFile, [switch]$Recopy, [string]$Speed = 'full')
$ErrorActionPreference = 'Continue'
$Root = Split-Path $PSScriptRoot -Parent
. (Join-Path $Root 'lib\Common.ps1')
. (Join-Path $Root 'lib\Files.ps1')
if (-not ('Hopover.RoboPool' -as [type])) { Add-Type -Path (Join-Path $Root 'lib\Native.cs') }
New-Item -ItemType Directory -Path "$Store\logs" -Force | Out-Null
$script:Log = Join-Path $Store 'logs\test.log'

function Get-Bytes([string]$Path) {
    $s = Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum
    [int64]$s.Sum
}

$plan = New-Object System.Collections.Generic.List[object]
$subs = @(Get-ChildItem -LiteralPath $Src -Directory -Force | Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) })
foreach ($d in $subs) { $plan.Add([pscustomobject]@{ Path = $d.FullName; Kind = 'dir'; Why = 'profile'; Group = $d.FullName; Bytes = (Get-Bytes $d.FullName) }) }
# The folder itself: its own files (the big one is its own chunk, kind file), subfolders are other chunks.
$rootFiles = @(Get-ChildItem -LiteralPath $Src -File -Force)
$plan.Add([pscustomobject]@{ Path = $Src; Kind = 'files'; Why = 'profile'; Group = $Src; Skip = @($subs.FullName); Bytes = [int64](($rootFiles | Where-Object { $_.Name -ne 'big.bin' } | Measure-Object Length -Sum).Sum) })
if ($BigFile) {
    $f = Get-Item -LiteralPath $BigFile
    $plan.Add([pscustomobject]@{ Path = $f.FullName; Kind = 'file'; Why = 'profile'; Group = $f.FullName; Bytes = $f.Length })
}
# The "files" chunk would also copy big.bin through /E: exclude it by name there.
$ex = @('/XD', '$RECYCLE.BIN', '/XF', '~$*', 'big.bin')
$env:USERPROFILE = $Dst          # ConvertTo-TargetPath: old profile = $Src, new profile = $Dst
New-Item -ItemType Directory -Path $Dst -Force | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
$failed = @(Copy-Plan $plan.ToArray() 'link' $Src $ex $Store $Speed {} -Recopy:$Recopy)
Write-Host ("##RESULT failed={0} seconds={1:N1}" -f $failed.Count, $sw.Elapsed.TotalSeconds)
if ($failed.Count) { exit 2 }
