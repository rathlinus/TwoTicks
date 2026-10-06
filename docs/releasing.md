# Releasing

## Publish a version

Tag the commit and push the tag:

```powershell
git tag v1.2.0
git push origin v1.2.0
```

The tag has to have the form `v<major>.<minor>.<patch>`. Pushing it starts the Release workflow, which runs the tests, builds the app and the helper with that version and creates a GitHub release with two files:

| File | Contents |
|---|---|
| `WinWhatsApp-1.2.0-Setup.exe` | the setup program. This is the download the README points to. |
| `WinWhatsApp-1.2.0-x64.zip` | a `WinWhatsApp` folder with `WinWhatsApp.exe`, for running without installing |

GitHub generates the release notes from the changes since the previous tag. Neither file is signed, so Windows SmartScreen can warn about the download.

## Try a release before tagging

```powershell
scripts\release.ps1 -Version 1.2.0
```

This writes the same files to `artifacts\release`. It needs Inno Setup and Go, and stops a running WinWhatsApp first, because that keeps its files locked.

## Keeping up with WhatsApp

WhatsApp changes its protocol now and then, and rejects clients that are too old with a "client outdated" error, which the app shows as "WhatsApp no longer accepts this version". The fix is a newer whatsmeow:

```powershell
cd src\WinWhatsApp.Bridge
go get go.mau.fi/whatsmeow@latest
go mod tidy
```

Then build, check that linking and messaging still work, and release.

## The setup program

`packaging\WinWhatsApp.iss` is an Inno Setup script. Setup installs for the current user only and needs no administrator: it copies the app to `%LOCALAPPDATA%\Programs\WinWhatsApp`, adds a Start menu entry and, if ticked, an entry that starts WinWhatsApp at sign-in in the notification area. It closes a running WinWhatsApp before replacing its files.

The uninstaller, under Settings, Apps, stops WinWhatsApp and removes what it registered. It asks whether to delete the messages, files and the link to the phone in `%LOCALAPPDATA%\WinWhatsApp`; by default they stay, so a reinstall picks up where it left off.
