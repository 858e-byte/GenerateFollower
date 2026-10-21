using System.Drawing;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace GenerateFollower;

/// <summary>Construit GenerateFollower.esp (ESL) + FaceGen + body morphs + scripts, à partir de presets.csv et des têtes exportées.</summary>
public static class FollowerBuilder
{
    static readonly FormKey PlayerNpc = new(ModKey.FromFileName("Skyrim.esm"), 0x000007);

    /// <param name="offline">têtes générées hors jeu (offline\) au lieu de l'export RaceMenu</param>
    /// <param name="only">ne construire que ces ids (lot de test), null = tous les inclus</param>
    /// <param name="zipName">nom du ZIP produit dans dist\</param>
    /// <param name="diag">ajoute la page de variantes de diagnostic (voir Diagnostics)</param>
    /// <param name="fromExport">FaceGen = export RaceMenu du preset (formes renommées) quand il existe</param>
    /// <param name="pluginName">plugin produit : GF_&lt;Nom&gt;.esp pour un follower seul, GF_&lt;Pack&gt;.esp pour un pack
    /// (voir PluginFor) ; null = config.pluginName</param>
    /// <param name="title">nom affiché du pouvoir d'invocation (« Invoquer : … »)</param>
    public static void Run(Context ctx, bool offline = false, HashSet<string>? only = null, string zipName = "GenerateFollower", bool diag = false, bool fromExport = false,
        string? pluginName = null, string? title = null)
    {
        var cfg = ctx.Config;
        var rows = PresetTable.Read(ctx.CsvPath).Where(r => only == null ? r.Inclure : only.Contains(r.Id)).ToList();
        if (rows.Count == 0) throw new GfException("Aucun preset à construire (inclure=oui dans presets.csv, ou ids de --only).");
        if (offline) ctx.HeadsOverride = OfflineHeads.Generate(ctx, rows);

        // Enregistrements vanilla communs, résolus par EditorID
        var cls = ctx.Need<IClassGetter>(cfg.FollowerClass, "followerClass");
        var outfit = ctx.Find<IOutfitGetter>(cfg.DefaultOutfit);
        var packages = cfg.DefaultPackages.Select(p => ctx.Need<IPackageGetter>(p, "defaultPackages")).ToList();
        var potential = ctx.Need<IFactionGetter>("PotentialFollowerFaction", "faction follower");
        var current = ctx.Need<IFactionGetter>("CurrentFollowerFaction", "faction follower");
        var voiceEquip = ctx.Need<IEquipTypeGetter>("Voice","type d'équipement du pouvoir");

        var plugin = pluginName ?? cfg.PluginName;
        var mod = new SkyrimMod(ModKey.FromFileName(plugin), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        mod.ModHeader.Author = "GenerateFollower";
        mod.ModHeader.Description = "Followers générés depuis les presets RaceMenu.";
        var formIds = new StableFormIds(ctx, mod);

        var pex = Packaging.CompilePapyrus(ctx);
        var modDir = Packaging.CleanModDir(ctx, zipName);
        var geomDir = Context.Dir(Path.Combine(modDir, "meshes", "actors", "character", "FaceGenData", "FaceGeom", plugin));
        var tintDir = Context.Dir(Path.Combine(modDir, "textures", "actors", "character", "FaceGenData", "FaceTint", plugin));
        // Forme du corps : plus de BodyGen (choix de l'utilisateur, 18/10) : OBody, avec ses presets « zero sliders »,
        // décide pour chaque follower. Les morphs du jslot (Jslot.BodyMorphs) ne sont plus écrits.

        var built = new List<(PresetRow Row, Npc Npc)>();
        var report = new StringBuilder();
        var standalone = cfg.Standalone ? new Standalone(ctx, mod) : null;
        // Textures d'exports RaceMenu absentes chez l'utilisateur (preset fait avec une autre version d'un mod),
        // remplacées par leur équivalent installé : { "ancien chemin": "nouveau chemin" }, chemins depuis Data
        var remapPath = Path.Combine(ctx.WorkDir, "texture_remap.json");
        var remap = File.Exists(remapPath)
            ? new Dictionary<string, string>(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(remapPath))!, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var slots = new Dictionary<string, Jslot>();
        foreach (var row in rows)
            try { slots[row.Id] = Jslot.Load(Path.Combine(ctx.PresetsDir, row.Preset + ".jslot")); }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { report.AppendLine($"ERREUR  {row.Id}  {row.Preset}  {e.Message}"); }
        var overlays = new Overlays(ctx);
        overlays.Prefetch(slots.Values);
        // Skins choisis dans le Builder (work\skins_choice.json)
        var skinChoices = Skins.LoadChoices(ctx);
        var skins = rows.Any(r => skinChoices.ContainsKey(r.Id)) ? Skins.Load(ctx) : null;
        // Style de combat et classe choisis dans le Builder (work\combat_choice.json) ; sans choix, PNJ inchangé
        var combatChoices = Combat.LoadChoices(ctx);
        // Voix choisie dans le Builder (work\voice_choice.json) ; sans choix, voix de presets.csv
        var voiceChoices = Voices.LoadChoices(ctx);
        // Options (tenue, sorts, atouts, niveau…) choisies dans le Builder (work\options_choice.json)
        var optionChoices = Options.LoadAll(ctx);
        foreach (var row in rows.Where(r => slots.ContainsKey(r.Id)))
        {
            var j = slots[row.Id];
            var nif = Path.Combine(ctx.HeadsDir, row.ExportName + ".nif");
            var dds = Path.Combine(ctx.HeadsDir, row.ExportName + ".dds");
            bool hasHead = File.Exists(nif) && File.Exists(dds);
            if (!hasHead && cfg.SkipWithoutHead) { report.AppendLine($"IGNORÉ  {row.Id}  {row.Preset}  (tête non exportée)"); continue; }

            // --from-export : formes de l'export, pour que l'enregistrement ait les mêmes headparts
            HashSet<string>? exportShapes = null;
            var ficheJson = Path.Combine(ctx.WorkDir, "headparts", row.Id + ".json");
            if (fromExport && File.Exists(ficheJson) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ficheJson))?["import"]?.GetValue<string>() is { } exp && File.Exists(exp))
                exportShapes = NifPatcher.ReadStrings(File.ReadAllBytes(exp));
            Npc npc;
            try { npc = BuildNpc(ctx, mod, row, j, cls, outfit, packages, potential, current, standalone, exportShapes, report); }
            catch (GfException e) { report.AppendLine($"ERREUR  {row.Id}  {row.Preset}  {e.Message}"); continue; }
            if (combatChoices.TryGetValue(row.Id, out var combat)) Combat.Apply(ctx, mod, npc, row, combat, report);
            Voices.Apply(ctx, npc, row, voiceChoices.GetValueOrDefault(row.Id), report);
            if (optionChoices[row.Id] is System.Text.Json.Nodes.JsonObject opts) Options.Apply(ctx, mod, npc, row, opts, report);

            var fid = npc.FormKey.ID.ToString("X8");
            if (hasHead)
            {
                // Avec le préfixe "textures\", comme les FaceGen exportés par le CK
                var tintPath = $@"textures\Actors\Character\FaceGenData\FaceTint\{plugin}\{fid}.dds";
                var fiche = Path.Combine(ctx.WorkDir, "headparts", row.Id + ".json");
                if (fromExport && File.Exists(fiche) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fiche))?["import"] != null)
                {
                    // --from-export : l'export RaceMenu tel quel (méthode des tutoriels), modifié en binaire seulement :
                    // noms des formes -> nos EditorID, chemin de teinte. Jamais réenregistré par PyNifly : le jeu
                    // rejette un FaceGen dont le skin a été réécrit (tests 9 et 10).
                    var fj = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fiche))!;
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in fj["headParts"]!.AsArray())
                        map.TryAdd(h!["srcEdid"]?.GetValue<string>() ?? h["edid"]!.GetValue<string>(), h["edid"]!.GetValue<string>());
                    // headparts pris à l'export par MatchExport (absents de la fiche) et recréés chez nous : la forme doit
                    // porter le nom de notre copie, sinon le jeu rejette la tête (nyridieRd ube, test GF_Skins_Diag :
                    // cils 00UBE_LashiviousLashes6 de l'export laissés sous ce nom, PNJ avec GF_00UBE_LashiviousLashes6)
                    foreach (var link in npc.HeadParts)
                        if (mod.HeadParts.FirstOrDefault(h => h.FormKey == link.FormKey) is { EditorID: { } ours } && ours.StartsWith(Standalone.Prefix))
                            map.TryAdd(ours[Standalone.Prefix.Length..], ours);
                    // Cheveux SMP : os de physique redirigés vers la tête (cheveux rigides). Renommer ces os à leur nom
                    // d'origine pour que HDT-SMP recrée la physique sur le PNJ n'a rien changé (test 11, Luna et Anna).
                    var bytes = NifPatcher.PinPhysicsBones(File.ReadAllBytes(fj["import"]!.GetValue<string>()), out var pinned, out var approx);
                    bytes = NifPatcher.PatchStrings(bytes, s => map.TryGetValue(s, out var n) ? n : null, out var renamed);
                    var remapped = new List<string>();
                    bytes = NifPatcher.PatchTintPath(bytes, tintPath, out var tints, t =>
                    {
                        // clés de texture_remap.json avec ou sans « textures\ » (réparations ajoutées par le Builder : sans)
                        var bare = t.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? t[9..] : t;
                        if (!remap.TryGetValue(t, out var n) && !remap.TryGetValue(bare, out n) && !remap.TryGetValue(@"textures\" + bare, out n)) return null;
                        remapped.Add(n);
                        standalone?.Files.Add(n);
                        return n;
                    });
                    if (remapped.Count > 0) report.AppendLine($"TEXTURE {row.Id}  {remapped.Count} chemins remplacés (texture_remap.json)");
                    File.WriteAllBytes(Path.Combine(geomDir, fid + ".nif"), bytes);
                    report.AppendLine($"EXPORT  {row.Id}  FaceGen = export RaceMenu, {renamed} chaînes renommées, teinte {(tints > 0 ? "OK" : "NON TROUVÉE")}" +
                        (pinned > 0 ? $", {pinned} os de physique -> tête" + (approx > 0 ? $" ({approx} formes sans la tête : liaison d'une autre forme)" : "") : ""));
                }
                else
                {
                    var patched = NifPatcher.PatchTintPath(File.ReadAllBytes(nif), tintPath, out var n);
                    if (n == 0) report.AppendLine($"ATTENTION {row.Id}  chemin de teint non trouvé dans le NIF");
                    File.WriteAllBytes(Path.Combine(geomDir, fid + ".nif"), patched);
                }
                Packaging.ConvertTint(ctx, dds, tintDir, fid);
            }
            // avant les overlays : la cuisson des overlays du visage (têtes non UBE) part de la diffuse du skin
            // diagnostic : GF_SKIN_NONE=<ids> -> skin choisi ignoré (peau du jeu)
            if ((Environment.GetEnvironmentVariable("GF_SKIN_NONE") ?? "").Split(',').Contains(row.Id, StringComparer.OrdinalIgnoreCase))
                report.AppendLine($"SKIN    {row.Id}  DIAGNOSTIC : skin ignoré (GF_SKIN_NONE)");
            else if (skins != null && skinChoices.TryGetValue(row.Id, out var skinId))
            {
                if (skins.Get(skinId) is { } skin)
                    skins.Apply(mod, npc, row, skin, modDir, hasHead ? Path.Combine(geomDir, fid + ".nif") : null, report);
                else report.AppendLine($"ATTENTION {row.Id}  skin {skinId} absent du catalogue (work\\skins_index.json) : peau du jeu");
            }
            if (overlays.For(j, row, hasHead ? Path.Combine(geomDir, fid + ".nif") : null, report, (modDir, plugin)) is { } ovl)
                npc.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { ovl } };
            built.Add((row, npc));
            report.AppendLine($"OK      {row.Id}  {fid}  {row.Nom}  [{row.Race} {row.Sexe} {row.Voix}]{(hasHead ? "" : "  SANS FACEGEN")}");
        }
        // Textures des overlays : dans le mod, comme celles des headparts recréés
        if (standalone != null) standalone.Files.UnionWith(overlays.Files);
        else if (overlays.Files.Count > 0) report.AppendLine($"ATTENTION textures d'overlays non copiées (standalone=false) : {overlays.Files.Count}");
        if (standalone != null)
        {
            report.AppendLine("Fichiers des headparts recréés (followers autonomes) : " + standalone.CopyFiles(modDir).Trim());
            // FaceGen : les textures des pièces copiées (cheveux…) sont maintenant sous textures\GenerateFollower\
            int faces = 0, facePaths = 0;
            foreach (var faceNif in Directory.EnumerateFiles(geomDir, "*.nif"))
            {
                var bytes = NifPatcher.ReplaceTextures(File.ReadAllBytes(faceNif), standalone.TextureMap, out var n);
                if (n > 0) { File.WriteAllBytes(faceNif, bytes); faces++; facePaths += n; }
            }
            report.AppendLine($"FaceGen : {facePaths} chemins de textures vers {Standalone.SubFolder}\\ dans {faces} têtes");
            // Overlays du corps (script NiOverride) : leurs textures copiées sont aussi sous textures\GenerateFollower\
            int ovl = 0;
            foreach (var (_, npc) in built)
                foreach (var script in npc.VirtualMachineAdapter?.Scripts.Where(s => s.Name == Overlays.Script) ?? [])
                    if (script.Properties.FirstOrDefault(p => p.Name == "OvlStrings") is ScriptStringListProperty strings)
                        for (int i = 0; i < strings.Data.Count; i++)
                            if (strings.Data[i].Length > 0 && standalone.TextureMap(strings.Data[i]) is { } moved) { strings.Data[i] = moved; ovl++; }
            if (ovl > 0) report.AppendLine($"Overlays : {ovl} chemins de textures vers {Standalone.SubFolder}\\ (script {Overlays.Script})");
        }
        if (built.Count == 0) throw new GfException("Aucun follower construit : as-tu lancé l'export des têtes en jeu ? (voir work\\rapport_build.txt)\n" + report);

        // Pages du menu d'invocation (UIListMenu limité à 128 entrées)
        var pagesList = mod.FormLists.AddNew("GF_Pages");
        var pageNames = new List<string>();
        foreach (var cat in built.GroupBy(b => b.Row.Categorie).OrderBy(g => g.Key == "(racine)").ThenBy(g => g.Key))
        {
            var chunks = cat.OrderBy(b => b.Row.Nom, StringComparer.OrdinalIgnoreCase).Chunk(Math.Clamp(cfg.PageSize, 1, 127)).ToList();
            for (int i = 0; i < chunks.Count; i++)
            {
                var page = mod.FormLists.AddNew($"GF_Page_{pageNames.Count:D2}");
                foreach (var b in chunks[i]) page.Items.Add(b.Npc.ToLink());
                pagesList.Items.Add(page.ToLink());
                pageNames.Add(chunks.Count > 1 ? $"{cat.Key} {i + 1}/{chunks.Count} ({chunks[i].Length})" : $"{cat.Key} ({chunks[i].Length})");
            }
        }
        if (diag) Diagnostics.AddVariants(ctx, mod, built, geomDir, tintDir, pagesList, pageNames);
        if (pageNames.Count > 127) throw new GfException($"{pageNames.Count} pages : augmente pageSize dans config.json.");

        // Pouvoir mineur "Invoquer un follower" -> quête principale -> menu UIExtensions
        var quest = mod.Quests.AddNew("GF_MainQuest");
        var mgef = mod.MagicEffects.AddNew("GF_SummonEffect");
        // textes vus en jeu : langue du Builder (builder\i18n)
        var powerName = title != null ? L.T("Invoquer : {0}", title) : L.T("Invoquer un follower");
        mgef.Name = powerName;
        mgef.Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.Script);
        mgef.CastType = CastType.FireAndForget;
        mgef.TargetType = TargetType.Self;
        mgef.Flags = MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea | MagicEffect.Flag.Painless | MagicEffect.Flag.NoHitEvent;
        mgef.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts = { new ScriptEntry { Name = "GF_SummonEffect", Properties = { new ScriptObjectProperty { Name = "Owner", Object = quest.ToLink<ISkyrimMajorRecordGetter>() } } } },
        };

        var spell = mod.Spells.AddNew("GF_SummonPower");
        spell.Name = powerName;
        spell.Type = SpellType.LesserPower;
        spell.CastType = CastType.FireAndForget;
        spell.TargetType = TargetType.Self;
        spell.EquipmentType.SetTo(voiceEquip);
        spell.Flags = SpellDataFlag.ManualCostCalc;
        spell.Effects.Add(new Effect { BaseEffect = mgef.ToNullableLink(), Data = new EffectData() });

        quest.Name = title ?? "GenerateFollower";
        quest.Flags = Quest.Flag.StartGameEnabled;
        quest.Priority = 50;
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts =
            {
                new ScriptEntry
                {
                    Name = "GF_MainQuest",
                    Properties =
                    {
                        new ScriptObjectProperty { Name = "SummonPower", Object = spell.ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "Pages", Object = pagesList.ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptStringListProperty { Name = "PageNames", Data = new ExtendedList<string>(pageNames) },
                        new ScriptStringProperty { Name = "SummonedText", Data = L.T(" est la. Parle-lui pour le recruter.") },
                    },
                },
            },
        };

        if (diag) Diagnostics.AddDiagPower(ctx, mod, pagesList, modDir);

        var dupes = mod.EnumerateMajorRecords().GroupBy(r => r.FormKey).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.ID:X6} : " + string.Join(", ", g.Select(r => $"{r.EditorID} ({r.GetType().Name})"))).ToList();
        if (dupes.Count > 0) throw new GfException("FormID en double (EditorID identiques ?) :\n" + string.Join("\n", dupes));
        int records = mod.EnumerateMajorRecords().Count();
        if (records > 2048) throw new GfException($"{records} enregistrements : trop pour un ESL (max 2048). Réduis le nombre de presets inclus.");
        mod.ModHeader.Stats.NextFormID = formIds.NextFree;
        formIds.Save();
        mod.BeginWrite.ToPath(Path.Combine(modDir, plugin)).WithLoadOrder(ctx.Env.LoadOrder).Write();
        Packaging.CopyPex(pex, modDir, "GF_MainQuest", "GF_SummonEffect");
        if (built.Any(b => b.Npc.VirtualMachineAdapter != null)) Packaging.CopyPex(pex, modDir, Overlays.Script);
        if (diag) Packaging.CopyPex(pex, modDir, "GF_DiagQuest", "GF_DiagEffect");

        File.WriteAllText(Path.Combine(ctx.WorkDir, "rapport_build.txt"), report.ToString(), new UTF8Encoding(true));
        var zip = Packaging.Zip(ctx, modDir, zipName);
        Console.WriteLine($"{built.Count} followers ({records} enregistrements, {pageNames.Count} pages) -> {zip}");
        Console.WriteLine($"Ignorés/erreurs : {rows.Count - built.Count} (détail : work\\rapport_build.txt)");
    }

    /// <summary>
    /// FaceGen = export RaceMenu : le moteur rejette le FaceGen s'il manque la forme d'un headpart du PNJ (Asagi au
    /// test 8). Quand l'export ne contient pas un headpart attendu mais en contient un autre du même type (Tavexa :
    /// cils par défaut UBE au lieu de LashiviousLashes6, absents chez l'utilisateur au moment de l'export), le PNJ
    /// prend celui de l'export.
    /// </summary>
    /// <summary>Nom de plugin GF_&lt;nom&gt;.esp : lettres, chiffres, _ et - seulement.</summary>
    public static string PluginFor(string name)
    {
        var clean = new string(name.Trim().Select(ch => char.IsLetterOrDigit(ch) && ch < 128 || ch is '-' or '_' ? ch : '_').ToArray()).Trim('_');
        while (clean.Contains("__")) clean = clean.Replace("__", "_");
        if (clean.Length == 0) throw new GfException($"Nom inutilisable pour un plugin : « {name} »");
        return "GF_" + clean + ".esp";
    }

    internal static List<IHeadPartGetter> MatchExport(Context ctx, List<IHeadPartGetter> effective, HashSet<string> shapes, PresetRow row, StringBuilder report)
    {
        var used = new HashSet<string>(effective.Select(h => h.EditorID ?? ""), StringComparer.OrdinalIgnoreCase);
        var spare = new List<IHeadPartGetter>();
        foreach (var s in shapes.Where(s => !used.Contains(s)))
            if (ctx.Cache.TryResolve<IHeadPartGetter>(s, out var hp) && hp.Model?.File is { } f && !string.IsNullOrEmpty(f.ToString()))
                spare.Add(hp);
        var result = new List<IHeadPartGetter>();
        foreach (var hp in effective)
        {
            bool shown = hp.Model?.File is { } f && !string.IsNullOrEmpty(f.ToString());
            if (!shown || shapes.Contains(hp.EditorID ?? "")) { result.Add(hp); continue; }
            // Parent absent de l'export mais toutes ses pièces extra présentes : le PNJ porte directement les pièces extra
            // (oreilles Viera de Bunnu : le parent VieraEarsSMP pointe vers un mesh absent du mod, RaceMenu ne l'exporte pas ;
            // tête rejetée au test GF_Skins_Diag)
            var extras = hp.ExtraParts.Select(e => ctx.Cache.TryResolve<IHeadPartGetter>(e.FormKey, out var x) ? x : null).ToList();
            if (extras.Count > 0 && extras.All(x => x != null && shapes.Contains(x.EditorID ?? "")))
            {
                result.AddRange(extras!);
                spare.RemoveAll(s => extras.Contains(s));
                report.AppendLine($"EXPORT  {row.Id}  " + L.T("{0} (absent de l'export) -> ses pièces : {1}", hp.EditorID, string.Join(", ", extras.Select(x => x!.EditorID))));
                continue;
            }
            var alt = spare.FirstOrDefault(x => x.Type == hp.Type);
            if (alt == null) { result.Add(hp); report.AppendLine($"ATTENTION {row.Id}  " + L.T("{0} absent de l'export RaceMenu", hp.EditorID)); continue; }
            spare.Remove(alt);
            result.Add(alt);
            report.AppendLine($"EXPORT  {row.Id}  " + L.T("{0} -> {1} (headpart de l'export)", hp.EditorID, alt.EditorID));
        }
        return result;
    }

    static Npc BuildNpc(Context ctx, SkyrimMod mod, PresetRow row, Jslot j, IClassGetter cls, IOutfitGetter? outfit,
        List<IPackageGetter> packages, IFactionGetter potential, IFactionGetter current, Standalone? standalone,
        HashSet<string>? exportShapes = null, StringBuilder? report = null)
    {
        var race = ctx.Need<IRaceGetter>(row.Race, $"race de {row.Preset}");
        var voice = ctx.Need<IVoiceTypeGetter>(row.Voix, $"voix de {row.Preset}");

        var npc = mod.Npcs.AddNew("GF_" + row.Id);
        npc.Name = row.Nom;
        npc.Race.SetTo(race);
        npc.Voice.SetTo(voice);
        npc.Class.SetTo(cls);
        if (outfit != null) npc.DefaultOutfit.SetTo(outfit);
        foreach (var p in packages) npc.Packages.Add(p.ToLink());
        npc.Height = 1f;
        npc.Weight = Math.Clamp(j.Weight, 0f, 100f);

        var flags = NpcConfiguration.Flag.Unique | NpcConfiguration.Flag.Protected | NpcConfiguration.Flag.AutoCalcStats;
        if (row.Female) flags |= NpcConfiguration.Flag.Female;
        npc.Configuration = new NpcConfiguration
        {
            Flags = flags,
            Level = new PcLevelMult { LevelMult = 1f },
            CalcMinLevel = 1,
            CalcMaxLevel = 81,
            SpeedMultiplier = 100,
            DispositionBase = 35,
        };
        npc.AIData = new AIData
        {
            Aggression = Aggression.Unaggressive,
            Confidence = Confidence.Brave,
            Assistance = Assistance.HelpsAllies,
            EnergyLevel = 50,
        };
        npc.Factions.Add(new RankPlacement { Faction = potential.ToLink(), Rank = 0 });
        npc.Factions.Add(new RankPlacement { Faction = current.ToLink(), Rank = -1 });

        // Mêmes headparts que le FaceGen, défauts de la race compris : sinon le jeu ignore le FaceGen.
        // En mode autonome, ceux qui ne viennent pas du mod de la race ou de la tête sont recréés chez nous.
        var effective = HeadParts.Effective(ctx, j, race, row.Female);
        if (exportShapes != null) effective = MatchExport(ctx, effective, exportShapes, row, report ?? new StringBuilder());
        var face = Standalone.Face(effective);
        var allowed = standalone != null ? Standalone.Allowed(ctx, race, face) : null;
        var validRaces = face?.ValidRaces ?? new FormLinkNullable<IFormListGetter>();
        foreach (var hp in effective)
            npc.HeadParts.Add((standalone != null ? standalone.Localize(hp, allowed!, validRaces, race) : hp).ToLink());
        if (j.HeadTexture is { } tex && ctx.TryResolveRm<ITextureSetGetter>(tex, out var ts))
            npc.HeadTexture.SetTo(standalone != null ? standalone.LocalizeTextureSet(ts, allowed!) : ts);
        if (j.HairColor is { } hc)
        {
            var color = mod.Colors.AddNew($"GF_Hair_{row.Id}");
            color.Color = Rgb(hc);
            npc.HairColor.SetTo(color);
        }
        if (j.SkinTone is { } st) npc.TextureLighting = Rgb(st);

        if (j.FaceMorphs.Count >= 18)
        {
            var m = j.FaceMorphs;
            npc.FaceMorph = new NpcFaceMorph
            {
                NoseLongVsShort = m[0], NoseUpVsDown = m[1], JawUpVsDown = m[2], JawNarrowVsWide = m[3], JawForwardVsBack = m[4],
                CheeksUpVsDown = m[5], CheeksForwardVsBack = m[6], EyesUpVsDown = m[7], EyesInVsOut = m[8], BrowsUpVsDown = m[9],
                BrowsInVsOut = m[10], BrowsForwardVsBack = m[11], LipsUpVsDown = m[12], LipsInVsOut = m[13], ChinNarrowVsWide = m[14],
                ChinUpVsDown = m[15], ChinUnderbiteVsOverbite = m[16], EyesForwardVsBack = m[17],
            };
        }
        if (j.FaceParts.Count >= 4)
            npc.FaceParts = new NpcFaceParts { Nose = (uint)j.FaceParts[0], Unknown = (uint)j.FaceParts[1], Eyes = (uint)j.FaceParts[2], Mouth = (uint)j.FaceParts[3] };

        var rel = mod.Relationships.AddNew($"GF_Rel_{row.Id}");
        rel.Parent.SetTo(npc);
        rel.Child.SetTo(PlayerNpc);
        rel.Rank = Relationship.RankType.Ally;
        return npc;
    }

    static Color Rgb(int argb) => Color.FromArgb(255, (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
}
