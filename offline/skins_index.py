"""Index des skins (textures de peau du corps et du visage) : mods du gestionnaire (Vortex ou MO2, activés ou non) et
archives téléchargées. Lecture seule (les archives sont seulement listées avec 7-Zip).

Usage : skins_index.py <mods_index.json> <dossier des mods> <dossier des archives> <7z.exe> <sortie.json>
Sortie : {"skins": [{"id", "name", "body": "UBE"|"CBBE"|"HIMBO", "sex": "F"|"M", "source": "mod"|"archive",
          "path": dossier du mod ou archive, "prefix": dossier avant « textures\\ » dans l'archive,
          "files": {"body_d": "textures\\…", …}, "sizes": {"body_d": octets, …}}]}

Seuls les mods de skin sont retenus : textures aux chemins génériques du corps (UBE textures\\!UBE\\Body\\…, CBBE/3BA
textures\\actors\\character\\female\\…, hommes textures\\actors\\character\\male\\…), avec au moins la diffuse du corps.
Les skins de followers (sous-dossiers propres) ne sont donc pas proposés (choix de l'utilisateur, 19/10).
Le listing des archives est mis en cache (<sortie>.archives.json), par chemin, taille et date.
"""
import os, sys, json, re, hashlib, subprocess
from concurrent.futures import ThreadPoolExecutor

vindex, mods_dir, arch_dir, sevenzip, out = sys.argv[1:6]
# familles des corps installés (src\Bodies.cs) : femmes CBBE | UNP | VANILLA, hommes HIMBO | MALE
FEMALE = sys.argv[6] if len(sys.argv) > 6 else 'CBBE'
MALE = sys.argv[7] if len(sys.argv) > 7 else 'HIMBO'
cache_path = out[:-5] + '.archives.json' if out.endswith('.json') else out + '.archives.json'

# rôle -> chemin générique (minuscules, depuis « textures\ »), par type de corps
LAYOUTS = {
    ('UBE', 'F'): {
        'body_d': r'!ube\body\femalebody_1_d.dds', 'body_n': r'!ube\body\femalebody_1_n.dds',
        'body_sk': r'!ube\body\femalebody_1_sk.dds', 'body_s': r'!ube\body\femalebody_1_s.dds',
        'head_d': r'!ube\head\femalehead_d.dds', 'head_n': r'!ube\head\femalehead_n.dds',
        'head_sk': r'!ube\head\femalehead_sk.dds', 'head_s': r'!ube\head\femalehead_s.dds',
    },
    ('UBE', 'M'): {
        'body_d': r'!ube\body\malebody_1_d.dds', 'body_n': r'!ube\body\malebody_1_n.dds',
        'body_sk': r'!ube\body\malebody_1_sk.dds', 'body_s': r'!ube\body\malebody_1_s.dds',
        'head_d': r'!ube\head\malehead_d.dds', 'head_n': r'!ube\head\malehead_n.dds',
        'head_sk': r'!ube\head\malehead_sk.dds', 'head_s': r'!ube\head\malehead_s.dds',
    },
    ('CBBE', 'F'): {
        'body_d': r'actors\character\female\femalebody_1.dds', 'body_n': r'actors\character\female\femalebody_1_msn.dds',
        'body_sk': r'actors\character\female\femalebody_1_sk.dds', 'body_s': r'actors\character\female\femalebody_1_s.dds',
        'hands_d': r'actors\character\female\femalehands_1.dds', 'hands_n': r'actors\character\female\femalehands_1_msn.dds',
        'hands_sk': r'actors\character\female\femalehands_1_sk.dds', 'hands_s': r'actors\character\female\femalehands_1_s.dds',
        'head_d': r'actors\character\female\femalehead.dds', 'head_n': r'actors\character\female\femalehead_msn.dds',
        'head_sk': r'actors\character\female\femalehead_sk.dds', 'head_s': r'actors\character\female\femalehead_s.dds',
    },
    ('HIMBO', 'M'): {
        'body_d': r'actors\character\male\malebody_1.dds', 'body_n': r'actors\character\male\malebody_1_msn.dds',
        'body_sk': r'actors\character\male\malebody_1_sk.dds', 'body_s': r'actors\character\male\malebody_1_s.dds',
        'hands_d': r'actors\character\male\malehands_1.dds', 'hands_n': r'actors\character\male\malehands_1_msn.dds',
        'hands_sk': r'actors\character\male\malehands_1_sk.dds', 'hands_s': r'actors\character\male\malehands_1_s.dds',
        'head_d': r'actors\character\male\malehead.dds', 'head_n': r'actors\character\male\malehead_msn.dds',
        'head_sk': r'actors\character\male\malehead_sk.dds', 'head_s': r'actors\character\male\malehead_s.dds',
    },
}
# corps des Erin femmes (src\Bodies.cs, meshes\actors\Erin) : s'il diffère du corps féminin installé (BHUNP pour les Erin,
# 3BA pour le reste), les skins marqués pour ce corps sont aussi gardés, sous sa famille (pour les Erin seulement)
ERIN = sys.argv[8] if len(sys.argv) > 8 else FEMALE


def marks(fam):
    """(autres corps féminins, corps non acceptés, corps acceptés) pour la famille fam : mêmes chemins de textures,
    UV différentes, reconnus au nom du mod ou du dossier d'option."""
    if fam == 'UNP':
        other = re.compile(r'cbbe|3ba|3bbb|vanilla', re.I)
        return other, other, re.compile(r'uunp|bhunp|\bunp\b|ube|himbo', re.I)
    if fam == 'VANILLA':
        other = re.compile(r'cbbe|3ba|3bbb|uunp|bhunp|\bunp\b', re.I)
        return other, other, re.compile(r'vanilla|ube|himbo', re.I)
    # CBBE/3BA : UUNP, BHUNP écartés ; acceptés : UBE, le corps féminin installé, HIMBO
    return re.compile(r'uunp|bhunp|unp\b|vanilla', re.I), re.compile(r'uunp|bhunp|\bunp\b', re.I), re.compile(r'cbbe|3ba|3bbb|ube|himbo', re.I)


OTHER_FEMALE, UNSUPPORTED, SUPPORTED = marks(FEMALE)
# marque explicite du corps des Erin (sans UBE/HIMBO) : exigée pour un skin gardé seulement pour les Erin
ERIN_MARK = {'UNP': re.compile(r'uunp|bhunp|\bunp\b', re.I), 'CBBE': re.compile(r'cbbe|3ba|3bbb', re.I)}.get(ERIN)
OTHER_HEAD = re.compile(r'\bece\b|expressive', re.I)
# corps masculin autre que HIMBO (UV vanilla) : les skins faits pour HIMBO sont écartés
HIMBO_MARK = re.compile(r'himbo', re.I)
# clés internes « CBBE F » et « HIMBO M » des chemins génériques -> famille installée
BODY_NAME = {'CBBE': FEMALE, 'HIMBO': MALE, 'UBE': 'UBE'}
excluded = []


def unsupported(name, prefix, body, fam=None):
    """Skin d'un corps non utilisé : dossier d'option UUNP/BHUNP, ou nom qui en parle sans citer un corps accepté
    (une archive « CBBE + UUNP » garde son dossier CBBE). Pour CBBE, « vanilla » aussi (mêmes chemins, autres UV).
    Hommes sans HIMBO : skins marqués HIMBO écartés. fam : famille féminine testée (corps installé par défaut)."""
    fam = fam or FEMALE
    other_female, unsupported_re, supported_re = marks(fam)
    if body == 'HIMBO' and MALE != 'HIMBO':
        return bool(HIMBO_MARK.search(name + ' ' + prefix))
    if unsupported_re.search(prefix):
        return True
    if supported_re.search(prefix):
        return False
    if body == 'CBBE' and other_female.search(prefix):
        return True
    text = name + ' ' + prefix
    if unsupported_re.search(text) and not supported_re.search(text):
        return True
    return body == 'CBBE' and fam != 'VANILLA' and bool(re.search(r'vanilla', text, re.I)) and not supported_re.search(text)


def families(name, prefix, body):
    """Familles sous lesquelles garder un skin : le corps installé s'il convient, et le corps des Erin s'il diffère et que
    le skin le cite explicitement (nom du mod ou dossier d'option : « … for BHUNP »)."""
    if body != 'CBBE':
        return [] if unsupported(name, prefix, body) else [BODY_NAME[body]]
    fams = [] if unsupported(name, prefix, body) else [FEMALE]
    if ERIN != FEMALE and ERIN_MARK and ERIN_MARK.search(name + ' ' + prefix) and not unsupported(name, prefix, body, ERIN):
        fams.append(ERIN)
    return fams


def classify(files):
    """files : {chemin minuscule depuis textures\\ : (chemin réel, taille)} -> liste de skins (body, sex, files, sizes)."""
    found = []
    for (body, sex), layout in LAYOUTS.items():
        if layout['body_d'] not in files:
            continue
        roles = {r: files[p] for r, p in layout.items() if p in files}
        found.append((body, sex, {r: v[0] for r, v in roles.items()}, {r: v[1] for r, v in roles.items()}))
    return found


PREFERRED = re.compile(r'required|smooth|base|default|core|main|soft|regular|normal', re.I)


def fill_from_archive(groups, own, layout, files, sizes):
    """Rôles absents du dossier <own> pris dans les autres dossiers de l'archive : d'abord les dossiers de même corps
    (CBBE si <own> est CBBE), puis les noms « required / base / core / smooth… » (pas une option d'apparence comme
    « Hardened »), puis ceux sans diffuse du corps (fichiers communs), puis le moins profond, puis l'ordre alpha.
    Jamais un dossier d'un corps non utilisé (UNP…)."""
    own_mark = SUPPORTED.search(own)
    def rank(prefix):
        has_body = layout['body_d'] in groups[prefix]
        same = 0 if own_mark and own_mark.group(0).lower() in prefix.lower() else 1
        tier = 0 if re.search(r'required', prefix, re.I) else 1 if PREFERRED.search(prefix) else 2
        return (same, tier, has_body, prefix.count('\\'), prefix.lower())
    # ni corps non utilisé (UNP…), ni options pour une autre tête (ECE : UV de tête différentes de HPH/vanilla)
    others = sorted((p for p in groups if p != own and not UNSUPPORTED.search(p) and not OTHER_HEAD.search(p)), key=rank)
    for role, path in layout.items():
        if role in files:
            continue
        for p in others:
            if path in groups[p]:
                files[role], sizes[role] = groups[p][path]
                break


def short(stem):
    """« PB's Silky Skin-95818-2-1-1721031275 » -> « PB's Silky Skin » (numéros Nexus retirés)."""
    s = re.sub(r'-\d{3,}(-[\w.]+)*$', '', stem).strip()
    return s or stem


def variant(prefix):
    """Dernier dossier de l'option FOMOD (« …\\Pubic Area Options\\Bush - Flat\\ » -> « Bush - Flat »)."""
    parts = [x for x in prefix.split('\\') if x]
    return parts[-1] if parts else prefix


def variant_labels(prefixes):
    """Noms d'options uniques dans l'archive : dernier dossier, ou les 2, 3… derniers s'il est ambigu
    (BnP male : « +pubes\\dark\\+hair » et « -pubes\\dark\\+hair »)."""
    parts = {p: [x for x in p.split('\\') if x] for p in prefixes}
    labels = {}
    for p, ps in parts.items():
        for n in range(1, len(ps) + 1):
            tail = ps[-n:]
            if sum(1 for q in parts.values() if q[-n:] == tail) == 1 or n == len(ps):
                labels[p] = ' / '.join(tail)
                break
    return labels


def skin_id(source, path, prefix, body, sex):
    return hashlib.md5(f'{source}|{path}|{prefix}|{body}|{sex}'.lower().encode('utf-8')).hexdigest()[:10]


skins = []
# sans gestionnaire de mods (ou dossier introuvable), pas d'index : archives seulement
mods = json.load(open(vindex, encoding='utf-8'))['mods'] if os.path.isfile(vindex) and mods_dir and os.path.isdir(mods_dir) else {}
for name, m in mods.items():
    files = {}
    for p in m['dds']:
        lp = p.lower()
        if lp.startswith('textures\\'):
            files[lp[9:]] = (p, None)
    for body, sex, f, _ in classify(files):
        fams = families(name, '', body)
        if not fams:
            excluded.append(f'{body} {sex}  {name}')
            continue
        # skin en plusieurs fichiers d'un même mod Nexus (Heartbreaker : « HIMBO Patch » = corps, « Core » = tête et
        # mains) : rôles absents pris dans les autres mods du même numéro Nexus (nom Vortex ou meta.ini de MO2)
        nexus = m.get('nexus') or (re.search(r'-(\d{3,})-', name) or [None, None])[1]
        label = name
        if nexus:
            layout = LAYOUTS[(body, sex)]
            for other, om in mods.items():
                if other == name or (om.get('nexus') != nexus and f'-{nexus}-' not in other):
                    continue
                low = {p.lower()[9:]: p for p in om['dds'] if p.lower().startswith('textures\\')}
                taken = [role for role, path in layout.items() if role not in f and path in low]
                for role in taken:
                    f[role] = os.path.join('..', other, low[layout[role]])
                # nom affiché : celui du mod principal (« Heartbreaker - Core » → « Heartbreaker »), pas du patch
                if taken:
                    label = re.sub(r'\s*-\s*(core|main|base)$', '', short(other), flags=re.I) + f' (+ {short(name)})'
        sizes = {}
        for r, p in f.items():
            try:
                sizes[r] = os.path.getsize(os.path.join(mods_dir, name, p))
            except OSError:
                sizes[r] = 0
        # « vortex » gardé dans l'empreinte : les id déjà choisis (skins_choice.json) restent valables
        for fam in fams:
            skins.append({'id': skin_id('vortex', name, '', fam, sex), 'name': label, 'body': fam, 'sex': sex,
                          'source': 'mod', 'path': os.path.join(mods_dir, name), 'prefix': '', 'files': f, 'sizes': sizes})

# Archives : listing 7-Zip (technique, UTF-8), en cache
try:
    cache = json.load(open(cache_path, encoding='utf-8'))
except Exception:
    cache = {}
# archives déjà installées : nom du dossier (Vortex) ou installationFile de meta.ini (MO2)
installed = {n.lower() for n in mods} | {m['archive'].lower() for m in mods.values() if m.get('archive')}


def list_dds(full):
    """Fichiers .dds d'une archive (listing 7-Zip) : [[chemin, taille], …]."""
    try:
        txt = subprocess.run([sevenzip, 'l', '-slt', '-sccUTF-8', '-ba', full], capture_output=True, timeout=300).stdout.decode('utf-8', 'replace')
    except Exception:
        txt = ''
    dds, path = [], None
    for line in txt.splitlines():
        if line.startswith('Path = '):
            path = line[7:]
        elif line.startswith('Size = ') and path and path.lower().endswith('.dds'):
            dds.append([path, int(line[7:] or 0)])
            path = None
    return dds


# dossier d'archives facultatif (Réglages) ; sans 7-Zip, les archives ne sont pas lues
archives = []
for d, _, names in (os.walk(arch_dir) if arch_dir and os.path.isfile(sevenzip) else []):
    for fn in names:
        if not fn.lower().endswith(('.zip', '.7z', '.rar')):
            continue
        full = os.path.join(d, fn)
        stem = os.path.splitext(fn)[0]
        if stem.lower() in installed:
            continue      # déjà installée dans le gestionnaire de mods
        st = os.stat(full)
        archives.append((full, stem, f'{st.st_size}|{int(st.st_mtime)}'))
# archives nouvelles ou modifiées : listées en parallèle (premier passage sur ~3000 archives : plusieurs minutes en série)
todo = [(full, key) for full, _, key in archives if cache.get(full, {}).get('key') != key]
with ThreadPoolExecutor(max_workers=min(8, os.cpu_count() or 4)) as pool:
    for (full, key), dds in zip(todo, pool.map(lambda t: list_dds(t[0]), todo)):
        cache[full] = {'key': key, 'dds': dds}
listed = len(todo)
# le cache garde aussi les archives d'un autre dossier (changer de dossier puis revenir ne relit pas tout)
new_cache = dict(cache)
# « X for BHUNP » et « X » téléchargés tous les deux (PB's Silky Skin) : « X », sans marque de corps, est pour l'autre corps
BODY_SUFFIX = re.compile(r'\s*[-(]?\s*(for\s+)?(cbbe|3ba|3bbb|uunp|bhunp|unp)\b.*$', re.I)
marked_bases = {}
for _, stem, _ in archives:
    m = BODY_SUFFIX.search(short(stem))
    if m and m.start() > 0:
        marked_bases.setdefault(short(stem)[:m.start()].strip().lower(), set()).add(m.group(2).lower())


def sibling_of_other_body(stem, fam):
    """Archive sans marque dont une sœur marquée existe pour la famille fam : elle est pour un autre corps."""
    found = marked_bases.get(short(stem).strip().lower())
    if not found or BODY_SUFFIX.search(short(stem)):
        return False
    return bool(found & ({'uunp', 'bhunp', 'unp'} if fam == 'UNP' else {'cbbe', '3ba', '3bbb'} if fam == 'CBBE' else set()))


for full, stem, key in archives:
    entry = cache[full]
    # regroupe par dossier avant « textures\ » (variantes d'un FOMOD)
    groups = {}
    for p, size in entry['dds']:
        lp = p.replace('/', '\\').lower()
        i = lp.find('textures\\')
        if i >= 0 and (i == 0 or lp[i - 1] == '\\'):
            groups.setdefault(p[:i], {})[lp[i + 9:]] = (p, size)
            continue
        # options rangées sans « textures\actors\character\ » (Reverie : « 2. Compatibility\2. Body\2. CBBE\0. Core\
        # Female\FemaleBody_1.dds ») : reconnues au dossier Female\ ou Male\
        m = re.search(r'(^|\\)(female|male)\\[^\\]+\.dds$', lp)
        if m:
            j = m.start(2)
            groups.setdefault(p[:j], {})['actors\\character\\' + lp[j:]] = (p, size)
    labels = variant_labels([p for p in groups if p])
    # une option marquée CBBE/3BA (ou HIMBO) dans l'archive : les autres diffuses du même corps, non marquées, sont
    # pour d'autres UV (Reverie : « 0. Required Installations » = vanilla) et sont écartées
    marked = {(b, s) for p, fs in groups.items() if SUPPORTED.search(p) for b, s, _, _ in classify(fs)}
    for prefix, files in groups.items():
        for body, sex, f, sizes in classify(files):
            fams = families(stem, prefix, body)
            if not fams:
                excluded.append(f'{body} {sex}  {stem} [{prefix}]')
                continue
            fams = [fm for fm in fams if not ((body, sex) in marked and not marks(fm)[2].search(prefix))
                    and not (body == 'CBBE' and sibling_of_other_body(stem, fm))]
            if not fams:
                excluded.append(f'{body} {sex}  {stem} [{prefix}] (autre UV : l\'archive a une option {body} explicite)')
                continue
            # FOMOD : une diffuse par option (PB's Silky Skin, The Pure, Binibini…), normal maps, mains, visage et
            # spéculaire rangés dans d'autres dossiers de l'archive : on complète avec ces dossiers
            fill_from_archive(groups, prefix, LAYOUTS[(body, sex)], f, sizes)
            label = short(stem) + (f' — {labels.get(prefix, variant(prefix))}' if prefix and len(groups) > 1 else '')
            for fam in fams:
                skins.append({'id': skin_id('archive', full, prefix, fam, sex), 'name': label, 'body': fam, 'sex': sex,
                              'source': 'archive', 'path': full, 'prefix': prefix, 'files': f, 'sizes': sizes})
json.dump(new_cache, open(cache_path, 'w', encoding='utf-8'))
skins.sort(key=lambda s: (s['body'], s['sex'], s['name'].lower()))
json.dump({'skins': skins, 'exclus_corps': sorted(set(excluded)), 'familles': [FEMALE, MALE, ERIN]},
          open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(json.dumps({'skins': len(skins), 'exclus_corps': len(set(excluded)), 'archives_listees': listed}))
