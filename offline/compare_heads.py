"""Écart entre la forme de visage (shader FaceGen) de deux .nif, en unités de jeu, au format JSON.

Usage : compare_heads.py <tête.nif> <référence.nif>
Compare sommet par sommet quand les deux visages ont le même nombre de sommets (même maillage de base).
"""
import sys, json
import numpy as np
import assets  # noqa: F401  (chemin de PyNifly)
from pyn.pynifly import NifFile


def face(path):
    shapes = [s for s in NifFile(path).shapes if s.shader and s.shader.properties.Shader_Type == 4]
    s = max(shapes, key=lambda s: len(s.verts))
    return s.name, np.asarray(s.verts, dtype=np.float64)


a_name, a = face(sys.argv[1])
b_name, b = face(sys.argv[2])
res = {"tete": a_name, "reference": b_name, "sommets": [len(a), len(b)]}
if len(a) == len(b):
    d = np.linalg.norm(a - b, axis=1)
    res.update(moyen=round(float(d.mean()), 3), p95=round(float(np.percentile(d, 95)), 3), max=round(float(d.max()), 3))
else:
    res["erreur"] = "maillages différents (nombre de sommets), comparaison impossible"
print(json.dumps(res))
