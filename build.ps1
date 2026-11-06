<#
.SYNOPSIS
  Builds dist\Hopover.exe: a .NET Framework 4.8 WPF app with the PowerShell engine inside.
  Needs the Roslyn compiler (Visual Studio or Build Tools). The result runs on every Windows 10/11.
.EXAMPLE
  .\build.ps1            # build
  .\build.ps1 -Shots     # build, then render every screen to dist\shots\*.png
#>
param([switch]$Shots)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$App = Join-Path $Root 'app'
$Dist = Join-Path $Root 'dist'
$Obj = Join-Path $Root 'obj'
New-Item -ItemType Directory -Path $Dist, $Obj -Force | Out-Null

# ---------- compiler and reference assemblies ----------
$csc = @(
    Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe' -ErrorAction SilentlyContinue
) | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $csc) { throw 'Roslyn csc.exe not found. Install the Visual Studio Build Tools (C# compiler).' }
$ref = @('v4.8.1', 'v4.8', 'v4.7.2') | ForEach-Object { "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\$_" } |
    Where-Object { Test-Path "$_\mscorlib.dll" } | Select-Object -First 1
if (-not $ref) {
    # Without the targeting pack the runtime assemblies work as references too.
    $ref = "$env:windir\Microsoft.NET\Framework64\v4.0.30319"
    $wpf = "$ref\WPF"
} else { $wpf = $ref }
Write-Host "csc: $($csc.FullName)"
Write-Host "references: $ref"

# ---------- icon, rendered from the logo in Theme.xaml ----------
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
# Theme.xaml names its fonts by folder (@FONTDIR@); the icon needs no text, so any folder will do.
$themeXaml = (Get-Content (Join-Path $App 'Theme.xaml') -Raw -Encoding UTF8).Replace('@FONTDIR@', ([Uri](Join-Path $App 'fonts\')).AbsoluteUri)
$theme = [Windows.Markup.XamlReader]::Parse($themeXaml)
$pngs = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 128, 256) {
    # Heavier cuts of the mark at the small sizes (16 px heaviest with a closed gap, 20 to 32 px heavy).
    $logo = if ($size -le 16) { $theme['LogoIcon16'] } elseif ($size -le 32) { $theme['LogoIcon24'] } else { $theme['LogoIcon'] }
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.DrawImage($logo, (New-Object Windows.Rect 0, 0, $size, $size))
    $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}
# ICO: header, one directory entry per size, then the PNG images.
$ico = Join-Path $Obj 'hopover.ico'
$fs = [IO.File]::Create($ico)
$bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$p.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }
$bw.Close()
Copy-Item (Join-Path $Obj 'hopover.ico') (Join-Path $Dist 'hopover.ico') -Force

# ---------- compile ----------
$resources = @(
    "$App\Theme.xaml,ui/Theme.xaml", "$App\Main.xaml,ui/Main.xaml", "$App\Splash.xaml,ui/Splash.xaml",
    "$Root\hopover.ps1,engine/hopover.ps1", "$Root\config\include.json,engine/config/include.json",
    "$Root\lib\Native.cs,engine/lib/Native.cs"
) + @(Get-ChildItem "$Root\lib\*.ps1" | ForEach-Object { "$($_.FullName),engine/lib/$($_.Name)" }) +
    @(Get-ChildItem "$App\fonts\*.ttf" | ForEach-Object { "$($_.FullName),fonts/$($_.Name)" })
$refs = @("$ref\mscorlib.dll", "$ref\System.dll", "$ref\System.Core.dll", "$ref\System.Xml.dll", "$ref\System.Xaml.dll",
          "$ref\System.Web.Extensions.dll", "$ref\System.Numerics.dll", "$wpf\WindowsBase.dll", "$wpf\PresentationCore.dll", "$wpf\PresentationFramework.dll")
$out = Join-Path $Dist 'Hopover.exe'
$rsp = Join-Path $Obj 'csc.rsp'
# Paths over 260 characters work in .NET Framework only when the assembly says it targets 4.6.2 or later.
$tfm = Join-Path $Obj 'TargetFramework.cs'
'[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]' | Set-Content $tfm -Encoding ASCII
# A release gets the number of its tag (v1.0.<run>, see release.yml). A local build stays 1.0.0.
$ver = if ($env:GITHUB_RUN_NUMBER) { "1.0.$env:GITHUB_RUN_NUMBER" } else { '1.0.0' }
$vcs = Join-Path $Obj 'Version.cs'
@(
    "[assembly: System.Reflection.AssemblyVersion(`"$ver.0`")]",
    "[assembly: System.Reflection.AssemblyFileVersion(`"$ver.0`")]",
    "[assembly: System.Reflection.AssemblyInformationalVersion(`"$ver`")]",
    "namespace Hopover.App { static class BuildInfo { public const string Version = `"$ver`"; } }"
) | Set-Content $vcs -Encoding ASCII
@(
    '/nologo', '/nostdlib+', '/target:winexe', '/platform:anycpu', '/optimize+', '/langversion:7.3',
    '/utf8output', '/warn:4', "/out:`"$out`"", "/win32icon:`"$ico`"", "/win32manifest:`"$App\app.manifest`""
) + ($refs | ForEach-Object { "/reference:`"$_`"" }) + ($resources | ForEach-Object { "/resource:`"$($_.Split(',')[0])`",$($_.Split(',')[1])" }) +
  (Get-ChildItem "$App\*.cs", "$App\Link\*.cs" | ForEach-Object { "`"$($_.FullName)`"" }) + "`"$tfm`"" + "`"$vcs`"" | Set-Content $rsp -Encoding UTF8
& $csc.FullName /noconfig "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }
Write-Host ("built {0} ({1:N0} KB)" -f $out, ((Get-Item $out).Length / 1KB)) -ForegroundColor Green

if ($Shots) {
    $dir = Join-Path $Dist 'shots'
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    $p = Start-Process $out -ArgumentList '--shot', "`"$dir`"" -PassThru -Wait
    if (Test-Path "$dir\error.txt") { Get-Content "$dir\error.txt"; throw 'screenshot run failed' }
    Get-ChildItem $dir -Filter *.png | ForEach-Object { Write-Host "  $($_.Name)" }
}
