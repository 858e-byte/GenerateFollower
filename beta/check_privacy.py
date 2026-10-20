"""Vérifie qu'un dossier d'application ne contient aucune information personnelle : cherche chaque terme donné
(nom d'utilisateur Windows, nom du PC, dossier du projet…) dans tous les fichiers, texte et binaires, sans tenir compte
de la casse. Code de sortie 1 si un terme est trouvé.
Usage : check_privacy.py <dossier> <terme> [<terme>…]
"""
import os, sys

sys.stdout.reconfigure(encoding='utf-8')
root, terms = sys.argv[1], [t for t in sys.argv[2:] if len(t) >= 3]
needles = [(t, t.lower().encode('utf-8')) for t in terms]
# variantes en UTF-16 (chaînes .NET dans l'exe)
needles += [(t + ' (UTF-16)', t.lower().encode('utf-16-le')) for t in terms]
found = 0
for d, _, files in os.walk(root):
    for f in files:
        path = os.path.join(d, f)
        data = open(path, 'rb').read().lower()
        for label, n in needles:
            if n in data:
                found += 1
                print(f'TROUVÉ « {label} » dans {os.path.relpath(path, root)}')
print(f'Vérification des informations personnelles : {len(terms)} terme(s), {found} occurrence(s)')
sys.exit(1 if found else 0)
