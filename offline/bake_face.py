"""Overlays RaceMenu du visage cuits dans une diffuse propre au follower (têtes non UBE : en overlay par script, ils
faussent la couleur du visage en jeu, voir ETAT_DU_PROJET.md).

Usage : bake_face.py <tâche.json>
  {"base": "textures\\...\\femalehead.dds", "out": "<chemin .dds>",
   "layers": [{"tex": "textures\\...\\x.dds", "color": <RGB entier>, "alpha": <0..1>}, ...]}   (ordre Ovl0, Ovl1…)
Chaque couche : couleur = texture × teinte (clé NiOverride 7), opacité = alpha de la texture × alpha (clé 8),
mélangée par-dessus la diffuse comme un overlay. L'alpha de la diffuse d'origine est conservé.
Sortie : DDS BC7 avec mipmaps, à la taille de la diffuse d'origine. Affiche un résumé JSON.
"""
import os, sys, json, struct, subprocess, glob
import numpy as np
from PIL import Image
import assets, tint


def dds_size(path):
    with open(path, 'rb') as f:
        h = f.read(20)
    return struct.unpack_from('<I', h, 16)[0] or 1024   # largeur


job = json.load(open(sys.argv[1], encoding='utf-8'))
# chemin absolu accepté : diffuse d'un skin déjà copiée dans le mod (skin choisi dans le Builder)
base_path = job['base'] if os.path.isabs(job['base']) and os.path.isfile(job['base']) else assets.find(job['base'])
if not base_path:
    sys.exit(f"diffuse introuvable : {job['base']}")
size = min(dds_size(base_path), 4096)
out = tint.load_dds(base_path, size)              # RGBA [0,1]
rgb, alpha = out[..., :3].copy(), out[..., 3:].copy()
used, missing = [], []
for layer in job['layers']:
    p = assets.find(layer['tex'])
    if not p:
        missing.append(layer['tex']); continue
    t = tint.load_dds(p, size)
    c = layer.get('color')
    col = np.ones(3) if c is None else np.array([(c >> 16) & 255, (c >> 8) & 255, c & 255]) / 255.0
    w = (t[..., 3] * float(layer.get('alpha', 1.0)))[..., None]
    rgb = rgb * (1 - w) + (t[..., :3] * col) * w
    used.append(os.path.basename(p))

if job.get('red'):
    # diagnostic (GF_BAKE_RED) : visage rouge vif, pour voir en jeu si cette diffuse est bien celle qui s'affiche
    rgb[...] = (1.0, 0.0, 0.0)
os.makedirs(os.path.dirname(job['out']), exist_ok=True)
stem = os.path.splitext(os.path.basename(job['out']))[0]
png = os.path.join(tint.CACHE, stem + '_bake.png')
Image.fromarray((np.concatenate([np.clip(rgb, 0, 1), alpha], 2) * 255 + 0.5).astype(np.uint8)).save(png)
subprocess.run([tint.texconv(), '-nologo', '-y', '-f', 'BC7_UNORM', '-m', '0', '-o', tint.CACHE, png],
               check=True, capture_output=True)
os.remove(png)
written = [p for p in glob.glob(os.path.join(tint.CACHE, stem + '_bake.*')) if p.lower().endswith('.dds')][0]
os.replace(written, job['out'])
print(json.dumps({'taille': size, 'couches': used, 'introuvables': missing}))
