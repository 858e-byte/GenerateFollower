using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// « Data virtuel » : où trouver un fichier relatif à Data (meshes\…, textures\…, SKSE\Plugins\CharGen\…, plugins).
/// Vortex, aucun gestionnaire, ou outil lancé depuis MO2 (système de fichiers virtuel) : une seule racine, Data, et
/// rien ne change. MO2 lu directement (comme le jeu lancé depuis MO2, sans que l'outil le soit) : racines dans l'ordre
/// de MO2, overwrite, puis les mods activés du haut vers le bas de modlist.txt (le premier gagne), puis Data (jeu de
/// base, DLC, Creation Club, mods non gérés). Les fichiers loose gagnent toujours sur les BSA (Archives, lues par les
/// scripts Python). Lecture seule : rien n'est jamais écrit dans MO2 ni dans Data.
/// </summary>
public sealed class GameFiles
{
    /// <summary>Racine : nom affiché (« Data », « overwrite » ou nom du mod MO2) et dossier.</summary>
    public record Root(string Name, string Dir, bool Overwrite = false);

    public string DataDir { get; }
    /// <summary>Racines par priorité décroissante ; Data en dernier.</summary>
    public IReadOnlyList<Root> Roots { get; }
    /// <summary>« data » (Data seulement) ou « mo2 » (mods de MO2 lus directement).</summary>
    public string Mode { get; }
    public bool Direct => Mode == "mo2";
    /// <summary>MO2 direct : plugins du profil dans l'ordre de chargement (null sinon).</summary>
    public Mo2.PluginList? Plugins { get; }
    /// <summary>MO2 direct : fichiers de modlist.txt / plugins.txt surveillés (relus s'ils changent).</summary>
    readonly string[] watched;
    DateTime[] stamps;

    /// <summary>Test seulement (GF_DATA_EXCLURE = fichier, un chemin relatif par ligne) : fichiers de Data à ignorer,
    /// pour simuler un Data sans les mods déployés par Vortex (banc d'essai work\diag\mo2_sans_vfs).</summary>
    readonly HashSet<string>? dataExcluded;

    // listings mis en cache, par racine : dossier relatif (minuscules) -> fichiers et sous-dossiers (null : absent)
    readonly Dictionary<string, Listing?>[] listings;
    sealed record Listing(HashSet<string> Files, HashSet<string> Dirs);

    GameFiles(string dataDir, string mode, List<Root> roots, Mo2.PluginList? plugins, string[] watched)
    {
        DataDir = dataDir;
        Mode = mode;
        Roots = roots;
        Plugins = plugins;
        this.watched = watched;
        stamps = Stamps();
        listings = roots.Select(_ => new Dictionary<string, Listing?>(StringComparer.OrdinalIgnoreCase)).ToArray();
        if (Environment.GetEnvironmentVariable("GF_DATA_EXCLURE") is { Length: > 0 } ex && File.Exists(ex))
            dataExcluded = File.ReadLines(ex).Select(Norm).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Data seulement (Vortex, aucun gestionnaire, lancé depuis MO2).</summary>
    public static GameFiles DataOnly(string dataDir) => new(dataDir, "data", [new("Data", dataDir)], null, []);

    /// <summary>Racines selon les réglages : MO2 direct si MO2 est choisi, l'instance et le profil trouvés, et que l'outil
    /// n'est pas lancé depuis MO2 (réglage mo2Mode vide) ou que « direct » est demandé ; sinon Data seulement.</summary>
    public static GameFiles Open(Settings s, string dataDir)
    {
        var mode = s.Mo2Mode.Trim().ToLowerInvariant();
        if (!s.IsMo2 || mode == "vfs" || (mode != "direct" && Mo2.UnderVfs()) || s.Mo2Instance() is not { } inst)
            return DataOnly(dataDir);
        var profile = s.ResolvedMo2ProfileDir();
        if (Mo2.ReadModList(profile) is not { } list) return DataOnly(dataDir);
        var roots = new List<Root>();
        if (Directory.Exists(inst.OverwriteDir)) roots.Add(new("overwrite", inst.OverwriteDir, Overwrite: true));
        foreach (var mod in list.Enabled)
        {
            var dir = Path.Combine(inst.ModsDir, mod);
            if (Directory.Exists(dir)) roots.Add(new(mod, dir));
        }
        roots.Add(new("Data", dataDir));
        return new GameFiles(dataDir, "mo2", roots, Mo2.ReadPlugins(profile, dataDir),
            [list.Path, Path.Combine(profile, "plugins.txt"), Path.Combine(profile, "loadorder.txt")]);
    }

    DateTime[] Stamps() => watched.Select(f => File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue).ToArray();

    /// <summary>Vrai si modlist.txt / plugins.txt ont changé depuis l'ouverture (coches ou priorités modifiées dans MO2).
    /// Dates relues au plus toutes les 2 s (un build cherche des milliers de fichiers).</summary>
    public bool Stale
    {
        get
        {
            if (watched.Length == 0 || Environment.TickCount64 - lastCheck < 2000) return false;
            lastCheck = Environment.TickCount64;
            return !Stamps().SequenceEqual(stamps);
        }
    }
    long lastCheck = Environment.TickCount64;

    /// <summary>Oublie les listings en cache (fichiers ajoutés dans un mod pendant que le Builder tourne).</summary>
    public void Invalidate()
    {
        lock (dirRoots)
        {
            foreach (var l in listings) l.Clear();
            dirRoots.Clear();
            stamps = Stamps();
        }
    }

    static string Norm(string rel) => rel.Replace('/', '\\').Trim().TrimStart('\\');

    /// <summary>Relatif à Data, sans « Data\ » (chemins des fiches et des exports RaceMenu).</summary>
    public static string Rel(string rel)
    {
        rel = Norm(rel);
        return rel.StartsWith(@"Data\", StringComparison.OrdinalIgnoreCase) ? rel[5..] : rel;
    }

    /// <summary>Listing d'un dossier d'une racine (MO2 direct, hors overwrite), mis en cache. RootsWith ne le demande
    /// que pour les racines qui contiennent ce dossier.</summary>
    Listing? List(int root, string relDir)
    {
        if (!listings[root].TryGetValue(relDir, out var l))
        {
            var full = Path.Combine(Roots[root].Dir, relDir);
            try
            {
                var info = new DirectoryInfo(full);
                l = info.Exists ? new Listing(
                    info.EnumerateFiles().Select(f => f.Name).Where(n => !n.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    info.EnumerateDirectories().Select(d => d.Name).Where(n => !n.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase)) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { l = null; }
            listings[root][relDir] = l;
        }
        return l;
    }

    // MO2 direct : pour un dossier relatif, racines (hors overwrite) qui le contiennent, par priorité décroissante
    readonly Dictionary<string, int[]> dirRoots = new(StringComparer.OrdinalIgnoreCase);

    int[] RootsWith(string relDir)
    {
        if (dirRoots.TryGetValue(relDir, out var r)) return r;
        if (relDir.Length == 0) r = Enumerable.Range(0, Roots.Count).Where(i => !Roots[i].Overwrite).ToArray();
        else
        {
            var name = Path.GetFileName(relDir);
            var parent = Path.GetDirectoryName(relDir) ?? "";
            r = RootsWith(parent).Where(i => List(i, parent) is { } l && l.Dirs.Contains(name)).ToArray();
        }
        return dirRoots[relDir] = r;
    }

    bool Excluded(int root, string rel) => dataExcluded != null && root == Roots.Count - 1 && dataExcluded.Contains(rel);

    /// <summary>Indice de la racine gagnante pour ce fichier, ou -1. Caches protégés : Mutagen peut lire depuis
    /// plusieurs fils.</summary>
    int Winner(string rel)
    {
        lock (dirRoots) return WinnerLocked(rel);
    }

    int WinnerLocked(string rel)
    {
        if (Stale) Invalidate();
        rel = Norm(rel);
        if (rel.Length == 0 || Path.IsPathRooted(rel)) return -1;
        // Data seul : fichier lu sur le disque à chaque fois (Vortex déploie pendant que le Builder tourne)
        if (!Direct) return File.Exists(Path.Combine(Roots[0].Dir, rel)) && !Excluded(0, rel) ? 0 : -1;
        // overwrite : sur le disque aussi (RaceMenu y exporte pendant que le Builder tourne)
        if (Roots[0].Overwrite && File.Exists(Path.Combine(Roots[0].Dir, rel))) return 0;
        var dir = Path.GetDirectoryName(rel) ?? "";
        var file = Path.GetFileName(rel);
        foreach (var i in RootsWith(dir))
            if (List(i, dir) is { } l && l.Files.Contains(file) && !Excluded(i, rel)) return i;
        return -1;
    }

    /// <summary>Chemin réel du fichier gagnant (relatif à Data, avec ou sans « Data\ »), ou null. Fichiers loose
    /// seulement (pas les BSA).</summary>
    public string? Find(string rel)
    {
        rel = Rel(rel);
        return Winner(rel) is var i and >= 0 ? Path.Combine(Roots[i].Dir, rel) : null;
    }

    /// <summary>Comme Find, mais renvoie le chemin dans Data quand le fichier est introuvable (pour les messages et
    /// les File.Exists qui suivent).</summary>
    public string PathOf(string rel) => Find(rel) ?? Path.Combine(DataDir, Rel(rel));

    /// <summary>Chemin relatif à Data d'un fichier réel (dans Data, overwrite ou un mod), sinon le chemin tel quel.</summary>
    public string RelOf(string realPath)
    {
        var full = Path.GetFullPath(realPath);
        foreach (var r in Roots)
        {
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Dir)) + "\\";
            if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return full[dir.Length..];
        }
        return realPath;
    }

    /// <summary>Nom de la racine gagnante (« Data », « overwrite », nom du mod MO2), ou null.</summary>
    public string? Owner(string rel) => Winner(Rel(rel)) is var i and >= 0 ? Roots[i].Name : null;

    /// <summary>Le dossier existe dans au moins une racine.</summary>
    public bool DirExists(string relDir) => Roots.Any(r => Directory.Exists(Path.Combine(r.Dir, Norm(relDir))));

    /// <summary>Dossiers réels d'un dossier relatif, par priorité décroissante (ceux qui existent).</summary>
    public List<string> Dirs(string relDir) => Roots.Select(r => Path.Combine(r.Dir, Norm(relDir))).Where(Directory.Exists).ToList();

    /// <summary>Fichiers d'un dossier relatif, toutes racines fusionnées : (chemin relatif au dossier, chemin réel du
    /// gagnant), triés par chemin relatif.</summary>
    public List<(string Rel, string Path)> Enumerate(string relDir, string pattern, bool recursive)
    {
        relDir = Norm(relDir);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Roots.Count; i++)
        {
            var dir = Path.Combine(Roots[i].Dir, relDir);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                var rel = System.IO.Path.GetRelativePath(dir, f);
                if (rel.Split('\\').Any(p => p.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase))) continue;
                if (i == Roots.Count - 1 && dataExcluded != null && dataExcluded.Contains(relDir.Length > 0 ? relDir + "\\" + rel : rel)) continue;
                found.TryAdd(rel, f);
            }
        }
        return found.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Chemin relatif à Data d'un chemin absolu situé dans Data, sinon null.</summary>
    string? UnderData(string fullPath)
    {
        if (!Direct || string.IsNullOrEmpty(fullPath)) return null;
        string full;
        try { full = Path.GetFullPath(fullPath); } catch { return null; }
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataDir));
        if (full.Equals(data, StringComparison.OrdinalIgnoreCase)) return "";
        return full.StartsWith(data + "\\", StringComparison.OrdinalIgnoreCase) ? full[(data.Length + 1)..] : null;
    }

    /// <summary>Système de fichiers de Mutagen : chemin réel d'un fichier de Data (MO2 direct), ou null (inchangé).</summary>
    public string? Redirect(string fullPath) => UnderData(fullPath) is { Length: > 0 } rel ? Find(rel) : null;

    /// <summary>Système de fichiers de Mutagen : listing d'un dossier de Data, toutes racines, sous les chemins de Data
    /// (MO2 direct), ou null (listing normal).</summary>
    public IEnumerable<string>? ListAsData(string fullDir, string pattern, bool recursive)
    {
        if (UnderData(fullDir) is not { } rel) return null;
        var dir = Path.Combine(DataDir, rel);
        return Enumerate(rel, pattern, recursive).Select(f => Path.Combine(dir, f.Rel)).ToList();
    }

    /// <summary>
    /// BSA dans l'ordre où le jeu les charge, la dernière gagnante en dernier : archives du jeu de base (Skyrim - *.bsa,
    /// sResourceArchiveList), puis pour chaque plugin chargé « nom.bsa » et « nom - Textures.bsa ». Null hors MO2 direct
    /// (les scripts Python gardent alors leur règle : tous les BSA de Data).
    /// </summary>
    public List<string>? Archives(IEnumerable<string> loadOrder)
    {
        if (!Direct) return null;
        var list = new List<string>();
        void Add(string? p) { if (p != null && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p); }
        if (Directory.Exists(DataDir))
            foreach (var f in Directory.EnumerateFiles(DataDir, "Skyrim - *.bsa").Order(StringComparer.OrdinalIgnoreCase)) Add(f);
        foreach (var plugin in loadOrder)
        {
            var stem = Path.GetFileNameWithoutExtension(plugin);
            Add(Find(stem + ".bsa"));
            Add(Find(stem + " - Textures.bsa"));
        }
        return list;
    }

    /// <summary>Fichier lu par les scripts Python (GF_VDATA) : racines et archives.</summary>
    public void WriteForPython(string path, IEnumerable<string> loadOrder)
    {
        var archives = Archives(loadOrder);
        var json = new JsonObject
        {
            ["mode"] = Mode,
            ["data"] = DataDir,
            ["roots"] = new JsonArray(Roots.Select(r => (JsonNode)r.Dir).ToArray()),
            ["overwrite"] = Roots.FirstOrDefault(r => r.Overwrite)?.Dir,
            // ordre du jeu inversé : le premier de la liste gagne (comme les racines)
            ["archives"] = archives == null ? null : new JsonArray(Enumerable.Reverse(archives).Select(a => (JsonNode)a).ToArray()),
            ["dataExclure"] = Environment.GetEnvironmentVariable("GF_DATA_EXCLURE"),
        };
        var text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
    }

    /// <summary>Résumé pour la console et le rapport de diagnostic.</summary>
    public string Summary => Direct
        ? L.T("MO2 lu directement : {0} mods activés + overwrite + Data", Roots.Count - (Roots[0].Overwrite ? 2 : 1))
        : L.T("Data seulement");
}
