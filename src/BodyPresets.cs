using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Preset de corps BodySlide d'un follower (option « corps » de work\options_choice.json), attribué en jeu par OBody NG.
/// - Catalogue : Data\CalienteTools\BodySlide\SliderPresets\*.xml, filtré comme OBody NG 4.4 (règle retrouvée d'après son
///   journal, 744 presets femmes et 95 hommes chez l'utilisateur) : homme si le nom contient himbo, talos, sam, sos ou
///   savren ; femmes : écartés si le nom contient cloth ou outfit (presets « habillés »), et presets dont le set est UNP
///   (unp, coco, bhunp, uunp) réservés aux corps UNP.
/// - En jeu : script GF_FollowerBody (OBodyNative.AssignPresetToActor, une seule fois). La config d'OBody
///   (OBody_presetDistributionConfig.json, un seul fichier pour tout le jeu) n'est jamais écrite.
/// - Aperçu : morphs du .tri BodySlide du corps (BODYTRI du NIF), valeur = curseur du preset entre small et big selon le
///   poids, / 100, comme OBody les donne à BodyMorph. Le corps installé doit être construit à zéro (presets « zero sliders »).
/// </summary>
public static class BodyPresets
{
    public const string Dir = @"CalienteTools\BodySlide\SliderPresets";
    public const string Option = "corps";
    public const string Script = "GF_FollowerBody";

    public sealed record Preset(string Name, string Set, string File, bool Male, bool Unp, bool Clothed,
        Dictionary<string, (float Small, float Big)> Sliders);

    static readonly string[] MaleWords = ["himbo", "talos", "sam", "sos", "savren"];
    static readonly string[] ClothWords = ["cloth", "outfit"];
    static readonly string[] UnpWords = ["unp", "coco", "bhunp", "uunp"];

    static List<Preset>? cache;
    static string? cacheKey;

    /// <summary>Tous les presets (premier fichier par nom, ordre alphabétique des fichiers), relus si le dossier change.</summary>
    public static List<Preset> All(Context ctx)
    {
        var files = ctx.Files.Enumerate(Dir, "*.xml", recursive: false);
        var key = files.Count + "|" + files.Select(f => File.GetLastWriteTimeUtc(f.Path).Ticks).DefaultIfEmpty().Max();
        if (cache != null && cacheKey == key) return cache;
        var list = new List<Preset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, path) in files.OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase))
        {
            XDocument doc;
            try { doc = XDocument.Load(path); } catch { continue; }   // fichier XML abîmé : OBody l'ignore aussi
            foreach (var p in doc.Descendants("Preset"))
            {
                var name = (string?)p.Attribute("name") ?? "";
                if (name.Length == 0 || !seen.Add(name)) continue;
                var set = (string?)p.Attribute("set") ?? "";
                var sliders = new Dictionary<string, (float Small, float Big)>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in p.Elements("SetSlider"))
                {
                    var sn = (string?)s.Attribute("name");
                    if (sn == null || !float.TryParse((string?)s.Attribute("value"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                    var cur = sliders.GetValueOrDefault(sn);
                    sliders[sn] = ((string?)s.Attribute("size")) == "small" ? (v, cur.Big) : (cur.Small, v);
                }
                bool Has(string text, string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
                list.Add(new Preset(name, set, rel, Has(name, MaleWords), Has(set, UnpWords), Has(name, ClothWords), sliders));
            }
        }
        cache = list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        cacheKey = key;
        return cache;
    }

    /// <summary>Presets qu'OBody NG peut donner à ce follower (sexe, corps UNP ou non, pas de preset habillé).</summary>
    public static IEnumerable<Preset> For(Context ctx, PresetRow row)
    {
        bool unp = Skins.BodyOf(ctx, row) == "UNP";
        // comptes d'OBody : les presets « cloth/outfit » ne sont retirés que chez les femmes (95 hommes avec eux)
        return All(ctx).Where(p => p.Male == !row.Female && (!row.Female || !p.Clothed && p.Unp == unp));
    }

    /// <summary>Valeurs BodyMorph du preset pour un poids (0 à 100) : curseur entre small et big, / 100.</summary>
    public static Dictionary<string, float> Values(Preset p, float weight) =>
        p.Sliders.ToDictionary(kv => kv.Key, kv => (kv.Value.Small + (kv.Value.Big - kv.Value.Small) * weight / 100f) / 100f, StringComparer.OrdinalIgnoreCase);

    /// <summary>Fiche du Builder : presets proposés, choix, et si OBody NG est installé.</summary>
    public static JsonObject Info(Context ctx, PresetRow row)
    {
        var choice = Options.For(ctx, row.Id)[Option]?.GetValue<string>();
        var list = For(ctx, row).ToList();
        return new JsonObject
        {
            ["choix"] = choice,
            ["choixInconnu"] = choice != null && !list.Any(p => p.Name.Equals(choice, StringComparison.OrdinalIgnoreCase)),
            ["obody"] = ctx.ActivePlugins.Contains("OBody.esp"),
            ["presets"] = new JsonArray(list.Select(p => (JsonNode)new JsonObject { ["nom"] = p.Name, ["set"] = p.Set, ["curseurs"] = p.Sliders.Count }).ToArray()),
        };
    }

    /// <summary>Enregistre le choix (null : OBody choisit au hasard, comme avant).</summary>
    public static void Save(Context ctx, PresetRow row, JsonNode? value)
    {
        var name = value?.GetValue<string>();
        if (!string.IsNullOrEmpty(name) && !For(ctx, row).Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new GfException(L.T("Preset de corps inconnu ou incompatible avec ce follower : {0}", name));
        Options.Set(ctx, row.Id, Option, string.IsNullOrEmpty(name) ? null : JsonValue.Create(name));
    }

    /// <summary>Script du PNJ qui fait attribuer le preset choisi par OBody NG, ou null sans choix.</summary>
    public static ScriptEntry? ScriptFor(Context ctx, PresetRow row, JsonObject opts, StringBuilder report)
    {
        if (opts[Option]?.GetValue<string>() is not { Length: > 0 } name) return null;
        if (!For(ctx, row).Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            report.AppendLine($"ATTENTION {row.Id}  preset de corps « {name} » absent de {Dir} chez toi : OBody en choisira un au hasard");
        if (!ctx.ActivePlugins.Contains("OBody.esp"))
            report.AppendLine($"ATTENTION {row.Id}  OBody NG n'est pas actif : preset de corps « {name} » sans effet en jeu");
        report.AppendLine($"CORPS   {row.Id}  preset « {name} » attribué par OBody NG au premier chargement (config d'OBody non modifiée)");
        return new ScriptEntry { Name = Script, Properties = { new ScriptStringProperty { Name = "BodyPreset", Data = name } } };
    }

    // ------------------------------------------------------------------ .tri BodySlide (aperçu)

    /// <summary>Morphs d'un .tri BodySlide (« PIRT ») : forme → morph → (index du sommet, décalage). Les décalages sont des
    /// entiers 16 bits × le multiplicateur du morph. La section des morphs d'UV qui suit est ignorée.</summary>
    public static Dictionary<string, Dictionary<string, List<(int Index, float X, float Y, float Z)>>> ReadTri(string path)
    {
        var shapes = new Dictionary<string, Dictionary<string, List<(int, float, float, float)>>>(StringComparer.OrdinalIgnoreCase);
        using var r = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "PIRT") throw new GfException($"fichier .tri BodySlide inattendu : {path}");
        string Str() => Encoding.Latin1.GetString(r.ReadBytes(r.ReadByte()));
        int shapeCount = r.ReadUInt16();
        for (int i = 0; i < shapeCount; i++)
        {
            var morphs = new Dictionary<string, List<(int, float, float, float)>>(StringComparer.OrdinalIgnoreCase);
            shapes[Str()] = morphs;
            int morphCount = r.ReadUInt16();
            for (int m = 0; m < morphCount; m++)
            {
                var name = Str();
                var mult = r.ReadSingle();
                int n = r.ReadUInt16();
                var verts = new List<(int, float, float, float)>(n);
                for (int v = 0; v < n; v++) verts.Add((r.ReadUInt16(), r.ReadInt16() * mult, r.ReadInt16() * mult, r.ReadInt16() * mult));
                morphs[name] = verts;
            }
        }
        return shapes;
    }

    /// <summary>Chemin du .tri de morphs d'un NIF (NiStringExtraData « BODYTRI », relatif à meshes\), ou null.</summary>
    public static string? BodyTri(Context ctx, string nif)
    {
        var tri = Nif.Load(nif).File.Blocks.OfType<NiflySharp.Blocks.NiStringExtraData>()
            .FirstOrDefault(b => string.Equals(b.Name?.String, "BODYTRI", StringComparison.OrdinalIgnoreCase))?.StringData?.String;
        return string.IsNullOrEmpty(tri) ? null : ctx.FindMesh(tri);
    }
}
