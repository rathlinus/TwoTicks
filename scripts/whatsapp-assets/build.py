"""Takes WhatsApp Web's emoji, icons, chat wallpapers and font for WinWhatsApp.

WhatsApp Web keeps its scripts and emoji sprites in the Firefox cache of
whoever uses it. This reads them from there, fetches what is not cached (the
stylesheet with the font and the page's list of images) from WhatsApp's
servers, and writes what the app uses:

    src/WinWhatsApp.App/Assets/WhatsApp/Emoji/<n>.webp   the emoji sprite sheets
    src/WinWhatsApp.App/Assets/WhatsApp/emoji.json        which emoji is in which cell
    src/WinWhatsApp.App/Assets/WhatsApp/doodle-*.webp     the chat wallpapers
    src/WinWhatsApp.App/Assets/Fonts/Roboto*.ttf          the text font
    src/WinWhatsApp.App/Controls/WaIcons.g.cs             the icons as path data

Run it after using WhatsApp Web in Firefox, so the cache is fresh:

    pip install zstandard brotli fonttools
    python scripts/whatsapp-assets/build.py

Needs Python 3.10 or newer and Node.js.
"""
import argparse
import glob
import gzip
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import urllib.request
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
ASSETS = os.path.join(REPO, 'src', 'WinWhatsApp.App', 'Assets')
ICONS_CS = os.path.join(REPO, 'src', 'WinWhatsApp.App', 'Controls', 'WaIcons.g.cs')

USER_AGENT = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:143.0) Gecko/20100101 Firefox/143.0'
WANTED_URL = re.compile(r'^https://(static\.whatsapp\.net|web\.whatsapp\.com)/')
# Never anything of a chat: media, profile pictures, the media cache.
PRIVATE_URL = re.compile(r'pps\.whatsapp\.net|mmg\.whatsapp\.net|media[-.]|/v/t\d|_media_cache_|\.enc\b', re.I)

# The wallpapers WhatsApp Web uses by default, by their number in the page's
# list of images: the beige one of the light theme and the one of the dark theme.
WALLPAPERS = {'doodle-light.webp': '31422', 'doodle-dark.webp': '31401'}

# The icons the app uses: its name for them and WhatsApp Web's module.
ICONS = {
    'Read': 'WDSIconWdsIcRead', 'Delivered': 'WDSIconWdsIcDelivered', 'Sent': 'WDSIconIcCheck',
    'Pending': 'WDSIconWdsIcStatusPending', 'Failed': 'WDSIconIcErrorFilled',
    'NewChat': 'WDSIconWdsIcNewChat', 'MoreVert': 'WDSIconIcMoreVert', 'Search': 'WDSIconIcSearch',
    'Archive': 'WDSIconIcArchive', 'Unarchive': 'WDSIconIcUnarchive', 'Muted': 'WDSIconIcNotificationsOff',
    'Notifications': 'WDSIconIcNotifications', 'Pin': 'WDSIconIcPushPin', 'Unpin': 'WDSIconWdsIcPushPinSlash',
    'Unread': 'WDSIconIcUnread', 'Mood': 'WDSIconIcMood', 'Add': 'WDSIconIcAdd', 'Attach': 'WAWebIcAttachFileIcon',
    'Send': 'WDSIconIcSendFilled', 'Mic': 'WDSIconIcMic', 'Document': 'WDSIconIcDescription',
    'DocumentFilled': 'WDSIconIcDescriptionFilled', 'Image': 'WDSIconIcImage', 'Media': 'WDSIconIcPermMedia',
    'Camera': 'WDSIconIcPhotoCamera', 'Video': 'WDSIconIcVideocam', 'Headphones': 'WDSIconIcHeadphones',
    'Sticker': 'WDSIconWdsIcSticker', 'Location': 'WDSIconIcLocationOn', 'Person': 'WDSIconIcPerson',
    'Poll': 'WDSIconWdsIcPoll', 'Block': 'WDSIconIcBlock', 'ViewOnce': 'WDSIconWdsIcViewOnce',
    'Schedule': 'WDSIconIcSchedule', 'Unsupported': 'WDSIconWdsIcUnsupportedMessage', 'Download': 'WDSIconIcDownload',
    'Play': 'WDSIconIcPlayArrowFilled', 'Pause': 'WDSIconIcPauseFilled', 'ChevronDown': 'WDSIconIcChevronDown',
    'Close': 'WDSIconIcClose', 'Back': 'WDSIconIcArrowBack', 'Reply': 'WDSIconIcReply', 'Copy': 'WDSIconIcContentCopy',
    'Edit': 'WDSIconIcEdit', 'Delete': 'WDSIconIcDelete', 'React': 'WDSIconWdsIcMoodAdd', 'OpenInNew': 'WDSIconIcOpenInNew',
    'Refresh': 'WDSIconIcRefresh', 'Settings': 'WDSIconIcSettings', 'Logout': 'WDSIconIcLogout', 'History': 'WDSIconIcHistory',
    'Lock': 'WDSIconIcLockFilled', 'Group': 'WDSIconIcGroupFilled', 'PersonFilled': 'WDSIconIcPersonFilled',
    'Call': 'WDSIconIcCall', 'Link': 'WDSIconIcLink', 'Info': 'WDSIconIcInfo', 'Chat': 'WDSIconWdsIcChat',
    'ChatFilled': 'WDSIconWdsIcChatFilled', 'Gif': 'WDSIconIcGif', 'Disappearing': 'WDSIconWdsIcDisappearingMessages',
    'Folder': 'WDSIconIcPermMedia', 'Check': 'WDSIconIcCheck', 'Filter': 'WDSIconIcFilter', 'Smartphone': 'WDSIconIcDevices',
    'EmojiRecent': 'WDSIconIcSchedule', 'EmojiPeople': 'WDSIconIcMood', 'EmojiNature': 'WDSIconIcEmojiNature',
    'EmojiFood': 'WDSIconIcEmojiFoodBeverage', 'EmojiActivity': 'WDSIconIcSportsBasketball', 'EmojiTravel': 'WDSIconIcDirectionsCar',
    'EmojiObjects': 'WDSIconIcEmojiObjects', 'EmojiSymbols': 'WDSIconIcEmojiSymbols', 'EmojiFlags': 'WDSIconIcFlag',
    'ZoomIn': 'WDSIconIcZoomIn', 'Visibility': 'WDSIconIcVisibility', 'Keyboard': 'WDSIconIcKeyboard',
}


def log(text):
    print(text, flush=True)


# ---- The Firefox cache ----

def find_cache(profile):
    root = os.path.expandvars(r'%LOCALAPPDATA%\Mozilla\Firefox\Profiles')
    candidates = [os.path.join(root, profile)] if profile else glob.glob(os.path.join(root, '*'))
    caches = [os.path.join(p, 'cache2', 'entries') for p in candidates if os.path.isdir(os.path.join(p, 'cache2', 'entries'))]
    if not caches:
        sys.exit('No Firefox cache found. Open WhatsApp Web in Firefox first.')
    return max(caches, key=lambda c: len(os.listdir(c)))


def read_entry(path):
    with open(path, 'rb') as f:
        data = f.read()
    if len(data) < 8:
        return None
    offset = struct.unpack('>I', data[-4:])[0]
    if offset > len(data):
        return None
    body, meta = data[:offset], data[offset:]
    # The metadata starts with a hash of itself and one hash per 256 KiB chunk.
    pos = 4 + 2 * ((offset + 262143) // 262144)
    version, *_, key_size = struct.unpack('>7I', meta[pos:pos + 28])
    pos += 28 + (4 if version >= 2 else 0)
    key = meta[pos:pos + key_size].decode('utf-8', 'replace')
    head = re.search(rb'response-head\x00(.*?)\x00', meta[pos + key_size + 1:], re.S)
    url = key[key.find(':http') + 1:] if ':http' in key else key
    return url, body, head.group(1).decode('latin-1') if head else ''


def decode_body(body, head):
    encoding = re.search(r'(?im)^content-encoding:\s*(\S+)', head)
    encoding = encoding.group(1).lower() if encoding else ''
    if encoding == 'gzip':
        return gzip.decompress(body)
    if encoding == 'deflate':
        return zlib.decompress(body)
    if encoding == 'br':
        import brotli
        return brotli.decompress(body)
    if encoding == 'zstd':
        import zstandard
        return zstandard.ZstdDecompressor().decompress(body, max_output_size=64 << 20)
    return body


def extract_cache(cache, out):
    """Copies WhatsApp Web's own files out of the cache, named after their URL."""
    os.makedirs(out, exist_ok=True)
    count = 0
    for name in os.listdir(cache):
        try:
            entry = read_entry(os.path.join(cache, name))
        except (OSError, struct.error):
            continue
        if not entry:
            continue
        url, body, head = entry
        if not WANTED_URL.search(url) or PRIVATE_URL.search(url):
            continue
        try:
            content = decode_body(body, head)
        except Exception as e:
            log(f'  skipped {url}: {e}')
            continue
        safe = re.sub(r'[^A-Za-z0-9._-]+', '_', url.split('://', 1)[1])[:180]
        with open(os.path.join(out, safe), 'wb') as f:
            f.write(content)
        count += 1
    return count


def fetch(url, page=False):
    """Downloads a file the way Firefox asks for it; WhatsApp refuses requests that look otherwise."""
    if page:
        headers = {
            'User-Agent': USER_AGENT,
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
            'Accept-Language': 'en',
            'Sec-Fetch-Dest': 'document',
            'Sec-Fetch-Mode': 'navigate',
            'Sec-Fetch-Site': 'none',
            'Sec-Fetch-User': '?1',
            'Upgrade-Insecure-Requests': '1',
        }
    else:
        headers = {'User-Agent': USER_AGENT, 'Referer': 'https://web.whatsapp.com/', 'Origin': 'https://web.whatsapp.com'}
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=30) as response:
        data = response.read()
        if response.headers.get('Content-Encoding') == 'gzip':
            data = gzip.decompress(data)
        return data


# ---- Emoji ----

def build_emoji(work, node):
    sprites = {}
    for path in glob.glob(os.path.join(work, '*emoji_v1_*_sprite_w_40_*.webp')):
        sprites[int(re.search(r'_(\d+)\.webp$', path).group(1))] = path
    if not sprites or sorted(sprites) != list(range(len(sprites))):
        sys.exit('The emoji sprite sheets are not all in the cache. Scroll through the emoji panel of WhatsApp Web once, then run this again.')

    data_file = os.path.join(work, 'emoji-data.json')
    subprocess.run([node, os.path.join(HERE, 'emoji-data.js'), work, data_file], check=True)
    data = json.load(open(data_file, encoding='utf-8'))

    # Cells follow the list, skipping empty entries; legacy spellings point at
    # entries of the list by their position in it.
    glyphs, dense_of = [], {}
    for position, entry in enumerate(data['ordered']):
        if entry in ('', None):
            continue
        dense_of[position] = len(glyphs)
        glyphs.append(entry if isinstance(entry, list) else [entry])
    legacy = {text: dense_of[position] for text, position in data['legacy'].items() if position in dense_of}

    sheets = (len(glyphs) + 24) // 25
    if sheets != len(sprites):
        sys.exit(f'{len(glyphs)} emoji need {sheets} sprite sheets, but the cache has {len(sprites)}.')

    target = os.path.join(ASSETS, 'WhatsApp', 'Emoji')
    if os.path.isdir(target):
        shutil.rmtree(target)
    os.makedirs(target)
    for number, path in sprites.items():
        shutil.copyfile(path, os.path.join(target, f'{number}.webp'))
    with open(os.path.join(ASSETS, 'WhatsApp', 'emoji.json'), 'w', encoding='utf-8') as f:
        json.dump({'glyphs': glyphs, 'legacy': legacy, 'categories': data['categories']}, f, ensure_ascii=False, separators=(',', ':'))
    log(f'  {len(glyphs)} emoji on {len(sprites)} sheets')


# ---- Wallpapers and font ----

def build_wallpapers(work, page):
    images = {key: url.replace('\\/', '/') for key, url in re.findall(r'"(\d+)":\{"uri":"([^"]+)"', page)}
    for name, key in WALLPAPERS.items():
        url = images.get(key)
        if not url:
            sys.exit(f'WhatsApp Web no longer lists wallpaper {key}.')
        cached = os.path.join(work, re.sub(r'[^A-Za-z0-9._-]+', '_', url.split('://', 1)[1])[:180])
        data = open(cached, 'rb').read() if os.path.exists(cached) else fetch(url)
        with open(os.path.join(ASSETS, 'WhatsApp', name), 'wb') as f:
            f.write(data)
    log('  wallpapers')


def build_fonts(page):
    from fontTools.ttLib import TTFont
    import io
    styles = [u.replace('\\/', '/') for u in re.findall(r'href="(https://static\.whatsapp\.net/rsrc\.php/[^"]+\.css)"', page)]
    css = ''.join(fetch(u).decode('utf-8', 'replace') for u in styles)
    os.makedirs(os.path.join(ASSETS, 'Fonts'), exist_ok=True)
    found = 0
    for style, url in re.findall(r'@font-face\{font-family:Roboto Variable;font-style:(\w+);[^}]*src:url\(([^)]+\.woff2)\)', css):
        font = TTFont(io.BytesIO(fetch('https://static.whatsapp.net' + url)))
        font.flavor = None
        font.save(os.path.join(ASSETS, 'Fonts', 'Roboto-Italic.ttf' if style == 'italic' else 'Roboto.ttf'))
        found += 1
    if found != 2:
        sys.exit('WhatsApp Web no longer uses Roboto Variable as it did.')
    log('  fonts')


# ---- Icons ----

ARGS = {'m': 2, 'l': 2, 'h': 1, 'v': 1, 'c': 6, 's': 4, 'q': 4, 't': 2, 'a': 7, 'z': 0}
NUMBER = re.compile(r'[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?')


def normalize_path(d):
    """Writes SVG path data with a space between all values, arc flags included, as XAML reads it."""
    out, i, command = [], 0, None
    while i < len(d):
        if d[i] in ' ,\t\n':
            i += 1
            continue
        if d[i].lower() in ARGS:
            command = d[i]
            out.append(command)
            i += 1
            if command.lower() == 'z':
                continue
        values = []
        for k in range(ARGS[command.lower()]):
            while i < len(d) and d[i] in ' ,\t\n':
                i += 1
            if command.lower() == 'a' and k in (3, 4):
                values.append(d[i])
                i += 1
                continue
            m = NUMBER.match(d, i)
            values.append(m.group(0))
            i = m.end()
        out.append(' '.join(values))
        if command in 'Mm':
            command = 'L' if command == 'M' else 'l'
    return ' '.join(out)


def build_icons(work, node):
    icons_file = os.path.join(work, 'icons.json')
    subprocess.run([node, os.path.join(HERE, 'icons.js'), work, icons_file], check=True)
    icons = json.load(open(icons_file, encoding='utf-8'))

    entries = []
    for name, module in ICONS.items():
        icon = icons.get(module)
        if icon is None:
            sys.exit(f'Icon {module} is no longer in WhatsApp Web.')
        view_box = icon['viewBox'] or f'0 0 {icon["w"]} {icon["h"]}'
        paths = [(normalize_path(e['d']), e.get('fillRule') == 'evenodd') for e in icon['elements'] if e['tag'] == 'path' and e.get('d')]
        entries.append((name, view_box, paths))

    # Two shapes that are not built with the icon helper: the tail on the first
    # bubble of a run, and the picture of someone without one.
    tails = {}
    default_user = None
    for path in glob.glob(os.path.join(work, '*')):
        text = open(path, encoding='utf-8', errors='replace').read()
        start = text.find('__d("WAWebWrapperTailIcon.react"')
        if start >= 0 and not tails:
            paths = re.findall(r'fill:"currentColor",d:"([^"]*)"', text[start:text.find('\n__d(', start + 5)])
            tails = {'TailIn': paths[0], 'TailOut': paths[1]}
        start = text.find('__d("WAWebDefaultUserColorIcon.react"')
        if start >= 0 and default_user is None:
            default_user = re.findall(r'd:"([^"]*)",className:"primary"', text[start:text.find('\n__d(', start + 5)])[0]
    if not tails or default_user is None:
        sys.exit('The bubble tails or the default profile picture are no longer in WhatsApp Web.')
    entries.append(('TailIn', '0 0 8 13', [(normalize_path(tails['TailIn']), False)]))
    entries.append(('TailOut', '0 0 8 13', [(normalize_path(tails['TailOut']), False)]))
    entries.append(('DefaultUser', '0 0 212 212', [(normalize_path(default_user), False)]))

    lines = [
        '// Generated by scripts/whatsapp-assets/build.py from WhatsApp Web. Do not edit.',
        '',
        'namespace WinWhatsApp.App.Controls;',
        '',
        'internal static partial class WaIcons',
        '{',
        '    private static readonly Dictionary<string, IconData> s_icons = new()',
        '    {',
    ]
    for name, view_box, paths in entries:
        parts = ', '.join(f'new("{d}", {"true" if even_odd else "false"})' for d, even_odd in paths)
        lines.append(f'        ["{name}"] = new("{view_box}", [{parts}]),')
    lines += ['    };', '}', '']
    with open(ICONS_CS, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    log(f'  {len(entries)} icons')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--profile', help='the Firefox profile folder name, when not the one with the largest cache')
    parser.add_argument('--keep', help='a folder to keep the extracted files in, for looking at them')
    args = parser.parse_args()

    node = shutil.which('node') or sys.exit('Node.js was not found.')
    work = args.keep or tempfile.mkdtemp(prefix='whatsapp-assets-')
    cache = find_cache(args.profile)
    log(f'Reading {cache}')
    log(f'  {extract_cache(cache, work)} files of WhatsApp Web')

    page = fetch('https://web.whatsapp.com/', page=True).decode('utf-8', 'replace')
    os.makedirs(os.path.join(ASSETS, 'WhatsApp'), exist_ok=True)
    build_emoji(work, node)
    build_icons(work, node)
    build_wallpapers(work, page)
    build_fonts(page)
    if not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    log('Done.')


if __name__ == '__main__':
    main()
