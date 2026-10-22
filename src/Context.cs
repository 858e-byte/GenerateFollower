using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
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

    /// <summary>Jeu introuvable au démarrage (message pour le Builder), sinon null.</summary>
    public string? StartupProblem { get; init; }
    public string DataDir => Env.DataFolderPath;
    public string CharGenDir => Path.Combine(DataDir, "SKSE", "Plugins", "CharGen");
    public string PresetsDir => Path.Combine(CharGenDir, "Presets");
    public string HeadsDir => string.IsNullOrWhiteSpace(HeadsOverride ?? Config.HeadsDir) ? CharGenDir : (HeadsOverride ?? Config.HeadsDir);
    public string? HeadsOverride { get; set; }
    public string WorkDir => Dir(Path.Combine(Root, "work"));
    public string OutDir => Dir(Path.Combine(Root, "out"));
    public string DistDir => Dir(Settings.ResolvedOutputDir(Root));
    public string PapyrusDir => Path.Combine(Root, "papyrus");
    public string CsvPath => Path.Combine(WorkDir, "presets.csv");
    public ILinkCache<ISkyrimMod, ISkyrimModGetter> Cache => Env.LinkCache;

    public HashSet<string> ActivePlugins { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Corps et tête installés (détectés dans Data à la première demande).</summary>
    public Bodies Bodies => bodies ??= Bodies.Detect(this);
    Bodies? bodies;

    public static Context Open()
    {
        var root = FindRoot();
        var cfgPath = Path.Combine(root, "config.json");
        var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(cfgPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? new Config();
        var settings = Settings.Load(root);
        L.Init(root, settings.Language);
        Console.WriteLine("Chargement de l'ordre de chargement Skyrim SE...");
        // dossier Data : celui des réglages s'il est renseigné, sinon détecté par Mutagen ; plugins lus avec le partage
        // « suppression » (SharedFileSystem) pour que Vortex/MO2 puissent les retirer pendant que le Builder tourne
        var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE)
            .WithFileSystem(new SharedFileSystem());
        if (!string.IsNullOrWhiteSpace(settings.GameDataDir)) builder = builder.WithTargetDataFolder(settings.GameDataDir.Trim());
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
            env = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE).WithFileSystem(new SharedFileSystem())
                .WithTargetDataFolder(Path.Combine(root, "work", "jeu_introuvable")).Build();
        }
        var ctx = new Context { Root = root, Config = cfg, Settings = settings, Env = env, StartupProblem = problem };
        if (problem != null) Console.WriteLine("  ATTENTION : " + problem);
        foreach (var l in env.LoadOrder.ListedOrder)
            if (l.Enabled && File.Exists(Path.Combine(env.DataFolderPath, l.ModKey.FileName))) ctx.ActivePlugins.Add(l.ModKey.FileName);
        Console.WriteLine($"  {ctx.ActivePlugins.Count} plugins actifs, Data = {ctx.DataDir}");
        Console.WriteLine("  " + ctx.Bodies.Summary);
        if (ctx.Bodies.Warning is { } bw) Console.WriteLine("  ATTENTION : " + bw);
        // MO2 : les mods ne sont dans Data que si l'outil est lancé depuis MO2 (système de fichiers virtuel)
        if (settings.Mo2Instance() is { } inst)
        {
            Console.WriteLine($"  MO2 : instance {inst.Dir}, profil {settings.ResolvedMo2ProfileDir()}"
                + (Mo2.UnderVfs() ? " (lancé depuis MO2)" : ""));
            if (Mo2.DataWarning(inst, Mo2.ReadModList(settings.ResolvedMo2ProfileDir()), env.DataFolderPath) is { } w)
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
