# Hopover

Moves a Windows PC to a new one over the LAN. Both PCs stay on.

One file: `dist\Hopover.exe` (.NET Framework 4.8, runs on every Windows 10 and 11).
It moves apps (winget, Chocolatey, Store apps by name, Visual Studio workloads;
1Password/KeePass/OneDrive/SeaDrive first), programs that no package source has (as a
folder, with their uninstall entry, registry key and data), env vars and PATH (`%VAR%`
stays `%VAR%`), the whole user profile (`.ssh`, every dot-folder, `.claude.json`), all of
AppData except browser profiles and temp data, own folders on `C:\` (`C:\java`), other
fixed drives, junctions and symbolic links as links, KeePass databases and key files,
hosts file, Windows features, long paths and Developer Mode, saved logins (git, gh,
docker tokens), WireGuard tunnels, Windows settings (taskbar, Explorer, theme, accent,
wallpaper, mouse, keyboard, input, notifications, languages, time zone, power plan,
Quick Access), app settings in the registry (every vendor key under `HKCU\Software`,
`HKCU\Software\Classes`, Office and Visual Studio, vendor keys under `HKLM\SOFTWARE`),
Wi-Fi, mapped drives, network printers, personal certificates, WSL distros, Docker
named volumes, PostgreSQL, scheduled tasks (switched off) and the `C:\Seafile` junctions.
Caches (Gradle, npm, uv, pip, Playwright, JetBrains indexes, Steam games) are lines in
the list: untick what you do not want. OneDrive and SeaDrive folders sync after sign-in.
The checklist lists what no tool can move: sign-ins, licences, default apps, display
scale. Docker images come back with a pull; the running Docker disk does not move.

## Old PC

1. Double-click `Hopover.exe`. Allow admin rights.
2. Click **This is the old PC**. Wait for the scan.
3. Untick what must not move. Click **Prepare the move**.
4. Read the pairing code in the card (like `3-alpha-bravo-cafe`). Leave this PC on.

## New PC

1. Sign in to OneDrive. Double-click `OneDrive\Hopover\Hopover.exe`.
2. Click **This is the new PC**. Choose **Same network** or **Over the internet**.
   Type the pairing code. Click **Connect**. No Windows password is needed.
   The code works for one try: a wrong guess makes a new code on the old PC.
   If there is no direct route, put both PCs on one network or move the files with a USB disk.
   No server ever carries your files.
3. Choose the speed. Click **Start the move**. Sign in to 1Password and OneDrive meanwhile.
4. Click **Open the checklist** and work through it.
5. Click **Turn on scheduled tasks** when the repos and venvs work.

Closing Hopover is safe. Start it again: done steps and copied chunks are skipped.

## Keep working on the old PC

The old PC can stay in use. After the first move, click **Catch up with the old PC** on
the new PC, as often as you like. Leave Hopover open on the old PC.

1. The old PC updates its list of folders, apps, env vars, settings, tasks, WSL, Docker
   volumes and PostgreSQL. The PostgreSQL server stops there for a moment.
2. The new PC fetches only what changed.

Rules (the new PC never loses work):

- Changed only on the old PC: it updates here.
- Changed only on the new PC: it stays.
- Changed on both: the new PC's version stays. The old PC's version goes to
  `C:\hopover-held\<time>\old-pc-version`.
- Gone on the old PC: it moves to `C:\hopover-held\<time>\removed-on-old-pc`. Nothing is
  deleted. A file made on the new PC is never touched.
- Env var: updates when only the old PC changed it. Otherwise the checklist names it.
- WSL: changed files come over; a newer file in the new distro stays. Deleted files stay.
- Docker volume: updates when nobody used it here. PostgreSQL: the data of the new PC
  stays after the first restore; the archive of the old PC is in the store.

Last step: stop working on the old PC, click **Catch up**, then work only on the new PC.
Turn off the scheduled tasks on the old PC before you turn them on here.

## Speed

- **Gentle**: 2 copies at once, idle CPU, low disk priority.
- **Balanced** (default): 4 copies at once, low CPU and disk priority.
- **Full speed**: 8 copies at once, normal priority.

Big folders are split into chunks (`chunkDepth` in `config\include.json`). App installs
run in the background while the files copy.

## Finish

1. Old PC: click **Stop sharing**.
2. Delete `C:\hopover-store` on both PCs. It holds Wi-Fi passwords, env values and saved logins.
3. Look through `C:\hopover-held`, then delete it.

## Build

1. Install the Visual Studio Build Tools (C# compiler).
2. `.\build.ps1` builds `dist\Hopover.exe`. `.\build.ps1 -Shots` also renders every screen to `dist\shots\`.

## Without the GUI (administrator PowerShell)

- Old PC: `.\hopover.ps1 inventory`, then `.\hopover.ps1 export`.
- Old PC: after `export`, run `Hopover.exe --link serve` (it prints the code).
- New PC: `Hopover.exe --link proxy --mode lan --code <code>` prints `##LINK proxy <port> <token>`.
  Set `HOPOVER_EXE` to the exe and `HOPOVER_LINK=<port>:<token>`, then `.\hopover.ps1 import`.
- Tests: `Hopover.exe --link selftest`, `tests\link-test.ps1`, `tests\punch-test.ps1`.
- `enable-tasks`, `verify`, `cleanup` as above. `-WhatIf` changes nothing.
  `-Only files,tasks` or `-Skip wsl` runs a part. `-Recopy` copies chunks marked done again.
- An `include.json` next to `Hopover.exe` overrides `config\include.json`.
