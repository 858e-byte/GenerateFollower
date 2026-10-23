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


ROOTS = gfpaths.ROOTS
# overwrite de MO2 : première racine quand elle existe, lue sur le disque (RaceMenu y exporte pendant le travail)
_OVERWRITE = bool(gfpaths.OVERWRITE) and len(ROOTS) > 1 and ROOTS[0] == gfpaths.OVERWRITE
_listings = {}


_dir_roots = {}


def _roots_with(d):
    """MO2 lu directement : racines (hors overwrite, lue sur le disque) qui contiennent le dossier relatif d."""
    if d not in _dir_roots:
        if not d:
            _dir_roots[d] = ROOTS[1:] if _OVERWRITE else list(ROOTS)
        else:
            parent, name = os.path.split(d)
            _dir_roots[d] = [r for r in _roots_with(parent) if name in _listing(r, parent)[1]]
    return _dir_roots[d]


def _find_loose(rel):
    """Fichier loose gagnant (chemin réel) ou None. Plusieurs racines (MO2 lu directement) : listings de dossiers en
    cache, un dossier n'est cherché que dans les racines qui contiennent son parent ; fichiers « .mohidden » ignorés."""
    if len(ROOTS) == 1:
        p = os.path.join(ROOTS[0], rel)
        return p if os.path.isfile(p) else None
    if _OVERWRITE:
        p = os.path.join(ROOTS[0], rel)
        if os.path.isfile(p):
            return p
    d, name = os.path.split(rel.lower())
    for r in _roots_with(d):
        if name in _listing(r, d)[0] and not (r == DATA and gfpaths.DATA_EXCLUDE is not None and rel.lower() in gfpaths.DATA_EXCLUDE):
            return os.path.join(r, rel)
    return None


def _listing(root, d):
    key = (root, d)
    if key not in _listings:
        files, dirs = set(), set()
        try:
            with os.scandir(os.path.join(root, d)) as it:
                for e in it:
                    n = e.name.lower()
                    if n.endswith('.mohidden'):
                        continue
                    (dirs if e.is_dir() else files).add(n)
        except OSError:
            pass
        _listings[key] = (files, dirs)
    return _listings[key]


def _roots():
    yield from ROOTS                             # loose : priorité sur les BSA
    # fichiers déjà extraits des BSA ; MO2 lu directement : seulement le cache de ses archives (bsa.FILES), les
    # anciens dossiers de offline\bsa_cache viennent des BSA de Data
    if gfpaths.ARCHIVES is not None:
        import bsa
        yield bsa.FILES
        return
    for d in sorted(glob.glob(os.path.join(CACHE, "*"))):
        yield d


def find(rel):
    """Chemin réel d'un fichier relatif à Data (ex. 'meshes\\kl\\...'), ou None."""
    rel = rel.replace('/', '\\').lstrip('\\')
    p = _find_loose(rel)
    if p:
        return p
    for r in _roots():
        if r in ROOTS:
            continue
        p = os.path.join(r, rel)
        if os.path.isfile(p):
            return p
    import bsa   # dernier recours : extraction à la demande depuis les archives du jeu
    return bsa.find(rel, DATA)


def chargen(rel):
    """Fichier relatif à SKSE\\Plugins\\CharGen (preset .jslot, export RaceMenu) : chemin réel, sinon son chemin dans Data."""
    return find('SKSE\\Plugins\\CharGen\\' + rel) or os.path.join(DATA, 'SKSE', 'Plugins', 'CharGen', rel)


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
