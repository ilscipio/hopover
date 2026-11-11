# Microsoft Store listing - Hopover

Paste these fields into Partner Center > Hopover > Store listings > English (United States).
Publisher: ilscipio GmbH (pending support case 2611110050000286).

## Product name

Hopover

## Category

Utilities & tools (subcategory: Backup & manage) - secondary: Developer tools

## Pricing

Free. No in-app purchases. (Coffee link stays on the website and in the app.)

## Short description (max 1000 chars; shown at the top)

Move your whole Windows PC to a new one - apps, settings, WSL, SSH keys and files - straight over your own network, while you keep working on the old PC.

## Description (max 10000 chars)

Hopover moves your PC to a new one. All of it.

Start Hopover on both PCs, type the pairing code, and go get a coffee. Your files go directly from PC to PC, encrypted. No cloud in between, no account, no USB drive.

WHAT MOVES
- Your apps: reinstalled through winget, Chocolatey and the Microsoft Store. No installer left? The program moves as a folder.
- Your dev setup: WSL distributions, Docker volumes, PostgreSQL, environment variables, PATH, SSH keys, git logins and scheduled tasks.
- Your Windows settings: taskbar, Start pins, wallpaper, theme, desktop layout, Explorer, sounds, night light, mouse, keyboard, languages, region, power plan, notifications, privacy, Wi-Fi, hosts file and Windows features.
- Your app settings: all of AppData and the registry entries that go with it.
- Your files: big repositories, junctions and symlinks included.
- Your caches, if you want them: Gradle, npm, Playwright, IDE indexes.

KEEP WORKING DURING THE MOVE
Your old PC stays in use. When you are ready to switch, click "Catch up" to fetch what changed. If a file changed on both PCs, the new PC keeps its version and the other one waits for you in C:\hopover-held. Hopover never deletes your data.

PREVIEW FIRST
Not sure? Switch on "Preview first" and Hopover writes a text file of everything it would do, before it moves a single file.

FAST
Hopover copies in parallel at low priority, so your PC stays usable. Plug one LAN cable straight from PC to PC and choose "Direct cable" for full speed, no router needed.

GOOD TO KNOW
- Windows 10 and Windows 11, on both PCs.
- Hopover needs administrator rights to install apps and apply system settings.
- Logins that Windows ties to the old PC (passwords in browsers, Microsoft accounts) cannot move. Hopover opens each sign-in in turn, so you just approve them.
- Cloud folders (OneDrive, Google Drive, Dropbox) are not copied. They sync after you sign in.

Free to use. If it works for you, a review helps a lot.

## What's new in this version

First release in the Microsoft Store.

## Product features (max 20, 200 chars each)

1. Moves apps, settings and files from your old Windows PC to the new one
2. Reinstalls apps through winget, Chocolatey and the Microsoft Store
3. Moves WSL, Docker volumes, PostgreSQL, SSH keys, env vars and scheduled tasks
4. Keeps your taskbar, Start pins, wallpaper, theme, mouse and keyboard settings
5. Direct, encrypted PC-to-PC transfer with a pairing code - no cloud, no account
6. Keep working on the old PC, then catch up with one click
7. Never deletes data: conflicts are kept side by side
8. Preview first: see everything before it moves
9. Direct LAN cable mode for full speed
10. Resumes where it stopped after a restart or a closed window

## Search terms (max 7, 30 chars each)

1. pc migration
2. move to new pc
3. transfer apps
4. windows transfer
5. wsl migration
6. pc mover
7. new computer setup

## Copyright and trademark

(c) 2026 ilscipio GmbH

## Additional license terms

Leave empty (standard Microsoft Store license terms). Source license: see LICENSE.md.

## Contact and URLs

- Website: https://www.ilscipio.com/en/apps/hopover/
- Support contact: https://github.com/ilscipio/hopover/issues
- Privacy policy URL: https://www.ilscipio.com/en/privacy-policy-hopover/ (source: ilscipio-hugo content/en/privacy-policy-hopover.md, de: datenschutzerklaerung-hopover.md; publish before submission)

## Screenshots (1366x768 or larger, PNG; up to 10)

Upload `store\screenshots\store\01..06-*.png` (1920x1200). Render again with
`$env:HOPOVER_SHOT_SCALE='1.5'; dist\Hopover.exe --shot store\screenshots`. Captions:
1. docs\welcome.png - "Move to your new PC. All of it."
2. docs\what-moves.png - "Choose what moves"
3. docs\moving.png - "Files, apps and WSL move side by side"
4. docs\test-drive.png - "Test drive your apps during the move"
5. docs\delivered.png - "Everything made it"

## Store logos (from the MSIX build)

- 300x300 Store logo (from the `Logo` DrawingImage in Theme.xaml, via build.ps1)

## Age rating questionnaire

No violence, no user-generated content, no purchases, no location, no personal data sharing -> expected rating: 3+ / Everyone.

## Submission notes for certification

Hopover is a PC migration tool. It needs the restricted capabilities runFullTrust and allowElevation:
it installs applications (winget), writes system settings, creates firewall rules for its own
PC-to-PC link (TCP 47800, UDP 47801) and reads/writes the user profile to move it to a new PC.
To test: run it on two PCs in the same network, choose "This is the old PC" on one and
"This is the new PC" on the other, enter the pairing code. "Preview first" runs without changes.

## German listing (de-DE) - TODO

The website has a German page (https://www.ilscipio.com/apps/hopover/); translate from it.
