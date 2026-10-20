"""Génère hors jeu les têtes d'une liste de presets (appelé par `GenerateFollower build --offline`).

Usage : batch_heads.py <jobs.json>
jobs.json : [{"preset": ..., "race": ..., "hp": <fiche headparts.json>, "out": <dossier>, "name": "GF_<id>"}, ...]
Produit <out>\\<name>.nif + <name>.dds. Le .nif pointe vers Data\\SKSE\\Plugins\\CharGen\\<name>.dds, comme un
export RaceMenu, pour que le build réécrive ce chemin vers le FaceTint du PNJ. Une tête déjà présente et plus
récente que son jslot et sa fiche n'est pas régénérée. N'écrit rien dans le dossier du jeu.
"""
import sys, os, json, time, traceback
import assets, make_head

jobs = json.load(open(sys.argv[1], encoding='utf-8-sig'))
ok = fail = skip = 0
t0 = time.time()
for i, job in enumerate(jobs, 1):
    nif = os.path.join(job['out'], job['name'] + '.nif')
    dds = os.path.join(job['out'], job['name'] + '.dds')
    jslot = assets.DATA + rf"\SKSE\Plugins\CharGen\Presets\{job['preset']}.jslot"
    if os.path.isfile(nif) and os.path.isfile(dds):
        newest = max(os.path.getmtime(jslot), os.path.getmtime(job['hp']))
        imp = json.load(open(job['hp'], encoding='utf-8-sig')).get('import')
        if imp and os.path.isfile(imp):   # tête RaceMenu importée modifiée depuis
            newest = max(newest, os.path.getmtime(imp))
        if os.path.getmtime(nif) > newest:
            skip += 1
            continue
    try:
        make_head.make_head(job['preset'], job['race'], job['hp'], job['out'], job['name'],
                            rf"Data\SKSE\Plugins\CharGen\{job['name']}.dds")
        ok += 1
        print(f"[{i}/{len(jobs)}] OK     {job['name']}  {job['preset']}", flush=True)
    except Exception as e:
        fail += 1
        for p in (nif, dds):
            if os.path.isfile(p): os.remove(p)
        print(f"[{i}/{len(jobs)}] ECHEC  {job['name']}  {job['preset']} : {e}", flush=True)
        traceback.print_exc(file=sys.stderr)
print(f"Têtes : {ok} générées, {skip} déjà à jour, {fail} échecs ({time.time() - t0:.0f} s)", flush=True)
