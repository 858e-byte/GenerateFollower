"""Copie dans le mod les fichiers des headparts recréés (followers autonomes), au même chemin relatif à Data.

Usage : copy_assets.py <liste.json> <dossier du mod>
Pour chaque .nif copié, ajoute les textures de ses formes (slots du BSShaderTextureSet). Les fichiers viennent
du dossier Data (loose) ou des BSA (extraits dans offline\\bsa_cache). N'écrit que dans le dossier du mod.
"""
import sys, os, json, shutil
import assets
from pyn.pynifly import NifFile


def norm(rel, kind):
    rel = rel.replace('/', '\\').lstrip('\\')
    return rel if rel.lower().startswith(kind + '\\') else kind + '\\' + rel


files = json.load(open(sys.argv[1], encoding='utf-8'))
out = sys.argv[2]
todo = [norm(f, 'meshes' if f.lower().endswith(('.nif', '.tri')) else 'textures') if not f.lower().startswith(('meshes', 'textures')) else f
        for f in files]
seen, copied, missing, size = set(), 0, [], 0
while todo:
    rel = todo.pop()
    key = rel.lower()
    if key in seen:
        continue
    seen.add(key)
    src = assets.find(rel)
    if not src:
        missing.append(rel)
        continue
    dst = os.path.join(out, rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(src, dst)
    copied += 1
    size += os.path.getsize(dst)
    if key.endswith('.nif'):
        try:
            for s in NifFile(src).shapes:
                for t in (s.textures.values() if s.shader else []):
                    if t:
                        todo.append(norm(t, 'textures'))
        except Exception as e:
            missing.append(f'{rel} (lecture impossible : {e})')
print(f'{copied} fichiers copiés ({size / 1e6:.0f} Mo), {len(missing)} introuvables')
for m in missing:
    print('  introuvable :', m)
