"""Retire le nom d'utilisateur Windows et le nom du PC de l'en-tête de fichiers .pex (même règle que
Packaging.SanitizePex en C#). Usage : sanitize_pex.py <fichier.pex ou dossier>…  Garde la date de modification
(le .pex livré doit rester plus récent que sa source .psc)."""
import os, sys


def pstr(s):
    b = s.encode('ascii')
    return len(b).to_bytes(2, 'big') + b


def sanitize(path):
    d = open(path, 'rb').read()
    if d[:4] != b'\xFA\x57\xC0\xDE':
        return False
    p = 16
    ln = lambda at: int.from_bytes(d[at:at + 2], 'big')
    p += 2 + ln(p)          # fichier source : gardé
    start = p
    p += 2 + ln(p)          # nom d'utilisateur
    p += 2 + ln(p)          # nom du PC
    out = d[:start] + pstr('user') + pstr('pc') + d[p:]
    if out == d:
        return False
    st = os.stat(path)
    open(path, 'wb').write(out)
    os.utime(path, (st.st_atime, st.st_mtime))
    return True


for arg in sys.argv[1:]:
    files = [os.path.join(arg, f) for f in os.listdir(arg) if f.lower().endswith('.pex')] if os.path.isdir(arg) else [arg]
    for f in files:
        print(('nettoyé ' if sanitize(f) else 'inchangé ') + f)
