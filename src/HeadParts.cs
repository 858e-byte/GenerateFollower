using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>Headparts réels d'un PNJ, tels que le moteur les assemble.</summary>
public static class HeadParts
{
    /// <summary>
    /// Headparts du preset, plus le headpart par défaut de la race pour chaque type absent (les cils d'UBE,
    /// par exemple). Le moteur fait de même au chargement ; le CK en tient compte en générant le FaceGen.
    /// Si le NIF n'a pas une forme pour chacun, le jeu ignore le FaceGen et reconstruit une tête par défaut.
    /// </summary>
    public static List<IHeadPartGetter> Effective(Context ctx, Jslot j, IRaceGetter? race, bool female)
    {
        var parts = new List<IHeadPartGetter>();
        foreach (var (key, _) in j.HeadParts)
            if (ctx.TryResolveRm<IHeadPartGetter>(key, out var hp) && parts.All(p => p.FormKey != hp.FormKey)) parts.Add(hp);

        var defaults = (female ? race?.HeadData?.Female : race?.HeadData?.Male)?.HeadParts ?? [];
        foreach (var d in defaults)
        {
            if (!ctx.Cache.TryResolve<IHeadPartGetter>(d.Head.FormKey, out var def)) continue;
            if (parts.Any(p => p.Type == def.Type)) continue;
            parts.Add(def);
        }
        return parts;
    }
}
