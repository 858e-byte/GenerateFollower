using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;

namespace GenerateFollower;

public record BodyMorph(string Name, string Key, float Value);

/// <summary>Valeur d'overlay RaceMenu (overrides[]) : nœud « Body [Ovl0] »…, clé NiOverride (7 couleur, 8 alpha,
/// 9 texture…), index (slot de texture pour la clé 9, -1 sinon), valeur brute du JSON.</summary>
public record OverrideValue(string Node, int Key, int Index, JsonNode? Data);

/// <summary>Contenu utile d'un preset RaceMenu (.jslot, formatVersion 3).</summary>
public class Jslot
{
    public List<string> Mods { get; } = [];
    /// <summary>(FormKey, type RaceMenu) : 0 misc, 1 face, 2 eyes, 3 hair, 4 facial hair, 5 scar, 6 brows.</summary>
    public List<(FormKey Key, int Type)> HeadParts { get; } = [];
    public int? HairColor { get; private set; }
    public FormKey? HeadTexture { get; private set; }
    public float Weight { get; private set; }
    public List<float> FaceMorphs { get; } = [];
    public List<int> FaceParts { get; } = [];
    public int? SkinTone { get; private set; }
    public List<BodyMorph> BodyMorphs { get; } = [];
    public List<OverrideValue> Overrides { get; } = [];

    public static Jslot Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new GfException($"jslot vide : {path}");
        var j = new Jslot();
        foreach (var m in root["mods"]?.AsArray() ?? [])
            if (m?["name"]?.GetValue<string>() is { } n) j.Mods.Add(n);

        foreach (var hp in root["headParts"]?.AsArray() ?? [])
        {
            var key = ParseFormKey(hp?["formIdentifier"]?.GetValue<string>());
            if (key != null) j.HeadParts.Add((key.Value, Int(hp?["type"])));
        }

        var actor = root["actor"];
        if (actor?["hairColor"] is { } hc) j.HairColor = Int(hc);
        j.HeadTexture = ParseFormKey(actor?["headTexture"]?.GetValue<string>());
        j.Weight = actor?["weight"] is { } w ? (float)w.GetValue<double>() : 50f;

        var def = root["morphs"]?["default"];
        foreach (var v in def?["morphs"]?.AsArray() ?? []) j.FaceMorphs.Add(v == null ? 0 : (float)v.GetValue<double>());
        foreach (var v in def?["presets"]?.AsArray() ?? []) j.FaceParts.Add(Int(v));

        // Couleur de peau : couche dont le masque est SkinTone.dds (les jslot n'ont pas de champ "type").
        // Comme pour la teinte du visage, elle recouvre un gris neutre (128) selon son alpha.
        foreach (var t in root["tintInfo"]?.AsArray() ?? [])
        {
            var tex = t?["texture"]?.GetValue<string>() ?? "";
            if (!tex.EndsWith("skintone.dds", StringComparison.OrdinalIgnoreCase) || t!["color"] is not { } c) continue;
            int argb = Int(c);
            double a = ((argb >> 24) & 0xFF) / 255.0;
            int Mix(int shift) => (int)Math.Round(128 * (1 - a) + ((argb >> shift) & 0xFF) * a);
            j.SkinTone = (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
            break;
        }

        foreach (var bm in root["bodyMorphs"]?.AsArray() ?? [])
        {
            var name = bm?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || NotAShape(name)) continue;
            foreach (var k in bm!["keys"]?.AsArray() ?? [])
            {
                var value = k?["value"] is { } kv ? (float)kv.GetValue<double>() : 0f;
                if (Math.Abs(value) > 1e-4 && k?["key"]?.GetValue<string>() is { } key)
                    j.BodyMorphs.Add(new BodyMorph(name, key, value));
            }
        }
        foreach (var o in root["overrides"]?.AsArray() ?? [])
        {
            var node = o?["node"]?.GetValue<string>();
            if (string.IsNullOrEmpty(node)) continue;
            foreach (var v in o!["values"]?.AsArray() ?? [])
                if (v != null) j.Overrides.Add(new OverrideValue(node, Int(v["key"]), v["index"] is { } ix ? Int(ix) : -1, v["data"]?.DeepClone()));
        }
        return j;
    }

    /// <summary>
    /// Valeurs rangées dans les body morphs du joueur sans être des formes : styles d'animation XPMSE,
    /// positions d'armes (RMW*), marqueur OBody.
    /// </summary>
    static bool NotAShape(string name) =>
        name.StartsWith("XPMSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RMW", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("obody_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Entier JSON, éventuellement écrit en flottant ou hors plage int32 (couleurs ARGB).</summary>
    static int Int(JsonNode? n) => n == null ? 0 : unchecked((int)(long)n.GetValue<double>());

    /// <summary>"Skyrim.esm|05150F" -> FormKey.</summary>
    public static FormKey? ParseFormKey(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var bar = s.LastIndexOf('|');
        if (bar <= 0) return null;
        if (!uint.TryParse(s[(bar + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var id)) return null;
        if (!ModKey.TryFromFileName(s[..bar], out var mod)) return null;
        return new FormKey(mod, id & 0xFFFFFF);
    }
}
