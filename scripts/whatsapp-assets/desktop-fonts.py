"""Makes the fonts of the macOS and Linux app from what build.py took.

The Windows app draws emoji as pictures inside the text and takes the weights
of its text from one variable font. The text of the macOS and Linux app can
do neither, so it gets:

    WhatsAppEmoji.ttf        WhatsApp's emoji as a colour font
    Roboto-<weight>.ttf      the text font, one file for each weight the app uses
    Roboto.ttf.manifest      which file is which weight, for Uno

The emoji font holds every emoji of the sprite sheets as a bitmap glyph (sbix,
which Core Text and FreeType both draw). Emoji made of several code points,
such as flags and skin tones, are ligatures.

Reads these and writes to the folder given, which the build puts into the app:

    src/TwoTicks.App/Assets/WhatsApp/Emoji/<n>.webp   the emoji sprite sheets
    src/TwoTicks.App/Assets/WhatsApp/emoji.json        which emoji is in which cell
    src/TwoTicks.App/Assets/Fonts/Roboto*.ttf          the text font

The project of the macOS and Linux app runs it when one of these changed:

    pip install fonttools pillow
    python scripts/whatsapp-assets/desktop-fonts.py --output artifacts/fonts
"""
import argparse
import io
import json
import math
import os

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont, newTable
from fontTools.ttLib.tables.sbixGlyph import Glyph as SbixGlyph
from fontTools.ttLib.tables.sbixStrike import Strike
from fontTools.varLib import instancer
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
ASSETS = os.path.join(REPO, 'src', 'TwoTicks.App', 'Assets')

# The sheets, as EmojiSet.CellOf in Core reads them.
CELL = 40
PER_SHEET = 25
COLUMNS = 5

# An emoji is a third taller than the letters around it and reaches a fifth of
# its height below the line, as in the Windows app (Windows/Emoji.cs).
UPM = 1000
SCALE = 1.33
PPEM = round(CELL / SCALE)
BELOW = round(CELL * 0.2)
SIDE = 2

VARIATION_SELECTOR = 0xFE0F

# The app writes an emoji as one private code point, this plus its number in
# the list: its text engine shapes the parts of a joined emoji one by one, so
# the ligatures alone would leave families and keycaps in pieces.
PRIVATE_USE = 0xF0000

# The weights the app's XAML asks for, by the names Uno's manifest and the files use.
WEIGHTS = {'Light': 300, 'Regular': 400, 'Medium': 500, 'SemiBold': 600, 'Bold': 700}


def cell_of(index, count):
    position = index % PER_SHEET
    last_sheet_start = count - count % PER_SHEET
    columns = COLUMNS
    if index >= last_sheet_start:
        columns = math.floor(math.sqrt(count - last_sheet_start))
    return index // PER_SHEET, position % columns * CELL, position // columns * CELL


def pictures(count):
    """Each emoji as a PNG of its own, cut from its sheet."""
    sheets = {}
    for index in range(count):
        sheet, x, y = cell_of(index, count)
        if sheet not in sheets:
            sheets[sheet] = Image.open(os.path.join(ASSETS, 'WhatsApp', 'Emoji', f'{sheet}.webp')).convert('RGBA')
        data = io.BytesIO()
        sheets[sheet].crop((x, y, x + CELL, y + CELL)).save(data, 'PNG', optimize=True)
        yield data.getvalue()


def units(pixels):
    return round(pixels * UPM / PPEM)


def box_glyph(left, bottom, right, top):
    """
    Nothing to draw, but with the bounds of the picture: a line from one
    corner to the other, which has no inside to fill. The picture of a glyph
    goes where the lower left corner of its outline is, and macOS draws
    nothing at all for a glyph without bounds.
    """
    pen = TTGlyphPen(None)
    pen.moveTo((left, bottom))
    pen.lineTo((right, top))
    pen.closePath()
    return pen.glyph()


def build_emoji(output):
    with open(os.path.join(ASSETS, 'WhatsApp', 'emoji.json'), encoding='utf-8') as f:
        data = json.load(f)
    texts = {}
    for index, variants in enumerate(data['glyphs']):
        for text in variants:
            texts.setdefault(text, index)
    for text, index in data['legacy'].items():
        texts.setdefault(text, index)
    count = len(data['glyphs'])

    order = ['.notdef'] + [f'e{index}' for index in range(count)]
    cmap = {}
    ligatures = {}
    for text, index in texts.items():
        # Written with or without the selector that asks for the colourful form: the same emoji.
        points = [ord(c) for c in text if ord(c) != VARIATION_SELECTOR]
        if len(points) == 1:
            cmap.setdefault(points[0], f'e{index}')
        elif points:
            ligatures.setdefault(tuple(points), index)
    for index in range(count):
        cmap[PRIVATE_USE + index] = f'e{index}'

    # The code points emoji are made of that are not emoji themselves: joiners,
    # tags, the digits of keycaps. They take no room and show nothing.
    for point in sorted({p for sequence in ligatures for p in sequence} | {VARIATION_SELECTOR}):
        if point not in cmap:
            name = f'u{point:04X}'
            cmap[point] = name
            order.append(name)

    width = units(CELL + 2 * SIDE)
    # The bottom a touch short of its place: renderers round it down to whole pixels.
    emoji_glyph = box_glyph(units(SIDE), -math.floor(BELOW * UPM / PPEM), units(SIDE + CELL), units(CELL - BELOW))
    empty = TTGlyphPen(None).glyph()
    glyphs = {name: emoji_glyph if name.startswith('e') else empty for name in order}
    metrics = {name: (width, units(SIDE)) if name.startswith('e') else (0, 0) for name in order}

    builder = FontBuilder(UPM, isTTF=True)
    builder.setupGlyphOrder(order)
    builder.setupCharacterMap(cmap)
    builder.setupGlyf(glyphs)
    builder.setupHorizontalMetrics(metrics)
    ascent, descent = units(CELL - BELOW), units(BELOW)
    builder.setupHorizontalHeader(ascent=ascent, descent=-descent)
    builder.setupNameTable({'familyName': 'WhatsApp Emoji', 'styleName': 'Regular'})
    builder.setupOS2(sTypoAscender=ascent, sTypoDescender=-descent, sTypoLineGap=0, usWinAscent=ascent, usWinDescent=descent)
    builder.setupPost()

    # Longer sequences first: a family before the couple it starts with.
    rules = '\n'.join(
        f'  sub {" ".join(cmap[p] for p in sequence)} by e{index};'
        for sequence, index in sorted(ligatures.items(), key=lambda item: -len(item[0])))
    builder.addOpenTypeFeatures(f'languagesystem DFLT dflt;\nfeature ccmp {{\n{rules}\n}} ccmp;\n')

    strike = Strike(ppem=PPEM, resolution=72)
    for index, png in enumerate(pictures(count)):
        name = f'e{index}'
        # No offsets of its own: the outline's corner already is where the picture belongs.
        strike.glyphs[name] = SbixGlyph(glyphName=name, graphicType='png ', originOffsetX=0, originOffsetY=0, imageData=png)
    for name in order:
        if name not in strike.glyphs:
            strike.glyphs[name] = SbixGlyph(glyphName=name)
    sbix = newTable('sbix')
    sbix.version = 1
    sbix.flags = 1
    sbix.strikes = {PPEM: strike}
    builder.font['sbix'] = sbix

    builder.save(output)
    print(f'{os.path.basename(output)}: {count} emoji, {len(ligatures)} sequences, {os.path.getsize(output) / 1e6:.1f} MB')


def build_text(folder):
    """One file of the variable text font for each weight, upright and italic, and the list of them."""
    fonts = []
    for style, source in (('normal', 'Roboto.ttf'), ('italic', 'Roboto-Italic.ttf')):
        for name, weight in WEIGHTS.items():
            file = f'Roboto-{name}{"Italic" if style == "italic" else ""}.ttf'
            font = TTFont(os.path.join(ASSETS, 'Fonts', source))
            instancer.instantiateVariableFont(font, {'wght': weight}, inplace=True)
            font.save(os.path.join(folder, file))
            fonts.append({
                'family_name': f'ms-appx:///Assets/Fonts/{file}',
                'font_weight': weight,
                'font_style': style,
                'font_stretch': 'normal',
            })
    # Uno looks for the manifest next to the font the XAML names.
    with open(os.path.join(folder, 'Roboto.ttf.manifest'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump({'fonts': fonts}, f, indent=2)
        f.write('\n')
    print(f'Roboto: {len(fonts)} files')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--output', default=os.path.join(REPO, 'artifacts', 'fonts'), help='the folder to write the fonts to')
    folder = os.path.abspath(parser.parse_args().output)
    os.makedirs(folder, exist_ok=True)
    build_text(folder)
    # Last: the build takes this file as the sign that all of them are made.
    build_emoji(os.path.join(folder, 'WhatsAppEmoji.ttf'))
