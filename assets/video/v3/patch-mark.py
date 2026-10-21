# Paint the new SchemaScope mark over the old lightning bolt in the app header of the captured screenshots.
# The shots were taken before the rebrand; the app now draws LogoMark (web/src/components/ui.tsx, 18 CSS px, gap 8)
# where the 17 px Zap icon was. A modal backdrop dims the header, so each shot's dimming is fitted per channel
# (out = m*in + b) against the undimmed header of shot 01 and applied to the painted mark.
#   python patch-mark.py            -> writes shots-mark/<v2|v3>/<name>.png for every shot scene.html uses
from pathlib import Path
import re
import numpy as np
from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
SRC = {'v2': HERE.parent / 'v2' / 'shots', 'v3': HERE / 'shots'}
OUT = HERE / 'shots-mark'
REF = SRC['v2'] / '01-empty-compare.png'
HDR = (0, 0, 300, 76)          # raw px region used to fit the dimming (logo + wordmark + white bar)
CLEAR = (28, 24, 72, 68)       # raw px box cleared (the old bolt sits in x36-61, y31-62)
MARK = (32, 28, 36)            # raw px: left, top, size (18 CSS px; right edge 68 keeps the wordmark at x84)

def mark_rgba(size, ss=8):
    n = size * ss; k = n / 64
    im = Image.new('RGBA', (n, n), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    R = lambda x, y, w, h, r, c: d.rounded_rectangle((x * k, y * k, (x + w) * k, (y + h) * k), radius=r * k, fill=c)
    R(2, 2, 60, 60, 14, '#2a78d6'); R(14, 17, 36, 7, 3.5, '#ffffff'); R(14, 28.5, 24, 7, 3.5, '#ffd60a'); R(14, 40, 36, 7, 3.5, '#ffffff')
    return im.resize((size, size), Image.LANCZOS)

def patched_ref():
    im = Image.open(REF).convert('RGB')
    ImageDraw.Draw(im).rectangle((CLEAR[0], CLEAR[1], CLEAR[2] - 1, CLEAR[3] - 1), fill=(255, 255, 255))
    im.paste(mark_rgba(MARK[2]), (MARK[0], MARK[1]), mark_rgba(MARK[2]))
    return im

def fit(ref, cur):
    a = np.asarray(ref, float).reshape(-1, 3); b = np.asarray(cur, float).reshape(-1, 3)
    out = []
    for c in range(3):
        A = np.c_[a[:, c], np.ones(len(a))]; (m, q), *_ = np.linalg.lstsq(A, b[:, c], rcond=None)
        out.append((m, q, np.abs(A @ (m, q) - b[:, c]).max()))
    return out

def patch(path, ref_old, ref_new):
    im = Image.open(path).convert('RGB')
    maps = fit(ref_old.crop(HDR), im.crop(HDR))
    worst = max(e for *_, e in maps)
    if worst > 12: raise SystemExit(f'{path.name}: header does not match the reference (max err {worst:.1f})')
    box = ref_new.crop(CLEAR); arr = np.asarray(box, float)
    arr = np.stack([arr[..., c] * maps[c][0] + maps[c][1] for c in range(3)], -1)
    im.paste(Image.fromarray(np.clip(arr.round(), 0, 255).astype('uint8')), CLEAR[:2])
    return im, worst

if __name__ == '__main__':
    ref_old = Image.open(REF).convert('RGB'); ref_new = patched_ref()
    scene = (HERE / 'scene.html').read_text(encoding='utf-8')
    used = sorted(set(re.findall(r"(V[23])\+'([^']+\.png)'", scene)))
    for v, name in used:
        key = 'v2' if v == 'V2' else 'v3'
        dst = OUT / key / name; dst.parent.mkdir(parents=True, exist_ok=True)
        im, err = patch(SRC[key] / name, ref_old, ref_new)
        im.save(dst, optimize=False, compress_level=6)
        print(f'{key}/{name:42} fit err {err:4.1f}')
