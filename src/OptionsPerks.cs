using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Atouts d'un follower : ceux des arbres de compétences du jeu (enregistrements AVIF de Skyrim.esm et des DLC, comme
/// l'écran des atouts du joueur), seulement pour les compétences utiles en combat. Un atout à plusieurs rangs (Armsman 1-5…)
/// est une chaîne d'enregistrements (NextPerk) : le choisir donne tous ses rangs, comme un joueur qui l'a monté au maximum.
/// Choix : options_choice.json, "atouts": [EditorID du premier rang…]. Au build : liste d'atouts du PNJ (PRKR, rang 1 pour
/// chaque enregistrement de la chaîne).
/// </summary>
public static partial class Options
{
    /// <summary>Arbres proposés (EditorID de l'AVIF → compétence affichée).</summary>
    static readonly string[] PerkTrees =
    [
        "AVOneHanded", "AVTwoHanded", "AVMarksman", "AVBlock", "AVHeavyArmor", "AVLightArmor", "AVSneak",
        "AVAlteration", "AVConjuration", "AVDestruction", "AVMysticism", "AVRestoration",
    ];

    static string TreeName(string tree) => tree switch
    {
        "AVOneHanded" => L.T("Une main"), "AVTwoHanded" => L.T("Deux mains"), "AVMarksman" => L.T("Archerie"), "AVBlock" => L.T("Parade"),
        "AVHeavyArmor" => L.T("Armure lourde"), "AVLightArmor" => L.T("Armure légère"), "AVSneak" => L.T("Discrétion"),
        "AVAlteration" => L.T("Altération"), "AVConjuration" => L.T("Conjuration"), "AVDestruction" => L.T("Destruction"),
        "AVMysticism" => L.T("Illusion"), "AVRestoration" => L.T("Guérison"), _ => tree,
    };

    public sealed record PerkChoice(string EditorId, FormKey Key, string Skill, string Name, int Ranks, List<FormKey> Chain);

    static List<PerkChoice>? perks;

    public static List<PerkChoice> Perks(Context ctx)
    {
        if (perks != null) return perks;
        var masters = MasterMods(ctx);
        var byKey = new Dictionary<FormKey, IPerkGetter>();
        var avifs = new Dictionary<string, IActorValueInformationGetter>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in masters)
        {
            foreach (var p in m.Perks) byKey[p.FormKey] = p;
            foreach (var a in m.ActorValueInformation) if (a.EditorID != null) avifs[a.EditorID] = a;
        }
        var list = new List<PerkChoice>();
        foreach (var tree in PerkTrees)
        {
            if (!avifs.TryGetValue(tree, out var avif)) continue;
            var skill = TreeName(tree);
            foreach (var node in avif.PerkTree)
            {
                if (node.Perk.IsNull || !byKey.TryGetValue(node.Perk.FormKey, out var perk) || perk.EditorID == null) continue;
                // chaîne des rangs : NextPerk jusqu'au dernier (protégée contre une boucle)
                var chain = new List<FormKey> { perk.FormKey };
                for (var next = perk.NextPerk.FormKeyNullable; next is { } n && !n.IsNull && !chain.Contains(n) && byKey.ContainsKey(n); next = byKey[n].NextPerk.FormKeyNullable)
                    chain.Add(n);
                list.Add(new(perk.EditorID, perk.FormKey, skill, perk.Name?.String ?? perk.EditorID, chain.Count, chain));
            }
        }
        return perks = list.DistinctBy(p => p.Key).ToList();
    }

    static JsonArray PerksJson(Context ctx) => new(Perks(ctx).Select(p => (JsonNode)new JsonObject
    {
        ["id"] = p.EditorId, ["competence"] = p.Skill, ["nom"] = p.Name, ["rangs"] = p.Ranks, ["source"] = p.Key.ModKey.FileName.String,
    }).ToArray());

    static void SavePerks(Context ctx, PresetRow row, JsonNode? value)
    {
        var ids = value is JsonArray a ? a.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList() : [];
        var known = Perks(ctx).Select(p => p.EditorId).ToHashSet();
        if (ids.FirstOrDefault(i => !known.Contains(i)) is { } bad) throw new GfException(L.T("Atout inconnu : {0}", bad));
        Set(ctx, row.Id, "atouts", ids.Count == 0 ? null : new JsonArray(ids.Select(i => (JsonNode)i).ToArray()));
    }

    static void ApplyPerks(Context ctx, Npc npc, PresetRow row, JsonObject opts, StringBuilder report)
    {
        if (opts["atouts"] is not JsonArray ids) return;
        var byId = Perks(ctx).ToDictionary(p => p.EditorId);
        var added = new List<string>();
        foreach (var id in ids.Select(x => x?.GetValue<string>() ?? ""))
        {
            if (!byId.TryGetValue(id, out var p)) { report.AppendLine($"ATTENTION {row.Id}  atout {id} inconnu : ignoré"); continue; }
            foreach (var fk in p.Chain)
            {
                if (!ctx.Cache.TryResolve<IPerkGetter>(fk, out var perk)) { report.AppendLine($"ATTENTION {row.Id}  atout {fk} introuvable : ignoré"); continue; }
                if (npc.Perks?.Any(x => x.Perk.FormKey == fk) == true) continue;
                (npc.Perks ??= []).Add(new PerkPlacement { Perk = perk.ToLink(), Rank = 1 });
            }
            added.Add(p.Ranks > 1 ? $"{id} ({p.Ranks} rangs)" : id);
        }
        if (added.Count > 0) report.AppendLine($"OPTION  {row.Id}  atouts {string.Join(", ", added)}");
    }
}
