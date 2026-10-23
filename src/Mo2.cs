using System.Diagnostics;

namespace GenerateFollower;

/// <summary>
/// Mod Organizer 2 : instance (ModOrganizer.ini), profil (modlist.txt) et système de fichiers virtuel. Sous MO2, les
/// mods ne sont pas dans Data : le jeu et les outils lancés depuis MO2 les voient par le système de fichiers virtuel
/// (usvfs). Le générateur lit les mods directement dans le dossier de MO2 (GameFiles, mode « direct », par défaut hors de
/// MO2), ou Data seulement s'il est lancé depuis MO2 (Exécutables). Lecture seule.
/// </summary>
public static class Mo2
{
    /// <summary>Instance MO2 : dossier (celui de ModOrganizer.ini), mods, profils, téléchargements, profil sélectionné.</summary>
    public record Instance(string Dir, string ModsDir, string ProfilesDir, string DownloadsDir, string SelectedProfile, string OverwriteDir);

    /// <summary>Plugins d'un profil dans l'ordre de chargement (loadorder.txt, sinon plugins.txt), avec leur coche. Un
    /// plugin de loadorder.txt absent de plugins.txt (jeu de base, Creation Club) est chargé d'office, comme dans MO2.</summary>
    public record PluginList(string Path, List<(string Name, bool Enabled)> Plugins);

    /// <summary>modlist.txt d'un profil : mods activés (« + ») et désactivés (« - »), dans l'ordre du fichier (le premier
    /// gagne les conflits). Les séparateurs et les entrées « * » (DLC, mods non gérés) sont ignorés.</summary>
    public record ModList(string Path, List<string> Enabled, List<string> Disabled)
    {
        readonly HashSet<string> enabled = new(Enabled, StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> known = new(Enabled.Concat(Disabled), StringComparer.OrdinalIgnoreCase);
        public bool IsEnabled(string mod) => enabled.Contains(mod);
        public bool Knows(string mod) => known.Contains(mod);
    }

    /// <summary>Instance par défaut : celle du MO2 ouvert (instance portable), sinon l'instance globale
    /// %LOCALAPPDATA%\ModOrganizer\&lt;instance&gt; dont le jeu est Skyrim SE.</summary>
    public static string? DefaultInstanceDir()
    {
        // MO2 ouvert (cas normal : outil lancé depuis MO2) : instance portable à côté de son ModOrganizer.exe
        foreach (var p in Process.GetProcessesByName("ModOrganizer"))
        {
            try
            {
                var exeDir = Path.GetDirectoryName(p.MainModule?.FileName);
                if (exeDir != null && File.Exists(Path.Combine(exeDir, "ModOrganizer.ini"))
                    && ReadIni(Path.Combine(exeDir, "ModOrganizer.ini")).GetValueOrDefault("General/gameName", "").Contains("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase))
                    return exeDir;
            }
            catch { }
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (!Directory.Exists(root)) return null;
        var dirs = Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "ModOrganizer.ini"))).ToList();
        bool IsSse(string d) => ReadIni(Path.Combine(d, "ModOrganizer.ini")).GetValueOrDefault("General/gameName", "")
            .Contains("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase);
        return dirs.FirstOrDefault(d => Path.GetFileName(d).Equals("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase))
            ?? dirs.FirstOrDefault(IsSse);
    }

    /// <summary>
    /// Instance qui contient ce dossier de mods (ModOrganizer.ini dans le dossier parent, cas habituel et instance
    /// portable), ou l'instance globale par défaut si le dossier de mods n'est pas renseigné. Sans ModOrganizer.ini :
    /// profils et téléchargements à côté du dossier de mods (disposition par défaut de MO2).
    /// </summary>
    public static Instance? Find(string modsDir)
    {
        // réglage = ModOrganizer.ini ou dossier de l'instance (bouton « MO2… » des Réglages) : dossiers lus dans l'ini
        var set = string.IsNullOrWhiteSpace(modsDir) ? "" : Path.GetFullPath(modsDir.Trim()).TrimEnd('\\');
        if (set.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && File.Exists(set)) { set = Path.GetDirectoryName(set)!; modsDir = ""; }
        else if (set != "" && File.Exists(Path.Combine(set, "ModOrganizer.ini"))) modsDir = "";
        else set = "";
        var dir = set != "" ? set : !string.IsNullOrWhiteSpace(modsDir) ? Path.GetDirectoryName(Path.GetFullPath(modsDir.Trim()).TrimEnd('\\')) : DefaultInstanceDir();
        if (dir == null) return null;
        var ini = Path.Combine(dir, "ModOrganizer.ini");
        var values = File.Exists(ini) ? ReadIni(ini) : [];
        var baseDir = Expand(values.GetValueOrDefault("Settings/base_directory", ""), dir, dir);
        string Setting(string key, string def) => Expand(values.GetValueOrDefault("Settings/" + key, ""), baseDir, Path.Combine(baseDir, def));
        var mods = string.IsNullOrWhiteSpace(modsDir) ? Setting("mod_directory", "mods") : Path.GetFullPath(modsDir.Trim());
        var profile = values.GetValueOrDefault("General/selected_profile", "");
        // « @ByteArray(Default) » : valeur Qt
        if (profile.StartsWith("@ByteArray(") && profile.EndsWith(')')) profile = profile[11..^1];
        return new Instance(dir, mods, Setting("profiles_directory", "profiles"), Setting("download_directory", "downloads"),
            profile == "" ? "Default" : profile, Setting("overwrite_directory", "overwrite"));
    }

    /// <summary>Ordre de chargement du profil : loadorder.txt (ordre complet) et plugins.txt (« * » = coché). Null sans
    /// plugins.txt. Lu avec partage : MO2 ouvert peut réécrire ces fichiers.</summary>
    public static PluginList? ReadPlugins(string profileDir, string? dataDir = null)
    {
        var pluginsTxt = Path.Combine(profileDir, "plugins.txt");
        if (!File.Exists(pluginsTxt)) return null;
        var listed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var raw in ReadShared(pluginsTxt))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var on = line[0] == '*';
            var name = on ? line[1..].Trim() : line;
            if (listed.TryAdd(name, on)) order.Add(name);
        }
        var loadOrder = Path.Combine(profileDir, "loadorder.txt");
        if (File.Exists(loadOrder))
        {
            var full = ReadShared(loadOrder).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#')
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // plugins de plugins.txt absents de loadorder.txt (fichier pas encore réécrit par MO2) : à la fin
            order = full.Concat(order.Where(p => !full.Contains(p, StringComparer.OrdinalIgnoreCase))).ToList();
        }
        else if (dataDir != null)
        {
            // sans loadorder.txt : masters du jeu et Creation Club (Skyrim.ccc) chargés d'office, en tête
            var ccc = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dataDir)) ?? "", "Skyrim.ccc");
            var implicits = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }
                .Concat(File.Exists(ccc) ? ReadShared(ccc).Select(l => l.Trim()).Where(l => l.Length > 0) : [])
                .Where(p => File.Exists(Path.Combine(dataDir, p)) && !listed.ContainsKey(p)).ToList();
            order = implicits.Concat(order).ToList();
        }
        return new PluginList(pluginsTxt, order.Select(p => (p, listed.GetValueOrDefault(p, true))).ToList());
    }

    static string[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd().Split('\n');
    }

    /// <summary>Dossier du profil : réglage (nom ou chemin complet), sinon le profil sélectionné dans MO2.</summary>
    public static string ProfileDir(Instance inst, string profileSetting)
    {
        var p = profileSetting.Trim();
        return p == "" ? Path.Combine(inst.ProfilesDir, inst.SelectedProfile)
            : Path.IsPathRooted(p) ? p : Path.Combine(inst.ProfilesDir, p);
    }

    public static ModList? ReadModList(string profileDir)
    {
        var path = Path.Combine(profileDir, "modlist.txt");
        if (!File.Exists(path)) return null;
        List<string> on = [], off = [];
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[0] == '#') continue;
            var name = line[1..];
            if (name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            if (line[0] == '+') on.Add(name);
            else if (line[0] == '-') off.Add(name);
        }
        return new ModList(path, on, off);
    }

    /// <summary>Vrai si le processus tourne dans le système de fichiers virtuel de MO2 (usvfs injecté au lancement).</summary>
    public static bool UnderVfs()
    {
        try
        {
            return Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Any(m => m.ModuleName.StartsWith("usvfs", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// Avertissement si l'outil ne voit pas les mods de MO2 : lancé hors de MO2 (pas d'usvfs) et les plugins des mods
    /// activés absents de Data. Null si tout va bien (ou si rien ne permet d'en juger).
    /// </summary>
    public static string? DataWarning(Instance inst, ModList? list, GameFiles files)
    {
        if (UnderVfs()) return null;
        if (list == null) return L.T("Mod Organizer 2 : modlist.txt introuvable (profil). Vérifie le dossier des mods et le profil dans les Réglages.");
        // mods lus directement dans le dossier de MO2 : pas besoin de lancer l'outil depuis MO2
        if (files.Direct) return null;
        var (plugins, found) = PluginsInData(inst, list, files);
        if (plugins == 0 || found * 2 > plugins) return null;
        return L.T("Mod Organizer 2 : lancé hors de MO2, Data ne contient pas tes mods ({0} plugins de mods activés sur "
            + "{1} absents de Data). Remets « Mode MO2 » sur Automatique dans les Réglages (mods lus dans le dossier de MO2), "
            + "ou lance GenerateFollower depuis MO2 : sinon les headparts, textures et l'ordre de chargement de tes mods ne sont "
            + "pas vus.", plugins - found, plugins);
    }

    /// <summary>Plugins (.esp/.esm/.esl) à la racine des mods activés, et combien sont visibles dans Data (Data virtuel
    /// en mode direct).</summary>
    public static (int Plugins, int InData) PluginsInData(Instance inst, ModList list, GameFiles files)
    {
        int plugins = 0, found = 0;
        foreach (var mod in list.Enabled)
        {
            var dir = Path.Combine(inst.ModsDir, mod);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)))
            {
                plugins++;
                if (files.Find(Path.GetFileName(f)) != null) found++;
            }
        }
        return (plugins, found);
    }

    /// <summary>INI façon Qt : « Section/clé » → valeur (chemins avec / ou \\, %BASE_DIR%).</summary>
    static Dictionary<string, string> ReadIni(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var eq = line.IndexOf('=');
            if (eq > 0) d[$"{section}/{line[..eq].Trim()}"] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    static string Expand(string value, string baseDir, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var v = value.Replace("%BASE_DIR%", baseDir, StringComparison.OrdinalIgnoreCase).Replace("\\\\", "\\").Replace('/', '\\');
        return Path.GetFullPath(Path.IsPathRooted(v) ? v : Path.Combine(baseDir, v));
    }
}
