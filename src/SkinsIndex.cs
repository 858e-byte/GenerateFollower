using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenerateFollower;

/// <summary>
/// Catalogue des skins (plan autonome .NET, étape 6 ; remplace offline\skins_index.py, mêmes règles) : mods du
/// gestionnaire (activés ou non) et archives téléchargées, listées avec SharpCompress au lieu de 7-Zip (7-Zip du joueur
/// en secours s'il est installé). Lecture seule. Sortie identique au script : {"skins": [...], "exclus_corps": [...],
/// "familles": [...]}, et le cache des listings (&lt;sortie&gt;.archives.json, même format).
/// </summary>
public sealed class SkinsIndex
{
    // rôle -> chemin générique (minuscules, depuis « textures\ »), par type de corps
    static readonly (string Body, string Sex, (string Role, string Path)[] Layout)[] Layouts =
    [
        ("UBE", "F", [
            ("body_d", @"!ube\body\femalebody_1_d.dds"), ("body_n", @"!ube\body\femalebody_1_n.dds"),
            ("body_sk", @"!ube\body\femalebody_1_sk.dds"), ("body_s", @"!ube\body\femalebody_1_s.dds"),
            ("head_d", @"!ube\head\femalehead_d.dds"), ("head_n", @"!ube\head\femalehead_n.dds"),
            ("head_sk", @"!ube\head\femalehead_sk.dds"), ("head_s", @"!ube\head\femalehead_s.dds")]),
        ("UBE", "M", [
            ("body_d", @"!ube\body\malebody_1_d.dds"), ("body_n", @"!ube\body\malebody_1_n.dds"),
            ("body_sk", @"!ube\body\malebody_1_sk.dds"), ("body_s", @"!ube\body\malebody_1_s.dds"),
            ("head_d", @"!ube\head\malehead_d.dds"), ("head_n", @"!ube\head\malehead_n.dds"),
            ("head_sk", @"!ube\head\malehead_sk.dds"), ("head_s", @"!ube\head\malehead_s.dds")]),
        ("CBBE", "F", [
            ("body_d", @"actors\character\female\femalebody_1.dds"), ("body_n", @"actors\character\female\femalebody_1_msn.dds"),
            ("body_sk", @"actors\character\female\femalebody_1_sk.dds"), ("body_s", @"actors\character\female\femalebody_1_s.dds"),
            ("hands_d", @"actors\character\female\femalehands_1.dds"), ("hands_n", @"actors\character\female\femalehands_1_msn.dds"),
            ("hands_sk", @"actors\character\female\femalehands_1_sk.dds"), ("hands_s", @"actors\character\female\femalehands_1_s.dds"),
            ("head_d", @"actors\character\female\femalehead.dds"), ("head_n", @"actors\character\female\femalehead_msn.dds"),
            ("head_sk", @"actors\character\female\femalehead_sk.dds"), ("head_s", @"actors\character\female\femalehead_s.dds")]),
        ("HIMBO", "M", [
            ("body_d", @"actors\character\male\malebody_1.dds"), ("body_n", @"actors\character\male\malebody_1_msn.dds"),
            ("body_sk", @"actors\character\male\malebody_1_sk.dds"), ("body_s", @"actors\character\male\malebody_1_s.dds"),
            ("hands_d", @"actors\character\male\malehands_1.dds"), ("hands_n", @"actors\character\male\malehands_1_msn.dds"),
            ("hands_sk", @"actors\character\male\malehands_1_sk.dds"), ("hands_s", @"actors\character\male\malehands_1_s.dds"),
            ("head_d", @"actors\character\male\malehead.dds"), ("head_n", @"actors\character\male\malehead_msn.dds"),
            ("head_sk", @"actors\character\male\malehead_sk.dds"), ("head_s", @"actors\character\male\malehead_s.dds"),
            // sexe : chemins SOS lus par le maillage TNG m_genitals_1.nif (diffuse VectorPlexus Regular, normal Muscular)
            ("genitals_d", @"actors\character\sos\vectorplexus regular\malegenitals_1.dds"),
            ("genitals_n", @"actors\character\sos\vectorplexus muscular\malegenitals_1_msn.dds"),
            ("genitals_sk", @"actors\character\sos\vectorplexus regular\malegenitals_1_sk.dds"),
            ("genitals_s", @"actors\character\sos\vectorplexus regular\malegenitals_1_s.dds")]),
    ];
    const RegexOptions I = RegexOptions.IgnoreCase;
    const string Ube = "(?<![a-z])ube(?![a-z])";
    static readonly Regex OtherHead = new(@"\bece\b|expressive", I), HimboMark = new("himbo", I);
    static readonly Regex Preferred = new("required|smooth|base|default|core|main|soft|regular|normal|natural", I);
    // fichier factice d'une option qui « désactive » une texture (BnP « sk fix » : DDS 4x4 vides) : jamais retenu
    const long Placeholder = 1024;
    // normal map du visage propre à une race (BnP « face normal » : redguardmale\malehead_msn.dds)
    static readonly Regex RaceHeadN = new(@"^actors\\character\\([^\\]+)\\(?:fe)?malehead\w*_msn\.dds$");
    static readonly Regex BodySuffix = new(@"\s*[-(]?\s*(for\s+)?(cbbe|3ba|3bbb|uunp|bhunp|unp)\b.*$", I);

    readonly string female, male, erin;
    readonly Regex otherFemale, unsupportedRe, supportedRe;
    readonly Regex? erinMark;
    readonly Dictionary<string, string> bodyName;
    readonly List<string> excluded = [];

    SkinsIndex(string female, string male, string erin)
    {
        this.female = female; this.male = male; this.erin = erin;
        (otherFemale, unsupportedRe, supportedRe) = Marks(female);
        erinMark = erin switch { "UNP" => new Regex(@"uunp|bhunp|\bunp\b", I), "CBBE" => new Regex("cbbe|3ba|3bbb", I), _ => null };
        bodyName = new() { ["CBBE"] = female, ["HIMBO"] = male, ["UBE"] = "UBE" };
    }

    /// <summary>(autres corps féminins, corps non acceptés, corps acceptés) pour la famille fam.</summary>
    static (Regex Other, Regex Unsupported, Regex Supported) Marks(string fam)
    {
        if (fam == "UNP")
        {
            var other = new Regex("cbbe|3ba|3bbb|vanilla", I);
            return (other, other, new Regex($@"uunp|bhunp|\bunp\b|{Ube}|himbo", I));
        }
        if (fam == "VANILLA")
        {
            var other = new Regex(@"cbbe|3ba|3bbb|uunp|bhunp|\bunp\b", I);
            return (other, other, new Regex($"vanilla|{Ube}|himbo", I));
        }
        return (new Regex(@"uunp|bhunp|unp\b|vanilla", I), new Regex(@"uunp|bhunp|\bunp\b", I), new Regex($"cbbe|3ba|3bbb|{Ube}|himbo", I));
    }

    bool Unsupported(string name, string prefix, string body, string? fam = null)
    {
        fam ??= female;
        var (other, unsupported, supported) = Marks(fam);
        if (body == "HIMBO" && male != "HIMBO") return HimboMark.IsMatch(name + " " + prefix);
        if (unsupported.IsMatch(prefix)) return true;
        if (supported.IsMatch(prefix)) return false;
        if (body == "CBBE" && other.IsMatch(prefix)) return true;
        var text = name + " " + prefix;
        if (unsupported.IsMatch(text) && !supported.IsMatch(text)) return true;
        return body == "CBBE" && fam != "VANILLA" && Regex.IsMatch(text, "vanilla", I) && !supported.IsMatch(text);
    }

    List<string> Families(string name, string prefix, string body)
    {
        if (body != "CBBE") return Unsupported(name, prefix, body) ? [] : [bodyName[body]];
        var fams = Unsupported(name, prefix, body) ? new List<string>() : [female];
        if (erin != female && erinMark != null && erinMark.IsMatch(name + " " + prefix) && !Unsupported(name, prefix, body, erin)) fams.Add(erin);
        return fams;
    }

    /// <summary>Fichiers (chemin minuscule depuis textures\ → (chemin réel, taille)) → skins (corps, sexe, fichiers, tailles).</summary>
    static List<(string Body, string Sex, OrderedDictionary<string, string> Files, OrderedDictionary<string, long?> Sizes)> Classify(
        Dictionary<string, (string Real, long? Size)> files)
    {
        var found = new List<(string, string, OrderedDictionary<string, string>, OrderedDictionary<string, long?>)>();
        foreach (var (body, sex, layout) in Layouts)
        {
            if (!files.ContainsKey(layout[0].Path)) continue;
            var f = new OrderedDictionary<string, string>();
            var s = new OrderedDictionary<string, long?>();
            foreach (var (role, path) in layout)
                if (files.TryGetValue(path, out var v)) { f[role] = v.Real; s[role] = v.Size; }
            found.Add((body, sex, f, s));
        }
        return found;
    }

    static (string Role, string Path)[] LayoutOf(string body, string sex) => Layouts.First(l => l.Body == body && l.Sex == sex).Layout;

    static HashSet<string> Segments(string prefix) => prefix.Split('\\').Where(x => x.Length > 0).Select(x => x.ToLowerInvariant()).ToHashSet();

    sealed class TupleComparer : IComparer<(int, int, int, bool, int, string)>
    {
        public int Compare((int, int, int, bool, int, string) a, (int, int, int, bool, int, string) b)
        {
            int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
            c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
            c = a.Item3.CompareTo(b.Item3); if (c != 0) return c;
            c = a.Item4.CompareTo(b.Item4); if (c != 0) return c;
            c = a.Item5.CompareTo(b.Item5); if (c != 0) return c;
            return string.CompareOrdinal(a.Item6, b.Item6);
        }
    }

    /// <summary>fill_from_archive : rôles absents de l'option &lt;own&gt; pris dans les autres dossiers de l'archive (voir
    /// le script pour l'ordre de préférence) ; hommes : normal maps du visage propres aux races (head_n_&lt;race&gt;).</summary>
    void FillFromArchive(OrderedDictionary<string, OrderedDictionary<string, (string Real, long? Size)>> groups, string own,
        (string Role, string Path)[] layout, OrderedDictionary<string, string> files, OrderedDictionary<string, long?> sizes)
    {
        var ownMark = supportedRe.Match(own);
        var root = groups.Count > 0 ? groups.Keys.Select(Segments).Aggregate((a, b) => { a.IntersectWith(b); return a; }) : [];
        var ownParts = Segments(own);
        ownParts.ExceptWith(root);
        (int, int, int, bool, int, string) Rank(string prefix)
        {
            bool hasBody = groups[prefix].ContainsKey(layout[0].Path);
            int same = ownMark.Success && prefix.Contains(ownMark.Value, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            int affinity = ownParts.Intersect(Segments(prefix)).Count();
            int tier = Regex.IsMatch(prefix, "required", I) ? 0 : Preferred.IsMatch(prefix) ? 1 : 2;
            return (same, -affinity, tier, hasBody, prefix.Count(c => c == '\\'), prefix.ToLowerInvariant());
        }
        var cmp = new TupleComparer();
        var others = groups.Keys.Where(p => p != own && !unsupportedRe.IsMatch(p) && !OtherHead.IsMatch(p))
            .Select((p, i) => (p, i, r: Rank(p))).OrderBy(x => x.r, cmp).ThenBy(x => x.i).Select(x => x.p).ToList();
        var source = files.Keys.ToDictionary(r => r, _ => own);
        foreach (var (role, path) in layout)
        {
            if (files.ContainsKey(role)) continue;
            var found = others.Where(p => groups[p].ContainsKey(path)).ToList();
            var real = found.Where(p => (groups[p][path].Size ?? 0) >= Placeholder).ToList();
            if (real.Count > 0) found = real;
            if (found.Count == 0) continue;
            (files[role], sizes[role]) = groups[found[0]][path];
            source[role] = found[0];
        }
        if (source.TryGetValue("head_n", out var hn) && layout == LayoutOf("HIMBO", "M"))
            foreach (var (path, (real, size)) in groups[hn])
                if (RaceHeadN.Match(path) is { Success: true } m && (size ?? 0) >= Placeholder && m.Groups[1].Value is not ("male" or "female"))
                    (files[$"head_n_{m.Groups[1].Value}"], sizes[$"head_n_{m.Groups[1].Value}"]) = (real, size);
    }

    /// <summary>« PB's Silky Skin-95818-2-1-1721031275 » → « PB's Silky Skin » (numéros Nexus retirés).</summary>
    static string Short(string stem)
    {
        var s = Regex.Replace(stem, @"-\d{3,}(-[\w.]+)*$", "").Trim();
        return s.Length > 0 ? s : stem;
    }

    static string Variant(string prefix) => prefix.Split('\\').Where(x => x.Length > 0).LastOrDefault() ?? prefix;

    static Dictionary<string, string> VariantLabels(List<string> prefixes)
    {
        var parts = prefixes.ToDictionary(p => p, p => p.Split('\\').Where(x => x.Length > 0).ToArray());
        var labels = new Dictionary<string, string>();
        foreach (var (p, ps) in parts)
            for (int n = 1; n <= ps.Length; n++)
            {
                var tail = ps[^n..];
                if (parts.Values.Count(q => q.Length >= n ? q[^n..].SequenceEqual(tail) : q.SequenceEqual(tail)) == 1 || n == ps.Length)
                {
                    labels[p] = string.Join(" / ", tail);
                    break;
                }
            }
        return labels;
    }

    static string SkinId(string source, string path, string prefix, string body, string sex)
        => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{source}|{path}|{prefix}|{body}|{sex}".ToLowerInvariant())))[..10];

    // ------------------------------------------------------------------ listings d'archives

    /// <summary>Fichiers .dds d'une archive : [[chemin, taille]] (SharpCompress ; 7-Zip du joueur en secours) ; [] si
    /// illisible. Chemins avec « \ », comme 7-Zip.</summary>
    public static List<(string Path, long Size)> ListDds(string archive, string? sevenZip)
    {
        try
        {
            using var a = SharpCompress.Archives.ArchiveFactory.OpenArchive(archive, new SharpCompress.Readers.ReaderOptions());
            return a.Entries.Where(e => !e.IsDirectory && e.Key != null && e.Key.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Key!.Replace('/', '\\'), e.Size)).ToList();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return sevenZip != null && File.Exists(sevenZip) ? ListDds7z(archive, sevenZip) : [];
        }
    }

    /// <summary>Extrait un fichier (chemin dans l'archive, « \ » ou « / ») vers dest avec SharpCompress ; null si réussi,
    /// sinon la raison.</summary>
    public static string? Extract(string archive, string inner, string dest)
    {
        try
        {
            using var a = SharpCompress.Archives.ArchiveFactory.OpenArchive(archive, new SharpCompress.Readers.ReaderOptions());
            var key = inner.Replace('/', '\\');
            var entry = a.Entries.FirstOrDefault(e => !e.IsDirectory && e.Key != null && e.Key.Replace('/', '\\').Equals(key, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return "fichier absent de l'archive";
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".tmp";
            using (var input = entry.OpenEntryStream())
            using (var output = File.Create(tmp)) input.CopyTo(output);
            File.Move(tmp, dest, true);
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return e.Message; }
    }

    static List<(string, long)> ListDds7z(string archive, string sevenZip)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(sevenZip) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var x in new[] { "l", "-slt", "-sccUTF-8", "-ba", archive }) psi.ArgumentList.Add(x);
        string txt;
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            txt = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(300_000)) { p.Kill(); txt = ""; }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { txt = ""; }
        var dds = new List<(string, long)>();
        string? path = null;
        foreach (var line in txt.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("Path = ")) path = line[7..];
            else if (line.StartsWith("Size = ") && path != null && path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                dds.Add((path, long.TryParse(line[7..], out var n) ? n : 0));
                path = null;
            }
        }
        return dds;
    }

    // ------------------------------------------------------------------ catalogue

    /// <summary>Catalogue des skins dans outPath (et le cache des listings à côté) ; rend le résumé JSON du script.</summary>
    public static string Build(string vindex, string? modsDir, string? archDir, string? sevenZip, string outPath, string female, string male, string erin)
    {
        var idx = new SkinsIndex(female, male, erin);
        var skins = new List<JsonObject>();
        var mods = File.Exists(vindex) && !string.IsNullOrEmpty(modsDir) && Directory.Exists(modsDir)
            ? JsonNode.Parse(File.ReadAllText(vindex))!["mods"]!.AsObject() : new JsonObject();
        foreach (var (name, mNode) in mods)
        {
            var m = mNode!;
            var files = new Dictionary<string, (string, long?)>();
            foreach (var p in m["dds"]!.AsArray().Select(x => x!.GetValue<string>()))
                if (p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase)) files[p.ToLowerInvariant()[9..]] = (p, null);
            foreach (var (body, sex, f, _) in Classify(files))
            {
                var fams = idx.Families(name, "", body);
                if (fams.Count == 0) { idx.excluded.Add($"{body} {sex}  {name}"); continue; }
                // skin en plusieurs fichiers d'un même mod Nexus (Heartbreaker : « HIMBO Patch » = corps, « Core » = tête et
                // mains) : rôles absents pris dans les autres mods du même numéro Nexus
                var nexus = m["nexus"]?.GetValue<string>() is { Length: > 0 } nx ? nx : Regex.Match(name, @"-(\d{3,})-") is { Success: true } mm ? mm.Groups[1].Value : null;
                var label = name;
                if (nexus != null)
                {
                    var layout = LayoutOf(body, sex);
                    foreach (var (other, omNode) in mods)
                    {
                        var om = omNode!;
                        if (other == name || (om["nexus"]?.GetValue<string>() != nexus && !other.Contains($"-{nexus}-"))) continue;
                        var low = new Dictionary<string, string>();
                        foreach (var p in om["dds"]!.AsArray().Select(x => x!.GetValue<string>()))
                            if (p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase)) low[p.ToLowerInvariant()[9..]] = p;
                        var taken = layout.Where(l => !f.ContainsKey(l.Role) && low.ContainsKey(l.Path)).Select(l => l.Role).ToList();
                        foreach (var role in taken) f[role] = Path.Combine("..", other, low[layout.First(l => l.Role == role).Path]);
                        if (taken.Count > 0) label = Regex.Replace(Short(other), @"\s*-\s*(core|main|base)$", "", I) + $" (+ {Short(name)})";
                    }
                }
                var sizes = new JsonObject();
                foreach (var (r, p) in f)
                {
                    long size;
                    try { size = new FileInfo(Path.Combine(modsDir!, name, p)).Length; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { size = 0; }
                    sizes[r] = size;
                }
                foreach (var fam in fams)
                    skins.Add(new JsonObject
                    {
                        // « vortex » gardé dans l'empreinte : les id déjà choisis (skins_choice.json) restent valables
                        ["id"] = SkinId("vortex", name, "", fam, sex), ["name"] = label, ["body"] = fam, ["sex"] = sex,
                        ["source"] = "mod", ["path"] = Path.Combine(modsDir!, name), ["prefix"] = "",
                        ["files"] = ToJson(f), ["sizes"] = sizes.DeepClone(),
                    });
            }
        }

        // archives : listings en cache (chemin, taille|date)
        var cachePath = outPath.EndsWith(".json") ? outPath[..^5] + ".archives.json" : outPath + ".archives.json";
        JsonObject cache;
        try { cache = JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject(); }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or NullReferenceException) { cache = new JsonObject(); }
        var installed = new HashSet<string>(mods.Select(kv => kv.Key.ToLowerInvariant()));
        foreach (var (_, mNode) in mods) if (mNode!["archive"]?.GetValue<string>() is { Length: > 0 } a) installed.Add(a.ToLowerInvariant());
        var archives = new List<(string Full, string Stem, string Key)>();
        if (!string.IsNullOrEmpty(archDir) && Directory.Exists(archDir))
            foreach (var full in WalkFiles(archDir))
            {
                var fn = Path.GetFileName(full);
                if (!(fn.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || fn.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || fn.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))) continue;
                var stem = Path.GetFileNameWithoutExtension(fn);
                if (installed.Contains(stem.ToLowerInvariant())) continue;   // déjà installée dans le gestionnaire de mods
                var fi = new FileInfo(full);
                archives.Add((full, stem, $"{fi.Length}|{(long)Math.Floor(NativeTools.PyMtime(full))}"));
            }
        var todo = archives.Where(a => cache[a.Full]?["key"]?.GetValue<string>() != a.Key).ToList();
        var listings = new (string, long)[todo.Count][];
        Parallel.For(0, todo.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
            i => listings[i] = ListDds(todo[i].Full, sevenZip).ToArray());
        for (int i = 0; i < todo.Count; i++)
            cache[todo[i].Full] = new JsonObject
            {
                ["key"] = todo[i].Key,
                ["dds"] = new JsonArray(listings[i].Select(d => (JsonNode)new JsonArray(d.Item1, d.Item2)).ToArray()),
            };
        // « X for BHUNP » et « X » téléchargés tous les deux : « X », sans marque de corps, est pour l'autre corps
        var markedBases = new Dictionary<string, HashSet<string>>();
        foreach (var (_, stem, _) in archives)
            if (BodySuffix.Match(Short(stem)) is { Success: true } m && m.Index > 0)
            {
                var b = Short(stem)[..m.Index].Trim().ToLowerInvariant();
                if (!markedBases.TryGetValue(b, out var set)) markedBases[b] = set = [];
                set.Add(m.Groups[2].Value.ToLowerInvariant());
            }
        bool SiblingOfOtherBody(string stem, string fam)
        {
            if (!markedBases.TryGetValue(Short(stem).Trim().ToLowerInvariant(), out var found) || found.Count == 0 || BodySuffix.IsMatch(Short(stem))) return false;
            string[] want = fam == "UNP" ? ["uunp", "bhunp", "unp"] : fam == "CBBE" ? ["cbbe", "3ba", "3bbb"] : [];
            return found.Overlaps(want);
        }
        foreach (var (full, stem, _) in archives)
        {
            // regroupe par dossier avant « textures\ » (variantes d'un FOMOD)
            var groups = new OrderedDictionary<string, OrderedDictionary<string, (string, long?)>>();
            void Put(string prefix, string key, (string, long?) v)
            {
                if (!groups.TryGetValue(prefix, out var g)) groups[prefix] = g = new();
                g[key] = v;
            }
            foreach (var d in cache[full]!["dds"]!.AsArray())
            {
                var p = d![0]!.GetValue<string>();
                long size = d[1]!.GetValue<long>();
                var lp = p.Replace('/', '\\').ToLowerInvariant();
                int i = lp.IndexOf(@"textures\", StringComparison.Ordinal);
                if (i >= 0 && (i == 0 || lp[i - 1] == '\\')) { Put(p[..i], lp[(i + 9)..], (p, size)); continue; }
                // options rangées sans « textures\actors\character\ » (Reverie) : reconnues au dossier Female\ ou Male\
                if (Regex.Match(lp, @"(^|\\)(female|male)\\[^\\]+\.dds$") is { Success: true } mm)
                {
                    int j = mm.Groups[2].Index;
                    Put(p[..j], @"actors\character\" + lp[j..], (p, size));
                }
            }
            var labels = VariantLabels(groups.Keys.Where(p => p.Length > 0).ToList());
            var marked = new HashSet<(string, string)>();
            foreach (var (p, fs) in groups)
                if (idx.supportedRe.IsMatch(p))
                    foreach (var (b, s, _, _) in Classify(fs.ToDictionary(kv => kv.Key, kv => kv.Value))) marked.Add((b, s));
            foreach (var (prefix, files) in groups)
                foreach (var (body, sex, f, sizes) in Classify(files.ToDictionary(kv => kv.Key, kv => kv.Value)))
                {
                    var fams = idx.Families(stem, prefix, body);
                    if (fams.Count == 0) { idx.excluded.Add($"{body} {sex}  {stem} [{prefix}]"); continue; }
                    fams = fams.Where(fm => !(marked.Contains((body, sex)) && !Marks(fm).Supported.IsMatch(prefix))
                        && !(body == "CBBE" && SiblingOfOtherBody(stem, fm))).ToList();
                    if (fams.Count == 0) { idx.excluded.Add($"{body} {sex}  {stem} [{prefix}] (autre UV : l'archive a une option {body} explicite)"); continue; }
                    idx.FillFromArchive(groups, prefix, LayoutOf(body, sex), f, sizes);
                    var label = Short(stem) + (prefix.Length > 0 && groups.Count > 1 ? " — " + labels.GetValueOrDefault(prefix, Variant(prefix)) : "");
                    var sz = new JsonObject();
                    foreach (var (r, v) in sizes) sz[r] = v;
                    foreach (var fam in fams)
                        skins.Add(new JsonObject
                        {
                            ["id"] = SkinId("archive", full, prefix, fam, sex), ["name"] = label, ["body"] = fam, ["sex"] = sex,
                            ["source"] = "archive", ["path"] = full, ["prefix"] = prefix, ["files"] = ToJson(f), ["sizes"] = sz.DeepClone(),
                        });
                }
        }
        var opts = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(cachePath, cache.ToJsonString(opts));
        var sorted = skins.Select((s, i) => (s, i)).OrderBy(x => x.s["body"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(x => x.s["sex"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(x => x.s["name"]!.GetValue<string>().ToLowerInvariant(), StringComparer.Ordinal).ThenBy(x => x.i).Select(x => (JsonNode)x.s).ToArray();
        var ex = idx.excluded.Distinct().Order(StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray();
        File.WriteAllText(outPath, new JsonObject
        {
            ["skins"] = new JsonArray(sorted), ["exclus_corps"] = new JsonArray(ex),
            ["familles"] = new JsonArray(female, male, erin),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = opts.Encoder }));
        return new JsonObject { ["skins"] = sorted.Length, ["exclus_corps"] = ex.Length, ["archives_listees"] = todo.Count }.ToJsonString();
    }

    static JsonObject ToJson(OrderedDictionary<string, string> f)
    {
        var o = new JsonObject();
        foreach (var (k, v) in f) o[k] = v;
        return o;
    }

    /// <summary>os.walk : fichiers d'un dossier, puis ses sous-dossiers, dans l'ordre du système.</summary>
    static IEnumerable<string> WalkFiles(string dir)
    {
        List<string> files, dirs;
        try { files = Directory.EnumerateFiles(dir).ToList(); dirs = Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
            foreach (var f in WalkFiles(d)) yield return f;
    }
}
