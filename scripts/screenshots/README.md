# Screenshots

The screenshots in the README show made-up chats. The app runs on `demo-bridge`, a stand-in for `TwoTicks.Bridge.exe` that speaks the same protocol but never connects to WhatsApp. It serves the chats in `demo`.

| Path | What it is |
|---|---|
| `Take-Screenshots.ps1` | Takes every screenshot in the light and the dark theme and saves them to `docs\screenshots` as `<theme>-<name>.webp`. |
| `Capture.ps1` | The helpers it uses: start the demo, open a chat, click, save the window. |
| `demo-bridge` | The stand-in helper, in Go. |
| `demo` | The made-up chats (`demo.json`), their photos and profile pictures, and [their credits](demo/CREDITS.md). |
| `make_demo.py` | Writes `demo`. Run it only to change the chats or the pictures. |

## Take them again

Build the app, then run the script:

```powershell
scripts\build.ps1
scripts\screenshots\Take-Screenshots.ps1
```

It needs Go, and Python with Pillow (`pip install pillow`) for the WebP files. It copies the app to `artifacts\demo\app` with its own data folder, so your own TwoTicks keeps running beside it. Leave the mouse alone while it runs: opening a photo is a real click, at a position that holds for the window size it sets (1400 × 900) at 100 % display scaling.

For the notification it moves the window to the bottom right corner of the main screen and has the stand-in send a new message, the `incoming` one in `demo.json`. Windows draws notifications in its own theme, not the app's, so the script switches Windows to light and to dark for that shot and back to what it was afterwards. Do not disturb has to be off.

The stand-in moves every time in `demo.json` forward to now, so the chats always look recent. `TWOTICKS_DEMO_QR=1` makes it show the linking screen.

## Change the chats

Edit `make_demo.py` and run it (`pip install requests pillow`). It downloads the photos from Wikimedia Commons once, to `artifacts\demo\cache`, and writes `demo` with the credits. Use only photos under a free license, and none of identifiable people. The phone numbers are from +44 7700 900xxx, a range the UK keeps for fiction.
