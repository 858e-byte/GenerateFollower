r"""Copie dans le mod les fichiers des headparts recréés (followers autonomes).

Usage : copy_assets.py <liste.json> <dossier du mod> [--sous-dossier NOM] [--manifeste fichier.json]
Pour chaque .nif copié, ajoute les textures de ses formes (slots du BSShaderTextureSet). Les fichiers viennent
du dossier Data (loose ; sous MO2 lu directement : overwrite et mods activés, voir assets.find) ou des BSA (extraits
dans offline\bsa_cache). N'écrit que dans le dossier du mod.
--sous-dossier : copie sous meshes\NOM\… et textures\NOM\… au lieu du chemin d'origine, pour ne pas entrer en conflit
avec le mod d'origine dans le gestionnaire de mods (le programme C# réécrit ensuite les chemins dans le plugin et les .nif).
--manifeste : écrit la liste des fichiers copiés (chemins d'origine relatifs à Data, « meshes\… » / « textures\… »).
"""
import sys, os, json, shutil
import assets
from pyn.pynifly import NifFile


def norm(rel, kind):
    rel = rel.replace('/', '\\').lstrip('\\')
    return rel if rel.lower().startswith(kind + '\\') else kind + '\\' + rel


def opt(name):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else None


files = json.load(open(sys.argv[1], encoding='utf-8'))
out = sys.argv[2]
sub = opt('--sous-dossier')
manifest = opt('--manifeste')
if sub is not None and (not sub or any(c in sub for c in '\\/:.') ):
    sys.exit('sous-dossier refusé : ' + sub)


def dest(rel):
    """Chemin de destination dans le mod : chemin d'origine, ou meshes\\<sub>\\… / textures\\<sub>\\…"""
    if not sub:
        return os.path.join(out, rel)
    top, rest = rel.split('\\', 1)
    return os.path.join(out, top, sub, rest)


todo = [norm(f, 'meshes' if f.lower().endswith(('.nif', '.tri')) else 'textures') if not f.lower().startswith(('meshes', 'textures')) else f
        for f in files]
seen, copied, missing, size, done = set(), 0, [], 0, []
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
    dst = dest(rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(src, dst)
    copied += 1
    size += os.path.getsize(dst)
    done.append(rel)
    if key.endswith('.nif'):
        try:
            for s in NifFile(src).shapes:
                for t in (s.textures.values() if s.shader else []):
                    if t:
                        todo.append(norm(t, 'textures'))
        except Exception as e:
            missing.append(f'{rel} (lecture impossible : {e})')
if manifest:
    json.dump(sorted(done, key=str.lower), open(manifest, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(f'{copied} fichiers copiés ({size / 1e6:.0f} Mo), {len(missing)} introuvables')
for m in missing:
    print('  introuvable :', m)
