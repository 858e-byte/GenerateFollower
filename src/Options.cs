using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Options d'un follower choisies dans le Builder (tenue, sorts, atouts, niveau, comportement, taille, mariage),
/// mémorisées dans work\options_choice.json : { "id du preset": { "tenue": …, "sorts": […], … } }. Une option absente =
/// comportement d'avant (ESP identique). Le style de combat, la classe (Combat) et la voix (Voices) ont leur propre fichier.
/// Tous les enregistrements proposés viennent de Skyrim.esm et des DLC, référencés par FormKey : aucune dépendance de plus.
/// </summary>
public static partial class Options
{
    internal static readonly string[] Masters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    // ------------------------------------------------------------------ stockage

    static string ChoicePath(Context ctx) => Path.Combine(ctx.WorkDir, "options_choice.json");

    public static JsonObject LoadAll(Context ctx) =>
        File.Exists(ChoicePath(ctx)) && JsonNode.Parse(File.ReadAllText(ChoicePath(ctx))) is JsonObject o ? o : new JsonObject();

    /// <summary>Options d'un preset (objet vide si aucune).</summary>
    public static JsonObject For(Context ctx, string id) => LoadAll(ctx)[id] is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();

    /// <summary>Remplace une option d'un preset (null : option retirée ; preset sans option : entrée retirée).</summary>
    public static void Set(Context ctx, string id, string key, JsonNode? value)
    {
        var all = LoadAll(ctx);
        var mine = all[id] as JsonObject ?? new JsonObject();
        if (value == null) mine.Remove(key); else mine[key] = value.DeepClone();
        all.Remove(id);
        if (mine.Count > 0) all[id] = mine;
        var sorted = new JsonObject();
        foreach (var kv in all.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) sorted[kv.Key] = kv.Value?.DeepClone();
        File.WriteAllText(ChoicePath(ctx), sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
    }

    /// <summary>Enregistrements du jeu de base et des DLC (versions d'origine), pour les catalogues.</summary>
    internal static List<ISkyrimModGetter> MasterMods(Context ctx) =>
        ctx.Env.LoadOrder.ListedOrder.Where(l => Masters.Contains(l.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase) && l.Mod != null)
            .Select(l => l.Mod!).ToList();

    /// <summary>Applique au PNJ toutes les options choisies (au build, après BuildNpc). Sans option : rien.</summary>
    public static void Apply(Context ctx, SkyrimMod mod, Npc npc, PresetRow row, JsonObject opts, StringBuilder report)
    {
        if (opts.Count == 0) return;
        ApplyOutfit(ctx, npc, row, opts, report);
        ApplySpells(ctx, npc, row, opts, report);
        ApplyPerks(ctx, npc, row, opts, report);
        ApplyAdvanced(npc, row, opts, report);
        ApplyMarriage(ctx, npc, row, opts, report);
        ApplyWeapons(ctx, npc, row, opts, report);
    }

    /// <summary>Fiche du Builder : catalogues et choix d'un preset.</summary>
    public static JsonObject Info(Context ctx, PresetRow row)
    {
        var opts = For(ctx, row.Id);
        return new JsonObject
        {
            ["choix"] = opts,
            ["tenues"] = OutfitsJson(ctx), ["sorts"] = SpellsJson(ctx), ["atouts"] = PerksJson(ctx), ["avance"] = AdvancedJson(), ["mariageVoix"] = MarriageVoiceJson(ctx, row), ["armes"] = WeaponsJson(ctx), ["archetypes"] = ArchetypesJson(ctx), ["lieux"] = PlacesJson(ctx),
            ["tenueDefaut"] = Outfits(ctx).FirstOrDefault(o => o.EditorId == ctx.Config.DefaultOutfit) is { } d ? $"{d.Name} ({d.EditorId})" : ctx.Config.DefaultOutfit,
        };
    }

    /// <summary>POST du Builder : { "cle": valeur | null } pour une option, après contrôle de la valeur.</summary>
    public static void Save(Context ctx, PresetRow row, JsonObject body)
    {
        // toutes les clés d'abord : rien n'est enregistré si l'une est inconnue
        if (body.Select(kv => kv.Key).FirstOrDefault(k => !OptionKeys.Contains(k)) is { } unknown) throw new GfException(L.T("Option inconnue : {0}", unknown));
        foreach (var (key, value) in body.ToList())
        {
            switch (key)
            {
                case "tenue":
                    var t = value?.GetValue<string>();
                    if (!string.IsNullOrEmpty(t) && !Outfits(ctx).Any(o => o.EditorId == t)) throw new GfException(L.T("Tenue inconnue : {0}", t));
                    Set(ctx, row.Id, "tenue", string.IsNullOrEmpty(t) ? null : JsonValue.Create(t));
                    break;
                case "sorts":
                    SaveSpells(ctx, row, value);
                    break;
                case "atouts":
                    SavePerks(ctx, row, value);
                    break;
                case "niveau": case "statut": case "agressivite": case "courage": case "moralite": case "assistance": case "taille":
                    SaveAdvanced(ctx, row, key, value);
                    break;
                case "mariage":
                    SaveMarriage(ctx, row, value);
                    break;
                case "armes":
                    SaveWeapons(ctx, row, value);
                    break;
                case "lieu":
                    SavePlace(ctx, row, value);
                    break;
                case SmpHair.Option:
                    // cheveux SMP : null = automatique (animés si Faster HDT-SMP est installé), true = animés, false = rigides
                    if (value is JsonValue v && !v.TryGetValue<bool>(out _)) throw new GfException(L.T("Valeur refusée pour {0} : {1}", key, v.ToJsonString()));
                    Set(ctx, row.Id, SmpHair.Option, value is JsonValue ? JsonValue.Create(value.GetValue<bool>()) : null);
                    break;
                default:
                    throw new GfException(L.T("Option inconnue : {0}", key));
            }
        }
    }

    /// <summary>Clés acceptées par Save (une par option de la fiche).</summary>
    static readonly HashSet<string> OptionKeys = ["tenue", "sorts", "atouts", "niveau", "statut", "agressivite", "courage", "moralite", "assistance", "taille", "mariage", "armes", "lieu", SmpHair.Option];

    // ------------------------------------------------------------------ tenue

    /// <summary>Tenues proposées (EditorID du jeu) : sans casque quand le jeu a cette variante, pour voir le visage.</summary>
    static readonly string[] OutfitIds =
    [
        // armures (groupe lourde / légère calculé d'après le plastron)
        "ArmorIronNoHelmetOutfit", "ArmorIronBandedNoHelmetOutfit", "ArmorSteelNoHelmetOutfit", "ArmorSteelPlateNoHelmetOutfit",
        "ArmorDwarvenNoHelmetOutfit", "ArmorOrcishNoHelmetOutfit", "ArmorEbonyNoHelmetOutfit", "ArmorDaedricOutfitNoHelmet",
        "ArmorDragonplateAllOutfit", "ArmorImperialHeavyOutfitNoHelmet", "ArmorCompanionsOutfitNoHelmet", "ArmorBladesOutfitNoHelmet",
        "DLC1OutfitDawnguard01Heavy", "ArmorLeatherSimpleOutfit", "ArmorLeatherNoHelmetOutfit", "ArmorHideSimpleOutfit",
        "ArmorStuddedSimpleOutfit", "ArmorScaledNoHelmetOutfit", "ArmorElvenNoHelmetOutfit", "ArmorElvenGildedNoHelmetOutfit",
        "ArmorGlassAllOutfit", "ArmorDragonscaleAllOutfit", "ArmorImperialLightOutfitNoHelmet", "ArmorStormcloakOutfitNoHelmet",
        "NightingaleOutfit", "DBArmorNoHood", "ThalmorArmorNoHelmetOutfit", "ForswornArmorMeleeOutfit", "SaviorsHideOutfit",
        "DLC1VolkiharVampireOutfitRed", "DLC1OutfitDawnguard03", "DLC2MoragTongOutfit",
        // robes et vêtements
        "ClothesMageRobesOutfitNoHood", "MageArchmageOutfitNoHood",
        "NecromancerOutfitNoHood", "ThalmorOutfitNoHood", "DBRobesNoHood", "MonkOutfitNoHood", "PsijicMonkOutfit",
        "VigilantOfStendarrOutfitNoHood", "DLC1VampireClotheOnly", "DLC2DarkElfTempleOutfitNoHood",
        "DLC2DarkElfCommonOutfitBlueNoHood", "DLC2SkaalOutfitNoHat", "FineClothesOutfit01", "MerchantClothesOutfit01NoHat", "HunterOutfit01", "FarmClothesOutfit01", "ClothesTavernWenchOutfit",
    ];

    /// <summary>Tenue du catalogue : Group = armure lourde / légère / vêtements (d'après la pièce de torse), Name = nom en
    /// jeu de cette pièce, Items = noms en jeu de toutes les pièces. Lu dans le jeu de base et les DLC, pas dans les versions
    /// des mods (Armor_Perk_Overhaul rend l'armure de cuir lourde chez l'utilisateur : le classement reste celui du jeu).</summary>
    public sealed record OutfitChoice(string EditorId, FormKey Key, string Group, string Name, string Items);

    static List<OutfitChoice>? outfits;

    public static List<OutfitChoice> Outfits(Context ctx)
    {
        if (outfits != null) return outfits;
        var byEdid = MasterMods(ctx).SelectMany(m => m.Outfits).Where(o => o.EditorID != null)
            .GroupBy(o => o.EditorID!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var masters = MasterMods(ctx);
        var armors = new Dictionary<FormKey, IArmorGetter>();
        var lists = new Dictionary<FormKey, ILeveledItemGetter>();
        foreach (var m in masters)   // ordre de chargement : Update.esm et les DLC remplacent Skyrim.esm
        {
            foreach (var a in m.Armors) armors[a.FormKey] = a;
            foreach (var l in m.LeveledItems) lists[l.FormKey] = l;
        }
        var list = new List<OutfitChoice>();
        foreach (var id in OutfitIds)
        {
            if (!byEdid.TryGetValue(id, out var o)) continue;
            var pieces = (o.Items ?? []).SelectMany(i => Pieces(i.FormKey, armors, lists, 0)).DistinctBy(a => a.FormKey).ToList();
            var body = pieces.FirstOrDefault(a => a.BodyTemplate?.FirstPersonFlags.HasFlag(BipedObjectFlag.Body) == true);
            if (body == null) continue;   // pas de pièce de torse : pas une tenue complète
            string group = body.BodyTemplate!.ArmorType switch
            {
                ArmorType.HeavyArmor => L.T("Armure lourde"), ArmorType.LightArmor => L.T("Armure légère"), _ => L.T("Robes et vêtements"),
            };
            list.Add(new(o.EditorID!, o.FormKey, group, body.Name?.String ?? body.EditorID ?? id,
                string.Join(", ", pieces.Select(p => p.Name?.String ?? p.EditorID).Distinct())));
        }
        return outfits = list;
    }

    /// <summary>Armures d'un élément de tenue : l'armure elle-même ; pour une liste nivelée, toutes ses entrées si elle a le
    /// drapeau « Use All » (ensembles du jeu : ArmorIronSetNoHelmet…), sinon la première (un tirage parmi des variantes).</summary>
    static IEnumerable<IArmorGetter> Pieces(FormKey fk, Dictionary<FormKey, IArmorGetter> armors, Dictionary<FormKey, ILeveledItemGetter> lists, int depth)
    {
        if (depth > 4) yield break;
        if (armors.TryGetValue(fk, out var a)) { yield return a; yield break; }
        if (!lists.TryGetValue(fk, out var lvl)) yield break;
        var entries = (lvl.Entries ?? []).Where(e => e.Data != null).ToList();
        foreach (var e in lvl.Flags.HasFlag(LeveledItem.Flag.UseAll) ? entries : entries.Take(1))
            foreach (var p in Pieces(e.Data!.Reference.FormKey, armors, lists, depth + 1)) yield return p;
    }

    static JsonArray OutfitsJson(Context ctx)
    {
        // deux tenues au même nom (même pièce de torse, autres accessoires) : EditorID en plus pour les distinguer
        var twins = Outfits(ctx).GroupBy(o => o.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return new(Outfits(ctx)
            .OrderBy(o => o.Group == L.T("Armure lourde") ? 0 : o.Group == L.T("Armure légère") ? 1 : 2).ThenBy(o => o.Name, StringComparer.CurrentCulture)
            .Select(o => (JsonNode)new JsonObject
            {
                ["id"] = o.EditorId, ["groupe"] = o.Group, ["nom"] = twins.Contains(o.Name) ? $"{o.Name} ({o.EditorId})" : o.Name,
                ["pieces"] = o.Items, ["source"] = o.Key.ModKey.FileName.String,
            }).ToArray());
    }

    static void ApplyOutfit(Context ctx, Npc npc, PresetRow row, JsonObject opts, StringBuilder report)
    {
        if (opts["tenue"]?.GetValue<string>() is not { } id) return;
        var o = Outfits(ctx).FirstOrDefault(x => x.EditorId == id);
        if (o != null && ctx.Cache.TryResolve<IOutfitGetter>(o.Key, out var outfit))
        {
            npc.DefaultOutfit.SetTo(outfit);
            report.AppendLine($"OPTION  {row.Id}  tenue {o.EditorId} ({o.Key})");
        }
        else report.AppendLine($"ATTENTION {row.Id}  tenue {id} introuvable : tenue de config.json gardée");
    }
}
