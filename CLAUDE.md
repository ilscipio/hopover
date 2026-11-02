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
- 2026-11-02 pre-move review (commit d6e112c): keep-awake (both PCs), link-loss text + "Connect again",
  old PC "Open the link again" without a new scan, orphan `--link serve` kill, store-copy STEP events,
  apps: `appMap` (Chrome, NVM, ... that winget export misses), Python user scope, winget check;
  WSL after apps + VirtualMachinePlatform check + import/export verified; Docker fails loudly when the engine
  is down; owner of copied trees set to the user (git "dubious ownership"); nvm node link; LinkCopy: source
  gone = skip, re-stat big files, time errors ignored, Mux race. Open: MuxProxy writes block its one loop;
  server queue is FIFO (list waits behind reads).
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
  opens a firewall rule (TCP 47800, UDP 47801, program Hopover.exe; group "Hopover") and the
  GUI starts `Hopover.exe --link serve`, which shows the pairing code. No SMB shares.
- `import -From OLDPC -Speed gentle|balanced|full`: env, then winget in a background
  runspace (`Start-BackgroundApps`), files (parallel chunks, largest first), certs, WSL,
  wait for apps, then settings (after installs, or installers overwrite them), Docker, tasks.
- Copy: `lib\Native.cs` `RoboPool` = N copy processes (`RoboJob.Exe`: robocopy for the scan, `Hopover.exe --link copy` for the move) on worker threads, CPU class
  Idle/BelowNormal and low I/O priority (NtSetInformationProcess), output parsed line by
  line (no memory growth). `files-done.json` lists finished chunks; a restart skips them
  (`-Recopy` ignores it). `/E /XO /XJ`, never `/MIR`.
- Events (stdout lines starting with `##`): `STEP <phase> start|done|failed|skipped`,
  `SCAN done total path`, `ITEM idx count planned path`, `ITEMDONE idx planned exit`,
  `PROGRESS done total runBytes`, `SHARE pc|user|ips`, `END ...`.
- Exit codes: 0 ok, 2 some phases failed, 1 fatal. `state-import.json` lists done phases.

## Link (pairing code, replaces SMB; added 2026-10-30, Paul's approved design)

- Code `<digit>-w1-w2-w3` (mnemonicode list from croc, MIT, `app\Link\Words.cs`). SPAKE2 P-256 per RFC 9382
  (`Spake2.cs`, `P256.cs` on BigInteger, variable time), AES-GCM via bcrypt.dll (`AesGcm.cs`), frames
  `u32 len | GCM(type+payload) | tag`, per-direction keys (`Channel.cs`). A wrong confirmation burns the code
  (`LinkServer` makes a new one). The joining PC (role A) sends `Use` on the connection it keeps.
- Paths: (1) LAN: UDP broadcast 47801 with the room hash, TCP 47800. (2) Internet: HTTP POST
  `http://hop.ilscipio.com/v1/meet` (long poll; `config\include.json` `link.rendezvous`; server in `rendezvous\`),
  then TCP hole punching from the same local port. (3) Nothing else. THERE IS NO DATA RELAY.
- New PC: the GUI holds the Channel and a loopback `MuxProxy` (random port + token, env `HOPOVER_LINK=port:token`).
  `RoboPool` starts `Hopover.exe --link copy SRC DST ...` per chunk. SRC is `\\link\hopover-store\...` or
  `\\link\hopover-d\...` (virtual roots `store`, `drive-d`; the server rejects `..`, rooted names, reparse points).
  `LinkCopy.cs` prints robocopy's tab lines and exit codes; /XO rule, `name.hopover-part` + `.meta` sidecar every 32 MiB,
  always `\\?\` paths, times and R/H/S/A set, folder times last, nothing deleted.
- Headless verbs (run before WPF in `Program.Main`): `--link selftest|serve|proxy|copy|info|stub`.
  `stub` is a local stand-in for the meeting point (tests only).
- Tests: `Hopover.exe --link selftest`; `tests\link-test.ps1` (a few GB through the real engine path, loopback and
  LAN IP, kill and resume); `tests\punch-test.ps1` (hole punch code path on loopback only: real NAT traversal untested).
- Events: `##LINK code|path|peer|state`.

## Files

- `app\Link\` - the link (see above). `tests\` - link tests.
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
