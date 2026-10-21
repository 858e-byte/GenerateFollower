using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Sorts d'un follower : ceux qu'enseignent les livres de sorts de Skyrim.esm et des DLC (exactement les sorts qu'un joueur
/// peut apprendre), lus dans les fichiers d'origine (des mods renomment les EditorID ou changent les sorts). École et niveau
/// (novice 0 … maître 100) = effet principal du sort. Choix : options_choice.json, "sorts": [EditorID…]. Au build : liste
/// des sorts du PNJ (SPLO). Un PNJ ne lance que les sorts de ses écoles fortes : classe et style de combat comptent.
/// </summary>
public static partial class Options
{
    public sealed record SpellChoice(string EditorId, FormKey Key, string School, int Level, string Name);

    static List<SpellChoice>? spells;

    /// <summary>Sorts utilitaires que l'IA d'un PNJ ne lance pas en combat (exploration, détection, transmutation).</summary>
    static readonly HashSet<string> UtilitySpells = new(StringComparer.OrdinalIgnoreCase)
        { "Candlelight", "Magelight", "Telekinesis", "TransmuteOreMineral", "Waterbreathing", "Clairvoyance", "DetectLife", "DetectDead" };

    public static List<SpellChoice> Spells(Context ctx)
    {
        if (spells != null) return spells;
        var masters = MasterMods(ctx);
        var spellsByKey = new Dictionary<FormKey, ISpellGetter>();
        var effects = new Dictionary<FormKey, IMagicEffectGetter>();
        foreach (var m in masters)
        {
            foreach (var s in m.Spells) spellsByKey[s.FormKey] = s;
            foreach (var e in m.MagicEffects) effects[e.FormKey] = e;
        }
        var list = new List<SpellChoice>();
        foreach (var book in masters.SelectMany(m => m.Books))
        {
            if (book.Teaches is not IBookSpellGetter teach || !spellsByKey.TryGetValue(teach.Spell.FormKey, out var sp)) continue;
            // effet principal : le premier dont l'école est une école de magie
            var main = sp.Effects.Select(e => effects.GetValueOrDefault(e.BaseEffect.FormKey))
                .FirstOrDefault(me => me != null && me.MagicSkill is ActorValue.Alteration or ActorValue.Conjuration or ActorValue.Destruction
                    or ActorValue.Illusion or ActorValue.Restoration);
            if (main == null || sp.EditorID == null || UtilitySpells.Contains(sp.EditorID)) continue;
            list.Add(new(sp.EditorID, sp.FormKey, main.MagicSkill.ToString(), (int)main.MinimumSkillLevel, sp.Name?.String ?? sp.EditorID));
        }
        return spells = list.DistinctBy(s => s.Key).OrderBy(s => s.School).ThenBy(s => s.Level).ThenBy(s => s.Name, StringComparer.CurrentCulture).ToList();
    }

    static string SchoolName(string school) => school switch
    {
        "Alteration" => L.T("Altération"), "Conjuration" => L.T("Conjuration"), "Destruction" => L.T("Destruction"),
        "Illusion" => L.T("Illusion"), "Restoration" => L.T("Guérison"), _ => school,
    };

    static string LevelName(int level) => level switch
    {
        < 25 => L.T("novice"), < 50 => L.T("apprenti"), < 75 => L.T("adepte"), < 100 => L.T("expert"), _ => L.T("maître"),
    };

    static JsonArray SpellsJson(Context ctx) => new(Spells(ctx).Select(s => (JsonNode)new JsonObject
    {
        ["id"] = s.EditorId, ["ecole"] = SchoolName(s.School), ["niveau"] = s.Level, ["niveauNom"] = LevelName(s.Level),
        ["nom"] = s.Name, ["source"] = s.Key.ModKey.FileName.String,
    }).ToArray());

    static void SaveSpells(Context ctx, PresetRow row, JsonNode? value)
    {
        var ids = value is JsonArray a ? a.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList() : [];
        var known = Spells(ctx).Select(s => s.EditorId).ToHashSet();
        if (ids.FirstOrDefault(i => !known.Contains(i)) is { } bad) throw new GfException(L.T("Sort inconnu : {0}", bad));
        Set(ctx, row.Id, "sorts", ids.Count == 0 ? null : new JsonArray(ids.Select(i => (JsonNode)i).ToArray()));
    }

    static void ApplySpells(Context ctx, Npc npc, PresetRow row, JsonObject opts, StringBuilder report)
    {
        if (opts["sorts"] is not JsonArray ids) return;
        var byId = Spells(ctx).ToDictionary(s => s.EditorId);
        var added = new List<string>();
        foreach (var id in ids.Select(x => x?.GetValue<string>() ?? ""))
        {
            if (byId.TryGetValue(id, out var s) && ctx.Cache.TryResolve<ISpellGetter>(s.Key, out var sp))
            {
                if (npc.ActorEffect?.Any(x => x.FormKey == s.Key) == true) continue;
                (npc.ActorEffect ??= []).Add(sp.ToLink());
                added.Add(id);
            }
            else report.AppendLine($"ATTENTION {row.Id}  sort {id} introuvable : ignoré");
        }
        if (added.Count > 0) report.AppendLine($"OPTION  {row.Id}  sorts {string.Join(", ", added)}");
    }
}
