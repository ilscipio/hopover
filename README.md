# Hopover

Moves a Windows PC to a new one over the LAN. Both PCs stay on.

It moves apps (winget, 1Password/KeePass/OneDrive/SeaDrive first), env vars and
PATH, the whole user profile (`.ssh`, every dot-folder, `.claude.json`), selected
AppData, own folders on `C:\` (`C:\java`), other fixed drives, KeePass databases
and key files, Wi-Fi, mapped drives, network printers, registry settings, personal
certificates, WSL distros, Docker named volumes, scheduled tasks (switched off)
and the `C:\Seafile` junctions. OneDrive and SeaDrive folders sync after sign-in.
`CHECKLIST.md` lists what no tool can move: sign-ins, saved Windows logins, licences.

## Old PC

1. Double-click `Hopover.cmd`, allow admin rights.
2. Click **This is the OLD PC**. Wait for the scan.
3. Untick what must not move. Click **Prepare the move**.
4. Note the PC name, IP and user in the green box.

## New PC

1. Sign in to OneDrive. Double-click `OneDrive\Hopover\Hopover.cmd`
   (or `\\OLDPC\hopover-store\tool\Hopover.cmd`).
2. Click **This is the NEW PC**. Type the PC name, user and password. Click **Connect**.
3. Click **Start the move**. Sign in to 1Password and OneDrive while the apps install.
4. Click **Open the checklist** and work through it.
5. Click **Turn on scheduled tasks** when the repos and venvs work.

Closing Hopover is safe. Start it again: the done steps are skipped, the copy goes on.

## Finish

1. Old PC: click **Stop sharing**.
2. Delete `C:\hopover-store` on both PCs. It holds Wi-Fi passwords and env values.

## Without the GUI (administrator PowerShell)

- Old PC: `.\hopover.ps1 inventory`, then `.\hopover.ps1 export`.
- New PC: `net use \\OLDPC\IPC$ /user:OLDPC\pp`, then `.\hopover.ps1 import -From OLDPC`.
- `enable-tasks`, `verify`, `cleanup` as above.
- `-WhatIf` changes nothing. `-Only files,tasks` or `-Skip wsl` runs a part.
  Steps: `apps env files settings certs wsl docker tasks`.
- What moves: `config\include.json`.
