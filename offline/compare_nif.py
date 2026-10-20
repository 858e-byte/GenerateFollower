"""Compare deux FaceGen .nif forme par forme : géométrie, skin, partitions, shader, textures, alpha."""
import sys
import numpy as np
import assets  # noqa: F401  (ajoute PyNifly au chemin)
from pyn.pynifly import NifFile


def info(s):
    sh = s.shader if s.properties.shaderPropertyID != 0xFFFFFFFF else None
    return dict(
        block=s.blockname, verts=np.array(s.verts), uvs=np.array(s.uvs), normals=np.array(s.normals) if s.normals else None,
        tris=np.array(s.tris), bones=list(s.unique_bone_names), weights=s.bone_weights,
        parts=[p.id for p in s.partitions], skin=s.skin_instance_name if hasattr(s, 'skin_instance_name') else '',
        shader=(sh.blockname, hex(sh.properties.Shader_Flags_1), hex(sh.properties.Shader_Flags_2), sh.properties.Shader_Type) if sh else None,
        tex={k: v.lower().replace('textures\\', '', 1) for k, v in (s.textures or {}).items() if v} if sh else {},
        alpha=s.alpha_property.properties.flags if s.alpha_property else None,
        g2s=np.array(s.global_to_skin.translation[:]), xform=np.array(s.transform.translation[:]),
    )


def compare(a_path, b_path):
    A = {s.name: s for s in NifFile(a_path).shapes}
    B = {s.name: s for s in NifFile(b_path).shapes}
    print(f'formes : réf {len(A)}  nous {len(B)}  manquantes {sorted(set(A) - set(B))}  en trop {sorted(set(B) - set(A))}')
    for name in A:
        if name not in B: continue
        a, b = info(A[name]), info(B[name])
        issues = []
        if a['block'] != b['block']: issues.append(f"type {a['block']} / {b['block']}")
        if len(a['verts']) != len(b['verts']):
            issues.append(f"sommets {len(a['verts'])} / {len(b['verts'])}")
        else:
            dv = np.abs(a['verts'] - b['verts']).max()
            du = np.abs(a['uvs'] - b['uvs']).max() if len(a['uvs']) == len(b['uvs']) else -1
            dn = np.abs(a['normals'] - b['normals']).max() if a['normals'] is not None and b['normals'] is not None else -1
            if dv > 0.01: issues.append(f'sommets max {dv:.3f}')
            if du > 1e-3: issues.append(f'UV max {du:.3f}')
            if dn > 0.05: issues.append(f'normales max {dn:.3f}')
            if a['tris'].shape != b['tris'].shape or (a['tris'] != b['tris']).any(): issues.append('triangles différents')
        if set(a['bones']) != set(b['bones']):
            issues.append(f"os réf-seuls {sorted(set(a['bones']) - set(b['bones']))[:4]} nous-seuls {sorted(set(b['bones']) - set(a['bones']))[:4]}")
        else:
            worst = 0
            for bn in a['bones']:
                wa = dict(a['weights'].get(bn, [])); wb = dict(b['weights'].get(bn, []))
                for v in set(wa) | set(wb): worst = max(worst, abs(wa.get(v, 0) - wb.get(v, 0)))
            if worst > 0.01: issues.append(f'poids max {worst:.3f}')
        if a['parts'] != b['parts']: issues.append(f"partitions {a['parts']} / {b['parts']}")
        if a['shader'] != b['shader']: issues.append(f"shader {a['shader']} / {b['shader']}")
        if a['tex'] != b['tex']:
            diff = {k: (a['tex'].get(k), b['tex'].get(k)) for k in set(a['tex']) | set(b['tex']) if a['tex'].get(k) != b['tex'].get(k)}
            issues.append(f'textures {diff}')
        if a['alpha'] != b['alpha']: issues.append(f"alpha {a['alpha']} / {b['alpha']}")
        if np.abs(a['g2s'] - b['g2s']).max() > 0.01 or np.abs(a['xform'] - b['xform']).max() > 0.01:
            issues.append(f"transfo forme {a['xform']}/{b['xform']} g2s {a['g2s']}/{b['g2s']}")
        print(f"{'OK ' if not issues else '!! '} {name:36} " + ' | '.join(issues))


if __name__ == '__main__':
    compare(sys.argv[1], sys.argv[2])
