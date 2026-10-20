using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Followers autonomes : comme les followers UBE livrés (Xiura, Alenye, UBE_3Followers), le plugin ne dépend
/// que du jeu de base et du mod qui fournit la race et la tête (UBE_AllRace.esp pour UBE, High Poly Head.esm
/// pour une race vanilla). Tout headpart (et TextureSet) venant d'un autre mod est recréé dans notre plugin,
/// valide pour la race, et ses fichiers sont copiés dans le mod au même chemin relatif.
/// </summary>
public class Standalone(Context ctx, SkyrimMod mod)
{
    public const string Prefix = "GF_";
    static readonly ModKey[] Vanilla = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    readonly Dictionary<FormKey, HeadPart> heads = [];
    readonly Dictionary<FormKey, TextureSet> textureSets = [];

    /// <summary>Fichiers (relatifs à Data) à copier dans le mod : modèles, .tri et textures des enregistrements recréés.</summary>
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plugins dont un follower peut dépendre : vanilla, plugin de la race, plugin de la tête, et leurs masters.</summary>
    public static HashSet<ModKey> Allowed(Context ctx, IRaceGetter? race, IHeadPartGetter? face)
    {
        var set = new HashSet<ModKey>(Vanilla);
        void Add(ModKey k)
        {
            if (!set.Add(k) && !Vanilla.Contains(k)) return;
            if (ctx.Env.LoadOrder.TryGetValue(k)?.Mod is { } m)
                foreach (var master in m.ModHeader.MasterReferences) Add(master.Master);
        }
        if (race != null) Add(race.FormKey.ModKey);
        if (face != null) Add(face.FormKey.ModKey);
        return set;
    }

    /// <summary>Headpart « Face » parmi les headparts effectifs d'un preset.</summary>
    public static IHeadPartGetter? Face(IEnumerable<IHeadPartGetter> parts) => parts.FirstOrDefault(p => p.Type == HeadPart.TypeEnum.Face);

    /// <summary>
    /// Headpart à recréer chez nous : seulement s'il vient d'un plugin non autorisé. Un headpart d'un plugin autorisé
    /// est gardé tel quel, même s'il n'est pas dans la liste de races valides : recréer FemaleMouthHumanoidDefault (la
    /// bouche par défaut des Hautes-Elfes, absente de HeadPartsHumansandVampires) faisait REJETER le FaceGen en jeu
    /// (Psynwen : bouche recréée → tête reconstruite, œil ouvert ; bouche d'origine → tête acceptée, test A/B du 19/10).
    /// </summary>
    public static bool MustCopy(Context ctx, IHeadPartGetter hp, HashSet<ModKey> allowed, IRaceGetter? race)
        => !allowed.Contains(hp.FormKey.ModKey);

    /// <summary>Nom du headpart dans notre plugin (et donc de sa forme dans le FaceGen).</summary>
    public static string Name(Context ctx, IHeadPartGetter hp, HashSet<ModKey> allowed, IRaceGetter? race)
        => MustCopy(ctx, hp, allowed, race) ? Prefix + hp.EditorID : hp.EditorID ?? "";

    /// <summary>Le headpart lui-même s'il vient d'un plugin autorisé et vaut pour la race, sinon notre copie (créée une
    /// seule fois).</summary>
    public IHeadPartGetter Localize(IHeadPartGetter hp, HashSet<ModKey> allowed, IFormLinkNullableGetter<IFormListGetter> validRaces, IRaceGetter? race)
    {
        if (!MustCopy(ctx, hp, allowed, race)) return hp;
        if (heads.TryGetValue(hp.FormKey, out var done)) return done;

        var copy = mod.HeadParts.DuplicateInAsNewRecord(hp, Prefix + hp.EditorID);
        heads[hp.FormKey] = copy;
        // Valide pour la race : même liste de races que la tête
        if (validRaces.IsNull) copy.ValidRaces.Clear(); else copy.ValidRaces.SetTo(validRaces.FormKey);
        if (!copy.Color.IsNull && !allowed.Contains(copy.Color.FormKey.ModKey)) copy.Color.Clear();
        if (!hp.TextureSet.IsNull && ctx.Cache.TryResolve<ITextureSetGetter>(hp.TextureSet.FormKey, out var ts))
            copy.TextureSet.SetTo(LocalizeTextureSet(ts, allowed));
        copy.ExtraParts.Clear();
        foreach (var e in hp.ExtraParts)
            if (ctx.Cache.TryResolve<IHeadPartGetter>(e.FormKey, out var x))
                copy.ExtraParts.Add(Localize(x, allowed, validRaces, race).ToLink());

        // Un headpart d'un plugin autorisé (recréé seulement pour la race) garde ses fichiers dans ce plugin
        if (allowed.Contains(hp.FormKey.ModKey)) return copy;
        if (hp.Model?.File is { } model) Files.Add(model.DataRelativePath.Path);
        foreach (var part in hp.Parts)
            if (part.FileName is { } f) Files.Add(f.DataRelativePath.Path);
        return copy;
    }

    /// <summary>TextureSet d'un plugin autorisé tel quel, sinon notre copie ; ses textures sont copiées dans le mod.</summary>
    public ITextureSetGetter LocalizeTextureSet(ITextureSetGetter ts, HashSet<ModKey> allowed)
    {
        if (allowed.Contains(ts.FormKey.ModKey)) return ts;
        if (textureSets.TryGetValue(ts.FormKey, out var done)) return done;
        var copy = mod.TextureSets.DuplicateInAsNewRecord(ts, Prefix + ts.EditorID);
        textureSets[ts.FormKey] = copy;
        foreach (var t in new[] { ts.Diffuse, ts.NormalOrGloss, ts.EnvironmentMaskOrSubsurfaceTint, ts.GlowOrDetailMap,
                     ts.Height, ts.Environment, ts.Multilayer, ts.BacklightMaskOrSpecular })
            if (t != null) Files.Add(t.DataRelativePath.Path);
        return copy;
    }

    /// <summary>
    /// Copie les fichiers collectés dans le mod (offline\copy_assets.py : loose ou BSA, plus les textures
    /// inscrites dans les .nif). Renvoie le rapport du script.
    /// </summary>
    public string CopyFiles(string modDir)
    {
        if (Files.Count == 0) return "aucun fichier à copier";
        var list = Path.Combine(ctx.WorkDir, "standalone_files.json");
        File.WriteAllText(list, System.Text.Json.JsonSerializer.Serialize(Files.OrderBy(f => f)));
        return Py.Run(ctx, "copy_assets.py", list, modDir);
    }
}
