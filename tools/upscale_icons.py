r"""the original's flat perk HUD icons (The Giant's set, specialty_giant_*: one style for all, Deadshot included) only exist at 64x64: upscales them 4x (Lanczos, then a light unsharp mask) into icons\<id>.png
so they stay crisp at HUD size. Usage: python -I upscale_icons.py"""
import os
from PIL import Image, ImageFilter

IMAGES = r'E:\GrayWolf\exported_files\rb3\images'
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'icons')

# perk id -> the original icon
ICONS = {
    'jugg': 'specialty_giant_juggernaut_zombies.png',
    'speed': 'specialty_giant_fastreload_zombies.png',
    'dtap': 'specialty_giant_doubletap_zombies.png',
    'deadshot': 'specialty_giant_ads_zombies.png',
    'stamin': 'specialty_giant_marathon_zombies.png',
    'revive': 'specialty_giant_quickrevive_zombies.png',
    'mule': 'specialty_giant_three_guns_zombies.png',
    'widow': 'specialty_giant_widows_wine_zombies.png',
}

for perk, name in ICONS.items():
    src = Image.open(os.path.join(IMAGES, name)).convert('RGBA')
    big = src.resize((src.width * 4, src.height * 4), Image.LANCZOS)
    # Sharpen the colour only; the alpha edge stays smooth
    r, g, b, a = big.split()
    rgb = Image.merge('RGB', (r, g, b)).filter(ImageFilter.UnsharpMask(radius=2, percent=80, threshold=2))
    out = Image.merge('RGBA', (*rgb.split(), a))
    out.save(os.path.join(OUT, perk + '.png'))
    print(perk, src.size, '->', out.size)
