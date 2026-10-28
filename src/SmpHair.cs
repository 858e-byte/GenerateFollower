using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace GenerateFollower;

/// <summary>
/// Cheveux SMP animés : la coiffure SMP d'un follower est portée comme une perruque (piste 2a de
/// work\diag\cheveux_smp\PLAN.md). Pour un PNJ, Faster HDT-SMP lit la physique dans le FaceGen, et l'Export Head de RaceMenu
/// a perdu à la fois la donnée de physique et les os d'attache des mèches. On prend donc la perruque SMP que l'auteur du
/// pack de cheveux livre (BDOr 01_0.nif, SMP Wig8\…_0.nif, KSWigsHDT\…) : un seul .nif, avec formes, collisions, os et
/// .xml. Elle devient une ARMA de l'armure de peau du follower (emplacement 31, un casque la cache), et les formes de cette
/// coiffure sont cachées dans le FaceGen (drapeau, aucune forme ajoutée ni retirée : la tête reste acceptée).
/// Choix par follower (Builder, section Avancé, option « cheveuxSmp » de options_choice.json) : absent = automatique
/// (animés si Faster HDT-SMP est installé), true = animés, false = rigides. Réglage de développeur GF_CHEVEUX_SMP=0/1 :
/// force tout (tests).
/// </summary>
public static class SmpHair
{
    public const string Key = "HDT Skinned Mesh Physics Object";
    const string TriKey = "BODYTRI";
    public const string Option = "cheveuxSmp";

    /// <summary>Faster HDT-SMP (ou HDT-SMP) installé : SKSE\Plugins\hdtSMP64.dll dans Data ou les mods (MO2).</summary>
    public static bool SmpInstalled(Context ctx) => smpInstalled ??= ctx.Files.Find(@"SKSE\Plugins\hdtSMP64.dll") != null;
    static bool? smpInstalled;

    /// <summary>Choix du follower : null = automatique, true = animés, false = rigides.</summary>
    public static bool? Choice(JsonObject? opts) => opts?[Option] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>Cheveux SMP animés (perruque) pour ce follower ?</summary>
    public static bool Enabled(Context ctx, JsonObject? opts)
    {
        var env = (Environment.GetEnvironmentVariable("GF_CHEVEUX_SMP") ?? "").Trim();
        if (env == "0") return false;
        if (env == "1") return true;
        return Choice(opts) ?? SmpInstalled(ctx);
    }

    /// <summary>
    /// État des cheveux SMP d'un preset pour le Builder (rien n'est écrit, sauf l'index work\smp_wigs.json) :
    /// { smp, installe, xml, perruque, fabriquee, pieces }. <paramref name="parts"/> : (forme dans l'export, .nif de la
    /// pièce). Mémorisé tant que l'export ne change pas.
    /// </summary>
    public static JsonObject Probe(Context ctx, string id, IEnumerable<(string Face, List<string> Models)> parts, string? exportPath)
    {
        var state = new JsonObject { ["smp"] = false, ["installe"] = SmpInstalled(ctx) };
        if (exportPath == null || !File.Exists(exportPath)) return state;
        var key = $"{id}|{exportPath}|{File.GetLastWriteTimeUtc(exportPath).Ticks}";
        lock (probes) if (probes.TryGetValue(key, out var done)) return (JsonObject)done.DeepClone();
        var bytes = File.ReadAllBytes(exportPath);
        if (Raw.Parse(bytes) is { } exp && exp.Nodes().Any(n => PhysBone.IsMatch(n)))
        {
            state["smp"] = true;
            var wig = Find(ctx, id, parts, bytes, new StringBuilder(), "(aperçu)");
            if (wig != null)
            {
                state["xml"] = wig.HairXml;
                state["perruque"] = wig.WigNif;
                state["fabriquee"] = wig.Fab != null;
                state["pieces"] = wig.Fab?.Pieces.Count ?? 0;
            }
        }
        lock (probes) probes[key] = (JsonObject)state.DeepClone();
        return state;
    }
    static readonly Dictionary<string, JsonObject> probes = [];

    /// <summary>Pièces d'un preset lues dans sa fiche work\headparts\&lt;id&gt;.json : (srcEdid, .nif du modèle et des parts).</summary>
    public static List<(string Face, List<string> Models)> PartsOf(JsonNode fiche)
    {
        var list = new List<(string, List<string>)>();
        foreach (var h in fiche["headParts"]?.AsArray() ?? [])
        {
            var face = h?["srcEdid"]?.GetValue<string>() ?? h?["edid"]?.GetValue<string>();
            if (face == null) continue;
            var models = new List<string>();
            if (h!["model"]?.GetValue<string>() is { Length: > 0 } m) models.Add(m);
            foreach (var part in h["parts"]?.AsArray() ?? [])
                if ((part is JsonValue ? part.GetValue<string>() : part?["file"]?.GetValue<string>()) is { Length: > 0 } f) models.Add(f);
            list.Add((face, models));
        }
        return list;
    }

    /// <summary>Pièces d'un preset depuis ses headparts effectifs (Builder) : EditorID d'origine et fichiers, extras compris.</summary>
    public static List<(string Face, List<string> Models)> PartsOf(Context ctx, IEnumerable<IHeadPartGetter> effective)
    {
        var list = new List<(string, List<string>)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(IHeadPartGetter h)
        {
            if (h.EditorID == null || !seen.Add(h.EditorID)) return;
            var models = new List<string>();
            if (h.Model?.File is { } m) models.Add(m.DataRelativePath.Path);
            foreach (var p in h.Parts) if (p.FileName is { } f) models.Add(f.DataRelativePath.Path);
            list.Add((h.EditorID, models));
            foreach (var e in h.ExtraParts) if (ctx.Cache.TryResolve<IHeadPartGetter>(e.FormKey, out var x)) Add(x);
        }
        foreach (var h in effective) Add(h);
        return list;
    }

    /// <summary>Perruque retenue pour un follower.</summary>
    /// <param name="HairXml">.xml de la coiffure (relatif à Data, « meshes\… »)</param>
    /// <param name="WigNif">.nif de la perruque (relatif à Data) ; variante _1 si les deux poids existent</param>
    /// <param name="WeightSlider">vrai si _0 et _1 existent (ARMA avec curseur de poids)</param>
    /// <param name="FaceShapes">formes de l'export (noms d'origine) à cacher dans le FaceGen</param>
    /// <param name="Files">fichiers à copier dans le mod (relatifs à Data)</param>
    /// <param name="Strings">chaînes à réécrire dans le .nif copié : ancienne valeur → chemin de notre copie</param>
    /// <param name="Model">chemin de la perruque dans le mod, relatif à meshes (ARMA)</param>
    /// <param name="Fab">perruque fabriquée (pas de perruque d'auteur) : .nif à écrire, sinon null</param>
    public sealed record Wig(string HairXml, string WigNif, string Model, bool WeightSlider, HashSet<string> FaceShapes, List<string> Files,
        Dictionary<string, string> Strings, Fabrication? Fab = null);

    /// <summary>
    /// Perruque fabriquée : le .nif de la pièce qui porte le .xml (hiérarchie d'os complète, os d'attache compris, donnée de
    /// physique), plus les formes des autres pièces de la coiffure (ligne de cheveux liée aux os de physique, collisions
    /// citées par le .xml), chacune sous le nom que le .xml attend. Les .nif d'origine, pas l'export : chez Asagi, les
    /// sommets de la coiffure de l'export sont identiques à ceux d'origine (RaceMenu n'adapte pas la coiffure à la tête).
    /// </summary>
    public sealed record Fabrication(string BasePath, List<(string Path, string Shape, string Name)> Pieces);

    /// <summary>Réglage de développeur : GF_PERRUQUE_FABRIQUEE=1 (ou ids) fabrique la perruque même si celle de l'auteur
    /// est installée (pour comparer).</summary>
    static bool ForceFabricate(string id)
    {
        var v = (Environment.GetEnvironmentVariable("GF_PERRUQUE_FABRIQUEE") ?? "").Trim();
        return v is "1" or "all" or "tous" || v.Split(',').Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ lecture brute d'un NIF 20.2.0.7

    sealed class Raw
    {
        public string[] Types = [], Strings = [];
        public int[] Off = [];
        public byte[] D = [];
        public string? Name(int b) { int s = BitConverter.ToInt32(D, Off[b]); return s >= 0 && s < Strings.Length ? Strings[s] : null; }
        public IEnumerable<string> Nodes() => Enumerable.Range(0, Types.Length).Where(b => Types[b] == "NiNode").Select(Name).OfType<string>();
        public IEnumerable<string> Shapes() => Enumerable.Range(0, Types.Length)
            .Where(b => Types[b] is "BSTriShape" or "BSDynamicTriShape" or "NiTriShape" or "BSSubIndexTriShape").Select(Name).OfType<string>();
        /// <summary>NiStringExtraData : (nom, valeur).</summary>
        public IEnumerable<(string Name, string Value)> Extra()
        {
            for (int b = 0; b < Types.Length; b++)
            {
                if (Types[b] != "NiStringExtraData") continue;
                int n = BitConverter.ToInt32(D, Off[b]), v = BitConverter.ToInt32(D, Off[b] + 4);
                if (n >= 0 && n < Strings.Length && v >= 0 && v < Strings.Length) yield return (Strings[n], Strings[v]);
            }
        }

        public static Raw? Read(string path)
        {
            try { return Parse(File.ReadAllBytes(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException) { return null; }
        }

        public static Raw? Parse(byte[] d)
        {
            int p = Array.IndexOf(d, (byte)'\n') + 1;
            if (p <= 0 || !Encoding.ASCII.GetString(d, 0, p).StartsWith("Gamebryo File Format, Version 20.2.0.7")) return null;
            p += 4 + 1 + 4;
            int numBlocks = BitConverter.ToInt32(d, p); p += 4;
            uint bsVersion = BitConverter.ToUInt32(d, p); p += 4;
            for (int i = 0, n = bsVersion >= 103 ? 4 : 3; i < n; i++) p += 1 + d[p];
            int numTypes = BitConverter.ToUInt16(d, p); p += 2;
            var typeNames = new string[numTypes];
            for (int i = 0; i < numTypes; i++) { int len = BitConverter.ToInt32(d, p); typeNames[i] = Encoding.ASCII.GetString(d, p + 4, len); p += 4 + len; }
            int typeIdx = p; p += 2 * numBlocks;
            int sizePos = p; p += 4 * numBlocks;
            int numStrings = BitConverter.ToInt32(d, p); p += 8;
            var strings = new string[numStrings];
            for (int i = 0; i < numStrings; i++) { int len = BitConverter.ToInt32(d, p); strings[i] = Encoding.Latin1.GetString(d, p + 4, len); p += 4 + len; }
            int numGroups = BitConverter.ToInt32(d, p); p += 4 + 4 * numGroups;
            var off = new int[numBlocks];
            var types = new string[numBlocks];
            for (int b = 0; b < numBlocks; b++)
            {
                off[b] = p;
                types[b] = typeNames[BitConverter.ToUInt16(d, typeIdx + 2 * b) & 0x7FFF];
                p += BitConverter.ToInt32(d, sizePos + 4 * b);
            }
            return new Raw { Types = types, Strings = strings, Off = off, D = d };
        }
    }

    static readonly Regex PhysBone = new(@"^hdtSSEPhysics_AutoRename_[A-Za-z]+_[0-9A-Fa-f]{8} (.*)$", RegexOptions.Compiled);

    /// <summary>« Data\meshes\x.xml » → « meshes\x.xml » ; « x.tri » (relatif à meshes) → « meshes\x.tri ».</summary>
    static string DataRel(string path, bool underMeshes = false)
    {
        var p = path.Replace('/', '\\').TrimStart('\\');
        if (p.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        if (underMeshes && !p.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase)) p = @"meshes\" + p;
        return p;
    }

    /// <summary>Noms des formes citées par un .xml SMP (per-vertex-shape, per-triangle-shape).</summary>
    static HashSet<string> XmlShapes(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var e in XDocument.Load(path).Descendants())
                if (e.Name.LocalName is "per-vertex-shape" or "per-triangle-shape" && e.Attribute("name")?.Value is { } n) set.Add(n);
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException) { }
        return set;
    }

    static (HashSet<string> Bones, bool Ok) XmlBones(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // certains .xml ont du texte hors balises (« //嘴 ») : toléré par XDocument tant que la structure est correcte
            var doc = XDocument.Load(path);
            foreach (var e in doc.Descendants())
            {
                if (e.Name.LocalName == "bone" && e.Attribute("name")?.Value is { } n) set.Add(n);
                if (e.Attribute("bodyA")?.Value is { } a) set.Add(a);
                if (e.Attribute("bodyB")?.Value is { } b) set.Add(b);
            }
            return (set, true);
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException) { return (set, false); }
    }

    // ------------------------------------------------------------------ index des perruques installées

    /// <summary>
    /// { .xml (relatif à Data, minuscules) : [.nif de perruques] } : .nif dont le chemin contient « wig » et qui portent la
    /// donnée de physique. Lu une fois, gardé dans work\smp_wigs.json (relu seulement pour les fichiers changés).
    /// </summary>
    static Dictionary<string, List<string>> WigIndex(Context ctx, StringBuilder report)
    {
        lock (wigGate) return WigIndexLocked(ctx, report);
    }
    static readonly object wigGate = new();

    static Dictionary<string, List<string>> WigIndexLocked(Context ctx, StringBuilder report)
    {
        if (wigIndex != null) return wigIndex;
        var cachePath = Path.Combine(ctx.WorkDir, "smp_wigs.json");
        var old = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        try { if (File.Exists(cachePath) && JsonNode.Parse(File.ReadAllText(cachePath)) is JsonObject o) foreach (var kv in o) old[kv.Key] = kv.Value; }
        catch (JsonException) { }
        var fresh = new JsonObject();
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int read = 0, kept = 0;
        foreach (var (sub, path) in ctx.Files.Enumerate("meshes", "*.nif", true))
        {
            if (!sub.Contains("wig", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = @"meshes\" + sub;
            var fi = new FileInfo(path);
            var stamp = $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            string? xml;
            if (old.TryGetValue(rel, out var prev) && prev?["s"]?.GetValue<string>() == stamp) { xml = prev["x"]?.GetValue<string>(); kept++; }
            else
            {
                read++;
                var raw = Raw.Read(path);
                xml = raw?.Shapes().Skip(1).Any() == true ? raw.Extra().Where(x => x.Name == Key).Select(x => DataRel(x.Value)).FirstOrDefault() : null;
            }
            fresh[rel] = new JsonObject { ["s"] = stamp, ["x"] = xml };
            if (xml != null) (index.TryGetValue(xml, out var l) ? l : index[xml] = []).Add(rel);
        }
        try { File.WriteAllText(cachePath, fresh.ToJsonString()); } catch (IOException) { }
        report.AppendLine($"PERRUQUE index des perruques SMP : {index.Count} .xml, {read} .nif lus, {kept} repris de work\\smp_wigs.json");
        return wigIndex = index;
    }
    static Dictionary<string, List<string>>? wigIndex;

    // ------------------------------------------------------------------ choix de la perruque d'un follower

    /// <summary>
    /// Perruque SMP pour l'export d'un follower, ou null (pas de cheveux SMP, pas de .xml de coiffure, pas de perruque
    /// installée : cheveux rigides comme avant). <paramref name="fiche"/> = work\headparts\&lt;id&gt;.json.
    /// Sans perruque d'auteur pour cette coiffure : perruque fabriquée (<see cref="Fabrication"/>), écrite par
    /// <see cref="BuildWig"/> au chemin <paramref name="madeModel"/> (relatif à meshes).
    /// </summary>
    public static Wig? Find(Context ctx, string id, JsonNode fiche, byte[] export, StringBuilder report, string madeModel)
        => Find(ctx, id, PartsOf(fiche), export, report, madeModel);

    public static Wig? Find(Context ctx, string id, IEnumerable<(string Face, List<string> Models)> parts, byte[] export, StringBuilder report, string madeModel)
    {
        var exp = Raw.Parse(export);
        if (exp == null) return null;
        var phys = exp.Nodes().Select(n => PhysBone.Match(n)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (phys.Count == 0) return null;
        var exportShapes = exp.Shapes().ToHashSet(StringComparer.OrdinalIgnoreCase);

        // pièces du PNJ : forme de l'export (srcEdid) ↔ noms des formes de son .nif, et .xml portés
        var pieces = new List<(string Face, HashSet<string> Mesh, List<string> Xml, string Path)>();
        foreach (var (face, models) in parts)
            foreach (var model in models.Where(x => x.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)))
                if (ctx.FindMesh(model) is { } path && Raw.Read(path) is { } raw)
                    pieces.Add((face, raw.Shapes().ToHashSet(StringComparer.OrdinalIgnoreCase), raw.Extra().Where(x => x.Name == Key).Select(x => DataRel(x.Value)).ToList(), path));

        // .xml de la coiffure : celui qui cite le plus d'os de physique de l'export (écarte Gecko.xml de VirtualGround et la
        // physique de la bouche de la tête UBE)
        var scored = pieces.SelectMany(p => p.Xml).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => (Xml: x, Path: ctx.FindFile(x)))
            .Where(x => x.Path != null)
            .Select(x => (x.Xml, Hits: XmlBones(x.Path!).Bones.Count(phys.Contains)))
            .Where(x => x.Hits > 0).OrderByDescending(x => x.Hits).ToList();
        if (scored.Count == 0)
        {
            report.AppendLine($"PERRUQUE {id}  {phys.Count} os SMP, aucun .xml de coiffure trouvé : cheveux rigides");
            return null;
        }
        if (scored.Count > 1)
            report.AppendLine($"PERRUQUE {id}  plusieurs .xml de coiffure ({string.Join(", ", scored.Select(s => s.Xml))}) : on prend {scored[0].Xml}");
        var hairXml = scored[0].Xml;
        var carrier = pieces.Where(p => p.Xml.Contains(hairXml, StringComparer.OrdinalIgnoreCase)).SelectMany(p => p.Mesh).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // texture (diffuse) de chaque forme de la pièce qui porte le .xml : une perruque d'une AUTRE coiffure peut réutiliser
        // le même .xml et le même nom de forme (Asagi, test du 24/10 : coiffure Tullius 692_Fifteen, textures sims\Brush,
        // forme « KSSMP_Steammist » et Steammist.xml de KS ; la perruque KSWigsHDT\f\Steammist.nif était l'originale de KS,
        // une autre coiffure). La perruque doit avoir les mêmes textures.
        // Même coiffure que le preset (test du lot, 24/10 : Aeyochi et Aria Black avaient la perruque dint999 n° 29, qui a
        // en plus des mèches For0-For3, H29_3, L L2 absentes de leur preset et des textures d'une autre variante) :
        // - chaque forme de la perruque qui existe dans les pièces du preset y a la même texture ;
        // - aucune mèche en trop : une forme absente du preset, texturée et liée à des os de coiffure (pas seulement des
        //   os « NPC … », comme les collisions) refuse la perruque. Sinon : perruque fabriquée à partir des pièces du preset.
        var pieceTex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in pieces)
            try { foreach (var s in Nif.Load(piece.Path).Shapes) pieceTex.TryAdd(s.Name, s.Slot(0)); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        // collisions déclarées par le .xml (per-triangle-shape) : jamais des mèches (colWigMidBDO de BDOr, liée à L Breast01)
        var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ctx.FindFile(hairXml) is { } hx)
            try
            {
                foreach (var e in XDocument.Load(hx).Descendants())
                    if (e.Name.LocalName == "per-triangle-shape" && e.Attribute("name")?.Value is { } n) collisions.Add(n);
            }
            catch (Exception e) when (e is System.Xml.XmlException or IOException) { }
        string? rejected = null;
        bool SameHair(string rel)
        {
            if (ctx.FindFile(rel) is not { } p) return false;
            try
            {
                foreach (var s in Nif.Load(p).Shapes)
                {
                    var tex = s.Slot(0);
                    if (pieceTex.TryGetValue(s.Name, out var mine))
                    {
                        if (tex.Length > 0 && mine.Length > 0 && !tex.Equals(mine, StringComparison.OrdinalIgnoreCase)) { rejected = $"{rel} : texture de {s.Name} différente"; return false; }
                    }
                    else if (tex.Length > 0 && !collisions.Contains(s.Name) && s.BoneNames.Any(b => !b.StartsWith("NPC ", StringComparison.OrdinalIgnoreCase)))
                    { rejected = $"{rel} : mèche {s.Name} absente du preset"; return false; }
                }
                return true;
            }
            catch (Exception e) when (e is not OutOfMemoryException) { return false; }
        }

        // candidates : index des perruques + .nif du dossier du .xml (BDOr : 01_0.nif à côté de 01.xml), avec plusieurs formes
        var cands = new List<string>(WigIndex(ctx, report).GetValueOrDefault(hairXml.ToLowerInvariant()) ?? WigIndex(ctx, report).GetValueOrDefault(hairXml) ?? []);
        var dir = Path.GetDirectoryName(hairXml) ?? "";
        foreach (var (sub, path) in ctx.Files.Enumerate(dir, "*.nif", false))
        {
            var rel = dir + "\\" + sub;
            if (!cands.Contains(rel, StringComparer.OrdinalIgnoreCase) && Raw.Read(path) is { } r && r.Shapes().Skip(1).Any()
                && r.Extra().Any(x => x.Name == Key && DataRel(x.Value).Equals(hairXml, StringComparison.OrdinalIgnoreCase)))
                cands.Add(rel);
        }
        var allMesh = pieces.SelectMany(p => p.Mesh).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var best = cands
            .Select(c => (Rel: c, Raw: ctx.FindFile(c) is { } p ? Raw.Read(p) : null))
            .Where(c => c.Raw != null)
            .Select(c => (c.Rel, c.Raw, Shapes: c.Raw!.Shapes().ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .Where(c => carrier.All(c.Shapes.Contains))          // la pièce qui porte le .xml doit être dans la perruque
            .Where(c => SameHair(c.Rel))                         // … et la même coiffure (textures, pas de mèche en trop)
            .OrderByDescending(c => c.Shapes.Count(allMesh.Contains))
            .ThenBy(c => c.Rel.Contains("ground", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Rel.Length)
            .FirstOrDefault();
        if (best.Raw == null || ForceFabricate(id))
        {
            report.AppendLine($"PERRUQUE {id}  coiffure {hairXml} : " + (best.Raw == null
                ? $"aucune perruque SMP d'auteur pour cette coiffure ({cands.Count} candidates" + (rejected != null ? $", dernière écartée : {rejected}" : "") + ")"
                : $"perruque d'auteur {best.Rel} ignorée (GF_PERRUQUE_FABRIQUEE)") + " : perruque fabriquée");
            return Fabricate(ctx, id, pieces, hairXml, phys, exportShapes, madeModel, report);
        }

        // variantes de poids _0 / _1 : ARMA avec curseur de poids, chemin de la variante _1
        var wig = best.Rel;
        bool slider = false;
        var m01 = Regex.Match(wig, @"^(.*)_([01])\.nif$", RegexOptions.IgnoreCase);
        var files = new List<string>();
        if (m01.Success && ctx.FindFile(m01.Groups[1].Value + "_0.nif") != null && ctx.FindFile(m01.Groups[1].Value + "_1.nif") != null)
        {
            slider = true;
            wig = m01.Groups[1].Value + "_1.nif";
            files.Add(m01.Groups[1].Value + "_0.nif");
        }
        files.Add(wig);
        files.Add(hairXml);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        // chaînes du .nif copié vers nos copies : .xml en chemin « meshes\… » (comme Erienne), .tri relatif à meshes
        foreach (var (name, value) in best.Raw!.Extra())
        {
            if (name == Key) strings[value] = @"meshes\" + MovedMesh(hairXml);
            else if (name == TriKey && ctx.FindFile(DataRel(value, true)) != null) { strings[value] = MovedMesh(DataRel(value, true)); files.Add(DataRel(value, true)); }
        }

        // formes à cacher dans le FaceGen : pièces de l'export dont la forme est dans la perruque
        var hide = pieces.Where(p => p.Mesh.Any(best.Shapes.Contains) && exportShapes.Contains(p.Face)).Select(p => p.Face).ToHashSet(StringComparer.OrdinalIgnoreCase);
        report.AppendLine($"PERRUQUE {id}  coiffure {hairXml} -> perruque {wig}{(slider ? " (_0/_1)" : "")}, {best.Shapes.Count} formes ; " +
            $"FaceGen : {hide.Count} formes cachées ({string.Join(", ", hide.Order())})");
        return new Wig(hairXml, wig, MovedMesh(wig), slider, hide, files, strings);
    }

    /// <summary>Plan de la perruque fabriquée (voir <see cref="Fabrication"/>), ou null si la coiffure ne s'y prête pas.</summary>
    static Wig? Fabricate(Context ctx, string id, List<(string Face, HashSet<string> Mesh, List<string> Xml, string Path)> pieces,
        string hairXml, HashSet<string> phys, HashSet<string> exportShapes, string madeModel, StringBuilder report)
    {
        var basePiece = pieces.First(p => p.Xml.Contains(hairXml, StringComparer.OrdinalIgnoreCase));
        var xmlShapes = ctx.FindFile(hairXml) is { } xp ? XmlShapes(xp) : [];
        var plan = new List<(string Path, string Shape, string Name)>();
        var hide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string> { hairXml };
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string path)
        {
            // textures et .tri de la pièce : copiés dans le mod (la perruque pointe ensuite vers nos copies)
            try
            {
                foreach (var s in Nif.Load(path).Shapes)
                    for (int i = 0; i < 9; i++)
                        if (s.Slot(i) is { Length: > 0 } t) files.Add(t);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { }
            foreach (var (n, v) in Raw.Read(path)?.Extra() ?? [])
            {
                if (n == Key) strings[v] = @"meshes\" + MovedMesh(hairXml);
                else if (n == TriKey && ctx.FindFile(DataRel(v, true)) != null) { strings[v] = MovedMesh(DataRel(v, true)); files.Add(DataRel(v, true)); }
            }
        }
        Collect(basePiece.Path);
        foreach (var s in basePiece.Mesh) names.Add(s);
        if (exportShapes.Contains(basePiece.Face)) hide.Add(basePiece.Face);
        foreach (var piece in pieces.Where(p => p.Path != basePiece.Path).DistinctBy(p => p.Path.ToLowerInvariant()))
        {
            List<NifShape> shapes;
            try { shapes = Nif.Load(piece.Path).Shapes; }
            catch (Exception e) when (e is not OutOfMemoryException) { continue; }
            bool added = false;
            foreach (var s in shapes)
            {
                // nom attendu par le .xml : celui du mesh (KS, BDOr), sinon celui du headpart (collision « VHC4 » du mesh
                // VirtualHairCollision_4.nif, citée « VirtualHairCollision_4 »)
                var name = xmlShapes.Contains(s.Name) ? s.Name : xmlShapes.Contains(piece.Face) ? piece.Face : s.Name;
                bool cited = xmlShapes.Contains(name);
                bool physBones = s.BoneNames.Any(phys.Contains);           // ligne de cheveux liée aux os de physique
                if (!cited && !physBones) continue;
                if (!names.Add(name)) continue;
                plan.Add((piece.Path, s.Name, name));
                added = true;
            }
            if (!added) continue;
            Collect(piece.Path);
            if (exportShapes.Contains(piece.Face)) hide.Add(piece.Face);
        }
        report.AppendLine($"PERRUQUE {id}  perruque fabriquée {madeModel} : base {ctx.Files.RelOf(basePiece.Path)} + " +
            $"{string.Join(", ", plan.Select(p => p.Name))} ; FaceGen : {hide.Count} formes cachées ({string.Join(", ", hide.Order())})");
        return new Wig(hairXml, ctx.Files.RelOf(basePiece.Path), madeModel, false, hide, files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            strings, new Fabrication(basePiece.Path, plan));
    }

    /// <summary>
    /// Écrit la perruque fabriquée (NiflySharp : c'est une pièce d'armure, pas un FaceGen ; seul le FaceGen doit rester
    /// l'export modifié en binaire). Renvoie un message pour le rapport.
    /// </summary>
    public static string BuildWig(Wig wig, string outFile)
    {
        var fab = wig.Fab ?? throw new GfException("pas de perruque à fabriquer");
        var nif = new NiflySharp.NifFile();
        if (nif.Load(fab.BasePath) != 0) throw new GfException("perruque : .nif de base illisible : " + fab.BasePath);
        var root = nif.GetRootNode() ?? throw new GfException("perruque : pas de racine dans " + fab.BasePath);
        int clonedBones = 0;
        foreach (var group in fab.Pieces.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            var src = new NiflySharp.NifFile();
            if (src.Load(group.Key) != 0) throw new GfException("perruque : pièce illisible : " + group.Key);
            foreach (var (_, shape, name) in group)
            {
                var s = src.GetShapes().FirstOrDefault(x => (x as NiflySharp.Blocks.NiObjectNET)?.Name?.String == shape)
                    ?? throw new GfException($"perruque : forme {shape} absente de {group.Key}");
                var c = nif.CloneShape(s, name, src) ?? throw new GfException($"perruque : clonage de {shape} impossible");
                nif.GetBlockIndex(c, out var idx);
                if (!root.Children.Indices.Contains(idx)) root.Children.AddBlockRef(idx);
                // os absents de la base (os du corps des collisions) : clonés depuis la pièce
                foreach (var b in nif.GetShapeBoneNames(c) ?? [])
                    if (!nif.Blocks.OfType<NiflySharp.Blocks.NiNode>().Any(n => n.Name?.String == b))
                    {
                        var id = nif.CloneNamedNode(b, src);
                        if (id >= 0 && !root.Children.Indices.Contains(id)) root.Children.AddBlockRef(id);
                        clonedBones++;
                    }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        if (nif.Save(outFile) != 0) throw new GfException("perruque : enregistrement impossible : " + outFile);
        return $"{nif.GetShapes().Count()} formes, {nif.Blocks.Count} blocs" + (clonedBones > 0 ? $", {clonedBones} os ajoutés" : "");
    }

    /// <summary>
    /// Cache des formes d'un FaceGen : bit 0 (Hidden) des flags NiAVObject, modifié en place (aucune taille de bloc ne
    /// change, noms et nombre de formes identiques). Disposition (BS 100) : nom, nb d'extra data, extra data, contrôleur,
    /// puis flags (uint32).
    /// </summary>
    public static byte[] HideShapes(byte[] src, ISet<string> names, out int hidden)
    {
        hidden = 0;
        var d = (byte[])src.Clone();
        var raw = Raw.Parse(d) ?? throw new GfException("NIF non reconnu (attendu 20.2.0.7)");
        for (int b = 0; b < raw.Types.Length; b++)
        {
            if (raw.Types[b] is not ("BSTriShape" or "BSDynamicTriShape" or "BSSubIndexTriShape")) continue;
            if (raw.Name(b) is not { } n || !names.Contains(n)) continue;
            int p = raw.Off[b] + 4;
            int extras = BitConverter.ToInt32(d, p);
            p += 4 + 4 * extras + 4;
            uint flags = BitConverter.ToUInt32(d, p);
            BitConverter.GetBytes(flags | 1u).CopyTo(d, p);
            hidden++;
        }
        return d;
    }

    /// <summary>
    /// Partitions (BSDismemberSkinInstance) de toutes les formes d'une perruque mises à 31 (Hair), en place. Le jeu ne
    /// dessine une partition d'armure que si son emplacement est dans l'ARMA (emplacement 31 pour la perruque) : la barbe
    /// de thorRg (partition 32), la tresse de BRANDA (141) et presque toute la perruque dint999 n° 29 (32) étaient
    /// invisibles (test du lot, 24/10). Disposition : données, partition du skin, racine, nb d'os, os, nb de partitions,
    /// puis (drapeaux u16, partie du corps u16) par partition.
    /// </summary>
    public static byte[] HairPartitions(byte[] src, out int changed)
    {
        changed = 0;
        var d = (byte[])src.Clone();
        var raw = Raw.Parse(d) ?? throw new GfException("NIF non reconnu (attendu 20.2.0.7)");
        for (int b = 0; b < raw.Types.Length; b++)
        {
            if (raw.Types[b] != "BSDismemberSkinInstance") continue;
            int p = raw.Off[b] + 12;
            p += 4 + 4 * BitConverter.ToInt32(d, p);
            int parts = BitConverter.ToInt32(d, p); p += 4;
            for (int i = 0; i < parts; i++, p += 4)
                if (BitConverter.ToUInt16(d, p + 2) != 31) { BitConverter.GetBytes((ushort)31).CopyTo(d, p + 2); changed++; }
        }
        return d;
    }

    /// <summary>Parties du corps des partitions d'un NIF : pour les contrôles.</summary>
    public static List<int> Partitions(byte[] src)
    {
        var list = new List<int>();
        if (Raw.Parse(src) is not { } raw) return list;
        for (int b = 0; b < raw.Types.Length; b++)
        {
            if (raw.Types[b] != "BSDismemberSkinInstance") continue;
            int p = raw.Off[b] + 12;
            p += 4 + 4 * BitConverter.ToInt32(src, p);
            int parts = BitConverter.ToInt32(src, p); p += 4;
            for (int i = 0; i < parts; i++, p += 4) list.Add(BitConverter.ToUInt16(src, p + 2));
        }
        return list;
    }

    /// <summary>Formes cachées (bit 0) d'un NIF : pour les contrôles.</summary>
    public static List<string> HiddenShapes(byte[] src)
    {
        var raw = Raw.Parse(src);
        if (raw == null) return [];
        var list = new List<string>();
        for (int b = 0; b < raw.Types.Length; b++)
        {
            if (raw.Types[b] is not ("BSTriShape" or "BSDynamicTriShape" or "BSSubIndexTriShape")) continue;
            int p = raw.Off[b] + 4;
            p += 4 + 4 * BitConverter.ToInt32(src, p) + 4;
            if ((BitConverter.ToUInt32(src, p) & 1) != 0 && raw.Name(b) is { } n) list.Add(n);
        }
        return list;
    }

    // ------------------------------------------------------------------ enregistrements

    /// <summary>
    /// ARMA de la perruque (emplacement 31, race du PNJ) ajoutée à une armure de peau propre au PNJ : copie de sa peau
    /// actuelle (skin choisi) ou de la peau de sa race. <paramref name="wigModel"/> : chemin relatif à meshes de la copie.
    /// </summary>
    public static void Apply(Context ctx, SkyrimMod mod, Npc npc, PresetRow row, Wig wig, string wigModel, Standalone? standalone, StringBuilder report)
    {
        var race = ctx.Need<IRaceGetter>(row.Race, "race");
        var fid = npc.FormKey.ID.ToString("X3");
        var aa = mod.ArmorAddons.AddNew($"GF_Perruque_{fid}");
        aa.Race.SetTo(race);
        aa.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Hair, ArmorType = ArmorType.Clothing };
        aa.WorldModel = new GenderedItem<Model?>(new Model { File = wigModel }, new Model { File = wigModel });
        aa.WeightSliderEnabled = new GenderedItem<bool>(wig.WeightSlider, wig.WeightSlider);
        aa.Priority = new GenderedItem<byte>(0, 0);

        Armor skin;
        var allowed = Standalone.Allowed(ctx, race, null);
        if (!npc.WornArmor.IsNull && mod.Armors.TryGetValue(npc.WornArmor.FormKey) is { } ours)
            skin = mod.Armors.DuplicateInAsNewRecord(ours, $"{ours.EditorID}_Perruque_{fid}");    // peau du skin, partagée : copie
        else if (!npc.WornArmor.IsNull && ctx.Cache.TryResolve<IArmorGetter>(npc.WornArmor.FormKey, out var worn))
            skin = mod.Armors.DuplicateInAsNewRecord(worn, $"GF_Peau_Perruque_{fid}");
        else if (ctx.Cache.TryResolve<IArmorGetter>(race.Skin.FormKey, out var raceSkin))
            skin = mod.Armors.DuplicateInAsNewRecord(raceSkin, $"GF_Peau_Perruque_{fid}");
        else
        {
            report.AppendLine($"ATTENTION {row.Id}  peau de la race {race.EditorID} introuvable : perruque non ajoutée");
            mod.ArmorAddons.Remove(aa.FormKey);
            return;
        }
        // follower autonome : seulement des pièces du jeu, de la race ou de notre plugin (comme Skins.Apply)
        var dropped = new List<string>();
        for (int i = skin.Armature.Count - 1; i >= 0; i--)
        {
            var k = skin.Armature[i].FormKey.ModKey;
            if (k != mod.ModKey && !allowed.Contains(k)) { dropped.Add(skin.Armature[i].FormKey.ToString()); skin.Armature.RemoveAt(i); }
        }
        skin.Keywords?.RemoveWhere(k => !allowed.Contains(k.FormKey.ModKey));
        skin.Armature.Add(aa.ToLink());
        skin.BodyTemplate ??= new BodyTemplate();
        skin.BodyTemplate.FirstPersonFlags |= BipedObjectFlag.Hair;
        npc.WornArmor.SetTo(skin);
        report.AppendLine($"PERRUQUE {row.Id}  ARMA {aa.EditorID} (emplacement 31) dans la peau {skin.EditorID}" +
            (dropped.Count > 0 ? $" ; pièces d'autres mods retirées de la peau : {string.Join(", ", dropped)}" : ""));
    }

    /// <summary>
    /// Après la copie des fichiers : dans les .nif de perruque copiés, le .xml et le .tri pointent vers nos copies
    /// (renommage de chaînes de l'en-tête, comme pour le FaceGen). Renvoie un message pour le rapport.
    /// </summary>
    public static string PatchCopies(Wig wig, string modDir, Standalone standalone)
    {
        var missing = wig.Files.Where(f => !standalone.Copied.Contains(Norm(f))).ToList();
        int nifs = 0, renamed = 0, textures = 0, parts = 0;
        // perruque d'auteur : ses .nif copiés (textures déjà réécrites par CopyFiles) ; perruque fabriquée : écrite par
        // BuildWig, ses textures pointent encore vers les originaux
        var targets = wig.Fab != null ? [wig.Model]
            : wig.Files.Where(f => f.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && standalone.Copied.Contains(f)).Select(MovedMesh).ToList();
        foreach (var model in targets)
        {
            var path = Path.Combine(modDir, "meshes", model);
            if (!File.Exists(path)) { missing.Add(model); continue; }
            var bytes = File.ReadAllBytes(path);
            if (wig.Fab != null) { bytes = NifPatcher.ReplaceTextures(bytes, standalone.TextureMap, out var t); textures += t; }
            bytes = HairPartitions(bytes, out var pc);           // toutes les partitions dans l'emplacement 31
            bytes = NifPatcher.PatchStrings(bytes, s => wig.Strings.TryGetValue(s, out var n) ? n : null, out var r);
            File.WriteAllBytes(path, bytes);
            nifs++; renamed += r; parts += pc;
        }
        return $"{nifs} .nif, {renamed} chemins vers nos copies" + (wig.Fab != null ? $", {textures} textures vers nos copies" : "")
            + (parts > 0 ? $", {parts} partitions mises à 31 (Hair)" : "")
            + (missing.Count > 0 ? $" ; ATTENTION non copiés : {string.Join(", ", missing)}" : "");
        // « textures\x.dds » / « meshes\x » tels que Standalone.Copied les range
        static string Norm(string f) => f.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) || f.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? f : @"textures\" + f;
    }

    /// <summary>Chemin relatif à meshes de la copie d'un fichier (sous-dossier des followers autonomes).</summary>
    public static string MovedMesh(string dataRel)
    {
        var p = DataRel(dataRel, true);
        return Standalone.SubFolder + "\\" + p[7..];
    }
}
