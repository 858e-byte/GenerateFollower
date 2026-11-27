using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Brillance d'un skin UBE au raccord du cou (option « brillanceCou », 24/11). L'alpha d'une normal map tangente (_n) est le
/// masque spéculaire ; certains skins ont un corps bien plus brillant que leur tête (Necoco Skin Recreation v16 : 96 contre 38),
/// et le raccord se voit en jeu alors que couleur et teinte sont égales. Mesure : moyenne de l'alpha le long de l'anneau du cou
/// de la tête et du corps. Correction, sur la tête seulement : alpha multiplié par le rapport corps / tête au raccord, avec un
/// fondu jusqu'à <see cref="FadeEnd"/> unités du cou ; le visage garde sa brillance (essais en jeu du 24/11 : tout le visage au
/// niveau du corps est trop gras). Texture écrite en RGBA non compressé (une recompression BC7 a déjà faussé des normal maps).
/// </summary>
public static class SkinGloss
{
    public const string Option = "brillanceCou";
    /// <summary>Distance au raccord (unités du jeu) : correction entière jusqu'à FadeStart, nulle après FadeEnd.</summary>
    const double FadeStart = 0.8, FadeEnd = 7.0;
    /// <summary>Écart qui mérite l'option : rapport et différence sur 255 (l'œil ne voit rien en dessous).</summary>
    const double MinRatio = 1.3, MinDiff = 10;
    const int MaxSize = 4096;

    public readonly record struct Gloss(double Head, double Body)
    {
        public bool Uneven => Head > 0.5 && Math.Max(Head / Math.Max(Body, 0.5), Body / Head) >= MinRatio && Math.Abs(Body - Head) >= MinDiff;
    }

    /// <summary>L'option est-elle cochée pour ce preset ?</summary>
    public static bool Chosen(JsonObject opts) => opts[Option] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Skin concerné : UBE (normal maps tangentes, alpha = brillance) avec les deux normal maps.</summary>
    static bool Possible(Skins.Skin s) => s.Body == "UBE" && s.Files.ContainsKey("head_n") && s.Files.ContainsKey("body_n");

    // ------------------------------------------------------------------ maillages

    sealed record Mesh(List<System.Numerics.Vector3> Verts, List<(int A, int B, int C)> Tris, List<(double U, double V)> Uv, List<int> Ring);

    static readonly Dictionary<string, Mesh?> meshes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forme d'un .nif dont la diffuse est <paramref name="diffuse"/>, et son anneau du cou : bord ouvert (sommets soudés
    /// par position) qui contient le sommet de bord le plus bas (tête) ou le plus haut (corps).</summary>
    static Mesh? Load(string path, string diffuse, bool lowest)
    {
        var key = path + "|" + diffuse;
        lock (meshes)
        {
            if (meshes.TryGetValue(key, out var known)) return known;
            Mesh? mesh = null;
            try
            {
                var shape = Nif.Load(path).Shapes.Where(s => Path.GetFileName(s.Texture("Diffuse").Replace('/', '\\')).Equals(diffuse, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.VertexCount).FirstOrDefault();
                if (shape != null)
                {
                    var v = shape.Verts;
                    var tris = shape.Tris.Select(t => ((int)t.V1, (int)t.V2, (int)t.V3)).ToList();
                    var uv = shape.UVs.Select(t => ((double)t.U, (double)t.V)).ToList();
                    if (v.Count > 0 && uv.Count == v.Count && Ring(v, tris, lowest) is { Count: > 0 } ring) mesh = new Mesh(v, tris, uv, ring);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { }
            return meshes[key] = mesh;
        }
    }

    static List<int> Ring(List<System.Numerics.Vector3> v, List<(int, int, int)> tris, bool lowest)
    {
        var rep = new int[v.Count];
        var byPos = new Dictionary<(long, long, long), int>();
        for (int i = 0; i < v.Count; i++)
        {
            var k = ((long)Math.Round(v[i].X * 1000), (long)Math.Round(v[i].Y * 1000), (long)Math.Round(v[i].Z * 1000));
            rep[i] = byPos.TryGetValue(k, out var r) ? r : byPos[k] = i;
        }
        var edges = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in tris)
            foreach (var (x, y) in new[] { (rep[a], rep[b]), (rep[b], rep[c]), (rep[c], rep[a]) })
                if (x != y) { var e = x < y ? (x, y) : (y, x); edges[e] = edges.GetValueOrDefault(e) + 1; }
        var adj = new Dictionary<int, List<int>>();
        foreach (var ((x, y), n) in edges)
            if (n == 1) { (adj.TryGetValue(x, out var lx) ? lx : adj[x] = []).Add(y); (adj.TryGetValue(y, out var ly) ? ly : adj[y] = []).Add(x); }
        if (adj.Count == 0) return [];
        int start = lowest ? adj.Keys.MinBy(i => v[i].Z) : adj.Keys.MaxBy(i => v[i].Z);
        var seen = new HashSet<int> { start };
        var stack = new Stack<int>([start]);
        while (stack.Count > 0) foreach (var k in adj[stack.Pop()]) if (seen.Add(k)) stack.Push(k);
        return Enumerable.Range(0, v.Count).Where(i => seen.Contains(rep[i])).ToList();
    }

    /// <summary>Maillages de base du follower : tête (headpart Face de sa race ou de son preset) et corps (peau de sa race).</summary>
    static (Mesh Head, Mesh Body)? Meshes(Context ctx, PresetRow row)
    {
        var race = ctx.Find<IRaceGetter>(row.Race);
        if (race == null || !ctx.Cache.TryResolve<IArmorGetter>(race.Skin.FormKey, out var skin)) return null;
        var face = Standalone.Face(HeadParts.Effective(ctx, Jslot.Load(ctx.PresetPath(row.Preset)), race, row.Female));
        if (face?.Model?.File?.DataRelativePath.Path is not { Length: > 0 } headRel || ctx.FindMesh(headRel) is not { } headPath) return null;
        var head = Load(headPath, row.Female ? "femalehead_d.dds" : "malehead_d.dds", lowest: true);
        Mesh? body = null;
        foreach (var link in skin.Armature)
        {
            if (!ctx.Cache.TryResolve<IArmorAddonGetter>(link.FormKey, out var aa)) continue;
            if (aa.Race.FormKeyNullable != race.FormKey && !aa.AdditionalRaces.Any(r => r.FormKey == race.FormKey)) continue;
            if (((aa.BodyTemplate?.FirstPersonFlags ?? 0) & BipedObjectFlag.Body) == 0) continue;
            var rel = (row.Female ? aa.WorldModel?.Female : aa.WorldModel?.Male)?.File?.DataRelativePath.Path;
            if (string.IsNullOrEmpty(rel) || ctx.FindMesh(rel) is not { } path) continue;
            body = Load(path, row.Female ? "femalebody_1_d.dds" : "malebody_1_d.dds", lowest: false);
            if (body != null) break;
        }
        return head == null || body == null ? null : (head, body);
    }

    // ------------------------------------------------------------------ mesure

    /// <summary>Moyenne de l'alpha d'une texture le long de l'anneau.</summary>
    static double Sample(Dds.Image img, Mesh m)
    {
        double sum = 0;
        foreach (var i in m.Ring)
        {
            int x = Math.Clamp((int)((m.Uv[i].U - Math.Floor(m.Uv[i].U)) * img.Width), 0, img.Width - 1);
            int y = Math.Clamp((int)((m.Uv[i].V - Math.Floor(m.Uv[i].V)) * img.Height), 0, img.Height - 1);
            sum += img.Rgba[(y * img.Width + x) * 4 + 3];
        }
        return sum / m.Ring.Count;
    }

    static readonly object cacheLock = new();

    /// <summary>Brillance de la tête et du corps d'un skin au cou, ou null (autre corps, texture ou maillage manquant). Gardée dans
    /// work\skins_gloss.json : la première mesure extrait et décode deux normal maps (quelques secondes).</summary>
    public static Gloss? Measure(Context ctx, PresetRow row, Skins skins, Skins.Skin s)
    {
        if (!Possible(s)) return null;
        var cachePath = Path.Combine(ctx.WorkDir, "skins_gloss.json");
        var stamp = string.Join("|", new[] { "head_n", "body_n" }.Select(r => s.Files[r] + ":" + s.Sizes.GetValueOrDefault(r)));
        lock (cacheLock)
        {
            JsonObject cache = [];
            try { if (File.Exists(cachePath)) cache = JsonNode.Parse(File.ReadAllText(cachePath)) as JsonObject ?? []; }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
            if (cache[s.Id] is JsonObject hit && hit["stamp"]?.GetValue<string>() == stamp)
                return hit["tete"] is JsonValue ht ? new Gloss(ht.GetValue<double>(), hit["corps"]!.GetValue<double>()) : null;
            Gloss? g = null;
            try
            {
                if (Meshes(ctx, row) is not { } m) return null;   // maillage pas lisible maintenant : pas mis en cache
                if (skins.SourceFile(s, "head_n") is { } hn && skins.SourceFile(s, "body_n") is { } bn)
                    g = new Gloss(Sample(Dds.DecodeNear(hn, 2048), m.Head), Sample(Dds.DecodeNear(bn, 2048), m.Body));
            }
            catch (Exception e) when (e is not OutOfMemoryException) { return null; }
            cache[s.Id] = g is { } v
                ? new JsonObject { ["stamp"] = stamp, ["tete"] = Math.Round(v.Head, 2), ["corps"] = Math.Round(v.Body, 2) }
                : new JsonObject { ["stamp"] = stamp };
            try { File.WriteAllText(cachePath, cache.ToJsonString()); } catch (IOException) { }
            return g;
        }
    }

    /// <summary>Fiche du Builder : { possible, ecart, tete, corps, choix } pour le skin choisi.</summary>
    public static JsonObject Info(Context ctx, PresetRow row, Skins skins)
    {
        var o = new JsonObject { ["ecart"] = false, ["choix"] = Chosen(Options.For(ctx, row.Id)) };
        if (skins.Get(Skins.LoadChoices(ctx).GetValueOrDefault(row.Id)) is not { } s) return o;
        o["skin"] = s.Id;
        if (Measure(ctx, row, skins, s) is not { } g) return o;
        o["ecart"] = g.Uneven;
        o["tete"] = Math.Round(g.Head);
        o["corps"] = Math.Round(g.Body);
        return o;
    }

    // ------------------------------------------------------------------ correction

    /// <summary>Normal map de la tête avec le fondu (work\skins_cache\&lt;id&gt;\head_n_fondu.dds, refaite si sa source change), ou null
    /// avec la raison dans le rapport.</summary>
    public static string? FadedHeadNormal(Context ctx, PresetRow row, Skins skins, Skins.Skin s, System.Text.StringBuilder report)
    {
        if (Measure(ctx, row, skins, s) is not { } g)
        {
            report.AppendLine(L.T("SKIN    {0}  brillance au cou : pas mesurable pour {1}, visage non modifié", row.Id, s.Name));
            return null;
        }
        if (!g.Uneven)
        {
            report.AppendLine(L.T("SKIN    {0}  brillance au cou : tête et corps déjà pareils dans {1}, rien à faire", row.Id, s.Name));
            return null;
        }
        var src = skins.SourceFile(s, "head_n");
        if (src == null || Meshes(ctx, row) is not { } m) return null;
        var (w, h) = Skins.DdsSize(src);
        if (w > MaxSize || h > MaxSize || w != h)
        {
            report.AppendLine(L.T("ATTENTION {0}  brillance au cou : normal map de la tête en {1}x{2}, trop grande pour être refaite ; visage non modifié", row.Id, w, h));
            return null;
        }
        var dst = Path.Combine(Context.Dir(Path.Combine(ctx.WorkDir, "skins_cache", s.Id)), $"head_n_fondu_{row.Sexe}.dds");
        var mark = dst + ".txt";
        var stamp = FormattableString.Invariant($"v1|{File.GetLastWriteTimeUtc(src).Ticks}|{new FileInfo(src).Length}|{g.Head:F2}|{g.Body:F2}|{m.Head.Verts.Count}");
        if (!File.Exists(dst) || !File.Exists(mark) || File.ReadAllText(mark) != stamp)
        {
            var img = Dds.Decode(src);
            var mask = Mask(m.Head, 1024);
            double k = g.Body / g.Head;
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                {
                    double t = Bilinear(mask, 1024, (x + 0.5) / img.Width, (y + 0.5) / img.Height);
                    if (t <= 0) continue;
                    int at = (y * img.Width + x) * 4 + 3;
                    img.Rgba[at] = (byte)Math.Clamp(img.Rgba[at] * (1 + (k - 1) * t) + 0.5, 0, 255);
                }
            Dds.WriteRgbaMips(dst, img);
            File.WriteAllText(mark, stamp);
        }
        report.AppendLine(L.T("SKIN    {0}  brillance au cou : tête {1}, corps {2} (sur 255) ; fondu sur le cou de la tête, {3}x{3} non compressé", row.Id, Math.Round(g.Head), Math.Round(g.Body), w));
        return dst;
    }

    /// <summary>Poids du fondu dans l'espace UV de la tête : 1 au raccord, 0 sur le visage ; étendu autour des îlots d'UV (le filtrage
    /// des textures lirait sinon la valeur non corrigée au bord).</summary>
    static float[] Mask(Mesh m, int size)
    {
        var ring = m.Ring.Select(i => m.Verts[i]).ToArray();
        var weight = new double[m.Verts.Count];
        for (int i = 0; i < weight.Length; i++)
        {
            double best = double.MaxValue;
            foreach (var r in ring) best = Math.Min(best, System.Numerics.Vector3.DistanceSquared(m.Verts[i], r));
            double t = Math.Clamp((Math.Sqrt(best) - FadeStart) / (FadeEnd - FadeStart), 0, 1);
            weight[i] = 1 - t * t * (3 - 2 * t);
        }
        var map = new float[size * size];
        var covered = new bool[size * size];
        foreach (var (a, b, c) in m.Tris)
        {
            (double U, double V) pa = m.Uv[a], pb = m.Uv[b], pc = m.Uv[c];
            // UV ramenées dans la texture par un même décalage pour les trois sommets
            double ou = Math.Floor(Math.Min(pa.U, Math.Min(pb.U, pc.U))), ov = Math.Floor(Math.Min(pa.V, Math.Min(pb.V, pc.V)));
            double ax = (pa.U - ou) * size, ay = (pa.V - ov) * size, bx = (pb.U - ou) * size, by = (pb.V - ov) * size, cx = (pc.U - ou) * size, cy = (pc.V - ov) * size;
            double area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            if (Math.Abs(area) < 1e-9) continue;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double wb = ((px - ax) * (cy - ay) - (cx - ax) * (py - ay)) / area, wc = ((bx - ax) * (py - ay) - (px - ax) * (by - ay)) / area, wa = 1 - wb - wc;
                    if (wa < -0.001 || wb < -0.001 || wc < -0.001) continue;
                    map[y * size + x] = (float)Math.Clamp(wa * weight[a] + wb * weight[b] + wc * weight[c], 0, 1);
                    covered[y * size + x] = true;
                }
        }
        for (int pass = 0; pass < 16; pass++)
        {
            var add = new List<(int At, float V)>();
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int at = y * size + x;
                    if (covered[at]) continue;
                    float sum = 0; int n = 0;
                    if (x > 0 && covered[at - 1]) { sum += map[at - 1]; n++; }
                    if (x < size - 1 && covered[at + 1]) { sum += map[at + 1]; n++; }
                    if (y > 0 && covered[at - size]) { sum += map[at - size]; n++; }
                    if (y < size - 1 && covered[at + size]) { sum += map[at + size]; n++; }
                    if (n > 0) add.Add((at, sum / n));
                }
            if (add.Count == 0) break;
            foreach (var (at, v) in add) { map[at] = v; covered[at] = true; }
        }
        return map;
    }

    static double Bilinear(float[] map, int size, double u, double v)
    {
        double x = u * size - 0.5, y = v * size - 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double At(int px, int py) => map[Math.Clamp(py, 0, size - 1) * size + Math.Clamp(px, 0, size - 1)];
        return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy) + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
    }
}
