# Tests the hole-punch code path: two local processes (--link serve and --link proxy) meet through a local stand-in
# of the meeting point (--link stub, plain HTTP like the real one) and connect to each other from their punch ports.
# Everything runs on loopback. REAL NAT TRAVERSAL IS NOT TESTED HERE: no router is involved.
# ASCII only. Test data lives in %TEMP%\hopover-punchtest and is deleted at the end.
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$Exe = Join-Path $Root 'dist\Hopover.exe'
$T = Join-Path $env:TEMP 'hopover-punchtest'
$Fails = 0
function Ok([string]$Name, [bool]$Cond, [string]$Info = '') {
    if ($Cond) { Write-Host "PASS $Name $Info" -ForegroundColor Green } else { Write-Host "FAIL $Name $Info" -ForegroundColor Red; $script:Fails++ }
}
function Start-Quiet([string]$A, [string]$Out) {
    $p = Start-Process -FilePath $Exe -ArgumentList $A -RedirectStandardOutput $Out -RedirectStandardError "$Out.err" -WindowStyle Hidden -PassThru
    $null = $p.Handle
    $p
}
function Wait-Line([string]$File, [string]$Pattern, [int]$Seconds = 40) {
    $until = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $until) {
        if (Test-Path $File) { $m = Select-String -Path $File -Pattern $Pattern | Select-Object -First 1; if ($m) { return $m.Line } }
        Start-Sleep -Milliseconds 200
    }
    $null
}
function Stop-Tree($p) { if ($p -and -not $p.HasExited) { & taskkill /PID $p.Id /T /F | Out-Null } }

if (Test-Path $T) { Remove-Item $T -Recurse -Force }
New-Item -ItemType Directory -Path "$T\store" | Out-Null
[IO.File]::WriteAllText("$T\store\hello.txt", 'hello through a punched hole')
$stub = $null; $serve = $null; $proxy = $null; $proxy2 = $null
try {
    $stub = Start-Quiet '--link stub --port 47990' "$T\stub.txt"
    Ok 'stub (local meeting point) is up' ([bool](Wait-Line "$T\stub.txt" 'listening')) ''
    $rv = 'http://127.0.0.1:47990'
    $serve = Start-Quiet "--link serve --store `"$T\store`" --code 6-alpha-bravo-cafe --tcp 47960 --udp 47961 --rendezvous $rv --name PUNCHHOST" "$T\serve.txt"
    $null = Wait-Line "$T\serve.txt" 'listening'
    Start-Sleep -Seconds 1   # the host registers at the meeting point
    $proxy = Start-Quiet "--link proxy --mode internet --rendezvous $rv --code 6-alpha-bravo-cafe" "$T\proxy.txt"
    $line = Wait-Line "$T\proxy.txt" '##LINK (proxy|failed)' 60
    Ok 'join: meeting point swapped the addresses and the punch produced an authenticated link' ([bool]($line -and $line -match 'proxy')) ($line)
    if ($line -match 'proxy (\d+) (\w+)') {
        $env:HOPOVER_LINK = "$($Matches[1]):$($Matches[2])"
        $info = & $Exe --link info | Out-String
        Ok 'data path: the old PC answers through the link' ($info -match 'PUNCHHOST') ($info.Trim())
        $dst = Join-Path $T 'dst'
        $env:HOPOVER_EXE = $Exe
        & $Exe --link copy '\\link\hopover-store' $dst /E | Out-Null
        $code = $LASTEXITCODE
        Ok 'data path: a file copies through the punched link' ($code -eq 1 -and [IO.File]::ReadAllText("$dst\hello.txt") -eq 'hello through a punched hole') "exit $code"
    }
    $sl = Get-Content "$T\serve.txt" -Raw
    Ok 'host side saw the path and the peer' ($sl -match '##LINK path (lan|internet)' -and $sl -match '##LINK peer') ''

    # A second PC with a wrong code: the host must burn the code.
    Stop-Tree $proxy
    Start-Sleep -Seconds 5   # the host registers again
    $proxy2 = Start-Quiet "--link proxy --mode internet --rendezvous $rv --code 6-alpha-bravo-cairo" "$T\proxy2.txt"
    $line2 = Wait-Line "$T\proxy2.txt" '##LINK failed' 60
    Ok 'wrong code over the punched path fails' ($line2 -match 'wrong-code') ($line2)
    Start-Sleep -Seconds 1
    Ok 'host burned the code after the wrong guess' ((Get-Content "$T\serve.txt" -Raw) -match '##LINK state burned') ''
}
finally {
    Stop-Tree $proxy; Stop-Tree $proxy2; Stop-Tree $serve; Stop-Tree $stub
    Start-Sleep -Milliseconds 500
    if (Test-Path $T) { Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host ''
if ($Fails -eq 0) { Write-Host 'PUNCH TEST OK (loopback only: real NAT traversal untested)' -ForegroundColor Green; exit 0 } else { Write-Host "PUNCH TEST FAILED ($Fails)" -ForegroundColor Red; exit 1 }
