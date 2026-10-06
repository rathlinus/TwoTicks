"""Writes the made-up chats the demo helper serves, for the screenshots in the README.

Downloads the pictures from Wikimedia Commons once (to artifacts/demo/cache) and
writes scripts/screenshots/demo: demo.json, the photos and profile pictures, and
their credits. The result is kept in the repository; run this only to change the
chats or the pictures.

    python scripts/screenshots/make_demo.py
"""

import base64
import io
import json
import random
import re
import time
from pathlib import Path

import requests
from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parents[2]
CACHE = ROOT / "artifacts" / "demo" / "cache"
DEMO = Path(__file__).resolve().parent / "demo"
UA = {"User-Agent": "WinWhatsApp-readme-screenshots/1.0 (https://github.com/rathlinus/WinWhatsApp)"}

# Every picture, with where it is from. The README credits them from credits.json.
PICTURES = {
    "dog": "File:Golden Retriever with a stick (Barras).jpg",
    "cat": "File:Domestic Cat Face Shot.jpg",
    "sunflower": "File:Sunflower head 2015 G1.jpg",
    "boat": "File:Sailing boat at sunset, Ionian Sea, Albania.jpg",
    "peaks": "File:Schneespitze (Stubaier Alpen).jpg",
    "books": "File:International books at Kent Library.jpg",
    "lake": "File:Lago di Braies South Tyrol 3.jpg",
    "pizza": "File:Pizza Tradición Napolitana.jpg",
    "beach": "File:Sunset at Livadhi Beach, Himare - 2020 July (2).jpg",
    "gate": "File:Brandenburger Tor morgens.jpg",
    "keyboard": "File:Backlit keyboard.jpg",
    "fox": "File:Vulpes vulpes Mallnitz 01.jpg",
}


def fetch(key: str) -> tuple[Path, dict]:
    """The picture at 1600 pixels wide, and its author and licence."""
    title = PICTURES[key]
    path = CACHE / f"{key}.jpg"
    meta_path = CACHE / f"{key}.json"
    if path.exists() and meta_path.exists():
        return path, json.loads(meta_path.read_text("utf-8"))
    r = requests.get("https://commons.wikimedia.org/w/api.php", headers=UA, params={
        "action": "query", "format": "json", "titles": title, "prop": "imageinfo",
        "iiprop": "url|extmetadata", "iiurlwidth": 1600,
        "iiextmetadatafilter": "LicenseShortName|LicenseUrl|Artist",
    }).json()
    page = next(iter(r["query"]["pages"].values()))
    info = page["imageinfo"][0]
    m = info["extmetadata"]
    meta = {
        "title": title,
        "page": info["descriptionurl"],
        "artist": m.get("Artist", {}).get("value", ""),
        "license": m.get("LicenseShortName", {}).get("value", ""),
        "licenseUrl": m.get("LicenseUrl", {}).get("value", ""),
    }
    CACHE.mkdir(parents=True, exist_ok=True)
    path.write_bytes(requests.get(info["thumburl"], headers=UA).content)
    meta_path.write_text(json.dumps(meta, indent=2, ensure_ascii=False), "utf-8")
    return path, meta


def avatar(key: str) -> str:
    """A profile picture, by its path in the demo folder; the helper makes it absolute."""
    source, _ = fetch(key)
    target = DEMO / "avatars" / f"{key}.jpg"
    target.parent.mkdir(parents=True, exist_ok=True)
    ImageOps.fit(Image.open(source).convert("RGB"), (480, 480)).save(target, quality=85)
    return f"avatars/{key}.jpg"


def photo(key: str, thumb_size: int = 96, keep: bool = True) -> dict:
    """Media of a photo: the file, its size and a small preview, as WhatsApp sends them.
    Without keep, only the preview, as for a video that is not downloaded."""
    source, _ = fetch(key)
    image = Image.open(source).convert("RGB")
    image.thumbnail((1280, 1280))
    media = {"mime": "image/jpeg", "w": image.width, "h": image.height}
    if keep:
        target = DEMO / "photos" / f"{key}.jpg"
        target.parent.mkdir(parents=True, exist_ok=True)
        image.save(target, quality=85)
        media.update(size=target.stat().st_size, path=f"photos/{key}.jpg")
    thumb = image.copy()
    thumb.thumbnail((thumb_size, thumb_size))
    buffer = io.BytesIO()
    thumb.save(buffer, "JPEG", quality=70)
    media["thumb"] = base64.b64encode(buffer.getvalue()).decode()
    return media


def voice(seconds: int, seed: int) -> dict:
    """Media of a voice message with a waveform: 64 loudness values from 0 to 100, as
    WhatsApp sends them, in bursts like words with short pauses between."""
    rng = random.Random(seed)
    wave = []
    while len(wave) < 64:
        word = rng.randint(3, 9)
        peak = rng.uniform(40, 100)
        wave += [int(peak * (0.2 + 0.8 * rng.random())) for _ in range(word)]
        wave += [rng.randint(3, 15) for _ in range(rng.randint(1, 3))]
    return {"mime": "audio/ogg; codecs=opus", "size": seconds * 1700, "secs": seconds,
            "wave": base64.b64encode(bytes(wave[:64])).decode()}


def main() -> None:
    now = int(time.time())
    minute, hour, day = 60, 3600, 86400

    me = {"jid": "447700900100@s.whatsapp.net", "name": "Alex"}
    people = {
        "mia": ("447700900101@s.whatsapp.net", "Mia Schneider"),
        "jonas": ("447700900102@s.whatsapp.net", "Jonas Weber"),
        "priya": ("447700900103@s.whatsapp.net", "Priya Nair"),
        "sam": ("447700900104@s.whatsapp.net", "Sam Okafor"),
        "mum": ("447700900105@s.whatsapp.net", "Mum"),
        "tom": ("447700900106@s.whatsapp.net", "Tom Becker"),
        "lena": ("447700900107@s.whatsapp.net", "Lena"),
        "daniel": ("447700900108@s.whatsapp.net", "Daniel"),
        "grandpa": ("447700900109@s.whatsapp.net", "Grandpa"),
        "noah": ("447700900110@s.whatsapp.net", "Noah Fischer"),
    }
    hiking = "120363000000000001@g.us"
    books = "120363000000000002@g.us"
    pizza = "120363000000000003@g.us"
    office = "120363000000000004@g.us"
    running = "120363000000000005@g.us"

    messages: dict[str, list] = {}
    seq = [0]

    def add(chat, who, ago, kind="text", text=None, **extra):
        seq[0] += 1
        from_me = who == "me"
        jid, name = (me["jid"], me["name"]) if from_me else people[who]
        m = {
            "chat": chat, "id": f"DEMO{seq[0]:04d}", "seq": seq[0], "sender": jid, "fromMe": from_me,
            "ts": now - ago, "kind": kind, "status": 3 if from_me else 0,
        }
        if not from_me and chat.endswith("@g.us"):
            m["senderName"] = name
        if text:
            m["text"] = text
        m.update(extra)
        messages.setdefault(chat, []).append(m)
        return m

    def quote(m):
        q = {"id": m["id"], "sender": m["sender"], "kind": m["kind"], "fromMe": m["fromMe"]}
        if "senderName" in m:
            q["senderName"] = m["senderName"]
        if m.get("text"):
            q["text"] = m["text"]
        return q

    def react(emoji, *who):
        return [{"sender": me["jid"] if w == "me" else people[w][0], "name": None if w == "me" else people[w][1],
                 "emoji": emoji, "fromMe": w == "me"} for w in who]

    # The hiking group: most of what a chat can show.
    add(hiking, "priya", day + 5 * hour, text="Okay, *Saturday* it is. Who's driving?")
    driving = messages[hiking][-1]
    add(hiking, "me", day + 5 * hour - 4 * minute, text="I can take 4 people 🚗")
    add(hiking, "sam", day + 5 * hour - 6 * minute, text="I'll bring snacks. _Lots_ of snacks.",
        quote=quote(driving), reactions=react("😂", "priya", "jonas"))
    add(hiking, "jonas", 3 * hour, "document", "Hut booking confirmation.pdf",
        media={"mime": "application/pdf", "size": 188_416, "name": "Hut booking confirmation.pdf", "pages": 2})
    messages[hiking][-1].pop("text")
    add(hiking, "jonas", 3 * hour - minute, text="Booked the Plätzwiese hut for Saturday night 🎉",
        reactions=react("🎉", "priya", "sam", "me"))
    hut = messages[hiking][-1]
    add(hiking, "priya", 90 * minute, text="@447700900104 don't forget the snacks 😄",
        mentions={"447700900104": "Sam Okafor"})
    add(hiking, "me", 80 * minute, "voice", media=voice(18, 1), status=4)
    add(hiking, "sam", 40 * minute, text="Weather looks perfect https://en.wikipedia.org/wiki/Lago_di_Braies",
        quote=quote(hut),
        link={"url": "https://en.wikipedia.org/wiki/Lago_di_Braies", "title": "Lago di Braies - Wikipedia",
              "description": "Lago di Braies is a lake in the Prags Dolomites in South Tyrol, Italy.",
              "thumb": photo("lake")["thumb"]})
    add(hiking, "jonas", 15 * minute, "image", "This is where we start. 06:30 at the parking lot ⏰", media=photo("lake"),
        reactions=react("😍", "priya", "me"))
    add(hiking, "me", 6 * minute, text="Alarm set for 05:45. See you there! 👋", edited=True, status=2)
    add(hiking, "jonas", 2 * minute, text="Can't wait 🙌")

    # A chat with one person: photos, a video, a reply.
    add(people["mia"][0], "mia", 6 * hour, text="Are you still coming tonight?")
    add(people["mia"][0], "me", 6 * hour - 2 * minute, text="Yes! Leaving in 10 min")
    add(people["mia"][0], "mia", 5 * hour, "image", "Saved you a slice from our favourite pizza place 🍕", media=photo("pizza"))
    slice_ = messages[people["mia"][0]][-1]
    add(people["mia"][0], "me", 5 * hour - minute, text="You're the best 😍", quote=quote(slice_),
        reactions=react("❤️", "mia"))
    beach = photo("beach", 480, keep=False)  # a sharp preview, since a video is never downloaded here
    add(people["mia"][0], "mia", 2 * hour, "video", "Last summer, still my favourite 🌅",
        media={"mime": "video/mp4", "size": 6_400_000, "w": beach["w"], "h": beach["h"], "secs": 24, "thumb": beach["thumb"]})
    add(people["mia"][0], "me", 2 * hour - 3 * minute, text="We need to go back there")
    add(people["mia"][0], "me", 25 * minute, "image", "Guess where I am 😄", media=photo("gate"), status=3)
    add(people["mia"][0], "mia", 20 * minute, text="Wait, you're in *Berlin*?!")

    # A chat about work: files, voice messages and, last so it shows, code.
    noah = people["noah"][0]
    add(noah, "noah", 5 * hour, text="Here's the release 👇")
    add(noah, "noah", 5 * hour - minute, "document",
        media={"mime": "application/pdf", "size": 241_664, "name": "Release notes 2.4.pdf", "pages": 3})
    add(noah, "noah", 5 * hour - 2 * minute, "document",
        media={"mime": "application/zip", "size": 2_516_582, "name": "screenshots.zip"})
    add(noah, "me", 4 * hour, "document",
        media={"mime": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "size": 48_128,
               "name": "Budget 2026.xlsx"}, status=3)
    add(noah, "noah", 3 * hour, "voice", media=voice(72, 2), status=4)
    add(noah, "me", 2 * hour, "voice", media=voice(31, 3))
    add(noah, "noah", 90 * minute, text="Can you look at this before I merge? The `retry` loop never stops when the server is down")
    add(noah, "noah", 90 * minute - 30, text="```\nasync function fetchWithRetry(url, tries = 3) {\n"
        "  for (let i = 0; i < tries; i++) {\n    try {\n      return await fetch(url);\n"
        "    } catch {\n      await sleep(2 ** i * 1000);\n    }\n  }\n"
        "  throw new Error(`Gave up on ${url}`);\n}\n```")
    add(noah, "me", 70 * minute, text="Looks good, ship it 🚀", status=3,
        reactions=react("👍", "noah"))

    add(people["mum"][0], "mum", 50 * minute, text="Call me when you land ❤️")
    for i, line in enumerate(["Chapter 12 was wild", "No spoilers please!!", "I'm only on chapter 9 😭", "Same",
                              "Next meeting at mine?", "Bringing cake 🍰"]):
        add(books, ["priya", "sam", "tom", "lena", "priya", "lena"][i], 2 * hour - i * 5 * minute, text=line)
    add(people["tom"][0], "tom", 3 * hour + 10 * minute, text="Dinner on Friday?")
    add(people["tom"][0], "me", 3 * hour, text="Sure, Friday works", status=2)
    add(people["lena"][0], "lena", 4 * hour, "voice", media=voice(42, 4))
    add(pizza, "me", 5 * hour + 30 * minute, text="Pizza at mine this Friday?")
    add(pizza, "sam", 5 * hour, "image", "Practising 👨‍🍳", media=photo("pizza"))
    add(people["daniel"][0], "daniel", day + 2 * hour, "document",
        media={"mime": "application/pdf", "size": 412_000, "name": "Flat contract.pdf", "pages": 6})
    add(office, "tom", day + 6 * hour, text="See you at 12:30")
    add(people["grandpa"][0], "grandpa", 2 * day + 3 * hour, text="Thanks for the photos!")
    add(running, "jonas", 3 * day, text="Sunday 9:00, usual spot")

    def last(chat):
        m = messages[chat][-1]
        l = {"id": m["id"], "fromMe": m["fromMe"], "kind": m["kind"], "status": m["status"]}
        if m.get("text"):
            l["text"] = m["text"]
        if m.get("senderName"):
            l["senderName"] = m["senderName"].split()[0]
        if m.get("media", {}).get("name"):
            l["name"] = m["media"]["name"]
        if m.get("media", {}).get("secs"):
            l["secs"] = m["media"]["secs"]
        return l

    def chat(jid, name, avatar_key=None, **extra):
        c = {"jid": jid, "name": name, "ts": messages[jid][-1]["ts"], "last": last(jid)}
        if jid.endswith("@g.us"):
            c["group"] = True
        if avatar_key:
            c["avatar"] = avatars[jid] = avatar(avatar_key)
        c.update(extra)
        return c

    # Your own picture, as beside your voice messages.
    avatars: dict[str, str] = {me["jid"]: avatar("fox")}
    chats = [
        chat(hiking, "Hiking crew 🏔️", "peaks", pinned=now - 10 * day, members=4),
        chat(people["mia"][0], "Mia Schneider", "dog", pinned=now - 20 * day),
        chat(people["mum"][0], "Mum", "sunflower", unread=1),
        chat(books, "Book club 📚", "books", unread=14, mutedUntil=-1, members=9),
        chat(noah, "Noah Fischer", "keyboard"),
        chat(people["tom"][0], "Tom Becker", "boat"),
        chat(people["lena"][0], "Lena", "cat", unread=1),
        chat(pizza, "Pizza Friday 🍕", None, members=5),
        chat(people["daniel"][0], "Daniel"),
        chat(office, "Office lunch", None, members=12, mutedUntil=now + 8 * hour),
        chat(people["grandpa"][0], "Grandpa"),
        chat(running, "Running club", None, archived=True, members=23),
    ]

    demo = {
        # The helper moves every time by the time since then, so the chats look as recent as now.
        "now": now,
        "qr": "2@WinWhatsAppDemo,ThisCodeLinksNothing,OnlyForScreenshots==,0123456789abcdef",
        "me": me,
        "chats": chats,
        "messages": messages,
        "avatars": avatars,
        "profiles": {
            people["mia"][0]: {"jid": people["mia"][0], "name": "Mia Schneider", "phone": "+44 7700 900101", "about": "Coffee first ☕"},
        },
        "groups": {
            hiking: {
                "name": "Hiking crew 🏔️", "topic": "Weekend hikes around the Dolomites. Photos welcome!",
                "created": now - 200 * day, "createdBy": "Jonas Weber",
                "members": [
                    {"jid": me["jid"], "name": "You", "me": True},
                    {"jid": people["jonas"][0], "name": "Jonas Weber", "admin": True},
                    {"jid": people["priya"][0], "name": "Priya Nair"},
                    {"jid": people["sam"][0], "name": "Sam Okafor"},
                ],
            },
        },
        "contacts": [{"jid": j, "name": n, "phone": "+" + j.split("@")[0]} for j, n in people.values()],
        "presence": {people["mia"][0]: {"jid": people["mia"][0], "online": True}},
        "typing": [{"chat": people["mia"][0], "sender": people["mia"][0], "typing": True}],
    }

    DEMO.mkdir(parents=True, exist_ok=True)
    (DEMO / "demo.json").write_text(json.dumps(demo, ensure_ascii=False, indent=1), "utf-8")
    lines = ["# Credits", "", "The photos and profile pictures here are from Wikimedia Commons, cropped and scaled.", "",
             "| Photo | By | License |", "|---|---|---|"]
    for key in PICTURES:
        meta = fetch(key)[1]
        # Commons gives the author as HTML, sometimes with where the photo was posted first.
        artist = re.sub(r"<[^>]+>", "", meta["artist"]).replace(" ", " ").split(":")[0].strip()
        license_ = f"[{meta['license']}]({meta['licenseUrl']})" if meta["licenseUrl"] else meta["license"]
        lines.append(f"| [{meta['title'][5:]}]({meta['page']}) | {artist} | {license_} |")
    (DEMO / "CREDITS.md").write_text("\n".join(lines) + "\n", "utf-8")
    print(f"Wrote {DEMO}")


if __name__ == "__main__":
    main()
