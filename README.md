# Hopover

Move your Windows PC to a new one over the network. Both PCs stay on, and you can keep working.

**[Download Hopover.exe](https://github.com/madppiper/hopover/releases/latest/download/Hopover.exe)** · Windows 10 and 11 · one file, no install

> Early version. The exe is not signed: at the SmartScreen warning, click **More info**, then **Run anyway**.

## How it works

| | Old PC | New PC |
|---|---|---|
| 1 | Start Hopover. Click **This is the old PC**. | |
| 2 | Untick what stays behind. Click **Prepare the move**. | |
| 3 | Read the pairing code. Leave the PC on. | Start Hopover. Click **This is the new PC**. Type the code. |
| 4 | | Optional: switch on **Preview first** to get a text file of what would happen. |
| 5 | | Click **Start the move**. |
| 6 | Keep working. | Click **Catch up** to fetch your changes, as often as you like. |

Closing Hopover is safe. Start it again and it goes on where it stopped.

## What moves

| | |
|---|---|
| **Apps** | winget, Chocolatey, Store apps, Visual Studio workloads, programs without an installer |
| **Dev setup** | WSL, Docker volumes, PostgreSQL, env vars and PATH, SSH keys, saved git logins, npm and pip tools |
| **Files** | Your profile, AppData, folders on `C:\`, other drives, junctions and links |
| **Windows** | Taskbar, Explorer, theme, wallpaper, mouse, keyboard, languages, power plan, Wi-Fi, hosts, features |
| **Registry** | App settings, file types, Office, Visual Studio |
| **Caches** | Gradle, npm, uv, Playwright, IDE indexes: untick what you do not want |

You sign in again to browsers, OneDrive, 1Password and Microsoft 365. Docker images come back with a pull.

## When both PCs have changes

Nothing gets deleted. What moves aside goes to `C:\hopover-held`.

| Situation | Result |
|---|---|
| First move, file differs | The old PC wins. The new PC's file is kept aside. |
| Changed on one PC | That version wins. |
| Changed on both PCs | The new PC keeps its version. The old version is kept aside. |
| Deleted on the old PC | Moved aside on the new PC. |
| Only on the new PC | Stays. |

## Finish

1. Do a last **Catch up**, then work only on the new PC.
2. Turn off the scheduled tasks on the old PC. Click **Turn on scheduled tasks** on the new PC.
3. Click **Stop sharing** on the old PC.
4. Delete `C:\hopover-store` on both PCs. It holds Wi-Fi passwords and tokens.

## Security

The pairing code sets up an encrypted, direct link between the two PCs. No server carries your files.

## Build

```powershell
.\build.ps1          # needs the Visual Studio Build Tools
.\build.ps1 -Shots   # also renders each screen to dist\shots
```

Source available under the [PolyForm Noncommercial License](LICENSE.md).
