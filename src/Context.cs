using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Installs;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

public class GfException(string message) : Exception(message);

public class Config
{
    public string PluginName { get; set; } = "GenerateFollower.esp";
    public string ExporterPluginName { get; set; } = "GF_Exporter.esp";
    public string DefaultRace { get; set; } = "NordRace";
    public string FollowerClass { get; set; } = "CombatWarrior1H";
    public string DefaultOutfit { get; set; } = "";
    public List<string> DefaultPackages { get; set; } = [];
    public string VoiceFemale { get; set; } = "FemaleYoungEager";
    public string VoiceMale { get; set; } = "MaleYoungEager";
    public bool SkipWithoutHead { get; set; } = true;
    public int PageSize { get; set; } = 120;
    /// <summary>Dossier des têtes exportées (GF_xxx.nif/.dds). Vide = Data\SKSE\Plugins\CharGen.</summary>
    public string HeadsDir { get; set; } = "";
    /// <summary>Texture de teint : RaceMenu exporte du 4096² non compressé (64 Mo), on la réduit.</summary>
    public int TintSize { get; set; } = 1024;
    public string TintFormat { get; set; } = "BC7_UNORM";
    /// <summary>
    /// Followers autonomes : seules dépendances = jeu de base + mod de la race et de la tête ; les autres
    /// headparts sont recréés dans le plugin et leurs fichiers copiés dans le mod (voir Standalone).
    /// </summary>
    public bool Standalone { get; set; } = true;
}

/// <summary>Chemins du projet, config et environnement Mutagen (ordre de chargement du jeu).</summary>
public sealed class Context : IDisposable
{
    public required string Root { get; init; }
    public required Config Config { get; init; }
    /// <summary>Chemins propres à l'installation (settings.json, écran « Réglages » du Builder).</summary>
    public required Settings Settings { get; set; }
    public required IGameEnvironment<ISkyrimMod, ISkyrimModGetter> Env { get; init; }
    /// <summary>« Data virtuel » : où est un fichier relatif à Data (Data seul, ou mods de MO2 lus directement).</summary>
    public required GameFiles Files { get; init; }

    /// <summary>Jeu introuvable au démarrage (message pour le Builder), sinon null.</summary>
    public string? StartupProblem { get; init; }
    public string DataDir => Env.DataFolderPath;
    /// <summary>Dossiers CharGen et Presets dans Data. Sous MO2 lu directement, presets et exports RaceMenu peuvent aussi
    /// être dans overwrite\ ou dans un mod : pour un fichier, passer par CharGenPath / PresetPath.</summary>
    public string CharGenDir => Path.Combine(DataDir, "SKSE", "Plugins", "CharGen");
    public string PresetsDir => Path.Combine(CharGenDir, "Presets");
    public const string CharGenRel = @"SKSE\Plugins\CharGen";
    public const string PresetsRel = @"SKSE\Plugins\CharGen\Presets";
    /// <summary>Fichier relatif à CharGen (export RaceMenu…) : chemin réel du gagnant, sinon son chemin dans Data.</summary>
    public string CharGenPath(string rel) => Files.PathOf(Path.Combine(CharGenRel, rel));
    /// <summary>Chemin relatif à CharGen d'un fichier réel de CharGen (Data, overwrite ou mod).</summary>
    public string CharGenRelOf(string realPath)
    {
        var rel = Files.RelOf(realPath);
        return rel.StartsWith(CharGenRel + "\\", StringComparison.OrdinalIgnoreCase) ? rel[(CharGenRel.Length + 1)..] : Path.GetRelativePath(CharGenDir, realPath);
    }
    /// <summary>Preset RaceMenu (chemin relatif à Presets, sans .jslot) : chemin réel du gagnant, sinon son chemin dans Data.</summary>
    public string PresetPath(string preset) => Files.PathOf(Path.Combine(PresetsRel, preset + ".jslot"));
    /// <summary>Tête exportée GF_xxx.nif/.dds : dossier des réglages (--heads, config), sinon CharGen du Data virtuel.</summary>
    public string HeadPath(string file) => string.IsNullOrWhiteSpace(HeadsOverride ?? Config.HeadsDir) ? CharGenPath(file) : Path.Combine(HeadsOverride ?? Config.HeadsDir, file);
    public string? HeadsOverride { get; set; }
    /// <summary>Dossier de travail ; GF_WORK : bancs d'essai (work\diag), sans toucher aux index et choix du joueur.</summary>
    public string WorkDir => Dir(Environment.GetEnvironmentVariable("GF_WORK") is { Length: > 0 } w ? w : Path.Combine(Root, "work"));
    public string OutDir => Dir(Path.Combine(Root, "out"));
    public string DistDir => Dir(Settings.ResolvedOutputDir(Root));
    /// <summary>Sources Papyrus et .pex livrés (papyrus\pex) : ceux du projet, sinon ceux intégrés à l'exe.</summary>
    public string PapyrusDir => Path.Combine(AppResources.Dir(Root), "papyrus");
    public string CsvPath => Path.Combine(WorkDir, "presets.csv");
    public ILinkCache<ISkyrimMod, ISkyrimModGetter> Cache => Env.LinkCache;

    public HashSet<string> ActivePlugins { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Corps et tête installés (détectés dans Data à la première demande).</summary>
    public Bodies Bodies => bodies ??= Bodies.Detect(this);
    Bodies? bodies;

    /// <summary>Fichiers des BSA (index à la première demande, extraction dans work\bsa_cache).</summary>
    public Bsa Bsa { get { lock (bsaGate) return bsa ??= Bsa.Open(this); } }
    Bsa? bsa;
    readonly object bsaGate = new();

    /// <summary>Fichier relatif à Data, loose (Data virtuel) puis BSA (extrait), comme assets.find des scripts Python :
    /// chemin réel, ou null.</summary>
    public string? FindFile(string rel)
    {
        rel = rel.Replace('/', '\\').TrimStart('\\');
        return Files.Find(rel) ?? Bsa.Find(rel);
    }

    /// <summary>Fichier relatif à Meshes (les enregistrements HDPT omettent parfois « Meshes\ »), comme assets.mesh.</summary>
    public string? FindMesh(string rel)
    {
        rel = rel.Replace('/', '\\');
        return FindFile(rel.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? rel : @"meshes\" + rel);
    }

    /// <summary>Moteur des outils : « python » (scripts de offline\, comme avant) ou « dotnet » (code C#, défaut).
    /// Réglage de développeur GF_MOTEUR, gardé le temps de comparer les deux (plan autonome .NET).</summary>
    public static bool PythonEngine => string.Equals(Environment.GetEnvironmentVariable("GF_MOTEUR"), "python", StringComparison.OrdinalIgnoreCase);

    public static Context Open()
    {
        var root = FindRoot();
        // config.json du projet, sinon celui intégré à l'exe (exe livré, même dézippé sur une ancienne version)
        var cfgPath = Path.Combine(AppResources.Dir(root), "config.json");
        var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(cfgPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? new Config();
        var settings = Settings.Load(root);
        L.Init(root, settings.Language);
        Console.WriteLine("Chargement de l'ordre de chargement Skyrim SE...");
        // dossier Data : celui des réglages s'il est renseigné, sinon détecté par Mutagen (registre Steam/GOG)
        var dataDir = !string.IsNullOrWhiteSpace(settings.GameDataDir) ? settings.GameDataDir.Trim()
            : GameLocations.TryGetDataFolder(GameRelease.SkyrimSE, out var detected) ? detected.Path : "";
        // MO2 lu directement (GameFiles) : plugins pris dans les mods de MO2, ordre de chargement du profil
        var files = dataDir != "" ? GameFiles.Open(settings, dataDir) : GameFiles.DataOnly(dataDir);
        // plugins lus avec le partage « suppression » (SharedFileSystem) pour que Vortex/MO2 puissent les retirer pendant
        // que le Builder tourne
        var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE)
            .WithFileSystem(new SharedFileSystem(files));
        if (dataDir != "") builder = builder.WithTargetDataFolder(dataDir);
        int notFound = 0;
        if (files.Direct && files.Plugins is { } pl)
        {
            var names = pl.Plugins.Where(p => p.Enabled && ModKey.TryFromFileName(p.Name, out _)).Select(p => p.Name).ToList();
            notFound = names.Count(n => files.Find(n) == null);
            builder = builder.WithLoadOrder(names.Where(n => files.Find(n) != null).Select(n => ModKey.FromFileName(n)).ToArray());
        }
        // jeu introuvable (pas installé, dossier déplacé) : on démarre quand même sur un dossier Data vide, pour que le
        // Builder s'ouvre et que le dossier du jeu puisse être indiqué dans les Réglages
        string? problem = null;
        IGameEnvironment<ISkyrimMod, ISkyrimModGetter> env;
        try
        {
            env = builder.Build();
            _ = env.LoadOrder.ListedOrder.Count();
            if (!Directory.Exists(env.DataFolderPath))
                problem = L.T("Dossier du jeu introuvable : {0}. Indique le dossier Data de Skyrim dans les Réglages, puis relance l'application.", env.DataFolderPath);
        }
        catch (Exception e) when (e is not GfException)
        {
            problem = L.T("Skyrim Special Edition introuvable ({0}). Indique le dossier Data de Skyrim dans les Réglages, puis relance l'application.", e.Message);
            files = GameFiles.DataOnly(Path.Combine(root, "work", "jeu_introuvable"));
            env = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE).WithFileSystem(new SharedFileSystem())
                .WithTargetDataFolder(files.DataDir).Build();
        }
        if (!files.DataDir.Equals(env.DataFolderPath.Path, StringComparison.OrdinalIgnoreCase)) files = GameFiles.DataOnly(env.DataFolderPath);
        var ctx = new Context { Root = root, Config = cfg, Settings = settings, Env = env, Files = files, StartupProblem = problem };
        if (problem != null) Console.WriteLine("  ATTENTION : " + problem);
        foreach (var l in env.LoadOrder.ListedOrder)
            if (l.Enabled && files.Find(l.ModKey.FileName) != null) ctx.ActivePlugins.Add(l.ModKey.FileName);
        Console.WriteLine($"  {ctx.ActivePlugins.Count} plugins actifs, Data = {ctx.DataDir}");
        if (files.Direct) Console.WriteLine("  " + files.Summary);
        if (notFound > 0) Console.WriteLine("  ATTENTION : " + L.T("Mod Organizer 2 : {0} plugins cochés dans le profil introuvables (ni dans un mod activé ni dans Data).", notFound));
        Console.WriteLine("  " + ctx.Bodies.Summary);
        if (ctx.Bodies.Warning is { } bw) Console.WriteLine("  ATTENTION : " + bw);
        // MO2 hors mode direct : les mods ne sont dans Data que si l'outil est lancé depuis MO2 (système de fichiers virtuel)
        if (settings.Mo2Instance() is { } inst)
        {
            Console.WriteLine($"  MO2 : instance {inst.Dir}, profil {settings.ResolvedMo2ProfileDir()}"
                + (Mo2.UnderVfs() ? " (lancé depuis MO2)" : ""));
            if (Mo2.DataWarning(inst, Mo2.ReadModList(settings.ResolvedMo2ProfileDir()), files) is { } w)
                Console.WriteLine("  ATTENTION : " + w);
        }
        else if (settings.IsMo2) Console.WriteLine("  ATTENTION : MO2 choisi dans les Réglages, mais aucune instance trouvée (renseigne le dossier des mods).");
        return ctx;
    }

    public static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "config.json"))) return d.FullName;
        }
        // exe seul (fichiers intégrés) : son dossier est celui de l'application
        if (AppResources.Embedded && Path.GetDirectoryName(Environment.ProcessPath) is { } exeDir) return exeDir;
        throw new GfException("config.json introuvable (lancer depuis le dossier GenerateFollower).");
    }

    public static string Dir(string p) { Directory.CreateDirectory(p); return p; }

    /// <summary>Résout un enregistrement par EditorID, ou lève une erreur claire.</summary>
    public T Need<T>(string editorId, string usage) where T : class, ISkyrimMajorRecordGetter
    {
        if (Cache.TryResolve<T>(editorId, out var rec)) return rec;
        throw new GfException($"{typeof(T).Name} « {editorId} » introuvable ({usage}). Corrige config.json ou presets.csv.");
    }

    public T? Find<T>(string? editorId) where T : class, ISkyrimMajorRecordGetter
        => !string.IsNullOrWhiteSpace(editorId) && Cache.TryResolve<T>(editorId, out var rec) ? rec : null;

    /// <summary>
    /// Résout un FormKey lu dans un jslot. Pour un plugin marqué ESL, RaceMenu écrit l'ID d'exécution
    /// (index light sur 12 bits + ID local sur 12 bits) : on retente alors avec les 12 bits bas.
    /// </summary>
    public bool TryResolveRm<T>(FormKey key, out T rec) where T : class, ISkyrimMajorRecordGetter
    {
        if (Cache.TryResolve<T>(key, out rec!)) return true;
        return key.ID > 0xFFF && Cache.TryResolve<T>(new FormKey(key.ModKey, key.ID & 0xFFF), out rec!);
    }

    public void Dispose() => Env.Dispose();
}
