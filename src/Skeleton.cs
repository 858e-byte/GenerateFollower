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
    // tableaux de propriétés Papyrus : 128 éléments au plus
    const int MaxArray = 128;

    /// <summary>Script GF_FollowerSkeleton du PNJ, ou null s'il n'a aucune transformation utile.</summary>
    public static ScriptEntry? For(Jslot j, PresetRow row, StringBuilder report)
    {
        // vue à la troisième personne seulement (un PNJ n'a pas de squelette 1re personne) ; nœuds de caméra : joueur seul
        var kept = j.Transforms
            .Where(t => !t.FirstPerson && !t.Node.StartsWith("CME Camera", StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Type switch { ScaleType => t.Values.Length == 1, PositionType => t.Values.Length == 3, RotationType => t.Values.Length == 9, _ => false })
            .Where(t => !Neutral(t))
            .ToList();
        int skipped = j.Transforms.Count(t => !t.FirstPerson && t.Type is not (ScaleType or PositionType or RotationType));
        if (skipped > 0) report.AppendLine($"SQUELETTE {row.Id}  {skipped} transformation(s) d'un type non géré ignorée(s)");
        // emplacements d'équipement mis à zéro (armes au dos, carquois, bouclier cachés pour les captures du joueur,
        // erin-celian) : un follower garderait ses armes rangées invisibles
        var hidden = kept.Where(t => t.Type == ScaleType && t.Values[0] < 0.01f && IsEquipment(t.Node)).ToList();
        if (hidden.Count > 0)
        {
            kept = kept.Except(hidden).ToList();
            report.AppendLine($"SQUELETTE {row.Id}  équipement caché par le preset gardé visible : {string.Join(", ", hidden.Select(t => t.Node).Distinct())}");
        }
        // trop de valeurs : positions des armes (RMWPlugin) retirées d'abord, puis le reste dans l'ordre
        int before = kept.Count;
        if (kept.Count > MaxArray || kept.Sum(t => t.Values.Length) > MaxArray)
            kept = kept.Where(t => !(IsEquipment(t.Node) && t.Type != ScaleType)).ToList();
        while (kept.Count > MaxArray || kept.Sum(t => t.Values.Length) > MaxArray) kept.RemoveAt(kept.Count - 1);
        if (kept.Count < before)
            report.AppendLine($"SQUELETTE {row.Id}  {before - kept.Count} transformation(s) retirée(s) (positions d'armes d'abord) : trop de valeurs pour le script");
        if (kept.Count == 0) return null;

        var starts = new List<int>();
        var values = new List<float>();
        foreach (var t in kept) { starts.Add(values.Count); values.AddRange(t.Values); }
        report.AppendLine($"SQUELETTE {row.Id}  {kept.Count} transformation(s) d'os : " + string.Join(", ", kept.Where(t => t.Type == ScaleType)
            .Select(t => $"{t.Node} ×{t.Values[0].ToString("0.##", CultureInfo.InvariantCulture)}").Distinct().Take(12))
            + (kept.Any(t => t.Type != ScaleType) ? $" (+ {kept.Count(t => t.Type != ScaleType)} position(s)/rotation(s))" : ""));
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
