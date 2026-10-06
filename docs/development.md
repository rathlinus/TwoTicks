# Developing WinWhatsApp

## Setup

You need the .NET 10 SDK and Go 1.24 or newer:

```
winget install Microsoft.DotNet.SDK.10
winget install GoLang.Go
winget install JRSoftware.InnoSetup
```

Inno Setup is only needed to compile the setup program.

## Scripts

| Script | What it does |
|---|---|
| `scripts\build.ps1` | Runs the tests, builds the helper and publishes the app to `artifacts\app`. |
| `scripts\build.ps1 -Run` | Also starts it, replacing a running copy. |
| `scripts\release.ps1 -Version 1.2.0` | Builds a release and collects the setup program and the zip in `artifacts\release`. |
| `scripts\generate-assets.ps1` | Draws the icons and the pictures of the setup wizard. Run it only to change the logo. |
| `scripts\screenshots` | Takes the README's screenshots on made-up chats; see its README. |
| `scripts\copy-whatsapp-desktop.ps1` | Copies the icon and the sounds of WhatsApp from the Microsoft Store to `Assets\WhatsAppIcon` and `Assets\WhatsAppSounds`. Needs WhatsApp installed; run it only to take a newer version. |
| `scripts\whatsapp-assetsuild.py` | Takes WhatsApp Web's emoji, icons, wallpapers and font; see below. |
| `scripts\whatsapp-voipuild.py` | Takes WhatsApp Web's calling engine; see Calls below. |

`dotnet build src\WinWhatsApp.App -p:Platform=x64` builds the app for debugging. It builds the Go helper too, whenever its sources changed.

Setting `WINWHATSAPP_DATA` to a folder runs a second copy of the app with its own data, beside the usual one. That is the way to try linking or logging out without touching your real session.

## Projects

| Project | What it is |
|---|---|
| `src/WinWhatsApp.Bridge` | The WhatsApp helper, in Go: the connection to WhatsApp, the database of chats and messages, media. |
| `src/WinWhatsApp.Core` | The app's side of the helper's protocol, its data, WhatsApp's text formatting and the settings. No UI. |
| `src/WinWhatsApp.App` | The WinUI 3 app. |
| `tests/WinWhatsApp.Core.Tests` | Unit tests of Core. |

## The helper

WhatsApp's protocol is implemented by [whatsmeow](https://github.com/tulir/whatsmeow), which is written in Go. `WinWhatsApp.Bridge.exe` wraps it: the app starts it with `--data <folder>` and talks to it over its standard input and output, one JSON object per line.

```
→ {"id":1,"method":"messages","params":{"chat":"491701234567@s.whatsapp.net","limit":60}}
← {"id":1,"result":{"messages":[…],"hasOlder":true,"hasNewer":false}}
← {"event":"message","data":{"chat":"…","id":"…","kind":"text","text":"Hi","notify":true}}
```

Every request gets one response with its id; lines with an `event` instead are things that happened on WhatsApp's side. The methods are in `methods.go`, the events are emitted with `b.out.event`.

The helper keeps two databases in the data folder:

- `session.db` is whatsmeow's: the device keys, the encryption sessions with every contact, contacts and app state.
- `chats.db` is WinWhatsApp's: chats, messages, reactions, receipts and profile pictures. WhatsApp's servers store no history for linked devices; what the app shows comes from history sync when linking, and every message since.

Downloaded files go to `media`, profile pictures to `avatars`. The helper writes `bridge.log`; start the app with `WINWHATSAPP_DEBUG=1` to get the protocol's debug output in it.

The app ties the helper to itself with a job object, so it ends with the app however the app ends. When it crashes, the app starts it again.

### Phone numbers and LIDs

WhatsApp is moving people from phone numbers to hidden ids, LIDs (`123456@lid`). Messages from the same person can arrive with either. The helper stores chats under the phone number whenever whatsmeow knows it (`canonical` in `bridge.go`), and moves a chat stored under a LID to the number once the number becomes known.

## WhatsApp's look

The app draws with WhatsApp Web's own material: its emoji, its icons, the doodle wallpaper behind the chat, its colours and its font. `scripts\whatsapp-assetsuild.py` collects them and writes:

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
python scripts\whatsapp-assetsuild.py
```

It needs Node.js too: `emoji-data.js` and `icons.js` run WhatsApp Web's own data modules to read the emoji order and the icons. Only WhatsApp Web's static files are read from the cache, never media or anything else of a chat. The stylesheet with the font, and the page's list of images, are fetched from WhatsApp's servers.

The colours in `App.xaml` and `Controls/WhatsAppColors.xaml` are the values of WhatsApp Web's design tokens (`--WDS-...`) in its default light and dark theme.

`EmojiSet` in Core finds emoji in text by what a reader sees as one character, so a family or a flag is looked up whole. `EmojiSet.CellOf` gives the sheet and position, laid out as WhatsApp Web lays them: five by five, with the rest on a narrower last sheet. In the app, `Controls/Emoji.cs` draws an emoji as its sheet moved behind a clip; message text puts these into a RichTextBlock, and `EmojiLabel` lays out single lines itself, because a RichTextBlock cut to one line draws the emoji of the cut-off part at the start of the line. The emoji need the WebP codec that comes with Windows; without it the app falls back to the emoji of the Windows font.

## The app

`Session` sits between the helper and the window: it moves the helper's events to the UI thread, keeps the chat list and the open chat up to date, sends, and decides when to notify, mark chats read and report typing. `ChatList` keeps the list in order by moving rows rather than rebuilding it, so the list keeps its scroll position and selection.

The messages of a chat are a virtualized `ListView`. `Conversation` builds its rows: messages, a label at each new day, and the line above the first unread message. Older messages load when scrolling near the top. Parts of a bubble that most messages do not have (a reply, a photo, a document) are created only for the messages that have them, with `x:Load`.

Notifications use the plain Windows toast API under the AppUserModelID `WinWhatsApp`, which the app registers itself; the Windows App SDK's notification API does not work for apps that ship the SDK in their folder without being packaged. The unread count on the taskbar button is an overlay icon drawn by `TaskbarBadge`.

## Calls

Calls run WhatsApp Web's own calling engine: WhatsApp's calling library compiled to WebAssembly, with the script Emscripten made for it. The app runs it in a WebView2 that is never shown, and passes call stanzas between it and the helper.

| File | What it is |
|---|---|
| `Assets/Voip/wa-voip-glue.js` | the engine's script, as WhatsApp Web has it |
| `Assets/Voip/manifest.json` | where WhatsApp serves the engine's binary, and its SHA-256 |
| `Assets/Voip/runtime.js` | the parts of WhatsApp Web's runtime the script expects around it |
| `Assets/Voip/worker.js` | one of the engine's threads |
| `Assets/Voip/host.js`, `host.html` | the page: starts the engine, answers its callbacks, carries messages to and from the app |
| `Assets/Voip/audio-worklet.js` | the microphone and the speakers |

The binary is not in the repository. The app downloads it from WhatsApp the first time it connects, checks it against the hash and keeps it in `Calls` in the data folder, next to the WebView2 profile.

The engine needs threads that share memory, so its page must be isolated from other sites. `PageServer` serves it on a free port of 127.0.0.1 with the headers for that. WebView2 could serve the files itself by intercepting requests, but the engine's threads then hang loading their scripts.

What goes where:

- The helper takes `<call>` stanzas and receipts for them as whatsmeow receives them, from its receive log, because whatsmeow's call events leave out the stanza's id and attributes. It decrypts the call key in an offer with Signal, confirms offers, accepts and rejects with a receipt, and sends the stanza on as a `callSignal` event, in WhatsApp's binary XML. The engine reads that encoding as it is.
- The engine's stanzas go back through `callSend`. The helper encrypts the call keys in an offer for each device, sends the stanza in a `<call>` and returns the server's ack, which the engine needs as well.
- The sound of a call goes to WhatsApp's relays over WebTransport, as in WhatsApp Web. The engine sends packets to relay addresses; the page opens one WebTransport session per relay from the relay list the engine reports.
- `CallManager` keeps the one call there can be, the call window and the sounds. The engine starts with the first call and stops two minutes after the last one, as it takes a few hundred megabytes.

Video and group calls are not handled: their offers show a notification to answer on the phone.

To take a newer engine, make or take a call in WhatsApp Web in Firefox, so that the cache has it, then run:

```
pip install zstandard brotli
python scripts\whatsapp-voipuild.py
```

It finds a binary in the Firefox cache together with the script made for it, and writes the script and the manifest.
