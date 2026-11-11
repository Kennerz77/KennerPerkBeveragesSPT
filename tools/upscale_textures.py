r"""the original's perk machine colour maps are 512-1024 px: upscales them 2x (Lanczos, then a light unsharp mask, as the HUD
icons are) into upscaled\<file name> (beside the project), which PerkImporter prefers over the originals. Normal, spec and
gloss maps stay as they are. Run before build_bundle.ps1 when a machine is added or re-baked.
Usage: python -I upscale_textures.py"""
import glob
import os
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'upscaled')  # beside the project, not in UnityAssets: only the importer reads it
# The asset export folder on the author's PC: the KZ_EXPORT_ROOT user environment variable (kept out of the source)
GREYHOUND = os.path.join(os.environ.get('KZ_EXPORT_ROOT', ''), 'xmodels')
BAKED = os.path.join(HERE, '..', 'UnityAssets', 'Perks', 'Baked')

# The machines (PerkImporter.Perks): baked ones read their colour-baked maps
MACHINES = ['p7_zm_vending_jugg', 'p7_zm_vending_sleight', 'p7_zm_vending_doubletap2', 'p7_zm_vending_ads',
            'p7_zm_vending_marathon', 'p7_zm_vending_revive', 'p7_zm_vending_three_gun', 'p7_zm_vending_widows_wine', 'p7_zm_vending_nuke']
MAX = 2048

os.makedirs(OUT, exist_ok=True)
done = 0
for m in MACHINES:
    root = os.path.join(BAKED, m) if os.path.isdir(os.path.join(BAKED, m)) else os.path.join(GREYHOUND, m)
    for f in glob.glob(os.path.join(root, '_images', '*', '*_c.png')):
        name = os.path.basename(f)
        if name.startswith('$'):
            continue
        out = os.path.join(OUT, name)
        if os.path.exists(out) and os.path.getmtime(out) >= os.path.getmtime(f):
            continue
        src = Image.open(f)
        mode = 'RGBA' if src.mode in ('RGBA', 'LA', 'P') else 'RGB'
        src = src.convert(mode)
        if max(src.size) * 2 > MAX:
            continue
        big = src.resize((src.width * 2, src.height * 2), Image.LANCZOS)
        # Sharpen the colour only; any alpha stays smooth
        bands = big.split()
        rgb = Image.merge('RGB', bands[:3]).filter(ImageFilter.UnsharpMask(radius=2, percent=70, threshold=2))
        big = Image.merge(mode, (*rgb.split(), *bands[3:]))
        big.save(out)
        done += 1
        print(m, name, src.size, '->', big.size)
print(done, 'upscaled')
