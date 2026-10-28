using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// 1.1.1 : armes et pièces d'armure des mods du joueur. Seulement les objets « de base » : jouables, nommés, avec un
/// modèle, ni enchantés ni variantes d'un gabarit (les packs d'enchantements en créent des milliers), ni objets de quête
/// ou artefacts (mots-clés DaedricArtifact, VendorNoSale, MagicDisallowEnchanting). Identifiant « EditorID@plugin » ;
/// le mod devient master du pack.
/// - Armes (et flèches ×50) : ajoutées à la liste des armes de départ (Options.Weapons), inventaire du PNJ.
/// - Pièces d'armure : « tenue composée » (options_choice.json, "tenueMods": [id…]) : au build, une tenue (OTFT) du pack
///   avec ces pièces remplace la tenue par défaut. Deux pièces sur un même emplacement sont refusées. Une pièce sans
///   modèle pour la race du follower (ARMA : race, races supplémentaires, ou race d'armure de la race) serait invisible :
///   signalé dans la fiche, les vérifications et le rapport.
/// </summary>
public static partial class Options
{
    static readonly HashSet<string> QuestKeywords = ["DaedricArtifact", "VendorNoSale", "MagicDisallowEnchanting"];

    static bool IsModRecord(Context ctx, IMajorRecordGetter r) =>
        !Masters.Contains(r.FormKey.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase);

    static bool HasQuestKeyword(Context ctx, IReadOnlyList<IFormLinkGetter<IKeywordGetter>>? keywords) =>
        (keywords ?? []).Any(k => k.TryResolve(ctx.Cache, out var kw) && kw.EditorID != null && QuestKeywords.Contains(kw.EditorID));

    /// <summary>Deux objets d'un même plugin peuvent avoir le même EditorID (BDE_Armor.esp) : le second reçoit
    /// « EditorID#FormID@plugin » pour que l'identifiant reste unique.</summary>
    static List<T> UniqueIds<T>(List<T> list, Func<T, string> id, Func<T, FormKey> key, Func<T, string, T> with) =>
        list.GroupBy(id).SelectMany(g => g.Count() == 1 ? g : g.Select(x => with(x, id(x).Replace("@", $"#{key(x).ID:X6}@")))).ToList();

    /// <summary>Armes et flèches liées (sorts d'invocation : « Bound… ») : elles disparaissent hors du sort.</summary>
    static bool IsBound(string editorId, string name) =>
        editorId.Contains("Bound", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Bound ", StringComparison.OrdinalIgnoreCase);

    static bool ExcludedName(string name) =>
        name.Contains("Template", StringComparison.OrdinalIgnoreCase) || name.Contains("Dummy", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Empty", StringComparison.OrdinalIgnoreCase);

    /// <summary>Armes et flèches de base des mods, triées par plugin puis par nom.</summary>
    static List<WeaponChoice> ModWeapons(Context ctx)
    {
        var list = new List<WeaponChoice>();
        foreach (var w in ctx.Env.LoadOrder.PriorityOrder.Weapon().WinningOverrides())
        {
            if (!IsModRecord(ctx, w) || w.EditorID == null || w.Name?.String is not { Length: > 0 } name || ExcludedName(name)) continue;
            if (w.Data == null || w.Data.Flags.HasFlag(WeaponData.Flag.NonPlayable) || !w.ObjectEffect.IsNull || !w.Template.IsNull) continue;
            if (w.Data.AnimationType is WeaponAnimationType.Staff or WeaponAnimationType.HandToHand) continue;   // bâton sans enchantement : inutile
            if (w.Model?.File == null || HasQuestKeyword(ctx, w.Keywords) || IsBound(w.EditorID, name)) continue;
            var plugin = w.FormKey.ModKey.FileName.String;
            list.Add(new($"{w.EditorID}@{plugin}", w.FormKey, WeaponGroup(w), name, 1,
                w.Data.AnimationType is WeaponAnimationType.Bow or WeaponAnimationType.Crossbow ? "arc" : "melee", plugin));
        }
        foreach (var a in ctx.Env.LoadOrder.PriorityOrder.Ammunition().WinningOverrides())
        {
            if (!IsModRecord(ctx, a) || a.EditorID == null || a.Name?.String is not { Length: > 0 } name || ExcludedName(name)) continue;
            if (a.Flags.HasFlag(Ammunition.Flag.NonPlayable) || a.Model?.File == null || HasQuestKeyword(ctx, a.Keywords) || IsBound(a.EditorID, name)) continue;
            var plugin = a.FormKey.ModKey.FileName.String;
            list.Add(new($"{a.EditorID}@{plugin}", a.FormKey, L.T("Flèches"), name, 50, "fleches", plugin));
        }
        return UniqueIds(list.DistinctBy(w => w.Key).ToList(), w => w.EditorId, w => w.Key, (w, i) => w with { EditorId = i }).OrderBy(w => w.Mod, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Name, StringComparer.CurrentCulture).ToList();
    }

    // ------------------------------------------------------------------ pièces d'armure (tenue composée)

    /// <param name="Slots">emplacements occupés (BipedObjectFlag) : deux pièces qui en partagent un ne vont pas ensemble</param>
    public sealed record ArmorPiece(string Id, FormKey Key, string Mod, string Name, string Slot, string Type, BipedObjectFlag Slots);

    static List<ArmorPiece>? armorPieces;

    public static List<ArmorPiece> ArmorPieces(Context ctx)
    {
        if (armorPieces != null) return armorPieces;
        var list = new List<ArmorPiece>();
        foreach (var a in ctx.Env.LoadOrder.PriorityOrder.Armor().WinningOverrides())
        {
            if (!IsModRecord(ctx, a) || a.EditorID == null || a.Name?.String is not { Length: > 0 } name || ExcludedName(name)) continue;
            if (a.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable) || a.BodyTemplate is not { } bt || bt.FirstPersonFlags == 0) continue;
            if (!a.ObjectEffect.IsNull || !a.TemplateArmor.IsNull || a.Armature.Count == 0 || HasQuestKeyword(ctx, a.Keywords)) continue;
            var plugin = a.FormKey.ModKey.FileName.String;
            list.Add(new($"{a.EditorID}@{plugin}", a.FormKey, plugin, name, SlotName(bt.FirstPersonFlags), bt.ArmorType switch
            {
                ArmorType.HeavyArmor => L.T("lourde"), ArmorType.LightArmor => L.T("légère"), _ => L.T("vêtement"),
            }, bt.FirstPersonFlags));
        }
        return armorPieces = UniqueIds(list.DistinctBy(p => p.Key).ToList(), p => p.Id, p => p.Key, (p, i) => p with { Id = i }).OrderBy(p => p.Mod, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Slot).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList();
    }

    static string SlotName(BipedObjectFlag f) =>
        f.HasFlag(BipedObjectFlag.Body) ? L.T("torse") : f.HasFlag(BipedObjectFlag.Shield) ? L.T("bouclier")
        : f.HasFlag(BipedObjectFlag.Head) || f.HasFlag(BipedObjectFlag.Hair) || f.HasFlag(BipedObjectFlag.Circlet) ? L.T("tête")
        : f.HasFlag(BipedObjectFlag.Hands) ? L.T("mains") : f.HasFlag(BipedObjectFlag.Feet) ? L.T("pieds")
        : f.HasFlag(BipedObjectFlag.Amulet) || f.HasFlag(BipedObjectFlag.Ring) ? L.T("bijou") : L.T("autre");

    static JsonArray ArmorPiecesJson(Context ctx) => new(ArmorPieces(ctx).Select(p => (JsonNode)new JsonObject
    {
        ["id"] = p.Id, ["mod"] = p.Mod, ["nom"] = p.Name, ["emplacement"] = p.Slot, ["type"] = p.Type,
    }).ToArray());

    /// <summary>Pièces choisies (dans l'ordre du choix) ; une pièce disparue du jeu est ignorée.</summary>
    static List<ArmorPiece> ChosenPieces(Context ctx, JsonObject opts)
    {
        var byId = ArmorPieces(ctx).ToDictionary(p => p.Id);
        return (opts["tenueMods"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").Where(byId.ContainsKey).Select(i => byId[i]).ToList();
    }

    static void SaveOutfitPieces(Context ctx, PresetRow row, JsonNode? value)
    {
        var ids = value is JsonArray a ? a.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList() : [];
        var byId = ArmorPieces(ctx).ToDictionary(p => p.Id);
        if (ids.FirstOrDefault(i => !byId.ContainsKey(i)) is { } bad) throw new GfException(L.T("Pièce d'armure inconnue : {0}", bad));
        for (int i = 0; i < ids.Count; i++)
            for (int j = 0; j < i; j++)
                if ((byId[ids[i]].Slots & byId[ids[j]].Slots) != 0)
                    throw new GfException(L.T("« {0} » et « {1} » occupent le même emplacement : retire l'une des deux.", byId[ids[j]].Name, byId[ids[i]].Name));
        Set(ctx, row.Id, "tenueMods", ids.Count == 0 ? null : new JsonArray(ids.Select(i => (JsonNode)i).ToArray()));
    }

    /// <summary>La pièce a-t-elle un modèle pour cette race (ARMA de la race, de ses races supplémentaires ou de la race
    /// d'armure de la race) ?</summary>
    static bool PieceFitsRace(Context ctx, ArmorPiece p, IRaceGetter race)
    {
        if (!ctx.Cache.TryResolve<IArmorGetter>(p.Key, out var armor)) return false;
        var races = new HashSet<FormKey> { race.FormKey };
        if (!race.ArmorRace.IsNull) races.Add(race.ArmorRace.FormKey);
        foreach (var l in armor.Armature)
            if (ctx.Cache.TryResolve<IArmorAddonGetter>(l.FormKey, out var arma)
                && (races.Contains(arma.Race.FormKey) || arma.AdditionalRaces.Any(r => races.Contains(r.FormKey))))
                return true;
        return false;
    }

    /// <summary>Fiche : pièces choisies avec, pour chacune, si elle a un modèle pour la race du follower.</summary>
    static JsonArray ChosenPiecesJson(Context ctx, PresetRow row, JsonObject opts)
    {
        var race = ctx.Find<IRaceGetter>(row.Race);
        return new(ChosenPieces(ctx, opts).Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id, ["nom"] = p.Name, ["mod"] = p.Mod, ["emplacement"] = p.Slot, ["race"] = race == null || PieceFitsRace(ctx, p, race),
        }).ToArray());
    }

    /// <summary>Vérification : pièces sans modèle pour la race (invisibles en jeu).</summary>
    static IEnumerable<(string, string)> OutfitPiecesCoherence(Context ctx, PresetRow row, JsonObject opts)
    {
        var race = ctx.Find<IRaceGetter>(row.Race);
        if (race == null) yield break;
        var missing = ChosenPieces(ctx, opts).Where(p => !PieceFitsRace(ctx, p, race)).Select(p => p.Name).ToList();
        if (missing.Count > 0)
            yield return ("attention", L.T("Tenue composée : pas de modèle pour la race {0} pour {1}. Ces pièces seraient invisibles en jeu (il faut un patch de l'armure pour cette race).",
                race.EditorID ?? "", string.Join(", ", missing)));
    }

    static void ApplyOutfitPieces(Context ctx, SkyrimMod mod, Npc npc, PresetRow row, JsonObject opts, StringBuilder report)
    {
        var pieces = ChosenPieces(ctx, opts);
        if (pieces.Count == 0) return;
        var outfit = mod.Outfits.AddNew($"GF_Tenue_{row.Id}");
        outfit.Items = [];
        foreach (var p in pieces) outfit.Items.Add(new FormLink<IOutfitTargetGetter>(p.Key));
        npc.DefaultOutfit.SetTo(outfit);
        // pièce sans modèle pour la race : déjà signalée au rapport par les vérifications (Coherence)
        report.AppendLine($"OPTION  {row.Id}  tenue composée de {pieces.Count} pièce(s) : {string.Join(", ", pieces.Select(p => p.Id))}");
    }
}
