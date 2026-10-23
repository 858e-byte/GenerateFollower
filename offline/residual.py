"""Diagnostic : quels morphs isolés expliquent le résidu d'un headpart ?"""
import sys, json
import numpy as np
import headgen as hg
from tri import Tri
import assets
from pyn.pynifly import NifFile

preset, race, hp_json, edid = sys.argv[1:5]
ctx = hg.Ctx()
j = json.load(open(assets.chargen(rf"Presets\{preset}.jslot"), encoding='utf-8'))
hp = [h for h in json.load(open(hp_json, encoding='utf-8-sig'))['headParts'] if h['edid'] == edid][0]
R = np.array([s for s in NifFile(assets.chargen(rf"{preset}.nif")).shapes if s.name == edid][0].verts)
base, out, info = hg.morph_part(ctx, hp, j, race, {})
res = (R - out).ravel()
pool = {}
for kind in ('ChargenMorph', 'Tri', 'RaceMorph'):
    p = hg.part_file(hp, kind)
    if p:
        for k, v in Tri(p).morphs.items(): pool[f'{kind}:{k}'] = v
for p in ctx.morph_reg.get(hg.rel_mesh(hg.part_file(hp, 'ChargenMorph')), []):
    for k, v in Tri(p).morphs.items(): pool[p.split('\\')[-1] + ':' + k] = v
err = np.abs(res).reshape(-1, 3).max(1)
print(f'résidu moyen {np.abs(res).mean():.4f}; sommets > 0.05 : {(err > 0.05).sum()} / {len(err)}')
for step in range(6):
    best = None
    for n, v in pool.items():
        a = v.ravel(); den = a @ a
        if den == 0: continue
        w = (a @ res) / den
        e = np.abs(res - w * a).mean()
        if best is None or e < best[0]: best = (e, n, w)
    e, n, w = best
    res = res - w * pool[n].ravel()
    print(f'  +{n:55} poids {w:+.3f} -> résidu {e:.4f}')
used = {c['name']: c['value'] for c in j['morphs'].get('custom') or []}
print('curseurs du preset :', used)
print('vanilla :', [round(x, 2) for x in j['morphs']['default']['morphs'][:18]], j['morphs']['default']['presets'])
