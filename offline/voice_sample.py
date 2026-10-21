r"""Extrait d'une voix du jeu pour l'écouter dans le Builder : une réplique (de préférence de follower) d'un type de voix,
lue dans l'archive de voix du jeu (Skyrim - Voices_<langue>0.bsa, ou Dawnguard.bsa / Dragonborn.bsa pour les voix des
DLC), puis convertie en WAV avec ffmpeg (cherché dans le PATH, ou GF_FFMPEG).
Un .fuz = « FUZE », version, taille du .lip, .lip, puis l'audio xWMA (que les navigateurs ne lisent pas).
Usage : voice_sample.py <EditorID de la voix> <plugin d'origine : skyrim.esm|dawnguard.esm|dragonborn.esm> <fichier .wav de sortie>
Lecture seule dans le dossier du jeu ; n'écrit que le .wav demandé (et un .xwm temporaire à côté, supprimé)."""
import glob, os, re, shutil, struct, subprocess, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bsa  # noqa: E402
from gfpaths import DATA  # noqa: E402

voice, plugin, out = sys.argv[1], sys.argv[2].lower(), sys.argv[3]
if not re.fullmatch(r'[A-Za-z0-9_]{1,64}', voice) or plugin not in ('skyrim.esm', 'dawnguard.esm', 'dragonborn.esm'):
    sys.exit('paramètres refusés')
ffmpeg = os.environ.get('GF_FFMPEG') or shutil.which('ffmpeg')
if not ffmpeg:
    sys.exit('ffmpeg introuvable (PATH)')

# Skyrim SE : les voix des DLC sont aussi dans l'archive de voix principale (dossier sound\voice\dawnguard.esm\…)
archives = sorted(glob.glob(os.path.join(DATA, 'Skyrim - Voices_*0.bsa'))) + \
    {'skyrim.esm': [], 'dawnguard.esm': [os.path.join(DATA, 'Dawnguard.bsa')], 'dragonborn.esm': [os.path.join(DATA, 'Dragonborn.bsa')]}[plugin]
folder = f'sound\\voice\\{plugin}\\{voice.lower()}\\'
for arc in archives:
    if not os.path.isfile(arc):
        continue
    idx = bsa.read_index(arc)
    files = sorted(k for k in idx if k.startswith(folder) and k.endswith('.fuz'))
    if not files:
        continue
    # une réplique de follower si la voix en a, sinon une réplique générique, sinon la première
    pick = next((f for f in files if '\\dialoguefollower' in f), None) or next((f for f in files if '\\dialoguegeneric' in f), None) or files[0]
    off, size, comp, embed = idx[pick]
    with open(arc, 'rb') as f:
        f.seek(off)
        if embed:
            n = f.read(1)[0]; f.read(n); size -= 1 + n
        data = f.read(size)
    if comp:
        data = bsa.lz4.frame.decompress(data[4:])
    if data[:4] != b'FUZE':
        sys.exit(f'{pick} : pas un fichier .fuz')
    lip_size, = struct.unpack_from('<I', data, 8)
    xwm = out + '.xwm'
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(xwm, 'wb') as g:
        g.write(data[12 + lip_size:])
    try:
        r = subprocess.run([ffmpeg, '-y', '-loglevel', 'error', '-i', xwm, '-ac', '1', '-c:a', 'pcm_s16le', out], capture_output=True, text=True)
    finally:
        os.remove(xwm)
    if r.returncode != 0 or not os.path.isfile(out):
        sys.exit('ffmpeg : ' + r.stderr.strip())
    print(pick)
    sys.exit(0)
sys.exit(f'aucune réplique pour {voice} dans {", ".join(os.path.basename(a) for a in archives)}')
