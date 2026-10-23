using System.Runtime.InteropServices;
using System.Text;
using WinForms = System.Windows.Forms;

namespace GenerateFollower;

/// <summary>
/// Filet de sécurité installé tout au début de Program.cs : une erreur non prévue (n'importe quel thread) écrit crash.txt
/// à côté de l'exe (sinon %LOCALAPPDATA%\GenerateFollower\crash.txt, dossier en lecture seule) puis affiche un message
/// (boîte de dialogue pour l'application, console pour les commandes en ligne). Rien n'est envoyé sur Internet. Ne couvre
/// pas un exe bloqué avant son démarrage (antivirus, SmartScreen) : aucun code ne s'exécute alors.
/// </summary>
static class CrashGuard
{
    static string command = "app";
    static int handled;

    public static void Install(string[] args)
    {
        command = args.Length > 0 ? args[0].ToLowerInvariant() : "app";
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
    }

    /// <summary>Écrit crash.txt et prévient l'utilisateur (une seule fois par exécution) ; rend le chemin du fichier.</summary>
    public static string? Report(Exception? e)
    {
        if (Interlocked.Exchange(ref handled, 1) != 0) return null;
        var path = Write(e);
        try
        {
            if (!L.Loaded) L.Init(Context.FindRoot(), Settings.Load(Context.FindRoot()).Language);
        }
        catch { }   // config absente ou illisible : texte français
        var text = L.T("GenerateFollower s'est arrêté à cause d'une erreur imprévue :") + "\n\n" + (e?.Message ?? "?") + "\n\n"
            + (path != null ? L.T("Détails : {0}", path) + "\n" + L.T("Joins ce fichier à ton retour (rien n'est envoyé automatiquement).")
                            : L.T("Le fichier crash.txt n'a pas pu être écrit."));
        try
        {
            if (command == "app")
                WinForms.MessageBox.Show(text, "GenerateFollower", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
            else
                Console.Error.WriteLine("ERREUR : " + text);
        }
        catch { }
        return path;
    }

    /// <summary>Écrit crash.txt (sans message) ; rend son chemin, ou null s'il n'a pas pu être écrit.</summary>
    public static string? Write(Exception? e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"GenerateFollower {typeof(CrashGuard).Assembly.GetName().Version} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Commande : {command}");
        sb.AppendLine($"Exe : {Environment.ProcessPath}");
        sb.AppendLine($"Dossier courant : {Environment.CurrentDirectory}");
        sb.AppendLine($"Windows : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine(e?.ToString() ?? "(erreur inconnue)");
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenerateFollower");
        foreach (var dir in new[] { exeDir, local })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "crash.txt");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch { }
        }
        return null;
    }
}
