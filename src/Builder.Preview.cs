using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>Follower Builder : aperçu 3D (tête affichée, géométrie, corps entier et tenue, textures).</summary>
public static partial class Builder
{
    /// <summary>Tête générée (aperçu 3D) : GF_&lt;id&gt;.json, déjà au format de l'aperçu (moteur C#, HeadGen) ; .nif d'assemble.py
    /// avec GF_MOTEUR=python.</summary>
    static string HeadNif(Context ctx, PresetRow r) => Path.Combine(ctx.WorkDir, "heads_offline", r.ExportName + (Context.PythonEngine ? ".nif" : ".json"));

    /// <summary>Export RaceMenu associé au preset (automatique ou choisi à la main), ou null.</summary>
    static string? ExportPath(Context ctx, PresetRow row, List<IHeadPartGetter> effective)
        => HeadImports.Resolve(ctx, row, effective).Path is { } p && ctx.CharGenPath(p) is var file && File.Exists(file) ? file : null;

    static bool HasExport(Context ctx, PresetRow r)
    {
        var fiche = Path.Combine(ctx.WorkDir, "headparts", r.Id + ".json");
        return File.Exists(fiche) && JsonNode.Parse(File.ReadAllText(fiche))?["import"]?.GetValue<string>() is { } p && File.Exists(p);
    }

    /// <summary>Tête affichée en 3D : l'export RaceMenu s'il existe (c'est lui qui part en jeu), sinon la tête générée.</summary>
    static string ShownHead(Context ctx, PresetRow row)
    {
        var race = ctx.Find<IRaceGetter>(row.Race);
        var effective = HeadParts.Effective(ctx, Jslot.Load(ctx.PresetPath(row.Preset)), race, row.Female);
        var export = ExportPath(ctx, row, effective);
        if (export != null && DonorPreview(ctx, row, export) is { } donorHead) return donorHead.Path;
        if (export != null && ExportRepair.Damaged(ctx, export).Count > 0)
        {
            // l'aperçu montre la tête réparée, comme au build
            var copy = Path.Combine(Context.Dir(Path.Combine(ctx.WorkDir, "heads_offline")), $"reparee_{row.Id}.nif");
            if (!File.Exists(copy) || File.GetLastWriteTimeUtc(copy) < File.GetLastWriteTimeUtc(export))
                File.WriteAllBytes(copy, ExportRepair.Repair(ctx, File.ReadAllBytes(export), out _));
            return copy;
        }
        return export ?? HeadNif(ctx, row);
    }

    /// <summary>Fichier de l'outil (date : aperçus refaits après une mise à jour). Dans l'exe en un fichier, Assembly.Location est
    /// vide et GetLastWriteTimeUtc("") plantait la fiche d'un follower à tête d'un autre preset (23/11).</summary>
    static string ToolFile => typeof(HeadWrite).Assembly.Location is { Length: > 0 } dll ? dll : Environment.ProcessPath!;

    /// <summary>
    /// Tête d'un autre preset (option « teteDonneur ») : copie de l'export du donneur avec la forme du visage du preset,
    /// écrite comme au build (HeadWrite.Run), dans work\heads_offline\donneur_&lt;id&gt;.nif ; refaite si l'export, l'un des
    /// deux .jslot ou la fiche de pièces a changé. Log : rapport de HeadWrite (lignes « cou » si le visage est écrit).
    /// null sans donneur.
    /// </summary>
    static (string Path, string Log)? DonorPreview(Context ctx, PresetRow row, string export)
    {
        if (HeadWrite.DonorRow(ctx, row) is not { } donor) return null;
        var copy = Path.Combine(Context.Dir(Path.Combine(ctx.WorkDir, "heads_offline")), $"donneur_{row.Id}.nif");
        var logPath = copy + ".txt";
        var sources = new[] { export, ctx.PresetPath(row.Preset), ctx.PresetPath(donor.Preset), Path.Combine(ctx.WorkDir, "headparts", row.Id + ".json") }
            .Where(File.Exists).Select(File.GetLastWriteTimeUtc).Append(File.GetLastWriteTimeUtc(ToolFile)).Max();
        if (File.Exists(copy) && File.Exists(logPath) && File.GetLastWriteTimeUtc(copy) > sources && File.ReadAllText(logPath).Contains($"[{donor.Id}]"))
            return (copy, File.ReadAllText(logPath));
        File.Copy(export, copy, true);
        string log;
        try { log = HeadWrite.Run(ctx, copy, row.Id, row.Id, donor.Id, keepCopy: false); }
        catch (GfException e) { log = "ERREUR " + e.Message; }
        File.WriteAllText(logPath, $"[{donor.Id}]\n" + log);
        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow);
        return (copy, log);
    }

    /// <summary>Chemin d'un .nif à afficher : absolu, ou relatif à Data (loose seulement ; Data virtuel sous MO2 lu
    /// directement).</summary>
    static string Resolve(Context ctx, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) throw new GfException("chemin manquant");
        if (Path.IsPathRooted(rel)) return File.Exists(rel) ? rel : throw new GfException($"introuvable : {rel}");
        // loose d'abord, puis fichiers déjà extraits des BSA (offline\bsa_cache\<archive>\...)
        var candidates = new[] { ctx.Files.PathOf(rel) }
            .Concat(Directory.Exists(Path.Combine(ctx.Root, "offline", "bsa_cache"))
                ? Directory.EnumerateDirectories(Path.Combine(ctx.Root, "offline", "bsa_cache")).Select(d => Path.Combine(d, rel))
                : []);
        // moteur C# : sinon extrait des BSA à la demande (work\bsa_cache)
        return candidates.FirstOrDefault(File.Exists) ?? (Context.PythonEngine ? null : ctx.Bsa.Find(rel))
            ?? throw new GfException($"introuvable (loose ou BSA déjà extrait) : {rel}");
    }

    /// <summary>
    /// Coiffure ajoutée choisie (option « coiffure ») pour l'aperçu : formes texturées de ses .nif (SmpHair.ChoiceModels),
    /// ramenées dans le repère de la tête. Les .nif de coiffure sont dans le repère du squelette (cheveux vers 120 unités de
    /// haut) et la tête affichée dans celui de l'os « NPC Head [Head] » : l'écart est une translation, la position de cet
    /// os dans le squelette de la race (mesuré le 12/11 sur Buttercup : 0,02 ; −1,55 ; 120,34). { choix: null } sans choix.
    /// </summary>
    static JsonObject AddedHairMesh(Context ctx, PresetRow row, string cache)
    {
        var shapes = new JsonArray();
        if (Options.ChosenHair(ctx, row, Options.For(ctx, row.Id)) is not { } choice) return new JsonObject { ["choix"] = null, ["shapes"] = shapes };
        // os de la tête au repos dans le squelette de la race (valeur mesurée si le squelette est introuvable)
        double tx = 0.02, ty = -1.55, tz = 120.34;
        var race = ctx.Find<IRaceGetter>(row.Race);
        var skel = (row.Female ? race?.SkeletalModel?.Female : race?.SkeletalModel?.Male)?.File?.DataRelativePath.Path;
        if (!string.IsNullOrEmpty(skel) && ctx.FindMesh(skel) is { } skelNif)
        {
            var sk = Nif.Load(skelNif);
            if (sk.Node("NPC Head [Head]") is { } hb) { var g = sk.GlobalTransform(hb); (tx, ty, tz) = (g.Tx, g.Ty, g.Tz); }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new JsonArray();
        foreach (var nif in SmpHair.ChoiceModels(ctx, row, choice))
        {
            files.Add(ctx.Files.RelOf(nif));
            foreach (var node in JsonNode.Parse(File.ReadAllText(Mesh(ctx, nif, cache)))!["shapes"]!.AsArray())
            {
                // formes sans texture : collisions de la physique, jamais dessinées
                if (node is not JsonObject s || string.IsNullOrEmpty(s["diffuse"]?.GetValue<string>()) || !seen.Add(nif + "|" + s["name"]?.GetValue<string>())) continue;
                var copy = (JsonObject)s.DeepClone();
                foreach (var k in new[] { "bones", "bi", "bw" }) copy.Remove(k);
                var v = copy["verts"]!.AsArray();
                double z = 0;
                for (int i = 2; i < v.Count; i += 3) z += v[i]!.GetValue<double>();
                if (v.Count >= 3 && z / (v.Count / 3) > 50)
                    for (int i = 0; i + 2 < v.Count; i += 3)
                    {
                        v[i] = Math.Round(v[i]!.GetValue<double>() - tx, 4);
                        v[i + 1] = Math.Round(v[i + 1]!.GetValue<double>() - ty, 4);
                        v[i + 2] = Math.Round(v[i + 2]!.GetValue<double>() - tz, 4);
                    }
                shapes.Add(copy);
            }
        }
        return new JsonObject
        {
            ["choix"] = new JsonObject { ["type"] = choice.Type, ["id"] = choice.Key.ToString(), ["nom"] = choice.Name },
            ["fichiers"] = files, ["osTete"] = new JsonArray(Math.Round(tx, 3), Math.Round(ty, 3), Math.Round(tz, 3)), ["shapes"] = shapes,
        };
    }

    /// <summary>Géométrie JSON d'un .nif (NativeTools.ExportMesh, ou offline\export_mesh.py avec GF_MOTEUR=python), en
    /// cache tant que le .nif ne change pas.</summary>
    static string Mesh(Context ctx, string nif, string cache)
    {
        if (!File.Exists(nif)) throw new GfException($"NIF introuvable : {nif}");
        // tête générée par le moteur C# : déjà la géométrie de l'aperçu
        if (nif.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return nif;
        // version du format : v2 = teinte FaceGen (tint) ; v3 = position par l'os de la tête (yeux de thorRg sous la
        // tête). Les anciens JSON en cache sont refaits.
        var outFile = Path.Combine(Context.Dir(Path.Combine(cache, "mesh")), Hash(nif + "|v7") + ".json");   // v4 : hairTint ; v5 : normal, spéculaire ; v6 : peau (os, poids) ; v7 : poids des NiTriShape, parents des os
        if (!File.Exists(outFile) || File.GetLastWriteTimeUtc(outFile) < File.GetLastWriteTimeUtc(nif))
        {
            if (Context.PythonEngine) Py.Run(ctx, "export_mesh.py", nif, outFile);
            else File.WriteAllText(outFile, NativeTools.ExportMesh(nif).ToJsonString());
        }
        return outFile;
    }

    /// <summary>
    /// Corps de base du follower pour l'aperçu « corps entier », habillé comme en jeu : ArmorAddons de la peau de la race
    /// (WNAM) pour le corps, les mains, les pieds et la queue (le premier pour la race, par emplacement), mesh du sexe du
    /// follower ; sommets mélangés entre _0 et _1 selon le poids du preset ; diffuse = texture alternative de la forme,
    /// sinon SkinTexture de l'ArmorAddon (formes de peau), sinon celle du NIF. C'est le corps installé (BodySlide) ; avec
    /// <paramref name="presetName"/>, les morphs de son .tri reçoivent les valeurs du preset, comme OBody en jeu.
    /// <paramref name="outfit"/> : tenue portée (Options.WornArmors), ArmorAddons de chaque armure pour la race (sinon pour
    /// sa race d'armure), chargés de la même façon ; les parties nues dont l'emplacement est pris par une armure ne sont
    /// pas dessinées, comme en jeu.
    /// </summary>
    static JsonObject Body(Context ctx, PresetRow row, string cache, string? presetName = null, bool outfit = false)
    {
        var race = ctx.Find<IRaceGetter>(row.Race) ?? throw new GfException(L.T("Race introuvable : {0}", row.Race));
        if (!ctx.Cache.TryResolve<IArmorGetter>(race.Skin.FormKey, out var skin))
            throw new GfException(L.T("Peau de la race {0} introuvable : pas de corps à afficher.", race.EditorID ?? row.Race));
        var weight = Math.Clamp(Jslot.Load(ctx.PresetPath(row.Preset)).Weight, 0f, 100f);
        var body = Skins.BodyOf(ctx, row);
        var erin = Erin.IsRace(row.Race);
        var preset = string.IsNullOrEmpty(presetName) ? null
            : BodyPresets.For(ctx, row).FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase))
              ?? throw new GfException(L.T("Preset de corps inconnu ou incompatible avec ce follower : {0}", presetName));
        var values = preset != null ? BodyPresets.Values(preset, weight) : null;
        int morphed = 0, sliders = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const BipedObjectFlag slots = BipedObjectFlag.Body | BipedObjectFlag.Hands | BipedObjectFlag.Feet | BipedObjectFlag.Tail;
        BipedObjectFlag seen = 0;
        var shapes = new JsonArray();
        var meshes = new JsonArray();
        bool ForRace(IArmorAddonGetter aa, FormKey r) => aa.Race.FormKeyNullable == r || aa.AdditionalRaces.Any(x => x.FormKey == r);

        // tenue : armures portées et emplacements qu'elles occupent
        BipedObjectFlag covered = 0;
        var pieces = new JsonArray();
        var worn = outfit ? Options.WornArmors(ctx, row) : ("", new List<IArmorGetter>());
        foreach (var armor in worn.Item2)
        {
            var addons = armor.Armature.Select(l => ctx.Cache.TryResolve<IArmorAddonGetter>(l.FormKey, out var a) ? a : null).OfType<IArmorAddonGetter>().ToList();
            var mine = addons.Where(a => ForRace(a, race.FormKey)).ToList();
            if (mine.Count == 0 && !race.ArmorRace.IsNull) mine = addons.Where(a => ForRace(a, race.ArmorRace.FormKey)).ToList();
            // race Erin : armures portées comme une Nordique (proxy du plugin Erin, voir OptionsModItems.PieceFitsRace)
            if (mine.Count == 0 && erin && ctx.Find<IRaceGetter>("NordRace") is { } nord)
                mine = addons.Where(a => ForRace(a, nord.FormKey) || (!nord.ArmorRace.IsNull && ForRace(a, nord.ArmorRace.FormKey))).ToList();
            int n = mine.Sum(aa => AddModel(aa, "armor"));
            // emplacements pris même sans modèle pour la race (pièce invisible en jeu : le corps nu n'y est pas non plus)
            covered |= armor.BodyTemplate?.FirstPersonFlags ?? 0;
            pieces.Add(new JsonObject
            {
                ["nom"] = armor.Name?.String ?? armor.EditorID, ["formes"] = n,
                // pièce sans modèle : races de ses ArmorAddons (diagnostic)
                ["races"] = n > 0 ? null : string.Join(", ", addons.SelectMany(a => a.AdditionalRaces.Select(r => r.FormKey).Prepend(a.Race.FormKey))
                    .Distinct().Select(k => ctx.Cache.TryResolve<IRaceGetter>(k, out var rr) ? rr.EditorID : k.ToString())),
            });
        }

        foreach (var link in skin.Armature)
        {
            if (!ctx.Cache.TryResolve<IArmorAddonGetter>(link.FormKey, out var aa) || !ForRace(aa, race.FormKey)) continue;
            var flags = aa.BodyTemplate?.FirstPersonFlags ?? 0;
            if ((flags & slots) == 0 || (flags & (BipedObjectFlag.Head | BipedObjectFlag.Hair)) != 0 || (flags & seen & slots) != 0) continue;
            seen |= flags & slots;
            if ((flags & covered) != 0) continue;   // sous une armure
            AddModel(aa, flags.HasFlag(BipedObjectFlag.Body) ? "body" : flags.HasFlag(BipedObjectFlag.Hands) ? "hands" : flags.HasFlag(BipedObjectFlag.Feet) ? "feet" : "tail");
        }

        // Formes d'un ArmorAddon (mesh du sexe du follower), ajoutées à shapes ; renvoie leur nombre
        int AddModel(IArmorAddonGetter aa, string part)
        {
            var model = row.Female ? aa.WorldModel?.Female : aa.WorldModel?.Male;
            var rel = model?.File?.DataRelativePath.Path;
            if (string.IsNullOrEmpty(rel) || ctx.FindMesh(rel) is not { } nif) return 0;
            int added = 0;
            // poids : le jeu mélange les sommets de _0 (poids 0) et _1 (poids 100)
            var m = System.Text.RegularExpressions.Regex.Match(nif, @"_([01])\.nif$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string? thin = null, fat = nif;
            if (m.Success)
            {
                var other = ctx.FindMesh(System.Text.RegularExpressions.Regex.Replace(rel, @"_[01]\.nif$", m.Groups[1].Value == "1" ? "_0.nif" : "_1.nif",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                (thin, fat) = m.Groups[1].Value == "1" ? (other, nif) : (nif, other ?? nif);
            }
            var fatShapes = JsonNode.Parse(File.ReadAllText(Mesh(ctx, fat, cache)))!["shapes"]!.AsArray();
            var thinShapes = thin != null && thin != fat ? JsonNode.Parse(File.ReadAllText(Mesh(ctx, thin, cache)))!["shapes"]!.AsArray() : null;
            var skinSet = (row.Female ? aa.SkinTexture?.Female : aa.SkinTexture?.Male) is { IsNull: false } st
                && ctx.Cache.TryResolve<ITextureSetGetter>(st.FormKey, out var txst) ? txst : null;
            var skinTex = skinSet?.Diffuse?.DataRelativePath.Path;
            meshes.Add(new JsonObject { ["partie"] = part, ["mesh"] = rel, ["mixte"] = thinShapes != null });
            var tri = values != null && BodyPresets.BodyTri(ctx, fat) is { } triPath ? BodyPresets.ReadTri(triPath) : null;
            foreach (var s in fatShapes.Select(n => n!.AsObject()))
            {
                var name = s["name"]!.GetValue<string>();
                if (thinShapes?.FirstOrDefault(t => t!["name"]!.GetValue<string>() == name) is { } t
                    && t["verts"]!.AsArray() is var tv && s["verts"]!.AsArray() is var fv && tv.Count == fv.Count)
                {
                    var mixed = new JsonArray();
                    for (int i = 0; i < fv.Count; i++)
                        mixed.Add(Math.Round(tv[i]!.GetValue<double>() + (fv[i]!.GetValue<double>() - tv[i]!.GetValue<double>()) * weight / 100.0, 4));
                    s["verts"] = mixed;
                }
                // preset BodySlide : décalage de chaque morph × valeur du curseur (forme du .tri du même nom)
                if (tri != null && values != null && tri.TryGetValue(name, out var morphs))
                {
                    var v = s["verts"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
                    foreach (var (slider, val) in values)
                    {
                        if (val == 0 || !morphs.TryGetValue(slider, out var offsets)) continue;
                        used.Add(slider);
                        foreach (var (i, dx, dy, dz) in offsets)
                            if (i * 3 + 2 < v.Length) { v[i * 3] += dx * val; v[i * 3 + 1] += dy * val; v[i * 3 + 2] += dz * val; }
                    }
                    s["verts"] = new JsonArray(v.Select(x => (JsonNode)Math.Round(x, 4)).ToArray());
                    morphed++;
                }
                var diffuse = s["diffuse"]?.GetValue<string>() ?? "";
                // forme de peau (shader Skin Tint) : teinte de peau du PNJ (QNAM) et texture de peau de la race
                var skinShape = s["shader"]?.GetValue<int>() == 5;
                var altSet = model!.AlternateTextures?.FirstOrDefault(a => a.Name == name) is { } at
                    && ctx.Cache.TryResolve<ITextureSetGetter>(at.NewTexture.FormKey, out var atx) ? atx : null;
                var shownSet = altSet ?? (skinShape ? skinSet : null);
                static string Tex(string p) => p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? p : @"textures\" + p;
                if (shownSet?.Diffuse?.DataRelativePath.Path is { Length: > 0 } shown)
                {
                    s["diffuse"] = Tex(shown);
                    // le jeu prend tout le jeu de textures : normal map et brillance suivent la diffuse
                    if (shownSet.NormalOrGloss?.DataRelativePath.Path is { Length: > 0 } n) s["normal"] = Tex(n);
                    if (shownSet.BacklightMaskOrSpecular?.DataRelativePath.Path is { Length: > 0 } sp) s["specular"] = Tex(sp);
                }
                s["skinTint"] = skinShape;
                // partie du skin choisi (body, hands), d'après la diffuse d'origine comme au build ; pour la peau visible
                // d'une armure, c'est skinZone ci-dessous (le jeu y met aussi le skin du follower)
                s["skinPart"] = part == "armor" ? null : Skins.SkinPart(body, row.Sexe, diffuse, erin);
                // peau visible d'une armure (shader Skin Tint, texture du corps ou des mains) : zone des overlays du corps,
                // que SKEE pose aussi sur elle en jeu
                if (part == "armor" && skinShape) s["skinZone"] = Skins.SkinPart(body, row.Sexe, diffuse, erin);
                s["part"] = part;
                shapes.Add(s.DeepClone());
                added++;
            }
            return added;
        }
        if (shapes.Count == 0) throw new GfException(L.T("Aucun mesh de corps trouvé dans la peau de la race {0}.", race.EditorID ?? row.Race));
        // os de la tête dans le squelette de la race (pose de repos) : les têtes (exports RaceMenu, FaceGen) sont dans le
        // repère de cet os, le jeu les y attache. Matrice 4×4 par lignes (rotation × échelle, translation).
        JsonArray? headBone = null;
        var bones = new JsonArray();
        static JsonArray Rows(Xform g) => [g.R[0, 0] * g.S, g.R[0, 1] * g.S, g.R[0, 2] * g.S, g.Tx, g.R[1, 0] * g.S, g.R[1, 1] * g.S, g.R[1, 2] * g.S, g.Ty,
            g.R[2, 0] * g.S, g.R[2, 1] * g.S, g.R[2, 2] * g.S, g.Tz, 0, 0, 0, 1];
        var skel = (row.Female ? race.SkeletalModel?.Female : race.SkeletalModel?.Male)?.File?.DataRelativePath.Path;
        if (!string.IsNullOrEmpty(skel) && ctx.FindMesh(skel) is { } skelNif)
        {
            var sk = Nif.Load(skelNif);
            if (sk.Node("NPC Head [Head]") is { } hb) headBone = Rows(sk.GlobalTransform(hb));
            // os du squelette au repos (pose de l'aperçu) : nom, parent, matrice globale par lignes
            foreach (var n in sk.File.Blocks.OfType<NiflySharp.Blocks.NiNode>().Where(n => !string.IsNullOrEmpty(n.Name?.String)))
                bones.Add(new JsonObject { ["nom"] = n.Name!.String, ["parent"] = sk.File.GetParentNode(n)?.Name?.String, ["m"] = Rows(sk.GlobalTransform(n)) });
        }
        sliders = values?.Count(kv => kv.Value != 0) ?? 0;
        return new JsonObject
        {
            ["shapes"] = shapes, ["poids"] = weight, ["corps"] = Skins.BodyLabel(body), ["meshes"] = meshes, ["osTete"] = headBone, ["squelette"] = skel,
            // hauteur de la race pour ce sexe (le jeu multiplie la taille du PNJ par elle : Hauts-Elfes 1,1…)
            ["hauteurRace"] = row.Female ? race.Height.Female : race.Height.Male,
            ["os"] = bones,
            // preset appliqué : curseurs non nuls du preset, dont trouvés dans les .tri du corps (les autres sont d'un autre corps)
            ["preset"] = preset?.Name, ["curseurs"] = sliders, ["curseursAppliques"] = used.Count, ["formesMorphees"] = morphed,
            // tenue : nom, pièces (formes dessinées : 0 = pas de modèle pour la race), cheveux sous un casque ou une capuche
            ["tenue"] = outfit ? worn.Item1 : null, ["pieces"] = pieces,
            ["raceArmure"] = race.ArmorRace.IsNull ? null : ctx.Cache.TryResolve<IRaceGetter>(race.ArmorRace.FormKey, out var ar) ? ar.EditorID : race.ArmorRace.FormKey.ToString(),
            ["cacheCheveux"] = (covered & (BipedObjectFlag.Hair | BipedObjectFlag.Head)) != 0,
        };
    }

    /// <summary>Texture du jeu convertie en PNG 1024² (NativeTools.DdsPng, ou offline\dds_png.py avec GF_MOTEUR=python),
    /// en cache.</summary>
    static string Texture(Context ctx, string rel, string cache)
    {
        rel = Remap(ctx, rel);
        // teinte d'un export RaceMenu : « Data\SKSE\Plugins\CharGen\…\x.dds », relative au dossier du jeu (PyNifly
        // y ajoute « textures\ »)
        var bare = rel.StartsWith(@"textures\Data\", StringComparison.OrdinalIgnoreCase) ? rel[9..] : rel;
        if (bare.StartsWith(@"Data\", StringComparison.OrdinalIgnoreCase)) rel = ctx.Files.PathOf(bare[5..]);
        // un fichier absolu peut changer (tête régénérée) : sa date entre dans la clé du cache
        var key = Path.IsPathRooted(rel) && File.Exists(rel) ? rel + "|" + File.GetLastWriteTimeUtc(rel).Ticks : rel;
        // « |r2 » : PNG refaits depuis la réduction sans alpha prémultiplié (02/11 : relief des normal maps abîmé avant)
        var outFile = Path.Combine(Context.Dir(Path.Combine(cache, "tex")), Hash((key + "|r2").ToLowerInvariant()) + ".png");
        if (!File.Exists(outFile))
        {
            if (Context.PythonEngine) Py.Run(ctx, "dds_png.py", rel, outFile, "1024");
            else NativeTools.DdsPng(ctx, rel, outFile, 1024);
        }
        return outFile;
    }

    /// <summary>Texture de remplacement de work\texture_remap.json, comme au build : l'aperçu montre ce que le jeu
    /// affichera (Psynwen : diffuse highelffemale absente, tête noire sans redirection). Clés brutes du .nif, avec
    /// ou sans « textures\ ».</summary>
    static string Remap(Context ctx, string rel)
    {
        var path = Path.Combine(ctx.WorkDir, "texture_remap.json");
        if (!File.Exists(path)) return rel;
        var raw = rel.Replace('/', '\\');
        var bare = raw.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? raw[9..] : raw;
        foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!)
            if (kv.Key.Equals(raw, StringComparison.OrdinalIgnoreCase) || kv.Key.Equals(bare, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return rel;
    }
}
