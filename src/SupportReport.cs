using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Rapport de diagnostic à joindre à un retour de bêta : installation (jeu, SKSE, mods requis en jeu, gestionnaire de
/// mods, corps et tête), outils, presets et verdicts, dernières erreurs. ZIP dans le dossier de sortie :
/// GF_Diagnostic_&lt;date&gt;.zip = diagnostic.txt + rapport du dernier build + erreurs des têtes et du Builder.
/// Écrit en anglais (lu par le support, quel que soit le joueur) ; nom d'utilisateur Windows remplacé par &lt;user&gt;.
/// Rien n'est envoyé : le joueur joint le ZIP lui-même.
/// </summary>
public static class SupportReport
{
    /// <summary>Version de l'application (InformationalVersion du projet, ex. 0.9.0-beta).</summary>
    public static string Version => typeof(SupportReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    /// <summary>Fichier des erreurs du Builder (requêtes en échec), joint au rapport.</summary>
    public static string ErrorLog(Context ctx) => Path.Combine(ctx.WorkDir, "builder_errors.log");

    public static void LogError(Context ctx, string what)
    {
        try { File.AppendAllText(ErrorLog(ctx), $"{DateTime.Now:s}  {what}{Environment.NewLine}"); } catch { }
    }

    /// <summary>Écrit le ZIP et renvoie (chemin du ZIP, texte de diagnostic.txt).</summary>
    public static (string Zip, string Text) Create(Context ctx)
    {
        var text = Anonymize(Build(ctx));
        var zip = Path.Combine(ctx.DistDir, $"GF_Diagnostic_{DateTime.Now:yyyyMMdd_HHmm}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("diagnostic.txt", text);
            foreach (var (file, name) in new[] { ("rapport_build.txt", "last_build_report.txt"), ("heads_offline_erreurs.txt", "head_errors.txt"),
                         ("builder_errors.log", "builder_errors.log") })
            {
                var p = Path.Combine(ctx.WorkDir, file);
                if (File.Exists(p)) Add(name, Anonymize(Tail(File.ReadAllText(p), 200_000)));
            }
        }
        return (zip, text);
    }

    static string Build(Context ctx)
    {
        var sb = new StringBuilder();
        void Line(string label, object? value) => sb.AppendLine($"{label,-28} {value}");
        void Head(string title) { sb.AppendLine(); sb.AppendLine($"== {title}"); }
        string Yes(bool b) => b ? "yes" : "NO";
        var s = ctx.Settings;
        var game = Path.GetDirectoryName(ctx.DataDir) ?? "";

        sb.AppendLine($"GenerateFollower {Version} - diagnostic report - {DateTime.Now:yyyy-MM-dd HH:mm}");
        Head("System");
        Line("Windows", RuntimeInformation.OSDescription);
        Line(".NET", RuntimeInformation.FrameworkDescription);
        Line("UI language", $"{L.Lang} (setting: {(s.Language == "" ? "auto" : s.Language)}, Windows: {System.Globalization.CultureInfo.CurrentUICulture.Name})");
        Line("Application folder", ctx.Root);
        Line("Settings file", Settings.FilePath(ctx.Root));

        Head("Game");
        Line("Data folder", ctx.DataDir);
        Line("SkyrimSE.exe", FileVersion(Path.Combine(game, "SkyrimSE.exe")));
        Line("SKSE (skse64_loader.exe)", FileVersion(Path.Combine(game, "skse64_loader.exe")));
        Line("Active plugins", ctx.ActivePlugins.Count);

        Head("Needed in game by the generated followers");
        // SKSE\Plugins : Data, ou un mod sous MO2 lu directement
        bool Plugin(string dll) => ctx.Files.Find(Path.Combine("SKSE", "Plugins", dll)) != null;
        Line("SKSE", Yes(File.Exists(Path.Combine(game, "skse64_loader.exe"))));
        Line("RaceMenu (skee64.dll)", Yes(Plugin("skee64.dll")) + $", RaceMenu.esp active: {Yes(ctx.ActivePlugins.Contains("RaceMenu.esp"))}");
        Line("PapyrusUtil", Yes(Plugin("PapyrusUtil.dll")));
        Line("UIExtensions.esp", Yes(ctx.ActivePlugins.Contains("UIExtensions.esp")));
        Line("UBE_AllRace.esp", Yes(ctx.ActivePlugins.Contains("UBE_AllRace.esp")));
        Line("High Poly Head.esm", Yes(ctx.ActivePlugins.Contains("High Poly Head.esm")));
        Line("RaceCompatibility.esm", Yes(ctx.ActivePlugins.Contains("RaceCompatibility.esm")));

        Head("Body and head");
        Line("Detected", ctx.Bodies.Summary);
        Line("Warning", ctx.Bodies.Warning ?? "-");

        Head("Mod manager");
        Line("Manager", s.ModManager);
        Line("Mods folder", $"{s.ResolvedModsDir()} (exists: {Yes(Directory.Exists(s.ResolvedModsDir()))})");
        var indexPath = ModIndex.IndexPath(ctx);
        if (File.Exists(indexPath))
            Line("Indexed mods", JsonNode.Parse(File.ReadAllText(indexPath))?["mods"]?.AsObject().Count);
        if (s.Mo2Instance() is { } inst)
        {
            var list = Mo2.ReadModList(s.ResolvedMo2ProfileDir());
            var (pl, seen) = list != null ? Mo2.PluginsInData(inst, list, ctx.Files) : (0, 0);
            Line("MO2 instance", inst.Dir);
            Line("MO2 profile", s.ResolvedMo2ProfileDir());
            Line("Started from MO2 (usvfs)", Yes(Mo2.UnderVfs()));
            Line("Mode", ctx.Files.Direct ? $"direct (mods read in the MO2 folder, {ctx.Files.Roots.Count} roots)" : "Data only");
            Line("Enabled mods", list?.Enabled.Count);
            Line("Their plugins seen in Data", $"{seen}/{pl}");
            Line("Warning", (list != null ? Mo2.DataWarning(inst, list, ctx.Files) : "modlist.txt not found") ?? "-");
        }
        Line("Archives folder", s.ResolvedArchivesDir() == "" ? "-" : s.ResolvedArchivesDir());

        Head("Tools");
        Line("Engine", Context.PythonEngine ? "python (GF_MOTEUR=python)" : "built-in (.NET)");
        Line("7-Zip (optional fallback)", $"{s.ResolvedSevenZip()} (exists: {Yes(File.Exists(s.ResolvedSevenZip()))})");
        Line("ffmpeg (optional fallback for voices)", Environment.GetEnvironmentVariable("PATH")?.Split(';').Any(d => File.Exists(Path.Combine(d.Trim().Trim('"'), "ffmpeg.exe"))) == true ? "found in PATH" : "-");
        string? webview;
        try { AppResources.PrepareWebView2(ctx.Root); webview = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(null); }
        catch (Exception e) when (e is not OutOfMemoryException) { webview = null; }
        Line("WebView2 (Windows)", webview ?? "not found (browser used)");
        if (Context.PythonEngine)
        {
            Line("Python", s.ResolvedPython());
            Line("texconv", $"{s.ResolvedTexconv(ctx.DataDir)} (exists: {Yes(File.Exists(s.ResolvedTexconv(ctx.DataDir)))})");
            Line("PyNifly", s.ResolvedPyNifly(ctx.Root));
            try { foreach (var l in Py.Run(ctx, "env_check.py").Split('\n', StringSplitOptions.RemoveEmptyEntries)) Line("  " + l.Split(':')[0], l[(l.IndexOf(':') + 1)..].Trim()); }
            catch (Exception e) { Line("  Python check", "FAILED: " + e.Message.Split('\n')[0]); }
        }
        var papyrus = Path.Combine(game, "Papyrus Compiler", "PapyrusCompiler.exe");
        Line("Papyrus compiler", File.Exists(papyrus) ? "yes (Creation Kit)" : "no (shipped .pex used)");

        Head("Presets");
        Line("CharGen presets (.jslot)", ctx.Files.Enumerate(Context.PresetsRel, "*.jslot", recursive: true).Count);
        Line("CharGen heads (.nif)", ctx.Files.Enumerate(Context.CharGenRel, "*.nif", recursive: true).Count);
        if (File.Exists(ctx.CsvPath))
        {
            var rows = PresetTable.Read(ctx.CsvPath);
            Line("Scanned", $"{rows.Count}, included {rows.Count(r => r.Inclure)}, UBE {rows.Count(r => r.Race.StartsWith("00UBE_", StringComparison.OrdinalIgnoreCase))}");
            Line("Scan status", string.Join(", ", rows.GroupBy(r => r.Statut.Split(' ')[0]).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        }
        foreach (var (file, label, key) in new[] { ("verdicts.json", "Verdicts", "niveau"), ("validation.json", "Reviews", "status") })
        {
            var p = Path.Combine(ctx.WorkDir, file);
            if (!File.Exists(p)) continue;
            var o = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
            Line(label, string.Join(", ", o.Select(kv => kv.Value?[key]?.GetValue<string>() ?? "?").GroupBy(x => x).Select(g => $"{g.Key} {g.Count()}")));
        }

        Head("Settings (settings.json)");
        var settingsFile = Settings.FilePath(ctx.Root);
        var settingsText = File.Exists(settingsFile) ? File.ReadAllText(settingsFile).Trim() : null;
        sb.AppendLine(settingsText == null ? "(none: all values detected)" : settingsText == "" ? "(empty file: portable mode, all values detected)" : settingsText);
        return sb.ToString();
    }

    static string FileVersion(string path)
    {
        if (!File.Exists(path)) return "NOT FOUND";
        var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
        return string.IsNullOrEmpty(v.FileVersion) ? "found" : v.FileVersion;
    }

    static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    /// <summary>Nom d'utilisateur Windows retiré des chemins (dossier utilisateur et nom seul).</summary>
    static string Anonymize(string s)
    {
        var user = Environment.UserName;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        s = s.Replace(home, @"C:\Users\<user>", StringComparison.OrdinalIgnoreCase)
             .Replace(home.Replace(@"\", @"\\"), @"C:\\Users\\<user>", StringComparison.OrdinalIgnoreCase);
        return user.Length >= 3 ? s.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase) : s;
    }
}
