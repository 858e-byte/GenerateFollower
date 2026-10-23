"""Assemble un FaceGen .nif complet (comme RaceMenu ExportHead) à partir des headparts et de headgen.

Structure produite, identique à l'export RaceMenu :
  BSFadeNode ''  ->  NiNode 'BSFaceGenNiNodeSkinned'  ->  une forme par headpart (nommée par son EditorID)
  + les os (NiNode) utilisés par le skinning, sous la racine.
Usage : assemble.py <preset> <race> <fiche headparts.json> <sortie.nif> <chemin teinte (slot FacegenDetail)>
"""
import sys, os, json
from ctypes import create_string_buffer
import numpy as np
import headgen as hg
import assets
from pyn import pynifly
from pyn.pynifly import NifFile, NiAlphaProperty
from pyn.nifdefs import NODEID_NONE

def _skeleton_nodes(rel):
    p = assets.mesh(rel)
    return set(NifFile(p).nodes) if p else set()


_VANILLA = _skeleton_nodes(r"actors\character\character assets\skeleton.nif")
_FEMALE = _skeleton_nodes(r"actors\character\character assets female\skeleton_female.nif")
MERGE_INTO_HEAD = _FEMALE - _VANILLA - {'NPC Head [Head]'}

MARKER = 'GF_FaceGenMarker'

# TextureSet (TX00..TX07) -> slot du BSShaderTextureSet
TXST_TO_SLOT = [0, 1, 5, 2, 3, 4, 6, 7]


def read_slots(shape):
    out = []
    for i in range(9):
        buf = create_string_buffer(500)
        try:
            pynifly.nifly.getShaderTextureSlot(shape.file._handle, shape._handle, i, buf, 500)
        except Exception:
            break
        out.append(buf.value.decode('utf-8'))
    return out


def write_slots(shape, slots):
    for i, path in enumerate(slots):
        if path: pynifly.nifly.setShaderTextureSlot(shape.file._handle, shape._handle, i, path.encode('utf-8'))


def recompute_normals(V, T):
    """Normales par sommet pondérées par l'aire des triangles (au plus près de l'export RaceMenu)."""
    fn = np.cross(V[T[:, 1]] - V[T[:, 0]], V[T[:, 2]] - V[T[:, 0]])
    N = np.zeros_like(V)
    for k in range(3): np.add.at(N, T[:, k], fn)
    return N / np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-12)


def copy_shape(dst, parent, src, name, verts, txst=None):
    # Un FaceGen ne contient que des BSDynamicTriShape (exports RaceMenu et CK). Une seule forme statique
    # (BSTriShape de cheveux ou de collision SMP) suffit à ce que le jeu ignore le FaceGen et
    # reconstruise la tête par défaut : comme RaceMenu, on convertit tout le reste.
    dynamic = src.blockname == 'BSDynamicTriShape'
    if dynamic:
        props = src.properties.copy()
        props.nameID = props.controllerID = props.collisionID = NODEID_NONE
        props.skinInstanceID = props.shaderPropertyID = props.alphaPropertyID = NODEID_NONE
        props.extraDataCount = 0
    else:
        props = pynifly.NiObject.block_types['BSDynamicTriShape'].getbuf()
        props.flags = src.properties.flags
    base = np.array(src.verts)
    normals = src.normals
    if normals and np.abs(verts - base).max() > 1e-4:
        normals = [tuple(n) for n in recompute_normals(np.asarray(verts, float), np.array(src.tris))]
    new = dst.createShapeFromData(name, [tuple(v) for v in verts], src.tris, src.uvs, normals,
                                  props=props, parent=parent)
    if dynamic:
        new.properties.vertexDesc = src.properties.vertexDesc
        new.write_properties()
    new.name = name
    if src.colors:
        new.set_colors(src.colors)

    # Shader + textures
    if src.properties.shaderPropertyID != NODEID_NONE:
        new.shader._properties = src.shader.properties.copy()
        new.shader.name = src.shader.name or ''
        new.save_shader_attributes()
        slots = read_slots(src)
        if txst:   # le TextureSet du headpart remplace les textures du .nif (slots non vides)
            slots += [''] * (9 - len(slots))
            for i, path in enumerate(txst):
                if path: slots[TXST_TO_SLOT[i]] = path
        write_slots(new, slots)
    # Alpha
    if src.properties.alphaPropertyID != NODEID_NONE:
        new._alpha = NiAlphaProperty(file=dst, parent=new)
        new._alpha._properties = src.alpha_property.properties.copy()
        new.save_alpha_property()

    # Skin : mêmes os, mêmes transformations, mêmes poids, mêmes partitions
    weights = {b: list(w) for b, w in src.bone_weights.items()}
    # Os absents du squelette vanilla, reversés dans NPC Head :
    # - os du squelette féminin (ex. MouthU/D/L/R d'UBE), comme RaceMenu et les FaceGen UBE livrés ;
    # - os de physique des cheveux SMP (« Sparklers L2 »…) : ils n'existent pas sur un PNJ, et les sommets
    #   qui en dépendent partaient à l'origine (mèches étirées en jeu). Les cheveux suivent la tête, sans physique.
    head = 'NPC Head [Head]'
    for b in list(weights):
        if b != head and (b in MERGE_INTO_HEAD or b not in _VANILLA):
            acc = dict(weights.get(head, []))
            for v, w in weights.pop(b): acc[v] = acc.get(v, 0) + w
            weights[head] = sorted(acc.items())
    # RaceMenu ne garde que les os qui portent un poids
    bones = [b for b in src.unique_bone_names if any(w > 0 for _, w in weights.get(b, []))]
    if head in weights and head not in bones and weights[head]: bones.append(head)
    if bones:
        new.skin()
        new.transform = src.transform
        new.set_global_to_skin(src.global_to_skin)
        for b in bones:
            node = src.file.nodes.get(b)
            new.add_bone(b, node.transform if node is not None else None)
        for b in bones:
            xf = src.get_shape_skin_to_bone(b)
            if xf is not None: new.set_skin_to_bone_xform(b, xf)
            new.setShapeWeights(b, weights.get(b, []))
        parts = src.partitions
        if parts:
            new.set_partitions(parts, src.partition_tris)
        else:
            new.demote_skin_instance()
    else:
        new.transform = src.transform
    return new


def assemble(preset, race, hp_json, out_path, tint_path):
    ctx = hg.Ctx()
    j = json.load(open(assets.chargen(rf"Presets\{preset}.jslot"), encoding='utf-8'))
    fiche = json.load(open(hp_json, encoding='utf-8-sig'))
    hps = fiche['headParts']
    # Tête RaceMenu compagnon (« Import Head ») : ses formes remplacent la géométrie calculée depuis le jslot,
    # quand le nom (EditorID d'origine) et le nombre de sommets correspondent
    imported = {}
    if fiche.get('import'):
        imported = {s.name.lower(): np.array(s.verts) for s in NifFile(fiche['import']).shapes}
    dst = NifFile()
    dst.initialize('SKYRIMSE', out_path, root_type='BSFadeNode', root_name='')
    fg = dst.add_node('BSFaceGenNiNodeSkinned', dst.rootNode.transform, dst.rootNode)
    # Plus de nœud GF_FaceGenMarker sous BSFaceGenNiNodeSkinned : le moteur compare les enfants de ce nœud
    # aux headparts du PNJ, et ce nœud en trop est le premier suspect du rejet de toutes nos têtes générées
    # (tests 7 à 9 ; les exports RaceMenu, sans marqueur, sont acceptés).
    # GF_EXPORT_FLAGS=<ids> : flags des nœuds comme l'export RaceMenu (test 10, moitié du lot)
    export_flags = os.path.basename(out_path)[3:-4] in os.environ.get('GF_EXPORT_FLAGS', '').split(',')
    report = []
    for hp in hps:
        if not hp.get('model'): continue
        path = assets.mesh(hp['model'])
        if not path:
            report.append(f"{hp['edid']}: modèle introuvable {hp['model']}"); continue
        src_file = NifFile(path)
        src = src_file.shapes[0]
        r = hg.morph_part(ctx, hp, j, race, {})
        verts = r[1] if r else np.array(src.verts)
        imp = imported.get((hp.get('srcEdid') or hp['edid']).lower())
        if imp is not None and len(imp) == len(verts):
            verts = imp
            report.append(f"{hp['edid']}: géométrie importée de {os.path.basename(fiche['import'])}")
        new = copy_shape(dst, fg, src, hp['edid'], verts, hp.get('textureSet'))
        if hp['hpType'] == 'Face':
            pynifly.nifly.setShaderTextureSlot(dst._handle, new._handle, 6, tint_path.encode('utf-8'))
        report.append(f"{hp['edid']}: {len(verts)} sommets, {len(src.unique_bone_names)} os")
    if export_flags:
        # Export RaceMenu : racine 14, formes = flags du headpart + bit 26
        dst.rootNode.properties.flags = 14
        dst.rootNode.write_properties()
        for s in dst.shapes:
            s.properties.flags |= 0x4000000
            s.write_properties()
        report.append('flags export RaceMenu')
    dst.save()
    return report


if __name__ == '__main__':
    for line in assemble(*sys.argv[1:6]): print(line)
