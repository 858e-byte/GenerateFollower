using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Mariage : un follower épousable est dans la faction PotentialMarriageFaction (Skyrim.esm 019809, rang 0), comme les PNJ
/// du jeu. Les répliques de mariage n'existent que pour certaines voix (Voices.MarriageOf) : sans elles, la faction ne suffit
/// pas, d'où l'avertissement dans la fiche et au rapport. En jeu : porter l'amulette de Mara (et, pour beaucoup de PNJ du
/// jeu, leur avoir rendu service). Choix : options_choice.json, "mariage": true.
/// </summary>
public static partial class Options
{
    static readonly FormKey PotentialMarriageFaction = new(ModKey.FromFileName("Skyrim.esm"), 0x019809);

    static void SaveMarriage(Context ctx, PresetRow row, JsonNode? value)
    {
        bool on = value is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        Set(ctx, row.Id, "mariage", on ? JsonValue.Create(true) : null);
    }

    /// <summary>Répliques de mariage de la voix finale du follower (voix choisie, sinon celle de presets.csv).</summary>
    static JsonObject? MarriageVoiceJson(Context ctx, PresetRow row)
    {
        if (Voices.FinalVoice(ctx, row) is not { } voice) return null;
        var d = Voices.MarriageOf(ctx, voice);
        return new JsonObject { ["niveau"] = d.Level, ["plugins"] = d.Plugins, ["retireePar"] = d.RemovedBy };
    }

    static void ApplyMarriage(Context ctx, Npc npc, PresetRow row, JsonObject opts, StringBuilder report)
    {
        if (opts["mariage"]?.GetValue<bool>() != true) return;
        if (!ctx.Cache.TryResolve<IFactionGetter>(PotentialMarriageFaction, out var fac))
        { report.AppendLine($"ATTENTION {row.Id}  faction de mariage introuvable : option ignorée"); return; }
        if (!npc.Factions.Any(f => f.Faction.FormKey == PotentialMarriageFaction))
            npc.Factions.Add(new RankPlacement { Faction = fac.ToLink(), Rank = 0 });
        report.AppendLine($"OPTION  {row.Id}  épousable (PotentialMarriageFaction)");
        var d = Voices.MarriageOf(ctx, npc.Voice.FormKey);
        if (d.Level != "jeu")
            report.AppendLine($"ATTENTION {row.Id}  voix sans répliques de mariage dans le jeu de base" +
                (d.Level == "mod" ? $" (ajoutées par {d.Plugins})" : " : mariage impossible par le dialogue"));
    }
}
