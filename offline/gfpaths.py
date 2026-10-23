"""Chemins de l'installation pour les scripts de offline\\ (plus de chemins en dur).

Ordre : variables d'environnement posées par le programme C# (Py.Start : GF_DATA, GF_VDATA, GF_PYNIFLY, GF_TEXCONV), sinon
settings.json (GF_SETTINGS, sinon à côté de config.json en mode portable, sinon %LOCALAPPDATA%\\GenerateFollower), sinon détection :
Data depuis le registre (Steam/GOG), PyNifly dans deps\\pynifly, texconv dans deps\\texconv puis celui de SSEEdit.
Permet aussi de lancer un script à la main.
"""
import os
import sys
import json
import glob

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _under_usvfs():
    """Vrai si le processus tourne dans le système de fichiers virtuel de MO2 (usvfs injecté au lancement)."""
    if os.name != 'nt':
        return False
    try:
        import ctypes
        k = ctypes.WinDLL('kernel32', use_last_error=True)
        k.GetModuleHandleW.restype = ctypes.c_void_p
        return any(k.GetModuleHandleW(n) for n in ('usvfs_x64.dll', 'usvfs_x86.dll'))
    except Exception:
        return False


def _patch_usvfs():
    """Lancé depuis MO2 : depuis Python 3.12, os.stat (donc os.path.isfile, exists, getmtime…) passe par
    GetFileInformationByName, qu'usvfs n'intercepte pas : les fichiers des mods (Data virtuel, overwrite) semblent absents
    alors que open, listdir et scandir les voient (22/10, poste d'un joueur et MO2 portable). Ici : si os.stat ne trouve pas
    le fichier, on le cherche dans le listing de son dossier (FindFirstFile, intercepté par usvfs) et on rend ses infos."""
    import stat as st

    def wrap(orig):
        def stat(path, *, dir_fd=None, follow_symlinks=True):
            try:
                return orig(path, dir_fd=dir_fd, follow_symlinks=follow_symlinks)
            except FileNotFoundError:
                if dir_fd is not None or isinstance(path, (bytes, int)):
                    raise
                parent, name = os.path.split(os.path.abspath(os.fspath(path)))
                try:
                    with os.scandir(parent) as it:
                        for e in it:
                            if e.name.lower() == name.lower():
                                return e.stat(follow_symlinks=follow_symlinks)
                except OSError:
                    pass
                raise
        return stat

    os.stat = wrap(os.stat)
    _lstat = os.stat
    os.lstat = lambda path, *, dir_fd=None: _lstat(path, dir_fd=dir_fd, follow_symlinks=False)

    def test(mode_ok, follow=True):
        def f(path):
            try:
                return mode_ok(os.stat(path, follow_symlinks=follow).st_mode)
            except (OSError, ValueError, TypeError):
                return False
        return f
    # Python 3.12+ : ntpath.isfile/isdir/exists sont des fonctions natives qui n'utilisent pas os.stat
    os.path.isfile = test(st.S_ISREG)
    os.path.isdir = test(st.S_ISDIR)
    os.path.exists = test(lambda m: True)
    os.path.lexists = test(lambda m: True, follow=False)


if _under_usvfs():
    _patch_usvfs()


def _settings():
    for p in ([os.environ['GF_SETTINGS']] if os.environ.get('GF_SETTINGS') else []) + [os.path.join(ROOT, 'settings.json'),
              os.path.join(os.environ.get('LOCALAPPDATA', ''), 'GenerateFollower', 'settings.json')]:
        if os.path.isfile(p):
            try:
                return {k.lower(): v for k, v in json.load(open(p, encoding='utf-8-sig')).items()}
            except Exception:
                return {}
    return {}


_S = _settings()


def _get(env, key):
    v = os.environ.get(env) or _S.get(key.lower()) or ''
    return v.strip()


def _registry_data():
    try:
        import winreg
    except ImportError:
        return ''
    for key in (r'SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition',
                r'SOFTWARE\Bethesda Softworks\Skyrim Special Edition'):
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, key) as k:
                return os.path.join(winreg.QueryValueEx(k, 'installed path')[0], 'Data')
        except OSError:
            pass
    return ''


DATA = _get('GF_DATA', 'gameDataDir') or _registry_data()


def _vdata():
    """Data virtuel (MO2 lu directement) : work\\vdata.json écrit par le programme C# (GameFiles), chemin dans GF_VDATA."""
    p = os.environ.get('GF_VDATA', '')
    if p and os.path.isfile(p):
        try:
            return json.load(open(p, encoding='utf-8-sig'))
        except Exception:
            pass
    return {}


_V = _vdata()
# Racines où chercher un fichier loose, par priorité décroissante (overwrite, mods activés, Data) ; Data seul sinon
ROOTS = _V.get('roots') or [DATA]
OVERWRITE = _V.get('overwrite')
# BSA par priorité décroissante (le premier gagne) ; None = tous les BSA de Data (Vortex, aucun gestionnaire)
ARCHIVES = _V.get('archives')
# test seulement (banc work\diag\mo2_sans_vfs) : fichiers de Data ignorés, un chemin relatif par ligne
DATA_EXCLUDE = None
if _V.get('dataExclure') and os.path.isfile(_V['dataExclure']):
    DATA_EXCLUDE = {l.strip().replace('/', '\\').lower() for l in open(_V['dataExclure'], encoding='utf-8-sig') if l.strip()}

PYNIFLY = _get('GF_PYNIFLY', 'pyNifly') or os.path.join(ROOT, 'deps', 'pynifly')
_TEXCONV = _get('GF_TEXCONV', 'texconv')


def texconv():
    if _TEXCONV:
        return _TEXCONV
    # livré avec l'application (deps\texconv), sinon celui de SSEEdit
    shipped = os.path.join(ROOT, 'deps', 'texconv', 'texconv.exe')
    if os.path.isfile(shipped):
        return shipped
    found = glob.glob(os.path.join(DATA, 'SSEEdit*', 'Edit Scripts', 'Texconvx64.exe'))
    if not found:
        raise FileNotFoundError("texconv introuvable : indique son chemin dans les Réglages du Builder")
    return found[0]


def add_pynifly():
    """Rend « pyn » (PyNifly) importable."""
    if PYNIFLY not in sys.path:
        sys.path.insert(0, PYNIFLY)
