using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Coiffure ajoutée par le Builder (plan : work\diag\coiffure\PLAN.md), pensée pour les presets chauves.
/// ÉTAPE 1 SEULEMENT : catalogue et choix dans la fiche (options_choice.json, "coiffure": { "type": "headpart" |
/// "perruque", "id": FormKey }). Le build ne s'en sert pas encore (étape 3 : perruque dans la peau, ARMA emplacement 31,
/// comme les cheveux SMP ; le FaceGen n'est jamais réécrit).
/// - « headpart » : coiffure ordinaire (headpart de type Hair, jouable, avec un modèle, du sexe du follower et valide
///   pour sa race) : la perruque sera fabriquée depuis son .nif.
/// - « perruque » : pièce d'armure d'un mod qui n'occupe que l'emplacement des cheveux (31, avec ou sans 41).
/// </summary>
public static partial class Options
{
    public const string HairOption = "coiffure";

    public sealed record HairChoice(string Type, FormKey Key, string Mod, string Name);

    /// <summary>Catalogues par race et sexe (les headparts dépendent des deux ; les perruques, non).</summary>
    static readonly Dictionary<string, List<HairChoice>> hairCatalogs = new(StringComparer.OrdinalIgnoreCase);

    public static List<HairChoice> Hairs(Context ctx, PresetRow row)
    {
        var cacheKey = $"{row.Race}|{row.Female}";
        if (hairCatalogs.TryGetValue(cacheKey, out var cached)) return cached;
        var race = ctx.Find<IRaceGetter>(row.Race);
        var sex = row.Female ? HeadPart.Flag.Female : HeadPart.Flag.Male;
        var list = new List<HairChoice>();
        // listes de races déjà vues : la même FormList sert à des centaines de coiffures
        var raceOk = new Dictionary<FormKey, bool>();
        foreach (var hp in ctx.Env.LoadOrder.PriorityOrder.HeadPart().WinningOverrides())
        {
            if (hp.Type != HeadPart.TypeEnum.Hair || hp.EditorID == null || OwnPack(hp.FormKey)) continue;
            // coiffures en plusieurs pièces (Tullius, Jino) : le headpart Hair n'a pas de modèle, ses pièces annexes si
            if (hp.Model?.File == null && !hp.ExtraParts.Any(e => ctx.Cache.TryResolve<IHeadPartGetter>(e.FormKey, out var x) && x.Model?.File != null)) continue;
            if (!hp.Flags.HasFlag(HeadPart.Flag.Playable) || hp.Flags.HasFlag(HeadPart.Flag.IsExtraPart) || !hp.Flags.HasFlag(sex)) continue;
            if (race == null || hp.ValidRaces.IsNull) continue;
            if (!raceOk.TryGetValue(hp.ValidRaces.FormKey, out var ok))
                raceOk[hp.ValidRaces.FormKey] = ok = ctx.Cache.TryResolve<IFormListGetter>(hp.ValidRaces.FormKey, out var fl) && RaceInList(ctx, fl, race);
            if (!ok) continue;
            list.Add(new("headpart", hp.FormKey, hp.FormKey.ModKey.FileName.String, hp.Name?.String is { Length: > 0 } n ? n : hp.EditorID));
        }
        const BipedObjectFlag hairSlots = BipedObjectFlag.Hair | BipedObjectFlag.LongHair;
        foreach (var p in ArmorPieces(ctx))
            if (p.Slots.HasFlag(BipedObjectFlag.Hair) && (p.Slots & ~hairSlots) == 0 && LooksLikeWig(p) && !OwnPack(p.Key))
                list.Add(new("perruque", p.Key, p.Mod, p.Name));
        return hairCatalogs[cacheKey] = list.OrderBy(h => h.Mod, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>La race est dans la liste du headpart, ou c'est une race dérivée d'une race de la liste (« 00UBE_NordRace »
    /// pour « NordRace ») : les races UBE ne sont pas dans les listes des mods de cheveux (elles y sont ajoutées en jeu),
    /// alors que RaceMenu leur propose bien ces coiffures (Buttercup, UBE Nord, coiffure Jino). Sans importance pour le
    /// jeu ici : la coiffure sera portée en perruque (ARMA), pas en headpart.</summary>
    static bool RaceInList(Context ctx, IFormListGetter list, IRaceGetter race)
    {
        if (list.Items.Any(i => i.FormKey == race.FormKey)) return true;
        var name = race.EditorID ?? "";
        return list.Items.Any(i => ctx.Cache.TryResolve<IRaceGetter>(i.FormKey, out var r) && r.EditorID is { Length: > 4 } e
            && name.Length > e.Length && name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Une pièce qui n'occupe que l'emplacement des cheveux peut être une capuche ou un casque : on ne garde que
    /// celles dont le nom, l'EditorID ou le plugin parle de cheveux ou de perruque (anglais, français, coréen, chinois,
    /// japonais).</summary>
    static readonly string[] WigWords = ["wig", "hair", "perruque", "cheveux", "가발", "헤어", "假发", "假髮", "头发", "頭髮", "发型", "ウィッグ", "髪"];

    /// <summary>Pièces de nos propres packs installés (GF_….esp : copies de coiffures et perruques des followers).</summary>
    static bool OwnPack(FormKey key) => key.ModKey.FileName.String.StartsWith("GF_", StringComparison.OrdinalIgnoreCase);

    static bool LooksLikeWig(ArmorPiece p) =>
        WigWords.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Choix enregistré, retrouvé dans le catalogue du preset (null : aucun, ou coiffure disparue du jeu).</summary>
    public static HairChoice? ChosenHair(Context ctx, PresetRow row, JsonObject opts)
    {
        if (opts[HairOption] is not JsonObject o) return null;
        string? type = (o["type"] as JsonValue)?.GetValue<string>(), id = (o["id"] as JsonValue)?.GetValue<string>();
        return FormKey.TryFactory(id, out var key) ? Hairs(ctx, row).FirstOrDefault(h => h.Type == type && h.Key == key) : null;
    }

    static JsonObject HairJson(HairChoice h) => new() { ["type"] = h.Type, ["id"] = h.Key.ToString(), ["mod"] = h.Mod, ["nom"] = h.Name };

    /// <summary>Fiche : coiffure choisie (null : aucune ; "inconnue" : enregistrée mais plus dans le jeu).</summary>
    static JsonNode? ChosenHairJson(Context ctx, PresetRow row, JsonObject opts) =>
        opts[HairOption] == null ? null : ChosenHair(ctx, row, opts) is { } h ? HairJson(h) : new JsonObject { ["inconnue"] = true };

    /// <summary>Route /api/coiffures/&lt;id&gt; : le preset est-il chauve (aucune coiffure, défauts de race compris),
    /// nombre de coiffures par mod, et résultats du filtre (?mod=, ?q=, 200 au plus).</summary>
    public static JsonObject HairsInfo(Context ctx, PresetRow row, string? mod, string? q)
    {
        var all = Hairs(ctx, row);
        var race = ctx.Find<IRaceGetter>(row.Race);
        bool bald = !HeadParts.Effective(ctx, Jslot.Load(ctx.PresetPath(row.Preset)), race, row.Female).Any(h => h.Type == HeadPart.TypeEnum.Hair);
        mod = mod?.Trim() ?? "";
        q = q?.Trim() ?? "";
        var hits = mod == "" && q == "" ? [] : all.Where(h => (mod == "" || h.Mod.Contains(mod, StringComparison.OrdinalIgnoreCase))
            && (q == "" || h.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || h.Mod.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        return new JsonObject
        {
            ["chauve"] = bald, ["total"] = all.Count, ["perruques"] = all.Count(h => h.Type == "perruque"),
            ["mods"] = new JsonArray(all.GroupBy(h => h.Mod, StringComparer.OrdinalIgnoreCase)
                .Select(g => (JsonNode)new JsonObject { ["mod"] = g.Key, ["n"] = g.Count() }).ToArray()),
            ["trouves"] = hits.Count,
            ["resultats"] = new JsonArray(hits.Take(200).Select(h => (JsonNode)HairJson(h)).ToArray()),
        };
    }

    static void SaveHair(Context ctx, PresetRow row, JsonNode? value)
    {
        if (value == null) { Set(ctx, row.Id, HairOption, null); return; }
        var probe = new JsonObject { [HairOption] = value.DeepClone() };
        if (value is not JsonObject || ChosenHair(ctx, row, probe) is not { } h)
            throw new GfException(L.T("Coiffure inconnue, ou pas faite pour la race et le sexe de ce follower."));
        Set(ctx, row.Id, HairOption, new JsonObject { ["type"] = h.Type, ["id"] = h.Key.ToString() });
    }
}
