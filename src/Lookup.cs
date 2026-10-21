using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>Outil de debug : liste les EditorID d'un type dans l'ordre de chargement.</summary>
public static class Lookup
{
    public static void Run(Context ctx, string type, string filter)
    {
        IEnumerable<ISkyrimMajorRecordGetter> recs = type.ToLowerInvariant() switch
        {
            "class" => ctx.Env.LoadOrder.PriorityOrder.Class().WinningOverrides(),
            "outfit" => ctx.Env.LoadOrder.PriorityOrder.Outfit().WinningOverrides(),
            "package" => ctx.Env.LoadOrder.PriorityOrder.Package().WinningOverrides(),
            "voice" => ctx.Env.LoadOrder.PriorityOrder.VoiceType().WinningOverrides(),
            "faction" => ctx.Env.LoadOrder.PriorityOrder.Faction().WinningOverrides(),
            "equip" => ctx.Env.LoadOrder.PriorityOrder.EquipType().WinningOverrides(),
            "race" => ctx.Env.LoadOrder.PriorityOrder.Race().WinningOverrides(),
            "hdpt" => ctx.Env.LoadOrder.PriorityOrder.HeadPart().WinningOverrides(),
            "npc" => ctx.Env.LoadOrder.PriorityOrder.Npc().WinningOverrides(),
            "txst" => ctx.Env.LoadOrder.PriorityOrder.TextureSet().WinningOverrides(),
            "armo" => ctx.Env.LoadOrder.PriorityOrder.Armor().WinningOverrides(),
            "csty" => ctx.Env.LoadOrder.PriorityOrder.CombatStyle().WinningOverrides(),
            "flst" => ctx.Env.LoadOrder.PriorityOrder.FormList().WinningOverrides(),
            "spel" => ctx.Env.LoadOrder.PriorityOrder.Spell().WinningOverrides(),
            "perk" => ctx.Env.LoadOrder.PriorityOrder.Perk().WinningOverrides(),
            _ => throw new GfException("types : class outfit package voice faction equip race hdpt npc txst armo csty flst spel perk"),
        };
        foreach (var r in recs.Where(r => (r.EditorID + " " + r.FormKey).Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(int.TryParse(Environment.GetEnvironmentVariable("GF_MAX"), out var max) ? max : type.ToLowerInvariant() is "csty" or "class" ? 500 : 60))
        {
            Console.WriteLine($"{r.EditorID,-45} {r.FormKey}");
            if (r is INpcGetter n)
                Console.WriteLine($"     QNAM={n.TextureLighting} HCLF={n.HairColor.FormKeyNullable} FTST={n.HeadTexture.FormKeyNullable} race={n.Race.FormKey} classe={n.Class.FormKey} style={n.CombatStyle.FormKeyNullable} " +
                    $"tints={n.TintLayers.Count} [{string.Join(" ", n.TintLayers.Take(4).Select(t => $"{t.Index}:{t.Color}/{t.InterpolationValue}"))}]");
            if (r is IRaceGetter rf)
            {
                Console.WriteLine($"     flags race : {rf.Flags}");
                foreach (var hpr in rf.HeadData?.Female?.HeadParts ?? [])
                    Console.WriteLine($"     headpart par défaut F {hpr.Number}: {(ctx.Cache.TryResolve<IHeadPartGetter>(hpr.Head.FormKey, out var dh) ? $"{dh.EditorID} [{dh.Type}] modèle={(dh.Model == null ? "non" : "oui")}" : hpr.Head.FormKey.ToString())}");
            }
            if (r is IFormListGetter fl)
            {
                // plugin gagnant et membres (EditorID quand ils se résolvent), ex. lookup flst VoicesFollower
                var winner = ctx.Cache.ResolveAllContexts<IFormList, IFormListGetter>(fl.FormKey).First().ModKey;
                Console.WriteLine($"     gagnant {winner}, {fl.Items.Count} éléments : " + string.Join(", ", fl.Items.Select(i =>
                    ctx.Cache.TryResolve<ISkyrimMajorRecordGetter>(i.FormKey, out var x) ? x.EditorID ?? i.FormKey.ToString() : i.FormKey.ToString())));
            }
            if (r is ISpellGetter sp)
            {
                // école et niveau : effet principal (le plus coûteux), compétence et niveau minimal de l'effet
                var main = sp.Effects.Select(e => ctx.Cache.TryResolve<IMagicEffectGetter>(e.BaseEffect.FormKey, out var me) ? me : null)
                    .FirstOrDefault(me => me != null);
                Console.WriteLine($"     {sp.Name?.String} type={sp.Type} lancer={sp.CastType} cible={sp.TargetType} équip={sp.EquipmentType.FormKeyNullable} " +
                    $"école={main?.MagicSkill} niveau={main?.MinimumSkillLevel} coût={sp.BaseCost} flags={sp.Flags}");
            }
            if (r is IPerkGetter pk)
                Console.WriteLine($"     {pk.Name?.String} jouable={pk.Playable} rang={pk.NumRanks} suivant={pk.NextPerk.FormKeyNullable}");
            if (r is ICombatStyleGetter cs)
                Console.WriteLine($"     {cs.FormKey.ModKey} mêlée={cs.EquipmentScoreMultMelee} magie={cs.EquipmentScoreMultMagic} distance={cs.EquipmentScoreMultRanged} cri={cs.EquipmentScoreMultShout} bâton={cs.EquipmentScoreMultStaff} " +
                    $"mains nues={cs.EquipmentScoreMultUnarmed} attaque={cs.OffensiveMult} défense={cs.DefensiveMult} flags={cs.Flags}");
            if (Environment.GetEnvironmentVariable("GF_DUMP") == "1") Console.WriteLine(r.ToString());
            var skin = r as IArmorGetter;
            if (r is IRaceGetter race) ctx.Cache.TryResolve<IArmorGetter>(race.Skin.FormKey, out skin);
            if (skin != null)
                foreach (var aa in skin.Armature)
                    if (ctx.Cache.TryResolve<IArmorAddonGetter>(aa.FormKey, out var addon))
                    {
                        Console.WriteLine($"     peau {skin.EditorID} / {addon.EditorID} [{addon.BodyTemplate?.FirstPersonFlags}] races={addon.Race.FormKeyNullable}+{addon.AdditionalRaces.Count}");
                        Console.WriteLine($"        F={addon.WorldModel?.Female?.File} M={addon.WorldModel?.Male?.File} skinF={addon.SkinTexture?.Female.FormKeyNullable} skinM={addon.SkinTexture?.Male.FormKeyNullable}");
                        foreach (var alt in addon.WorldModel?.Female?.AlternateTextures ?? [])
                            Console.WriteLine($"        altF {alt.Name}#{alt.Index} -> {alt.NewTexture.FormKey}");
                    }
            if (r is ITextureSetGetter ts)
                Console.WriteLine($"     flags={ts.Flags} d={ts.Diffuse} n={ts.NormalOrGloss} sk={ts.EnvironmentMaskOrSubsurfaceTint} detail={ts.GlowOrDetailMap} spec={ts.BacklightMaskOrSpecular}");
        }
    }

    /// <summary>Debug : plugins qui définissent ou modifient une race (ou un ArmorAddon de peau), du gagnant au
    /// premier, avec la peau et ses textures. Sert à voir si un mod change une race (écart cou/visage selon la race).</summary>
    public static void Overrides(Context ctx, string edid)
    {
        var race = ctx.Find<IRaceGetter>(edid) ?? throw new GfException($"race introuvable : {edid}");
        var all = ctx.Cache.ResolveAllContexts<IRace, IRaceGetter>(race.FormKey).ToList();
        var origin = all[^1].Record.Print().Split('\n').Select(l => l.TrimEnd()).ToList();
        foreach (var c in all)
        {
            var r = c.Record;
            // GF_DIFF=1 : lignes de l'enregistrement qui diffèrent de l'original (Skyrim.esm)
            if (Environment.GetEnvironmentVariable("GF_DIFF") == "1" && c != all[^1])
            {
                var lines = r.Print().Split('\n').Select(l => l.TrimEnd()).ToList();
                var removed = origin.Except(lines).ToList();
                var added = lines.Except(origin).ToList();
                Console.WriteLine($"--- {c.ModKey} : {added.Count} lignes ajoutées/modifiées, {removed.Count} retirées");
                foreach (var l in added.Take(40)) Console.WriteLine("   + " + l.Trim());
                foreach (var l in removed.Take(20)) Console.WriteLine("   - " + l.Trim());
            }
            var skin = ctx.Cache.TryResolve<IArmorGetter>(r.Skin.FormKey, out var s) ? s.EditorID : r.Skin.FormKey.ToString();
            Console.WriteLine($"{c.ModKey,-50} peau={skin} headF={string.Join(",", (r.HeadData?.Female?.HeadParts ?? []).Select(h => ctx.Cache.TryResolve<IHeadPartGetter>(h.Head.FormKey, out var x) ? x.EditorID : "?"))}");
        }
        if (ctx.Cache.TryResolve<IArmorGetter>(race.Skin.FormKey, out var arm))
            foreach (var aa in arm.Armature)
                if (ctx.Cache.TryResolve<IArmorAddonGetter>(aa.FormKey, out var addon) && addon.Race.FormKey == race.FormKey)
                {
                    var tx = addon.SkinTexture?.Female.FormKeyNullable is { } k && ctx.Cache.TryResolve<ITextureSetGetter>(k, out var t) ? $"{t.EditorID} d={t.Diffuse}" : "-";
                    Console.WriteLine($"  {addon.EditorID} ({addon.FormKey.ModKey}) F={addon.WorldModel?.Female?.File} texture F={tx}");
                }
    }

    /// <summary>Détail des headparts d'un preset (modèle, fichiers .tri, parties supplémentaires), en JSON pour les outils Python.</summary>
    public static void HeadParts(Context ctx, string preset, string? raceEdid)
        => Console.WriteLine(HeadPartsJson(ctx, preset, raceEdid).ToJsonString(new() { WriteIndented = true }));

    /// <param name="importNif">tête RaceMenu dont la géométrie remplace celle calculée (voir HeadImports), chemin absolu</param>
    public static System.Text.Json.Nodes.JsonObject HeadPartsJson(Context ctx, string preset, string? raceEdid, bool female = true, string? importNif = null)
    {
        var j = Jslot.Load(Path.Combine(ctx.PresetsDir, preset + ".jslot"));
        var race = raceEdid != null ? ctx.Find<IRaceGetter>(raceEdid) : null;
        var effective = GenerateFollower.HeadParts.Effective(ctx, j, race, female);
        // Followers autonomes : les formes du FaceGen portent le nom de nos headparts recréés (GF_…)
        var allowed = ctx.Config.Standalone ? Standalone.Allowed(ctx, race, Standalone.Face(effective)) : null;
        string Name(IHeadPartGetter hp) => allowed != null ? Standalone.Name(ctx, hp, allowed, race) : hp.EditorID ?? "";
        var list = new System.Text.Json.Nodes.JsonArray();
        void Add(IHeadPartGetter hp, int rmType, bool extra)
        {
            var parts = new System.Text.Json.Nodes.JsonArray();
            foreach (var p in hp.Parts) parts.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = p.PartType?.ToString(), ["file"] = p.FileName?.ToString() });
            // TextureSet du headpart : remplace les textures du .nif (slots TX00..TX07)
            System.Text.Json.Nodes.JsonArray? txst = null;
            if (!hp.TextureSet.IsNull && ctx.Cache.TryResolve<ITextureSetGetter>(hp.TextureSet.FormKey, out var ts))
                txst = new System.Text.Json.Nodes.JsonArray(new[] { ts.Diffuse, ts.NormalOrGloss, ts.EnvironmentMaskOrSubsurfaceTint, ts.GlowOrDetailMap,
                    ts.Height, ts.Environment, ts.Multilayer, ts.BacklightMaskOrSpecular }.Select(t => (System.Text.Json.Nodes.JsonNode?)(t?.ToString() ?? "")).ToArray());
            list.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["edid"] = Name(hp), ["srcEdid"] = hp.EditorID, ["formKey"] = hp.FormKey.ToString(), ["rmType"] = rmType, ["hpType"] = hp.Type?.ToString(),
                ["extra"] = extra, ["model"] = hp.Model?.File.ToString(), ["parts"] = parts, ["textureSet"] = txst,
            });
            foreach (var ep in hp.ExtraParts)
                if (ctx.Cache.TryResolve<IHeadPartGetter>(ep.FormKey, out var e)) Add(e, rmType, true);
        }
        // Type RaceMenu du jslot ; pour un headpart ajouté par défaut, son propre type (même numérotation)
        var rmTypes = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, int>();
        foreach (var (key, type) in j.HeadParts)
            if (ctx.TryResolveRm<IHeadPartGetter>(key, out var hp)) rmTypes.TryAdd(hp.FormKey, type);
        foreach (var hp in effective)
            Add(hp, rmTypes.TryGetValue(hp.FormKey, out var t) ? t : (int)(hp.Type ?? 0), false);
        var root = new System.Text.Json.Nodes.JsonObject { ["headParts"] = list, ["import"] = importNif };
        if (race != null)
        {
            root["race"] = race.EditorID;
            root["morphRace"] = race.MorphRace.IsNull ? null : (ctx.Cache.TryResolve<IRaceGetter>(race.MorphRace.FormKey, out var mr) ? mr.EditorID : null);
            var hd = race.HeadData?.Female;
            root["tintMasksFemale"] = hd?.TintMasks.Count ?? 0;
            root["tintMasksMale"] = race.HeadData?.Male?.TintMasks.Count ?? 0;
            root["racePresetsFemale"] = new System.Text.Json.Nodes.JsonArray(hd?.RacePresets.Select(p => (System.Text.Json.Nodes.JsonNode?)p.FormKey.ToString()).ToArray() ?? []);
        }
        return root;
    }

    /// <summary>
    /// Validité de chaque headpart d'un preset (et de ses parties extra) pour une race et un sexe :
    /// le moteur écarte un headpart invalide, et le FaceGen ne correspond alors plus au PNJ.
    /// </summary>
    public static void CheckHeadParts(Context ctx, string preset, string raceEdid, bool female)
    {
        var race = ctx.Need<IRaceGetter>(raceEdid, "checkhp");
        var j = Jslot.Load(Path.Combine(ctx.PresetsDir, preset + ".jslot"));
        void Check(IHeadPartGetter hp, string indent)
        {
            var sex = hp.Flags.HasFlag(HeadPart.Flag.Female) ? (hp.Flags.HasFlag(HeadPart.Flag.Male) ? "M+F" : "F") : (hp.Flags.HasFlag(HeadPart.Flag.Male) ? "M" : "-");
            string races = "aucune liste";
            bool ok = true;
            if (!hp.ValidRaces.IsNull)
            {
                if (ctx.Cache.TryResolve<IFormListGetter>(hp.ValidRaces.FormKey, out var fl))
                {
                    ok = fl.Items.Any(i => i.FormKey == race.FormKey);
                    races = $"{fl.EditorID} ({fl.Items.Count}) : {(ok ? "contient la race" : "SANS la race")}";
                }
                else { ok = false; races = "liste introuvable"; }
            }
            bool sexOk = female ? hp.Flags.HasFlag(HeadPart.Flag.Female) : hp.Flags.HasFlag(HeadPart.Flag.Male);
            var verdict = ok && sexOk ? "ok" : "INVALIDE";
            Console.WriteLine($"{indent}{verdict,-9} {hp.EditorID,-40} type={hp.Type,-10} sexe={sex,-3} modèle={(hp.Model == null ? "non" : "oui")}  {races}  flags={hp.Flags}");
            foreach (var ep in hp.ExtraParts)
                if (ctx.Cache.TryResolve<IHeadPartGetter>(ep.FormKey, out var e)) Check(e, indent + "    ");
        }
        foreach (var (key, _) in j.HeadParts)
            if (ctx.TryResolveRm<IHeadPartGetter>(key, out var hp)) Check(hp, "  ");
            else Console.WriteLine($"  INTROUVABLE {key}");
    }

    /// <summary>Masques de teinte (TINI/TINT) d'une race, féminins : index, type, texture.</summary>
    public static void Tints(Context ctx, string raceEdid)
    {
        var race = ctx.Need<IRaceGetter>(raceEdid, "tints");
        foreach (var t in race.HeadData?.Female?.TintMasks ?? [])
            Console.WriteLine($"{t.Index,4} {t.MaskType,-14} {t.FileName}");
    }

    /// <summary>Relit un plugin généré : en-tête, masters, et quelques enregistrements avec leurs scripts.</summary>
    public static void Inspect(string path)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        Console.WriteLine($"Flags {mod.ModHeader.Flags}  version {mod.ModHeader.Stats.Version}  enregistrements {mod.ModHeader.Stats.NumRecords}  nextID {mod.ModHeader.Stats.NextFormID:X}");
        Console.WriteLine("Masters : " + string.Join(", ", mod.ModHeader.MasterReferences.Select(m => m.Master.FileName)));
        foreach (var g in mod.EnumerateMajorRecords().GroupBy(r => r.GetType().Name.Replace("BinaryOverlay", "")))
        {
            Console.WriteLine($"  {g.Key,-22} {g.Count()}");
            foreach (var r in g.Take(2))
            {
                var vm = (r as IHaveVirtualMachineAdapterGetter)?.VirtualMachineAdapter;
                var scripts = vm == null ? "" : "  scripts: " + string.Join(", ", vm.Scripts.Select(s => s.Name + "(" + string.Join(",", s.Properties.Select(p => p.Name)) + ")"));
                Console.WriteLine($"     {r.FormKey.ID:X8} {r.EditorID}{scripts}");
            }
        }
        foreach (var q in mod.Quests) Console.WriteLine($"  Quête {q.EditorID} flags={q.Flags}");
        if (mod.Npcs.FirstOrDefault() is { } npc)
            Console.WriteLine($"  NPC exemple : QNAM={npc.TextureLighting} FTST={npc.HeadTexture.FormKeyNullable} {npc.Name} race={npc.Race.FormKey} voix={npc.Voice.FormKey} headparts={npc.HeadParts.Count} taille={npc.Height} vitesse={npc.Configuration.SpeedMultiplier} flags={npc.Configuration.Flags}");
        // Skins : peau (WNAM) de chaque PNJ, ArmorAddons et TextureSets du plugin
        string Name(Mutagen.Bethesda.Plugins.FormKey k) => mod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == k)?.EditorID ?? k.ToString();
        if (Environment.GetEnvironmentVariable("GF_DUMP") == "1")
            foreach (var n in mod.Npcs)
                Console.WriteLine($"  Headparts {n.Name} ({n.FormKey.ID:X8}) : {string.Join(", ", n.HeadParts.Select(h => Name(h.FormKey)))}");
        foreach (var n in mod.Npcs.Where(n => !n.WornArmor.IsNull))
            Console.WriteLine($"  Peau {n.Name} : WNAM={Name(n.WornArmor.FormKey)} FTST={(n.HeadTexture.IsNull ? "-" : Name(n.HeadTexture.FormKey))} QNAM={n.TextureLighting?.R},{n.TextureLighting?.G},{n.TextureLighting?.B}");
        foreach (var a in mod.Armors)
            Console.WriteLine($"  ARMO {a.EditorID} : {string.Join(", ", a.Armature.Select(x => Name(x.FormKey)))}");
        foreach (var aa in mod.ArmorAddons)
            Console.WriteLine($"  ARMA {aa.EditorID} F={aa.WorldModel?.Female?.File} skinF={(aa.SkinTexture?.Female.IsNull != false ? "-" : Name(aa.SkinTexture.Female.FormKey))} " +
                string.Join(" ", (aa.WorldModel?.Female?.AlternateTextures ?? []).Select(t => $"alt {t.Name}#{t.Index}->{Name(t.NewTexture.FormKey)}"))
                + $" | M={aa.WorldModel?.Male?.File} skinM={(aa.SkinTexture?.Male.IsNull != false ? "-" : Name(aa.SkinTexture.Male.FormKey))} "
                + string.Join(" ", (aa.WorldModel?.Male?.AlternateTextures ?? []).Select(t => $"alt {t.Name}#{t.Index}->{Name(t.NewTexture.FormKey)}")));
        foreach (var t in mod.TextureSets.Where(t => t.EditorID?.StartsWith("GF_Skin_") == true))
            Console.WriteLine($"  TXST {t.EditorID} flags={t.Flags} d={t.Diffuse} n={t.NormalOrGloss} sk={t.GlowOrDetailMap} s={t.BacklightMaskOrSpecular}");
    }
}
