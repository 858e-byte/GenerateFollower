using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Tête d'un autre preset, sans « Export Head » (plan : work\diag\gabarit\PLAN.md, étape 1). Option « teteDonneur » de
/// options_choice.json = id d'un preset au verdict vert, du même sexe, qui a la même tête (HeadWrite.Candidates) : le
/// follower prend son export (accepté par le jeu) et ses pièces de tête ; de la cible viennent la forme du visage
/// (HeadWrite.Run au build), la teinte calculée depuis son .jslot et ses couleurs ; du donneur, le poids et les os du cou.
/// Absente : rien ne change.
/// </summary>
public static partial class Options
{
    public const string DonorHeadOption = "teteDonneur";

    /// <summary>Id du donneur choisi pour ce preset, ou null. Fonction expérimentale : sans le réglage « Fonctions
    /// expérimentales » (settings.json, experimental = oui), le choix est ignoré partout (build, aperçu, verdict) et
    /// chaque follower utilise son propre export.</summary>
    public static string? ChosenDonor(Context ctx, string id) =>
        ctx.Settings.UseExperimental && For(ctx, id)[DonorHeadOption] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    static void RequireExperimental(Context ctx)
    {
        if (!ctx.Settings.UseExperimental)
            throw new GfException(L.T("« Tête d'un autre preset » est une fonction expérimentale : active « Fonctions expérimentales » dans les Réglages."));
    }

    /// <summary>
    /// « Plusieurs followers » : donne son meilleur donneur (HeadWrite.Candidates) à chaque preset dont le dernier verdict
    /// est dans levels (« export » = sans export, « rouge », « orange ») et qui n'a pas déjà un donneur. Les autres ne
    /// changent pas. La tête de chacun est refaite au prochain build ou à l'ouverture de sa fiche (« Générer »).
    /// </summary>
    public static JsonObject BatchDonors(Context ctx, List<PresetRow> targets, HashSet<string> levels)
    {
        RequireExperimental(ctx);
        if (targets.Count == 0) throw new GfException(L.T("Aucun preset à modifier."));
        if (levels.Count == 0) throw new GfException(L.T("Coche au moins un verdict."));
        var verdicts = File.Exists(Path.Combine(ctx.WorkDir, "verdicts.json")) ? JsonNode.Parse(File.ReadAllText(Path.Combine(ctx.WorkDir, "verdicts.json")))!.AsObject() : [];
        var rows = PresetTable.Read(ctx.CsvPath);
        int given = 0, ready = 0, already = 0, unknown = 0;
        var none = new JsonArray();
        var log = new JsonArray();
        foreach (var t in targets)
        {
            if (ChosenDonor(ctx, t.Id) != null) { already++; continue; }
            var lvl = verdicts[t.Id]?["niveau"]?.GetValue<string>();
            if (lvl == null) { unknown++; continue; }
            if (!levels.Contains(lvl)) { ready++; continue; }
            List<HeadWrite.Candidate> ranked;
            try { ranked = HeadWrite.Candidates(ctx, t, rows).Ranked; }
            catch (GfException) { ranked = []; }
            // un donneur dont on ne sait pas réécrire le visage ne sert à rien (le follower aurait le visage du donneur)
            if (ranked.FirstOrDefault(c => c.FaceOk) is not { } best) { none.Add(t.Nom); continue; }
            Set(ctx, t.Id, DonorHeadOption, JsonValue.Create(best.Row.Id));
            log.Add($"{t.Nom} ← {best.Row.Nom}");
            given++;
        }
        return new JsonObject { ["donnes"] = given, ["ignores"] = ready, ["dejaDonneur"] = already, ["nonVerifies"] = unknown, ["sansDonneur"] = none, ["detail"] = log };
    }

    static void SaveDonorHead(Context ctx, PresetRow row, JsonNode? value)
    {
        var id = value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        if (string.IsNullOrEmpty(id)) { Set(ctx, row.Id, DonorHeadOption, null); return; }
        RequireExperimental(ctx);
        var cand = HeadWrite.Candidates(ctx, row).Ranked.FirstOrDefault(c => c.Row.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new GfException(L.T("Ce preset ne peut pas donner sa tête à ce follower (il faut un export accepté, même sexe, même tête) : {0}", id));
        Set(ctx, row.Id, DonorHeadOption, JsonValue.Create(cand.Row.Id));
    }
}
