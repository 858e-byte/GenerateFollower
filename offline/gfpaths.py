"""Chemins de l'installation pour les scripts de offline\\ (plus de chemins en dur).

Ordre : variables d'environnement posées par le programme C# (Py.Start : GF_DATA, GF_PYNIFLY, GF_TEXCONV), sinon
settings.json (à côté de config.json en mode portable, sinon %LOCALAPPDATA%\\GenerateFollower), sinon détection :
Data depuis le registre (Steam/GOG), PyNifly dans deps\\pynifly, texconv dans deps\\texconv puis celui de SSEEdit.
Permet aussi de lancer un script à la main.
"""
import os
import sys
import json
import glob

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _settings():
    for p in (os.path.join(ROOT, 'settings.json'),
              os.path.join(os.environ.get('LOCALAPPDATA', ''), 'GenerateFollower', 'settings.json')):
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
