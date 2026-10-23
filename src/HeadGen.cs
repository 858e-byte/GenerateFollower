using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Tête générée depuis le jslot, pour l'aperçu 3D seulement (plan autonome .NET, étape 5 ; remplace offline\headgen.py,
/// tri.py et la partie géométrie d'assemble.py). Aucun .nif n'est écrit : la géométrie part directement en JSON, au format
/// d'export_mesh. Une tête générée n'est jamais livrée en jeu (le jeu la rejette) : le FaceGen livré reste l'export
/// RaceMenu modifié en binaire.
/// </summary>
public sealed class HeadGen
{
    /// <summary>Les 18 curseurs vanilla de morphs.default : (morph si valeur &lt; 0, morph si valeur &gt; 0). Slots 2 et 16
    /// inversés (vérifié sur les exports, voir headgen.py).</summary>
    static readonly (string Lo, string Hi)[] Pairs =
    [
        ("NoseShort", "NoseLong"), ("NoseDown", "NoseUp"), ("JawUp", "JawDown"), ("JawNarrow", "JawWide"),
        ("JawBack", "JawForward"), ("CheeksDown", "CheeksUp"), ("CheeksIn", "CheeksOut"), ("EyesMoveDown", "EyesMoveUp"),
        ("EyesMoveIn", "EyesMoveOut"), ("BrowDown", "BrowUp"), ("BrowIn", "BrowOut"), ("BrowBack", "BrowForward"),
        ("LipMoveDown", "LipMoveUp"), ("LipMoveIn", "LipMoveOut"), ("ChinThin", "ChinWide"), ("ChinMoveUp", "ChinMoveDown"),
        ("Overbite", "Underbite"), ("EyesBack", "EyesForward"),
    ];
    const string MorphsDir = @"meshes\actors\character\facegenmorphs";
    const string Head = "NPC Head [Head]";
    // TextureSet (TX00..TX07) -> slot du BSShaderTextureSet
    static readonly int[] TxstToSlot = [0, 1, 5, 2, 3, 4, 6, 7];

    readonly Context ctx;
    readonly Dictionary<string, Tri> tris = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, List<string>>? morphReg;
    readonly Dictionary<string, Dictionary<string, (string Lo, string Hi)>> sliders = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string>? vanilla, mergeIntoHead;

    public HeadGen(Context ctx) => this.ctx = ctx;

    // ------------------------------------------------------------------ fichiers .tri (FRTRI003)

    /// <summary>Morphs différentiels d'un .tri : deltas int16 × échelle, en double ; NVerts = -1 si illisible (format
    /// PIRT de certains cheveux, fichier tronqué) : aucun morph.</summary>
    public sealed class Tri
    {
        public int NVerts = -1;
        public Dictionary<string, double[]> Morphs = new();

        public static Tri Read(string path)
        {
            var t = new Tri();
            try
            {
                var d = File.ReadAllBytes(path);
                if (d.Length < 64 || Encoding.ASCII.GetString(d, 0, 8) != "FRTRI003") return t;
                int I(int at) => BitConverter.ToInt32(d, at);
                int nverts = I(8), ntris = I(12), nquads = I(16), nlv = I(20), nls = I(24), nuv = I(28), ext = I(32), nmorphs = I(36), nmodverts = I(44);
                long p = 64 + (long)nverts * 12 + (long)nmodverts * 12 + (long)ntris * 12 + (long)nquads * 16 + (long)nlv * 4 + (long)nls * 16;
                if ((ext & 1) != 0) p += (long)nuv * 8 + (long)ntris * 12 + (long)nquads * 16;
                var morphs = new Dictionary<string, double[]>();
                for (int m = 0; m < nmorphs; m++)
                {
                    int n = BitConverter.ToInt32(d, (int)p); p += 4;
                    var name = Encoding.Latin1.GetString(d, (int)p, n).TrimEnd('\0'); p += n;
                    double scale = BitConverter.ToSingle(d, (int)p); p += 4;
                    if (p + (long)nverts * 6 > d.Length) return t;
                    var delta = new double[nverts * 3];
                    for (int i = 0; i < nverts * 3; i++) delta[i] = BitConverter.ToInt16(d, (int)p + i * 2) * scale;
                    p += (long)nverts * 6;
                    morphs[name] = delta;
                }
                t.NVerts = nverts;
                t.Morphs = morphs;
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or IOException) { t.NVerts = -1; t.Morphs = new(); }
            return t;
        }
    }

    Tri TriOf(string path)
    {
        if (!tris.TryGetValue(path, out var t)) tris[path] = t = Tri.Read(path);
        return t;
    }

    static Dictionary<string, double[]> Lower(Dictionary<string, double[]> m)
    {
        var d = new Dictionary<string, double[]>();
        foreach (var (k, v) in m) d[k.ToLowerInvariant()] = v;   // comme {k.lower(): v} : la dernière gagne
        return d;
    }

    // ------------------------------------------------------------------ registres RaceMenu (assets.py)

    /// <summary>Racines où chercher les .ini de facegenmorphs : Data virtuel, puis les fichiers extraits des BSA (après
    /// extraction de tous les .ini de facegenmorphs).</summary>
    List<string> Roots()
    {
        foreach (var rel in ctx.Bsa.Matching(MorphsDir + "\\", ".ini")) ctx.Bsa.Find(rel);
        return [.. ctx.Files.Dirs(""), ctx.Bsa.FilesDir];
    }

    /// <summary>morph_registry : tri source (minuscules, relatif à Meshes) -> tri additionnels (chemins réels).</summary>
    Dictionary<string, List<string>> MorphRegistry()
    {
        if (morphReg != null) return morphReg;
        var reg = new Dictionary<string, List<string>>();
        foreach (var root in Roots())
        {
            var dir = Path.Combine(root, MorphsDir);
            if (!Directory.Exists(dir)) continue;
            foreach (var mod in Directory.EnumerateFileSystemEntries(dir))
            {
                var ini = Path.Combine(mod, "morphs.ini");
                if (!File.Exists(ini)) continue;
                foreach (var raw in File.ReadAllLines(ini, Encoding.Latin1))
                {
                    var line = raw.Split('#')[0].Trim();
                    if (!line.StartsWith("extension", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var parts = line[(eq + 1)..].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    if (parts.Count == 0) continue;
                    var src = parts[0].ToLowerInvariant().Replace('/', '\\');
                    foreach (var e in parts.Skip(1))
                    {
                        var p = ctx.FindFile(Path.Combine(MorphsDir, "morphs", e));
                        if (p == null) continue;
                        if (!reg.TryGetValue(src, out var list)) reg[src] = list = [];
                        if (!list.Contains(p)) list.Add(p);
                    }
                }
            }
        }
        return morphReg = reg;
    }

    /// <summary>slider_registry : curseur RaceMenu (minuscules) -> (morph valeur négative, morph valeur positive), pour une
    /// race (races.ini de chaque dossier) ; la définition la plus loin gagne.</summary>
    Dictionary<string, (string Lo, string Hi)> Sliders(string race)
    {
        if (sliders.TryGetValue(race, out var known)) return known;
        var folders = new Dictionary<string, string>();
        foreach (var root in Roots())
        {
            var dir = Path.Combine(root, MorphsDir);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFileSystemEntries(dir))
                folders.TryAdd(Path.GetFileName(f).ToLowerInvariant(), f);
        }
        var inis = new List<string>();
        foreach (var name in folders.Keys.Order(StringComparer.Ordinal))
        {
            var f = folders[name];
            var races = Path.Combine(f, "races.ini");
            if (race.Length > 0 && File.Exists(races))
            {
                foreach (var raw in File.ReadAllLines(races, Encoding.Latin1))
                {
                    var line = raw.Split('#')[0].Trim();
                    int eq = line.IndexOf('=');
                    if (eq >= 0 && line[..eq].Trim().Equals(race, StringComparison.OrdinalIgnoreCase))
                    {
                        var p = Path.Combine(f, line[(eq + 1)..].Trim());
                        if (File.Exists(p)) inis.Add(p);
                    }
                }
            }
            else if (Directory.Exists(Path.Combine(f, "sliders")))
                inis.AddRange(Directory.EnumerateFiles(Path.Combine(f, "sliders"), "*.ini"));
        }
        var reg = new Dictionary<string, (string, string)>();
        foreach (var ini in inis)
            foreach (var raw in File.ReadAllLines(ini, Encoding.Latin1))
            {
                var line = raw.Split('#')[0].Trim();
                int eq = line.IndexOf('=');
                if (eq < 0 || line.StartsWith('[')) continue;
                var f = line[(eq + 1)..].Split(',').Select(x => x.Trim()).ToArray();
                if (f.Length >= 4 && f[1].Equals("slider", StringComparison.OrdinalIgnoreCase)) reg[line[..eq].Trim().ToLowerInvariant()] = (f[2], f[3]);
            }
        return sliders[race] = reg;
    }

    // ------------------------------------------------------------------ morphs d'un headpart (headgen.morph_part)

    string? PartFile(JsonNode hp, string kind)
    {
        foreach (var p in hp["parts"]?.AsArray() ?? [])
            if (p!["type"]?.GetValue<string>() == kind && p["file"]?.GetValue<string>() is { Length: > 0 } f) return ctx.FindMesh(f);
        return null;
    }

    static string RelMesh(string path)
    {
        var low = path.ToLowerInvariant();
        int i = low.IndexOf(@"meshes\", StringComparison.Ordinal);
        return i >= 0 ? low[(i + 7)..] : low;
    }

    /// <summary>Nombre JSON écrit comme Python l'afficherait dans une f-string (int « 2 », float « 2.0 »).</summary>
    static string PyNum(JsonNode n)
    {
        var s = n.ToJsonString();
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')) return s;
        var d = NativeTools.Num(n);
        var r = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return r.Contains('.') || r.Contains('E') ? r : r + ".0";
    }

    /// <summary>Sommets morphés d'un headpart (double), ou null s'il n'a pas de modèle.</summary>
    public double[]? MorphPart(JsonNode hp, JsonNode j, string race)
    {
        if (hp["model"]?.GetValue<string>() is not { Length: > 0 } modelRel || ctx.FindMesh(modelRel) is not { } model) return null;
        var bv = Nif.Load(model).Shapes[0].Verts;
        int n = bv.Count;
        var out_ = new double[n * 3];
        for (int i = 0; i < n; i++) { out_[i * 3] = bv[i].X; out_[i * 3 + 1] = bv[i].Y; out_[i * 3 + 2] = bv[i].Z; }
        var cgPath = PartFile(hp, "ChargenMorph");
        var cg = cgPath != null ? Lower(TriOf(cgPath).Morphs) : new Dictionary<string, double[]>();
        if (cgPath != null && TriOf(cgPath).NVerts != n) return out_;
        void Add(double[] m, double k) { for (int i = 0; i < out_.Length; i++) out_[i] += k * m[i]; }
        void AddUnit(double[] m) { for (int i = 0; i < out_.Length; i++) out_[i] += m[i]; }

        if (PartFile(hp, "RaceMorph") is { } rc)
        {
            var rms = TriOf(rc).Morphs;
            if (!rms.TryGetValue(race, out var rm)) rms.TryGetValue(race.Replace("00UBE_", ""), out rm);
            if (rm != null && rm.Length / 3 == n) Add(rm, 1);
        }
        // poids du personnage : SkinnyMorph (tri d'expressions) appliqué à (1 - poids/100)
        if (PartFile(hp, "Tri") is { } triPath && TriOf(triPath).NVerts == n
            && Lower(TriOf(triPath).Morphs).TryGetValue("skinnymorph", out var sk))
        {
            double wt = j["actor"]?["weight"] is { } w ? NativeTools.Num(w) : 100;
            if (wt < 100) Add(sk, 1 - wt / 100);
        }
        var d = j["morphs"]!["default"]!;
        var vals = d["morphs"]!.AsArray();
        for (int k = 0; k < Math.Min(18, vals.Count); k++)
        {
            double v = NativeTools.Num(vals[k]!);
            if (v != 0 && Math.Abs(v) < 1e3 && cg.TryGetValue((v > 0 ? Pairs[k].Hi : Pairs[k].Lo).ToLowerInvariant(), out var m)) Add(m, Math.Abs(v) * 1);
        }
        var nama = d["presets"]!.AsArray();
        foreach (var (kind, node) in new[] { ("nosetype", nama[0]!), ("eyestype", nama[2]!), ("liptype", nama[3]!) })
        {
            double v = NativeTools.Num(node);
            if (v >= 0 && v < 1000 && cg.TryGetValue(kind + PyNum(node), out var m)) AddUnit(m);
        }
        var ext = new Dictionary<string, double[]>();
        if (cgPath != null && MorphRegistry().TryGetValue(RelMesh(cgPath), out var extra))
            foreach (var p in extra)
            {
                var t = TriOf(p);
                if (t.NVerts == n) foreach (var (k, v) in t.Morphs) ext.TryAdd(k.ToLowerInvariant(), v);
            }
        foreach (var c in j["morphs"]?["custom"]?.AsArray() ?? [])
        {
            if (!Sliders(race).TryGetValue(c!["name"]!.GetValue<string>().ToLowerInvariant(), out var s)) continue;
            double value = NativeTools.Num(c["value"]!);
            var key = (value > 0 ? s.Hi : s.Lo).ToLowerInvariant();
            if (ext.TryGetValue(key, out var m) || cg.TryGetValue(key, out m)) Add(m, Math.Abs(value) * 1);
        }
        foreach (var s in j["morphs"]?["sculpt"]?.AsArray() ?? [])
        {
            var host = s!["host"]!.GetValue<string>().ToLowerInvariant().Replace('/', '\\');
            if (cgPath == null || RelMesh(cgPath) != host || s["data"] is not { } dataNode) continue;
            if (dataNode is JsonValue dv && dv.TryGetValue<string>(out var str)) { if (str.Length == 0) continue; dataNode = JsonNode.Parse(str)!; }
            var flat = Flatten(dataNode);
            if (flat.Count == 0) continue;
            double div = j["morphs"]?["sculptDivisor"] is { } dn && NativeTools.Num(dn) != 0 ? NativeTools.Num(dn) : 10000;
            int rows = flat.Count / 4;
            int max = 0;
            for (int r = 0; r < rows; r++) max = Math.Max(max, (int)flat[r * 4]);
            if (rows > 0 && max >= n) continue;
            // out[idx] += … de numpy : avec un indice en double, la dernière ligne gagne (pas de cumul)
            var before = (double[])out_.Clone();
            for (int r = 0; r < rows; r++)
            {
                int idx = (int)flat[r * 4];
                for (int k = 0; k < 3; k++) out_[idx * 3 + k] = before[idx * 3 + k] + flat[r * 4 + 1 + k] / div;
            }
        }
        return out_;
    }

    static List<double> Flatten(JsonNode n)
    {
        var l = new List<double>();
        void Walk(JsonNode? x) { if (x is JsonArray a) foreach (var e in a) Walk(e); else if (x != null) l.Add(NativeTools.Num(x)); }
        Walk(n);
        return l;
    }

    // ------------------------------------------------------------------ aperçu (assemble.py + export_mesh.py)

    HashSet<string> SkeletonNodes(string rel) => ctx.FindMesh(rel) is { } p
        ? Nif.Load(p).File.Blocks.OfType<NiflySharp.Blocks.NiNode>().Select(n => n.Name?.String ?? "").ToHashSet() : [];

    /// <summary>
    /// Géométrie JSON de la tête générée, comme export_mesh.py sur le .nif d'assemble.py : une forme par headpart (nommée
    /// par son EditorID, BSDynamicTriShape), sommets morphés (ou ceux de la tête RaceMenu importée s'ils correspondent),
    /// textures du TextureSet du headpart, teinte dans le slot FacegenDetail du visage. En plus, « local » : sommets du
    /// visage dans le repère de la forme (comparaison avec un export, comme compare_heads.py).
    /// </summary>
    public JsonObject Mesh(string preset, string race, string hpJson, string tintPath)
    {
        vanilla ??= SkeletonNodes(@"actors\character\character assets\skeleton.nif");
        if (mergeIntoHead == null)
        {
            mergeIntoHead = SkeletonNodes(@"actors\character\character assets female\skeleton_female.nif");
            mergeIntoHead.ExceptWith(vanilla);
            mergeIntoHead.Remove(Head);
        }
        var j = JsonNode.Parse(File.ReadAllText(ctx.PresetPath(preset)))!;
        var fiche = JsonNode.Parse(File.ReadAllText(hpJson))!;
        var imported = new Dictionary<string, List<System.Numerics.Vector3>>();
        if (fiche["import"]?.GetValue<string>() is { } imp)
            foreach (var s in Nif.Load(imp).Shapes) imported[s.Name.ToLowerInvariant()] = s.Verts;
        var shapes = new JsonArray();
        var boneXf = new Dictionary<string, Xform>();
        foreach (var hp in fiche["headParts"]!.AsArray())
        {
            if (hp!["model"]?.GetValue<string>() is not { Length: > 0 } modelRel || ctx.FindMesh(modelRel) is not { } path) continue;
            var srcNif = Nif.Load(path);
            var src = srcNif.Shapes[0];
            var edid = hp["edid"]!.GetValue<string>();
            float[] verts;
            var morphed = MorphPart(hp, j, race);
            verts = morphed != null ? morphed.Select(x => (float)x).ToArray() : src.Verts.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray();
            var srcEdid = (hp["srcEdid"]?.GetValue<string>() ?? edid).ToLowerInvariant();
            if (imported.TryGetValue(srcEdid, out var iv) && iv.Count * 3 == verts.Length)
                verts = iv.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray();
            var tris = src.Tris;
            if (verts.Length == 0 || tris.Count == 0) continue;

            // textures : slots de la pièce, remplacés par le TextureSet du headpart ; teinte dans le slot 6 du visage
            var slots = new string[9];
            for (int i = 0; i < 9; i++) slots[i] = src.Slot(i);
            if (hp["textureSet"] is JsonArray txst)
                for (int i = 0; i < txst.Count && i < 8; i++)
                    if (txst[i]?.GetValue<string>() is { Length: > 0 } t) slots[TxstToSlot[i]] = t;
            if (hp["hpType"]?.GetValue<string>() == "Face" && src.Shader != null) slots[6] = tintPath;
            var tex = src.TexturesFrom(slots);

            // position : par l'os de la tête (os du skin gardés comme assemble.py : ceux hors du squelette vanilla sont
            // reversés dans la tête)
            var srcBones = src.BoneNames.Distinct().ToList();
            var bones = srcBones.Where(b => b == Head || (vanilla.Contains(b) && !mergeIntoHead.Contains(b))).ToList();
            bool merged = srcBones.Any(b => b != Head && (mergeIntoHead.Contains(b) || !vanilla.Contains(b)));
            if (merged && !bones.Contains(Head)) bones.Add(Head);
            // os ajouté sous la racine avec la transformation de son nœud dans la pièce, une seule fois : la première pièce
            // qui ajoute un os fixe sa position pour toutes les suivantes (nœud déjà présent dans le .nif d'assemble.py)
            foreach (var b in bones) boneXf.TryAdd(b, srcNif.Node(b) is { } node ? Xform.Of(node) : Xform.Identity);
            var b0 = bones.Contains(Head) ? Head : bones.FirstOrDefault();
            // peau → os : celle de la pièce ; à défaut (os de physique reversés dans la tête), la transformation passée à
            // add_bone (nœud de l'os dans la pièce), que nifly garde comme peau → os
            Xform? world = b0 == null ? src.Transform
                : boneXf[b0].Then(src.SkinToBone(b0) ?? (srcNif.Node(b0) is { } n0 ? Xform.Of(n0) : Xform.Identity));
            var wv = new JsonArray();
            for (int i = 0; i < verts.Length; i += 3)
            {
                var (x, y, z) = world.Value.Apply(verts[i], verts[i + 1], verts[i + 2]);
                wv.Add(Round(x, 4)); wv.Add(Round(y, 4)); wv.Add(Round(z, 4));
            }
            var tri = new JsonArray();
            // nifly réécrit chaque triangle en commençant par son plus petit indice (même sens de rotation)
            foreach (var t in tris)
            {
                int a = t.V1, b = t.V2, cc = t.V3;
                if (b < a && b <= cc) (a, b, cc) = (b, cc, a);
                else if (cc < a && cc < b) (a, b, cc) = (cc, a, b);
                tri.Add(a); tri.Add(b); tri.Add(cc);
            }
            var uvs = new JsonArray();
            // UV enregistrées en demi-précision dans un BSDynamicTriShape SSE
            foreach (var uv in src.UVs) { uvs.Add(Round((float)(Half)uv.U, 5)); uvs.Add(Round((float)(Half)uv.V, 5)); }
            var alpha = src.Alpha;
            int flags = src.AlphaFlags;
            var shape = new JsonObject
            {
                ["name"] = edid,
                ["block"] = "BSDynamicTriShape",
                ["shader"] = src.ShaderType,
                ["hairTint"] = src.ShaderType == 6,
                ["alpha"] = alpha != null,
                ["alphaBlend"] = alpha != null && (flags & 1) != 0,
                ["blendSrc"] = alpha != null ? (flags >> 1) & 0xF : 6,
                ["blendDst"] = alpha != null ? (flags >> 5) & 0xF : 7,
                ["alphaTest"] = alpha != null && (flags & 0x200) != 0 ? alpha.Threshold / 255.0 : 0,
                ["diffuse"] = tex.GetValueOrDefault("Diffuse", ""),
                ["tint"] = tex.GetValueOrDefault("FacegenDetail", ""),
                ["verts"] = wv,
                ["tris"] = tri,
                ["uvs"] = uvs,
            };
            if (src.ShaderType == 4) shape["local"] = new JsonArray(verts.Select(v => (JsonNode)(double)v).ToArray());
            shapes.Add(shape);
        }
        return new JsonObject { ["shapes"] = shapes };
    }

    static double Round(double x, int n)
    {
        var f = Math.Pow(10, n);
        return Math.Round(x * f, MidpointRounding.ToEven) / f;
    }
}
