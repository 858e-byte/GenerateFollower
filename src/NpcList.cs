using System.Diagnostics;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// PNJ remplaçables (remplacement de PNJ, étape 1, lecture seule) : PNJ uniques nommés qui ont leur propre visage
/// (pas de modèle qui fournit les traits, pas d'enfant, race à tête FaceGen). Pour chacun : sexe, race, plugin
/// d'origine, et ce qui change déjà son apparence : règles SkyPatcher (copyVisualStyle, skin, race), autres plugins
/// (race, parties de tête, morphs différents de la fiche d'origine), tête fournie par un mod
/// (FaceGeom\&lt;plugin&gt;\&lt;FormID&gt;.nif hors archives du jeu).
/// </summary>
public static class NpcList
{
    static JsonObject? cache;
    static readonly string[] AppearanceKeys = ["copyvisualstyle", "skin", "race"];
    static readonly HashSet<string> Fixes = new(StringComparer.OrdinalIgnoreCase) { "unofficial skyrim special edition patch.esp" };

    /// <summary>
    /// Références de PNJ placées dans le monde, version gagnante de chaque référence (un plugin chargé après peut la
    /// désactiver ou la supprimer) ; nos plugins GF_ ignorés. PNJ de base → (références, références actives au départ).
    /// </summary>
    static Dictionary<FormKey, (int Count, int Enabled)> Placements(Context ctx)
    {
        var refs = new Dictionary<FormKey, (FormKey Base, bool Active)>();
        foreach (var l in ctx.Env.LoadOrder.PriorityOrder.Reverse())
            if (l.Mod is { } m && !Ours(m.ModKey))
                foreach (var r in m.EnumerateMajorRecords<IPlacedNpcGetter>())
                    refs[r.FormKey] = (r.Base.FormKey, !r.MajorFlags.HasFlag(PlacedNpc.MajorFlag.InitiallyDisabled) && !r.IsDeleted);
        var result = new Dictionary<FormKey, (int Count, int Enabled)>();
        foreach (var (b, active) in refs.Values)
        {
            result.TryGetValue(b, out var c);
            result[b] = (c.Count + 1, c.Enabled + (active ? 1 : 0));
        }
        return result;
    }

    /// <summary>
    /// PNJ qui fournit l'apparence (« Traits » : race, sexe, voix, visage, taille) : le PNJ lui-même, ou son modèle s'il
    /// hérite de ses traits (Immersive Wenches : IW_FACEPRESET_…), en remontant les modèles ; null si un modèle est une
    /// liste de PNJ (apparence tirée au hasard) ou introuvable.
    /// </summary>
    public static INpcGetter? TraitsProvider(Context ctx, INpcGetter n)
    {
        for (int depth = 0; depth < 10; depth++)
        {
            if (n.Template.IsNull || !n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)) return n;
            if (!ctx.Cache.TryResolve<INpcGetter>(n.Template.FormKey, out var t)) return null;
            n = t;
        }
        return null;
    }

    /// <summary>Plugins construits par GenerateFollower (followers, remplacements).</summary>
    static bool Ours(ModKey k) => k.FileName.String.StartsWith(Standalone.Prefix, StringComparison.OrdinalIgnoreCase);

    public static JsonObject Json(Context ctx, bool refresh = false)
    {
        if (cache != null && !refresh) return cache;
        var sw = Stopwatch.StartNew();
        var winners = ctx.Env.LoadOrder.PriorityOrder.Npc().WinningOverrides().ToList();
        var byEdid = winners.Where(n => n.EditorID is { Length: > 0 })
            .GroupBy(n => n.EditorID!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().FormKey, StringComparer.OrdinalIgnoreCase);
        var sky = SkyPatcherRules(ctx, byEdid, out int iniCount, out var templates);
        var templatePlugins = templates.Select(t => t.ModKey).Where(m => !m.FileName.String.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)).ToHashSet();

        var world = Placements(ctx);
        var list = new JsonArray();
        int skipped = 0;
        Bsa? bsa = null;
        foreach (var w in winners)
        {
            // PNJ « modèles » des remplaçants SkyPatcher (source d'un copyVisualStyle) : jamais placés dans le monde
            // (et tout PNJ d'un plugin qui en fournit : plugins de modèles, ex. Girls of SkyrimNpc.esp)
            if (templates.Contains(w.FormKey) || templatePlugins.Contains(w.FormKey.ModKey)) { skipped++; continue; }
            // version en jeu sans nos propres plugins (GF_…), pour ne pas compter un remplacement déjà construit
            var versions = ctx.Cache.ResolveAllContexts<INpc, INpcGetter>(w.FormKey).ToList();
            var n = versions.FirstOrDefault(v => !Ours(v.ModKey))?.Record ?? w;
            var cfg = n.Configuration;
            if (!cfg.Flags.HasFlag(NpcConfiguration.Flag.Unique) || n.Name?.String is not { Length: > 0 } name) { skipped++; continue; }
            // apparence héritée d'un modèle (Traits) : race, sexe, visage du modèle ; le remplacement coupera cet
            // héritage pour ce PNJ seulement. Modèle = liste de PNJ (visage au hasard) : écarté
            var p = TraitsProvider(ctx, n);
            if (p == null) { skipped++; continue; }
            if (!ctx.Cache.TryResolve<IRaceGetter>(p.Race.FormKey, out var race)
                || race.Flags.HasFlag(Race.Flag.Child) || !race.Flags.HasFlag(Race.Flag.FaceGenHead)) { skipped++; continue; }

            // plugins qui changent l'apparence (du PNJ ou de son modèle) par rapport à la fiche d'origine (la dernière
            // des versions) ; les correctifs non officiels à part (corrections de teintes surtout)
            var pVersions = p == n ? versions : ctx.Cache.ResolveAllContexts<INpc, INpcGetter>(p.FormKey).ToList();
            var origin = pVersions[^1].Record;
            var changers = new JsonArray();
            var fixes = new JsonArray();
            foreach (var v in pVersions.Take(pVersions.Count - 1).Where(v => !Ours(v.ModKey)))
                if (Appearance(v.Record) != Appearance(origin))
                    (Fixes.Contains(v.ModKey.FileName.String) ? fixes : changers).Add(v.ModKey.FileName.String);

            var id = p.FormKey.ID.ToString("X8");
            var faceRel = $@"meshes\actors\character\FaceGenData\FaceGeom\{p.FormKey.ModKey.FileName}\{id}.nif";
            // tête fournie par un mod (fichier libre, ou archive d'un autre mod que celui du PNJ, ex. Botox SE -
            // Immersive Wenches.bsa) ; pas celle d'un de nos remplacements déjà construits
            string? face = null;
            if (!versions.Any(v => Ours(v.ModKey)))
            {
                if (ctx.Files.Find(faceRel) != null) face = ctx.Files.Owner(faceRel) ?? "Data";
                else if ((bsa ??= Bsa.Open(ctx)).ArchiveOf(faceRel) is { } arc
                    && !arc.StartsWith("Skyrim - ", StringComparison.OrdinalIgnoreCase)
                    && !arc.StartsWith(Path.GetFileNameWithoutExtension(p.FormKey.ModKey.FileName.String), StringComparison.OrdinalIgnoreCase))
                    face = arc;
            }
            var o = new JsonObject
            {
                ["id"] = n.FormKey.ToString(),
                ["edid"] = n.EditorID,
                ["nom"] = name,
                ["plugin"] = n.FormKey.ModKey.FileName.String,
                ["sexe"] = p.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? "F" : "M",
                ["race"] = race.EditorID,
                ["raceNom"] = race.Name?.String,
                ["essentiel"] = cfg.Flags.HasFlag(NpcConfiguration.Flag.Essential),
                // tête FaceGen que le jeu affiche (celle du modèle si l'apparence est héritée) : aperçu côte à côte
                ["tete"] = faceRel,
            };
            if (sky.TryGetValue(n.FormKey, out var rules)) o["skypatcher"] = new JsonArray(rules.Select(r => (JsonNode)r).ToArray());
            if (changers.Count > 0) o["plugins"] = changers;
            if (fixes.Count > 0) o["correctifs"] = fixes;
            if (face != null) o["visage"] = face;
            if (p != n) o["modele"] = p.EditorID ?? p.FormKey.ToString();
            // présence dans le monde : aucune référence placée (contenu coupé, PNJ créé par une quête ou un script),
            // ou seulement des références désactivées au départ (apparaissent à un moment du jeu)
            if (!world.TryGetValue(n.FormKey, out var placed)) o["monde"] = "absent";
            else if (placed.Enabled == 0) o["monde"] = "desactive";
            list.Add(o);
        }
        // un nom porté par 3 PNJ ou plus (« Prisoner », « Bandit Chief »…) ne désigne pas un personnage
        var common = list.GroupBy(x => x!["nom"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 3).SelectMany(g => g).ToList();
        foreach (var x in common) list.Remove(x);
        skipped += common.Count;
        int Count(string key) => list.Count(x => x![key] != null);
        cache = new JsonObject
        {
            ["total"] = list.Count,
            ["ecartes"] = skipped,
            ["skypatcher"] = Count("skypatcher"),
            ["plugins"] = Count("plugins"),
            ["visage"] = Count("visage"),
            ["correctifs"] = Count("correctifs"),
            ["absents"] = list.Count(x => x!["monde"]?.GetValue<string>() == "absent"),
            ["desactives"] = list.Count(x => x!["monde"]?.GetValue<string>() == "desactive"),
            ["libres"] = list.Count(x => x!["skypatcher"] == null && x["plugins"] == null && x["visage"] == null),
            ["fichiersSkyPatcher"] = iniCount,
            ["secondes"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["pnj"] = list,
        };
        return cache;
    }

    /// <summary>PNJ remplacé choisi par preset : work\replace_choice.json { « id du preset » : « FormKey du PNJ » }.</summary>
    static string ChoicePath(Context ctx) => Path.Combine(ctx.WorkDir, "replace_choice.json");

    public static JsonObject Choices(Context ctx) =>
        File.Exists(ChoicePath(ctx)) && JsonNode.Parse(File.ReadAllText(ChoicePath(ctx))) is JsonObject o ? o : new JsonObject();

    /// <summary>GET : choix du preset et PNJ déjà pris par d'autres presets (un PNJ = un seul remplaçant).</summary>
    public static JsonObject ChoiceInfo(Context ctx, string presetId)
    {
        var all = Choices(ctx);
        var taken = new JsonObject();
        foreach (var (k, v) in all)
            if (!k.Equals(presetId, StringComparison.OrdinalIgnoreCase) && v?.GetValue<string>() is { } npc) taken[npc] = k;
        return new JsonObject { ["choix"] = all[presetId]?.GetValue<string>(), ["pris"] = taken, ["total"] = all.Count };
    }

    /// <summary>POST {"pnj": FormKey | null} ; refuse un PNJ déjà pris par un autre preset.</summary>
    public static void SaveChoice(Context ctx, string presetId, string? npc)
    {
        var all = Choices(ctx);
        if (npc != null && all.Any(kv => !kv.Key.Equals(presetId, StringComparison.OrdinalIgnoreCase) && kv.Value?.GetValue<string>() == npc))
            throw new GfException(L.T("Ce PNJ est déjà remplacé par un autre preset."));
        if (npc == null) all.Remove(presetId); else all[presetId] = npc;
        File.WriteAllText(ChoicePath(ctx), all.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Remplacement en lot : { preset : FormKey | null } appliqués ensemble ; refusé en entier si, à la fin, un
    /// PNJ est remplacé par deux presets.</summary>
    public static int SaveChoices(Context ctx, JsonObject changes)
    {
        var all = Choices(ctx);
        foreach (var (preset, v) in changes)
        {
            var key = all.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(preset, StringComparison.OrdinalIgnoreCase)) ?? preset;
            if (v?.GetValue<string>() is { Length: > 0 } npc) all[key] = npc; else all.Remove(key);
        }
        var twice = all.GroupBy(kv => kv.Value?.GetValue<string>()).FirstOrDefault(g => g.Key != null && g.Count() > 1);
        if (twice != null)
            throw new GfException(L.T("Le PNJ {0} serait remplacé par plusieurs presets ({1}) : rien n'a été enregistré.",
                twice.Key!, string.Join(", ", twice.Select(kv => kv.Key))));
        File.WriteAllText(ChoicePath(ctx), all.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return all.Count;
    }

    /// <summary>Empreinte de l'apparence d'une fiche : race, parties de tête, peau, couleurs, teintes.</summary>
    static string Appearance(INpcGetter n) =>
        $"{n.Race.FormKey}|{string.Join(",", n.HeadParts.Select(h => h.FormKey))}|{n.WornArmor.FormKeyNullable}|" +
        $"{n.TextureLighting}|{n.HeadTexture.FormKeyNullable}|{n.HairColor.FormKeyNullable}|" +
        string.Join(",", n.TintLayers.Select(t => $"{t.Index}:{t.Color}:{t.InterpolationValue}")) + "|" +
        (n.FaceMorph is { } m ? string.Join(",", m.NoseLongVsShort, m.NoseUpVsDown, m.JawUpVsDown, m.JawNarrowVsWide, m.CheeksUpVsDown,
            m.EyesUpVsDown, m.EyesInVsOut, m.BrowsUpVsDown, m.LipsUpVsDown, m.ChinUpVsDown) : "");

    /// <summary>
    /// Règles SkyPatcher qui changent l'apparence d'un PNJ précis (filterByNpcs + copyVisualStyle / skin / race) :
    /// PNJ → noms des fichiers .ini (souvent le plugin du remplaçant : « YsoldaDF.esp »). Les règles par faction ou
    /// par morceau d'EditorID (PNJ génériques) ne sont pas suivies.
    /// </summary>
    static Dictionary<FormKey, List<string>> SkyPatcherRules(Context ctx, Dictionary<string, FormKey> byEdid, out int iniCount,
        out HashSet<FormKey> templates)
    {
        var result = new Dictionary<FormKey, List<string>>();
        templates = [];
        var files = ctx.Files.Enumerate(@"SKSE\Plugins\SkyPatcher\npc", "*.ini", true);
        iniCount = files.Count;
        foreach (var (rel, path) in files)
        {
            var parts = rel.Replace('/', '\\').Split('\\');
            int i = Array.FindIndex(parts, p => p.Equals("npc", StringComparison.OrdinalIgnoreCase));
            var source = i >= 0 && i + 2 < parts.Length ? parts[i + 1] : Path.GetFileNameWithoutExtension(path);
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                var segs = line.Split(':');
                var filter = segs.FirstOrDefault(s => s.TrimStart().StartsWith("filterByNpcs=", StringComparison.OrdinalIgnoreCase));
                if (filter == null) continue;
                if (!segs.Any(s => AppearanceKeys.Any(k => s.TrimStart().StartsWith(k + "=", StringComparison.OrdinalIgnoreCase)))) continue;
                foreach (var s in segs.Where(s => s.TrimStart().StartsWith("copyVisualStyle=", StringComparison.OrdinalIgnoreCase)))
                    if (Target(s[(s.IndexOf('=') + 1)..].Trim(), byEdid) is { } t) templates.Add(t);
                foreach (var v in filter[(filter.IndexOf('=') + 1)..].Split(','))
                {
                    var key = Target(v.Trim(), byEdid);
                    if (key == null) continue;
                    if (!result.TryGetValue(key.Value, out var l)) result[key.Value] = l = [];
                    if (!l.Contains(source)) l.Add(source);
                }
            }
        }
        return result;
    }

    /// <summary>« Skyrim.esm|13BAB », « Skyrim.esm|0x13BAB » ou un EditorID.</summary>
    static FormKey? Target(string v, Dictionary<string, FormKey> byEdid)
    {
        var bar = v.IndexOf('|');
        if (bar < 0) return byEdid.TryGetValue(v, out var k) ? k : null;
        var hex = v[(bar + 1)..].Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var id)
            && ModKey.TryFromFileName(v[..bar].Trim(), out var mk) ? new FormKey(mk, id & 0xFFFFFF) : null;
    }
}
