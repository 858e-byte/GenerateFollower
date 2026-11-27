using System.Globalization;
using System.Text;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace GenerateFollower;

/// <summary>
/// Transformations d'os RaceMenu (échelle de la tête, du buste, des jambes, position des oreilles…, transforms[] du jslot) :
/// rangées dans les propriétés du script GF_FollowerSkeleton, attaché au PNJ, qui les applique au chargement par NiOverride
/// (AddNodeTransform* puis UpdateNodeTransform), comme RaceMenu pour le joueur. Sans elles, la tête et le corps du
/// follower gardent les proportions de la race (erina-tokki : tête ×1,3 mais colonne ×0,8 et ×0,9).
/// </summary>
public static class Skeleton
{
    public const string Script = "GF_FollowerSkeleton";
    const int ScaleType = 30, PositionType = 31, RotationType = 32;
    // Tableaux de propriétés remplis par le plugin : la limite de 128 éléments de Papyrus ne vaut que pour les tableaux
    // créés par « new » dans un script. Plafond de 128 levé le 14/11 (49 presets sur 416 le dépassaient chez l'auteur :
    // réglages XPMSE perdus, signalement d'un joueur) ; garde-fou large contre un preset aberrant.
    const int MaxArray = 1024;

    /// <summary>Script GF_FollowerSkeleton du PNJ, ou null s'il n'a aucune transformation utile.</summary>
    /// <summary>Os qui portent le raccord tête / corps : leurs réglages viennent du preset dont vient la tête.</summary>
    static bool IsNeckNode(string node) =>
        node.Contains("Neck", StringComparison.OrdinalIgnoreCase) || node.StartsWith("NPC Head", StringComparison.OrdinalIgnoreCase);

    /// <param name="headOwner">preset dont vient la tête quand ce n'est pas celui du follower (HeadWrite.HeadOwner) : ses
    /// réglages d'os du cou et de la tête et ses morphs « __HEAD » remplacent ceux du preset, car l'export a été fait avec
    /// eux (Ruvi ← Kaiba, ligne au cou malgré le bon poids, 17/11)</param>
    public static ScriptEntry? For(Jslot j, PresetRow row, StringBuilder report, (PresetRow Row, Jslot J)? headOwner = null)
    {
        var transforms = j.Transforms;
        var morphSource = j;
        if (headOwner is { } o)
        {
            transforms = j.Transforms.Where(t => !IsNeckNode(t.Node)).Concat(o.J.Transforms.Where(t => IsNeckNode(t.Node))).ToList();
            morphSource = o.J;
            var theirs = o.J.Transforms.Where(t => IsNeckNode(t.Node) && !t.FirstPerson && !Neutral(t)).Select(t => t.Node).Distinct().ToList();
            var mine = j.Transforms.Where(t => IsNeckNode(t.Node) && !t.FirstPerson && !Neutral(t)).Select(t => t.Node).Distinct().ToList();
            if (theirs.Count + mine.Count > 0)
                report.AppendLine(L.T("COU     {0}  os du cou et de la tête de {1} ({2}), dont vient la tête : {3}", row.Id, o.Row.Nom, o.Row.Id,
                    theirs.Count > 0 ? string.Join(", ", theirs) : L.T("aucun")) + (mine.Count > 0 ? L.T(" (ceux du preset retirés : {0})", string.Join(", ", mine)) : ""));
        }
        // vue à la troisième personne seulement (un PNJ n'a pas de squelette 1re personne) ; nœuds de caméra : joueur seul
        var kept = transforms
            .Where(t => !t.FirstPerson && !t.Node.StartsWith("CME Camera", StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Type switch { ScaleType => t.Values.Length == 1, PositionType => t.Values.Length == 3, RotationType => t.Values.Length == 9, _ => false })
            .Where(t => !Neutral(t))
            .ToList();
        int skipped = transforms.Count(t => !t.FirstPerson && t.Type is not (ScaleType or PositionType or RotationType));
        if (skipped > 0) report.AppendLine(L.T("SQUELETTE {0}  {1} transformation(s) d'un type non géré ignorée(s)", row.Id, skipped));
        // emplacements d'équipement mis à zéro (armes au dos, carquois, bouclier cachés pour les captures du joueur,
        // erin-celian) : un follower garderait ses armes rangées invisibles
        var hidden = kept.Where(t => t.Type == ScaleType && t.Values[0] < 0.01f && IsEquipment(t.Node)).ToList();
        if (hidden.Count > 0)
        {
            kept = kept.Except(hidden).ToList();
            report.AppendLine(L.T("SQUELETTE {0}  équipement caché par le preset gardé visible : {1}", row.Id, string.Join(", ", hidden.Select(t => t.Node).Distinct())));
        }
        // trop de valeurs : positions des armes (RMWPlugin) retirées d'abord, puis le reste dans l'ordre
        int before = kept.Count;
        if (kept.Count > MaxArray || kept.Sum(t => t.Values.Length) > MaxArray)
            kept = kept.Where(t => !(IsEquipment(t.Node) && t.Type != ScaleType)).ToList();
        while (kept.Count > MaxArray || kept.Sum(t => t.Values.Length) > MaxArray) kept.RemoveAt(kept.Count - 1);
        if (kept.Count < before)
            report.AppendLine(L.T("SQUELETTE {0}  {1} transformation(s) retirée(s) (positions d'armes d'abord) : trop de valeurs pour le script", row.Id, before - kept.Count));
        // Morphs de corps liés à la tête (curseurs UBE « NeckSize__HEAD> », « UpperChestHeight_n|p__HEAD> » : RaceMenu les
        // applique à la tête et au corps ensemble). L'export de tête a le cou modifié ; sans le même morph sur le corps, une
        // ligne apparaît à la jonction (signalement d'un joueur, 14/11). Seuls ceux-là sont repris : le reste du corps est
        // laissé à OBody (choix du 18/10).
        var neck = morphSource.BodyMorphs.Where(m => m.Name.Contains("__HEAD", StringComparison.OrdinalIgnoreCase) && !m.Key.Equals("OBody", StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.Name).Select(g => (Name: g.Key, Value: g.Sum(m => m.Value))).Where(m => Math.Abs(m.Value) > 1e-4).ToList();
        if (neck.Count > 0)
            report.AppendLine(L.T("COU     {0}  morph(s) de corps liés à la tête repris : {1}", row.Id, string.Join(", ", neck.Select(m => $"{m.Name} {m.Value.ToString("0.##", CultureInfo.InvariantCulture)}"))));
        if (kept.Count == 0 && neck.Count == 0) return null;

        var starts = new List<int>();
        var values = new List<float>();
        foreach (var t in kept) { starts.Add(values.Count); values.AddRange(t.Values); }
        if (kept.Count > 0)
            report.AppendLine(L.T("SQUELETTE {0}  {1} transformation(s) d'os : ", row.Id, kept.Count) + string.Join(", ", kept.Where(t => t.Type == ScaleType)
            .Select(t => $"{t.Node} ×{t.Values[0].ToString("0.##", CultureInfo.InvariantCulture)}").Distinct().Take(12))
            + (kept.Any(t => t.Type != ScaleType) ? L.T(" (+ {0} position(s)/rotation(s))", kept.Count(t => t.Type != ScaleType)) : ""));
        return new ScriptEntry
        {
            Name = Script,
            Properties =
            {
                new ScriptStringListProperty { Name = "TrNodes", Data = new ExtendedList<string>(kept.Select(t => t.Node)) },
                new ScriptStringListProperty { Name = "TrKeys", Data = new ExtendedList<string>(kept.Select(t => t.Key)) },
                new ScriptIntListProperty { Name = "TrTypes", Data = new ExtendedList<int>(kept.Select(t => t.Type)) },
                new ScriptIntListProperty { Name = "TrStarts", Data = new ExtendedList<int>(starts) },
                new ScriptFloatListProperty { Name = "TrValues", Data = new ExtendedList<float>(values) },
                new ScriptStringListProperty { Name = "MorphNames", Data = new ExtendedList<string>(neck.Select(m => m.Name)) },
                new ScriptFloatListProperty { Name = "MorphValues", Data = new ExtendedList<float>(neck.Select(m => m.Value)) },
            },
        };
    }

    /// <summary>Nœud d'arme, de bouclier ou de carquois (pas un os du corps).</summary>
    static bool IsEquipment(string node) =>
        node.Contains("Weapon", StringComparison.OrdinalIgnoreCase) || node.Contains("Shield", StringComparison.OrdinalIgnoreCase)
        || node.Contains("Quiver", StringComparison.OrdinalIgnoreCase);

    /// <summary>Transformation sans effet : échelle 1, position nulle, rotation identité.</summary>
    static bool Neutral(NodeTransform t) => t.Type switch
    {
        ScaleType => Math.Abs(t.Values[0] - 1) < 1e-4,
        PositionType => t.Values.All(v => Math.Abs(v) < 1e-4),
        RotationType => t.Values.Select((v, i) => Math.Abs(v - (i % 4 == 0 ? 1 : 0))).All(d => d < 1e-4),
        _ => false,
    };
}
