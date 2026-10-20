"""Lecteur de fichiers FaceGen TRI (FRTRI003) utilisés par Skyrim pour les morphs de tête."""
import struct
import numpy as np


class Tri:
    def __init__(self, path):
        d = open(path, 'rb').read()
        if d[:8] != b'FRTRI003':
            raise ValueError(f'{path}: pas un FRTRI003 ({d[:8]!r})')
        (self.nverts, self.ntris, self.nquads, self.nlv, self.nls, self.nuv,
         self.ext, self.nmorphs, self.nmods, self.nmodverts) = struct.unpack_from('<10i', d, 8)
        p = 64
        self.verts = np.frombuffer(d, '<f4', self.nverts * 3, p).reshape(-1, 3).astype(np.float64); p += self.nverts * 12
        self.modverts = np.frombuffer(d, '<f4', self.nmodverts * 3, p).reshape(-1, 3).astype(np.float64); p += self.nmodverts * 12
        self.tris = np.frombuffer(d, '<i4', self.ntris * 3, p).reshape(-1, 3); p += self.ntris * 12
        p += self.nquads * 16
        p += self.nlv * 4 + self.nls * 16          # libellés (non utilisés par Skyrim)
        if self.ext & 1:
            p += self.nuv * 8 + self.ntris * 12 + self.nquads * 16
        # Morphs différentiels : nom + échelle + deltas int16
        self.morphs = {}
        for _ in range(self.nmorphs):
            n = struct.unpack_from('<i', d, p)[0]; p += 4
            name = d[p:p + n].rstrip(b'\0').decode('latin-1'); p += n
            scale = struct.unpack_from('<f', d, p)[0]; p += 4
            delta = np.frombuffer(d, '<i2', self.nverts * 3, p).reshape(-1, 3).astype(np.float64) * scale
            p += self.nverts * 6
            self.morphs[name] = delta
        # Modificateurs : liste d'indices, positions prises dans modverts dans l'ordre
        self.mods = {}
        k = 0
        for _ in range(self.nmods):
            n = struct.unpack_from('<i', d, p)[0]; p += 4
            name = d[p:p + n].rstrip(b'\0').decode('latin-1'); p += n
            cnt = struct.unpack_from('<i', d, p)[0]; p += 4
            idx = np.frombuffer(d, '<i4', cnt, p); p += cnt * 4
            self.mods[name] = (idx, self.modverts[k:k + cnt]); k += cnt
        self.consumed = p
        self.size = len(d)

    def summary(self):
        return (f'verts={self.nverts} tris={self.ntris} quads={self.nquads} uv={self.nuv} ext={self.ext} '
                f'morphs={self.nmorphs} mods={self.nmods} lu={self.consumed}/{self.size}')
