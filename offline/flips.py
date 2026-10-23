"""Cherche le sens de chaque curseur vanilla (slot) en minimisant l'erreur sur plusieurs presets/headparts.
Usage : flips.py "preset|race" ...   (fiches headparts dans hp\\<preset>.json ou anna_hp.json)"""
import sys, json, os
import numpy as np
import headgen as hg, assets
from pyn.pynifly import NifFile

ctx = hg.Ctx()
cases = []
for arg in sys.argv[1:]:
    preset, race = arg.split('|')
    hpj = f'hp\\{preset}.json' if os.path.exists(f'hp\\{preset}.json') else 'anna_hp.json'
    j = json.load(open(assets.chargen(rf"Presets\{preset}.jslot"), encoding='utf-8'))
    ref = {s.name: np.array(s.verts) for s in NifFile(assets.chargen(rf"{preset}.nif")).shapes}
    for hp in json.load(open(hpj, encoding='utf-8-sig'))['headParts']:
        if hp['edid'] in ref and hg.part_file(hp, 'ChargenMorph') and hp.get('model'):
            cases.append((preset, race, j, hp, ref[hp['edid']]))


def total(pairs):
    s = 0
    for preset, race, j, hp, R in cases:
        r = hg.morph_part(ctx, hp, j, race, {'pairs': pairs})
        if r and len(r[1]) == len(R): s += np.abs(R - r[1]).mean()
    return s / len(cases)


pairs = list(hg.PAIRS)
print(f'départ {total(pairs):.5f} sur {len(cases)} headparts')
for it in range(2):
    for i in range(18):
        flipped = pairs[:i] + [(pairs[i][1], pairs[i][0])] + pairs[i + 1:]
        a, b = total(pairs), total(flipped)
        if b < a - 1e-6:
            pairs = flipped
            print(f'  slot {i:2} inversé -> {pairs[i]}  ({a:.5f} -> {b:.5f})')
print(f'final {total(pairs):.5f}')
print('PAIRS =', pairs)
