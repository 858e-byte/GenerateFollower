"""Shader de la tête d'un FaceGen (forme dont le slot FacegenDetail est rempli) : normal map, specular et flag
Model_Space_Normals. Lecture seule.

Usage : head_shader.py <facegen.nif>  ->  JSON {"normal": …, "specular": …, "msn": true|false} ou {} sans tête
"""
import sys, json
import assets  # noqa: F401  (chemin de PyNifly)
from pyn.pynifly import NifFile

MODEL_SPACE_NORMALS = 0x1000   # SLSF1
out = {}
for s in NifFile(sys.argv[1]).shapes:
    if s.shader and s.textures.get('FacegenDetail'):
        out = {'diffuse': s.textures.get('Diffuse', ''), 'normal': s.textures.get('Normal', ''), 'specular': s.textures.get('Specular', ''),
               'subsurface': s.textures.get('SoftLighting', ''),
               'msn': bool(s.shader.properties.Shader_Flags_1 & MODEL_SPACE_NORMALS)}
        break
print(json.dumps(out))
