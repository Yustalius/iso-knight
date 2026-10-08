# Contact sheet "concept | Godot": one row per frame name, the concept's frame on the left, the viewer's on the right.
#   python -I tools/pairsheet.py out.jpg CONCEPT_DIR GODOT_DIR name1 name2 ... [--crop x0,y0,x1,y1] [--title text]
# Frames are looked up as <dir>/<name>.png; a missing one leaves its cell dark. Needs Pillow.
import sys, os
from PIL import Image, ImageDraw, ImageFont

args = sys.argv[1:]
crop = title = None
if '--crop' in args: i = args.index('--crop'); crop = tuple(int(v) for v in args[i + 1].split(',')); del args[i:i + 2]
if '--title' in args: i = args.index('--title'); title = args[i + 1]; del args[i:i + 2]
out, cdir, gdir, names = args[0], args[1], args[2], args[3:]

def load(d, n):
    p = os.path.join(d, n + '.png')
    if not os.path.exists(p): return None
    im = Image.open(p).convert('RGB')
    return im.crop(crop) if crop else im

pairs = [(n, load(cdir, n), load(gdir, n)) for n in names]
w = max(im.width for _, a, b in pairs for im in (a, b) if im)
h = max(im.height for _, a, b in pairs for im in (a, b) if im)
head, top = 26, 34 if title else 0
try: font = ImageFont.truetype('arial.ttf', 18); big = ImageFont.truetype('arial.ttf', 22)
except OSError: font = big = ImageFont.load_default()
S = Image.new('RGB', (2 * w + 6, top + len(pairs) * (h + head)), (18, 20, 16))
d = ImageDraw.Draw(S)
if title: d.text((8, 6), title, fill=(235, 230, 200), font=big)
for k, (n, a, b) in enumerate(pairs):
    y = top + k * (h + head)
    for j, (im, label) in enumerate(((a, 'концепт'), (b, 'Godot'))):
        x = j * (w + 6)
        d.text((x + 6, y + 3), f'{n} — {label}', fill=(230, 226, 200), font=font)
        if im: S.paste(im, (x, y + head))
S.save(out, quality=88)
print(out, S.size)
