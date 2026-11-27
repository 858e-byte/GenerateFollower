using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace GenerateFollower.Native;

/// <summary>
/// Fenêtre « Réglages » (settings.json) de la fenêtre native, comme celle de la page : libellé et aide de chaque réglage,
/// listes à choix (langue, gestionnaire de mods, mode MO2, encodage, références placées, fonctions expérimentales,
/// nouvelles versions), valeur utilisée (« Utilisé : … » / « Introuvable »), question si on ferme sans enregistrer,
/// rapport de diagnostic (/api/diagnostic).
/// </summary>
public sealed partial class BuilderForm
{
    static readonly (string Key, string Label, string Help)[] SettingsList =
    [
        ("language", "Langue", "Vide : langue de Windows si elle est traduite (français, anglais, espagnol, russe, chinois, japonais, coréen), sinon anglais."),
        ("gameDataDir", "Dossier Data du jeu", "Vide : détecté automatiquement (Steam, GOG). Un changement demande de redémarrer le Builder."),
        ("modManager", "Gestionnaire de mods", ""),
        ("modsDir", "Dossier des mods du gestionnaire", "Vortex : dossier de staging (%APPDATA%\\Vortex\\skyrimse\\mods par défaut). MO2 : dossier mods de l'instance, ou directement son fichier ModOrganizer.ini (instance portable : à côté de ModOrganizer.exe)."),
        ("mo2Profile", "Profil MO2", "MO2 seulement. Vide : le profil sélectionné dans MO2. Sinon le nom du profil ou le chemin de son dossier."),
        ("mo2Mode", "Mode MO2", "MO2 seulement. Automatique : les mods sont lus directement dans le dossier de MO2 (inutile de lancer GenerateFollower depuis MO2) ; lancé depuis MO2, Data suffit. Un changement demande de redémarrer le Builder."),
        ("archivesDir", "Dossier des archives téléchargées (facultatif)", "Archives zip/7z/rar où chercher des skins non installés (lues par l'application ; 7-Zip seulement en secours). Vide : dossier de téléchargements du gestionnaire (MO2 : downloads de l'instance ; Vortex : %APPDATA%\\Vortex\\downloads\\skyrimse)."),
        ("outputDir", "Dossier de sortie des ZIP", "Vide : dist\\ du projet."),
        ("placedRefs", "Références placées des followers", "Oui (par défaut) : chaque follower a une référence placée dans une petite cellule de son pack, visible dans xEdit et par les mods qui cherchent les PNJ dans les plugins (Are You There). Non : followers créés en cours de partie, comme en 1.1.1. « Reconstruire » garde le choix fait pour chaque pack."),
        ("pluginEncoding", "Encodage des textes des plugins", "Pour les noms d'objets et de PNJ des mods traduits (chinois, russe, japonais…) qui s'affichent en caractères brouillés. Automatique : UTF-8, sinon l'encodage de la langue du jeu (Skyrim.ini). Un changement demande de redémarrer le Builder."),
        ("experimental", "Fonctions expérimentales", "Non (par défaut) : chaque follower utilise son propre export RaceMenu. Oui : propose « Tête d'un autre preset (sans Export Head) » au poste Visage et dans « Plusieurs followers » ; le résultat peut s'écarter du visage de RaceMenu."),
        ("checkUpdates", "Vérifier les nouvelles versions", "Oui (par défaut) : au démarrage, une fois par jour, l'application demande à GitHub le numéro de la dernière version publiée et affiche un lien en haut de la page s'il y en a une plus récente. Rien d'autre n'est envoyé, rien n'est téléchargé ni installé. Non : aucune requête."),
        ("apercu3D", "Aperçu 3D", "Non : pas de vue 3D de la tête ni du corps, pour les cartes graphiques avec peu de mémoire vidéo ou quand Skyrim tourne à côté. Le reste du Builder fonctionne pareil et les packs construits sont identiques."),
        ("sevenZip", "7-Zip (7z.exe)", "Facultatif : seulement pour une archive que l'application ne sait pas lire."),
        ("texconv", "texconv.exe", "Vide : celui livré avec l'application, sinon celui de SSEEdit dans Data."),
        ("python", "Python", "Vide : le Python livré avec l'application. Sinon un lanceur (py, python) ou le chemin de python.exe."),
        ("pyNifly", "PyNifly", "Dossier qui contient le paquet « pyn » (add-on Blender io_scene_nifly)."),
    ];

    static (string Value, string Text)[]? SettingChoices(string k) => k switch
    {
        "modManager" => [("Vortex", "Vortex"), ("MO2", "MO2"), ("Aucun", L.T("Aucun"))],
        "language" => [("", L.T("Automatique (langue de Windows)")), .. L.Languages],
        "placedRefs" or "checkUpdates" or "apercu3D" => [("", L.T("Oui (par défaut)")), ("non", L.T("Non"))],
        "experimental" => [("", L.T("Non (par défaut)")), ("oui", L.T("Oui"))],
        "pluginEncoding" => [("", L.T("Automatique")), ("utf8", "UTF-8"), ("1252", L.T("Occidental (Windows-1252)")), ("1250", L.T("Europe centrale (Windows-1250)")),
            ("1251", L.T("Cyrillique (Windows-1251)")), ("932", L.T("Japonais (Shift-JIS, 932)"))],
        "mo2Mode" => [("", L.T("Automatique")), ("direct", L.T("Lire les mods dans le dossier de MO2")), ("vfs", L.T("Data seulement (lancé depuis MO2)"))],
        _ => null,
    };

    /// <summary>« Tout vérifier » : verdict de chaque preset de la liste affichée (filtres compris), comme la page.</summary>
    async Task VerifyAll()
    {
        var todo = shown.Select(p => Str(p, "id")).ToList();
        if (todo.Count == 0 || new ConfirmDialog(L.T("Tout vérifier"), L.T("Vérifier {0} preset(s) ? (quelques secondes chacun)", todo.Count)).ShowDialog(this) != DialogResult.OK) return;
        for (int i = 0; i < todo.Count; i++)
        {
            Say(L.T("Vérification {0}/{1}…", i + 1, todo.Count));
            try
            {
                var r = await Api("api/preset/" + todo[i]);
                if (presets.OfType<JsonObject>().FirstOrDefault(x => Str(x, "id") == todo[i]) is { } p && r["verdict"] is JsonObject v) p["verdict"] = v.DeepClone();
            }
            catch (Exception e) when (e is not OutOfMemoryException) { }
            if (i % 5 == 4) list.Invalidate();
        }
        Fill();
        Say(L.T("Tout vérifier") + " : " + L.T("{0} presets", todo.Count));
    }

    /// <summary>Au démarrage : avertissement d'installation (MO2 lancé hors de MO2…) et lien vers une nouvelle version, comme la page.</summary>
    async Task StartupNotices(FlowLayoutPanel bar)
    {
        try
        {
            var s = await Api("api/settings");
            if (Str(s, "avertissement") is { Length: > 0 } av && Environment.GetEnvironmentVariable("GF_NATIF_SHOT") == null) Message(L.T("Installation"), av);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            JsonNode? r = null;
            try { r = await Api("api/update"); } catch (Exception e) when (e is not OutOfMemoryException) { }
            if (Bool(r, "nouvelle") && Str(r, "url") is { Length: > 0 } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var link = new LinkButton(L.T("Nouvelle version {0} disponible", Str(r, "derniere"))) { Width = 240, Height = 34, Margin = new Padding(6, 0, 0, 0), ForeColor = Theme.Warn };
                link.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { } };
                bar.Controls.Add(link);
                return;
            }
            if (Str(r, "etat") != "en cours") return;
            await Task.Delay(8000);
        }
    }

    async Task ShowSettings()
    {
        await Guard(async () =>
        {
            var r = (await Api("api/settings")).AsObject();
            using var dlg = new SettingsDialog(this, r);
            dlg.ShowDialog(this);
            if (dlg.Saved?["valeurs"] is JsonObject vals) Apply3D(!Str(vals, "apercu3D").Trim().Equals("non", StringComparison.OrdinalIgnoreCase));
            if (dlg.Saved?["recharger"] is not null) Message(L.T("Réglages"), L.T("Langue changée : relance le Follower Builder pour l'appliquer à la fenêtre."));
        });
    }

    sealed class SettingsDialog : DarkDialog
    {
        readonly BuilderForm f;
        readonly Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
        readonly VStack v = new() { BackColor = Theme.Bg, Gap = 6 };
        readonly Dictionary<string, Func<string>> values = [];
        JsonObject original = [];
        string shownValues = "";
        bool closing;
        public JsonObject? Saved;

        public SettingsDialog(BuilderForm f, JsonObject r) : base(L.T("Réglages"), new Size(860, 700))
        {
            this.f = f;
            scroll.Controls.Add(v);
            scroll.Resize += (_, _) => v.Width = scroll.ClientSize.Width - 4;
            Body.Controls.Add(scroll);
            var save = new FlatButton(L.T("Enregistrer")) { Width = 160, Primary = true, Margin = new Padding(8, 0, 0, 0) };
            save.Click += async (_, _) => await Save(save);
            Buttons.Controls.Add(save);
            var close = new FlatButton(L.T("Fermer")) { Width = 120, Margin = new Padding(8, 0, 0, 0) };
            close.Click += (_, _) => Close();
            Buttons.Controls.Add(close);
            var diag = new FlatButton(L.T("Rapport de diagnostic")) { Width = 220, Margin = new Padding(8, 0, 0, 0) };
            diag.Click += async (_, _) => await Diagnostic(diag);
            Buttons.Controls.Add(diag);
            Render(r);
            Shown += (_, _) => { v.Width = scroll.ClientSize.Width - 4; v.PerformLayout(); Theme.DarkScroll(scroll); };
            FormClosing += (_, e) =>
            {
                if (closing || CurrentValues() == shownValues) return;
                if (new ConfirmDialog(L.T("Réglages"), L.T("Des réglages ont été modifiés sans être enregistrés. Fermer quand même ?")).ShowDialog(this) != DialogResult.OK) e.Cancel = true;
            };
        }

        string CurrentValues() => string.Join("\u0001", values.Select(kv => kv.Key + "=" + kv.Value().Trim()));

        void Render(JsonObject r)
        {
            v.SuspendLayout();
            foreach (Control c in v.Controls.Cast<Control>().ToList()) c.Dispose();
            v.Controls.Clear();
            values.Clear();
            original = r["valeurs"] as JsonObject ?? [];
            var used = r["utilises"] as JsonObject ?? [];
            var tip = new ToolTip();
            v.Disposed += (_, _) => tip.Dispose();
            v.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Muted,
                Text = L.T("Fichier : {0}", Str(r, "fichier")) + "\n" + Str(r, "installation") + (Str(r, "avertissement") is { Length: > 0 } av ? "\n⚠ " + av : "") });
            // texconv, Python, PyNifly : seulement avec l'ancien moteur (scripts Python)
            string[] pyOnly = ["texconv", "python", "pyNifly"];
            foreach (var (k, label, help) in SettingsList)
            {
                if (Str(r, "moteur") != "python" && pyOnly.Contains(k)) continue;
                var val = original[k] is JsonValue jv ? jv.ToString() : "";
                var u = used[k];
                v.Controls.Add(new TextLabel { Text = L.T(label), Font = Theme.Bold });
                Control input;
                if (SettingChoices(k) is { } choices)
                {
                    var p = new Picker();
                    p.SetItems(choices.Select(c => c.Value).ToList(), choices.Select(c => c.Text).ToList());
                    p.Index = Math.Max(0, p.Items.IndexOf(val));
                    values[k] = () => p.Value ?? "";
                    input = p;
                }
                else
                {
                    var fld = new Field(Str(u, "valeur") is { Length: > 0 } uv ? L.T("auto : {0}", uv) : "");
                    fld.Box.Text = val;
                    fld.Box.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Save(null); } };
                    values[k] = () => fld.Box.Text;
                    input = fld;
                }
                v.Controls.Add(input);
                if (help.Length > 0) { tip.SetToolTip(input, L.T(help)); v.Controls.Add(new TextLabel { Text = L.T(help), Font = Theme.Small, ForeColor = Theme.Muted }); }
                if (u != null)
                {
                    bool ok = u["ok"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && ob;
                    v.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = ok ? Theme.Ok : Theme.Err,
                        Text = (ok ? L.T("Utilisé") : L.T("Introuvable")) + " : " + (Str(u, "valeur") is { Length: > 0 } uv2 ? uv2 : L.T("(aucun)")) });
                }
            }
            v.ResumeLayout();
            v.PerformLayout();
            shownValues = CurrentValues();
        }

        async Task Save(FlatButton? b)
        {
            var body = new JsonObject();
            foreach (var (k, get) in values) body[k] = get().Trim();
            var label = b?.Text;
            // un nouveau dossier d'archives fait relire les archives : quelques minutes la première fois
            if (b != null) { b.Enabled = false; b.Text = L.T("Enregistrement… (catalogue des skins)"); }
            try
            {
                var r = (await f.Api("api/settings", body)).AsObject();
                Saved = r;
                if (r["recharger"] is not null) { closing = true; Close(); return; }   // nouvelle langue
                Render(r);
                if (Str(r, "message") is { Length: > 0 } msg)
                {
                    new MessageDialog(L.T("Réglages enregistrés"), msg).ShowDialog(this);
                    f.skinInfo = null;   // catalogue des skins refait : relu à la prochaine ouverture du poste
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
            if (b != null) { b.Enabled = true; b.Text = label!; }
        }

        async Task Diagnostic(FlatButton b)
        {
            var label = b.Text;
            b.Enabled = false; b.Text = L.T("Création…");
            try
            {
                var r = await f.Api("api/diagnostic", new JsonObject());
                new MessageDialog(L.T("Rapport de diagnostic"), L.T("Joins ce fichier à ton retour : {0}", Str(r, "zip")) + "\n\n" + Str(r, "texte")).ShowDialog(this);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
            b.Enabled = true; b.Text = label;
        }
    }
}
