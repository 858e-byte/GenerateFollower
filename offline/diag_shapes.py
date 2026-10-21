r"""Noms des formes de chaque FaceGen d'un dossier, au format JsonUtil (build --diag).

Clé = FormID local en décimal (nom du fichier & 0xFFF), comme dans le script GF_DiagQuest.
Usage : diag_shapes.py <dossier FaceGeom\plugin> <sortie.json>
"""
import sys, os, glob, json
import assets  # noqa: F401  (chemin de PyNifly)
from pyn.pynifly import NifFile

folder, out = sys.argv[1], sys.argv[2]
shapes = {}
for p in glob.glob(os.path.join(folder, '*.nif')):
    key = str(int(os.path.splitext(os.path.basename(p))[0], 16) & 0xFFF)
    shapes[key] = [s.name for s in NifFile(p).shapes]
os.makedirs(os.path.dirname(out), exist_ok=True)
json.dump({'stringList': shapes}, open(out, 'w', encoding='utf-8'), indent=1)
