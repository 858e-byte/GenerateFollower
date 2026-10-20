"""Copie un FaceGen existant en y ajoutant le nœud marqueur (build --diag, témoin Alenye).

Usage : diag_marker.py <source.nif> <cible.nif>
"""
import sys
import assets  # noqa: F401  (chemin de PyNifly)
from pyn.pynifly import NifFile
from assemble import MARKER

src, dst = sys.argv[1], sys.argv[2]
f = NifFile(src)
f.filepath = dst
f.add_node(MARKER, f.rootNode.transform, f.nodes['BSFaceGenNiNodeSkinned'])
f.save()
