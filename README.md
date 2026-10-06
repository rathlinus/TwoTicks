<p align="center">
  <img src="src/WinWhatsApp.App/Assets/Logo.png" width="96" alt="The WinWhatsApp logo: four green tiles with a white chat bubble across them">
</p>

# WinWhatsApp

A native WhatsApp app for Windows 11. It replaces the official app, which since 2025 is WhatsApp Web in a browser window: slow to start, slow to scroll, and a gigabyte of memory.

WinWhatsApp is a WinUI 3 app. It opens in about half a second and uses around 180 MB with a few hundred chats. It links to your phone the way WhatsApp Web does, so the phone keeps your account and WinWhatsApp is one of its linked devices.

<p align="center">
  <picture><source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/dark-chat.webp" /><img src="docs/screenshots/light-chat.webp" alt="WinWhatsApp with the chat list on the left and a group chat on the right: a reply with a link preview, a photo of a mountain lake with reactions, an edited message and read ticks" width="100%" /></picture>
  <br />
  <sub>A group chat with a link preview, a photo, reactions and an edited message</sub>
</p>

It looks like WhatsApp because it draws with WhatsApp Web's own emoji, icons, colours, chat wallpaper and font, in the light and the dark theme.

## Chats

- Your chats and recent history, synced from the phone when you link
- Pinned, muted and archived chats, in sync with the phone, and chats you mark as unread
- Filters for unread chats and groups above the list
- Unread counts, typing and online status, and the last time someone was seen
- The chat list and the chat side by side; drag the line between them to make the list wider or narrower

## Messages

- WhatsApp's formatting: \*bold\*, \_italic\_, \~strikethrough\~, \`inline code\` and \`\`\`code blocks\`\`\`
- Replies, @mentions, reactions and link previews
- Editing your messages, and deleting for everyone or for you; press Up in an empty message box to edit your last one
- Photos, videos, GIFs, stickers, documents, voice messages and locations
- Read receipts: one tick, two ticks, two blue ticks
- Send photos, videos and files with a caption each: pick them, paste them or drop them on the chat

<p align="center">
  <picture><source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/dark-media.webp" /><img src="docs/screenshots/light-media.webp" alt="A chat with a video, a photo of the Brandenburg Gate and the other person typing" width="49%" /></picture>
  <picture><source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/dark-files.webp" /><img src="docs/screenshots/light-files.webp" alt="A chat with a PDF, a zip file, a spreadsheet, two voice messages and a block of code" width="49%" /></picture>
  <br />
  <sub>Photos, a video and someone typing&nbsp;&nbsp;·&nbsp;&nbsp;Files, voice messages and code</sub>
</p>

## Photos and videos

Click a photo or video to open the viewer. It zooms, saves the file, opens it in another app and steps through every photo and video of the chat with the arrow keys.

## Search

The search field finds chats by name and messages by their text, across all chats. Click a message to jump to it.

<p align="center">
  <picture><source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/dark-viewer.webp" /><img src="docs/screenshots/light-viewer.webp" alt="The viewer showing a photo of Lago di Braies with its caption, zoom controls and buttons to save and open it" width="49%" /></picture>
  <picture><source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/dark-search.webp" /><img src="docs/screenshots/light-search.webp" alt="A search for pizza: one chat whose name matches and two messages that contain the word" width="49%" /></picture>
  <br />
  <sub>The photo and video viewer&nbsp;&nbsp;·&nbsp;&nbsp;Search across chats and messages</sub>
</p>

## Contact and group info

Click the name at the top of a chat to see who it is: the profile picture, the about text and the phone number of a person, or the description, the members and the admins of a group.

## On your desktop

- Notifications for new messages that you can reply to without opening the app
- An icon in the notification area and the number of unread chats on the taskbar button
- Keeps running in the notification area when you close the window, so messages keep arriving
- Starts when you sign in to Windows, if you want
- Light, dark or the system's theme, and WinWhatsApp's own icon or WhatsApp's

<p align="center">
  <picture><source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/dark-group-info.webp" /><img src="docs/screenshots/light-group-info.webp" alt="The info of a group beside the chat: its picture, description, who created it and the list of members" width="49%" /></picture>
  <picture><source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/dark-settings.webp" /><img src="docs/screenshots/light-settings.webp" alt="The settings: notifications, running in the notification area, starting with Windows, theme, app icon and the account" width="49%" /></picture>
  <br />
  <sub>Group info&nbsp;&nbsp;·&nbsp;&nbsp;Settings</sub>
</p>

| Keys | What they do |
|---|---|
| Ctrl+N | New chat |
| Ctrl+F | Search |
| Enter, Shift+Enter | Send, new line |
| Up | Edit your last message |
| Esc | Close the viewer or cancel a reply |
| Ctrl+S | Save the photo or video in the viewer |
| Left, Right, Home, End | Previous, next, first and last photo or video in the viewer |
| +, -, 0 | Zoom in, zoom out, fit to the window in the viewer |

## What it does not do

There are no voice or video calls. An incoming call shows a notification to answer it on the phone. Status updates, channels and communities are not shown.

## Install

You need 64-bit Windows 11.

Download `WinWhatsApp-<version>-Setup.exe` from the [releases page](https://github.com/rathlinus/WinWhatsApp/releases) and run it. It installs for your user only and needs no administrator rights.

Setup is not signed, so Windows SmartScreen may stop it with "Windows protected your PC". Choose **More info**, then **Run anyway**.

The release also has `WinWhatsApp-<version>-x64.zip` for running without installing: unpack it anywhere and start `WinWhatsApp.exe`.

## Link your phone

Open WinWhatsApp, then on your phone open WhatsApp, go to Linked devices, tap Link a device and scan the code. If you can't scan, choose **Link with phone number instead** and type the code WinWhatsApp shows into your phone.

<p align="center">
  <picture><source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/dark-link.webp" /><img src="docs/screenshots/light-link.webp" alt="The linking screen: four steps to link the phone and a QR code" width="70%" /></picture>
</p>

## A word of warning

WhatsApp has no public API for personal accounts. WinWhatsApp talks to WhatsApp's servers with [whatsmeow](https://github.com/tulir/whatsmeow), an open source implementation of the protocol of WhatsApp Web. Using a client WhatsApp did not make is against its terms of service, and WhatsApp can ban accounts for it. Bans of accounts that only chat normally are rare, but the risk is yours.

WinWhatsApp is not made by, affiliated with or endorsed by WhatsApp or Meta.

## Where your data is

Everything is in `%LOCALAPPDATA%\WinWhatsApp`: the link to your phone, the messages, downloaded files, settings and logs. Nothing leaves your PC except what WhatsApp itself sends. When you type a link, WinWhatsApp loads that page and its picture to show a preview, as WhatsApp's apps do. Logging out from the menu, or removing the device on the phone, deletes the messages and files.

## Build

See [docs/development.md](docs/development.md).

## License

WinWhatsApp's own code is under the [MIT License](LICENSE).

It includes work by others, under their own terms:

- [whatsmeow](https://github.com/tulir/whatsmeow), in the helper `WinWhatsApp.Bridge.exe`, under the Mozilla Public License 2.0
- [QRCoder](https://github.com/codebude/QRCoder), under the MIT License
- Roboto, by Google, under the Apache License 2.0
- WhatsApp's name, logo, emoji, icons, wallpapers and notification sounds belong to WhatsApp and Meta. They are not covered by the MIT License.

## Screenshots

The screenshots show made-up chats: the app runs on a stand-in for its WhatsApp helper that serves them, so no real account or person is in them. `scripts\screenshots\Take-Screenshots.ps1` takes them all again, in both themes; see [scripts/screenshots](scripts/screenshots).

The photos in them are from Wikimedia Commons, under CC0, public domain, CC BY-SA 3.0 and CC BY-SA 4.0. [CREDITS.md](scripts/screenshots/demo/CREDITS.md) lists each one with its author and license.
