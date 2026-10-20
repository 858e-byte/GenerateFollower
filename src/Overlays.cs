using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace GenerateFollower;

/// <summary>
/// Overlays RaceMenu (tatouages, maquillage, peintures) : les valeurs NiOverride du jslot (overrides[]) sont rangées
/// dans les propriétés du script GF_FollowerOverlays, attaché au PNJ, qui les applique au chargement (AddOverlays puis
/// AddNodeOverride*). Les textures sont copiées dans le mod (followers autonomes).
/// </summary>
public class Overlays(Context ctx)
{
    public const string Script = "GF_FollowerOverlays";
    static readonly Regex NodeRx = new(@"^(Body|Hands|Feet|Face) \[Ovl(\d+)\]$", RegexOptions.IgnoreCase);
    // Clés NiOverride (NiOverride.psc) : 0 et 7 entiers (couleurs), 9 texture, 6 TextureSet (non géré), le reste flottant
    static readonly int[] IntKeys = [0, 7];
    const int AlphaKey = 8, TextureKey = 9, TextureSetKey = 6;

    record Entry(string Node, int Key, int Index, int I, float F, string S);

    readonly Dictionary<string, int> counts = ReadCounts(ctx);
    readonly Dictionary<string, bool> exists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Textures d'overlays utilisées (relatives à Data), à copier dans le mod.</summary>
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nombre d'overlays par nœud dans skee64.ini (iNumOverlays) : un overlay d'index plus grand n'existe pas
    /// sur l'acteur (Violet : Body [Ovl41]…, créés par d'autres mods pour le joueur).</summary>
    static Dictionary<string, int> ReadCounts(Context ctx)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Body"] = 6, ["Hands"] = 3, ["Feet"] = 3, ["Face"] = 3 };
        var ini = Path.Combine(ctx.DataDir, "SKSE", "Plugins", "skee64.ini");
        if (!File.Exists(ini)) return counts;
        string? section = null;
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Split(';')[0].Trim();
            var m = Regex.Match(line, @"^\[Overlays/(\w+)\]");
            if (m.Success) { section = m.Groups[1].Value; continue; }
            if (line.StartsWith('[')) { section = null; continue; }
            m = Regex.Match(line, @"^iNumOverlays\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (section != null && m.Success) counts[section] = int.Parse(m.Groups[1].Value);
        }
        return counts;
    }

    /// <summary>« Actors\…\x.dds », « textures\… », « data/textures/… » -> « textures\Actors\…\x.dds ».</summary>
    public static string NormTexture(string path)
    {
        var p = path.Replace('/', '\\').TrimStart('\\');
        if (p.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? p : @"textures\" + p;
    }

    /// <summary>Vérifie en une fois (loose ou BSA) l'existence des textures de tous les presets.</summary>
    public void Prefetch(IEnumerable<Jslot> slots)
    {
        var todo = slots.SelectMany(j => j.Overrides).Where(v => v.Key == TextureKey && v.Data?.GetValueKind() == System.Text.Json.JsonValueKind.String)
            .Select(v => NormTexture(v.Data!.GetValue<string>())).Where(t => !exists.ContainsKey(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (todo.Count == 0) return;
        var list = Path.Combine(ctx.WorkDir, "overlay_textures.json");
        File.WriteAllText(list, System.Text.Json.JsonSerializer.Serialize(todo));
        var found = JsonNode.Parse(Py.Run(ctx, "find_files.py", list))!.AsObject();
        foreach (var t in todo) exists[t] = found[t]?.GetValue<bool>() ?? false;
    }

    /// <summary>
    /// Cuit les overlays du visage dans une copie de la diffuse de la tête (offline\bake_face.py) :
    /// textures\GenerateFollower\&lt;plugin&gt;\&lt;FormID&gt;_visage.dds, puis fait pointer la tête du FaceGen dessus (réécriture
    /// binaire des chemins de textures, comme pour la teinte). Renvoie le chemin de la diffuse, ou null en cas d'échec.
    /// </summary>
    string? BakeFace(string faceGen, string diffuse, List<(int Index, string Tex, int? Color, float Alpha)> layers,
        string modDir, string plugin, StringBuilder report, PresetRow row)
    {
        var fid = Path.GetFileNameWithoutExtension(faceGen);
        var rel = $@"textures\GenerateFollower\{plugin}\{fid}_visage.dds";
        var job = new JsonObject
        {
            // diffuse d'un skin choisi : déjà copiée dans le mod, absente de Data
            ["base"] = File.Exists(Path.Combine(modDir, NormTexture(diffuse))) ? Path.Combine(modDir, NormTexture(diffuse)) : NormTexture(diffuse),
            ["out"] = Path.Combine(modDir, rel),
            ["layers"] = new JsonArray(layers.Select(l => (JsonNode)new JsonObject { ["tex"] = l.Tex, ["color"] = l.Color, ["alpha"] = l.Alpha }).ToArray()),
        };
        // diagnostic : GF_BAKE_RED=<ids> -> diffuse rouge vif (la diffuse cuite est-elle celle que le jeu affiche ?)
        if ((Environment.GetEnvironmentVariable("GF_BAKE_RED") ?? "").Split(',').Contains(row.Id, StringComparer.OrdinalIgnoreCase))
        {
            job["red"] = true;
            report.AppendLine($"OVERLAY {row.Id}  DIAGNOSTIC : diffuse du visage rouge vif (GF_BAKE_RED)");
        }
        var jobPath = Path.Combine(ctx.WorkDir, "bake_face.json");
        File.WriteAllText(jobPath, job.ToJsonString());
        try
        {
            var res = JsonNode.Parse(Py.Run(ctx, "bake_face.py", jobPath).Trim().Split('\n')[^1]);
            foreach (var m in res?["introuvables"]?.AsArray() ?? []) report.AppendLine($"OVERLAY {row.Id}  overlay du visage introuvable : {m}");
        }
        catch (GfException e)
        {
            // ex. diffuse de la tête introuvable (Liana : RSV\NordFemale\FemaleHead.dds, mod absent) : rien à peindre
            report.AppendLine($"ATTENTION {row.Id}  cuisson des overlays du visage impossible : {string.Join(" ", e.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))}");
            return null;
        }
        // la diffuse d'origine (même chemin, à « textures\ » près) est remplacée partout où la tête l'utilise
        var target = NormTexture(diffuse);
        var bytes = NifPatcher.ReplaceTextures(File.ReadAllBytes(faceGen),
            t => NormTexture(t).Equals(target, StringComparison.OrdinalIgnoreCase) ? rel : null, out var n);
        if (n == 0) { report.AppendLine($"ATTENTION {row.Id}  diffuse du visage non trouvée dans le FaceGen ({diffuse})"); return null; }
        File.WriteAllBytes(faceGen, bytes);
        return rel;
    }

    /// <summary>
    /// Script GF_FollowerOverlays du PNJ, ou null s'il n'a aucun overlay visible. Gardés : nœuds Body/Hands/Feet/Face
    /// [Ovl#] existants, avec une texture (slot 0) installée autre que default.dds et un alpha non nul.
    /// </summary>
    /// <param name="faceGen">FaceGen livré du PNJ (null s'il n'en a pas) : dit si la tête est UBE (overlays du visage gardés)</param>
    /// <param name="bake">dossier du mod et plugin : pour une tête non UBE, les overlays du visage sont cuits dans une
    /// diffuse du visage écrite dans le mod (null : ils sont retirés)</param>
    public ScriptEntry? For(Jslot j, PresetRow row, string? faceGen, StringBuilder report, (string ModDir, string Plugin)? bake = null)
    {
        // Diagnostic (visage pâle des têtes HPH au test overlays) : GF_OVL_NONE=<ids> -> pas de script ;
        // GF_OVL_NOFACE=<ids> -> overlays du visage retirés (AddOverlays et corps conservés)
        static bool In(string env, string id) => (Environment.GetEnvironmentVariable(env) ?? "").Split(',').Contains(id, StringComparer.OrdinalIgnoreCase);
        if (In("GF_OVL_NONE", row.Id)) { report.AppendLine($"OVERLAY {row.Id}  DIAGNOSTIC : aucun overlay (GF_OVL_NONE)"); return null; }
        bool noFace = In("GF_OVL_NOFACE", row.Id);
        if (noFace) report.AppendLine($"OVERLAY {row.Id}  DIAGNOSTIC : overlays du visage retirés (GF_OVL_NOFACE)");
        // Tête du FaceGen : normal map en espace objet (HPH, vanilla) ou non (UBE), voir plus bas
        JsonNode? head = null;
        if (faceGen != null && File.Exists(faceGen) && j.Overrides.Any(v => v.Node.StartsWith("Face", StringComparison.OrdinalIgnoreCase)))
            head = JsonNode.Parse(Py.Run(ctx, "head_shader.py", faceGen));
        bool msn = head?["msn"]?.GetValue<bool>() == true;
        var entries = new List<Entry>();
        var kept = new List<string>();
        var dropped = new List<string>();
        var skippedFace = new List<string>();
        var faceLayers = new List<(int Index, string Tex, int? Color, float Alpha)>();
        foreach (var g in j.Overrides.GroupBy(v => v.Node))
        {
            var m = NodeRx.Match(g.Key);
            if (!m.Success || !counts.TryGetValue(m.Groups[1].Value, out var n) || int.Parse(m.Groups[2].Value) >= n) continue;
            bool isFace = m.Groups[1].Value.Equals("Face", StringComparison.OrdinalIgnoreCase);
            if (noFace && isFace) continue;
            var vals = g.ToList();
            var alpha = vals.LastOrDefault(v => v.Key == AlphaKey)?.Data is { } a ? (float)a.GetValue<double>() : 1f;
            var tex = vals.LastOrDefault(v => v.Key == TextureKey && v.Index == 0)?.Data?.GetValue<string>();
            if (alpha <= 0.001f || string.IsNullOrWhiteSpace(tex) || NormTexture(tex).EndsWith(@"overlays\default.dds", StringComparison.OrdinalIgnoreCase)) continue;
            if (!exists.GetValueOrDefault(NormTexture(tex))) { dropped.Add($"{g.Key} ({tex})"); continue; }
            // Têtes non UBE (normal map en espace objet : HPH, vanilla) : un overlay du visage par script fausse la
            // couleur du visage en jeu (thorquel, test A/B : correct sans ses overlays du visage, corps conservés ; Nora,
            // UBE, correcte avec les siens). Ils sont cuits dans une diffuse du visage propre au follower (BakeFace).
            if (msn && isFace)
            {
                var color = vals.LastOrDefault(v => v.Key == 7)?.Data is { } c ? unchecked((int)(long)c.GetValue<double>()) & 0xFFFFFF : (int?)null;
                faceLayers.Add((int.Parse(m.Groups[2].Value), NormTexture(tex), color, alpha));
                skippedFace.Add($"{g.Key} {Path.GetFileName(tex)}");
                continue;
            }

            foreach (var v in vals.GroupBy(v => (v.Key, v.Index)).Select(x => x.Last()))
            {
                if (v.Data == null || v.Key == TextureSetKey) continue;
                if (v.Key == TextureKey)
                {
                    var t = NormTexture(v.Data.GetValue<string>());
                    if (!exists.GetValueOrDefault(t)) { dropped.Add($"{g.Key} slot {v.Index} ({t})"); continue; }
                    Files.Add(t);
                    entries.Add(new Entry(g.Key, v.Key, v.Index, 0, 0f, t));
                }
                else if (IntKeys.Contains(v.Key))
                    entries.Add(new Entry(g.Key, v.Key, v.Index, unchecked((int)(long)v.Data.GetValue<double>()), 0f, ""));
                else
                    entries.Add(new Entry(g.Key, v.Key, v.Index, 0, (float)v.Data.GetValue<double>(), ""));
            }
            kept.Add($"{g.Key} {Path.GetFileName(tex)} α{alpha.ToString("0.##", CultureInfo.InvariantCulture)}");
        }
        foreach (var d in dropped) report.AppendLine($"OVERLAY {row.Id}  texture introuvable, ignoré : {d}");
        if (faceLayers.Count > 0)
        {
            var baked = bake != null && head?["diffuse"]?.GetValue<string>() is { Length: > 0 } diffuse
                ? BakeFace(faceGen!, diffuse, faceLayers.OrderBy(l => l.Index).ToList(), bake.Value.ModDir, bake.Value.Plugin, report, row)
                : null;
            report.AppendLine($"OVERLAY {row.Id}  tête non UBE : overlays du visage " + (baked != null
                ? $"cuits dans la diffuse du visage ({baked}) : {string.Join(", ", skippedFace)}"
                : $"retirés (faussent la couleur en jeu) : {string.Join(", ", skippedFace)}"));
        }
        if (entries.Count == 0) return null;
        report.AppendLine($"OVERLAY {row.Id}  {kept.Count} overlays : {string.Join(", ", kept)}");
        return new ScriptEntry
        {
            Name = Script,
            Properties =
            {
                new ScriptStringListProperty { Name = "OvlNodes", Data = new ExtendedList<string>(entries.Select(e => e.Node)) },
                new ScriptIntListProperty { Name = "OvlKeys", Data = new ExtendedList<int>(entries.Select(e => e.Key)) },
                new ScriptIntListProperty { Name = "OvlIndexes", Data = new ExtendedList<int>(entries.Select(e => e.Index)) },
                new ScriptIntListProperty { Name = "OvlInts", Data = new ExtendedList<int>(entries.Select(e => e.I)) },
                new ScriptFloatListProperty { Name = "OvlFloats", Data = new ExtendedList<float>(entries.Select(e => e.F)) },
                new ScriptStringListProperty { Name = "OvlStrings", Data = new ExtendedList<string>(entries.Select(e => e.S)) },
            },
        };
    }
}
