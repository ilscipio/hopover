# Hopover - notes for Claude

Hopover moves a Windows PC to a new one over the LAN: a WPF wizard (`Hopover.cmd` ->
`gui.ps1`) that drives the engine `hopover.ps1`. User steps: `README.md`.

## Status (2026-10-30)

- Built and pushed in one session. An agent review found 15 bugs; all are fixed.
- Tested: all scripts parse; the GUI window loads (`HOPOVER_GUI_TEST=1`, not clicked
  through); `inventory` ran on Paul's PC (MADPPIPER-2022: 697 GB, 245 items, `C:\java`
  = 498 GB / 3.3M files); unit checks of ACL, task UserId rewrite, drive mapping, WSL list.
- NEVER run: `export`, `import`, `enable-tasks`, `cleanup`. They need admin and two PCs.
- Next: Paul's real move in the week of 2026-11-02 (old PC stays on). That run is the
  test. Fix what it finds here, then commit and push.
- Selling it: decide AFTER the move (Paul, 2026-10-30). Slaide stays first in the push
  order. Competitors: PCmover ~$30-50, EaseUS PCTrans ~$50, Zinstall ~$130; none of
  them claim WSL, SSH keys, env vars or dev scheduled tasks. A sale version needs a
  code-signing certificate and tests on many PC types.

## How it works

- Old PC: `inventory` (robocopy `/L` sizes -> `C:\hopover-store\manifest.json`), then
  `export`: winget, env, registry/Wi-Fi/drives/printers, certs, tasks, `wsl --export`,
  Docker named volumes; copies the tool into the store and `OneDrive\Hopover`; shares
  `hopover-store` and every fixed drive as `hopover-<letter>`, read-only, this user only.
- New PC: `import -From OLDPC`: copies the store locally, then the phases
  `apps env files settings certs wsl docker tasks`, then `CHECKLIST.md` and `verify`.
- Files are PULLED by robocopy (`/E /XO /XJ /MT:16`, never `/MIR`) from the shares.
- The GUI starts the engine as a hidden child and tails its stdout file. Lines that
  start with `##` are events: `STEP <phase> start|done|failed|skipped`,
  `SCAN i n path`, `ITEM i n bytes path`, `BYTES n`, `SHARE pc|user|ips`, `END ...`.
- Exit codes: 0 ok, 2 some phases failed, 1 fatal. `state-import.json` lists done
  phases; a restart skips them (`files` always runs again, robocopy skips what is there).

## Files

- `hopover.ps1` - commands, phases, shares, firewall, state.
- `lib/Common.ps1` - log, events, JSON, `Protect-Folder`, WSL helpers.
- `lib/Files.ps1` - copy plan, sizes, KeePass discovery, junctions, robocopy pull, `.ssh` ACL.
- `lib/System.ps1` - apps, env, settings, certs, WSL, Docker, scheduled tasks.
- `lib/Report.ps1` - `CHECKLIST.md` and `verify`.
- `config/include.json` - what moves and what is excluded.

## Rules (keep them when you change code)

- Windows PowerShell 5.1, German or English Windows. Use SIDs, not group names
  (`*S-1-5-32-544`, `*S-1-5-18`); match robocopy output by position or tabs, not words.
- Files with non-ASCII characters (`gui.ps1`) need a UTF-8 BOM, or PS 5.1 garbles them.
- The engine must never prompt when `HOPOVER_GUI` is set: throw instead.
- `gui.ps1` keeps `$ErrorActionPreference = 'Continue'`: with `Stop`, any stderr line
  from net.exe or robocopy throws.
- Nothing may delete data on either PC. Imported tasks arrive switched off.
- `C:\hopover-store` holds Wi-Fi keys, env tokens and .pfx files: it stays ACL-locked,
  never goes to OneDrive or git (`.gitignore` has `store/`).
- OneDrive and SeaDrive folders are never copied; they sync after sign-in.
- 1Password vaults are not files: the checklist says sign in (Secret Key).

## Paul's machine facts

- Only drive C:. OneDrive = `C:\Users\pp\OneDrive - ilscipio.com`. SeaDrive junctions in
  `C:\Seafile\*`. WSL: Ubuntu-22.04 (+ docker-desktop, skipped). 15 "AIVory *" scheduled
  tasks in `aivory-sales-manager` move; vendor tasks are filtered (`taskExclude`).
- One live KeePass database outside OneDrive (found through `KeePass.config.xml`).
- After the move: `docker-compose up -d` in `aivory-sales-manager`, then check
  `http://localhost:7777/api/sales`.
