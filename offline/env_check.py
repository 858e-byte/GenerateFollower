"""Rapport de diagnostic : versions de Python et des paquets, PyNifly importable. Une ligne « nom: valeur » par élément."""
import os
import sys
import platform

# lz4 livré dans offline\pylib (comme bsa.py)
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), 'pylib'))

print(f'python: {platform.python_version()} ({sys.executable})')
for name in ('numpy', 'PIL', 'lz4'):
    try:
        mod = __import__(name)
        print(f'{name}: {getattr(mod, "__version__", "ok")}')
    except Exception as e:
        print(f'{name}: MISSING ({e})')
try:
    import gfpaths
    gfpaths.add_pynifly()
    from pyn import pynifly
    print(f'pynifly: ok ({pynifly.nifly_path})')
except Exception as e:
    print(f'pynifly: FAILED ({e})')
