@echo off
rem Hopover launcher. Copies the tool to this PC first: an elevated window cannot
rem read a network share or OneDrive placeholder that the normal window opened.
setlocal
set "SRC=%~dp0"
set "DST=%LOCALAPPDATA%\Hopover\app"
robocopy "%SRC%." "%DST%" /MIR /XD .git /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
  echo Could not copy Hopover to %DST%.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%DST%\gui.ps1\" -SessionUser \"%USERNAME%\"'"
