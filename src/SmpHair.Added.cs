using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Coiffure ajoutée par le Builder (option « coiffure », Options.Hairs ; plan work\diag\coiffure\PLAN.md, étape 3) : la
/// coiffure choisie est portée comme une perruque, par le même mécanisme que les cheveux SMP (ARMA emplacement 31 dans une
/// copie de la peau du PNJ, <see cref="Apply"/> ; fichiers copiés et chemins réécrits par <see cref="PatchCopies"/>).
/// Le FaceGen n'est jamais réécrit : si l'export a déjà une coiffure, ses formes sont seulement cachées (drapeau) et la
/// coiffure de la fiche est vidée, comme pour une perruque SMP.
/// - « perruque » (pièce d'armure d'un mod) : son modèle tel quel (variantes _0/_1, .xml et .tri de physique compris).
/// - « headpart » (coiffure ordinaire) : perruque fabriquée depuis le .nif du headpart et de ses pièces annexes ; si la
///   coiffure a une physique SMP (.xml), elle la garde.
/// </summary>
public static partial class SmpHair
{
    /// <summary>
    /// Formes de la coiffure dans l'export (noms d'origine, avant renommage) : celles des headparts de type Hair de la
    /// fiche du PNJ, pièces annexes comprises. <paramref name="map"/> : nom dans l'export → EditorID dans notre plugin.
    /// </summary>
    public static HashSet<string> ExportHairShapes(Context ctx, SkyrimMod mod, Npc npc, IReadOnlyDictionary<string, string> map, byte[] export)
    {
        IHeadPartGetter? part(FormKey key) => mod.HeadParts.FirstOrDefault(h => h.FormKey == key)
            ?? (ctx.Cache.TryResolve<IHeadPartGetter>(key, out var hp) ? hp : null);
        var edids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void walk(IHeadPartGetter h, int depth)
        {
            if (h.EditorID != null && h.Model?.File != null) edids.Add(h.EditorID);
            if (depth < 4) foreach (var x in h.ExtraParts) if (part(x.FormKey) is { } e) walk(e, depth + 1);
        }
        foreach (var link in npc.HeadParts)
            if (part(link.FormKey) is { } hp && hp.Type == HeadPart.TypeEnum.Hair) walk(hp, 0);
        var hide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shape in Raw.Parse(export)?.Shapes() ?? [])
            if (edids.Contains(shape) || map.TryGetValue(shape, out var ours) && edids.Contains(ours)) hide.Add(shape);
        return hide;
    }

    /// <summary>
    /// Perruque pour la coiffure choisie, ou null (fichier introuvable : dit dans le rapport, le follower garde sa tête
    /// telle quelle). <paramref name="hide"/> : formes de l'export à cacher (<see cref="ExportHairShapes"/>).
    /// <paramref name="madeModel"/> : chemin (relatif à meshes) de la perruque fabriquée.
    /// </summary>
    public static Wig? FromChoice(Context ctx, PresetRow row, Options.HairChoice choice, HashSet<string> hide, StringBuilder report, string madeModel)
    {
        var what = $"COIFFURE {row.Id}  « {choice.Name} » ({choice.Mod})";
        return choice.Type == "perruque" ? FromArmor(ctx, row, choice, hide, report, what) : FromHeadPart(ctx, row, choice, hide, report, what, madeModel);
    }

    /// <summary>Fichiers .nif (chemins complets) de la coiffure choisie, pour l'aperçu 3D : le modèle de la perruque
    /// d'armure, ou ceux du headpart et de ses pièces annexes. Liste vide si rien n'est trouvé.</summary>
    public static List<string> ChoiceModels(Context ctx, PresetRow row, Options.HairChoice choice)
    {
        if (choice.Type == "perruque")
            return ctx.Cache.TryResolve<IArmorGetter>(choice.Key, out var armor) && WigModel(ctx, row, armor).Model is { } m && ctx.FindFile(m) is { } p ? [p] : [];
        if (!ctx.Cache.TryResolve<IHeadPartGetter>(choice.Key, out var hp)) return [];
        return PartsOf(ctx, [hp]).SelectMany(x => x.Models).Where(x => x.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            .Select(ctx.FindMesh).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static Wig? FromArmor(Context ctx, PresetRow row, Options.HairChoice choice, HashSet<string> hide, StringBuilder report, string what)
    {
        if (!ctx.Cache.TryResolve<IArmorGetter>(choice.Key, out var armor)) { report.AppendLine(L.T("ATTENTION {0} : pièce d'armure introuvable, coiffure non ajoutée", what)); return null; }
        var (model, alternate) = WigModel(ctx, row, armor);
        if (model == null || ctx.FindFile(model) is not { } path || Raw.Read(path) is not { } raw)
        {
            report.AppendLine(L.T("ATTENTION {0} : modèle de la perruque introuvable ({1}), coiffure non ajoutée", what, model ?? L.T("aucun")));
            return null;
        }
        // variantes de poids _0 / _1, comme les perruques SMP d'auteur
        bool slider = false;
        var files = new List<string>();
        var m01 = Regex.Match(model, @"^(.*)_([01])\.nif$", RegexOptions.IgnoreCase);
        if (m01.Success && ctx.FindFile(m01.Groups[1].Value + "_0.nif") != null && ctx.FindFile(m01.Groups[1].Value + "_1.nif") != null)
        {
            slider = true;
            model = m01.Groups[1].Value + "_1.nif";
            files.Add(m01.Groups[1].Value + "_0.nif");
        }
        files.Add(model);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        string hairXml = "";
        foreach (var (name, value) in raw.Extra())
        {
            if (name == Key && ctx.FindFile(DataRel(value)) != null) { hairXml = DataRel(value); files.Add(hairXml); strings[value] = @"meshes\" + MovedMesh(hairXml); }
            else if (name == TriKey && ctx.FindFile(DataRel(value, true)) != null) { strings[value] = MovedMesh(DataRel(value, true)); files.Add(DataRel(value, true)); }
        }
        report.AppendLine(L.T("{0} -> perruque {1}{2}, {3} formes", what, model, slider ? " (_0/_1)" : "", raw.Shapes().Count()) + (hairXml != "" ? L.T(", physique {0}", hairXml) : "")
            + L.T(" ; FaceGen : {0} formes cachées", hide.Count) + (hide.Count > 0 ? $" ({string.Join(", ", hide.Order())})" : ""));
        if (alternate)
            report.AppendLine(L.T("ATTENTION {0} : cette pièce change ses textures par l'armure (variante de couleur) : la perruque aura les textures de son .nif", what));
        return new Wig(hairXml, model, MovedMesh(model), slider, hide, files, strings);
    }

    /// <summary>Modèle (relatif à Data) de la perruque d'armure pour ce follower, et « ses textures sont changées par
    /// l'armure ».</summary>
    static (string? Model, bool Alternate) WigModel(Context ctx, PresetRow row, IArmorGetter armor)
    {
        // Une armure a souvent plusieurs ARMA (une par groupe de races : la « perruque » KSSMP de BBLS commence par un
        // casque pour Khajiits). On prend celle de la race du follower : race exacte, sinon race d'origine d'une race
        // dérivée (« 00UBE_NordRace » → « NordRace »), sinon DefaultRace, sinon la première.
        var race = ctx.Find<IRaceGetter>(row.Race);
        string raceName = race?.EditorID ?? row.Race;
        int Score(IArmorAddonGetter aa)
        {
            var keys = new[] { aa.Race.FormKey }.Concat(aa.AdditionalRaces.Select(r => r.FormKey)).ToList();
            if (race != null && keys.Contains(race.FormKey)) return 3;
            var names = keys.Select(k => ctx.Cache.TryResolve<IRaceGetter>(k, out var r) ? r.EditorID ?? "" : "").ToList();
            if (names.Any(n => n.Length > 4 && n != "DefaultRace" && raceName.Length > n.Length && raceName.EndsWith(n, StringComparison.OrdinalIgnoreCase))) return 2;
            return names.Contains("DefaultRace") ? 1 : 0;
        }
        string? model = null;
        bool alternate = false;
        int best = -1;
        foreach (var link in armor.Armature)
        {
            if (!ctx.Cache.TryResolve<IArmorAddonGetter>(link.FormKey, out var aa)) continue;
            var m = (row.Female ? aa.WorldModel?.Female : aa.WorldModel?.Male) ?? aa.WorldModel?.Female ?? aa.WorldModel?.Male;
            if (m?.File == null || Score(aa) is var score && score <= best) continue;
            best = score;
            model = DataRel(m.File.DataRelativePath.Path, true);
            alternate = m.AlternateTextures is { Count: > 0 };
        }
        // fiche qui cite « x_1.nif » alors que le mod livre « x.nif » (KS Wigs vus par le patch BBLS) : on prend ce qui existe
        if (model != null && ctx.FindFile(model) == null && Regex.Match(model, @"^(.*)_[01]\.nif$", RegexOptions.IgnoreCase) is { Success: true } plain
            && ctx.FindFile(plain.Groups[1].Value + ".nif") != null)
            model = plain.Groups[1].Value + ".nif";
        return (model, alternate);
    }

    static Wig? FromHeadPart(Context ctx, PresetRow row, Options.HairChoice choice, HashSet<string> hide, StringBuilder report, string what, string madeModel)
    {
        if (!ctx.Cache.TryResolve<IHeadPartGetter>(choice.Key, out var hp)) { report.AppendLine(L.T("ATTENTION {0} : coiffure introuvable, coiffure non ajoutée", what)); return null; }
        // pièces de la coiffure : (EditorID du headpart, .nif, formes, .xml de physique portés)
        var pieces = new List<(string Face, string Path, List<NifShape> Shapes, List<string> Xml)>();
        foreach (var (face, models) in PartsOf(ctx, [hp]))
            foreach (var model in models.Where(x => x.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)))
            {
                if (ctx.FindMesh(model) is not { } path || pieces.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                try { pieces.Add((face, path, Nif.Load(path).Shapes, (Raw.Read(path)?.Extra() ?? []).Where(x => x.Name == Key).Select(x => DataRel(x.Value)).ToList())); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            }
        if (pieces.Count == 0) { report.AppendLine(L.T("ATTENTION {0} : aucun .nif de cette coiffure trouvé, coiffure non ajoutée", what)); return null; }

        // .xml de la coiffure : celui qui cite le plus d'os (écarte la physique des collisions du sol)
        var hairXml = pieces.SelectMany(p => p.Xml).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => (Xml: x, Path: ctx.FindFile(x))).Where(x => x.Path != null)
            .Select(x => (x.Xml, Bones: XmlBones(x.Path!).Bones.Count)).OrderByDescending(x => x.Bones).Select(x => x.Xml).FirstOrDefault() ?? "";
        var xmlShapes = hairXml != "" && ctx.FindFile(hairXml) is { } xp ? XmlShapes(xp) : [];
        // base : la pièce qui porte le .xml (hiérarchie d'os et donnée de physique), sinon la première avec une forme texturée
        var basePiece = pieces.FirstOrDefault(p => hairXml != "" && p.Xml.Contains(hairXml, StringComparer.OrdinalIgnoreCase));
        if (basePiece.Path == null) basePiece = pieces.FirstOrDefault(p => p.Shapes.Any(s => s.Slot(0).Length > 0));
        if (basePiece.Path == null) basePiece = pieces[0];

        var files = new List<string>();
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hairXml != "") files.Add(hairXml);
        void Collect((string Face, string Path, List<NifShape> Shapes, List<string> Xml) piece)
        {
            foreach (var s in piece.Shapes)
                for (int i = 0; i < 9; i++)
                    if (s.Slot(i) is { Length: > 0 } t) files.Add(t);
            foreach (var (n, v) in Raw.Read(piece.Path)?.Extra() ?? [])
            {
                if (n == Key && hairXml != "") strings[v] = @"meshes\" + MovedMesh(hairXml);
                else if (n == TriKey && ctx.FindFile(DataRel(v, true)) != null) { strings[v] = MovedMesh(DataRel(v, true)); files.Add(DataRel(v, true)); }
            }
        }
        Collect(basePiece);
        var names = basePiece.Shapes.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(string Path, string Shape, string Name)>();
        foreach (var piece in pieces.Where(p => p.Path != basePiece.Path))
        {
            bool added = false;
            foreach (var s in piece.Shapes)
            {
                // nom attendu par le .xml (collisions citées sous le nom du headpart), sinon celui du mesh
                var name = xmlShapes.Contains(s.Name) ? s.Name : xmlShapes.Contains(piece.Face) ? piece.Face : s.Name;
                // une forme sans texture qui n'est pas citée par la physique est une collision d'une autre physique : laissée
                if (!xmlShapes.Contains(name) && s.Slot(0).Length == 0) continue;
                // même nom de forme dans deux pièces (KS : « 911 » et sa ligne de cheveux « 911Hl », toutes deux
                // « s4studio_mesh_4 ») : la seconde prend le nom de son headpart, comme dans un export RaceMenu
                if (!names.Add(name) && (xmlShapes.Contains(name) || !names.Add(name = piece.Face))) continue;
                plan.Add((piece.Path, s.Name, name));
                added = true;
            }
            if (added) Collect(piece);
        }
        report.AppendLine(L.T("{0} -> perruque fabriquée {1} : base {2}", what, madeModel, ctx.Files.RelOf(basePiece.Path))
            + (plan.Count > 0 ? " + " + string.Join(", ", plan.Select(p => p.Name)) : "") + (hairXml != "" ? L.T(", physique {0}", hairXml) : L.T(", sans physique"))
            + L.T(" ; FaceGen : {0} formes cachées", hide.Count) + (hide.Count > 0 ? $" ({string.Join(", ", hide.Order())})" : ""));
        return new Wig(hairXml, ctx.Files.RelOf(basePiece.Path), madeModel, false, hide, files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            strings, new Fabrication(basePiece.Path, plan));
    }
}
