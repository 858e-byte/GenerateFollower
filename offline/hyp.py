"""Recherche des conventions RaceMenu : modèle direct (sans ajustement libre) comparé à une tête exportée."""
import sys, json, itertools
import numpy as np
import os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gfpaths
gfpaths.add_pynifly()
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # notre tri.py avant le paquet tri de PyNifly
from pyn.pynifly import NifFile
from tri import Tri
import assets

PRESET, SHAPE, RACE = sys.argv[1], sys.argv[2], sys.argv[3]
reg = assets.morph_registry(); sl = assets.slider_registry()
src = r"kl\high poly head\femaleheadchargen.tri"
B = np.array(NifFile(assets.mesh(r"KL\High Poly Head\FemaleHead.nif")).shapes[0].verts)
R = np.array([s for s in NifFile(assets.DATA + rf"\SKSE\Plugins\CharGen\{PRESET}.nif").shapes if s.name == SHAPE][0].verts)
j = json.load(open(assets.DATA + rf"\SKSE\Plugins\CharGen\Presets\{PRESET}.jslot", encoding='utf-8'))
cg = Tri(assets.mesh(r"KL\High Poly Head\FemaleHeadCharGen.tri")).morphs
races = Tri(assets.mesh(r"KL\High Poly Head\FemaleHeadRaces.tri")).morphs
ext = {}
for p in reg[src]:
    for k, v in Tri(p).morphs.items(): ext.setdefault(k.lower(), v)
cgl = {k.lower(): v for k, v in cg.items()}
Z = np.zeros_like(B)

PAIRS = [('NoseShort', 'NoseLong'), ('NoseDown', 'NoseUp'), ('JawDown', 'JawUp'), ('JawNarrow', 'JawWide'), ('JawBack', 'JawForward'),
         ('CheeksDown', 'CheeksUp'), ('CheeksIn', 'CheeksOut'), ('EyesMoveDown', 'EyesMoveUp'), ('EyesMoveIn', 'EyesMoveOut'),
         ('BrowDown', 'BrowUp'), ('BrowIn', 'BrowOut'), ('BrowBack', 'BrowForward'), ('LipMoveDown', 'LipMoveUp'),
         ('LipMoveIn', 'LipMoveOut'), ('ChinThin', 'ChinWide'), ('ChinMoveUp', 'ChinMoveDown'), ('Underbite', 'Overbite'), ('EyesBack', 'EyesForward')]
nam9 = j['morphs']['default']['morphs'][:18]
if '--minus1-zero' in sys.argv:
    nam9 = [0.0 if v == -1 else v for v in nam9]
nama = j['morphs']['default']['presets']


def m(name):
    return cgl.get(name.lower(), Z)


def part_race(w): return w * races.get(RACE, Z)


def part_nam9(flip, scale):
    d = Z.copy()
    for (a, b), v in zip(PAIRS, nam9):
        lo, hi = (b, a) if flip else (a, b)
        d += abs(v) * scale * (m(hi) if v > 0 else m(lo))
    return d


def part_nama(off):
    d = Z.copy()
    for kind, v in (('NoseType', nama[0]), ('EyesType', nama[2]), ('LipType', nama[3])):
        if 0 <= v < 1000: d += m(f'{kind}{v + off}')
    return d


def part_custom(scale, flip):
    d = Z.copy()
    for c in j['morphs']['custom']:
        s = sl.get(c['name'].lower())
        if not s: continue
        lo, hi = s if not flip else (s[1], s[0])
        tgt = hi if c['value'] > 0 else lo
        d += abs(c['value']) * scale * ext.get(tgt.lower(), Z)
    return d


def err(d): return np.abs(R - B - d).mean()


y = R - B
print(f'|ref-base| moyen {np.abs(y).mean():.4f}')
best = None
for rw, flip, s9, off, cs, cflip in itertools.product([0, 0.5, 1], [False, True], [0, 0.5, 1], [-1, 0, 1], [1], [False]):
    e = err(part_race(rw) + part_nam9(flip, s9) + part_nama(off) + part_custom(cs, cflip))
    if best is None or e < best[0]: best = (e, rw, flip, s9, off, cs)
    print(f'race={rw} flip9={flip!s:5} s9={s9:<3} nama+{off:<2} custom×{cs:<3} -> {e:.4f}')
print('MEILLEUR', best)

# Résidu du meilleur modèle : quels morphs isolés l'expliquent le mieux ?
_, rw, flip, s9, off, cs = best
res = (y - (part_race(rw) + part_nam9(flip, s9) + part_nama(off) + part_custom(cs, False))).ravel()
pool = {'cg:' + k: v for k, v in cg.items()}
pool.update({'race:' + k: v for k, v in races.items()})
pool.update({'ext:' + k: v for k, v in ext.items()})
pool.update({'expr:' + k: v for k, v in Tri(assets.mesh(r"KL\High Poly Head\FemaleHead.tri")).morphs.items()})
for step in range(8):
    scores = []
    for n, v in pool.items():
        a = v.ravel(); den = a @ a
        if den == 0: continue
        w = (a @ res) / den
        scores.append((np.abs(res - w * a).mean(), n, w))
    e, n, w = min(scores)
    res = res - w * pool[n].ravel()
    print(f'  +{n:40} poids {w:+.3f} -> résidu {e:.4f}')
