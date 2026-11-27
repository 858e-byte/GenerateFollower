using Mutagen.Bethesda.Skyrim;
using NiflySharp.Blocks;

namespace GenerateFollower;

/// <summary>
/// Export RaceMenu abîmé (omidu789, RaceMenu 0.5.0 b7, 23/11) : les textures du visage ont été écrites aussi sur d'autres
/// pièces de la tête. Le gloss d'UBE est une copie exacte de la tête en mélange additif : avec la texture du visage (alpha
/// 255) à la place de lips_gloss.dds, presque transparente, le visage est dessiné deux fois (« deux textures de tête »,
/// tête d'une autre couleur que le corps). L'humidité des yeux (eyewetness.dds) est touchée de la même façon.
/// Réparation : une forme autre que la tête, qui porte la diffuse de la tête alors que sa pièce (EditorID = nom de la
/// forme) en a une autre, reprend les textures de sa pièce (TextureSet, sinon modèle). Le slot de teinte CharGen est gardé.
/// Aucun des 346 exports de l'utilisateur n'est dans ce cas. Seuls des chemins de textures changent (NifPatcher).
/// </summary>
public static class ExportRepair
{
    static string Bare(string? t)
    {
        var p = (t ?? "").Replace('/', '\\').Replace(@"\\", @"\").TrimStart('\\');
        return (p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? p[9..] : p).ToLowerInvariant();
    }

    static BSShaderTextureSet? Set(Nif nif, NifShape s) =>
        s.Shader is BSLightingShaderProperty { HasTextureSet: true } l ? nif.File.GetBlock(l.TextureSetRef) as BSShaderTextureSet : null;

    /// <summary>Formes abîmées de l'export → (bloc BSShaderTextureSet, textures d'origine par slot).</summary>
    static List<(string Shape, int Block, List<string> Textures)> Find(Context ctx, Nif nif)
    {
        var found = new List<(string, int, List<string>)>();
        // la tête : shader FaceGen avec la teinte, headpart Face (les yeux humides ont aussi le shader FaceGen)
        NifShape? head = null;
        var parts = new Dictionary<NifShape, IHeadPartGetter>();
        foreach (var s in nif.Shapes)
        {
            if (ctx.Find<IHeadPartGetter>(s.Name) is not { } hp) continue;
            parts[s] = hp;
            if (hp.Type == HeadPart.TypeEnum.Face) head ??= s;
        }
        if (head == null || Set(nif, head) is not { } headSet) return found;
        var face = Bare(head.Texture("Diffuse"));
        if (face.Length == 0) return found;
        foreach (var (s, hp) in parts)
        {
            if (s == head || hp.Type == HeadPart.TypeEnum.Face || Bare(s.Texture("Diffuse")) != face) continue;
            if (Set(nif, s) is not { } set || ReferenceEquals(set, headSet)) continue;
            var original = Original(ctx, hp);
            if (original == null || original.Count == 0 || Bare(original[0]).Length == 0 || Bare(original[0]) == face) continue;
            int block = nif.File.Blocks.IndexOf(set);
            if (block >= 0) found.Add((s.Name, block, original));
        }
        return found;
    }

    /// <summary>Textures d'origine d'une pièce par slot : son TextureSet, sinon celles de la forme de son modèle.</summary>
    static List<string>? Original(Context ctx, IHeadPartGetter hp)
    {
        if (!hp.TextureSet.IsNull && ctx.Cache.TryResolve<ITextureSetGetter>(hp.TextureSet.FormKey, out var t) && t.Diffuse != null)
        {
            string P(Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter? a) => a?.DataRelativePath.Path ?? "";
            // ordre des slots du .nif : diffuse, normale, masque / lueur, parallaxe, cubemap, masque d'environnement, multicouche, spéculaire
            return [P(t.Diffuse), P(t.NormalOrGloss), P(t.GlowOrDetailMap), P(t.Height), P(t.Environment), P(t.EnvironmentMaskOrSubsurfaceTint), P(t.Multilayer), P(t.BacklightMaskOrSpecular)];
        }
        if (hp.Model?.File?.DataRelativePath.Path is not { Length: > 0 } rel || ctx.FindMesh(rel) is not { } mesh) return null;
        try
        {
            var nif = Nif.Load(mesh);
            var shape = nif.Shapes.Count == 1 ? nif.Shapes[0] : nif.Shapes.FirstOrDefault(s => s.Name.Equals(hp.EditorID, StringComparison.OrdinalIgnoreCase)) ?? nif.Shapes.FirstOrDefault();
            return shape == null ? null : Set(nif, shape)?.Textures?.Select(x => x?.Content ?? "").ToList();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>Formes abîmées d'un export (noms) ; liste vide si l'export est sain ou illisible.</summary>
    public static List<string> Damaged(Context ctx, string exportPath)
    {
        try { return Find(ctx, Nif.Load(exportPath)).Select(f => f.Shape).ToList(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return []; }
    }

    /// <summary>Export réparé (mêmes octets si rien à réparer) et noms des formes réparées.</summary>
    public static byte[] Repair(Context ctx, byte[] export, out List<string> repaired)
    {
        repaired = [];
        List<(string Shape, int Block, List<string> Textures)> found;
        try { found = Find(ctx, Nif.Load(new MemoryStream(export), "export")); }
        catch (Exception e) when (e is not OutOfMemoryException) { return export; }
        if (found.Count == 0) return export;
        var result = NifPatcher.SetTextures(export, found.ToDictionary(f => f.Block, f => f.Textures));
        repaired = found.Select(f => f.Shape).ToList();
        return result;
    }
}
