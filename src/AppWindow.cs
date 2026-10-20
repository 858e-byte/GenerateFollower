using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GenerateFollower;

/// <summary>
/// Version application : le Follower Builder dans une fenêtre à lui, sans barre d'adresse, au lieu d'un onglet du
/// navigateur. La fenêtre est Microsoft Edge en mode application (même moteur que WebView2, présent sur Windows 10/11 ;
/// rien à télécharger), avec un profil propre à GenerateFollower ; fermer la fenêtre arrête l'application.
/// Sans Edge : le navigateur par défaut, et l'application tourne jusqu'à la fermeture de la console.
/// </summary>
public static class AppWindow
{
    public static int Run(Context ctx)
    {
        var port = FreePort(5180);
        var url = $"http://localhost:{port}/";
        var server = new Thread(() =>
        {
            try { Builder.Serve(ctx, port); }
            catch (Exception e) { Console.Error.WriteLine("ERREUR du Builder : " + e.Message); Environment.Exit(2); }
        }) { IsBackground = true };
        server.Start();

        // le Builder rescanne les presets avant d'écouter : attendre qu'il réponde
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
            for (var t = 0; ; t++)
            {
                try { http.GetAsync(url + "api/settings").Result.EnsureSuccessStatusCode(); break; }
                catch when (t < 600 && server.IsAlive) { Thread.Sleep(500); }
            }

        var edge = EdgePath();
        if (edge == null)
        {
            Console.WriteLine($"Microsoft Edge introuvable : ouverture dans le navigateur par défaut ({url}). Ferme cette fenêtre pour arrêter.");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            server.Join();
            return 0;
        }

        var profile = Context.Dir(ProfileDir(ctx));
        var psi = new ProcessStartInfo(edge) { UseShellExecute = false };
        foreach (var a in new[] { $"--app={url}", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check",
                     "--disable-features=Translate", "--window-size=1500,950" })
            psi.ArgumentList.Add(a);
        Process.Start(psi);
        Console.WriteLine($"Follower Builder ouvert dans sa fenêtre ({url}). Fermer la fenêtre arrête l'application.");

        // Edge peut confier la fenêtre à un processus déjà ouvert avec ce profil : on attend la fin de tous les
        // processus Edge de notre profil, pas seulement de celui qu'on a lancé
        Thread.Sleep(3000);
        while (EdgeRunning(profile)) Thread.Sleep(1500);
        Console.WriteLine("Fenêtre fermée : arrêt.");
        return 0;
    }

    /// <summary>Port <paramref name="preferred"/> s'il est libre, sinon un port libre donné par le système.</summary>
    static int FreePort(int preferred)
    {
        foreach (var p in new[] { preferred, 0 })
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new GfException("aucun port local libre");
    }

    /// <summary>Profil Edge de l'application : à côté des réglages (work\ en mode portable, sinon %LOCALAPPDATA%).</summary>
    static string ProfileDir(Context ctx)
    {
        var settingsDir = Path.GetDirectoryName(Settings.FilePath(ctx.Root))!;
        return settingsDir.Equals(ctx.Root, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(ctx.WorkDir, "edge_profile") : Path.Combine(settingsDir, "edge_profile");
    }

    static string? EdgePath()
    {
        foreach (var f in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData })
        {
            var p = Path.Combine(Environment.GetFolderPath(f), "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Edge tourne-t-il avec notre profil ? Edge garde le fichier « lockfile » du profil ouvert sans partage.</summary>
    static bool EdgeRunning(string profile)
    {
        var lockFile = Path.Combine(profile, "lockfile");
        if (!File.Exists(lockFile)) return false;
        try { using (File.Open(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
