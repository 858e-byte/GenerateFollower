using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace GenerateFollower;

/// <summary>
/// Skins (textures de peau du corps, des mains, des pieds et du visage) choisis par follower dans le Builder.
/// Catalogue : offline\skins_index.py → work\skins_index.json (mods de skin du gestionnaire, activés ou non, et archives du
/// dossier des Réglages, listées avec 7-Zip). Choix : work\skins_choice.json { "id du preset": "id du skin" }.
/// Au build : textures copiées dans le mod (réduites à 4K), TextureSets, ArmorAddons et Armor de peau propres au
/// follower (champ WNAM : les autres PNJ et le joueur ne changent pas), diffuse du visage remplacée dans le FaceGen.
/// </summary>
public class Skins
{
    public record Skin(string Id, string Name, string Body, string Sex, string Source, string Path, string Prefix,
        Dictionary<string, string> Files, Dictionary<string, long> Sizes);

    public const int MaxSize = 4096;
    readonly Context ctx;
    string SevenZip => ctx.Settings.ResolvedSevenZip();
    public List<Skin> All { get; }

    Skins(Context ctx, List<Skin> all) { this.ctx = ctx; All = all; }

    static string IndexPath(Context ctx) => System.IO.Path.Combine(ctx.WorkDir, "skins_index.json");
    static string ChoicePath(Context ctx) => System.IO.Path.Combine(ctx.WorkDir, "skins_choice.json");
    static string ExcludePath(Context ctx) => System.IO.Path.Combine(ctx.WorkDir, "skins_exclus.txt");

    /// <summary>Charge le catalogue ; <paramref name="refresh"/> le refait (archives en cache : quelques secondes).</summary>
    public static Skins Load(Context ctx, bool refresh = false)
    {
        if (refresh || !File.Exists(IndexPath(ctx)))
        {
            ModIndex.Load(ctx, refresh: true);    // work\mods_index.json à jour (incrémental : skins installés depuis)
            // sans dossier d'archives ou sans 7-Zip, seuls les mods installés sont lus
            // familles de corps installées : seuls les skins de ces corps sont gardés (les UV diffèrent)
            Py.Run(ctx, "skins_index.py", ModIndex.IndexPath(ctx), ModIndex.ModsDir(ctx),
                ctx.Settings.ResolvedArchivesDir(), ctx.Settings.ResolvedSevenZip(), IndexPath(ctx), ctx.Bodies.Female, ctx.Bodies.Male);
        }
        var list = new List<Skin>();
        // skins écartés par l'utilisateur : work\skins_exclus.txt, un morceau de nom par ligne (« The New Gentleman »)
        var exclus = File.Exists(ExcludePath(ctx))
            ? File.ReadAllLines(ExcludePath(ctx)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList() : [];
        foreach (var s in JsonNode.Parse(File.ReadAllText(IndexPath(ctx)))!["skins"]!.AsArray())
            if (!exclus.Any(x => s!["name"]!.GetValue<string>().Contains(x, StringComparison.OrdinalIgnoreCase)))
            list.Add(new Skin(s!["id"]!.GetValue<string>(), s["name"]!.GetValue<string>(), s["body"]!.GetValue<string>(),
                s["sex"]!.GetValue<string>(), s["source"]!.GetValue<string>(), s["path"]!.GetValue<string>(), s["prefix"]?.GetValue<string>() ?? "",
                s["files"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()),
                s["sizes"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<long>() ?? 0)));
        return new Skins(ctx, list);
    }

    /// <summary>Type de corps du follower : UBE (race 00UBE_*), sinon la famille du corps installé (voir Bodies) :
    /// CBBE, UNP ou VANILLA pour les femmes, HIMBO ou MALE pour les hommes.</summary>
    public static string BodyOf(Context ctx, PresetRow row) =>
        row.Race.StartsWith("00UBE_", StringComparison.OrdinalIgnoreCase) ? "UBE" : row.Female ? ctx.Bodies.Female : ctx.Bodies.Male;

    public static string BodyLabel(string body) => Bodies.Label(body);

    /// <summary>Skins compatibles avec le corps et le sexe du follower (les UV diffèrent d'un corps à l'autre).</summary>
    public IEnumerable<Skin> For(PresetRow row) => All.Where(s => s.Body == BodyOf(ctx, row) && s.Sex == row.Sexe);

    public Skin? Get(string? id) => id == null ? null : All.FirstOrDefault(s => s.Id == id);

    public static Dictionary<string, string> LoadChoices(Context ctx) =>
        File.Exists(ChoicePath(ctx))
            ? new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ChoicePath(ctx)))!, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mémorise le skin d'un preset (null : peau du jeu).</summary>
    public static void SaveChoice(Context ctx, string presetId, string? skinId)
    {
        var choices = LoadChoices(ctx);
        if (string.IsNullOrEmpty(skinId)) choices.Remove(presetId); else choices[presetId] = skinId;
        File.WriteAllText(ChoicePath(ctx), JsonSerializer.Serialize(choices, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ fichiers

    /// <summary>
    /// Fichier prêt à livrer pour un rôle du skin (body_d, head_n…) : lu dans le dossier du mod, ou extrait de l'archive
    /// (work\archives\skins\&lt;id&gt;\), puis réduit à 4K s'il est plus grand (work\skins_cache\&lt;id&gt;\, gardé pour les
    /// builds suivants). Null si le skin n'a pas ce rôle.
    /// </summary>
    public string? File4K(Skin s, string role)
    {
        if (!s.Files.TryGetValue(role, out var inner)) return null;
        string src;
        if (s.Source is "mod" or "vortex") src = System.IO.Path.Combine(s.Path, inner);
        else
        {
            var dir = Context.Dir(System.IO.Path.Combine(ctx.WorkDir, "archives", "skins", s.Id));
            src = System.IO.Path.Combine(dir, inner);
            if (!File.Exists(src))
            {
                if (!File.Exists(SevenZip)) throw new GfException("7-Zip introuvable : indique 7z.exe dans les Réglages du Builder (skin pris dans une archive).");
                var psi = new ProcessStartInfo(SevenZip) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "x", "-y", "-sccUTF-8", "-o" + dir, s.Path, inner }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!File.Exists(src)) throw new GfException($"extraction impossible : {inner} de {s.Path}\n{output}");
            }
        }
        if (!File.Exists(src)) throw new GfException($"texture du skin introuvable : {src}");
        var (w, h) = DdsSize(src);
        if (Math.Max(w, h) <= MaxSize) return src;

        var cacheDir = Context.Dir(System.IO.Path.Combine(ctx.WorkDir, "skins_cache", s.Id));
        var dst = System.IO.Path.Combine(cacheDir, role + ".dds");
        if (File.Exists(dst) && File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(src)) return dst;
        // copie au nom du rôle (texconv garde le nom d'entrée), puis réduction en deux passes, même format :
        // texconv échoue (« FAILED [mipmaps] ») s'il réduit et refait les mipmaps d'un BC7 8K en une fois (SoulFrost)
        var tmpDir = Context.Dir(System.IO.Path.Combine(cacheDir, "tmp"));
        var tmp = System.IO.Path.Combine(tmpDir, role + ".dds");
        File.Copy(src, tmp, true);
        double f = (double)MaxSize / Math.Max(w, h);
        var small = Texconv(src, tmp, Context.Dir(System.IO.Path.Combine(tmpDir, "4k")), role, "-w", ((int)(w * f)).ToString(), "-h", ((int)(h * f)).ToString(), "-m", "1");
        var mipped = Texconv(src, small, cacheDir, role, "-m", "0");
        File.Delete(tmp);
        File.Delete(small);
        if (!mipped.Equals(dst, StringComparison.Ordinal)) File.Move(mipped, dst, true);
        return dst;
    }

    /// <summary>texconv sur <paramref name="input"/> vers <paramref name="outDir"/> ; renvoie le fichier écrit.</summary>
    string Texconv(string original, string input, string outDir, string role, params string[] options)
    {
        var tc = new ProcessStartInfo(Packaging.Texconv(ctx)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-nologo", "-y" }.Concat(options).Concat(["-o", outDir, input])) tc.ArgumentList.Add(a);
        using var proc = Process.Start(tc)!;
        var log = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        var written = Directory.EnumerateFiles(outDir, role + ".*").FirstOrDefault(x => x.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
        if (proc.ExitCode != 0 || written == null) throw new GfException($"texconv a échoué sur {original} :\n{log}");
        return written;
    }

    /// <summary>Largeur et hauteur d'un .dds (en-tête).</summary>
    public static (int W, int H) DdsSize(string path)
    {
        using var fs = File.OpenRead(path);
        var b = new byte[20];
        fs.ReadExactly(b);
        return (BitConverter.ToInt32(b, 16), BitConverter.ToInt32(b, 12));
    }

    // ------------------------------------------------------------------ build

    // diffuse générique des formes de peau, par corps : seules ces formes reçoivent le skin (pas les parties
    // génitales ni les accessoires d'un mesh de corps)
    static readonly Dictionary<string, (string Body, string? Hands)> BaseDiffuse = new()
    {
        ["UBE F"] = ("femalebody_1_d.dds", null), ["UBE M"] = ("malebody_1_d.dds", null),
        ["CBBE F"] = ("femalebody_1.dds", "femalehands_1.dds"), ["HIMBO M"] = ("malebody_1.dds", "malehands_1.dds"),
        // autres corps (non testés en jeu) : mêmes chemins de textures que CBBE / HIMBO
        ["UNP F"] = ("femalebody_1.dds", "femalehands_1.dds"), ["VANILLA F"] = ("femalebody_1.dds", "femalehands_1.dds"),
        ["MALE M"] = ("malebody_1.dds", "malehands_1.dds"),
    };

    readonly Dictionary<string, TextureSet> textureSets = [];
    readonly Dictionary<string, ArmorAddon> addons = [];
    readonly Dictionary<string, Armor> armors = [];
    readonly Dictionary<string, JsonArray?> shapes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dossier du skin dans le mod (sous textures\), commun à tous les followers qui le portent.</summary>
    static string Folder(Skin s)
    {
        var clean = new string(s.Name.Select(ch => char.IsLetterOrDigit(ch) && ch < 128 ? ch : '_').ToArray());
        while (clean.Contains("__")) clean = clean.Replace("__", "_");
        clean = clean.Trim('_');
        if (clean.Length > 40) clean = clean[..40].TrimEnd('_');
        return $@"GenerateFollower\Skins\{clean}_{s.Id[..6]}";
    }

    /// <summary>Copie un rôle du skin dans le mod ; renvoie son chemin depuis textures\ (null si absent du skin).</summary>
    string? Deliver(Skin s, string role, string modDir, StringBuilder report)
    {
        var file = File4K(s, role);
        if (file == null) return null;
        var rel = $@"{Folder(s)}\{role}.dds";
        var dst = System.IO.Path.Combine(modDir, "textures", rel);
        if (!File.Exists(dst))
        {
            Context.Dir(System.IO.Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, true);
            var (w, h) = DdsSize(dst);
            report.AppendLine($"SKIN    {s.Name} : {role} {w}x{h}{(file.Contains(@"\skins_cache\") ? " (réduit à 4K)" : "")}");
        }
        return rel;
    }

    JsonArray? Shapes(string mesh)
    {
        if (shapes.TryGetValue(mesh, out var known)) return known;
        var list = System.IO.Path.Combine(ctx.WorkDir, "nif_shapes.json");
        File.WriteAllText(list, JsonSerializer.Serialize(new[] { mesh }));
        var res = JsonNode.Parse(Py.Run(ctx, "nif_shapes.py", list))![mesh]?.AsArray();
        shapes[mesh] = res;
        return res;
    }

    static string Bare(string? t)
    {
        var p = (t ?? "").Replace('/', '\\').TrimStart('\\');
        return p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? p[9..] : p;
    }

    /// <summary>TextureSet du skin pour une partie (body, hands, head) : rôles du skin, sinon textures d'origine.</summary>
    TextureSet Txst(SkyrimMod mod, Skin s, string part, string modDir, JsonNode origin, StringBuilder report)
    {
        var key = s.Id + "|" + part;
        if (textureSets.TryGetValue(key, out var done)) return done;
        var t = mod.TextureSets.AddNew($"GF_Skin_{s.Id[..6]}_{part}");
        string Pick(string role, string originKey) => Deliver(s, $"{part}_{role}", modDir, report) ?? Bare(origin[originKey]?.GetValue<string>());
        t.Diffuse = Pick("d", "diffuse");
        if (Pick("n", "normal") is { Length: > 0 } n) t.NormalOrGloss = n;
        // _sk : TX03 (comme les TextureSets de peau d'Alenye, Mel Medarda, des têtes UBE)
        if (Pick("sk", "subsurface") is { Length: > 0 } sk) t.GlowOrDetailMap = sk;
        if (Pick("s", "specular") is { Length: > 0 } sp) t.BacklightMaskOrSpecular = sp;
        // Drapeaux des TextureSets de peau du jeu et des followers (SkinBodyFemale_1, Mel Medarda, Alenye…) :
        // FaceGenTextures = la teinte de peau du PNJ (QNAM) s'applique ; HasModelSpaceNormalMap pour une normal map _msn.
        // Sans eux (test GF_Skins_CBBE du 19/10) : corps 3BA blanc bleuté, sans teinte, normal map _msn lue comme tangente.
        t.Flags = TextureSet.Flag.FaceGenTextures;
        // type de normal map = celui de la forme d'origine (nos fichiers s'appellent body_n.dds) : 3BA/HIMBO et têtes HPH
        // en _msn, UBE en tangent (_n)
        if (origin["normal"]?.GetValue<string>()?.EndsWith("_msn.dds", StringComparison.OrdinalIgnoreCase) == true)
            t.Flags |= TextureSet.Flag.HasModelSpaceNormalMap;
        textureSets[key] = t;
        return t;
    }

    /// <summary>
    /// Donne le skin au PNJ : Armor de peau (WNAM) = copie de la peau de sa race, où les ArmorAddons du corps, des mains
    /// et des pieds (même mesh installé : OBody garde la main) ont la TextureSet du skin (SkinTexture + textures
    /// alternatives) ; visage : FaceGen et TextureSet de tête (FTST) vers les textures du skin.
    /// </summary>
    public void Apply(SkyrimMod mod, Npc npc, PresetRow row, Skin s, string modDir, string? faceGen, StringBuilder report)
    {
        var race = ctx.Need<IRaceGetter>(row.Race, "race");
        if (s.Body != BodyOf(ctx, row) || s.Sex != row.Sexe)
        {
            report.AppendLine($"ATTENTION {row.Id}  skin {s.Name} ({s.Body} {s.Sex}) incompatible avec le corps {BodyOf(ctx, row)} {row.Sexe} : ignoré");
            return;
        }
        var allowed = Standalone.Allowed(ctx, race, null);
        var bases = BaseDiffuse[s.Body + " " + s.Sex];

        // ---- corps, mains, pieds
        if (!ctx.Cache.TryResolve<IArmorGetter>(race.Skin.FormKey, out var raceSkin))
            report.AppendLine($"ATTENTION {row.Id}  peau de la race {race.EditorID} introuvable : corps non modifié");
        else
        {
            var armoKey = $"{s.Id}|{race.FormKey}|{row.Sexe}";
            if (!armors.TryGetValue(armoKey, out var armo))
            {
                armo = mod.Armors.DuplicateInAsNewRecord(raceSkin, $"GF_Skin_{s.Id[..6]}_{race.EditorID}_{row.Sexe}");
                int replaced = 0;
                var dropped = new List<string>();
                for (int i = armo.Armature.Count - 1; i >= 0; i--)
                {
                    var link = armo.Armature[i];
                    if (!allowed.Contains(link.FormKey.ModKey)) { dropped.Add(link.FormKey.ToString()); armo.Armature.RemoveAt(i); continue; }
                    if (!ctx.Cache.TryResolve<IArmorAddonGetter>(link.FormKey, out var aa)) continue;
                    if (SkinAddon(mod, aa, race, row, s, bases, modDir, report) is { } copy) { armo.Armature[i] = copy.ToLink(); replaced++; }
                }
                armo.Keywords?.RemoveWhere(k => !allowed.Contains(k.FormKey.ModKey));
                // références restantes hors du jeu et de la race (follower autonome) : signalées
                var foreign = armo.EnumerateFormLinks().Concat(addons.Values.SelectMany(a => a.EnumerateFormLinks()))
                    .Where(l => !l.IsNull && l.FormKey.ModKey != mod.ModKey && !allowed.Contains(l.FormKey.ModKey))
                    .Select(l => l.FormKey.ModKey.FileName.String).Distinct().ToList();
                if (foreign.Count > 0) report.AppendLine($"ATTENTION {row.Id}  la peau dépend encore de : {string.Join(", ", foreign)}");
                if (dropped.Count > 0) report.AppendLine($"SKIN    {row.Id}  ArmorAddons d'autres mods retirés de la peau : {string.Join(", ", dropped)}");
                if (replaced == 0) report.AppendLine($"ATTENTION {row.Id}  aucune pièce de corps reconnue dans {raceSkin.EditorID} : corps non modifié");
                armors[armoKey] = armo;
            }
            npc.WornArmor.SetTo(armo);
        }

        // ---- visage
        // diagnostic (tête rejetée au test GF_Skins_Test) : GF_SKIN_NOFACE=<ids> -> visage non modifié (corps seulement) ;
        // GF_SKIN_NOFTST=<ids> -> FaceGen modifié, mais pas la TextureSet de tête (FTST) du PNJ
        static bool In(string env, string id) => (Environment.GetEnvironmentVariable(env) ?? "").Split(',').Contains(id, StringComparer.OrdinalIgnoreCase);
        bool noFtst = In("GF_SKIN_NOFTST", row.Id);
        if (In("GF_SKIN_NOFACE", row.Id)) report.AppendLine($"SKIN    {row.Id}  DIAGNOSTIC : visage non modifié (GF_SKIN_NOFACE)");
        else if (!s.Files.ContainsKey("head_d")) report.AppendLine($"SKIN    {row.Id}  {s.Name} n'a pas de texture de visage : visage non modifié");
        else if (faceGen == null || !File.Exists(faceGen)) report.AppendLine($"ATTENTION {row.Id}  pas de FaceGen : visage du skin non appliqué");
        else
        {
            var head = JsonNode.Parse(Py.Run(ctx, "head_shader.py", faceGen))!;
            if (head["diffuse"] == null) { report.AppendLine($"ATTENTION {row.Id}  tête introuvable dans le FaceGen : visage non modifié"); return; }
            var txst = Txst(mod, s, "head", modDir, head, report);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Map(string key, string? ours)
            {
                if (head[key]?.GetValue<string>() is { Length: > 0 } old && !string.IsNullOrEmpty(ours)) map[Bare(old)] = @"textures\" + ours;
            }
            Map("diffuse", txst.Diffuse?.DataRelativePath.Path is { } d ? Bare(d) : null);
            Map("normal", txst.NormalOrGloss?.DataRelativePath.Path is { } n ? Bare(n) : null);
            Map("subsurface", txst.GlowOrDetailMap?.DataRelativePath.Path is { } sk ? Bare(sk) : null);
            Map("specular", txst.BacklightMaskOrSpecular?.DataRelativePath.Path is { } sp ? Bare(sp) : null);
            var bytes = NifPatcher.ReplaceTextures(File.ReadAllBytes(faceGen), t => map.TryGetValue(Bare(t), out var o) && !o.Equals(t, StringComparison.OrdinalIgnoreCase) ? o : null, out var count);
            File.WriteAllBytes(faceGen, bytes);
            if (!noFtst) npc.HeadTexture.SetTo(txst);
            report.AppendLine($"SKIN    {row.Id}  visage : {count} chemins du FaceGen remplacés, " + (noFtst ? "FTST inchangée (DIAGNOSTIC GF_SKIN_NOFTST)" : $"FTST {txst.EditorID}"));
        }
        report.AppendLine($"SKIN    {row.Id}  {s.Name} ({BodyLabel(s.Body)}) appliqué");
    }

    /// <summary>Copie d'un ArmorAddon de peau de la race pour le skin, ou null s'il ne porte pas une forme de peau du
    /// corps ou des mains (loup-garou, seigneur vampire, enfant, autre race…).</summary>
    ArmorAddon? SkinAddon(SkyrimMod mod, IArmorAddonGetter aa, IRaceGetter race, PresetRow row, Skin s,
        (string Body, string? Hands) bases, string modDir, StringBuilder report)
    {
        bool forRace = aa.Race.FormKeyNullable == race.FormKey || aa.AdditionalRaces.Any(r => r.FormKey == race.FormKey);
        var flags = aa.BodyTemplate?.FirstPersonFlags ?? 0;
        if (!forRace || (flags & (BipedObjectFlag.Body | BipedObjectFlag.Hands | BipedObjectFlag.Feet)) == 0
            || (flags & (BipedObjectFlag.Head | BipedObjectFlag.Hair | BipedObjectFlag.Tail)) != 0) return null;
        var model = row.Female ? aa.WorldModel?.Female : aa.WorldModel?.Male;
        var mesh = model?.File?.DataRelativePath.Path;
        if (string.IsNullOrEmpty(mesh)) return null;
        var key = $"{s.Id}|{aa.FormKey}|{row.Sexe}";
        if (addons.TryGetValue(key, out var done)) return done;

        var shp = Shapes(mesh);
        if (shp == null) { report.AppendLine($"ATTENTION {row.Id}  mesh de peau introuvable : {mesh}"); return null; }
        var alts = new List<(string Name, int Index, TextureSet T)>();
        TextureSet? main = null;
        foreach (var sh in shp)
        {
            var file = System.IO.Path.GetFileName(sh!["diffuse"]!.GetValue<string>()).ToLowerInvariant();
            string? part = file == bases.Body ? "body" : bases.Hands != null && file == bases.Hands ? "hands" : null;
            if (part == null) continue;
            // UBE : mains et pieds prennent la texture du corps ; CBBE/HIMBO sans texture de mains : mains d'origine
            if (part == "hands" && !s.Files.ContainsKey("hands_d")) continue;
            var t = Txst(mod, s, part, modDir, sh, report);
            alts.Add((sh["name"]!.GetValue<string>(), sh["index"]!.GetValue<int>(), t));
            main ??= t;
        }
        if (main == null) return null;

        var copy = mod.ArmorAddons.DuplicateInAsNewRecord(aa, $"GF_Skin_{s.Id[..6]}_{aa.EditorID}");
        // follower autonome : la version gagnante de l'ArmorAddon peut venir d'un autre mod (MaleSkinImprovements pour
        // NakedTorso) ; ses références hors du jeu et de la race sont retirées (l'autre sexe garde le reste)
        var allowed = Standalone.Allowed(ctx, race, null);
        bool Ok(IFormLinkGetter l) => l.IsNull || allowed.Contains(l.FormKey.ModKey);
        copy.AdditionalRaces.RemoveWhere(r => !Ok(r));
        foreach (var m in new[] { copy.WorldModel?.Male, copy.WorldModel?.Female, copy.FirstPersonModel?.Male, copy.FirstPersonModel?.Female })
            m?.AlternateTextures?.RemoveWhere(t => !Ok(t.NewTexture));
        if (!Ok(copy.FootstepSound)) copy.FootstepSound.Clear();
        if (!Ok(copy.ArtObject)) copy.ArtObject.Clear();
        if (copy.TextureSwapList is { } tsl && (!Ok(tsl.Male) || !Ok(tsl.Female)))
            copy.TextureSwapList = new GenderedItem<IFormLinkNullableGetter<IFormListGetter>>(
                Ok(tsl.Male) ? tsl.Male : new FormLinkNullable<IFormListGetter>(), Ok(tsl.Female) ? tsl.Female : new FormLinkNullable<IFormListGetter>());
        var male = copy.SkinTexture?.Male is { } sm && Ok(sm) ? sm : new FormLinkNullable<ITextureSetGetter>();
        var female = copy.SkinTexture?.Female is { } sf && Ok(sf) ? sf : new FormLinkNullable<ITextureSetGetter>();
        copy.SkinTexture = row.Female
            ? new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(male, main.ToNullableLink())
            : new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(main.ToNullableLink(), female);
        // diagnostic (corps blanc, teinte de peau perdue au test GF_Skins_CBBE) : GF_SKIN_NOALT=<ids> -> SkinTexture seulement,
        // sans textures alternatives (comme Mel Medarda / Skogul)
        if (!(Environment.GetEnvironmentVariable("GF_SKIN_NOALT") ?? "").Split(',').Contains(row.Id, StringComparer.OrdinalIgnoreCase))
        {
            var target = row.Female ? copy.WorldModel!.Female! : copy.WorldModel!.Male!;
            target.AlternateTextures = new ExtendedList<AlternateTexture>(alts.Select(a => new AlternateTexture { Name = a.Name, Index = a.Index, NewTexture = a.T.ToLink() }));
        }
        else report.AppendLine($"SKIN    {row.Id}  DIAGNOSTIC : {copy.EditorID} sans textures alternatives (GF_SKIN_NOALT)");
        addons[key] = copy;
        return copy;
    }
}
