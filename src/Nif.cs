using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

namespace GenerateFollower;

/// <summary>
/// Lecture d'un .nif avec NiflySharp (C#), comme PyNifly le faisait pour les scripts de offline\ : formes dans l'ordre
/// des blocs, sommets, triangles, UV, textures par slot (avec le préfixe « textures\ » de PyNifly), shader, alpha, os.
/// Lecture seule : les FaceGen livrés restent modifiés en binaire par NifPatcher, jamais réenregistrés.
/// </summary>
public sealed class Nif
{
    public NifFile File { get; }
    public List<NifShape> Shapes { get; }

    Nif(NifFile file)
    {
        File = file;
        Shapes = file.GetShapes().Select(s => new NifShape(this, s)).ToList();
    }

    /// <summary>Ouvre en lecture partagée (Vortex et MO2 peuvent déplacer le fichier pendant que le Builder tourne).</summary>
    public static Nif Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Load(fs, path);
    }

    public static Nif Load(Stream stream, string name)
    {
        var file = new NifFile();
        if (file.Load(stream, new NifFileLoadOptions()) != 0 || !file.Valid) throw new GfException($"NIF illisible : {name}");
        return new Nif(file);
    }

    /// <summary>Nœud nommé (os), ou null.</summary>
    public NiNode? Node(string name) => File.Blocks.OfType<NiNode>().FirstOrDefault(n => n.Name?.String == name);

    /// <summary>Transformation d'un nœud vers le repère du fichier (chaîne des parents, racine comprise).</summary>
    public Xform GlobalTransform(NiAVObject node)
    {
        var x = Xform.Of(node);
        for (var p = File.GetParentNode(node); p != null; p = File.GetParentNode(p)) x = Xform.Of(p).Then(x);
        return x;
    }
}

/// <summary>Transformation rotation × échelle + translation, en double (comme numpy dans les scripts).
/// R[i, j] : ligne i, colonne j, dans l'ordre de nifly (lignes = colonnes du Matrix33 de nif.xml).</summary>
public readonly record struct Xform(double[,] R, double S, double Tx, double Ty, double Tz)
{
    public static Xform Of(NiAVObject o) => From(o.Rotation, o.Scale, o.Translation);

    public static Xform Identity => new(new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }, 1, 0, 0, 0);

    public static Xform From(Matrix33 m, float scale, System.Numerics.Vector3 t) => new(new double[,]
    {
        { m.M11, m.M21, m.M31 },
        { m.M12, m.M22, m.M32 },
        { m.M13, m.M23, m.M33 },
    }, scale, t.X, t.Y, t.Z);

    /// <summary>this ∘ child : child appliquée d'abord (parent.global × enfant, comme nifly).</summary>
    public Xform Then(Xform c)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i, j] = R[i, 0] * c.R[0, j] + R[i, 1] * c.R[1, j] + R[i, 2] * c.R[2, j];
        var (x, y, z) = Rotate(c.Tx, c.Ty, c.Tz);
        return new Xform(r, S * c.S, x * S + Tx, y * S + Ty, z * S + Tz);
    }

    (double, double, double) Rotate(double x, double y, double z) =>
        (R[0, 0] * x + R[0, 1] * y + R[0, 2] * z, R[1, 0] * x + R[1, 1] * y + R[1, 2] * z, R[2, 0] * x + R[2, 1] * y + R[2, 2] * z);

    /// <summary>(v @ R.T) * s + t, comme export_mesh.apply.</summary>
    public (double X, double Y, double Z) Apply(double x, double y, double z)
    {
        var (a, b, c) = Rotate(x, y, z);
        return (a * S + Tx, b * S + Ty, c * S + Tz);
    }
}

/// <summary>Une forme du .nif, avec les propriétés que lisaient les scripts PyNifly.</summary>
public sealed class NifShape
{
    readonly Nif nif;
    public INiShape Shape { get; }
    public INiShader? Shader { get; }

    internal NifShape(Nif nif, INiShape shape)
    {
        this.nif = nif;
        Shape = shape;
        Shader = shape.HasShaderProperty ? nif.File.GetShader(shape) : null;
    }

    public string Name => (Shape as NiObjectNET)?.Name?.String ?? "";
    /// <summary>Type de bloc (BSTriShape, BSDynamicTriShape, NiTriShape…).</summary>
    public string Block => Shape.GetType().Name;
    public int VertexCount => Shape.VertexCount;

    /// <summary>Type de shader Skyrim (4 FaceGen, 6 Hair Tint…), 0 sans BSLightingShaderProperty (comme PyNifly).</summary>
    public int ShaderType => Shader is BSLightingShaderProperty l ? (int)l.ShaderType : 0;
    public uint ShaderFlags1 => Shader is BSShaderProperty ? (uint)(Shader.ShaderFlags_SSPF1) : 0;
    public bool ModelSpaceNormals => Shader is BSLightingShaderProperty && Shader.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Model_Space_Normals);

    public List<System.Numerics.Vector3> Verts => Shape switch
    {
        BSTriShape b => b.VertexPositions ?? [],
        NiGeometry g => g.GeometryData?.Vertices ?? [],
        _ => [],
    };

    public List<Triangle> Tris => Shape.Triangles ?? [];

    public List<TexCoord> UVs => Shape switch
    {
        BSTriShape b => b.UVs ?? [],
        NiGeometry g => g.GeometryData?.UVSets is { Count: > 0 } u ? u : [],
        _ => [],
    };

    // ------------------------------------------------------------------ textures

    Dictionary<string, string>? textures;

    /// <summary>
    /// Textures par nom de slot, mêmes règles que PyNifly (NiShader.textures) : Diffuse et Normal toujours, les autres
    /// selon les drapeaux du shader (SoftLighting, FacegenDetail, Specular…). Chemins de BSLightingShaderProperty
    /// préfixés par « textures\ » s'il manque (comme la DLL de PyNifly) ; BSEffectShaderProperty : chemins bruts.
    /// </summary>
    public IReadOnlyDictionary<string, string> Textures => textures ??= ReadTextures();

    /// <summary>Texture d'un slot, ou "" (comme textures.get(slot, '')).</summary>
    public string Texture(string slot) => Textures.TryGetValue(slot, out var t) ? t : "";

    Dictionary<string, string> ReadTextures() => TexturesFrom(Enumerable.Range(0, 9).Select(Slot).ToArray());

    /// <summary>Chemin d'un slot du BSShaderTextureSet comme la DLL de PyNifly le rend : « \\ » réduits à un seul
    /// (exports RaceMenu : « CharGen\\Asagi.dds »), préfixe « textures\ » ajouté s'il manque ; "" si absent.</summary>
    public string Slot(int i)
    {
        var set = Shader is BSLightingShaderProperty { HasTextureSet: true } l ? nif.File.GetBlock(l.TextureSetRef) as BSShaderTextureSet : null;
        return set?.Textures is { } list && i < list.Count ? NormSlot(list[i]?.Content ?? "") : "";
    }

    static string NormSlot(string raw)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(raw, @"\\{2,}", @"\");
        return s.Length == 0 || s.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? s : @"textures\" + s;
    }

    /// <summary>Textures par nom de slot pour des chemins de slots donnés (0 à 8), avec les drapeaux du shader de cette
    /// forme : ce que PyNifly lirait sur une copie de la forme dont on a changé les textures (tête générée).</summary>
    public Dictionary<string, string> TexturesFrom(string[] slots)
    {
        var t = new Dictionary<string, string>();
        if (Shader is BSLightingShaderProperty l)
        {
            string Slot(int i) => i < slots.Length ? NormSlot(slots[i] ?? "") : "";
            var f1 = l.ShaderFlags_SSPF1;
            var f2 = l.ShaderFlags_SSPF2;
            t["Diffuse"] = Slot(0);
            t["Normal"] = Slot(1);
            if (f2.HasFlag(SkyrimShaderPropertyFlags2.Glow_Map)) t["Glow"] = Slot(2);
            if (f2.HasFlag(SkyrimShaderPropertyFlags2.Rim_Lighting)) t["RimLighting"] = Slot(2);
            if (f2.HasFlag(SkyrimShaderPropertyFlags2.Soft_Lighting)) t["SoftLighting"] = Slot(2);
            if (f1.HasFlag(SkyrimShaderPropertyFlags1.Parallax)) t["HeightMap"] = Slot(3);
            if (f1.HasFlag(SkyrimShaderPropertyFlags1.Greyscale_To_PaletteColor)) t["Greyscale"] = Slot(3);
            if (f2.HasFlag(SkyrimShaderPropertyFlags2.EnvMap_Light_Fade)) t["EnvMap"] = Slot(4);
            if (f1.HasFlag(SkyrimShaderPropertyFlags1.Facegen_Detail_Map)) t["FacegenDetail"] = Slot(6);
            if (f1.HasFlag(SkyrimShaderPropertyFlags1.Environment_Mapping) || f1.HasFlag(SkyrimShaderPropertyFlags1.Eye_Environment_Mapping))
            {
                t["EnvMap"] = Slot(4);
                t["EnvMask"] = Slot(5);
            }
            if (f2.HasFlag(SkyrimShaderPropertyFlags2.Multi_Layer_Parallax)) t["InnerLayer"] = Slot(6);
            if (f1.HasFlag(SkyrimShaderPropertyFlags1.Model_Space_Normals) || f1.HasFlag(SkyrimShaderPropertyFlags1.Specular)) t["Specular"] = Slot(7);
        }
        else if (Shader is BSEffectShaderProperty e)
        {
            t["Diffuse"] = e.SourceTexture?.Content ?? "";
            t["Greyscale"] = e.GreyscaleTexture?.Content ?? "";
            t["EnvMap"] = e.EnvMapTexture?.Content ?? "";
            t["Normal"] = e.NormalTexture?.Content ?? "";
            t["EnvMapMask"] = e.EnvMaskTexture?.Content ?? "";
            t["EmitGradient"] = e.EmitGradientTexture?.Content ?? "";
        }
        return t;
    }

    // ------------------------------------------------------------------ alpha

    public NiAlphaProperty? Alpha => Shape.HasAlphaProperty ? nif.File.GetBlock(Shape.AlphaPropertyRef) : null;
    public ushort AlphaFlags => Alpha is { } a ? (ushort)a.Flags.Value : (ushort)0;

    // ------------------------------------------------------------------ os

    /// <summary>Noms des os du skin, dans l'ordre du skin (PyNifly bone_names).</summary>
    public List<string> BoneNames => nif.File.GetShapeBoneNames(Shape) ?? [];

    /// <summary>Transformation peau → os (BSSkin::BoneData ou NiSkinData), ou null.</summary>
    public Xform? SkinToBone(string bone)
    {
        int i = BoneNames.IndexOf(bone);
        if (i < 0 || !Shape.HasSkinInstance) return null;
        var skin = nif.File.GetBlock(Shape.SkinInstanceRef);
        if (skin is BSSkin_Instance bs && nif.File.GetBlock(bs.Data) is BSSkin_BoneData bd && i < bd.BoneList.Count)
            return Xform.From(bd.BoneList[i].Rotation, bd.BoneList[i].Scale, bd.BoneList[i].Translation);
        if (skin is NiSkinInstance ns && nif.File.GetBlock(ns.Data) is NiSkinData sd && i < sd.BoneList.Count)
        {
            var b = sd.BoneList[i].SkinTransform;
            return Xform.From(b.Rotation, b.Scale, b.Translation);
        }
        return null;
    }

    public Xform Transform => Xform.From(Shape.Rotation, Shape.Scale, Shape.Translation);
}
