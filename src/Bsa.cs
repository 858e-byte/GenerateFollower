using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace GenerateFollower;

/// <summary>
/// Fichiers rangés dans les BSA (remplace offline\bsa.py) : index de toutes les archives, extraction à la demande dans
/// work\bsa_cache. Mêmes règles que bsa.py, pour ne rien changer aux builds : seules les archives SE (version 105) ;
/// Vortex ou Data seul : tous les *.bsa de Data, le premier dans l'ordre alphabétique gagne ; MO2 lu directement :
/// archives de GameFiles.Archives, la dernière chargée gagne. Lecture seule dans Data et dans MO2.
/// </summary>
public sealed class Bsa
{
    readonly List<string> archives;
    readonly string filesDir;
    Dictionary<string, IArchiveFile>? index;
    readonly Dictionary<string, IArchiveReader> readers = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();

    /// <param name="archives">archives par priorité décroissante (la première gagne)</param>
    Bsa(List<string> archives, string cacheDir, string signature)
    {
        this.archives = archives;
        filesDir = Path.Combine(cacheDir, "_files" + signature);
    }

    public static Bsa Open(Context ctx)
    {
        var mo2 = ctx.Files.Archives(ctx.Env.LoadOrder.ListedOrder.Select(l => l.ModKey.FileName.String).Where(ctx.ActivePlugins.Contains));
        List<string> list;
        var sig = "";
        if (mo2 != null)
        {
            list = Enumerable.Reverse(mo2).ToList();
            sig = "_" + Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join("\n", list.Select(a => a.ToLowerInvariant())))))[..10];
        }
        else
            // ordre de sorted(glob(...)) en Python : chemins comparés caractère par caractère
            list = Directory.Exists(ctx.DataDir) ? Directory.EnumerateFiles(ctx.DataDir, "*.bsa").Order(StringComparer.Ordinal).ToList() : [];
        return new Bsa(list, Path.Combine(ctx.WorkDir, "bsa_cache"), sig);
    }

    /// <summary>En-tête « BSA\0 » version 105 (Skyrim SE) ; les autres archives sont ignorées, comme dans bsa.py.</summary>
    static bool IsSse(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> h = stackalloc byte[8];
            return fs.Read(h) == 8 && h[..4].SequenceEqual("BSA\0"u8) && BitConverter.ToUInt32(h[4..]) == 105;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    IArchiveReader Reader(string archive)
    {
        if (!readers.TryGetValue(archive, out var r))
            readers[archive] = r = Archive.CreateReader(GameRelease.SkyrimSE, archive, new SharedFileSystem());
        return r;
    }

    Dictionary<string, IArchiveFile> Index()
    {
        if (index != null) return index;
        var idx = new Dictionary<string, IArchiveFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in archives)
        {
            if (!IsSse(a)) continue;
            try
            {
                foreach (var f in Reader(a).Files)
                    idx.TryAdd(f.Path.Replace('/', '\\'), f);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { Console.WriteLine($"  BSA illisible : {Path.GetFileName(a)} ({e.Message})"); }
        }
        return index = idx;
    }

    /// <summary>Dossier des fichiers extraits (chemins relatifs à Data, en minuscules), une racine de plus après Data.</summary>
    public string FilesDir => filesDir;

    /// <summary>Le fichier (relatif à Data) est dans une archive.</summary>
    public bool Contains(string rel)
    {
        rel = Norm(rel);
        lock (gate) return File.Exists(Path.Combine(filesDir, rel)) || Index().ContainsKey(rel);
    }

    /// <summary>Extrait (une fois) le fichier relatif à Data depuis les BSA, renvoie son chemin dans le cache, ou null.</summary>
    public string? Find(string rel)
    {
        rel = Norm(rel);
        if (rel.Length == 0) return null;
        var outPath = Path.Combine(filesDir, rel.ToLowerInvariant());
        lock (gate)
        {
            if (File.Exists(outPath)) return outPath;
            if (!Index().TryGetValue(rel, out var file)) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var tmp = outPath + ".tmp";
            File.WriteAllBytes(tmp, file.GetBytes());
            File.Move(tmp, outPath, true);
            return outPath;
        }
    }

    /// <summary>Tous les fichiers des archives dont le chemin commence par prefix et finit par suffix (minuscules).</summary>
    public List<string> Matching(string prefix, string suffix)
    {
        lock (gate)
            return Index().Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    static string Norm(string rel) => rel.Replace('/', '\\').Trim().TrimStart('\\');
}
