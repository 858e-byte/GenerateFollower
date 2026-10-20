"""Accès aux fichiers du jeu (loose puis BSA extraits) et registre des morphs RaceMenu (morphs.ini)."""
import os
import sys
import glob
import gfpaths

# PyNifly (DLL nifly de BodySlide) et dossier Data : Réglages (voir gfpaths.py)
PYNIFLY = gfpaths.PYNIFLY
gfpaths.add_pynifly()

DATA = gfpaths.DATA
CACHE = os.path.join(os.path.dirname(__file__), "bsa_cache")
MORPHS_DIR = r"meshes\actors\character\facegenmorphs\morphs"


def _roots():
    yield DATA                                   # loose : priorité sur les BSA
    for d in sorted(glob.glob(os.path.join(CACHE, "*"))):
        yield d


def find(rel):
    """Chemin réel d'un fichier relatif à Data (ex. 'meshes\\kl\\...'), ou None."""
    rel = rel.replace('/', '\\').lstrip('\\')
    for r in _roots():
        p = os.path.join(r, rel)
        if os.path.isfile(p):
            return p
    import bsa   # dernier recours : extraction à la demande depuis les archives du jeu
    return bsa.find(rel, DATA)


def mesh(rel):
    """Chemin d'un fichier relatif à Meshes (les enregistrements HDPT omettent parfois 'Meshes\\')."""
    rel = rel.replace('/', '\\')
    if not rel.lower().startswith('meshes\\'):
        rel = 'meshes\\' + rel
    return find(rel)


_inis_ready = False


def _extract_morph_inis():
    """Une fois : extrait des BSA les morphs.ini / races.ini / sliders\\*.ini de tous les mods."""
    global _inis_ready
    if not _inis_ready:
        import bsa
        bsa.extract_matching(r"meshes\actors\character\facegenmorphs" + "\\", '.ini', DATA)
        _inis_ready = True


def _slider_inis(race):
    """Fichiers sliders\\*.ini applicables à une race, d'après le races.ini de chaque dossier de mod."""
    _extract_morph_inis()
    folders = {}
    for r in _roots():
        for f in glob.glob(os.path.join(r, r"meshes\actors\character\facegenmorphs\*")):
            folders.setdefault(os.path.basename(f).lower(), f)   # loose prioritaire
    inis = []
    for name in sorted(folders):
        f = folders[name]
        races = os.path.join(f, 'races.ini')
        if race and os.path.isfile(races):
            for line in open(races, encoding='latin-1'):
                line = line.split('#')[0].strip()
                if '=' in line and line.split('=', 1)[0].strip().lower() == race.lower():
                    p = os.path.join(f, line.split('=', 1)[1].strip())
                    if os.path.isfile(p): inis.append(p)
        else:
            inis += glob.glob(os.path.join(f, 'sliders', '*.ini'))
    return inis


def slider_registry(race=None):
    """Nom de curseur RaceMenu -> (morph pour valeur négative, morph pour valeur positive).
    Une définition plus loin dans les fichiers remplace la précédente (comme RaceMenu)."""
    reg = {}
    for ini in _slider_inis(race):
        for line in open(ini, encoding='latin-1'):
            line = line.split('#')[0].strip()
            if '=' not in line or line.startswith('['):
                continue
            name, rhs = line.split('=', 1)
            f = [x.strip() for x in rhs.split(',')]
            if len(f) >= 4 and f[1].lower() == 'slider':
                reg[name.strip().lower()] = (f[2], f[3])
    return reg


def morph_registry():
    """source TRI (minuscules, relatif à Meshes) -> liste de TRI additionnels (chemins réels)."""
    _extract_morph_inis()
    reg = {}
    inis = []
    for r in _roots():
        inis += glob.glob(os.path.join(r, r"meshes\actors\character\facegenmorphs\*\morphs.ini"))
    for ini in inis:
        for line in open(ini, encoding='latin-1'):
            line = line.split('#')[0].strip()
            if not line.lower().startswith('extension'):
                continue
            parts = [p.strip() for p in line.split('=', 1)[1].split(',') if p.strip()]
            src, extra = parts[0].lower().replace('/', '\\'), parts[1:]
            for e in extra:
                p = find(os.path.join(MORPHS_DIR, e))
                if p and p not in reg.setdefault(src, []):
                    reg[src].append(p)
    return reg
