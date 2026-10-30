# Hopover

Moves a Windows PC to a new one over the LAN. Both PCs stay on.

One file: `dist\Hopover.exe` (.NET Framework 4.8, runs on every Windows 10 and 11).
It moves apps (winget, 1Password/KeePass/OneDrive/SeaDrive first), env vars and PATH,
the whole user profile (`.ssh`, every dot-folder, `.claude.json`), selected AppData,
own folders on `C:\` (`C:\java`), other fixed drives, KeePass databases and key files,
Windows settings (taskbar, Explorer, theme, accent, wallpaper, mouse, keyboard, input,
notifications, languages, time zone, power plan, Quick Access), app settings in the
registry (every vendor key under `HKCU\Software`), Wi-Fi, mapped drives, network
printers, personal certificates, WSL distros, Docker named volumes, scheduled tasks
(switched off) and the `C:\Seafile` junctions. OneDrive and SeaDrive folders sync after
sign-in. The checklist lists what no tool can move: sign-ins, saved Windows logins,
licences, default apps, display scale.

## Old PC

1. Double-click `Hopover.exe`. Allow admin rights.
2. Click **This is the old PC**. Wait for the scan.
3. Untick what must not move. Click **Prepare the move**.
4. Note the PC name, IP address and user in the green card.

## New PC

1. Sign in to OneDrive. Double-click `OneDrive\Hopover\Hopover.exe`
   (or `\\OLDPC\hopover-store\tool\Hopover.exe`).
2. Click **This is the new PC**. Type the PC name, user and password. Click **Connect**.
3. Choose the speed. Click **Start the move**. Sign in to 1Password and OneDrive meanwhile.
4. Click **Open the checklist** and work through it.
5. Click **Turn on scheduled tasks** when the repos and venvs work.

Closing Hopover is safe. Start it again: done steps and copied chunks are skipped.

## Speed

- **Gentle**: 2 copies at once, idle CPU, low disk priority.
- **Balanced** (default): 4 copies at once, low CPU and disk priority.
- **Full speed**: 8 copies at once, normal priority.

Big folders are split into chunks (`chunkDepth` in `config\include.json`). App installs
run in the background while the files copy.

## Finish

1. Old PC: click **Stop sharing**.
2. Delete `C:\hopover-store` on both PCs. It holds Wi-Fi passwords and env values.

## Build

1. Install the Visual Studio Build Tools (C# compiler).
2. `.\build.ps1` builds `dist\Hopover.exe`. `.\build.ps1 -Shots` also renders every screen to `dist\shots\`.

## Without the GUI (administrator PowerShell)

- Old PC: `.\hopover.ps1 inventory`, then `.\hopover.ps1 export`.
- New PC: `net use \\OLDPC\IPC$ /user:OLDPC\pp`, then `.\hopover.ps1 import -From OLDPC -Speed balanced`.
- `enable-tasks`, `verify`, `cleanup` as above. `-WhatIf` changes nothing.
  `-Only files,tasks` or `-Skip wsl` runs a part. `-Recopy` copies chunks marked done again.
- An `include.json` next to `Hopover.exe` overrides `config\include.json`.
