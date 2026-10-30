# Hopover - notes for Claude

Hopover moves a Windows PC to a new one over the LAN. `dist\Hopover.exe` is a C# WPF app
(.NET Framework 4.8) that embeds the PowerShell engine (`hopover.ps1`, `lib\`, `config\`),
unpacks it to `%LOCALAPPDATA%\Hopover\engine\<build id>` and drives it as a hidden child.
User steps: `README.md`.

## Status (2026-10-30)

- v1 (scripts + `gui.ps1`) built and reviewed (15 bugs fixed). Same day v2: real exe,
  modern UI, parallel chunked copy, low priority, apps install during the copy, Windows
  settings and app registry keys. v2 had a second agent review (see git log).
- Tested: all scripts parse; `build.ps1 -Shots` compiles and renders every screen with
  sample data (`dist\shots\*.png`: look at them after UI changes); the C# RoboPool
  measured and copied a small tree byte for byte; `inventory` ran on Paul's PC
  (MADPPIPER-2022: ~697 GB, `C:\java` = 498 GB / 3.3M files).
- NEVER run: `export`, `import`, `enable-tasks`, `cleanup`, or the exe's real flow.
  They need admin and two PCs. The exe was never clicked through for real.
- Next: Paul's real move in the week of 2026-11-02 (old PC stays on). That run is the
  test. Fix what it finds here, rebuild, commit and push (`dist\Hopover.exe` is in git).
- Selling it: decide AFTER the move (Paul, 2026-10-30). Slaide stays first in the push
  order. Competitors: PCmover ~$30-50, EaseUS PCTrans ~$50, Zinstall ~$130; none of
  them claim WSL, SSH keys, env vars or dev scheduled tasks. A sale version needs a
  code-signing certificate and tests on many PC types.

## How it works

- Old PC: `inventory` builds the plan and measures it in parallel (`robocopy /L` in the
  C# pool) -> `C:\hopover-store\manifest.json`. Big trees are split into chunks
  (`chunkDepth` per kind; a `files` chunk = the files directly in a folder, no `/E`).
  `Group` = the top-level folder; the GUI shows groups, `deselected.json` drops groups.
- `export`: winget, env, Windows settings (`registry`, `registryValues`, languages, time
  zone, power plan), app registry keys (every `HKCU\Software\<vendor>` except
  `registryVendorsExclude`), Wi-Fi, drives, printers, certs, tasks, `wsl --export`,
  Docker named volumes; copies `HOPOVER_EXE` into the store and `OneDrive\Hopover`;
  shares `hopover-store` and every fixed drive as `hopover-<letter>`, read-only.
- `import -From OLDPC -Speed gentle|balanced|full`: env, then winget in a background
  runspace (`Start-BackgroundApps`), files (parallel chunks, largest first), certs, WSL,
  wait for apps, then settings (after installs, or installers overwrite them), Docker, tasks.
- Copy: `lib\Native.cs` `RoboPool` = N robocopy processes on worker threads, CPU class
  Idle/BelowNormal and low I/O priority (NtSetInformationProcess), output parsed line by
  line (no memory growth). `files-done.json` lists finished chunks; a restart skips them
  (`-Recopy` ignores it). `/E /XO /XJ`, never `/MIR`.
- Events (stdout lines starting with `##`): `STEP <phase> start|done|failed|skipped`,
  `SCAN done total path`, `ITEM idx count planned path`, `ITEMDONE idx planned exit`,
  `PROGRESS done total runBytes`, `SHARE pc|user|ips`, `END ...`.
- Exit codes: 0 ok, 2 some phases failed, 1 fatal. `state-import.json` lists done phases.

## Files

- `app\` - the exe: `Program.cs` (self-copy + UAC elevation, splash, `--shot DIR`),
  `MainController.cs` (all screens), `Engine.cs` (child process + engine unpack),
  `Models.cs`, `Win32.cs` (DWM dark/rounded frame), `Screenshots.cs`, `Theme.xaml`
  (tokens, styles, logo), `Main.xaml`, `Splash.xaml`, `app.manifest` (asInvoker, PMv2 DPI).
- `build.ps1` - Roslyn csc + .NET 4.8 reference assemblies; renders the .ico from the
  `Logo` DrawingImage in `Theme.xaml`; embeds XAML (`ui/...`) and engine (`engine/...`).
- `hopover.ps1` - commands, phases, shares, firewall, background apps, state.
- `lib\Common.ps1` - log, events, JSON, `Protect-Folder`, WSL helpers.
- `lib\Files.ps1` - plan, chunks, measure, parallel copy, KeePass, junctions, `.ssh` ACL.
- `lib\System.ps1` - apps, env, Windows/app settings, certs, WSL, Docker, tasks.
- `lib\Report.ps1` - `CHECKLIST.md` and `verify`. `lib\Native.cs` - the robocopy pool.
- `config\include.json` - what moves, chunk depth, registry lists.

## Rules (keep them when you change code)

- Engine: Windows PowerShell 5.1, German or English Windows. Use SIDs, not group names;
  match robocopy output by position or tabs, not words. `lib\*.ps1` stay ASCII (no BOM:
  PS 5.1 reads them as ANSI). `lib\Native.cs` stays C# 5 (Add-Type compiler).
- App: C# 7.3, .NET Framework 4.8 only (no NuGet). XAML: a Setter or Storyboard
  `TargetName` must name an element, never a transform; put animation triggers on the
  animated element, not in `ControlTemplate.Triggers`.
- After any UI change: `.\build.ps1 -Shots` and look at `dist\shots\*.png`.
- The engine never prompts when `HOPOVER_GUI` is set: it throws instead.
- Nothing may delete data on either PC. Imported tasks arrive switched off.
- `C:\hopover-store` holds Wi-Fi keys, env tokens and .pfx files: ACL-locked, never in
  OneDrive or git.
- OneDrive and SeaDrive folders are never copied; they sync after sign-in.
- No DPI, monitor or hardware registry keys (`Control Panel\Desktop` only by value).

## Paul's machine facts

- Only drive C:. OneDrive = `C:\Users\pp\OneDrive - ilscipio.com`. SeaDrive junctions in
  `C:\Seafile\*`. WSL: Ubuntu-22.04 (+ docker-desktop, skipped). 15 "AIVory *" scheduled
  tasks in `aivory-sales-manager` move; vendor tasks are filtered (`taskExclude`).
- One live KeePass database outside OneDrive (found through `KeePass.config.xml`).
- Build tools: VS 2022 Build Tools Roslyn + .NET 4.8.1 targeting pack are installed.
- After the move: `docker-compose up -d` in `aivory-sales-manager`, then check
  `http://localhost:7777/api/sales`.
