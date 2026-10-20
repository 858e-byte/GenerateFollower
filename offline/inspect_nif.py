import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gfpaths
gfpaths.add_pynifly()
from pyn.pynifly import NifFile
f=NifFile(sys.argv[1])
print('root',f.rootNode.blockname,repr(f.rootNode.name),'nodes',len(f.nodes))
for n,node in list(f.nodes.items())[:12]: print('  node',node.blockname,repr(n),'parent',getattr(node.parent,'name',None))
for s in f.shapes[:3]:
    print('shape',s.name,s.blockname,'parent',s.parent.name if s.parent else None)
    print('   bones',len(s.bone_names),s.bone_names[:6])
    print('   shader',s.shader.blockname if s.shader else None,'flags1',hex(s.shader.properties.Shader_Flags_1) if s.shader else '', 'type',s.shader.properties.Shader_Type if s.shader else '')
    print('   textures',s.textures)
    print('   alpha',s.has_alpha_property, ' partitions',[ (p.id) for p in s.partitions][:5])
    print('   extra', [(e.name, e.blockname) for e in (s.string_data if hasattr(s,'string_data') else [])][:3])
print('string extra data root', f.string_data if hasattr(f,'string_data') else None)
