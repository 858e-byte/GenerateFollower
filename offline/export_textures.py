"""Textures d'un export RaceMenu introuvables chez l'utilisateur (ni loose ni BSA), après work\\texture_remap.json.
Le slot de teinte (CharGen\\…) est ignoré : le build le réécrit. Lecture seule.

Usage : export_textures.py <export.nif>   ->  JSON {"introuvables": [{"forme": …, "texture": …}]}
"""
import sys, os, json
import assets
from pyn.pynifly import NifFile

remap_path = os.path.join(os.path.dirname(__file__), '..', 'work', 'texture_remap.json')
remap = {k.lower(): v for k, v in json.load(open(remap_path, encoding='utf-8')).items()} if os.path.exists(remap_path) else {}
missing = []
for s in NifFile(sys.argv[1]).shapes:
    for t in (s.textures.values() if s.shader else []):
        if not t or 'plugins\\chargen' in t.lower():
            continue
        # PyNifly préfixe « textures\ » ; le fichier de correspondance garde les chemins bruts du .nif
        raw = t[9:] if t.lower().startswith('textures\\') else t
        t = remap.get(raw.lower(), remap.get(t.lower(), t))
        rel = t if t.lower().startswith('textures\\') else 'textures\\' + t
        if not assets.find(rel):
            missing.append({'forme': s.name, 'texture': t})
print(json.dumps({'introuvables': missing}))
