"""Writes the app's icon in the sizes a Linux desktop looks for.

    python3 icons.py <AppIcon.png> <.../share/icons/hicolor> <icon name>
"""
import os
import sys

from PIL import Image

source, theme, name = sys.argv[1:4]
icon = Image.open(source).convert('RGBA')
for size in (16, 24, 32, 48, 64, 128, 256):
    folder = os.path.join(theme, f'{size}x{size}', 'apps')
    os.makedirs(folder, exist_ok=True)
    icon.resize((size, size), Image.LANCZOS).save(os.path.join(folder, name + '.png'))
