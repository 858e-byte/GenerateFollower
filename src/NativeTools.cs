using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenerateFollower;

/// <summary>
/// Portage C# des scripts Python de offline\ en lecture seule, et de copy_assets.py (plan autonome .NET, étapes 2 et 3).
/// Chaque méthode rend la même sortie que le script (même JSON, même texte), pour que les appelants ne changent pas et
/// que la comparaison avec les fichiers témoins (work\diag\autonome_dotnet\golden) soit directe.
/// </summary>
public static class NativeTools
{
    // ------------------------------------------------------------------ find_files.py

    /// <summary>Existence de fichiers relatifs à Data (loose ou BSA) : {"textures\…": true|false}.</summary>
    public static JsonObject FindFiles(Context ctx, IEnumerable<string> rels)
    {
        var o = new JsonObject();
        foreach (var rel in rels) o[rel] = ctx.FindFile(rel) != null;
        return o;
    }

    // ------------------------------------------------------------------ head_shader.py

    /// <summary>Shader de la tête d'un FaceGen (forme dont le slot FacegenDetail est rempli) ; {} sans tête.</summary>
    public static JsonObject HeadShader(string faceGen)
    {
        foreach (var s in Nif.Load(faceGen).Shapes)
            if (s.Texture("FacegenDetail").Length > 0)
                return new JsonObject
                {
                    ["diffuse"] = s.Texture("Diffuse"), ["normal"] = s.Texture("Normal"), ["specular"] = s.Texture("Specular"),
                    ["subsurface"] = s.Texture("SoftLighting"), ["msn"] = s.ModelSpaceNormals,
                };
        return new JsonObject();
    }

    // ------------------------------------------------------------------ nif_shapes.py

    /// <summary>Formes de meshes du jeu et leurs textures : {"meshes\…": [{name, index, diffuse, normal, subsurface,
    /// specular}] | null si introuvable}.</summary>
    public static JsonObject NifShapes(Context ctx, IEnumerable<string> rels)
    {
        var o = new JsonObject();
        foreach (var rel in rels)
        {
            if (ctx.FindMesh(rel) is not { } real) { o[rel] = null; continue; }
            var shapes = new JsonArray();
            int i = 0;
            foreach (var s in Nif.Load(real).Shapes)
                shapes.Add(new JsonObject
                {
                    ["name"] = s.Name, ["index"] = i++, ["diffuse"] = s.Texture("Diffuse"), ["normal"] = s.Texture("Normal"),
                    ["subsurface"] = s.Texture("SoftLighting"), ["specular"] = s.Texture("Specular"),
                });
            o[rel] = shapes;
        }
        return o;
    }

    // ------------------------------------------------------------------ export_textures.py

    /// <summary>Textures d'un export RaceMenu introuvables (ni loose ni BSA), après texture_remap.json ; slot de teinte
    /// (CharGen\…) ignoré : {"introuvables": [{forme, texture}]}.</summary>
    public static JsonObject ExportTextures(Context ctx, string export)
    {
        var remapPath = Path.Combine(ctx.WorkDir, "texture_remap.json");
        var remap = new Dictionary<string, string>();
        if (File.Exists(remapPath))
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(remapPath))!)
                remap[k.ToLowerInvariant()] = v;
        var missing = new JsonArray();
        foreach (var s in Nif.Load(export).Shapes)
            foreach (var tex in s.Textures.Values)
            {
                if (tex.Length == 0 || tex.Contains(@"plugins\chargen", StringComparison.OrdinalIgnoreCase)) continue;
                // le préfixe « textures\ » vient de la lecture (comme PyNifly) ; texture_remap.json garde les chemins bruts du .nif
                var raw = tex.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? tex[9..] : tex;
                var t = remap.TryGetValue(raw.ToLowerInvariant(), out var r1) ? r1 : remap.TryGetValue(tex.ToLowerInvariant(), out var r2) ? r2 : tex;
                var rel = t.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? t : @"textures\" + t;
                if (ctx.FindFile(rel) == null) missing.Add(new JsonObject { ["forme"] = s.Name, ["texture"] = t });
            }
        return new JsonObject { ["introuvables"] = missing };
    }

    // ------------------------------------------------------------------ export_mesh.py

    /// <summary>Géométrie d'un .nif en JSON pour l'aperçu 3D : {"shapes": [{name, block, shader, hairTint, alpha,
    /// alphaBlend, blendSrc, blendDst, alphaTest, diffuse, tint, verts, tris, uvs}]}, sommets dans le repère du fichier.</summary>
    public static JsonObject ExportMesh(string nifPath)
    {
        var nif = Nif.Load(nifPath);
        var shapes = new JsonArray();
        foreach (var s in nif.Shapes)
        {
            var v = s.Verts;
            var tris = s.Tris;
            if (v.Count == 0 || tris.Count == 0) continue;
            var alpha = s.Alpha;
            int flags = s.AlphaFlags;
            var world = World(nif, s, v);
            var verts = new JsonArray();
            foreach (var p in world) { verts.Add(Round(p.X, 4)); verts.Add(Round(p.Y, 4)); verts.Add(Round(p.Z, 4)); }
            var tri = new JsonArray();
            foreach (var t in tris) { tri.Add((int)t.V1); tri.Add((int)t.V2); tri.Add((int)t.V3); }
            var uvs = new JsonArray();
            foreach (var uv in s.UVs) { uvs.Add(Round(uv.U, 5)); uvs.Add(Round(uv.V, 5)); }
            shapes.Add(new JsonObject
            {
                ["name"] = s.Name,
                ["block"] = s.Block,
                ["shader"] = s.ShaderType,
                // shader « Hair Tint » (6) : le jeu teinte la forme avec la couleur de cheveux du PNJ (blanc dans le FaceGen)
                ["hairTint"] = s.ShaderType == 6,
                ["alpha"] = alpha != null,
                ["alphaBlend"] = alpha != null && (flags & 1) != 0,
                // facteurs de mélange (bits 1-4 source, 5-8 destination) : 0 ONE, 1 ZERO, 6 SRC_ALPHA, 7 INV_SRC_ALPHA…
                ["blendSrc"] = alpha != null ? (flags >> 1) & 0xF : 6,
                ["blendDst"] = alpha != null ? (flags >> 5) & 0xF : 7,
                ["alphaTest"] = alpha != null && (flags & 0x200) != 0 ? alpha.Threshold / 255.0 : 0,
                ["diffuse"] = s.Texture("Diffuse"),
                // teinte FaceGen (slot FacegenDetail) : couleur de peau du visage, appliquée par le shader FaceGen
                ["tint"] = s.Texture("FacegenDetail"),
                ["verts"] = verts,
                ["tris"] = tri,
                ["uvs"] = uvs,
            });
        }
        return new JsonObject { ["shapes"] = shapes };
    }

    /// <summary>Position dans le repère du fichier. Forme skinnée : par l'os de la tête (comme le squelette en jeu) ;
    /// certaines pièces n'ont pas de transformation propre et resteraient à l'origine (yeux de thorRg).</summary>
    static List<(double X, double Y, double Z)> World(Nif nif, NifShape s, List<System.Numerics.Vector3> v)
    {
        Xform? xf = null;
        try
        {
            var bones = s.BoneNames;
            var b = bones.Contains("NPC Head [Head]") ? "NPC Head [Head]" : bones.Count > 0 ? bones[0] : null;
            if (b != null && nif.Node(b) is { } node && s.SkinToBone(b) is { } stb)
            {
                var g = nif.GlobalTransform(node);
                return v.Select(p => { var (x, y, z) = stb.Apply(p.X, p.Y, p.Z); return g.Apply(x, y, z); }).ToList();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        try { xf = s.Transform; } catch (Exception e) when (e is not OutOfMemoryException) { }
        return xf is { } t ? v.Select(p => t.Apply(p.X, p.Y, p.Z)).ToList() : v.Select(p => ((double)p.X, (double)p.Y, (double)p.Z)).ToList();
    }

    /// <summary>Arrondi de numpy (np.round) : x × 10^n arrondi au pair, puis divisé.</summary>
    static double Round(double x, int n)
    {
        var f = Math.Pow(10, n);
        return Math.Round(x * f, MidpointRounding.ToEven) / f;
    }

    // ------------------------------------------------------------------ compare_heads.py

    /// <summary>Écart entre la forme de visage (shader FaceGen) de deux .nif, en unités de jeu.</summary>
    public static JsonObject CompareHeads(string head, string reference)
    {
        static (string Name, List<System.Numerics.Vector3> V) Face(string path)
        {
            // tête générée par HeadGen (JSON) : sommets du visage dans le repère de la forme (« local »)
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var face = JsonNode.Parse(File.ReadAllText(path))!["shapes"]!.AsArray().Where(s => s!["local"] != null)
                    .MaxBy(s => s!["local"]!.AsArray().Count) ?? throw new GfException($"compare_heads : aucune forme de visage dans {path}");
                var l = face!["local"]!.AsArray().Select(x => (float)x!.GetValue<double>()).ToArray();
                return (face["name"]!.GetValue<string>(),Enumerable.Range(0, l.Length / 3).Select(i => new System.Numerics.Vector3(l[i * 3], l[i * 3 + 1], l[i * 3 + 2])).ToList());
            }
            var faces = Nif.Load(path).Shapes.Where(s => s.ShaderType == 4).ToList();
            if (faces.Count == 0) throw new GfException($"compare_heads : aucune forme de visage dans {path}");
            var best = faces[0];
            foreach (var f in faces.Skip(1)) if (f.Verts.Count > best.Verts.Count) best = f;
            return (best.Name, best.Verts);
        }
        var (an, a) = Face(head);
        var (bn, b) = Face(reference);
        var res = new JsonObject { ["tete"] = an, ["reference"] = bn, ["sommets"] = new JsonArray(a.Count, b.Count) };
        if (a.Count == b.Count)
        {
            var d = a.Zip(b, (p, q) => Math.Sqrt(Sq((double)p.X - q.X) + Sq((double)p.Y - q.Y) + Sq((double)p.Z - q.Z))).ToArray();
            res["moyen"] = Math.Round(d.Average(), 3);
            res["p95"] = Math.Round(Percentile(d, 95), 3);
            res["max"] = Math.Round(d.Max(), 3);
        }
        else res["erreur"] = "maillages différents (nombre de sommets), comparaison impossible";
        return res;
    }

    static double Sq(double x) => x * x;

    /// <summary>np.percentile (interpolation linéaire).</summary>
    static double Percentile(double[] values, double p)
    {
        var s = values.Order().ToArray();
        var pos = (s.Length - 1) * p / 100;
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    // ------------------------------------------------------------------ index_heads.py

    /// <summary>Index des têtes exportées par RaceMenu dans CharGen (formes et sommets, teinte uniforme ou non),
    /// mis à jour de façon incrémentale. Le premier dossier gagne pour un même chemin relatif.</summary>
    public static string IndexHeads(IReadOnlyList<string> roots, string outPath)
    {
        var index = File.Exists(outPath) ? JsonNode.Parse(File.ReadAllText(outPath))!.AsObject() : new JsonObject();
        var files = new Dictionary<string, (string Rel, string Path)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var p in PyGlobRecursive(root, ".nif"))
            {
                var rel = Path.GetRelativePath(root, p);
                if (files.TryAdd(rel, (rel, p))) order.Add(rel);
            }
        }
        var seen = new HashSet<string>();
        foreach (var key in order)
        {
            var (rel, p) = files[key];
            seen.Add(rel);
            var mtime = PyMtime(p);
            if (index[rel] is JsonObject old && old["mtime"]?.GetValue<double>() == mtime) continue;
            var entry = new JsonObject { ["mtime"] = mtime, ["shapes"] = new JsonObject(), ["dds"] = false, ["uniforme"] = null };
            try
            {
                var shapes = entry["shapes"]!.AsObject();
                foreach (var s in Nif.Load(p).Shapes) shapes[s.Name] = s.VertexCount;
            }
            catch (Exception e) when (e is not OutOfMemoryException) { entry["erreur"] = e.Message; }
            var dds = Path.ChangeExtension(p, ".dds");
            if (File.Exists(dds) && new FileInfo(dds).Length > 1024)
            {
                entry["dds"] = true;
                try { entry["uniforme"] = Dds.IsUniform(dds); }
                catch (Exception e) when (e is not OutOfMemoryException) { entry["uniforme"] = null; }
            }
            index[rel] = entry;
        }
        foreach (var rel in index.Select(kv => kv.Key).ToList())
            if (!seen.Contains(rel)) index.Remove(rel);
        File.WriteAllText(outPath, index.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return $"{index.Count} têtes indexées";
    }

    /// <summary>glob(root\**\*.ext, recursive=True) : fichiers du dossier puis des sous-dossiers, dans l'ordre du
    /// système (comme os.scandir).</summary>
    static IEnumerable<string> PyGlobRecursive(string root, string ext)
    {
        foreach (var f in Directory.EnumerateFiles(root))
            if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) yield return f;
        foreach (var d in Directory.EnumerateDirectories(root))
            foreach (var f in PyGlobRecursive(d, ext)) yield return f;
    }

    /// <summary>os.path.getmtime de Python sous Windows : secondes + nanosecondes × 1e-9, en double.</summary>
    public static double PyMtime(string path)
    {
        long ticks = (File.GetLastWriteTimeUtc(path) - DateTime.UnixEpoch).Ticks;
        long sec = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long rem);
        if (rem < 0) { sec--; rem += TimeSpan.TicksPerSecond; }
        return sec + rem * 100 * 1e-9;
    }

    static double PyDirMtime(string path)
    {
        long ticks = (Directory.GetLastWriteTimeUtc(path) - DateTime.UnixEpoch).Ticks;
        long sec = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long rem);
        if (rem < 0) { sec--; rem += TimeSpan.TicksPerSecond; }
        return sec + rem * 100 * 1e-9;
    }

    // ------------------------------------------------------------------ mods_index.py

    const int ModsIndexVersion = 2;

    /// <summary>Index des mods du gestionnaire (activés ou non) : plugins, textures, numéro Nexus, archive installée.
    /// Incrémental (un mod dont le dossier n'a pas changé garde son entrée). Rend le résumé JSON du script.</summary>
    public static string ModsIndex(string root, string outPath)
    {
        JsonObject old = new();
        if (File.Exists(outPath))
            try { old = JsonNode.Parse(File.ReadAllText(outPath))?["mods"]?.AsObject() ?? new JsonObject(); }
            catch (JsonException) { old = new JsonObject(); }
        var mods = new JsonObject();
        int scanned = 0;
        foreach (var name in Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(root, name!);
            if (!Directory.Exists(path) || name!.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            var mtime = PyDirMtime(path);
            if (old[name] is JsonObject prev && prev["v"]?.GetValue<int>() == ModsIndexVersion && Math.Abs((prev["mtime"]?.GetValue<double>() ?? 0) - mtime) < 1)
            {
                mods[name] = prev.DeepClone();
                continue;
            }
            scanned++;
            var plugins = new JsonArray(Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName)
                .Where(f => f!.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                .Select(f => (JsonNode)f!).ToArray());
            var dds = new JsonArray();
            foreach (var f in Walk(path))
                if (f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(path, f);
                    if (rel.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase)) dds.Add(rel);
                }
            var (nexus, archive) = ModMeta(path, name);
            mods[name] = new JsonObject
            {
                ["v"] = ModsIndexVersion, ["mtime"] = mtime, ["plugins"] = plugins, ["dds"] = dds, ["nexus"] = nexus, ["archive"] = archive,
            };
        }
        File.WriteAllText(outPath, new JsonObject { ["mods"] = mods }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return new JsonObject { ["mods"] = mods.Count, ["relus"] = scanned }.ToJsonString();
    }

    /// <summary>os.walk : fichiers d'un dossier, puis ses sous-dossiers, dans l'ordre du système.</summary>
    static IEnumerable<string> Walk(string dir)
    {
        IEnumerable<string> files, dirs;
        try { files = Directory.EnumerateFiles(dir).ToList(); dirs = Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
            foreach (var f in Walk(d)) yield return f;
    }

    /// <summary>(numéro Nexus, archive) : meta.ini de MO2 (section [General], modid et installationFile), sinon le
    /// numéro dans le nom du dossier Vortex (« Nom-95818-2-1-… »).</summary>
    static (string? Nexus, string? Archive) ModMeta(string path, string name)
    {
        string? nexus = null, archive = null;
        var ini = Path.Combine(path, "meta.ini");
        if (File.Exists(ini))
            try
            {
                var general = IniSection(File.ReadAllLines(ini, System.Text.Encoding.UTF8), "General");
                var mid = (general.GetValueOrDefault("modid") ?? "").Trim();
                nexus = mid.Length > 0 && mid.All(char.IsAsciiDigit) && long.Parse(mid) > 0 ? mid : null;
                var inst = (general.GetValueOrDefault("installationfile") ?? "").Trim().Trim('"');
                var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(inst.Replace('/', '\\')));
                archive = stem.Length > 0 ? stem : null;
            }
            catch (Exception e) when (e is IOException or FormatException or OverflowException) { }
        if (nexus == null && Regex.Match(name, @"-(\d{3,})-") is { Success: true } m) nexus = m.Groups[1].Value;
        return (nexus, archive);
    }

    /// <summary>Clés (minuscules) d'une section d'un .ini, comme configparser (délimiteurs « = » et « : »).</summary>
    static Dictionary<string, string> IniSection(string[] lines, string section)
    {
        var d = new Dictionary<string, string>();
        bool inside = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { inside = line[1..^1] == section; continue; }
            if (!inside) continue;
            int i = line.IndexOfAny(['=', ':']);
            if (i > 0) d[line[..i].Trim().ToLowerInvariant()] = line[(i + 1)..].Trim();
        }
        return d;
    }

    // ------------------------------------------------------------------ dds_png.py

    /// <summary>Texture du jeu (relative à Data, loose ou BSA, ou chemin absolu) en PNG size × size pour l'aperçu 3D.</summary>
    public static void DdsPng(Context ctx, string rel, string outFile, int size)
    {
        var path = Path.IsPathRooted(rel) && File.Exists(rel) ? rel
            : ctx.FindFile(rel.StartsWith("textures", StringComparison.OrdinalIgnoreCase) ? rel : @"textures\" + rel);
        if (path == null) throw new GfException($"aperçu de texture :\nintrouvable : {rel}");
        var img = Dds.Decode(path);
        if (img.Width != size || img.Height != size) img = Dds.Resize(img, size, size);
        Dds.WritePng(outFile, img);
    }

    // ------------------------------------------------------------------ bake_face.py

    /// <summary>
    /// Overlays RaceMenu du visage cuits dans une diffuse propre au follower (têtes non UBE). Tâche comme pour le script :
    /// {"base", "out", "layers": [{tex, color, alpha}], "red"?}. Chaque couche : couleur = texture × teinte, opacité = alpha
    /// de la texture × alpha, mélangée par-dessus la diffuse ; alpha de la diffuse conservé. Sortie : DDS BC7 avec
    /// mipmaps, à la taille de la diffuse (4096 au plus). Rend le résumé JSON du script.
    /// </summary>
    public static JsonObject BakeFace(Context ctx, JsonObject job)
    {
        var (img, summary) = BakePixels(ctx, job);
        var outPath = job["out"]!.GetValue<string>();
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        Dds.WriteBc7(outPath, img, mipmaps: true);
        return summary;
    }

    /// <summary>Pixels de la diffuse cuite, avant compression (comparables au bit près au PNG du script).</summary>
    public static (Dds.Image Image, JsonObject Summary) BakePixels(Context ctx, JsonObject job)
    {
        var b = job["base"]!.GetValue<string>();
        var basePath = Path.IsPathRooted(b) && File.Exists(b) ? b : ctx.FindFile(b);
        if (basePath == null) throw new GfException($"cuisson des overlays :\ndiffuse introuvable : {b}");
        int width;
        using (var fs = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var h = new byte[20];
            fs.ReadExactly(h);
            width = BitConverter.ToInt32(h, 16);
        }
        int size = Math.Min(width == 0 ? 1024 : width, 4096);
        var baseImg = Dds.Load(basePath, size).Rgba;
        int n = size * size;
        var rgb = new double[n * 3];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < 3; k++) rgb[i * 3 + k] = baseImg[i * 4 + k] / 255.0;
        var used = new JsonArray();
        var missing = new JsonArray();
        foreach (var layer in job["layers"]!.AsArray())
        {
            var tex = layer!["tex"]!.GetValue<string>();
            if (ctx.FindFile(tex) is not { } p) { missing.Add(tex); continue; }
            var t = Dds.Load(p, size).Rgba;
            double cr = 1, cg = 1, cb = 1;
            if (layer["color"] is { } cn)
            {
                long c = (long)Num(cn);
                cr = ((c >> 16) & 255) / 255.0; cg = ((c >> 8) & 255) / 255.0; cb = (c & 255) / 255.0;
            }
            double alpha = layer["alpha"] is { } an ? Num(an) : 1.0;
            for (int i = 0; i < n; i++)
            {
                double w = t[i * 4 + 3] / 255.0 * alpha;
                rgb[i * 3] = rgb[i * 3] * (1 - w) + t[i * 4] / 255.0 * cr * w;
                rgb[i * 3 + 1] = rgb[i * 3 + 1] * (1 - w) + t[i * 4 + 1] / 255.0 * cg * w;
                rgb[i * 3 + 2] = rgb[i * 3 + 2] * (1 - w) + t[i * 4 + 2] / 255.0 * cb * w;
            }
            used.Add(Path.GetFileName(p));
        }
        bool red = job["red"]?.GetValue<bool>() == true;
        var px = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < 3; k++)
                px[i * 4 + k] = red ? (byte)(k == 0 ? 255 : 0) : (byte)(Math.Clamp(rgb[i * 3 + k], 0, 1) * 255 + 0.5);
            px[i * 4 + 3] = (byte)(baseImg[i * 4 + 3] / 255.0 * 255 + 0.5);
        }
        return (new Dds.Image(size, size, px), new JsonObject { ["taille"] = size, ["couches"] = used, ["introuvables"] = missing });
    }

    /// <summary>Nombre d'un nœud JSON lu ou construit (int, float, double) : relu depuis son texte JSON, comme le
    /// script le recevait (un float 0.078431375 donne le même double que Python).</summary>
    public static double Num(JsonNode n) => double.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ copy_assets.py

    /// <summary>
    /// Copie dans le mod les fichiers des headparts recréés (followers autonomes) ; pour chaque .nif copié, ajoute les
    /// textures de ses formes. Sous-dossier : meshes\NOM\… et textures\NOM\… au lieu du chemin d'origine. Manifeste :
    /// liste des fichiers copiés (chemins d'origine relatifs à Data). Rend le même rapport texte que le script.
    /// </summary>
    public static string CopyAssets(Context ctx, IEnumerable<string> files, string outDir, string? sub, string? manifest)
    {
        if (sub != null && (sub.Length == 0 || sub.IndexOfAny(['\\', '/', ':', '.']) >= 0)) throw new GfException("sous-dossier refusé : " + sub);
        static string Norm(string rel, string kind)
        {
            rel = rel.Replace('/', '\\').TrimStart('\\');
            return rel.StartsWith(kind + "\\", StringComparison.OrdinalIgnoreCase) ? rel : kind + "\\" + rel;
        }
        string Dest(string rel)
        {
            if (string.IsNullOrEmpty(sub)) return Path.Combine(outDir, rel);
            int i = rel.IndexOf('\\');
            return Path.Combine(outDir, rel[..i], sub, rel[(i + 1)..]);
        }
        var todo = files.Select(f => f.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) || f.StartsWith("textures", StringComparison.OrdinalIgnoreCase) ? f
            : Norm(f, f.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tri", StringComparison.OrdinalIgnoreCase) ? "meshes" : "textures")).ToList();
        var seen = new HashSet<string>();
        var missing = new List<string>();
        var done = new List<string>();
        int copied = 0;
        long size = 0;
        while (todo.Count > 0)
        {
            var rel = todo[^1];
            todo.RemoveAt(todo.Count - 1);
            if (!seen.Add(rel.ToLowerInvariant())) continue;
            if (ctx.FindFile(rel) is not { } src) { missing.Add(rel); continue; }
            var dst = Dest(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, true);
            copied++;
            size += new FileInfo(dst).Length;
            done.Add(rel);
            if (rel.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                try
                {
                    foreach (var s in Nif.Load(src).Shapes)
                        foreach (var t in s.Textures.Values)
                            if (t.Length > 0) todo.Add(Norm(t, "textures"));
                }
                catch (Exception e) when (e is not OutOfMemoryException) { missing.Add($"{rel} (lecture impossible : {e.Message})"); }
        }
        if (manifest != null)
            File.WriteAllText(manifest, new JsonArray(done.OrderBy(d => d.ToLowerInvariant(), StringComparer.Ordinal).Select(d => (JsonNode)d).ToArray())
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var sb = new System.Text.StringBuilder();
        sb.Append($"{copied} fichiers copiés ({size / 1e6:F0} Mo), {missing.Count} introuvables\n");
        foreach (var m in missing) sb.Append("  introuvable : ").Append(m).Append('\n');
        return sb.ToString();
    }
}
