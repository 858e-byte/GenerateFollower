"""Calcul hors jeu de la texture de teint FaceGen (FaceTint) depuis tintInfo du jslot.

Chaque couche : masque (DDS) × couleur ARGB du preset ; on empile les couches dans l'ordre du jslot.
Usage : tint.py <preset> [taille] [sortie.png]  -> compare à Data\\SKSE\\Plugins\\CharGen\\<preset>.dds
"""
import os, sys, json, subprocess, hashlib, glob
import numpy as np
from PIL import Image
import assets
import gfpaths

CACHE = os.path.join(os.path.dirname(__file__), 'png_cache')


def texconv():
    return gfpaths.texconv()


def load_dds(path, size):
    """DDS -> tableau float RGBA [0,1] à la taille demandée (converti une fois en PNG, mis en cache)."""
    os.makedirs(CACHE, exist_ok=True)
    key = hashlib.md5(f'{path}|{os.path.getmtime(path)}'.encode()).hexdigest()
    png = os.path.join(CACHE, key + '.png')
    if not os.path.exists(png):
        tmp = os.path.join(CACHE, key + '.dds')
        with open(path, 'rb') as a, open(tmp, 'wb') as b: b.write(a.read())
        subprocess.run([texconv(), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM', '-o', CACHE, tmp],
                       check=True, capture_output=True)
        os.remove(tmp)
        written = [p for p in glob.glob(os.path.join(CACHE, key + '.*')) if p.lower().endswith('.png')][0]
        if written != png: os.replace(written, png)
    img = Image.open(png).convert('RGBA')
    if img.size != (size, size): img = img.resize((size, size), Image.BILINEAR)
    return np.asarray(img, dtype=np.float64) / 255.0


def layers(j):
    for t in j['tintInfo']:
        c = t['color']
        a = ((c >> 24) & 255) / 255.0
        if a <= 0: continue
        rgb = np.array([(c >> 16) & 255, (c >> 8) & 255, c & 255]) / 255.0
        p = assets.find('textures\\' + t['texture'])
        if p: yield t, rgb, a, p


def compose(j, size, mask_channel='r', base=(128 / 255,) * 3):
    """Empile les couches sur un gris neutre (128) : visible quand la couche SkinTone n'est pas opaque."""
    out = np.ones((size, size, 3)) * np.array(base, float)
    for t, rgb, a, p in layers(j):
        m = load_dds(p, size)
        mask = {'a': m[..., 3], 'r': m[..., 0], 'ra': m[..., 0] * m[..., 3],
                'luma': m[..., :3].mean(2) * m[..., 3]}[mask_channel]
        w = (mask * a)[..., None]
        out = out * (1 - w) + rgb * w
    return out


def save_dds(rgb, path, fmt='BC7_UNORM'):
    """Écrit la teinte en DDS compressé (même format que le build : BC7, alpha opaque)."""
    os.makedirs(os.path.dirname(path) or '.', exist_ok=True)
    stem = os.path.splitext(os.path.basename(path))[0]
    png = os.path.join(CACHE, stem + '.png')
    rgba = np.concatenate([np.clip(rgb, 0, 1), np.ones(rgb.shape[:2] + (1,))], 2)
    Image.fromarray((rgba * 255 + 0.5).astype(np.uint8)).save(png)
    subprocess.run([texconv(), '-nologo', '-y', '-f', fmt, '-m', '1', '-o', os.path.dirname(path) or '.', png],
                   check=True, capture_output=True)
    os.remove(png)
    written = [p for p in glob.glob(os.path.join(os.path.dirname(path) or '.', stem + '.*')) if p.lower().endswith('.dds')][0]
    if written != path: os.replace(written, path)


if __name__ == '__main__':
    preset = sys.argv[1]
    size = int(sys.argv[2]) if len(sys.argv) > 2 else 1024
    j = json.load(open(assets.chargen(rf"Presets\{preset}.jslot"), encoding='utf-8'))
    ref = load_dds(assets.chargen(rf"{preset}.dds"), size)
    print(f"référence : RGB moyen {ref[..., :3].mean((0, 1)).round(3)}  alpha moyen {ref[..., 3].mean():.3f}")
    for t, rgb, a, p in layers(j):
        m = load_dds(p, size)
        print(f"  couche {t['index']:3} a={a:.2f} rgb={rgb.round(2)}  masque R moyen {m[..., 0].mean():.3f} A moyen {m[..., 3].mean():.3f}  {t['texture']}")
    for ch in ('r', 'a'):
        for base in ((0, 0, 0), (1, 1, 1), (0.5, 0.5, 0.5)):
            o = compose(j, size, ch, base)
            e = np.abs(o - ref[..., :3])
            print(f"masque={ch} base={base}: écart moyen {e.mean() * 255:6.2f}/255  p99 {np.percentile(e, 99) * 255:6.1f}")
    if len(sys.argv) > 3:
        Image.fromarray((np.clip(compose(j, size, 'r'), 0, 1) * 255).astype(np.uint8)).save(sys.argv[3])
