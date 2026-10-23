"""Identifie l'association curseurs vanilla (jslot morphs.default) -> morphs chargen, sur plusieurs presets à la fois."""
import sys, json
import numpy as np
import os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gfpaths
gfpaths.add_pynifly()
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # notre tri.py avant le paquet tri de PyNifly
from pyn.pynifly import NifFile
from tri import Tri
import assets

reg = assets.morph_registry(); sl = assets.slider_registry()
src = r"kl\high poly head\femaleheadchargen.tri"
B = np.array(NifFile(assets.mesh(r"KL\High Poly Head\FemaleHead.nif")).shapes[0].verts)
cg = {k.lower(): v for k, v in Tri(assets.mesh(r"KL\High Poly Head\FemaleHeadCharGen.tri")).morphs.items()}
races = Tri(assets.mesh(r"KL\High Poly Head\FemaleHeadRaces.tri")).morphs
ext = {}
for p in reg[src]:
    for k, v in Tri(p).morphs.items(): ext.setdefault(k.lower(), v)
Z = np.zeros_like(B)
MORPHS = ['NoseShort', 'NoseLong', 'NoseDown', 'NoseUp', 'JawDown', 'JawUp', 'JawNarrow', 'JawWide', 'JawBack', 'JawForward',
          'CheeksDown', 'CheeksUp', 'CheeksIn', 'CheeksOut', 'EyesMoveDown', 'EyesMoveUp', 'EyesMoveIn', 'EyesMoveOut',
          'BrowDown', 'BrowUp', 'BrowIn', 'BrowOut', 'BrowBack', 'BrowForward', 'LipMoveDown', 'LipMoveUp', 'LipMoveIn',
          'LipMoveOut', 'ChinThin', 'ChinWide', 'ChinMoveUp', 'ChinMoveDown', 'Underbite', 'Overbite', 'EyesBack', 'EyesForward']
PAIRS = [(MORPHS[2 * i], MORPHS[2 * i + 1]) for i in range(18)]


def load(preset):
    nif = NifFile(assets.chargen(rf"{preset}.nif"))
    R = np.array([s for s in nif.shapes if len(s.verts) == len(B)][0].verts)
    j = json.load(open(assets.chargen(rf"Presets\{preset}.jslot"), encoding='utf-8'))
    hp_face = [h['formIdentifier'] for h in j['headParts']]
    race = 'RedguardRace' if 'High Poly Head.esm|000A05' in hp_face else 'NordRace'
    fixed = Z.copy()
    nama = j['morphs']['default']['presets']
    for kind, v in (('nosetype', nama[0]), ('eyestype', nama[2]), ('liptype', nama[3])):
        if 0 <= v < 1000: fixed = fixed + cg.get(f'{kind}{v}', Z)
    for c in j['morphs']['custom']:
        s = sl.get(c['name'].lower())
        if s:
            fixed = fixed + abs(c['value']) * ext.get((s[1] if c['value'] > 0 else s[0]).lower(), Z)
    return dict(name=preset, y=R - B, fixed=fixed, race=races.get(race, Z), nam9=j['morphs']['default']['morphs'][:18])


P = [load(p) for p in sys.argv[1:]]


def contrib(p, assign):
    d = Z.copy()
    for v, (lo, hi) in zip(p['nam9'], assign):
        if lo is None: continue
        d += abs(v) * (cg[hi.lower()] if v > 0 else cg[lo.lower()])
    return d


def total(assign, rw):
    return sum(np.abs(p['y'] - p['fixed'] - rw * p['race'] - contrib(p, assign)).mean() for p in P) / len(P)


assign = list(PAIRS); rw = 0.5
print('départ', round(total(assign, rw), 4))
choices = [(None, None)] + [(a, b) for a, b in PAIRS] + [(b, a) for a, b in PAIRS]
for it in range(3):
    for rwc in (0, 0.25, 0.5, 0.75, 1):
        if total(assign, rwc) < total(assign, rw): rw = rwc
    for i in range(18):
        best = min(choices, key=lambda c: total(assign[:i] + [c] + assign[i + 1:], rw))
        assign[i] = best
    print(f'itération {it}: race={rw} erreur moyenne {total(assign, rw):.4f}')
for i, (c, orig) in enumerate(zip(assign, PAIRS)):
    print(f'  slot {i:2}: {str(c):32} (hypothèse initiale {orig})')
expr = Tri(assets.mesh(r"KL\High Poly Head\FemaleHead.tri")).morphs
E = np.stack([v.ravel() for k, v in expr.items() if k != 'SkinnyMorph'], 1)
for p in P:
    r = p['y'] - p['fixed'] - rw * p['race'] - contrib(p, assign)
    w = np.linalg.lstsq(E, r.ravel(), rcond=None)[0]
    r2 = r.ravel() - E @ w
    top = sorted(zip([k for k in expr if k != 'SkinnyMorph'], w), key=lambda x: -abs(x[1]))[:5]
    print(f"  {p['name']:10} |ref-base| {np.abs(p['y']).mean():.4f}  erreur {np.abs(r).mean():.4f} max {np.abs(r).max():.4f}"
          f"  | + expressions libres -> {np.abs(r2).mean():.4f} max {np.abs(r2).max():.4f}  {[(k, round(x, 2)) for k, x in top]}")
