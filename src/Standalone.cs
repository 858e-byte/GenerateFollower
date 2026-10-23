using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Followers autonomes : comme les followers UBE livrés (Xiura, Alenye, UBE_3Followers), le plugin ne dépend
/// que du jeu de base et du mod qui fournit la race et la tête (UBE_AllRace.esp pour UBE, High Poly Head.esm
/// pour une race vanilla). Tout headpart (et TextureSet) venant d'un autre mod est recréé dans notre plugin,
/// valide pour la race, et ses fichiers sont copiés dans le mod sous meshes\GenerateFollower\… et textures\GenerateFollower\…
/// (chemin d'origine en dessous) : au même chemin que le mod d'origine, le gestionnaire de mods signalait un conflit et nos
/// copies pouvaient remplacer les fichiers du mod de cheveux pour tous les PNJ (demande d'un utilisateur, 20/10). Les
/// chemins sont réécrits dans nos headparts et TextureSets, dans les .nif copiés et dans les FaceGen (CopyFiles,
/// TextureMap). Les fichiers du jeu, de la race et de la tête ne sont pas copiés et gardent leur chemin.
/// </summary>
public class Standalone(Context ctx, SkyrimMod mod)
{
    public const string Prefix = "GF_";

    /// <summary>Sous-dossier des fichiers copiés : meshes\GenerateFollower\…, textures\GenerateFollower\….</summary>
    public const string SubFolder = "GenerateFollower";

    /// <summary>Chemin relatif à Data sans « data\ » ni le dossier de premier niveau (meshes, textures).</summary>
    static string Under(string path, string top)
    {
        var p = path.Replace('/', '\\').TrimStart('\\');
        if (p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return p.StartsWith(top + "\\", StringComparison.OrdinalIgnoreCase) ? p[(top.Length + 1)..] : p;
    }

    /// <summary>
    /// Après la copie : un chemin passé sous le sous-dossier dans nos headparts ou TextureSets alors que le fichier n'a pas
    /// pu être copié (introuvable dans Data) revient au chemin d'origine. Renvoie le nombre de chemins remis.
    /// </summary>
    int RestoreMissing()
    {
        int n = 0;
        string? Back(Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter? link, string top)
        {
            var path = link == null ? null : Under(link.DataRelativePath.Path, top);
            if (path == null || !path.StartsWith(SubFolder + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            var rest = path[(SubFolder.Length + 1)..];
            if (Copied.Contains(top + "\\" + rest)) return null;
            n++;
            return rest;
        }
        foreach (var h in heads.Values)
        {
            if (h.Model?.File is { } m && Back(m, "meshes") is { } o) h.Model.File = o;
            foreach (var part in h.Parts)
                if (part.FileName is { } f && Back(f, "meshes") is { } of) part.FileName = of;
        }
        foreach (var t in textureSets.Values)
        {
            if (Back(t.Diffuse, "textures") is { } d) t.Diffuse = d;
            if (Back(t.NormalOrGloss, "textures") is { } nm) t.NormalOrGloss = nm;
            if (Back(t.EnvironmentMaskOrSubsurfaceTint, "textures") is { } e) t.EnvironmentMaskOrSubsurfaceTint = e;
            if (Back(t.GlowOrDetailMap, "textures") is { } g) t.GlowOrDetailMap = g;
            if (Back(t.Height, "textures") is { } h2) t.Height = h2;
            if (Back(t.Environment, "textures") is { } en) t.Environment = en;
            if (Back(t.Multilayer, "textures") is { } ml) t.Multilayer = ml;
            if (Back(t.BacklightMaskOrSpecular, "textures") is { } b) t.BacklightMaskOrSpecular = b;
        }
        return n;
    }

    /// <summary>Chemin d'un fichier copié, tel qu'on l'écrit dans un enregistrement (relatif à meshes ou textures).</summary>
    static string Moved(string path, string top) => SubFolder + "\\" + Under(path, top);

    /// <summary>Fichiers réellement copiés (chemins d'origine « meshes\… » / « textures\… »), connus après CopyFiles.</summary>
    public HashSet<string> Copied { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Nouveau chemin d'une texture inscrite dans un .nif si elle a été copiée dans le sous-dossier (null sinon) ; garde la
    /// forme d'origine (avec ou sans « textures\ »).
    /// </summary>
    public string? TextureMap(string tex)
    {
        var rest = Under(tex, "textures");
        if (!Copied.Contains("textures\\" + rest)) return null;
        bool withTop = tex.Replace('/', '\\').TrimStart('\\').StartsWith("textures\\", StringComparison.OrdinalIgnoreCase);
        return (withTop ? "textures\\" : "") + SubFolder + "\\" + rest;
    }
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
        if (hp.Model?.File is { } model)
        {
            Files.Add(model.DataRelativePath.Path);
            copy.Model!.File = Moved(model.DataRelativePath.Path, "meshes");
        }
        foreach (var part in copy.Parts)
            if (part.FileName is { } f)
            {
                Files.Add(f.DataRelativePath.Path);
                part.FileName = Moved(f.DataRelativePath.Path, "meshes");
            }
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
        // textures de notre copie : sous-dossier (copiées par CopyFiles)
        string? M(Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter? t) => t == null ? null : Moved(t.DataRelativePath.Path, "textures");
        if (M(ts.Diffuse) is { } d) copy.Diffuse = d;
        if (M(ts.NormalOrGloss) is { } n) copy.NormalOrGloss = n;
        if (M(ts.EnvironmentMaskOrSubsurfaceTint) is { } e) copy.EnvironmentMaskOrSubsurfaceTint = e;
        if (M(ts.GlowOrDetailMap) is { } g) copy.GlowOrDetailMap = g;
        if (M(ts.Height) is { } h) copy.Height = h;
        if (M(ts.Environment) is { } en) copy.Environment = en;
        if (M(ts.Multilayer) is { } ml) copy.Multilayer = ml;
        if (M(ts.BacklightMaskOrSpecular) is { } b) copy.BacklightMaskOrSpecular = b;
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
        var manifest = Path.Combine(ctx.WorkDir, "standalone_copied.json");
        File.WriteAllText(list, System.Text.Json.JsonSerializer.Serialize(Files.OrderBy(f => f)));
        if (File.Exists(manifest)) File.Delete(manifest);
        var report = Context.PythonEngine
            ? Py.Run(ctx, "copy_assets.py", list, modDir, "--sous-dossier", SubFolder, "--manifeste", manifest)
            : NativeTools.CopyAssets(ctx, Files.OrderBy(f => f), modDir, SubFolder, manifest);
        foreach (var f in System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(manifest)) ?? [])
            Copied.Add(f.Replace('/', '\\'));
        // .nif copiés : leurs chemins de textures vers nos copies (réécriture binaire des BSShaderTextureSet, comme pour la
        // teinte du FaceGen ; jamais réenregistrés par PyNifly)
        int nifs = 0, paths = 0;
        var failed = new List<string>();
        foreach (var nif in Copied.Where(f => f.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)))
        {
            var file = Path.Combine(modDir, "meshes", SubFolder, Under(nif, "meshes"));
            try
            {
                var bytes = NifPatcher.ReplaceTextures(File.ReadAllBytes(file), TextureMap, out var n);
                if (n > 0) { File.WriteAllBytes(file, bytes); nifs++; paths += n; }
            }
            catch (GfException e) { failed.Add($"{nif} ({e.Message})"); }
        }
        int restored = RestoreMissing();
        report = report.Trim() + $" ; sous {SubFolder}\\ : {paths} chemins de textures réécrits dans {nifs} .nif"
            + (restored > 0 ? $", {restored} chemins laissés au chemin d'origine (fichiers non copiés)" : "");
        if (failed.Count > 0) report += $"\nATTENTION .nif non réécrits (textures cherchées au chemin d'origine) : {string.Join(", ", failed)}";
        return report;
    }
}
