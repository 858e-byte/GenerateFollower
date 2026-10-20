"""Liste les blocs d'un NIF Skyrim SE (20.2.0.7) : types, tailles, chaînes de l'en-tête."""
import struct, sys, collections


def blocks(path):
    d = open(path, 'rb').read()
    p = d.index(b'\n') + 1 + 5
    uv, nb, bs = struct.unpack_from('<III', d, p); p += 12
    for _ in range(3 if bs < 103 else 4): p += 1 + d[p]
    nt = struct.unpack_from('<H', d, p)[0]; p += 2
    types = []
    for _ in range(nt):
        l = struct.unpack_from('<I', d, p)[0]; types.append(d[p + 4:p + 4 + l].decode()); p += 4 + l
    idx = struct.unpack_from('<%dH' % nb, d, p); p += 2 * nb
    sizes = struct.unpack_from('<%dI' % nb, d, p); p += 4 * nb
    ns = struct.unpack_from('<I', d, p)[0]; p += 8
    strings = []
    for _ in range(ns):
        l = struct.unpack_from('<I', d, p)[0]; strings.append(d[p + 4:p + 4 + l].decode('latin-1')); p += 4 + l
    return [types[i & 0x7fff] for i in idx], sizes, strings


if __name__ == '__main__':
    t, s, strs = blocks(sys.argv[1])
    print(f'{len(t)} blocs, {sum(s)} octets')
    for k, v in collections.Counter(t).most_common(): print(f'  {v:4} {k}')
    if '--strings' in sys.argv: print(strs)
