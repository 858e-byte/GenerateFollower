"""Convertit une texture du jeu (chemin relatif à Data, loose ou BSA) en PNG pour l'aperçu 3D.

Usage : dds_png.py <chemin relatif ou absolu .dds> <sortie.png> [taille]
"""
import sys, os, glob, subprocess, shutil, tempfile
import assets, tint

rel, out = sys.argv[1], sys.argv[2]
size = int(sys.argv[3]) if len(sys.argv) > 3 else 1024
path = rel if os.path.isabs(rel) and os.path.isfile(rel) else assets.find(rel if rel.lower().startswith('textures') else 'textures\\' + rel)
if not path:
    sys.exit(f"introuvable : {rel}")
tmp = tempfile.mkdtemp(dir=os.path.dirname(os.path.abspath(out)))
try:
    src = os.path.join(tmp, 'src.dds')
    shutil.copyfile(path, src)
    subprocess.run([tint.texconv(), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM', '-w', str(size), '-h', str(size),
                    '-m', '1', '-o', tmp, src], check=True, capture_output=True)
    png = [p for p in glob.glob(os.path.join(tmp, 'src.*')) if p.lower().endswith('.png')][0]
    os.replace(png, out)
finally:
    shutil.rmtree(tmp, ignore_errors=True)
