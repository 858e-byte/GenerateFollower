"""Index des têtes exportées par RaceMenu dans CharGen (Export Head / Head Sculpts) : formes et sommets.

Usage : index_heads.py <dossier CharGen> <index.json> [autres dossiers CharGen…]
Les autres dossiers (MO2 lu directement : overwrite, mods activés, Data) sont de priorité plus faible : pour un même
chemin relatif, le premier dossier gagne.
Sortie : {"<chemin relatif à CharGen>": {"mtime": .., "shapes": {nom: nombre de sommets}, "dds": bool, "uniforme": bool}}
Mis à jour de façon incrémentale (seuls les .nif nouveaux ou modifiés sont relus).
"""
import sys, os, glob, json
import numpy as np
import assets  # noqa: F401  (chemin de PyNifly)
from pyn.pynifly import NifFile

roots, out = [sys.argv[1]] + sys.argv[3:], sys.argv[2]
index = json.load(open(out, encoding='utf-8')) if os.path.isfile(out) else {}
seen = set()
files = {}
for root in roots:
    for p in glob.glob(os.path.join(root, '**', '*.nif'), recursive=True):
        files.setdefault(os.path.relpath(p, root).lower(), (os.path.relpath(p, root), p))
for rel, p in files.values():
    if rel.lower().startswith('presets\\'):
        pass  # des auteurs rangent aussi des têtes à côté des presets
    seen.add(rel)
    mtime = os.path.getmtime(p)
    if rel in index and index[rel].get('mtime') == mtime:
        continue
    entry = {'mtime': mtime, 'shapes': {}, 'dds': False, 'uniforme': None}
    try:
        for s in NifFile(p).shapes:
            entry['shapes'][s.name] = len(s.verts)
    except Exception as e:
        entry['erreur'] = str(e)
    dds = os.path.splitext(p)[0] + '.dds'
    if os.path.isfile(dds) and os.path.getsize(dds) > 1024:
        entry['dds'] = True
        try:
            import tint
            a = tint.load_dds(dds, 64)[..., :3]
            entry['uniforme'] = bool(a.std() < 0.01)   # exports UBE : gris uni, inutilisables comme teinte
        except Exception:
            entry['uniforme'] = None
    index[rel] = entry
for rel in list(index):
    if rel not in seen:
        del index[rel]
json.dump(index, open(out, 'w', encoding='utf-8'), indent=1)
print(f'{len(index)} têtes indexées')
