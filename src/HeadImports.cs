using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Têtes RaceMenu « compagnons » d'un preset (CharGen\**\*.nif, Export Head / Head Sculpts). Certains presets ne
/// sont justes qu'après « Import Head » dans RaceMenu : la géométrie de ce .nif remplace alors celle calculée
/// depuis le jslot. Le choix est automatique (contenu du .nif + nom) et peut être forcé dans le Follower Builder
/// (work\head_imports.json).
/// </summary>
public static class HeadImports
{
    public record Choice(string? Path, string Mode, string Reason);

    static string IndexPath(Context ctx) => System.IO.Path.Combine(ctx.WorkDir, "chargen_heads.json");
    static string ChoicesPath(Context ctx) => System.IO.Path.Combine(ctx.WorkDir, "head_imports.json");

    /// <summary>Met à jour l'index des têtes de CharGen (offline\index_heads.py, incrémental).</summary>
    public static void RefreshIndex(Context ctx) => Py.Run(ctx, "index_heads.py", ctx.CharGenDir, IndexPath(ctx));

    public static JsonObject LoadChoices(Context ctx)
        => File.Exists(ChoicesPath(ctx)) ? JsonNode.Parse(File.ReadAllText(ChoicesPath(ctx)))!.AsObject() : [];

    public static void SaveChoice(Context ctx, string id, string mode, string? path)
    {
        var all = LoadChoices(ctx);
        if (mode == "auto") all.Remove(id);
        else all[id] = new JsonObject { ["mode"] = mode, ["path"] = path };
        File.WriteAllText(ChoicesPath(ctx), all.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
    }

    /// <summary>Tête à importer pour ce preset : choix manuel, sinon meilleure correspondance de l'index.</summary>
    public static Choice Resolve(Context ctx, PresetRow row, IEnumerable<IHeadPartGetter> effective)
    {
        if (LoadChoices(ctx)[row.Id] is JsonObject c)
        {
            var mode = c["mode"]?.GetValue<string>() ?? "aucun";
            return mode == "manuel" ? new(c["path"]?.GetValue<string>(), mode, L.T("choisie à la main")) : new(null, mode, L.T("import désactivé à la main"));
        }
        var (best, reason) = Suggest(ctx, row, effective).FirstOrDefault();
        return best != null ? new(best, "auto", reason) : new(null, "auto", L.T("aucune tête RaceMenu ne correspond"));
    }

    /// <summary>
    /// Candidats classés : la tête (headpart Face) doit y être, puis recouvrement des noms de formes avec les
    /// headparts du preset, et bonus si un mot du nom du preset figure dans le nom du fichier.
    /// </summary>
    public static List<(string Path, string Reason)> Suggest(Context ctx, PresetRow row, IEnumerable<IHeadPartGetter> effective)
    {
        if (!File.Exists(IndexPath(ctx))) return [];
        var index = JsonNode.Parse(File.ReadAllText(IndexPath(ctx)))!.AsObject();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(IHeadPartGetter h)
        {
            if (h.EditorID != null && h.Model != null) names.Add(h.EditorID);
            foreach (var e in h.ExtraParts) if (ctx.Cache.TryResolve<IHeadPartGetter>(e.FormKey, out var x)) Walk(x);
        }
        var parts = effective.ToList();
        parts.ForEach(Walk);
        var face = parts.FirstOrDefault(p => p.Type == HeadPart.TypeEnum.Face)?.EditorID;
        if (face == null || names.Count == 0) return [];
        // nom du preset, pas le nom en jeu : renommer un follower dans le Builder ne doit pas changer son export
        var words = Words(row.NomPreset + " " + System.IO.Path.GetFileName(row.Preset));

        var presetName = System.IO.Path.GetFileName(row.Preset);
        var res = new List<(string Path, double Score, DateTime Date, bool Named, string Reason)>();
        foreach (var (rel, node) in index)
        {
            var shapes = node!["shapes"]!.AsObject().Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!shapes.Contains(face)) continue;
            int common = shapes.Count(names.Contains);
            double jaccard = common / (double)(shapes.Count + names.Count - common);
            var stem = System.IO.Path.GetFileNameWithoutExtension(rel);
            // Export Head de l'utilisateur sous le nom exact du preset : RaceMenu ne remplace pas l'export de l'auteur,
            // il en crée un nouveau (CharGen\Exported\) ; c'est lui qu'il faut prendre (psynwen-v2, Lazarus - Aria…).
            bool exact = stem.Equals(presetName, StringComparison.OrdinalIgnoreCase) || stem.Equals(row.NomPreset, StringComparison.OrdinalIgnoreCase);
            bool named = exact || Words(stem).Overlaps(words);
            // sans indice de nom, il faut un recouvrement quasi complet (beaucoup de presets partagent tête + cheveux)
            if (jaccard < (exact ? 0.3 : named ? 0.5 : 0.85)) continue;
            var file = System.IO.Path.Combine(ctx.CharGenDir, rel);
            var date = File.Exists(file) ? File.GetLastWriteTime(file) : DateTime.MinValue;
            res.Add((rel, jaccard + (named ? 1 : 0) + (exact ? 2 : 0), date, named,
                L.T("{0}/{1} headparts en commun", common, names.Count) + (exact ? L.T(", nom exact") : named ? L.T(", nom proche") : "") + $", {date:d}"));
        }
        // à score égal (à 0,05 près) et nom correspondant, le plus récent : un nouvel export l'emporte sur l'ancien.
        // Sans indice de nom, pas de critère de date (un export récent d'un autre preset aux mêmes headparts gagnerait).
        return res.OrderByDescending(r => Math.Round(r.Score / 0.05)).ThenByDescending(r => r.Named ? r.Date : DateTime.MinValue)
            .Select(r => (r.Path, r.Reason)).ToList();
    }

    static HashSet<string> Words(string s)
    {
        var stop = new HashSet<string> { "head", "preset", "export", "sculpt", "ube", "hph", "female", "male", "nif", "jslot", "the", "and", "follower", "replacer", "v2", "v3" };
        s = System.Text.RegularExpressions.Regex.Replace(s, "([a-z])([A-Z])", "$1 $2");   // AikaHead -> Aika Head
        return System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(w => w.Length >= 3 && !stop.Contains(w)).ToHashSet();
    }
}
