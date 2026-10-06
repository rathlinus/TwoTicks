# Screenshots

The screenshots in the README show made-up chats. The app runs on `demo-bridge`, a stand-in for `WinWhatsApp.Bridge.exe` that speaks the same protocol but never connects to WhatsApp. It serves the chats `make_demo.py` writes, with photos from Wikimedia Commons.

Everything goes to `artifacts\demo`, with its own data folder, so your own WinWhatsApp keeps running beside it.

## Take them again

Build the app first (`scripts\build.ps1`), then:

```powershell
# A copy of the app with the stand-in in place of the helper.
Remove-Item -Recurse -Force artifacts\demo\app -ErrorAction SilentlyContinue
Copy-Item -Recurse artifacts\app artifacts\demo\app
go build -C scripts\screenshots\demo-bridge -o ..\..\..\artifacts\demo\app\WinWhatsApp.Bridge.exe .

pip install requests pillow
$env:PYTHONIOENCODING = 'utf-8'
. scripts\screenshots\Capture.ps1
$out = 'docs\screenshots'

python scripts\screenshots\make_demo.py --theme Light
Start-Demo
Select-Chat 'Hiking crew'; Save-Window "$out\chat.png"
Invoke-Element 'HeaderButton'; Start-Sleep 2; Save-Window "$out\group-info.png"
Invoke-Element 'Close'
Invoke-Point 765 467; Start-Sleep 2; Save-Window "$out\viewer.png"   # the lake photo
Invoke-Element 'Close'
Set-Field 'Search or start a new chat' 'pizza'; Save-Window "$out\search.png"

python scripts\screenshots\make_demo.py --theme Dark
Start-Demo
Select-Chat 'Mia'; Start-Sleep 2; Save-Window "$out\chat-dark.png"

python scripts\screenshots\make_demo.py --qr
Start-Demo; Save-Window "$out\link.png"
Stop-Demo
```

The README uses `viewer.jpg`: convert `viewer.png` to JPEG, since the photo makes the PNG large. `Invoke-Point` clicks in window pixels, so the photo's position holds only for the window size `make_demo.py` sets (1400 × 900) at 100 % display scaling.

`make_demo.py` downloads the photos once to `artifacts\demo\cache` and writes their authors and licenses to `artifacts\demo\credits.json`. When you change a photo, update the credits at the end of the README.

The phone numbers are from +44 7700 900xxx, a range the UK keeps for fiction.
