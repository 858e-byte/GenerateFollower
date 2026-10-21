using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace GenerateFollower;

/// <summary>
/// Version application (double-clic sur GenerateFollower.exe, sans console) : une petite fenêtre de chargement (version,
/// barre de progression, étape en cours, « rien n'est envoyé sur Internet »), puis le Follower Builder dans sa fenêtre
/// (WebView2), sans dépendre du navigateur Edge. Runtime WebView2 : celui livré (deps\webview2, version fixe), sinon celui
/// de Windows. La fenêtre n'affiche que notre serveur local : pas d'outils de développement, de menu contextuel, de
/// raccourcis du navigateur, de téléchargements ni de SmartScreen ; un lien https vers un autre site s'ouvre dans le
/// navigateur normal. Version Lite (paquet sans deps\webview2), « app --navigateur » (Linux/Proton), ou secours (WebView2
/// absent, échec) : le navigateur par défaut, quel qu'il soit (Edge, Chrome, Brave, Opera, Firefox…),
/// et la petite fenêtre reste ouverte pour arrêter l'application. Ce qui s'écrivait dans la console va dans app.log
/// (work\ en mode portable, sinon %LOCALAPPDATA%\GenerateFollower) ; une erreur s'affiche dans une boîte de dialogue.
/// </summary>
public static class AppWindow
{
    public static int Run(bool browser = false)
    {
        int code = 0;
        var ui = new Thread(() =>
        {
            // erreur imprévue dans la fenêtre : pas la boîte « Continuer » de WinForms, mais CrashGuard (crash.txt + message)
            WinForms.Application.SetUnhandledExceptionMode(WinForms.UnhandledExceptionMode.ThrowException);
            WinForms.Application.SetHighDpiMode(WinForms.HighDpiMode.PerMonitorV2);
            WinForms.Application.EnableVisualStyles();
            WinForms.Application.SetCompatibleTextRenderingDefault(false);
            App app;
            try { app = new App(browser); }
            catch (Exception e)
            {
                var crash = CrashGuard.Write(e);
                WinForms.MessageBox.Show(L.T("GenerateFollower n'a pas pu démarrer :") + "\n\n" + e.Message
                    + (crash != null ? "\n\n" + L.T("Détails : {0}", crash) : ""), "GenerateFollower",
                    WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                code = 2;
                return;
            }
            using (app) WinForms.Application.Run(app);
            code = app.ExitCode;
        });
        ui.SetApartmentState(ApartmentState.STA);   // exigé par WebView2 (COM) et WinForms
        ui.Start();
        ui.Join();
        return code;
    }

    /// <summary>Déroulé de l'application sur le thread de l'interface : chargement, puis fenêtre du Builder ou navigateur.</summary>
    sealed class App : WinForms.ApplicationContext
    {
        readonly bool browser;
        readonly Splash splash;
        readonly LogWriter log;
        Context? ctx;
        string url = "";
        public int ExitCode { get; private set; }

        public App(bool browser)
        {
            this.browser = browser;
            var root = Context.FindRoot();
            L.Init(root, Settings.Load(root).Language);
            log = new LogWriter(LogPath(root));
            splash = new Splash();
            log.Step = splash.SetStep;
            Console.SetOut(log);
            Console.SetError(log);
            splash.FormClosed += (_, _) => { if (MainForm == splash) ExitThread(); };
            MainForm = splash;
            splash.Show();
            // diagnostic : GF_FENETRE_TEST=<capture.png> -> image de la fenêtre de chargement après 4 s (<capture>.splash.png)
            if (Environment.GetEnvironmentVariable("GF_FENETRE_TEST") is { Length: > 0 } capture)
            {
                var timer = new WinForms.Timer { Interval = 4000 };
                timer.Tick += (_, _) =>
                {
                    timer.Dispose();
                    if (splash.IsDisposed) return;
                    using var bmp = new Drawing.Bitmap(splash.Width, splash.Height);
                    splash.DrawToBitmap(bmp, new Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(capture + ".splash.png", Drawing.Imaging.ImageFormat.Png);
                };
                timer.Start();
            }
            Task.Run(Load);
        }

        /// <summary>Hors du thread de l'interface : ordre de chargement, presets, serveur local.</summary>
        void Load()
        {
            try
            {
                ctx = Context.Open();
                var port = FreePort(5180);
                url = $"http://localhost:{port}/";
                var server = new Thread(() =>
                {
                    try { Builder.Serve(ctx, port); }
                    catch (Exception e) { Console.WriteLine("ERREUR du Builder : " + e); OnUi(() => Fail(e.Message)); }
                }) { IsBackground = true };
                server.Start();
                // le Builder rescanne les presets avant d'écouter : attendre qu'il réponde
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                    for (var t = 0; ; t++)
                    {
                        try { http.GetAsync(url + "api/settings").Result.EnsureSuccessStatusCode(); break; }
                        catch when (t < 600 && server.IsAlive) { Thread.Sleep(500); }
                    }
                OnUi(Ready);
            }
            catch (Exception e)
            {
                Console.WriteLine("ERREUR : " + e);
                OnUi(() => Fail(e is GfException ? e.Message : e.GetType().Name + " : " + e.Message));
            }
        }

        /// <summary>Exécute sur le thread de l'interface (par la fenêtre encore ouverte, ou rien si tout est fermé).</summary>
        void OnUi(Action a)
        {
            var target = MainForm is { IsDisposed: false } f ? f : splash.IsDisposed ? null : splash;
            try { target?.BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        void Ready()
        {
            // version Lite (paquet sans deps\webview2) : le navigateur par défaut, quel qu'il soit
            if (!browser && ctx != null)
            {
                if (!Directory.Exists(Path.Combine(ctx.Root, "deps", "webview2"))) Console.WriteLine("Version Lite (sans WebView2 livré).");
                else if (Window(ctx)) return;
            }
            InBrowser();
        }

        /// <summary>Navigateur par défaut ; la petite fenêtre reste ouverte : la fermer arrête l'application.</summary>
        void InBrowser()
        {
            Console.WriteLine($"Ouverture dans le navigateur par défaut ({url}).");
            // diagnostic : GF_FENETRE_TEST -> pas de vrai navigateur ; capture de la petite fenêtre, puis fermeture
            var capture = Environment.GetEnvironmentVariable("GF_FENETRE_TEST");
            if (string.IsNullOrEmpty(capture)) OpenBrowser();
            if (!splash.Visible) splash.Show();
            MainForm = splash;
            splash.ShowBrowserMode(OpenBrowser);
            if (string.IsNullOrEmpty(capture)) return;
            var timer = new WinForms.Timer { Interval = 2000 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                using var bmp = new Drawing.Bitmap(splash.Width, splash.Height);
                splash.DrawToBitmap(bmp, new Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(capture + ".navigateur.png", Drawing.Imaging.ImageFormat.Png);
                File.WriteAllText(capture + ".txt", $"navigateur {url}\n");
                splash.Close();
            };
            timer.Start();
        }

        void OpenBrowser() => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        void Fail(string message)
        {
            ExitCode = 2;
            WinForms.MessageBox.Show(splash is { IsDisposed: false, Visible: true } ? splash : null,
                L.T("GenerateFollower n'a pas pu démarrer :") + "\n\n" + message + "\n\n" + L.T("Détails : {0}", log.Path),
                "GenerateFollower", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
            ExitThread();
        }

        /// <summary>Fenêtre du Builder (WebView2) ; false si WebView2 est absent (le navigateur prend le relais).</summary>
        bool Window(Context ctx)
        {
            // runtime livré (deps\webview2, version fixe), sinon celui de Windows
            string? runtime = Path.Combine(ctx.Root, "deps", "webview2");
            if (!File.Exists(Path.Combine(runtime, "msedgewebview2.exe")) || RuntimeVersion(runtime) == null)
            {
                if (Directory.Exists(runtime)) Console.WriteLine("WebView2 livré inutilisable : essai de celui de Windows.");
                runtime = null;
                if (RuntimeVersion(null) == null) { Console.WriteLine("WebView2 introuvable."); return false; }
            }
            var profile = Context.Dir(Path.Combine(Path.GetDirectoryName(log.Path)!, "webview2_profile"));
            var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
            var form = new WinForms.Form
            {
                Text = $"Follower Builder - GenerateFollower {SupportReport.Version}",
                Width = 1500, Height = 950, StartPosition = WinForms.FormStartPosition.CenterScreen,
                Icon = AppIcon(), BackColor = Splash.Back,
            };
            var view = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = WinForms.DockStyle.Fill, DefaultBackgroundColor = Splash.Back };
            form.Controls.Add(view);
            form.FormClosed += (_, _) => { if (MainForm == form) { Console.WriteLine("Fenêtre fermée : arrêt."); ExitThread(); } };
            form.Load += async (_, _) =>
            {
                try
                {
                    var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false };
                    var env = await CoreWebView2Environment.CreateAsync(runtime, profile, options);
                    await view.EnsureCoreWebView2Async(env);
                    var core = view.CoreWebView2;
                    var s = core.Settings;
                    s.AreDevToolsEnabled = false;
                    s.AreDefaultContextMenusEnabled = false;
                    s.AreBrowserAcceleratorKeysEnabled = false;
                    s.IsStatusBarEnabled = false;
                    s.IsGeneralAutofillEnabled = false;
                    s.IsPasswordAutosaveEnabled = false;
                    // SmartScreen : inutile pour notre page locale, et rien n'est envoyé à Microsoft
                    s.IsReputationCheckingRequired = false;
                    core.NavigationStarting += (_, e) =>
                    {
                        if (e.Uri.StartsWith(origin + "/", StringComparison.OrdinalIgnoreCase)) return;
                        e.Cancel = true;
                        External(e.Uri);
                    };
                    core.NewWindowRequested += (_, e) => { e.Handled = true; External(e.Uri); };
                    core.DownloadStarting += (_, e) => e.Cancel = true;
                    core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                    core.Navigate(url);
                    Console.WriteLine($"Follower Builder ouvert dans sa fenêtre (WebView2 {env.BrowserVersionString}"
                        + (runtime != null ? ", version livrée" : ", version de Windows") + "). Fermer la fenêtre arrête l'application.");
                    // la petite fenêtre de chargement laisse la place au Builder
                    MainForm = form;
                    splash.Close();
                    // diagnostic : GF_FENETRE_TEST=<capture.png> -> capture après 25 s, puis fermeture
                    if (Environment.GetEnvironmentVariable("GF_FENETRE_TEST") is { Length: > 0 } capture)
                    {
                        await Task.Delay(15000);
                        // outils externes (Python, texconv, 7-Zip, ffmpeg) : tête et vignettes de skin d'un preset, extrait de voix
                        await core.ExecuteScriptAsync("document.querySelector('#list li[data-id=\"7C25FC8F\"]')?.click()");
                        await Task.Delay(15000);
                        await core.ExecuteScriptAsync("fetch('/api/voice-sample?voix=FemaleYoungEager').catch(()=>0)");
                        await Task.Delay(10000);
                        var webgl = await core.ExecuteScriptAsync("(()=>{const g=document.createElement('canvas').getContext('webgl2');return g?g.getParameter(g.VERSION):'aucun'})()");
                        var n = await core.ExecuteScriptAsync("document.querySelectorAll('#list li').length");
                        using (var fs = File.Create(capture)) await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                        File.WriteAllText(capture + ".txt", $"WebView2 {env.BrowserVersionString} ({(runtime != null ? "livrée" : "Windows")})\nWebGL {webgl}\npresets {n}\n");
                        form.Close();
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("La fenêtre WebView2 n'a pas démarré : " + e);
                    form.Hide();
                    InBrowser();
                    form.Close();
                }
            };
            form.Show();
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ctx?.Dispose(); log.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Petite fenêtre de chargement : nom, version, barre de progression, étape en cours (sans chemin de fichier) et la
    /// garantie que tout reste sur le PC ; en mode navigateur, elle reste ouverte pour arrêter l'application.
    /// </summary>
    sealed class Splash : WinForms.Form
    {
        public static readonly Drawing.Color Back = Drawing.Color.FromArgb(23, 26, 33);
        readonly WinForms.Label step;
        readonly WinForms.ProgressBar bar;
        readonly WinForms.Label note;
        readonly WinForms.Button reopen;

        public Splash()
        {
            Text = "GenerateFollower";
            Icon = AppIcon();
            FormBorderStyle = WinForms.FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = WinForms.FormStartPosition.CenterScreen;
            AutoScaleMode = WinForms.AutoScaleMode.Dpi;
            ClientSize = new Drawing.Size(520, 210);
            BackColor = Back;
            ForeColor = Drawing.Color.White;
            var font = new Drawing.Font("Segoe UI", 10f);
            Font = font;
            var title = new WinForms.Label
            {
                Text = "Follower Builder", Font = new Drawing.Font("Segoe UI Semibold", 17f), AutoSize = true,
                Location = new Drawing.Point(24, 18),
            };
            var version = new WinForms.Label
            {
                Text = "GenerateFollower " + SupportReport.Version, ForeColor = Drawing.Color.FromArgb(150, 160, 180),
                AutoSize = true, Location = new Drawing.Point(27, 56),
            };
            step = new WinForms.Label
            {
                Text = L.T("Démarrage…"), AutoSize = false, Location = new Drawing.Point(27, 92), Size = new Drawing.Size(466, 24),
            };
            bar = new WinForms.ProgressBar
            {
                Style = WinForms.ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Location = new Drawing.Point(27, 120),
                Size = new Drawing.Size(466, 10),
            };
            note = new WinForms.Label
            {
                Text = L.T("Tout se passe sur ton PC : rien n'est envoyé sur Internet, le dossier du jeu n'est jamais modifié."),
                ForeColor = Drawing.Color.FromArgb(150, 160, 180), AutoSize = false, Location = new Drawing.Point(27, 145),
                Size = new Drawing.Size(466, 44), Font = new Drawing.Font("Segoe UI", 9f),
            };
            reopen = new WinForms.Button
            {
                Text = L.T("Rouvrir dans le navigateur"), AutoSize = true, Visible = false, Location = new Drawing.Point(27, 140),
                FlatStyle = WinForms.FlatStyle.System,
            };
            Controls.AddRange([title, version, step, bar, note, reopen]);
        }

        /// <summary>Étape affichée (appelable depuis n'importe quel thread).</summary>
        public void SetStep(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(() => SetStep(text)); } catch (InvalidOperationException) { } return; }
            step.Text = text;
        }

        public void ShowBrowserMode(Action reopenAction)
        {
            step.Text = L.T("Le Follower Builder est ouvert dans ton navigateur.");
            bar.Visible = false;
            reopen.Visible = true;
            reopen.Click += (_, _) => reopenAction();
            note.Location = new Drawing.Point(27, 175);
            note.Text = L.T("Ferme cette fenêtre pour arrêter GenerateFollower.");
            ClientSize = new Drawing.Size(520, 210);
            Activate();
        }
    }

    /// <summary>
    /// Remplace la console : tout va dans app.log ; certaines lignes connues donnent l'étape affichée dans la fenêtre de
    /// chargement (texte traduit, sans chemin de fichier).
    /// </summary>
    sealed class LogWriter : TextWriter
    {
        readonly StreamWriter file;
        readonly StringBuilder line = new();
        public string Path { get; }
        public Action<string>? Step { get; set; }
        public override Encoding Encoding => Encoding.UTF8;

        public LogWriter(string path)
        {
            Path = path;
            file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            file.WriteLine($"GenerateFollower {SupportReport.Version} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        public override void Write(char value)
        {
            lock (line)
            {
                if (value == '\n') { Flush(line.ToString().TrimEnd('\r')); line.Clear(); }
                else line.Append(value);
            }
        }

        public override void Write(string? value) { if (value != null) foreach (var c in value) Write(c); }

        void Flush(string text)
        {
            file.WriteLine(text);
            var step = text.StartsWith("Chargement de l'ordre de chargement") ? L.T("Lecture de l'ordre de chargement de Skyrim…")
                : text.Contains(" plugins actifs") ? L.T("Plugins lus, préparation…")
                : text.StartsWith("Scan des presets") ? L.T("Lecture des presets RaceMenu…")
                : text.StartsWith("Follower Builder :") ? L.T("Ouverture de la fenêtre…")
                : null;
            if (step != null) Step?.Invoke(step);
        }

        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Journal de l'application : à côté des réglages (work\ en mode portable, sinon %LOCALAPPDATA%).</summary>
    static string LogPath(string root)
    {
        var settingsDir = Path.GetDirectoryName(Settings.FilePath(root))!;
        var dir = settingsDir.Equals(root, StringComparison.OrdinalIgnoreCase) ? Path.Combine(root, "work") : settingsDir;
        return Path.Combine(Context.Dir(dir), "app.log");
    }

    static Drawing.Icon? AppIcon()
    {
        try { return Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { return null; }
    }

    /// <summary>Version d'un runtime WebView2 (dossier livré, ou null : celui de Windows), ou null s'il est absent ou abîmé.</summary>
    static string? RuntimeVersion(string? dir)
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(dir) is { Length: > 0 } v ? v : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Lien vers un autre site : seulement https, dans le navigateur normal.</summary>
    static void External(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
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
}
