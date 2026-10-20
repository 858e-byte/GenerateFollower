"""Index des mods du gestionnaire (Vortex : dossier de staging ; MO2 : dossier mods de l'instance), activés ou non :
plugins et textures de chaque mod. Lecture seule.

Usage : mods_index.py <dossier des mods> <sortie.json>
Sortie : {"mods": {"<dossier du mod>": {"mtime": …, "plugins": ["x.esp"…], "dds": ["textures\\…\\a.dds"…],
          "nexus": "95818" | null, "archive": "<nom de l'archive installée, sans extension>" | null}}}
Numéro Nexus et archive : meta.ini de MO2 (modid, installationFile), sinon le nom du dossier Vortex (« Nom-95818-2-1-… »).
Incrémental : un mod dont le dossier n'a pas changé (date) garde son entrée précédente.
Sert au Follower Builder : trouver une texture du même nom installée ailleurs (réparation), dire quel mod fournit
un plugin ou une texture manquante (désactivé, ou absent), et au catalogue des skins.
"""
import os, sys, json, re, configparser

VERSION = 2
root, out = sys.argv[1], sys.argv[2]
old = {}
if os.path.isfile(out):
    try:
        old = json.load(open(out, encoding='utf-8')).get('mods', {})
    except Exception:
        old = {}


def meta(path, name):
    """(numéro Nexus, archive) : meta.ini de MO2, sinon nom du dossier Vortex."""
    nexus, archive = None, None
    ini = os.path.join(path, 'meta.ini')
    if os.path.isfile(ini):
        cp = configparser.RawConfigParser(strict=False, interpolation=None)
        try:
            cp.read(ini, encoding='utf-8')
            mid = cp.get('General', 'modid', fallback='').strip()
            nexus = mid if mid.isdigit() and int(mid) > 0 else None
            inst = cp.get('General', 'installationFile', fallback='').strip().strip('"')
            archive = os.path.splitext(os.path.basename(inst.replace('/', '\\')))[0] or None
        except Exception:
            pass
    if not nexus:
        m = re.search(r'-(\d{3,})-', name)
        nexus = m.group(1) if m else None
    return nexus, archive


mods, scanned = {}, 0
for name in sorted(os.listdir(root)):
    path = os.path.join(root, name)
    if not os.path.isdir(path) or name.lower().endswith('_separator'):
        continue      # séparateurs de MO2 : dossiers vides
    mtime = os.path.getmtime(path)
    prev = old.get(name)
    if prev and prev.get('v') == VERSION and abs(prev.get('mtime', 0) - mtime) < 1:
        mods[name] = prev
        continue
    scanned += 1
    plugins = [f for f in os.listdir(path) if f.lower().endswith(('.esp', '.esm', '.esl'))]
    dds = []
    for d, _, files in os.walk(path):
        for f in files:
            if f.lower().endswith('.dds'):
                rel = os.path.relpath(os.path.join(d, f), path)
                if rel.lower().startswith('textures' + os.sep):
                    dds.append(rel)
    nexus, archive = meta(path, name)
    mods[name] = {'v': VERSION, 'mtime': mtime, 'plugins': plugins, 'dds': dds, 'nexus': nexus, 'archive': archive}
json.dump({'mods': mods}, open(out, 'w', encoding='utf-8'))
print(json.dumps({'mods': len(mods), 'relus': scanned}))
