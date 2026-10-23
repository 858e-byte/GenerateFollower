using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenerateFollower;

/// <summary>
/// Réglages propres à l'installation du joueur (chemins), modifiables dans l'écran « Réglages » du Builder.
/// Fichier : settings.json à côté de config.json (application portable : rien sur C:), voir FilePath.
/// Un champ vide = valeur détectée (voir les méthodes Resolved*).
/// La lecture n'écrit jamais rien : le fichier n'est créé qu'à l'enregistrement.
/// </summary>
public class Settings
{
    /// <summary>Dossier Data du jeu. Vide = détecté par Mutagen (registre Steam/GOG).</summary>
    public string GameDataDir { get; set; } = "";
    /// <summary>Gestionnaire de mods : « Vortex », « MO2 » ou « Aucun ».</summary>
    public string ModManager { get; set; } = "Vortex";
    /// <summary>Dossier des mods du gestionnaire. Vide = %APPDATA%\Vortex\skyrimse\mods pour Vortex, dossier mods de
    /// l'instance globale %LOCALAPPDATA%\ModOrganizer\Skyrim Special Edition pour MO2. MO2 : aussi ModOrganizer.ini ou le
    /// dossier de l'instance (dossiers des mods, profils et overwrite lus dans l'ini).</summary>
    public string ModsDir { get; set; } = "";
    /// <summary>MO2 : profil (nom, ou chemin du dossier du profil). Vide = profil sélectionné dans MO2.</summary>
    public string Mo2Profile { get; set; } = "";
    /// <summary>MO2 : « direct » = mods lus dans le dossier mods de MO2 (pas besoin de lancer l'outil depuis MO2) ;
    /// « vfs » = seulement Data (outil lancé depuis MO2). Vide = automatique : vfs si lancé depuis MO2, sinon direct.</summary>
    public string Mo2Mode { get; set; } = "";
    /// <summary>Dossier des archives téléchargées (zip/7z/rar) où chercher des skins. Facultatif ; vide sous MO2 = dossier
    /// des téléchargements de l'instance.</summary>
    public string ArchivesDir { get; set; } = "";
    /// <summary>Dossier de sortie des ZIP. Vide = dist\ du projet.</summary>
    public string OutputDir { get; set; } = "";
    /// <summary>7z.exe. Vide = deps\7zip\7z.exe, sinon C:\Program Files\7-Zip\7z.exe s'il existe.</summary>
    public string SevenZip { get; set; } = "";
    /// <summary>texconv.exe. Vide = deps\texconv\texconv.exe, sinon celui fourni avec SSEEdit dans Data.</summary>
    public string Texconv { get; set; } = "";
    /// <summary>Lanceur Python. Vide = deps\python\python.exe, sinon « py ».</summary>
    public string Python { get; set; } = "";
    /// <summary>Langue de l'interface (« fr », « en »…). Vide = langue de Windows (français, sinon anglais).</summary>
    public string Language { get; set; } = "";
    /// <summary>Dossier de PyNifly (celui qui contient le paquet « pyn »). Vide = deps\pynifly du projet.</summary>
    public string PyNifly { get; set; } = "";

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Fichier des réglages : dans le dossier de l'application (à côté de config.json : tout reste au même endroit) ;
    /// %LOCALAPPDATA%\GenerateFollower seulement s'il y existe déjà ou si le dossier de l'application n'est pas
    /// modifiable (Program Files).
    /// </summary>
    public static string FilePath(string root)
    {
        // bancs d'essai (work\diag) : réglages de test sans toucher à ceux du joueur
        if (Environment.GetEnvironmentVariable("GF_SETTINGS") is { Length: > 0 } test) return test;
        var portable = Path.Combine(root, "settings.json");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenerateFollower", "settings.json");
        if (File.Exists(portable)) return portable;
        if (File.Exists(local)) return local;
        return Writable(root) ? portable : local;
    }

    static bool Writable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".gf_write_test_{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>Dossier du projet (ou de l'application) : outils livrés dans deps\.</summary>
    [JsonIgnore] public string Root { get; set; } = "";
    string Dep(params string[] parts) => Path.Combine([Root, "deps", .. parts]);

    public static Settings Load(string root)
    {
        var path = FilePath(root);
        // fichier vide = mode portable demandé par le joueur (guide de bêta), valeurs détectées
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var s = text.Trim().Length > 0 ? JsonSerializer.Deserialize<Settings>(text, Json) ?? new() : new();
        s.Root = root;
        return s;
    }

    public void Save(string root)
    {
        var path = FilePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
    }

    static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [JsonIgnore] public bool IsVortex => ModManager.Equals("Vortex", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsMo2 => ModManager.Equals("MO2", StringComparison.OrdinalIgnoreCase);
    /// <summary>Nom du gestionnaire pour les messages (« Vortex », « MO2 »), ou « ton gestionnaire de mods ».</summary>
    [JsonIgnore] public string ManagerLabel => IsVortex ? "Vortex" : IsMo2 ? "MO2" : L.T("ton gestionnaire de mods");

    /// <summary>Instance MO2 (null si le gestionnaire n'est pas MO2 ou si aucune instance n'est trouvée).</summary>
    public Mo2.Instance? Mo2Instance() => IsMo2 ? Mo2.Find(ModsDir) : null;

    public string ResolvedModsDir() => (IsMo2 ? Mo2Instance()?.ModsDir : null) ?? Blank(ModsDir)
        ?? (IsVortex ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex", "skyrimse", "mods")
            : Mo2Instance()?.ModsDir ?? "");

    /// <summary>MO2 : dossier du profil utilisé (modlist.txt), sinon "".</summary>
    public string ResolvedMo2ProfileDir() => Mo2Instance() is { } i ? Mo2.ProfileDir(i, Mo2Profile) : "";

    /// <summary>Archives : réglage, sinon téléchargements du gestionnaire (MO2 : downloads de l'instance ; Vortex : dossier
    /// par défaut %APPDATA%\Vortex\downloads\skyrimse, s'il existe ; un dossier déplacé dans Vortex est à indiquer).</summary>
    public string ResolvedArchivesDir()
    {
        if (Blank(ArchivesDir) is { } set) return set;
        if (Mo2Instance() is { } i && Directory.Exists(i.DownloadsDir)) return i.DownloadsDir;
        var vortex = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex", "downloads", "skyrimse");
        return IsVortex && Directory.Exists(vortex) ? vortex : "";
    }

    // Outils : réglage, sinon celui livré dans deps\ (version publique), sinon celui installé sur le PC
    public string ResolvedSevenZip() => Blank(SevenZip)
        ?? new[] { Dep("7zip", "7z.exe") }.Concat(new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "7-Zip", "7z.exe"))).FirstOrDefault(File.Exists) ?? "";

    public string ResolvedTexconv(string dataDir) => Blank(Texconv)
        ?? (File.Exists(Dep("texconv", "texconv.exe")) ? Dep("texconv", "texconv.exe") : null)
        ?? (Directory.Exists(dataDir) ? Directory.EnumerateDirectories(dataDir, "SSEEdit*")
            .Select(d => Path.Combine(d, "Edit Scripts", "Texconvx64.exe")).FirstOrDefault(File.Exists) : null) ?? "";

    /// <summary>Python : réglage, sinon le Python portable de deps\python (numpy, Pillow, lz4), sinon le lanceur « py ».</summary>
    public string ResolvedPython() => Blank(Python) ?? (File.Exists(Dep("python", "python.exe")) ? Dep("python", "python.exe") : "py");

    public string ResolvedPyNifly(string root) => Blank(PyNifly) ?? Path.Combine(root, "deps", "pynifly");

    public string ResolvedOutputDir(string root) => Blank(OutputDir) ?? Path.Combine(root, "dist");
}
