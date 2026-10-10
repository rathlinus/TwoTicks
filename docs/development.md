# Developing TwoTicks

## Setup

You need the .NET 10 SDK and Go 1.24 or newer. On Windows:

```
winget install Microsoft.DotNet.SDK.10
winget install GoLang.Go
winget install JRSoftware.InnoSetup
```

Inno Setup is only needed to compile the setup program.

On macOS and Linux the build also needs Python 3 with two packages, for the fonts, and on macOS the command line tools of Xcode, for `clang`:

```
pip install fonttools pillow
xcode-select --install
```

## Scripts

| Script | What it does |
|---|---|
| `scripts\build.ps1` | Runs the tests, builds the helper and publishes the app to `artifacts\app`. |
| `scripts\build.ps1 -Run` | Also starts it, replacing a running copy. |
| `scripts\release.ps1 -Version 1.2.0` | Builds a release and collects the setup program and the zip in `artifacts\release`. |
| `scripts/build.sh` | The same as `build.ps1`, on macOS and Linux: the app for the system it runs on, in `artifacts/app`. `--run` starts it. |
| `scripts/release.sh --version 1.2.0` | Builds a release there: the `.tar.gz` and the `.deb` on Linux, the `.dmg` on macOS. |
| `scripts/check.sh` | Starts what `build.sh` built on made-up chats and has it check itself; see macOS and Linux below. |
| `scripts\check-upgrade.ps1` | Updates an install of version 0.5.0 with a new setup program and checks what is left; see The former name below. For the build machine only. |
| `scripts\generate-assets.ps1` | Draws the icons and the pictures of the setup wizard. Run it only to change the logo. |
| `scripts\screenshots` | Takes the README's screenshots on made-up chats; see its README. |
| `scripts\copy-whatsapp-desktop.ps1` | Copies the icon and the sounds of WhatsApp from the Microsoft Store to `Assets\WhatsAppIcon` and `Assets\WhatsAppSounds`. Needs WhatsApp installed; run it only to take a newer version. |
| `scripts\whatsapp-assets\build.py` | Takes WhatsApp Web's emoji, icons, wallpapers and font; see below. |
| `scripts\whatsapp-voip\build.py` | Takes WhatsApp Web's calling engine; see Calls below. |
| `scripts\whatsapp-assets\desktop-fonts.py` | Makes the emoji font and the text fonts of the macOS and Linux app. The build runs it. |

`dotnet build src\TwoTicks.App -p:Platform=x64` builds the app for debugging. It builds the Go helper too, whenever its sources changed.

Setting `TWOTICKS_DATA` to a folder runs a second copy of the app with its own data, beside the usual one. That is the way to try linking or logging out without touching your real session.

## Projects

| Project | What it is |
|---|---|
| `src/TwoTicks.Bridge` | The WhatsApp helper, in Go: the connection to WhatsApp, the database of chats and messages, media. |
| `src/TwoTicks.Core` | The app's side of the helper's protocol, its data, WhatsApp's text formatting and the settings. No UI. |
| `src/TwoTicks.App` | The WinUI 3 app. Most of it is the app on every system; `Windows/` holds what only Windows has. |
| `src/TwoTicks.Desktop` | The app for macOS and Linux: the sources of `TwoTicks.App` again, and what those systems have in place of `Windows/`. |
| `tests/TwoTicks.Core.Tests` | Unit tests of Core. |

## The helper

WhatsApp's protocol is implemented by [whatsmeow](https://github.com/tulir/whatsmeow), which is written in Go. `TwoTicks.Bridge.exe` wraps it: the app starts it with `--data <folder>` and talks to it over its standard input and output, one JSON object per line.

```
→ {"id":1,"method":"messages","params":{"chat":"491701234567@s.whatsapp.net","limit":60}}
← {"id":1,"result":{"messages":[…],"hasOlder":true,"hasNewer":false}}
← {"event":"message","data":{"chat":"…","id":"…","kind":"text","text":"Hi","notify":true}}
```

Every request gets one response with its id; lines with an `event` instead are things that happened on WhatsApp's side. The methods are in `methods.go`, the events are emitted with `b.out.event`.

The helper keeps two databases in the data folder:

- `session.db` is whatsmeow's: the device keys, the encryption sessions with every contact, contacts and app state.
- `chats.db` is TwoTicks's: chats, messages, reactions, receipts and profile pictures. WhatsApp's servers store no history for linked devices; what the app shows comes from history sync when linking, and every message since.

Downloaded files go to `media`, profile pictures to `avatars`. The helper writes `bridge.log`; start the app with `TWOTICKS_DEBUG=1` to get the protocol's debug output in it.

The app ties the helper to itself with a job object on Windows, so it ends with the app however the app ends. Elsewhere the helper ends when its input closes, which it does when the app is gone. When the helper crashes, the app starts it again.

### Phone numbers and LIDs

WhatsApp is moving people from phone numbers to hidden ids, LIDs (`123456@lid`). Messages from the same person can arrive with either. The helper stores chats under the phone number whenever whatsmeow knows it (`canonical` in `bridge.go`), and moves a chat stored under a LID to the number once the number becomes known.

## WhatsApp's look

The app draws with WhatsApp Web's own material: its emoji, its icons, the doodle wallpaper behind the chat, its colours and its font. `scripts\whatsapp-assets\build.py` collects them and writes:

| File | What it is |
|---|---|
| `Assets/WhatsApp/Emoji/<n>.webp` | WhatsApp's emoji sprite sheets: 25 emoji each, 40 pixels per emoji |
| `Assets/WhatsApp/emoji.json` | which emoji is in which cell, older spellings, and the categories of the picker |
| `Assets/WhatsApp/doodle-light.webp`, `doodle-dark.webp` | the default chat wallpapers |
| `Assets/Fonts/Roboto.ttf`, `Roboto-Italic.ttf` | WhatsApp Web's text font, converted from WOFF2 |
| `Controls/WaIcons.g.cs` | the icons the app uses, as SVG path data |

The scripts and sprites come from the Firefox cache of someone who uses WhatsApp Web, so open WhatsApp Web in Firefox, scroll through its emoji panel once, then run:

```
pip install zstandard brotli fonttools
python scripts\whatsapp-assets\build.py
```

It needs Node.js too: `emoji-data.js` and `icons.js` run WhatsApp Web's own data modules to read the emoji order and the icons. Only WhatsApp Web's static files are read from the cache, never media or anything else of a chat. The stylesheet with the font, and the page's list of images, are fetched from WhatsApp's servers.

The colours in `App.xaml` and `Controls/WhatsAppColors.xaml` are the values of WhatsApp Web's design tokens (`--WDS-...`) in its default light and dark theme.

`EmojiSet` in Core finds emoji in text by what a reader sees as one character, so a family or a flag is looked up whole. `EmojiSet.CellOf` gives the sheet and position, laid out as WhatsApp Web lays them: five by five, with the rest on a narrower last sheet. In the app, `Controls/Emoji.cs` draws an emoji as its sheet moved behind a clip; message text puts these into a RichTextBlock, and `EmojiLabel` lays out single lines itself, because a RichTextBlock cut to one line draws the emoji of the cut-off part at the start of the line. The emoji need the WebP codec that comes with Windows; without it the app falls back to the emoji of the Windows font.

## The app

`Session` sits between the helper and the window: it moves the helper's events to the UI thread, keeps the chat list and the open chat up to date, sends, and decides when to notify, mark chats read and report typing. `ChatList` keeps the list in order by moving rows rather than rebuilding it, so the list keeps its scroll position and selection.

The messages of a chat are a virtualized `ListView`. `Conversation` builds its rows: messages, a label at each new day, and the line above the first unread message. Older messages load when scrolling near the top. Parts of a bubble that most messages do not have (a reply, a photo, a document) are created only for the messages that have them, with `x:Load`.

Notifications use the plain Windows toast API under the AppUserModelID `TwoTicks`, which the app registers itself; the Windows App SDK's notification API does not work for apps that ship the SDK in their folder without being packaged. The unread count on the taskbar button is an overlay icon drawn by `TaskbarBadge`.

## macOS and Linux

`src/TwoTicks.Desktop` builds the same app with [Uno Platform](https://platform.uno), which implements WinUI's API and draws with Skia. It has no XAML of its own and little C#: the project takes the sources of `TwoTicks.App`, without the `Windows` folder, and adds what a system does for the app.

| Where | What is there |
|---|---|
| `TwoTicks.App/Windows/` | Windows' side: the notification area, toasts, the taskbar badge, WebView2, what Windows knows about media files |
| `TwoTicks.Desktop/` | the same classes for macOS and Linux |
| `TwoTicks.Desktop/Linux/` | D-Bus: notifications, the icon in the notification area (StatusNotifierItem) and its menu |
| `TwoTicks.Desktop/Mac/` | the menu bar icon, the notification centre and the Dock, through a small Objective-C library, `TwoTicksMac.m`, which the project builds with `clang` |

A class with two sides is `partial`: the shared file has what is common and declares what each side fills in. Where a few lines differ inside shared code, `#if HAS_UNO` marks the lines for macOS and Linux.

What Uno lacks or does differently is dealt with in the project file, which says why at each place:

- The XAML is compiled from changed copies in `obj/Xaml`. Uno has no `RichTextBlock`, so each becomes a `TextBlock`; `x:Load` on the parts of a list row becomes `Visibility`; the window's Mica is left out.
- Text cannot hold pictures, so emoji in text are a font, `WhatsAppEmoji.ttf`, made from the sprite sheets by `desktop-fonts.py`. Shown text holds each emoji as one private code point, which `Controls/Emoji.cs` explains. The picker and single lines still draw emoji from the sheets.
- The title bar is the system's.

Things to know when something breaks:

- Sound and video play through VLC on Linux. Uno only uses it when it finds `libvlc.so`, which only VLC's development package has; `Program.FindVlc` makes it work with `libvlc.so.5`.
- SkiaSharp's library for ARM on Linux leaves out libraries it needs; `Program.LoadForDrawing` loads them first.
- macOS draws nothing for a glyph without bounds, so the emoji font's glyphs have an outline with nothing to fill.
- macOS does not play Ogg, so `AudioPlayer` decodes voice messages there. `TWOTICKS_DECODE_OPUS=1` makes it do the same on Linux.

### Checking a build

`scripts/check.sh` starts the built app with `TWOTICKS_CHECK` set to a folder. The app then runs on the stand-in helper of `scripts/screenshots`, opens the first chat, writes a picture of its window and a report to that folder, and quits; the script fails when the chats did not show. On macOS it does the same for the app bundle `release.sh` made, where the menu bar icon and the notification centre exist. The CI workflow runs it on both systems and keeps the pictures and reports as artifacts named `check-...`, which is the only look at the macOS app there is without a Mac.

On Linux the app runs under `xvfb-run` when there is no screen. With `xdotool` on a virtual screen it can be clicked through from a script. `TWOTICKS_DEMO` set to `scripts/screenshots/demo`, with the stand-in helper in place of `TwoTicks.Bridge`, gives it chats without an account.

## The former name

Up to version 0.5 the app was called WinWhatsApp, and Windows PCs that had it then still have things of that name. A new version takes them over, so an update loses nothing:

| What | Who takes it over |
|---|---|
| The data folder `%LOCALAPPDATA%\WinWhatsApp` | `AppPaths.TakeOver` in Core renames it at the first start. If something still has files open in it, the app runs on the old folder and tries again at the next start. |
| The paths of downloaded files in `chats.db`, which are stored in full | `rebasePaths` in the helper, whenever the data folder is another than last time |
| A copy of the old version that still runs | `FormerName.StopRunning` ends it before the data is touched; setup does the same |
| The program folder, the Start menu entry, the entry to start at sign-in | `packaging\TwoTicks.iss`, where it mentions `FormerName`; `Startup.Refresh` for copies that were not installed |
| A button pinned to the taskbar | `AppIcon.UpdateShortcuts` points it at the new program |
| The registration for notifications | `FormerName.Forget` |

Setup keeps its AppId, so Windows sees one app that got a new version. The old version finds the new setup program because it looks for a release file ending in `-Setup.exe`, whatever comes before.

The repository was renamed with the app. The old version still asks `rathlinus/WinWhatsApp` for releases, and GitHub sends it on to this one. That lasts as long as the account has no other repository of the old name, so do not create one.

`scripts\check-upgrade.ps1` tries all of this: it installs version 0.5.0, updates it with a setup program just built and looks at what is left. It is for the build machine, which runs it for every change, and refuses to run elsewhere.

None of it runs for a copy on data of its own (`TWOTICKS_DATA`), so trying something out never touches an install.

## The name on the taskbar

The setting for the icon is one for the name as well: with WhatsApp's icon the app goes by WhatsApp everywhere outside its own window. `AppName.Shown` in Core is that name, and `App.ApplyIcon` applies it with the icon:

- the window's title, which is what the taskbar shows
- the Start menu entry on Windows, whose file is renamed, and the menu entry on Linux, which the app writes into the user's folder
- the icon in the notification area and its menu
- the name above the notifications, where the system takes one from the app

A pinned taskbar button on Windows keeps the name of its file, as Windows knows the button by that file. On macOS the icon in the Dock changes while the app runs; the name beside it is the app bundle's and stays.

## Languages

The app's text is in `src/TwoTicks.Core/Strings/<language>/*.json`, one
folder per language, with English as the fallback for anything missing. C#
reads it with `Loc.T("key")` and `Loc.Plural("key", count)`, XAML with
`{app:L Key=key}`. Values go in braces, such as `{name}`. The language follows
Windows unless one is picked in the settings; a change needs a restart.

The helper writes some text itself (group notices such as "Anna added Bob",
missed calls, and errors the app shows). That text is in
`src/TwoTicks.Bridge/text.go`, and the app passes its language with `--lang`.
Notices are stored in the language that was active when they arrived.

To add a language, copy the `en` folder to the new language code, translate
the files, add the language to `text.go`, and add it to `[Languages]` in
`packaging/TwoTicks.iss`. The tests check that every language has the same
keys and placeholders as English, and that every key the code uses exists.
## Calls

Calls run WhatsApp Web's own calling engine: WhatsApp's calling library compiled to WebAssembly, with the script Emscripten made for it. The app runs it in a browser that is never shown, and passes call stanzas between it and the helper.

On Windows the browser is WebView2, and the app and the page talk in web messages. The web views of macOS and Linux lack WebTransport, so there `CallBrowser` starts a browser that is installed, without a window and with a profile of its own that is deleted afterwards: a Chromium browser if there is one, otherwise Firefox. The page then talks to the app over a WebSocket on `PageServer`, which it may open with the key the app put into its address. `TWOTICKS_BROWSER` names another browser to use.

| File | What it is |
|---|---|
| `Assets/Voip/wa-voip-glue.js` | the engine's script, as WhatsApp Web has it |
| `Assets/Voip/manifest.json` | where WhatsApp serves the engine's binary, and its SHA-256 |
| `Assets/Voip/runtime.js` | the parts of WhatsApp Web's runtime the script expects around it |
| `Assets/Voip/worker.js` | one of the engine's threads |
| `Assets/Voip/host.js`, `host.html` | the page: starts the engine, answers its callbacks, carries messages to and from the app |
| `Assets/Voip/audio-worklet.js` | the microphone and the speakers |

The binary is not in the repository. The app downloads it from WhatsApp the first time it connects, checks it against the hash and keeps it in `Calls` in the data folder, next to the browser's profile.

The engine needs threads that share memory, so its page must be isolated from other sites. `PageServer` serves it on a free port of 127.0.0.1 with the headers for that. WebView2 could serve the files itself by intercepting requests, but the engine's threads then hang loading their scripts.

What goes where:

- The helper takes `<call>` stanzas and receipts for them as whatsmeow receives them, from its receive log, because whatsmeow's call events leave out the stanza's id and attributes. It decrypts the call key in an offer with Signal, confirms offers, accepts and rejects with a receipt, and sends the stanza on as a `callSignal` event, in WhatsApp's binary XML. The engine reads that encoding as it is.
- The engine's stanzas go back through `callSend`. The helper encrypts the call keys in an offer for each device, sends the stanza in a `<call>` and returns the server's ack, which the engine needs as well.
- The sound of a call goes to WhatsApp's relays over WebTransport, as in WhatsApp Web. The engine sends packets to relay addresses; the page opens one WebTransport session per relay from the relay list the engine reports.
- `CallManager` keeps the one call there can be, the call window and the sounds. The engine starts with the first call and stops two minutes after the last one, as it takes a few hundred megabytes.

Video and group calls are not handled: their offers show a notification to answer on the phone.

The page tells the app every second that it is alive. When it stops for 4 seconds the app logs that, and on Windows it pauses the page's thread with the browser's debugger and logs where it is, right away and again after 20 seconds. During a call the page logs every 10 seconds what went to and from the relays, the microphone and the speaker, and it logs calls into the engine that hold its thread up for more than 100 ms, the browser freezing the page, sound devices changing and relays closing. All of this lands in `app.log`, with or without `TWOTICKS_DEBUG`.

To take a newer engine, make or take a call in WhatsApp Web in Firefox, so that the cache has it, then run:

```
pip install zstandard brotli
python scripts\whatsapp-voip\build.py
```

It finds a binary in the Firefox cache together with the script made for it, and writes the script and the manifest.
