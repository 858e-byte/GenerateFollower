"""FaceGen à partir de l'export RaceMenu d'un preset, comme dans les tutoriels (renommage des formes) :
le .nif exporté est repris tel quel, ses formes sont renommées vers les EditorID de nos headparts (GF_… pour
les headparts recréés) et le chemin de teinte de la tête est réécrit.

Usage : export_facegen.py <fiche headparts.json> <sortie.nif> <chemin de teinte dans le nif>
La fiche doit contenir "import" (chemin du .nif exporté).
"""
import sys, json
import assets  # noqa: F401  (chemin de PyNifly)
from pyn import pynifly
from pyn.pynifly import NifFile

HEAD = 'NPC Head [Head]'
PHYS = 'hdtsssephysics_autorename'[:0] + 'hdtssephysics_autorename'   # os de physique renommés par RaceMenu à l'export
fiche_path, out, tint_path = sys.argv[1:4]
fiche = json.load(open(fiche_path, encoding='utf-8-sig'))
rename = {(h.get('srcEdid') or h['edid']).lower(): h['edid'] for h in fiche['headParts']}
f = NifFile(fiche['import'])
f.filepath = out
done = []
for s in f.shapes:
    new = rename.get(s.name.lower())
    if new and new != s.name:
        done.append(f'{s.name} -> {new}')
        s.name = new
    if s.shader and s.shader.properties.Shader_Type == 4 and 'head' in s.name.lower():
        pynifly.nifly.setShaderTextureSlot(f._handle, s._handle, 6, tint_path.encode('utf-8'))
    # Cheveux SMP : leurs sommets dépendent d'os de physique (hdtSSEPhysics_AutoRename_…) absents du
    # squelette d'un PNJ, d'où des mèches étirées en jeu (test 8). On reporte ces poids sur NPC Head, en place,
    # sans reconstruire le fichier (le reste de l'export, accepté par le jeu, n'est pas touché).
    weights = s.bone_weights
    phys = [b for b in weights if b.lower().startswith(PHYS) and any(w > 0 for _, w in weights[b])]
    if phys and HEAD in weights:
        acc = dict(weights[HEAD])
        for b in phys:
            for v, w in weights[b]: acc[v] = acc.get(v, 0) + w
            s.setShapeWeights(b, [(v, 0.0) for v, _ in weights[b]])
        s.setShapeWeights(HEAD, sorted(acc.items()))
        done.append(f'{s.name} : {len(phys)} os de physique -> {HEAD}')
f.save()
print(f'{len(done)} modifications')
for d in done: print('  ' + d)
