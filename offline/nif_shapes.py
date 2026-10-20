"""Formes de meshes du jeu et leurs textures (pour les skins : ARMA de peau). Lecture seule.

Usage : nif_shapes.py <liste.json de chemins relatifs à Data>  ->  JSON {"meshes\\…": [{"name", "index", "diffuse",
        "normal", "subsurface", "specular"}] | null si introuvable}
« index » : rang de la forme dans le NIF, utilisé par les textures alternatives (MO2S/MO3S) des ArmorAddons.
"""
import sys, json
import assets
from pyn.pynifly import NifFile

out = {}
for rel in json.load(open(sys.argv[1], encoding='utf-8')):
    real = assets.mesh(rel)
    if not real:
        out[rel] = None
        continue
    shapes = []
    for i, s in enumerate(NifFile(real).shapes):
        t = s.textures if s.shader else {}
        shapes.append({'name': s.name, 'index': i, 'diffuse': t.get('Diffuse', ''), 'normal': t.get('Normal', ''),
                       'subsurface': t.get('SoftLighting', ''), 'specular': t.get('Specular', '')})
    out[rel] = shapes
print(json.dumps(out))
