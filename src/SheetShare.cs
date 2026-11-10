using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Fiche de follower partagée (fichier .gffiche.json) : nom en jeu, style et classe, voix, skin et options (tenue, armes,
/// sorts, atouts, réglages avancés, mariage, lieu, pièces de mods, cheveux SMP, preset de corps) d'un preset, pour qu'un
/// autre joueur, ou un autre preset, reprenne les mêmes choix. Identifiants portables : EditorID, « EditorID@plugin »,
/// FormKey (plugin compris) ; le skin (identifiant propre à l'installation) est retrouvé par son nom.
/// Import : le fichier vient d'un autre joueur, donc chaque choix repasse par la validation habituelle (catalogues de
/// l'ordre de chargement du joueur, sexe de la voix…) ; ce qui manque chez lui (mod absent) revient au défaut et est
/// signalé, le reste est appliqué. La fiche remplace les choix du preset cible (une option absente revient au défaut).
/// </summary>
public static class SheetShare
{
    public const string Kind = "GenerateFollower.fiche";
    const int FormatVersion = 1;
    /// <summary>Options en liste : appliquées élément par élément quand la liste entière est refusée.</summary>
    static readonly string[] ListKeys = ["sorts", "armes", "atouts", "tenueMods"];

    static string Label(string key) => key switch
    {
        "tenue" => L.T("Tenue"), "sorts" => L.T("Sorts"), "atouts" => L.T("Atouts"), "armes" => L.T("Armes de départ"),
        "lieu" => L.T("Lieu de départ"), "mariage" => L.T("Mariage"), "tenueMods" => L.T("Pièces d'armure de mods"),
        SmpHair.Option => L.T("Cheveux SMP"), BodyPresets.Option => L.T("Preset de corps OBody"),
        _ => L.T("Réglages avancés") + $" ({key})",
    };

    /// <summary>Erreurs d'une valeur refusée : validation (GfException) ou valeur mal formée dans le fichier.</summary>
    static bool Refused(Exception e) => e is GfException or InvalidOperationException or FormatException or JsonException;

    /// <summary>Raison lisible d'un refus (le message technique d'une valeur mal formée ne dit rien au joueur).</summary>
    static string Why(Exception e) => e is GfException ? e.Message : L.T("valeur invalide dans la fiche");

    public static JsonObject Export(Context ctx, PresetRow row, Skins skins)
    {
        var combat = Combat.LoadChoices(ctx).GetValueOrDefault(row.Id);
        var skin = skins.Get(Skins.LoadChoices(ctx).GetValueOrDefault(row.Id));
        return new JsonObject
        {
            ["type"] = Kind, ["format"] = FormatVersion, ["application"] = SupportReport.Version,
            ["nom"] = row.Nom, ["preset"] = Path.GetFileName(row.Preset), ["sexe"] = row.Sexe, ["race"] = row.Race,
            ["combat"] = combat == null ? null : new JsonObject { ["style"] = combat.Style, ["classe"] = combat.Classe },
            ["voix"] = Voices.LoadChoices(ctx).GetValueOrDefault(row.Id),
            ["skin"] = skin == null ? null : new JsonObject { ["nom"] = skin.Name, ["source"] = skin.Source },
            ["options"] = Options.For(ctx, row.Id),
        };
    }

    /// <summary>Applique une fiche au preset ; rend (appliqués, ignorés avec la raison, nom en jeu). Le nom est rendu à
    /// part : le Builder le range dans presets.csv.</summary>
    public static (List<string> Applied, List<string> Skipped, string? Name) Import(Context ctx, PresetRow row, JsonObject sheet, Skins skins)
    {
        if (Str(sheet["type"]) != Kind) throw new GfException(L.T("Ce fichier n'est pas une fiche de follower GenerateFollower."));
        if (sheet["format"] is JsonValue f && f.TryGetValue<int>(out var format) && format > FormatVersion)
            throw new GfException(L.T("Fiche faite par une version plus récente de GenerateFollower ({0}) : mets l'application à jour.", Str(sheet["application"]) ?? "?"));
        var applied = new List<string>();
        var skipped = new List<string>();
        // un choix refusé revient au défaut (la fiche remplace les choix du preset)
        void Try(string label, Action apply, Action reset)
        {
            try { apply(); applied.Add(label); }
            catch (Exception e) when (Refused(e)) { reset(); skipped.Add($"{label} : {Why(e)}"); }
        }
        if (Str(sheet["sexe"]) is { } sex && sex != row.Sexe)
            skipped.Add(L.T("Fiche d'un follower de l'autre sexe : la voix et le preset de corps ne peuvent pas convenir."));

        var combat = sheet["combat"] as JsonObject;
        Try(L.T("Style et classe"), () => Combat.SaveChoice(ctx, row.Id, Str(combat?["style"]), Str(combat?["classe"])),
            () => Combat.SaveChoice(ctx, row.Id, null, null));
        Try(L.T("Voix"), () => Voices.SaveChoice(ctx, row, Str(sheet["voix"])), () => Voices.SaveChoice(ctx, row, null));
        if (sheet["skin"] is JsonObject s)
        {
            var name = Str(s["nom"]);
            var found = skins.For(row).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Skins.SaveChoice(ctx, row.Id, found?.Id);
            if (found != null) applied.Add(L.T("Peau : {0}", found.Name));
            else skipped.Add(L.T("Peau « {0} » : pas installée chez toi, ou pas faite pour le corps de ce follower.", name ?? "?"));
        }
        else Skins.SaveChoice(ctx, row.Id, null);

        var opts = sheet["options"] as JsonObject ?? [];
        foreach (var key in Options.OptionKeys)
        {
            void Reset() => Options.Save(ctx, row, new JsonObject { [key] = null });
            var value = opts[key]?.DeepClone();
            if (value == null) { Reset(); continue; }
            try { Options.Save(ctx, row, new JsonObject { [key] = value }); applied.Add(Label(key)); }
            catch (Exception e) when (Refused(e) && ListKeys.Contains(key) && value is JsonArray list)
            {
                // liste refusée en bloc (un élément d'un mod absent…) : on garde les éléments acceptés un par un
                var kept = new JsonArray();
                foreach (var item in list)
                    try { Options.Save(ctx, row, new JsonObject { [key] = new JsonArray(item?.DeepClone()) }); kept.Add(item?.DeepClone()); }
                    catch (Exception ie) when (Refused(ie)) { skipped.Add($"{Label(key)} : {Why(ie)}"); }
                try { Options.Save(ctx, row, new JsonObject { [key] = kept.Count > 0 ? kept : null }); if (kept.Count > 0) applied.Add($"{Label(key)} ({kept.Count}/{list.Count})"); }
                catch (Exception ke) when (Refused(ke)) { Reset(); skipped.Add($"{Label(key)} : {Why(ke)}"); }
            }
            catch (Exception e) when (Refused(e)) { Reset(); skipped.Add($"{Label(key)} : {Why(e)}"); }
        }
        var nom = Str(sheet["nom"])?.Trim();
        return (applied, skipped, string.IsNullOrEmpty(nom) || nom.Contains(';') ? null : nom);
    }

    /// <summary>Chaîne d'un nœud JSON, null si absent ou d'un autre type (fichier venu d'ailleurs).</summary>
    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
