"""Textes à traduire du Follower Builder (clés de builder\\i18n\\<langue>.json) et contrôle d'un fichier de traduction.

Usage : i18n_keys.py [fichier de traduction]
Sources : L.T("…") des .cs de src\\ (littéraux concaténés par +), T('…') de builder\\index.html, tableau SETTINGS,
textes fixes de la page (nœuds de texte, title, placeholder) et textes des verdicts (Builder.cs, traduits par la page).
Sans argument : liste des clés (une par ligne). Avec un fichier : clés manquantes et clés en trop, code 1 s'il en manque.
"""
import os, re, sys, json, glob
from html.parser import HTMLParser

sys.stdout.reconfigure(encoding='utf-8')

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
keys = []


def add(k, strip=True):
    # textes du code gardés tels quels (« " est la…" » commence par une espace) ; textes de la page sans les blancs
    k = k.strip() if strip else k
    if k and re.search(r'[A-Za-zÀ-ÿ]', k) and k not in keys:
        keys.append(k)


# ---- C# : L.T("a" + "b", …)
CS_LIT = r'"((?:[^"\\]|\\.)*)"'
for path in sorted(glob.glob(os.path.join(ROOT, 'src', '*.cs'))):
    src = open(path, encoding='utf-8').read()
    for m in re.finditer(r'L\.T\(\s*((?:' + CS_LIT + r'\s*\+?\s*)+)', src):
        parts = re.findall(CS_LIT, m.group(1))
        add(''.join(p.replace('\\"', '"').replace('\\\\', '\\') for p in parts), strip=False)
    # verdicts : textes français mémorisés, traduits par la page
    for m in re.finditer(r'\["texte"\]\s*=\s*' + CS_LIT, src):
        add(m.group(1))

# ---- page
html = open(os.path.join(ROOT, 'builder', 'index.html'), encoding='utf-8').read()
script_at = html.index('<script type="module">')
JS_LIT = r"'((?:[^'\\]|\\.)*)'|`((?:[^`\\]|\\.)*)`|\"((?:[^\"\\]|\\.)*)\""


def js_unescape(s):
    return re.sub(r"\\(.)", r'\1', s)


for m in re.finditer(r'\bT\(\s*(?:' + JS_LIT + r')', html[script_at:]):
    add(js_unescape(next(g for g in m.groups() if g is not None)))
settings = re.search(r'const SETTINGS = \[(.*?)\n\];', html, re.S).group(1)
for row in re.finditer(r"\['\w+',\s*'((?:[^'\\]|\\.)*)',\s*'((?:[^'\\]|\\.)*)'\]", settings):
    add(js_unescape(row.group(1)))
    add(js_unescape(row.group(2)))
add('Pas encore vérifié')


class Static(HTMLParser):
    skip = 0

    def handle_starttag(self, tag, attrs):
        if tag in ('style', 'script'):
            self.skip += 1
        for a, v in attrs:
            if a in ('title', 'placeholder') and v:
                add(v)

    def handle_endtag(self, tag):
        if tag in ('style', 'script'):
            self.skip -= 1

    def handle_data(self, data):
        if not self.skip:
            add(data)


Static().feed(html[:script_at])

if len(sys.argv) < 2:
    print('\n'.join(keys))
    sys.exit(0)
tr = json.load(open(sys.argv[1], encoding='utf-8'))
missing = [k for k in keys if not tr.get(k)]
extra = [k for k in tr if k not in keys]
for k in missing:
    print('MANQUANT', k)
for k in extra:
    print('EN TROP ', k)
print(f'{len(keys)} clés, {len(missing)} manquantes, {len(extra)} en trop')
sys.exit(1 if missing else 0)
