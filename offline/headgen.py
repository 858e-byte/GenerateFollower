"""Reconstruction hors jeu de la géométrie d'une tête RaceMenu (prototype).

Pour chaque headpart : maillage de base (.nif) + morph de race (RaceMorph tri) + curseurs vanilla
(morphs.default, ChargenMorph tri) + presets nez/yeux/lèvres + curseurs RaceMenu (sliders\\*.ini ->
morphs des tri déclarés dans morphs.ini) + sculpt (deltas stockés dans le jslot).
"""
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

# Ordre des 18 curseurs vanilla de morphs.default : (morph si valeur < 0, morph si valeur > 0).
# Slot 2 (mâchoire) inversé : vérifié sur l'export d'Anna (erreur 0.0035 -> 0.0003).
PAIRS = [('NoseShort', 'NoseLong'), ('NoseDown', 'NoseUp'), ('JawUp', 'JawDown'), ('JawNarrow', 'JawWide'),
         ('JawBack', 'JawForward'), ('CheeksDown', 'CheeksUp'), ('CheeksIn', 'CheeksOut'), ('EyesMoveDown', 'EyesMoveUp'),
         ('EyesMoveIn', 'EyesMoveOut'), ('BrowDown', 'BrowUp'), ('BrowIn', 'BrowOut'), ('BrowBack', 'BrowForward'),
         ('LipMoveDown', 'LipMoveUp'), ('LipMoveIn', 'LipMoveOut'), ('ChinThin', 'ChinWide'), ('ChinMoveUp', 'ChinMoveDown'),
         ('Overbite', 'Underbite'), ('EyesBack', 'EyesForward')]   # slot 16 inversé (4 presets UBE)

_tri_cache = {}


class _NoTri:
    """Fichier .tri illisible (ex. format PIRT de certains cheveux) : aucun morph, ignoré par les appelants."""
    nverts = -1
    morphs = {}


def tri(path):
    if path not in _tri_cache:
        try:
            _tri_cache[path] = Tri(path)
        except ValueError:
            _tri_cache[path] = _NoTri()
    return _tri_cache[path]


class Ctx:
    def __init__(self):
        self.morph_reg = assets.morph_registry()
        self._sliders = {}

    def sliders(self, race):
        """Curseurs RaceMenu disponibles pour la race (races.ini de chaque mod)."""
        if race not in self._sliders: self._sliders[race] = assets.slider_registry(race)
        return self._sliders[race]


def part_file(hp, kind):
    for p in hp['parts']:
        if p['type'] == kind and p['file']: return assets.mesh(p['file'])
    return None


def rel_mesh(path):
    """Chemin réel -> chemin relatif à Meshes, en minuscules (clé de morphs.ini et des hôtes sculpt)."""
    low = path.lower()
    return low[low.index('meshes\\') + 7:] if 'meshes\\' in low else low


def morph_part(ctx, hp, j, race, opts):
    """Renvoie (verts de base, verts morphés, détail) pour un headpart, ou None s'il n'a pas de modèle."""
    model = assets.mesh(hp['model']) if hp.get('model') else None
    if not model: return None
    base = np.array(NifFile(model).shapes[0].verts)
    out = base.copy()
    info = []
    cg_path = part_file(hp, 'ChargenMorph')
    cg = {k.lower(): v for k, v in tri(cg_path).morphs.items()} if cg_path else {}
    if cg_path and tri(cg_path).nverts != len(base):
        return base, base, [f'chargen {tri(cg_path).nverts} sommets != nif {len(base)} : ignoré']

    rc_path = part_file(hp, 'RaceMorph')
    if rc_path and opts.get('race_w', 1):
        rms = tri(rc_path).morphs
        rm = rms.get(race)
        if rm is None: rm = rms.get(race.replace('00UBE_', ''))
        # morph de race d'un autre maillage (race Erin : tri de 7936 sommets pour une pièce de 1986) : ignoré
        if rm is not None and len(rm) != len(base): info.append(f'race {race} : {len(rm)} sommets != nif {len(base)} : ignoré')
        elif rm is not None: out += opts.get('race_w', 1) * rm; info.append(f'race {race}')

    # Poids du personnage : SkinnyMorph (tri d'expressions) appliqué à (1 - poids/100)
    tri_path = part_file(hp, 'Tri')
    if tri_path and opts.get('weight', True) and tri(tri_path).nverts == len(base):
        sk = {k.lower(): v for k, v in tri(tri_path).morphs.items()}.get('skinnymorph')
        wt = float(j['actor'].get('weight', 100))
        if sk is not None and wt < 100: out += (1 - wt / 100) * sk; info.append(f'poids {wt:g}')

    d = j['morphs']['default']
    for v, (lo, hi) in zip(d['morphs'][:18], opts.get('pairs', PAIRS)):
        if v and abs(v) < 1e3:
            m = cg.get((hi if v > 0 else lo).lower())
            if m is not None: out += abs(v) * opts.get('s9', 1) * m
    nama = d['presets']
    for kind, v in (('nosetype', nama[0]), ('eyestype', nama[2]), ('liptype', nama[3])):
        if 0 <= v < 1000:
            m = cg.get(f'{kind}{v + opts.get("nama_off", 0)}')
            if m is not None: out += m

    ext = {}
    if cg_path:
        for p in ctx.morph_reg.get(rel_mesh(cg_path), []):
            t = tri(p)
            if t.nverts == len(base):
                for k, v in t.morphs.items(): ext.setdefault(k.lower(), v)
    used = 0
    for c in j['morphs'].get('custom') or []:
        s = ctx.sliders(race).get(c['name'].lower())
        if not s: continue
        m = ext.get((s[1] if c['value'] > 0 else s[0]).lower())
        if m is None: m = cg.get((s[1] if c['value'] > 0 else s[0]).lower())
        if m is not None: out += abs(c['value']) * opts.get('cs', 1) * m; used += 1
    info.append(f'{used} curseurs RaceMenu')

    for s in j['morphs'].get('sculpt') or []:
        host = s['host'].lower().replace('/', '\\')
        if cg_path and rel_mesh(cg_path) == host and s.get('data'):   # entrée sans 'data' = sculpt vide
            data = json.loads(s['data']) if isinstance(s['data'], str) else s['data']
            div = j['morphs'].get('sculptDivisor') or 10000
            arr = np.array(data, dtype=np.float64).reshape(-1, 4)
            idx = arr[:, 0].astype(int)
            if len(idx) and idx.max() >= len(out):
                info.append(f'sculpt ignoré (fait pour {s.get("vertices")} sommets, maillage {len(out)})')
                continue
            out[idx] += arr[:, 1:4] / div
            info.append(f'sculpt {len(arr)} sommets')
    return base, out, info


def compare(preset, race, hp_json, opts=None):
    opts = opts or {}
    ctx = Ctx()
    j = json.load(open(assets.chargen(rf"Presets\{preset}.jslot"), encoding='utf-8'))
    ref = {s.name: np.array(s.verts) for s in NifFile(assets.chargen(rf"{preset}.nif")).shapes}
    hps = json.load(open(hp_json, encoding='utf-8-sig'))['headParts']
    rows = []
    for hp in hps:
        r = morph_part(ctx, hp, j, race, opts)
        if r is None or hp['edid'] not in ref: continue
        base, out, info = r
        R = ref[hp['edid']]
        if len(R) != len(base):
            rows.append((hp['edid'], len(base), None, None, None, f'référence {len(R)} sommets')); continue
        rows.append((hp['edid'], len(base), np.abs(R - base).mean(), np.abs(R - out).mean(), np.abs(R - out).max(), ', '.join(info)))
    return rows


if __name__ == '__main__':
    preset, race, hp_json = sys.argv[1:4]
    print(f"{'headpart':36} {'sommets':>7} {'réf-base':>9} {'réf-nous':>9} {'max':>7}")
    for name, n, e0, e1, emax, info in compare(preset, race, hp_json):
        if e0 is None: print(f'{name:36} {n:7}   {info}'); continue
        print(f'{name:36} {n:7} {e0:9.4f} {e1:9.4f} {emax:7.3f}  {info}')
