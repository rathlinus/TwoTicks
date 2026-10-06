# WinWhatsApp

A native WhatsApp app for Windows 11. It replaces the official app, which since 2025 is WhatsApp Web in a browser window: slow to start, slow to scroll, and a gigabyte of memory.

WinWhatsApp is a WinUI 3 app. It opens in about half a second and uses around 180 MB with a few hundred chats. It links to your phone the way WhatsApp Web does, so the phone keeps your account and WinWhatsApp is one of its linked devices.

![](src/WinWhatsApp.App/Assets/Logo.png)

## What it does

- Link with a QR code, or with a code typed on the phone
- Your chats and recent history, synced from the phone when you link
- Text with WhatsApp's formatting, links, mentions and replies
- Photos, videos, GIFs, stickers, documents, voice messages and locations
- Send text, photos, videos and files: pick them, paste them or drop them on the chat
- Reactions, editing your messages, deleting for everyone or for you
- Read receipts, typing and online status, unread counts
- Notifications you can reply to, an icon in the notification area and the unread count on the taskbar button
- Pin, mute, archive and mark chats unread, in sync with the phone
- Search across chats and messages
- Light and dark theme

It looks like WhatsApp: WhatsApp's emoji, icons, colours, chat wallpaper and font, taken from WhatsApp Web, in its light and dark theme.

It does not do voice or video calls; an incoming call shows a notification to answer it on the phone. Status updates, channels and communities are not shown.

## A word of warning

WhatsApp has no public API for personal accounts. WinWhatsApp talks to WhatsApp's servers with [whatsmeow](https://github.com/tulir/whatsmeow), an open source implementation of the protocol of WhatsApp Web. Using a client WhatsApp did not make is against its terms of service, and WhatsApp can ban accounts for it. Bans of accounts that only chat normally are rare, but the risk is yours.

WinWhatsApp is not made by, affiliated with or endorsed by WhatsApp or Meta. The name, the logo, the emoji, the icons and the wallpapers are theirs; Roboto is Google's, under the Apache License.

## Install

Run `WinWhatsApp-<version>-Setup.exe`. It installs for your user only and needs no administrator rights. Open WinWhatsApp, then on your phone open WhatsApp, go to Linked devices, tap Link a device and scan the code.

The zip download runs without installing: unpack it anywhere and start `WinWhatsApp.exe`.

## Where your data is

Everything is in `%LOCALAPPDATA%\WinWhatsApp`: the link to your phone, the messages, downloaded files, settings and logs. Nothing leaves your PC except what WhatsApp itself sends. Logging out from the menu, or removing the device on the phone, deletes the messages and files.

## Build

See [docs/development.md](docs/development.md).
