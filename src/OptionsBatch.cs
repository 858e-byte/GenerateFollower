using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Modification de plusieurs followers à la fois (Builder : presets affichés dans la liste, filtres compris) : copie des
/// choix d'un follower source (parties cochées) ou application d'un archétype. La voix n'est copiée que vers un follower
/// du même sexe ; le follower source n'est jamais modifié.
/// </summary>
public static partial class Options
{
    /// <summary>Parties copiables → clés concernées (options_choice.json) ; « combat » et « voix » ont leur propre fichier.</summary>
    static readonly Dictionary<string, string[]> BatchParts = new()
    {
        ["combat"] = [], ["voix"] = [], ["tenue"] = ["tenue"], ["armes"] = ["armes"], ["sorts"] = ["sorts"], ["atouts"] = ["atouts"],
        ["avance"] = ["niveau", "statut", "agressivite", "courage", "moralite", "assistance", "taille"], ["mariage"] = ["mariage"],
    };

    public static JsonObject Batch(Context ctx, List<PresetRow> targets, PresetRow? source, string? archetype, List<string> parts)
    {
        if (targets.Count == 0) throw new GfException(L.T("Aucun preset à modifier."));
        if (archetype == null && source == null) throw new GfException(L.T("Indique un follower source ou un archétype."));
        if (parts.FirstOrDefault(p => !BatchParts.ContainsKey(p)) is { } badPart) throw new GfException(L.T("Partie inconnue : {0}", badPart));
        if (archetype == null && parts.Count == 0) throw new GfException(L.T("Coche au moins une partie à copier."));
        int done = 0, voiceSkipped = 0;
        var src = source != null ? For(ctx, source.Id) : null;
        var srcCombat = source != null ? Combat.LoadChoices(ctx).GetValueOrDefault(source.Id) : null;
        var srcVoice = source != null ? Voices.LoadChoices(ctx).GetValueOrDefault(source.Id) : null;
        foreach (var t in targets.Where(t => t.Id != source?.Id))
        {
            if (archetype != null) { ApplyArchetype(ctx, t, archetype); done++; continue; }
            if (parts.Contains("combat")) Combat.SaveChoice(ctx, t.Id, srcCombat?.Style, srcCombat?.Classe);
            if (parts.Contains("voix"))
            {
                if (t.Female == source!.Female) Voices.SaveChoice(ctx, t, srcVoice);
                else voiceSkipped++;
            }
            foreach (var key in parts.SelectMany(p => BatchParts[p]))
                Set(ctx, t.Id, key, src![key]?.DeepClone());
            done++;
        }
        return new JsonObject { ["modifies"] = done, ["voixIgnorees"] = voiceSkipped };
    }
}
