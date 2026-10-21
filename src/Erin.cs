using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Race Erin (mod « Erin Race », race anime : ErinRace.esm + plugin SKSE MuErinRace.dll). Particularités :
/// - le plugin SKSE crée en jeu des headparts (yeux 3D, sourcils, cicatrices…) décrits dans SKSE\Plugins\ErinRace\HeadPart\*.ini :
///   le preset les cite avec une clé d'ErinRace.esm qui n'existe dans aucun plugin (ex. C7FFC1:ErinRace.esm) ; leur forme est
///   dans l'export RaceMenu (FaceGen), le plugin SKSE les remet en jeu ;
/// - tête anime propre (FemaleHeadErin, textures actors\Erin\face) : la texture du visage n'est jamais remplacée par un skin ;
/// - corps CBBE 3BA propre à la race (Meshes\Actors\Erin, textures actors\Erin) : un skin CBBE peut le remplacer (mêmes UV).
/// </summary>
public static class Erin
{
    public static readonly ModKey Plugin = ModKey.FromFileName("ErinRace.esm");

    /// <summary>ErinRace, ErinRaceVampire…</summary>
    public static bool IsRace(string? editorId) => editorId != null && editorId.StartsWith("ErinRace", StringComparison.OrdinalIgnoreCase);

    /// <summary>Headpart créé en jeu par le plugin SKSE d'Erin Race (absent de tout plugin), pour un preset de race Erin.</summary>
    public static bool IsDynamicHeadPart(Context ctx, FormKey key, string? race) =>
        IsRace(race) && key.ModKey == Plugin && !ctx.TryResolveRm<IHeadPartGetter>(key, out _);

    /// <summary>Type d'une forme de l'export RaceMenu créée par le plugin SKSE d'Erin Race (nom « &lt;préfixe&gt;_&lt;fichier&gt;.ini_&lt;nom&gt; »,
    /// ex. ErinEyesF_erin3deyecolorset.ini_Gem_Stone_Eye_06*173), d'après le fichier .ini qui la décrit ; null sinon.</summary>
    public static HeadPart.TypeEnum? DynamicShapeType(string shape)
    {
        var i = shape.IndexOf(".ini_", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var ini = shape[..i].ToLowerInvariant();
        // la base des yeux (ErinExtra_…_EyeBase) est une pièce extra des yeux, pas des yeux
        if (shape.StartsWith("ErinExtra", StringComparison.OrdinalIgnoreCase)) return null;
        return ini.Contains("eye") ? HeadPart.TypeEnum.Eyes : ini.Contains("brow") ? HeadPart.TypeEnum.Eyebrows
            : ini.Contains("scar") ? HeadPart.TypeEnum.Scars : null;
    }

    /// <summary>Réglages du preset Erin (SKSE\Plugins\ErinRace\Preset\&lt;nom&gt;.ini) repris tels quels dans la config du PNJ :
    /// clés de même format dans les deux fichiers (comparées à ErinRace\example1234.ini et aux chaînes du plugin SKSE).</summary>
    static readonly Dictionary<string, string> SameKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EarsReversed"] = "EarsReversed", ["TailReversed"] = "TailReversed",
        ["VisibleHeadpart"] = "VisibleHeadPart", ["HeadPartAdjust"] = "HeadPartAdjust",
    };

    /// <summary>
    /// Config Erin de chaque follower Erin : SKSE\Plugins\ErinRace\GF_&lt;plugin&gt;_&lt;id&gt;.ini (PluginName, ActorBaseID), lue par
    /// le plugin SKSE d'Erin Race pour ce PNJ (sans elle : config du « faceNPC » ou config par défaut). Réglages du preset
    /// repris quand leur format est le même ; les autres (teintes, skin, headparts créés en jeu) sont gardés en
    /// commentaire en attendant de connaître leur format pour un PNJ (commande console du plugin : actorconfig).
    /// </summary>
    public static void WriteActorConfigs(Context ctx, IEnumerable<(PresetRow Row, Npc Npc)> built, string plugin, string modDir, System.Text.StringBuilder report)
    {
        foreach (var (row, npc) in built.Where(b => IsRace(b.Row.Race)))
        {
            var id = npc.FormKey.ID;
            var lines = new List<string>
            {
                $"# GenerateFollower: Erin Race config of the follower {row.Nom} (preset {row.Preset})",
                $"PluginName = {plugin}",
                $"ActorBaseID = 0x{id:X}",
            };
            var preset = PresetIni(ctx, row.Preset);
            int copied = 0, kept = 0;
            if (File.Exists(preset))
            {
                var other = new List<string>();
                foreach (var raw in File.ReadAllLines(preset))
                {
                    var eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = raw[..eq].Trim();
                    if (SameKeys.TryGetValue(key, out var npcKey)) { lines.Add($"{npcKey} = {raw[(eq + 1)..].Trim()}"); copied++; }
                    else { other.Add("# " + raw.Trim()); kept++; }
                }
                if (other.Count > 0)
                {
                    lines.Add("");
                    lines.Add("# Other settings of the Erin preset (NPC format not confirmed yet):");
                    lines.AddRange(other);
                }
            }
            var skin = SkinNumber(ctx, row, preset, out var skinFrom);
            if (skin != null)
            {
                lines.Add("");
                lines.Add($"# Erin complexion of the preset ({skinFrom}); 0 = default skin");
                lines.Add($"SkinNumber = {skin}");
            }
            var dir = Context.Dir(Path.Combine(modDir, "SKSE", "Plugins", "ErinRace"));
            var file = $"{Path.GetFileNameWithoutExtension(plugin)}_{id:X}.ini";
            // ASCII seulement (lecteur .ini du plugin SKSE) : les commentaires sont en anglais
            File.WriteAllLines(Path.Combine(dir, file), lines, System.Text.Encoding.ASCII);
            report.AppendLine($"ERIN    {row.Id}  config Erin Race : SKSE\\Plugins\\ErinRace\\{file} ("
                + (File.Exists(preset) ? $"{copied} réglage(s) du preset {Path.GetFileName(preset)} repris, {kept} en commentaire" : "pas de preset Erin : config par défaut")
                + (skin != null ? $", skin Erin {skin} : {skinFrom}" : ", skin Erin par défaut") + ")");
        }
    }

    /// <summary>
    /// Complexion Erin du preset pour SkinNumber (curseur Complexion du plugin : 0 = peau par défaut, n = n-ième fichier de
    /// SKSE\Plugins\ErinRace\Complexion\&lt;Female|Male&gt;\*.ec dans l'ordre des noms ; validé en jeu sur Ruby). D'après le SkinID
    /// du preset Erin, sinon d'après la texture de tête du jslot : le plugin crée en jeu une TXST par complexion
    /// (ErinRace.esm F7FFFE en descendant : hommes, défaut compris, puis femmes ; absentes de tout plugin). null : inconnue.
    /// </summary>
    static int? SkinNumber(Context ctx, PresetRow row, string presetIni, out string from)
    {
        from = "";
        var dir = Path.Combine(ctx.DataDir, "SKSE", "Plugins", "ErinRace", "Complexion");
        string[] List(string sex) => Directory.Exists(Path.Combine(dir, sex))
            ? Directory.GetFiles(Path.Combine(dir, sex), "*.ec").Select(f => Path.GetFileName(f)).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        var male = List("Male");
        var list = row.Female ? List("Female") : male;
        if (File.Exists(presetIni))
            foreach (var raw in File.ReadAllLines(presetIni))
            {
                var eq = raw.IndexOf('=');
                if (eq <= 0 || !raw[..eq].Trim().Equals("SkinID", StringComparison.OrdinalIgnoreCase)) continue;
                var i = Array.FindIndex(list, f => f.Equals(raw[(eq + 1)..].Trim(), StringComparison.OrdinalIgnoreCase));
                if (i >= 0) { from = list[i]; return i + 1; }
            }
        var path = Path.Combine(ctx.PresetsDir, row.Preset + ".jslot");
        if (!File.Exists(path) || Jslot.Load(path).HeadTexture is not { } tex || tex.ModKey != Plugin
            || ctx.TryResolveRm<ITextureSetGetter>(tex, out _)) return null;
        var slot = 0xF7FFFE - (row.Female ? male.Length + 1 : 0) - (int)tex.ID;
        if (slot < 0 || slot > list.Length) return null;
        from = slot == 0 ? "peau par défaut" : list[slot - 1];
        return slot;
    }

    /// <summary>
    /// Teinte du visage (DDS non compressé 32 bits de l'export RaceMenu) divisée par la couleur de peau du preset : la peau
    /// d'une Erin vient de son plugin SKSE, une couleur de peau cuite dans la teinte grise la tête anime. Copie dans
    /// work\tint_tmp ; null si le format n'est pas pris en charge.
    /// </summary>
    public static string? NeutralTint(Context ctx, string src, int tone)
    {
        var d = File.ReadAllBytes(src);
        if (d.Length < 128 || BitConverter.ToUInt32(d, 0) != 0x20534444 || BitConverter.ToUInt32(d, 88) != 32
            || (BitConverter.ToUInt32(d, 80) & 0x40) == 0) return null;     // « DDS », 32 bits, RGB non compressé
        int Shift(uint mask) => mask switch { 0xFF => 0, 0xFF00 => 8, 0xFF0000 => 16, 0xFF000000 => 24, _ => -1 };
        int r = Shift(BitConverter.ToUInt32(d, 92)), g = Shift(BitConverter.ToUInt32(d, 96)), b = Shift(BitConverter.ToUInt32(d, 100));
        if (r < 0 || g < 0 || b < 0) return null;
        var factors = new[] { (r / 8, (tone >> 16) & 0xFF), (g / 8, (tone >> 8) & 0xFF), (b / 8, tone & 0xFF) };
        for (int p = 128; p + 4 <= d.Length; p += 4)
            foreach (var (offset, c) in factors)
                if (c > 0) d[p + offset] = (byte)Math.Min(255, d[p + offset] * 255 / c);
        var dst = Path.Combine(Context.Dir(Path.Combine(ctx.WorkDir, "tint_tmp")), "erin_" + Path.GetFileName(src));
        File.WriteAllBytes(dst, d);
        return dst;
    }

    /// <summary>ErinRace\Preset\&lt;nom&gt;.ini du preset ; sinon sans préfixe entre parenthèses (« (Erin) Suzu » → Suzu.ini).</summary>
    static string PresetIni(Context ctx, string preset)
    {
        var dir = Path.Combine(ctx.DataDir, "SKSE", "Plugins", "ErinRace", "Preset");
        var name = Path.GetFileName(preset);
        var path = Path.Combine(dir, name + ".ini");
        var bare = System.Text.RegularExpressions.Regex.Replace(name, @"^\s*[\(\[][^\)\]]*[\)\]]\s*", "");
        return File.Exists(path) || bare.Length == 0 || bare == name ? path : Path.Combine(dir, bare + ".ini");
    }
}
