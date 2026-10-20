"""Lecteur minimal d'archives BSA Skyrim SE (version 105) : index des fichiers et extraction à la demande."""
import os, sys, struct, glob, json

sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'pylib'))
import lz4.frame  # noqa: E402

INDEX = os.path.join(os.path.dirname(__file__), 'bsa_cache', '_index.json')
FILES = os.path.join(os.path.dirname(__file__), 'bsa_cache', '_files')


def read_index(path):
    """{chemin minuscule: (offset, taille brute, compressé)} pour une archive."""
    with open(path, 'rb') as f:
        d = f.read(36)
        magic, ver, off, aflags, nfold, nfile, lfold, lfile, fflags = struct.unpack('<4sIIIIIIIH', d[:34])
        if magic != b'BSA\0' or ver != 105: return {}
        f.seek(off)
        folders = [struct.unpack('<QII Q'.replace(' ', ''), f.read(24))[1] for _ in range(nfold)]
        recs = []
        for cnt in folders:
            name = ''
            if aflags & 1:
                l = f.read(1)[0]; name = f.read(l).rstrip(b'\0').decode('latin-1')
            for _ in range(cnt):
                _, size, foff = struct.unpack('<QII', f.read(16))
                recs.append((name, size, foff))
        names = f.read(lfile).split(b'\0') if aflags & 2 else []
    out = {}
    for (folder, size, foff), fn in zip(recs, names):
        comp = bool(aflags & 4) != bool(size & 0x40000000)
        out[(folder + '\\' + fn.decode('latin-1')).lower()] = (foff, size & 0x3FFFFFFF, comp, bool(aflags & 0x100))
    return out


def build_index(data_dir):
    idx = {}
    for p in sorted(glob.glob(os.path.join(data_dir, '*.bsa'))):
        try:
            for k, v in read_index(p).items(): idx.setdefault(k, (p, *v))
        except Exception:
            pass
    os.makedirs(os.path.dirname(INDEX), exist_ok=True)
    json.dump(idx, open(INDEX, 'w'))
    return idx


_idx = None


def extract_matching(prefix, suffix, data_dir):
    """Extrait tous les fichiers des BSA dont le chemin commence par prefix et finit par suffix."""
    global _idx
    if _idx is None:
        _idx = json.load(open(INDEX)) if os.path.isfile(INDEX) else build_index(data_dir)
    prefix, suffix = prefix.lower(), suffix.lower()
    return [find(k, data_dir) for k in _idx if k.startswith(prefix) and k.endswith(suffix)]


def find(rel, data_dir):
    """Extrait (une fois) le fichier relatif à Data depuis les BSA, renvoie son chemin, ou None."""
    global _idx
    rel = rel.replace('/', '\\').lower()
    out = os.path.join(FILES, rel)
    if os.path.isfile(out): return out
    if _idx is None:
        _idx = json.load(open(INDEX)) if os.path.isfile(INDEX) else build_index(data_dir)
    e = _idx.get(rel)
    if not e: return None
    archive, off, size, comp, embed = e
    with open(archive, 'rb') as f:
        f.seek(off)
        if embed:
            l = f.read(1)[0]; f.read(l); size -= 1 + l
        if comp:
            f.read(4); data = lz4.frame.decompress(f.read(size - 4))
        else:
            data = f.read(size)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, 'wb') as g: g.write(data)
    return out
