using System.IO.Compression;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Packs déjà construits (« Mes packs » du Builder) : les ZIP GF_*.zip du dossier de sortie, relus sans fichier à part :
/// chaque follower du plugin a l'EditorID « GF_&lt;id du preset&gt; » (FollowerBuilder) et le titre du pack est le nom de
/// sa quête GF_MainQuest_… (pouvoir « Invoquer : titre »). Marche donc aussi pour les packs faits avant cette fonction.
/// Reconstruire = même ZIP, même plugin, même titre, mêmes followers, avec les choix actuels du Builder.
/// </summary>
public static class Packs
{
    /// <summary>Plugin = null : ZIP sans plugin GF_ unique à sa racine (pas reconstructible).</summary>
    /// <summary>NoPower : pack construit sans pouvoir d'invocation (propriété NoPower du script GF_MainQuest), gardé à la
    /// reconstruction.</summary>
    public sealed record Pack(string Zip, string Path, DateTime Date, long Size, string? Plugin, string? Title, List<string> Ids, string? Problem,
        bool NoPower = false, bool PlacedRefs = false);

    static readonly Dictionary<string, (DateTime Date, long Size, Pack Pack)> cache = new(StringComparer.OrdinalIgnoreCase);

    public static List<Pack> List(Context ctx) =>
        Directory.Exists(ctx.DistDir)
            ? Directory.EnumerateFiles(ctx.DistDir, "GF_*.zip").Select(z => Read(ctx, z)).OrderByDescending(p => p.Date).ToList()
            : [];

    /// <summary>Pack d'un nom de ZIP du dossier de sortie (nom seul : rien en dehors de ce dossier).</summary>
    public static Pack Get(Context ctx, string zip)
    {
        if (string.IsNullOrEmpty(zip) || zip != System.IO.Path.GetFileName(zip) || !zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(System.IO.Path.Combine(ctx.DistDir, zip)))
            throw new GfException(L.T("Pack introuvable : {0}", zip));
        return Read(ctx, System.IO.Path.Combine(ctx.DistDir, zip));
    }

    static Pack Read(Context ctx, string zipPath)
    {
        var info = new FileInfo(zipPath);
        lock (cache)
            if (cache.TryGetValue(zipPath, out var c) && c.Date == info.LastWriteTimeUtc && c.Size == info.Length) return c.Pack;
        Pack pack;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var esps = zip.Entries.Where(e => e.FullName == e.Name && e.Name.StartsWith("GF_", StringComparison.OrdinalIgnoreCase)
                && e.Name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)).ToList();
            if (esps.Count != 1)
                pack = new(info.Name, zipPath, info.LastWriteTime, info.Length, null, null, [],
                    esps.Count == 0 ? L.T("pas de plugin GF_ dans ce ZIP") : L.T("plusieurs plugins (un par follower) : à reconstruire depuis la liste"));
            else
            {
                // copie temporaire : Mutagen lit un fichier ; supprimée après lecture
                var tmp = System.IO.Path.Combine(Context.Dir(System.IO.Path.Combine(ctx.WorkDir, "packs_lus")), esps[0].Name);
                esps[0].ExtractToFile(tmp, true);
                try
                {
                    using var mod = SkyrimMod.CreateFromBinaryOverlay(tmp, SkyrimRelease.SkyrimSE, PluginText.Read);
                    var ids = mod.Npcs.Select(n => n.EditorID).Where(e => e != null && e.StartsWith("GF_", StringComparison.Ordinal))
                        .Select(e => e![3..]).ToList();
                    var quest = mod.Quests.FirstOrDefault(q => q.EditorID?.StartsWith("GF_MainQuest", StringComparison.Ordinal) == true);
                    var noPower = quest?.VirtualMachineAdapter?.Scripts.SelectMany(s => s.Properties)
                        .OfType<IScriptBoolPropertyGetter>().Any(p => p.Name == "NoPower" && p.Data) == true;
                    // références placées (propriété HomeRefs) : gardées ou non à la reconstruction, comme le pack
                    var placedRefs = quest?.VirtualMachineAdapter?.Scripts.SelectMany(s => s.Properties).Any(p => p.Name == "HomeRefs") == true;
                    pack = new(info.Name, zipPath, info.LastWriteTime, info.Length, esps[0].Name, quest?.Name?.String, ids, null, noPower, placedRefs);
                }
                finally { File.Delete(tmp); }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)   // ZIP abîmé, plugin illisible : signalé, la liste continue
        {
            pack = new(info.Name, zipPath, info.LastWriteTime, info.Length, null, null, [], L.T("ZIP illisible : {0}", e.Message));
        }
        lock (cache) cache[zipPath] = (info.LastWriteTimeUtc, info.Length, pack);
        return pack;
    }
}
