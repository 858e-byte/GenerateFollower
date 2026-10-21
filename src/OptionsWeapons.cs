using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Armes de départ : objets du jeu de base ajoutés à l'inventaire du PNJ (CNTO). Sans eux, un follower généré n'a aucune
/// arme : un style d'archer ou de bretteur n'a d'effet qu'avec un arc ou une épée. Le PNJ équipe la meilleure arme de son
/// inventaire (et un bouclier avec une arme à une main). Choix : options_choice.json, "armes": [EditorID…] ; flèches : 50.
/// </summary>
public static partial class Options
{
    /// <summary>Objets proposés (EditorID de Skyrim.esm et des DLC), contrôlés au chargement : un objet introuvable est écarté
    /// (commande « options » : ligne « arme ABSENTE »).</summary>
    static readonly string[] WeaponIds =
    [
        "IronSword", "SteelSword", "ElvenSword", "GlassSword", "EbonySword",
        "IronDagger", "SteelDagger", "ElvenDagger", "GlassDagger", "EbonyDagger",
        "IronMace", "SteelMace", "IronWarAxe", "SteelWarAxe",
        "SteelGreatsword", "ElvenGreatsword", "SteelBattleAxe", "SteelWarhammer",
        "HuntingBow", "LongBow", "ElvenBow", "GlassBow",
        "IronArrow", "SteelArrow", "ElvenArrow", "GlassArrow",
        "ArmorIronShield", "ArmorSteelShield", "ArmorElvenShield",
        "StaffFlames", "StaffFirebolt", "StaffFrostbite", "StaffSparks",
    ];

    /// <param name="Kind">nature, indépendante de la langue : arc, fleches, bouclier, baton, melee (contrôles de cohérence)</param>
    public sealed record WeaponChoice(string EditorId, FormKey Key, string Group, string Name, int Count, string Kind);

    static List<WeaponChoice>? weapons;
    static List<string> missingWeapons = [];

    public static List<WeaponChoice> Weapons(Context ctx)
    {
        if (weapons != null) return weapons;
        var masters = MasterMods(ctx);
        var byEdid = new Dictionary<string, IMajorRecordGetter>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in masters)
        {
            foreach (var w in m.Weapons) if (w.EditorID != null) byEdid[w.EditorID] = w;
            foreach (var a in m.Ammunitions) if (a.EditorID != null) byEdid[a.EditorID] = a;
            foreach (var a in m.Armors) if (a.EditorID != null) byEdid[a.EditorID] = a;
        }
        var list = new List<WeaponChoice>();
        missingWeapons = [];
        foreach (var id in WeaponIds)
        {
            if (!byEdid.TryGetValue(id, out var rec)) { missingWeapons.Add(id); continue; }
            (string group, string name, int count, string kind) = rec switch
            {
                IWeaponGetter w => (WeaponGroup(w), w.Name?.String ?? id, 1, w.Data?.AnimationType switch
                {
                    WeaponAnimationType.Bow or WeaponAnimationType.Crossbow => "arc",
                    WeaponAnimationType.Staff => "baton",
                    _ => "melee",
                }),
                IAmmunitionGetter a => (L.T("Flèches"), a.Name?.String ?? id, 50, "fleches"),
                IArmorGetter s => (L.T("Boucliers"), s.Name?.String ?? id, 1, "bouclier"),
                _ => ("?", id, 1, "?"),
            };
            list.Add(new(id, rec.FormKey, group, name, count, kind));
        }
        return weapons = list;
    }

    public static IReadOnlyList<string> MissingWeapons => missingWeapons;

    static string WeaponGroup(IWeaponGetter w) => w.Data?.AnimationType switch
    {
        WeaponAnimationType.OneHandSword or WeaponAnimationType.OneHandAxe or WeaponAnimationType.OneHandMace => L.T("Armes à une main"),
        WeaponAnimationType.OneHandDagger => L.T("Dagues"),
        WeaponAnimationType.TwoHandSword or WeaponAnimationType.TwoHandAxe => L.T("Armes à deux mains"),
        WeaponAnimationType.Bow or WeaponAnimationType.Crossbow => L.T("Arcs"),
        WeaponAnimationType.Staff => L.T("Bâtons"),
        _ => L.T("Autres"),
    };

    static JsonArray WeaponsJson(Context ctx) => new(Weapons(ctx).Select(w => (JsonNode)new JsonObject
    {
        ["id"] = w.EditorId, ["groupe"] = w.Group, ["nom"] = w.Count > 1 ? $"{w.Name} ×{w.Count}" : w.Name, ["source"] = w.Key.ModKey.FileName.String,
    }).ToArray());

    static void SaveWeapons(Context ctx, PresetRow row, JsonNode? value)
    {
        var ids = value is JsonArray a ? a.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList() : [];
        var known = Weapons(ctx).Select(w => w.EditorId).ToHashSet();
        if (ids.FirstOrDefault(i => !known.Contains(i)) is { } bad) throw new GfException(L.T("Arme inconnue : {0}", bad));
        Set(ctx, row.Id, "armes", ids.Count == 0 ? null : new JsonArray(ids.Select(i => (JsonNode)i).ToArray()));
    }

    static void ApplyWeapons(Context ctx, Npc npc, PresetRow row, JsonObject opts, StringBuilder report)
    {
        if (opts["armes"] is not JsonArray ids) return;
        var byId = Weapons(ctx).ToDictionary(w => w.EditorId);
        var added = new List<string>();
        foreach (var id in ids.Select(x => x?.GetValue<string>() ?? ""))
        {
            if (!byId.TryGetValue(id, out var w) || !ctx.Cache.TryResolve<IItemGetter>(w.Key, out var item))
            { report.AppendLine($"ATTENTION {row.Id}  objet {id} introuvable : ignoré"); continue; }
            (npc.Items ??= []).Add(new ContainerEntry { Item = new ContainerItem { Item = item.ToLink(), Count = w.Count } });
            added.Add(w.Count > 1 ? $"{id} x{w.Count}" : id);
        }
        if (added.Count > 0) report.AppendLine($"OPTION  {row.Id}  armes {string.Join(", ", added)}");
    }
}
