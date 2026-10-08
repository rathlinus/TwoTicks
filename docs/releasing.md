# Releasing

## Publish a version

Tag the commit and push the tag:

```powershell
git tag v1.2.0
git push origin v1.2.0
```

The tag has to have the form `v<major>.<minor>.<patch>`. Pushing it starts the Release workflow, which runs the tests, builds the app and the helper with that version on Windows, macOS and Linux, and creates a GitHub release with these files:

| File | Contents |
|---|---|
| `TwoTicks-1.2.0-Setup.exe` | the setup program for Windows. This is the download the README points to. |
| `TwoTicks-1.2.0-x64.zip` | a `TwoTicks` folder with `TwoTicks.exe`, for running without installing |
| `TwoTicks-1.2.0-macos-arm64.dmg`, `-macos-x64.dmg` | a disk image with `TwoTicks.app`, for Macs with Apple silicon and with Intel processors |
| `twoticks_1.2.0_amd64.deb`, `_arm64.deb` | a package for Debian, Ubuntu and their relatives, which installs to `/opt/twoticks` |
| `TwoTicks-1.2.0-linux-x64.tar.gz`, `-linux-arm64.tar.gz` | the app's folder, which runs after unpacking |

The release starts as a draft. Each system adds its files to it, and it is published once all of them have, so a build that fails on one system leaves a draft and no release. Run the failed job again, or delete the draft and the tag.

GitHub generates the release notes from the changes since the previous tag. None of the files is signed: Windows SmartScreen can warn about the download, and macOS opens the app only after a right click and Open. The `.app` is signed for nobody in particular, which Apple silicon needs to start a program at all.

Installed copies pick the release up by themselves; see below.

## Try a release before tagging

```powershell
scripts\release.ps1 -Version 1.2.0
```

This writes the same files to `artifacts\release`. It needs Inno Setup and Go, and stops a running TwoTicks first, because that keeps its files locked.

On macOS and Linux, `scripts/release.sh --version 1.2.0` writes that system's files to `artifacts/release`, and `scripts/check.sh` starts what was built. The CI workflow runs both for every change, so a release that would not build shows up before the tag.

## Keeping up with WhatsApp

WhatsApp changes its protocol now and then, and rejects clients that are too old with a "client outdated" error, which the app shows as "WhatsApp no longer accepts this version". The fix is a newer whatsmeow:

```powershell
cd src\TwoTicks.Bridge
go get go.mau.fi/whatsmeow@latest
go mod tidy
```

Then build, check that linking and messaging still work, and release.

## The setup program

`packaging\TwoTicks.iss` is an Inno Setup script. Setup installs for the current user only and needs no administrator: it copies the app to `%LOCALAPPDATA%\Programs\TwoTicks`, adds a Start menu entry and, if ticked, an entry that starts TwoTicks at sign-in in the notification area. It closes a running TwoTicks before replacing its files.

On Windows the app updates itself with the same setup program. It asks GitHub's API for the latest release a minute after start and every six hours, and takes the asset whose name ends in `-Setup.exe`, so keep that name. Drafts and prereleases are skipped. The download is checked against the size and SHA-256 digest GitHub lists for the asset. The app then runs it with `/VERYSILENT /RELAUNCH=window` or `/RELAUNCH=background` and quits; setup waits for it to end, installs, and starts the new version the same way. When the update was started by hand, the app shows a small update window, and setup draws the same window at the same place until the new version is up. Setup writes its log to `%LOCALAPPDATA%\TwoTicks\update.log`; the `[Code]` section of the script lists the switches the app passes. Only a copy in the folder setup recorded in its uninstall entry updates itself, so the zip and development builds never replace themselves. The app on macOS and Linux looks for releases the same way and tells of a new one, with a button that opens the releases page.

The uninstaller, under Settings, Apps, stops TwoTicks and removes what it registered. It asks whether to delete the messages, files and the link to the phone in `%LOCALAPPDATA%\TwoTicks`; by default they stay, so a reinstall picks up where it left off.
