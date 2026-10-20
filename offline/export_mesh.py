"""Exporte la géométrie d'un .nif en JSON pour l'aperçu 3D du Follower Builder.

Usage : export_mesh.py <fichier.nif> <sortie.json>
Sortie : {"shapes": [{"name", "block", "shader", "alpha", "diffuse", "tint", "verts", "tris", "uvs"}]}
Les sommets sont exprimés dans le repère du fichier (transformation de la forme appliquée).
"""
import sys, json
import numpy as np
import assets  # noqa: F401  (chemin de PyNifly)
from pyn.pynifly import NifFile


def apply(t, v):
    rot = np.array(t.rotation, dtype=np.float64).reshape(3, 3)
    return (v @ rot.T) * float(t.scale) + np.array(t.translation, dtype=np.float64)


def world(shape, nif):
    """Position dans le repère du fichier. Forme skinnée : par l'os de la tête (comme le squelette en jeu) ; certaines
    pièces n'ont pas de transformation propre et resteraient à l'origine (yeux de thorRg, 120 unités sous la tête)."""
    v = np.asarray(shape.verts, dtype=np.float64)
    try:
        bones = list(shape.bone_names or [])
        b = 'NPC Head [Head]' if 'NPC Head [Head]' in bones else (bones[0] if bones else None)
        node = nif.nodes.get(b) if b else None
        if node is not None:
            return apply(node.global_transform, apply(shape.get_shape_skin_to_bone(b), v))
    except Exception:
        pass
    try:
        return apply(shape.transform, v)
    except Exception:
        return v


src, out = sys.argv[1], sys.argv[2]
shapes = []
nif = NifFile(src)
for s in nif.shapes:
    if not s.verts or not s.tris:
        continue
    tex = s.textures if s.shader else {}
    # NiAlphaProperty : bit 0 = mélange (alpha de la texture), bit 9 = test (coupe sous le seuil)
    ap = s.alpha_property.properties if s.has_alpha_property else None
    shapes.append({
        "name": s.name,
        "block": s.blockname,
        "shader": s.shader.properties.Shader_Type if s.shader else None,
        "alpha": bool(s.has_alpha_property),
        "alphaBlend": bool(ap and ap.flags & 1),
        # facteurs de mélange (bits 1-4 source, 5-8 destination) : 0 ONE, 1 ZERO, 6 SRC_ALPHA, 7 INV_SRC_ALPHA…
        "blendSrc": (ap.flags >> 1) & 0xF if ap else 6,
        "blendDst": (ap.flags >> 5) & 0xF if ap else 7,
        "alphaTest": (ap.threshold / 255.0) if ap and ap.flags & 0x200 else 0,
        "diffuse": tex.get("Diffuse", ""),
        # teinte FaceGen (slot FacegenDetail) : couleur de peau du visage, appliquée par le shader FaceGen
        "tint": tex.get("FacegenDetail", ""),
        "verts": np.round(world(s, nif), 4).ravel().tolist(),
        "tris": np.asarray(s.tris, dtype=np.int64).ravel().tolist(),
        "uvs": np.round(np.asarray(s.uvs, dtype=np.float64), 5).ravel().tolist() if s.uvs else [],
    })
json.dump({"shapes": shapes}, open(out, "w", encoding="utf-8"))
print(f"{len(shapes)} formes")
