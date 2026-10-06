"""Takes WhatsApp Web's calling engine for WinWhatsApp.

WhatsApp Web places and answers calls with a WebAssembly build of WhatsApp's
native calling library, started by a script that Emscripten generated for
it. Script and binary belong together: the script has the binary's SHA-256
in it. This finds a binary in the Firefox cache together with the script
made for it, and writes

    src/WinWhatsApp.App/Assets/Voip/wa-voip-glue.js   the script, as WhatsApp Web has it
    src/WinWhatsApp.App/Assets/Voip/manifest.json     where to download the binary, and its hash

The binary itself is not copied. The app downloads it from WhatsApp's servers
the first time it is needed and checks it against the hash.

Run it after making or taking a call in WhatsApp Web in Firefox, so that the
cache has the calling engine:

    pip install zstandard brotli
    python scripts/whatsapp-voip/build.py
"""
import argparse
import hashlib
import json
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'whatsapp-assets'))
from build import REPO, decode_body, find_cache, log, read_entry  # noqa: E402

OUT = os.path.join(REPO, 'src', 'WinWhatsApp.App', 'Assets', 'Voip')
GLUE_MODULE = 'WAWebVoipWebWasmLoader_ContentAddressed_internal'
WASM_URL = re.compile(r'^https://static\.whatsapp\.net/rsrc\.php/.+\.wasm$')
SCRIPT_URL = re.compile(r'^https://static\.whatsapp\.net/rsrc\.php/.+\.js')


def cached_files(cache):
    """Yields the URL and content of WhatsApp Web's scripts and WebAssembly binaries in the cache."""
    for name in os.listdir(cache):
        try:
            entry = read_entry(os.path.join(cache, name))
        except (OSError, struct.error):
            continue
        if not entry:
            continue
        url, body, head = entry
        if not (WASM_URL.match(url) or SCRIPT_URL.match(url)):
            continue
        try:
            yield url, decode_body(body, head)
        except Exception as e:
            log(f'  skipped {url}: {e}')


def glue_modules(script):
    """The copies of the glue module in a script, with the SHA-256 of the binary each was made for."""
    text = script.decode('utf-8', 'replace')
    start = 0
    while (start := text.find(f'__d("{GLUE_MODULE}",', start)) >= 0:
        end = text.find('__d("', start + 5)
        module = text[start:end if end >= 0 else len(text)]
        # Scripts end each module with a comment that marks the next part of the package.
        module = module.split('/*FB_PKG_DELIM*/')[0].rstrip()
        sha = re.search(r'="([0-9a-f]{64})",', module)
        if sha:
            yield sha.group(1), module
        start += 5


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--profile', help='the Firefox profile folder name, when not the one with the largest cache')
    args = parser.parse_args()

    cache = find_cache(args.profile)
    log(f'Reading {cache}')
    binaries, glues = {}, {}
    for url, content in cached_files(cache):
        if url.endswith('.wasm'):
            binaries[hashlib.sha256(content).hexdigest()] = (url, len(content))
        else:
            for sha, module in glue_modules(content):
                glues[sha] = module
    log(f'  {len(binaries)} WebAssembly binaries, {len(glues)} versions of the calling engine\'s script')

    matches = [sha for sha in glues if sha in binaries]
    if not matches:
        sys.exit('The cache has no calling engine whose script and binary match. Make or take a call in '
                 'WhatsApp Web in Firefox, then run this again.')
    if len(matches) > 1:
        # Keep the one built last; its script has the larger resource numbers.
        matches.sort(key=lambda sha: max(int(n) for n in re.findall(r'bx"\)\("(\d+)"\)', glues[sha])))
    sha = matches[-1]
    url, size = binaries[sha]

    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, 'wa-voip-glue.js'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(glues[sha] + '\n')
    with open(os.path.join(OUT, 'manifest.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump({'wasm': {'url': url, 'sha256': sha, 'size': size}}, f, indent=2)
        f.write('\n')
    log(f'Calling engine {sha[:12]}, {size / 1e6:.1f} MB at {url}')
    log('Done.')


if __name__ == '__main__':
    main()
