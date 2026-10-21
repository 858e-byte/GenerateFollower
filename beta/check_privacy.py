"""Vérifie qu'un dossier d'application ne contient aucune information personnelle : cherche chaque terme donné
(nom d'utilisateur Windows, nom du PC, dossier du projet…) dans tous les fichiers, texte et binaires, sans tenir compte
de la casse. Code de sortie 1 si un terme est trouvé.
Usage : check_privacy.py <dossier> <terme> [<terme>…]
"""
import os, re, sys

sys.stdout.reconfigure(encoding='utf-8')
root, terms = sys.argv[1], [t for t in sys.argv[2:] if len(t) >= 3]
needles = [(t, t.lower().encode('utf-8')) for t in terms]
# variantes en UTF-16 (chaînes .NET dans l'exe)
needles += [(t + ' (UTF-16)', t.lower().encode('utf-16-le')) for t in terms]
# seule exception : un terme tombé par hasard dans un identifiant d'extension Chromium (32 lettres de a à p, format
# interne fixe ; cas rencontré dans msedge.dll du runtime WebView2 de Microsoft, 21/10)
EXT_ID = re.compile(rb'(?<![a-z])[a-p]{32}(?![a-z])')


def in_extension_id(data, start, end):
    left, right = max(0, start - 32), min(len(data), end + 32)
    return any(m.start() <= start and end <= m.end() for m in EXT_ID.finditer(data, left, right))


found = 0
for d, _, files in os.walk(root):
    for f in files:
        path = os.path.join(d, f)
        data = open(path, 'rb').read().lower()
        for label, n in needles:
            pos, real, skipped = data.find(n), 0, 0
            while pos >= 0:
                if in_extension_id(data, pos, pos + len(n)): skipped += 1
                else: real += 1
                pos = data.find(n, pos + 1)
            if real:
                found += 1
                print(f'TROUVÉ « {label} » dans {os.path.relpath(path, root)}')
            elif skipped:
                print(f'ignoré « {label} » dans {os.path.relpath(path, root)} : {skipped} fois dans un identifiant d\'extension Chromium')
print(f'Vérification des informations personnelles : {len(terms)} terme(s), {found} occurrence(s)')
sys.exit(1 if found else 0)
