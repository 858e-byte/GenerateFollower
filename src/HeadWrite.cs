using System.Text;
using System.Text.Json.Nodes;
using NiflySharp.Structs;

namespace GenerateFollower;

/// <summary>
/// ESSAI (14/11) : se passer de l'Export Head de RaceMenu. Le jeu rejette un FaceGen réécrit par nifly (tests 9 et 10),
/// mais accepte un export modifié « sur place » (chaînes, chemin de teinte, drapeaux). Ici on remplace, sur place aussi,
/// seulement les positions des sommets des formes d'un FaceGen déjà construit (tableau dynamique des BSDynamicTriShape :
/// 4 flottants par sommet à la fin du bloc) par celles que HeadGen calcule depuis un .jslot. Aucune taille de bloc ne
/// change ; normales, peau, triangles et tous les autres blocs restent ceux de l'export.
/// Garde-fou : une forme n'est réécrite que si nos sommets calculés pour le preset de l'export lui-même retombent sur
/// ceux de l'export (même ordre, même repère) ; sinon elle est laissée telle quelle et signalée.
/// Commande : sommets &lt;facegen.nif&gt; &lt;id du preset de l'export&gt; &lt;id du preset source des curseurs&gt;.
/// </summary>
public static class HeadWrite
{
    /// <summary>Bas du cou de la tête : bande (unités au-dessus du sommet le plus bas) gardée telle que dans l'export, puis
    /// bande de transition vers le déplacement complet.</summary>
    const double NeckLock = 1.0, NeckFade = 3.0;
    /// <summary>Écart maximal (unités du jeu) entre nos sommets et ceux de l'export pour juger une forme « maîtrisée ».</summary>
    const double Tolerance = 0.5;

    /// <summary>
    /// Gabarit pour un preset sans tête utilisable (verdict bleu « sans export », rouge « pièce manquante », orange) :
    /// l'export d'un preset au verdict vert (accepté tel quel par le jeu), du même sexe, qui a la même tête (headpart
    /// Face) et le plus de pièces en commun. Le preset cible prendra cet export (choix manuel de HeadImports) : au build,
    /// MatchExport remplace ses pièces absentes de l'export par celles de l'export, du même type, donc par des pièces
    /// installées ; la commande « sommets » y écrit ensuite la forme du visage de la cible.
    /// Commande : gabarit &lt;id cible&gt; [--appliquer]. Rend le rapport ; la première ligne « DONNEUR id » sert aux scripts.
    /// </summary>
    /// <param name="Gap">écart maximal entre notre calcul du visage du donneur et son export ; null : pas mesurable</param>
    public record Candidate(PresetRow Row, string Import, int Common, int Shapes, double? Gap)
    {
        /// <summary>false : le visage d'un autre preset ne pourra pas être écrit sur cet export (garde-fou). Cas courant : export
        /// téléchargé avec le preset, fait chez son auteur (Pike sur SafiyaRD, 19/11).</summary>
        public bool FaceOk => Gap is not { } g || g <= Tolerance;
    }

    /// <summary>Le rapport de <see cref="Run"/> dit-il que le visage (headpart Face) a été écrit ? Seule ligne avec le cou.</summary>
    public static bool FaceWritten(string log) => log.Contains(" ; cou : ");

    static readonly object gapLock = new();

    /// <summary>Écart maximal (unités du jeu) entre nos sommets du visage et ceux de l'export du preset, avec ses propres curseurs :
    /// le garde-fou de <see cref="Run"/>, mesuré d'avance. 999 : forme absente de l'export. null : fiche ou calcul impossible.
    /// Gardé dans work\donor_gaps.json tant que l'export, le preset et la fiche ne changent pas.</summary>
    static double? FaceGap(Context ctx, PresetRow row, string export, JsonObject cache)
    {
        var fichePath = Path.Combine(ctx.WorkDir, "headparts", row.Id + ".json");
        var jslot = ctx.PresetPath(row.Preset);
        if (!File.Exists(fichePath) || !File.Exists(jslot)) return null;
        var stamp = $"{File.GetLastWriteTimeUtc(export).Ticks}|{new FileInfo(export).Length}|{File.GetLastWriteTimeUtc(jslot).Ticks}|{row.Race}";
        if (cache[row.Id] is JsonObject hit && hit["stamp"]?.GetValue<string>() == stamp) return hit["gap"]?.GetValue<double>();
        double? gap = null;
        try
        {
            var hp = JsonNode.Parse(File.ReadAllText(fichePath))!["headParts"]!.AsArray().FirstOrDefault(h => h!["hpType"]?.GetValue<string>() == "Face");
            if (hp != null && new HeadGen(ctx).MorphPart(hp, JsonNode.Parse(File.ReadAllText(jslot))!, row.Race) is { } mine)
            {
                string edid = hp["edid"]!.GetValue<string>(), srcEdid = hp["srcEdid"]?.GetValue<string>() ?? edid;
                var d = File.ReadAllBytes(export);
                int n = mine.Length / 3, at = SmpHair.DynamicVertices(d, [edid, srcEdid], n);
                double max = at < 0 ? 999 : 0;
                if (at >= 0)
                    for (int i = 0; i < n; i++)
                    {
                        double dx = mine[i * 3] - BitConverter.ToSingle(d, at + i * 16), dy = mine[i * 3 + 1] - BitConverter.ToSingle(d, at + i * 16 + 4),
                            dz = mine[i * 3 + 2] - BitConverter.ToSingle(d, at + i * 16 + 8);
                        max = Math.Max(max, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                    }
                gap = max;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        cache[row.Id] = new JsonObject { ["stamp"] = stamp, ["gap"] = gap };
        return gap;
    }

    /// <summary>Noms des formes d'un export (cache par chemin et date : la liste des donneurs relit ~90 exports).</summary>
    static readonly Dictionary<string, (DateTime Date, HashSet<string> Shapes)> shapeCache = new(StringComparer.OrdinalIgnoreCase);

    static HashSet<string>? ExportShapes(string path)
    {
        var date = File.GetLastWriteTimeUtc(path);
        lock (shapeCache)
            if (shapeCache.TryGetValue(path, out var hit) && hit.Date == date) return hit.Shapes;
        try
        {
            var shapes = Nif.Load(path).Shapes.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (shapeCache) shapeCache[path] = (date, shapes);
            return shapes;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>Donneurs possibles pour un preset, du meilleur au moins bon (voir <see cref="Donor"/>), avec le nombre de
    /// pièces à modèle de la cible et sa tête.</summary>
    public static (List<Candidate> Ranked, int Parts, string Face) Candidates(Context ctx, PresetRow t, List<PresetRow>? rows = null)
    {
        rows ??= PresetTable.Read(ctx.CsvPath);
        var race = ctx.Find<Mutagen.Bethesda.Skyrim.IRaceGetter>(t.Race);
        var parts = HeadParts.Effective(ctx, Jslot.Load(ctx.PresetPath(t.Preset)), race, t.Female);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(Mutagen.Bethesda.Skyrim.IHeadPartGetter h, int depth)
        {
            if (h.EditorID != null && h.Model?.File != null) names.Add(h.EditorID);
            if (depth < 4) foreach (var e in h.ExtraParts) if (ctx.Cache.TryResolve<Mutagen.Bethesda.Skyrim.IHeadPartGetter>(e.FormKey, out var x)) Walk(x, depth + 1);
        }
        foreach (var p in parts) Walk(p, 0);
        var face = parts.FirstOrDefault(p => p.Type == Mutagen.Bethesda.Skyrim.HeadPart.TypeEnum.Face)?.EditorID
            ?? throw new GfException($"{t.Nom} : pas de headpart Face (race {t.Race})");
        var verdicts = File.Exists(Path.Combine(ctx.WorkDir, "verdicts.json")) ? JsonNode.Parse(File.ReadAllText(Path.Combine(ctx.WorkDir, "verdicts.json")))!.AsObject() : [];
        var found = new List<(PresetRow Row, string Import, int Common, int Shapes)>();
        foreach (var g in rows)
        {
            if (g.Id == t.Id || g.Female != t.Female || verdicts[g.Id]?["niveau"]?.GetValue<string>() != "vert") continue;
            var fp = Path.Combine(ctx.WorkDir, "headparts", g.Id + ".json");
            if (!File.Exists(fp) || JsonNode.Parse(File.ReadAllText(fp))?["import"]?.GetValue<string>() is not { } imp || !File.Exists(imp)) continue;
            if (ExportShapes(imp) is not { } shapes) continue;
            if (!shapes.Contains(face)) continue;
            found.Add((g, imp, shapes.Count(names.Contains), shapes.Count));
        }
        // exports dont on sait réécrire le visage d'abord (les autres donneraient le visage du donneur), puis le plus de pièces
        // communes ; à égalité, même race, puis l'export le plus simple (moins de formes de coiffure)
        var measured = new List<Candidate>();
        lock (gapLock)
        {
            var gapPath = Path.Combine(ctx.WorkDir, "donor_gaps.json");
            JsonObject cache = [];
            try { if (File.Exists(gapPath)) cache = JsonNode.Parse(File.ReadAllText(gapPath)) as JsonObject ?? []; }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
            foreach (var f in found) measured.Add(new Candidate(f.Row, f.Import, f.Common, f.Shapes, FaceGap(ctx, f.Row, f.Import, cache)));
            try { File.WriteAllText(gapPath, cache.ToJsonString()); } catch (IOException) { }
        }
        return (measured.OrderByDescending(f => f.FaceOk).ThenByDescending(f => f.Common).ThenByDescending(f => f.Row.Race == t.Race).ThenBy(f => f.Shapes)
            .ToList(), names.Count, face);
    }

    /// <param name="option">--option : pose l'option « teteDonneur » de la fiche (vraie option du Builder) au lieu du
    /// choix manuel d'export de l'essai</param>
    public static string Donor(Context ctx, string targetId, bool apply, bool option = false)
    {
        var rows = PresetTable.Read(ctx.CsvPath);
        var t = rows.FirstOrDefault(r => r.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase)) ?? throw new GfException($"preset {targetId} inconnu");
        var (ranked, partCount, face) = Candidates(ctx, t, rows);
        var log = new StringBuilder();
        if (ranked.Count == 0) { log.AppendLine($"DONNEUR aucun  ({t.Nom} : aucun export au verdict vert n'a la tête {face})"); return log.ToString(); }
        var best = ranked[0];
        log.AppendLine($"DONNEUR {best.Row.Id}");
        log.AppendLine($"GABARIT {t.Id} {t.Nom} [{t.Race}] : {partCount} pièces à modèle, tête {face}");
        foreach (var f in ranked.Take(3))
            log.AppendLine($"  {(f == best ? "->" : "  ")} {f.Row.Id} {f.Row.Nom} [{f.Row.Race}] : {f.Common}/{partCount} pièces communes, {f.Shapes} formes, écart {f.Gap:F3}{(f.FaceOk ? "" : " VISAGE NON ÉCRIT")} ({ctx.CharGenRelOf(f.Import)})");
        if (option)
        {
            Options.Set(ctx, t.Id, Options.DonorHeadOption, System.Text.Json.Nodes.JsonValue.Create(best.Row.Id));
            log.AppendLine($"  option « {Options.DonorHeadOption} » = {best.Row.Id} ({best.Row.Nom}) dans work\\options_choice.json");
        }
        if (apply)
        {
            HeadImports.SaveChoice(ctx, t.Id, "manuel", ctx.CharGenRelOf(best.Import));
            log.AppendLine($"  export de {best.Row.Nom} donné à {t.Nom} (work\\head_imports.json, choix manuel)");
        }
        return log.ToString();
    }

    /// <summary>
    /// GF_GABARIT=« idCible:idDonneur,… » : pour un preset cible, le .jslot du donneur, dont on prend les pièces de tête
    /// (headparts). Le FaceGen est l'export du donneur : le follower doit porter exactement les pièces que le jeu a déjà
    /// acceptées avec cet export ; de la cible ne viennent que la forme du visage (commande « sommets »), la teinte, les
    /// couleurs et le poids. null hors essai.
    /// </summary>
    public static Jslot? DonorJslot(Context ctx, string presetRel, StringBuilder? report = null)
    {
        var rows = PresetTable.Read(ctx.CsvPath);
        if (rows.FirstOrDefault(r => r.Preset.Equals(presetRel, StringComparison.OrdinalIgnoreCase)) is not { } target) return null;
        if (DonorRow(ctx, rows, target) is not { } donor) return null;
        report?.AppendLine($"GABARIT {target.Id}  pièces de tête du preset donneur {donor.Nom} ({donor.Id})");
        return Jslot.Load(ctx.PresetPath(donor.Preset));
    }

    /// <summary>Donneur de tête du preset : variable d'essai GF_GABARIT d'abord, puis l'option « teteDonneur » de sa
    /// fiche. null : la tête est la sienne. Seul endroit qui en décide (pièces, export, teinte, poids, cou, visage).</summary>
    public static PresetRow? DonorRow(Context ctx, List<PresetRow> rows, PresetRow target)
    {
        var env = Environment.GetEnvironmentVariable("GF_GABARIT");
        if (!string.IsNullOrWhiteSpace(env))
            foreach (var pair in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var ids = pair.Split(':');
                if (ids.Length == 2 && ids[0].Equals(target.Id, StringComparison.OrdinalIgnoreCase))
                    return rows.FirstOrDefault(r => r.Id.Equals(ids[1], StringComparison.OrdinalIgnoreCase));
            }
        return Options.ChosenDonor(ctx, target.Id) is { } id && !id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)
            ? rows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) : null;
    }

    /// <summary>Même chose sans liste de presets : ne relit presets.csv que s'il y a un donneur (appelé pour chaque
    /// preset par l'analyse du Builder).</summary>
    public static PresetRow? DonorRow(Context ctx, PresetRow target) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GF_GABARIT")) && Options.ChosenDonor(ctx, target.Id) == null
            ? null : DonorRow(ctx, PresetTable.Read(ctx.CsvPath), target);

    /// <summary>
    /// Preset dont vient vraiment la tête du follower, quand ce n'est pas le sien : donneur de l'essai gabarit
    /// (GF_GABARIT), sinon export choisi à la main (work\head_imports.json) au nom exact d'un autre preset. null : la tête
    /// est celle du preset. Le cou de l'export a la forme du corps du DONNEUR (son poids, ses réglages d'os du cou, ses
    /// morphs « __HEAD ») : le follower doit les reprendre, sinon ligne au cou (Gabarit_Test2, validé en jeu le 17/11 :
    /// `setnpcweight <poids du donneur>` la fait disparaître chez Airon, Tatio, Pike).
    /// </summary>
    public static (PresetRow Row, Jslot J)? HeadOwner(Context ctx, PresetRow target)
    {
        var rows = PresetTable.Read(ctx.CsvPath);
        var donor = DonorRow(ctx, rows, target);
        if (donor == null && HeadImports.LoadChoices(ctx)[target.Id] is JsonObject c && c["mode"]?.GetValue<string>() == "manuel"
            && c["path"]?.GetValue<string>() is { Length: > 0 } path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            bool Named(PresetRow r) => Path.GetFileName(r.Preset).Equals(stem, StringComparison.OrdinalIgnoreCase)
                || r.NomPreset.Equals(stem, StringComparison.OrdinalIgnoreCase);
            if (!Named(target) && rows.Where(Named).ToList() is [var only]) donor = only;
        }
        if (donor == null || donor.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)) return null;
        var file = ctx.PresetPath(donor.Preset);
        return File.Exists(file) ? (donor, Jslot.Load(file)) : null;
    }

    /// <summary>
    /// Distance (le long de la surface, en unités du jeu) de chaque sommet de la tête au bord du cou : bord ouvert du
    /// maillage (arêtes d'un seul triangle, sommets soudés par position pour ignorer les coutures d'UV) qui contient le
    /// sommet le plus bas. Les yeux et la bouche, autres bords ouverts, ne comptent pas. null si la forme n'est pas lisible.
    /// </summary>
    static double[]? NeckDistances(string nifPath, string[] names, byte[] d, int at, int n, StringBuilder log)
    {
        List<Triangle> tris;
        try
        {
            var shape = Nif.Load(nifPath).Shapes.FirstOrDefault(s => names.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
            if (shape == null || shape.VertexCount != n || shape.Tris.Count == 0) return null;
            tris = shape.Tris;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        var pos = new (float X, float Y, float Z)[n];
        for (int i = 0; i < n; i++) pos[i] = (BitConverter.ToSingle(d, at + i * 16), BitConverter.ToSingle(d, at + i * 16 + 4), BitConverter.ToSingle(d, at + i * 16 + 8));
        // soudure par position (au 1/1000)
        var rep = new int[n];
        var byPos = new Dictionary<(long, long, long), int>();
        for (int i = 0; i < n; i++)
        {
            var k = ((long)Math.Round(pos[i].X * 1000), (long)Math.Round(pos[i].Y * 1000), (long)Math.Round(pos[i].Z * 1000));
            rep[i] = byPos.TryGetValue(k, out var r) ? r : byPos[k] = i;
        }
        var edges = new Dictionary<(int, int), int>();
        var adj = new Dictionary<int, HashSet<int>>();
        void Edge(int a, int b)
        {
            a = rep[a]; b = rep[b];
            if (a == b) return;
            var key = a < b ? (a, b) : (b, a);
            edges[key] = edges.GetValueOrDefault(key) + 1;
            (adj.TryGetValue(a, out var sa) ? sa : adj[a] = []).Add(b);
            (adj.TryGetValue(b, out var sb) ? sb : adj[b] = []).Add(a);
        }
        foreach (var t in tris) { Edge(t.V1, t.V2); Edge(t.V2, t.V3); Edge(t.V3, t.V1); }
        var bAdj = new Dictionary<int, List<int>>();
        foreach (var ((a, b), c) in edges)
            if (c == 1) { (bAdj.TryGetValue(a, out var la) ? la : bAdj[a] = []).Add(b); (bAdj.TryGetValue(b, out var lb) ? lb : bAdj[b] = []).Add(a); }
        if (bAdj.Count == 0) return null;
        // bord qui contient le sommet de bord le plus bas
        int start = bAdj.Keys.MinBy(v => pos[v].Z);
        var ring = new HashSet<int> { start };
        var stack = new Stack<int>([start]);
        while (stack.Count > 0) foreach (var nb in bAdj[stack.Pop()]) if (ring.Add(nb)) stack.Push(nb);
        // Dijkstra depuis le bord du cou, sur les arêtes du maillage soudé
        var dist = new double[n];
        Array.Fill(dist, double.MaxValue);
        var pq = new PriorityQueue<int, double>();
        foreach (var v in ring) { dist[v] = 0; pq.Enqueue(v, 0); }
        double Len(int a, int b) { var (dx, dy, dz) = (pos[a].X - pos[b].X, pos[a].Y - pos[b].Y, pos[a].Z - pos[b].Z); return Math.Sqrt(dx * dx + dy * dy + dz * dz); }
        while (pq.TryDequeue(out var v, out var dv))
        {
            if (dv > dist[v]) continue;
            if (!adj.TryGetValue(v, out var nbs)) continue;
            foreach (var nb in nbs)
            {
                var nd = dv + Len(v, nb);
                if (nd < dist[nb]) { dist[nb] = nd; pq.Enqueue(nb, nd); }
            }
        }
        for (int i = 0; i < n; i++) dist[i] = dist[rep[i]];
        float zLo = ring.Min(v => pos[v].Z), zHi = ring.Max(v => pos[v].Z);
        log.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"  bord du cou : {ring.Count} sommets, hauteur de {zLo:F2} à {zHi:F2} (écart {zHi - zLo:F2})"));
        return dist;
    }

    /// <param name="guardId">preset dont l'export vient vraiment (gabarit d'un autre preset) : c'est avec ses curseurs que
    /// l'on vérifie que nos sommets retombent sur l'export ; null = le preset de la fiche</param>
    public static string Run(Context ctx, string nifPath, string exportId, string sourceId, string? guardId = null, bool keepCopy = true)
    {
        var rows = PresetTable.Read(ctx.CsvPath);
        PresetRow Row(string id) => rows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new GfException($"preset {id} inconnu");
        PresetRow exp = Row(exportId), src = Row(sourceId);
        var fichePath = Path.Combine(ctx.WorkDir, "headparts", exp.Id + ".json");
        if (!File.Exists(fichePath)) throw new GfException($"fiche {fichePath} absente : construire d'abord le follower");
        var fiche = JsonNode.Parse(File.ReadAllText(fichePath))!;
        var own = JsonNode.Parse(File.ReadAllText(ctx.PresetPath((guardId != null ? Row(guardId) : exp).Preset)))!;
        var other = JsonNode.Parse(File.ReadAllText(ctx.PresetPath(src.Preset)))!;
        var heads = new HeadGen(ctx);
        var before = File.ReadAllBytes(nifPath);
        var d = (byte[])before.Clone();
        var log = new StringBuilder();
        log.AppendLine($"SOMMETS {Path.GetFileName(nifPath)} : export de {exp.Nom} ({exp.Id}), curseurs de {src.Nom} ({src.Id})");
        int written = 0;
        foreach (var hp in fiche["headParts"]!.AsArray())
        {
            string edid = hp!["edid"]!.GetValue<string>(), srcEdid = hp["srcEdid"]?.GetValue<string>() ?? edid;
            if (heads.MorphPart(hp, own, exp.Race) is not { } mine) continue;   // pas de modèle
            int n = mine.Length / 3;
            var at = SmpHair.DynamicVertices(d, [edid, srcEdid], n);
            if (at < 0) { log.AppendLine($"  {edid,-44} {n,6} sommets : forme absente du FaceGen ou d'un autre maillage, laissée"); continue; }
            // garde-fou : nos sommets pour le preset de l'export contre ceux de l'export
            double sum = 0, max = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = mine[i * 3] - BitConverter.ToSingle(d, at + i * 16), dy = mine[i * 3 + 1] - BitConverter.ToSingle(d, at + i * 16 + 4),
                    dz = mine[i * 3 + 2] - BitConverter.ToSingle(d, at + i * 16 + 8);
                double e = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                sum += e; max = Math.Max(max, e);
            }
            if (max > Tolerance) { log.AppendLine($"  {edid,-44} {n,6} sommets : écart avec l'export moyen {sum / n:F3}, max {max:F3} > {Tolerance} : laissée"); continue; }
            var theirs = heads.MorphPart(hp, other, exp.Race)!;
            // Cou (tête, headpart Face) : l'anneau du bas rejoint le corps, qui ne bouge pas. Déplacé par un sculpt ou des
            // curseurs qui touchent le cou, il ouvrait le raccord (Gabriellee avec les curseurs d'asagi v2, essai du 14/11).
            // Les sommets du bas du cou gardent la position de l'export (bande de NeckLock unités au-dessus du point le
            // plus bas), puis le déplacement reprend progressivement sur NeckFade unités : pas de marche visible.
            bool face = hp["hpType"]?.GetValue<string>() == "Face";
            // Distance de chaque sommet au bord du cou (Ruvi, 17/11 : avec une bande horizontale au-dessus du point le
            // plus bas, les côtés du cou, plus hauts que l'arrière, bougeaient : deux fentes sous la mâchoire, absentes
            // sans réécriture). Sans triangles lisibles : l'ancienne règle (hauteur au-dessus du point le plus bas).
            double[]? neckDist = face ? NeckDistances(nifPath, [edid, srcEdid], d, at, n, log) : null;
            double zMin = double.MaxValue;
            if (face) for (int i = 0; i < n; i++) zMin = Math.Min(zMin, BitConverter.ToSingle(d, at + i * 16 + 8));
            double moved = 0, movedMax = 0, neckWould = 0;
            int locked = 0;
            for (int i = 0; i < n; i++)
            {
                double w = 1;
                if (face)
                {
                    double h = neckDist != null ? neckDist[i] : BitConverter.ToSingle(d, at + i * 16 + 8) - zMin;
                    w = Math.Clamp((h - NeckLock) / NeckFade, 0, 1);
                }
                double m = 0, full = 0;
                for (int k = 0; k < 3; k++)
                {
                    float old = BitConverter.ToSingle(d, at + i * 16 + k * 4), target = (float)theirs[i * 3 + k];
                    float val = (float)(old + w * (target - old));
                    full += (target - old) * (double)(target - old);
                    m += (val - old) * (double)(val - old);
                    BitConverter.GetBytes(val).CopyTo(d, at + i * 16 + k * 4);
                }
                if (w < 1) { locked++; neckWould = Math.Max(neckWould, Math.Sqrt(full)); }
                m = Math.Sqrt(m); moved += m; movedMax = Math.Max(movedMax, m);
            }
            written++;
            log.AppendLine($"  {edid,-44} {n,6} sommets : écart avec l'export moyen {sum / n:F3}, max {max:F3} ; ÉCRITE, déplacement moyen {moved / n:F3}, max {movedMax:F3}"
                + (face ? $" ; cou : {locked} sommets retenus (sans cela, jusqu'à {neckWould:F3})" : ""));
        }
        if (d.Length != before.Length) throw new GfException("taille du fichier changée : abandon");
        int diff = 0;
        for (int i = 0; i < d.Length; i++) if (d[i] != before[i]) diff++;
        log.AppendLine($"  {written} formes écrites ; {diff} octets changés sur {d.Length} (taille du fichier inchangée)");
        if (written > 0)
        {
            if (keepCopy && !File.Exists(nifPath + ".avant")) File.WriteAllBytes(nifPath + ".avant", before);
            File.WriteAllBytes(nifPath, d);
        }
        return log.ToString();
    }
}

public static partial class SmpHair
{
    /// <summary>Position (dans le fichier) du tableau dynamique des sommets d'une forme BSDynamicTriShape nommée par l'un de
    /// <paramref name="names"/>, si elle a <paramref name="count"/> sommets : le bloc finit par la taille du tableau
    /// (uint32 = 16 × sommets) puis 4 flottants par sommet. −1 sinon.</summary>
    internal static int DynamicVertices(byte[] d, IEnumerable<string> names, int count)
    {
        if (Raw.Parse(d) is not { } raw) return -1;
        for (int b = 0; b < raw.Types.Length; b++)
        {
            if (raw.Types[b] != "BSDynamicTriShape" || raw.Name(b) is not { } n || !names.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
            int end = b + 1 < raw.Off.Length ? raw.Off[b + 1] : -1;
            if (end < 0) continue;
            int at = end - count * 16;
            if (at - 4 > raw.Off[b] && BitConverter.ToUInt32(d, at - 4) == (uint)(count * 16)) return at;
        }
        return -1;
    }
}
