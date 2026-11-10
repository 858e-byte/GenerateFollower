using Mutagen.Bethesda;
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
            // pièces créées par le plugin (yeux 3D, sourcils…) portées dans l'export : sans HeadPartUniqueID, le plugin remet
            // au PNJ ses yeux par défaut (Erin 0 : yeux rouges de l'export, pâles en jeu, 09/11)
            var unique = UniqueIds(ctx, row);
            if (unique != null) { lines.Add($"HeadPartUniqueID = {unique}"); copied++; }
            if (File.Exists(preset))
            {
                var other = new List<string>();
                foreach (var raw in File.ReadAllLines(preset))
                {
                    var eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = raw[..eq].Trim();
                    if (unique != null && key.Equals("HeadPartUniqueID", StringComparison.OrdinalIgnoreCase)) continue;
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
    /// Yeux du follower dans notre plugin : copie des yeux par défaut de la race (même maillage 3DEyes.nif que les yeux
    /// créés par le plugin Erin, mêmes .tri, mêmes races), nommée comme la forme de l'export (« GF_ » + nom, pour que le
    /// FaceGen renommé corresponde), texture de l'export ; sa base (« ErinExtra_…_EyeBase ») copiée de même. Le plugin Erin
    /// ne donne pas ses yeux créés en jeu à un PNJ (HeadPartUniqueID ignoré, test Erin 0 du 09/11) : il remettait les yeux
    /// par défaut. null : autre type de pièce, ou forme introuvable dans l'export.
    /// </summary>
    public static IHeadPartGetter? OwnPart(Context ctx, SkyrimMod mod, PresetRow row, IHeadPartGetter def, string shape, HashSet<string> shapes)
    {
        if (def.Type != HeadPart.TypeEnum.Eyes) return null;
        var name = Standalone.Prefix + shape;
        if (mod.HeadParts.FirstOrDefault(h => h.EditorID == name) is { } done) return done;
        if (ExportOf(ctx, row) is not { } exp) return null;
        var mesh = NativeTools.ExportMesh(exp)["shapes"]!.AsArray();
        string? Diffuse(string n) => mesh.FirstOrDefault(s => s?["name"]?.GetValue<string>() == n)?["diffuse"]?.GetValue<string>() is { Length: > 0 } d
            ? (d.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? d[9..] : d) : null;
        if (Diffuse(shape) is not { } diffuse) return null;

        var copy = mod.HeadParts.DuplicateInAsNewRecord(def, name);
        copy.Flags &= ~HeadPart.Flag.Playable;
        if (!def.TextureSet.IsNull && ctx.Cache.TryResolve<ITextureSetGetter>(def.TextureSet.FormKey, out var ts))
        {
            var tex = mod.TextureSets.DuplicateInAsNewRecord(ts, name + "_TXST");
            tex.Diffuse = diffuse;
            copy.TextureSet.SetTo(tex);
        }
        // base des yeux de l'export, du même .ini que les yeux (« ErinEyesF_erin3deyecolorset.ini_… » →
        // « ErinExtra_erin3deyecolorset.ini_…EyeBase*n »)
        var ini = shape[(shape.IndexOf('_') + 1)..shape.IndexOf(".ini_", StringComparison.OrdinalIgnoreCase)];
        var extraShape = shapes.FirstOrDefault(s => s.StartsWith("ErinExtra_" + ini + ".ini_", StringComparison.OrdinalIgnoreCase));
        copy.ExtraParts.Clear();
        foreach (var e in def.ExtraParts)
        {
            if (extraShape != null && ctx.Cache.TryResolve<IHeadPartGetter>(e.FormKey, out var x) && x.Model?.File != null)
            {
                var extra = mod.HeadParts.FirstOrDefault(h => h.EditorID == Standalone.Prefix + extraShape)
                    ?? mod.HeadParts.DuplicateInAsNewRecord(x, Standalone.Prefix + extraShape);
                extra.Flags &= ~HeadPart.Flag.Playable;
                copy.ExtraParts.Add(extra.ToLink());
                extraShape = null;
            }
            else copy.ExtraParts.Add(e.FormKey);
        }
        return copy;
    }

    /// <summary>Export RaceMenu associé au preset (fiche work\headparts\&lt;id&gt;.json), ou null.</summary>
    static string? ExportOf(Context ctx, PresetRow row)
    {
        var fiche = Path.Combine(ctx.WorkDir, "headparts", row.Id + ".json");
        return File.Exists(fiche) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fiche))?["import"]?.GetValue<string>() is { } exp
            && File.Exists(exp) ? exp : null;
    }

    /// <summary>
    /// HeadPartUniqueID du PNJ (format des presets Erin : « 2|ErinEyesF_erin3deyecolorset.ini_Gem_Stone_Eye_03 », type de
    /// headpart puis nom de la pièce créée par le plugin, sans le suffixe « *n ») d'après les formes de l'export RaceMenu
    /// du follower ; plusieurs pièces séparées par des virgules. null : pas d'export ou aucune pièce du plugin.
    /// </summary>
    static string? UniqueIds(Context ctx, PresetRow row)
    {
        var fiche = Path.Combine(ctx.WorkDir, "headparts", row.Id + ".json");
        if (!File.Exists(fiche) || System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fiche))?["import"]?.GetValue<string>() is not { } exp
            || !File.Exists(exp)) return null;
        var parts = NifPatcher.ReadStrings(File.ReadAllBytes(exp))
            .Select(s => (Type: DynamicShapeType(s), Name: s.Contains('*') ? s[..s.LastIndexOf('*')] : s))
            .Where(p => p.Type != null)
            .Select(p => $"{(int)p.Type!.Value}|{p.Name}")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : null;
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
        // Data, ou sous MO2 lu directement overwrite\ et les mods activés
        string[] List(string sex) => ctx.Files.Enumerate(Path.Combine("SKSE", "Plugins", "ErinRace", "Complexion", sex), "*.ec", recursive: false)
            .Select(f => f.Rel).Order(StringComparer.OrdinalIgnoreCase).ToArray();
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
        var path = ctx.PresetPath(row.Preset);
        if (!File.Exists(path) || Jslot.Load(path).HeadTexture is not { } tex || tex.ModKey != Plugin
            || ctx.TryResolveRm<ITextureSetGetter>(tex, out _)) return null;
        var slot = 0xF7FFFE - (row.Female ? male.Length + 1 : 0) - (int)tex.ID;
        if (slot < 0 || slot > list.Length) return null;
        from = slot == 0 ? "peau par défaut" : list[slot - 1];
        return slot;
    }

    /// <summary>
    /// Tête de l'aperçu 3D (JSON de Builder.Mesh) telle que le plugin SKSE la montre en jeu : la tête anime n'a pas de
    /// diffuse dans l'export (le plugin la remplit avec la complexion du preset) et les pièces d'émotion (yeux « tristes »,
    /// larmes, icônes) ne sont affichées qu'avec l'émotion. Copie dans le cache de l'aperçu.
    /// </summary>
    public static string PreviewHead(Context ctx, PresetRow? row, string mesh, string cache)
    {
        var (diffuse, normal, specular) = FaceTextures(ctx, row);
        var outFile = Path.Combine(Context.Dir(Path.Combine(cache, "mesh")),
            Path.GetFileNameWithoutExtension(mesh) + "_erin2_" + Path.GetFileNameWithoutExtension(diffuse) + ".json");
        if (File.Exists(outFile) && File.GetLastWriteTimeUtc(outFile) >= File.GetLastWriteTimeUtc(mesh)) return outFile;
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(mesh))!;
        var shapes = json["shapes"]!.AsArray();
        foreach (var s in shapes.ToList())
        {
            var name = s!["name"]?.GetValue<string>() ?? "";
            // nez anime (texture entièrement noire) : en jeu un petit trait, dans l'aperçu deux barres noires entre les yeux
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^(Female|Male)((EmoEye|Icon)Erin_|AnimeNoseErin)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                shapes.Remove(s);
            else if (s["shader"]?.GetValue<int>() == 4 && string.IsNullOrEmpty(s["diffuse"]?.GetValue<string>()))
            {
                s["diffuse"] = diffuse;
                s["normal"] = normal;
                s["specular"] = specular;
            }
        }
        File.WriteAllText(outFile, json.ToJsonString());
        return outFile;
    }

    /// <summary>JSON d'aperçu (Builder.Mesh) contenant une tête anime Erin (FemaleHeadErin, MaleHeadErin).</summary>
    public static bool HasErinHead(string mesh) => File.ReadAllText(mesh).Contains("HeadErin\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>Textures du visage de la complexion Erin du preset (Complexion\&lt;sexe&gt;\*.ec, section [Head], chemins
    /// sous textures\actors\Erin\) ; sans complexion (ou sans preset : référence affichée) : visage par défaut du mod (face\face1).</summary>
    static (string Diffuse, string? Normal, string? Specular) FaceTextures(Context ctx, PresetRow? row)
    {
        const string Root = @"textures\actors\Erin\";
        string from = "";
        var n = row == null ? null : SkinNumber(ctx, row, PresetIni(ctx, row.Preset), out from);
        var ec = n > 0 ? ctx.Files.PathOf(Path.Combine("SKSE", "Plugins", "ErinRace", "Complexion", row!.Female ? "Female" : "Male", from)) : null;
        if (ec != null && File.Exists(ec))
        {
            var head = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool inHead = false;
            foreach (var raw in File.ReadAllLines(ec))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inHead = line.Equals("[Head]", StringComparison.OrdinalIgnoreCase); continue; }
                var eq = line.IndexOf('=');
                if (inHead && eq > 0) head[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            if (head.TryGetValue("Diffuse", out var d) && d.Length > 0)
                return (Root + d, head.TryGetValue("Normal", out var nm) ? Root + nm : null, head.TryGetValue("Specular", out var sp) ? Root + sp : null);
        }
        return (Root + @"face\face1.dds", Root + @"face\face1_msn.dds", Root + @"face\face1_S.dds");
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
        var dir = Path.Combine("SKSE", "Plugins", "ErinRace", "Preset");
        var name = Path.GetFileName(preset);
        var path = ctx.Files.PathOf(Path.Combine(dir, name + ".ini"));
        var bare = System.Text.RegularExpressions.Regex.Replace(name, @"^\s*[\(\[][^\)\]]*[\)\]]\s*", "");
        return File.Exists(path) || bare.Length == 0 || bare == name ? path : ctx.Files.PathOf(Path.Combine(dir, bare + ".ini"));
    }
}
