using System.Reflection;

namespace GenerateFollower;

/// <summary>
/// Fichiers de l'application intégrés à l'exe (plan autonome .NET, étape 8 : un seul exe) : pages du Builder (builder\ :
/// index.html, traductions, three.js), scripts compilés (papyrus\pex\) et config.json par défaut. Le dossier du projet (et
/// les anciens paquets) garde ses fichiers ; un exe seul les dépose une fois dans work\app\&lt;version&gt;\ (dans son propre
/// dossier, comme work\), refaits à chaque nouvel exe. Rien n'est écrit ailleurs.
/// </summary>
public static class AppResources
{
    const string Prefix = "app/";
    static string? extracted;
    static readonly object gate = new();

    /// <summary>Dossier qui contient builder\, papyrus\ et config.json : celui de l'application s'il a builder\, sinon les
    /// fichiers déposés depuis l'exe.</summary>
    public static string Dir(string root)
    {
        if (Development(root)) return root;
        lock (gate) return extracted ??= Extract(root);
    }

    /// <summary>Dossier du projet (sources) : ses fichiers servent tels quels. Un exe livré prend toujours les siens, même
    /// dézippé par-dessus une ancienne version (son builder\ et son papyrus\ seraient périmés).</summary>
    public static bool Development(string root) =>
        File.Exists(Path.Combine(root, "src", "GenerateFollower.csproj")) || !Embedded;

    static bool webView2Ready;

    /// <summary>
    /// Exe seul : WebView2Loader.dll (seule DLL native, du SDK WebView2) déposée dans work\app\&lt;version&gt;\native\ et
    /// indiquée à WebView2, au lieu de l'extraction automatique de .NET dans %TEMP% (sur C:). À appeler avant tout appel à
    /// WebView2. Compilation normale (DLL à côté de l'exe) : rien à faire.
    /// </summary>
    public static void PrepareWebView2(string root)
    {
        lock (gate)
        {
            if (webView2Ready) return;
            webView2Ready = true;
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll"))
                || File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "WebView2Loader.dll"))
                || !Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(Prefix + "native/WebView2Loader.dll")) return;
            var dir = Path.Combine(extracted ??= Extract(root), "native");
            if (File.Exists(Path.Combine(dir, "WebView2Loader.dll")))
                Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(dir);
        }
    }

    /// <summary>Exe sans fichiers intégrés (compilation de développement) : rien à déposer.</summary>
    public static bool Embedded => Assembly.GetExecutingAssembly().GetManifestResourceNames().Any(n => n.StartsWith(Prefix));

    static string Extract(string root)
    {
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        // identifiant du module : change à chaque compilation, donc à chaque nouvel exe
        var id = $"{version}-{asm.ManifestModule.ModuleVersionId.ToString("N")[..8]}";
        var work = Environment.GetEnvironmentVariable("GF_WORK") is { Length: > 0 } w ? w : Path.Combine(root, "work");
        var parent = Path.Combine(work, "app");
        var dir = Path.Combine(parent, id);
        var done = Path.Combine(dir, ".complet");
        if (File.Exists(done)) return dir;
        // versions précédentes (exe remplacé) : retirées
        if (Directory.Exists(parent))
            foreach (var old in Directory.EnumerateDirectories(parent).Where(d => !d.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                try { Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix)))
        {
            var rel = name[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(dir, rel));
            if (!path.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = asm.GetManifestResourceStream(name)!;
            using var output = File.Create(path);
            input.CopyTo(output);
        }
        File.WriteAllText(done, id);
        return dir;
    }
}
