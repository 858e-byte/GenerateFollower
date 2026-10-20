using System.Diagnostics;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// build --diag : ajoute une page « DIAG » au menu d'invocation, avec des variantes qui isolent la cause
/// d'un FaceGen ignoré en jeu.
/// Déjà établi (DIAG2) : une copie exacte d'Alenye dans notre plugin obtient son FaceGen ; changer les champs
/// FTST/NAM9/TINI/WNAM/DNAM d'Asagi ne change rien. Test en cours : les cheveux d'Anna et d'Asagi ne sont pas
/// valides pour 00UBE_NordRace (liste HeadPartsAllRacesMinusBeast) ; on les retire de l'enregistrement et du NIF.
/// </summary>
public static class Diagnostics
{
    const string AlenyePlugin = "UBE Alenye Follower.esp";
    const string AlenyeEdid = "UBE_Alenye_Follower";

    public static void AddVariants(Context ctx, SkyrimMod mod, List<(PresetRow Row, Npc Npc)> built,
        string geomDir, string tintDir, FormList pagesList, List<string> pageNames)
    {
        var page = mod.FormLists.AddNew("GF_Page_DIAG");
        string fid(Npc n) => n.FormKey.ID.ToString("X8");
        string geom(Npc n) => Path.Combine(geomDir, fid(n) + ".nif");
        string tint(Npc n) => Path.Combine(tintDir, fid(n) + ".dds");

        // Témoin positif : copie exacte d'Alenye dans notre plugin, avec son FaceGen et sa teinte
        if (!ctx.Cache.TryResolve<INpcGetter>(AlenyeEdid, out var alenye))
            throw new GfException($"{AlenyeEdid} introuvable : {AlenyePlugin} doit être actif pour --diag.");
        var copy = mod.Npcs.DuplicateInAsNewRecord(alenye, "GF_DIAG_AlenyeCopie");
        copy.Name = "DIAG 1 Alenye copie";
        var fg = Path.Combine(ctx.DataDir, "meshes", "actors", "character", "FaceGenData");
        Python(ctx, "diag_marker.py", Path.Combine(fg, "FaceGeom", AlenyePlugin, $"{alenye.FormKey.ID:X8}.nif"), geom(copy));
        // La teinte est cherchée au chemin calculé depuis le FormID : sans elle, le shader FaceGen plante
        // (Community Shaders, TruePBR SetupMaterial)
        File.Copy(Path.Combine(ctx.DataDir, "textures", "actors", "character", "FaceGenData", "FaceTint", AlenyePlugin, $"{alenye.FormKey.ID:X8}.dds"), tint(copy), true);
        page.Items.Add(copy.ToLink());

        // Asagi et Anna sans cheveux, dans l'enregistrement comme dans le NIF
        int n = 2;
        foreach (var name in new[] { "Asagi", "Anna" })
        {
            var (row, src) = built.FirstOrDefault(b => b.Row.Nom == name);
            if (src == null) throw new GfException($"--diag : {name} doit faire partie du lot (--only).");
            var hp = JsonNode.Parse(File.ReadAllText(Path.Combine(ctx.WorkDir, "headparts", row.Id + ".json")))!["headParts"]!.AsArray();

            // le headpart de cheveux et les parties extra qui le suivent dans la fiche
            var kept = new JsonArray();
            string? hairKey = null;
            bool inHair = false;
            foreach (var p in hp)
            {
                bool extra = p!["extra"]!.GetValue<bool>();
                if (!extra) inHair = p["hpType"]?.GetValue<string>() == "Hair";
                if (inHair) { if (!extra) hairKey = p["formKey"]!.GetValue<string>(); continue; }
                kept.Add(p.DeepClone());
            }
            if (hairKey == null) throw new GfException($"--diag : pas de cheveux pour {name}.");

            var v = mod.Npcs.DuplicateInAsNewRecord(src, $"GF_DIAG_{name}SansCheveux");
            v.Name = $"DIAG {n++} {name} sans cheveux";
            v.HeadParts.RemoveAll(l => l.FormKey.ToString() == hairKey);

            var json = Path.Combine(Context.Dir(Path.Combine(ctx.WorkDir, "diag")), $"{row.Id}_sans_cheveux.json");
            File.WriteAllText(json, new JsonObject { ["headParts"] = kept }.ToJsonString());
            Assemble(ctx, row, json, geom(v), $@"textures\Actors\Character\FaceGenData\FaceTint\{ctx.Config.PluginName}\{fid(v)}.dds");
            File.Copy(tint(src), tint(v), true);
            page.Items.Add(v.ToLink());
        }

        pagesList.Items.Add(page.ToLink());
        pageNames.Add($"DIAG ({page.Items.Count})");
    }

    /// <summary>
    /// Pouvoir « GF Diagnostic » (script GF_DiagQuest) et liste des formes de chaque FaceGen
    /// (StorageUtilData\GenerateFollower\diag_shapes.json, clé = FormID local en décimal).
    /// À appeler une fois les NIF écrits dans modDir.
    /// </summary>
    public static void AddDiagPower(Context ctx, SkyrimMod mod, FormList pagesList, string modDir)
    {
        var quest = mod.Quests.AddNew("GF_DiagQuest");
        var mgef = mod.MagicEffects.AddNew("GF_DiagEffect");
        mgef.Name = "GF Diagnostic";
        mgef.Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.Script);
        mgef.CastType = CastType.FireAndForget;
        mgef.TargetType = TargetType.Self;
        mgef.Flags = MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea | MagicEffect.Flag.Painless | MagicEffect.Flag.NoHitEvent;
        mgef.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts = { new ScriptEntry { Name = "GF_DiagEffect", Properties = { new ScriptObjectProperty { Name = "Owner", Object = quest.ToLink<ISkyrimMajorRecordGetter>() } } } },
        };
        var spell = mod.Spells.AddNew("GF_DiagPower");
        spell.Name = "GF Diagnostic";
        spell.Type = SpellType.LesserPower;
        spell.CastType = CastType.FireAndForget;
        spell.TargetType = TargetType.Self;
        spell.EquipmentType.SetTo(ctx.Need<IEquipTypeGetter>("Voice", "type d'équipement du pouvoir"));
        spell.Flags = SpellDataFlag.ManualCostCalc;
        spell.Effects.Add(new Effect { BaseEffect = mgef.ToNullableLink(), Data = new EffectData() });
        quest.Name = "GenerateFollower Diagnostic";
        quest.Flags = Quest.Flag.StartGameEnabled;
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts =
            {
                new ScriptEntry
                {
                    Name = "GF_DiagQuest",
                    Properties =
                    {
                        new ScriptObjectProperty { Name = "DiagPower", Object = spell.ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "Pages", Object = pagesList.ToLink<ISkyrimMajorRecordGetter>() },
                    },
                },
            },
        };

        // Noms réels des formes de chaque FaceGen livré : en jeu, on regarde lesquels existent sur la tête chargée
        Python(ctx, "diag_shapes.py",
            Path.Combine(modDir, "meshes", "actors", "character", "FaceGenData", "FaceGeom", ctx.Config.PluginName),
            Path.Combine(modDir, "SKSE", "Plugins", "StorageUtilData", "GenerateFollower", "diag_shapes.json"));
    }

    /// <summary>offline\assemble.py : FaceGen complet depuis une fiche de headparts.</summary>
    static void Assemble(Context ctx, PresetRow row, string json, string outNif, string tintPath)
    {
        Python(ctx, "assemble.py", row.Preset, row.Race, json, outNif, tintPath);
        if (!File.Exists(outNif)) throw new GfException($"assemble.py n'a pas produit {outNif} ({row.Nom}).");
    }

    static void Python(Context ctx, string script, params string[] args) => Py.Run(ctx, script, args);
}
