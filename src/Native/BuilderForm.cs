using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace GenerateFollower.Native;

/// <summary>
/// Follower Builder natif, étape 1 du plan work\diag\natif\PLAN.md (18/11) : fenêtre Windows dessinée avec le thème de
/// FacePhoto, sans Edge ni WebView2. Liste des presets (recherche, filtres, verdicts), fiche minimale (verdict,
/// vérifications, Valider / Rejeter / Remettre à voir, note), construction d'un follower seul ou d'un pack des validés,
/// Mes packs, Prérequis, Rescanner, Réglages. Pas encore de 3D (étapes 2 et 3) ni de postes de l'atelier (étape 4) :
/// l'ancienne interface reste ouverte d'un clic. Les données passent par l'API locale du Builder (même serveur, même
/// code que la page web), dans le même processus.
/// </summary>
public sealed partial class BuilderForm : Form
{
    readonly HttpClient http;
    readonly Action openWeb;
    readonly Field search = new(L.T("Chercher un preset…"));
    readonly Picker fStatus = new(), fVerdict = new(), fCat = new();
    readonly ListBox list = new();
    readonly Label counts = new(), status = new();
    readonly HoldScrollPanel right = new();
    // vue 3D de la tête (étape 2 du plan) et sa barre d'outils
    readonly HeadView view = new() { Dock = DockStyle.Fill };
    readonly Label viewStatus = new();
    readonly FlowLayoutPanel viewBar;   // cases et cadrages, cachés sans aperçu 3D
    readonly Toggle hair = new(L.T("Cheveux"), true), relief = new(L.T("Relief"), true), makeup = new(L.T("Maquillage"), true),
        compare = new(L.T("Export à côté"), false),
        // corps entier (étape 3) : tenue portée, bras le long du corps
        bodyOn = new(L.T("Corps entier"), false), outfit = new(L.T("Tenue"), true), pose = new(L.T("Pose naturelle"), true);
    readonly FlatButton buildPack = new(L.T("Construire un pack des validés")) { Primary = true };
    JsonArray presets = [];
    List<JsonObject> shown = [];
    string? current;
    JsonObject? info;
    readonly BusyHandler busy = new();
    readonly LoadingBar loading;

    public BuilderForm(string baseUrl, Action openWeb, bool use3D = true)
    {
        this.openWeb = openWeb;
        view.Use3D = use3D;
        // toutes les requêtes (fiche, aperçu 3D, vignettes, voix, builds) passent par ce compteur : barre de chargement
        http = new HttpClient(busy) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(30) };
        loading = new LoadingBar(() => busy.Count > 0);
        Text = $"Follower Builder - GenerateFollower {SupportReport.Version}";
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body;
        Size = new Size(1500, 880); MinimumSize = new Size(1100, 600); StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon();

        // barre du haut
        var top = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Theme.Panel, Padding = new Padding(14, 10, 14, 10) };
        var title = new Label { Text = "Follower Builder", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Location = new Point(14, 14) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, BackColor = Theme.Panel, WrapContents = false };
        var tools = new FlatButton(L.T("Outils") + " ▾") { Width = 110 };
        var packs = new FlatButton(L.T("Mes packs")) { Width = 110 };
        var prereq = new FlatButton(L.T("Prérequis")) { Width = 110 };
        var batch = new FlatButton(L.T("Plusieurs followers")) { Width = 170 };
        buildPack.Width = 250;
        foreach (var b in new Control[] { buildPack, tools, packs, batch, prereq }) { b.Margin = new Padding(6, 0, 0, 0); bar.Controls.Add(b); }
        top.Controls.Add(bar); top.Controls.Add(title);
        tools.Click += (_, _) => ToolsMenu(tools);
        batch.Click += async (_, _) => await OpenBatch();
        Shown += async (_, _) => await StartupNotices(bar);
        packs.Click += async (_, _) => await ShowPacks();
        prereq.Click += async (_, _) => await ShowPrereq();
        buildPack.Click += async (_, _) => await BuildPack();

        // liste à gauche
        var left = new Panel { Dock = DockStyle.Left, Width = 380, BackColor = Theme.Panel, Padding = new Padding(12, 10, 8, 10) };
        var filters = new VStack { Dock = DockStyle.Top, Gap = 8, BackColor = Theme.Panel };
        var row = new HRow { Height = 32 };
        fStatus.Height = fVerdict.Height = fCat.Height = 32;
        fStatus.SetItems(["", "avoir", "valide", "rejete", "exclu"], [L.T("Tous les statuts"), L.T("à voir"), L.T("validé"), L.T("rejeté"), L.T("exclu")]);
        fVerdict.SetItems(["", "vert", "orange", "rouge", "export", "aucun"],
            [L.T("Tous les verdicts"), L.T("Prêt (vert)"), L.T("Défauts (orange)"), L.T("Ne passera pas (rouge)"), L.T("Sans export (bleu)"), L.T("Pas vérifié")]);
        fStatus.Index = 0; fVerdict.Index = 0;
        row.Controls.Add(fStatus); row.Controls.Add(fVerdict);
        counts.ForeColor = Theme.Muted; counts.AutoSize = false; counts.Height = 20; counts.Font = Theme.Small;
        filters.Controls.Add(search); filters.Controls.Add(row); filters.Controls.Add(fCat); filters.Controls.Add(counts);
        list.Dock = DockStyle.Fill; list.BorderStyle = BorderStyle.None; list.BackColor = Theme.Panel; list.ForeColor = Theme.Text;
        list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 46; list.IntegralHeight = false;
        list.DrawItem += DrawPreset;
        list.SelectedIndexChanged += async (_, _) => { if (list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count) await Select(Str(shown[list.SelectedIndex], "id")); };
        left.Controls.Add(list); left.Controls.Add(filters);
        search.Box.TextChanged += (_, _) => Fill();
        fStatus.Changed += (_, _) => Fill(); fVerdict.Changed += (_, _) => Fill(); fCat.Changed += (_, _) => Fill();

        // vue 3D au centre : barre (cheveux, cadrages), vue, ligne d'état
        var viewHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(10, 10, 4, 10) };
        // barre sur deux lignes si la vue est étroite (cases, puis cadrages)
        viewBar = new FlowLayoutPanel { Visible = use3D, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg, WrapContents = true, Padding = new Padding(0, 2, 0, 6) };
        hair.Changed += (_, _) => view.HairOn = hair.Checked;
        relief.Changed += (_, _) => view.ReliefOn = relief.Checked;
        makeup.Changed += (_, _) => view.OverlaysOn = makeup.Checked;
        compare.Changed += (_, _) => LoadHead();
        bodyOn.Changed += (_, _) => { outfit.Visible = pose.Visible = bodyOn.Checked; compare.Visible = !bodyOn.Checked; LoadHead(); };
        outfit.Changed += (_, _) => { if (bodyOn.Checked) LoadHead(); };
        pose.Changed += (_, _) => { if (bodyOn.Checked) LoadHead(); };
        outfit.Visible = pose.Visible = false;
        foreach (var t in new[] { hair, relief, makeup, compare, bodyOn, outfit, pose })
        {
            t.Width = TextRenderer.MeasureText(t.Text, Theme.Body).Width + 34; t.Margin = new Padding(2, 4, 8, 0);
            viewBar.Controls.Add(t);
        }
        foreach (var (label, v) in new[] { (L.T("Face"), "front"), (L.T("Trois-quarts"), "three"), (L.T("Profil"), "side") })
        {
            var b = new FlatButton(label) { Width = 92, Height = 30, Margin = new Padding(0, 0, 6, 0) };
            b.Click += (_, _) => view.SetView(v);
            viewBar.Controls.Add(b);
        }
        viewStatus.Dock = DockStyle.Bottom; viewStatus.Height = 22; viewStatus.ForeColor = Theme.Muted; viewStatus.Font = Theme.Small;
        viewStatus.BackColor = Theme.Bg; viewStatus.Padding = new Padding(2, 4, 0, 0);
        view.StatusChanged += t => viewStatus.Text = t;
        viewHost.Controls.Add(view); viewHost.Controls.Add(viewBar); viewHost.Controls.Add(viewStatus);

        // fiche à droite
        right.Dock = DockStyle.Right; right.Width = 560; right.AutoScroll = true; right.BackColor = Theme.Bg; right.Padding = new Padding(8, 14, 18, 14);
        status.Dock = DockStyle.Bottom; status.Height = 24; status.BackColor = Theme.Panel; status.ForeColor = Theme.Muted;
        status.Font = Theme.Small; status.Padding = new Padding(12, 4, 0, 0);
        // barre de chargement juste au-dessus de la barre d'état (ajoutée avant elle : placée à l'intérieur)
        Controls.Add(viewHost); Controls.Add(right); Controls.Add(left); Controls.Add(top); Controls.Add(loading); Controls.Add(status);
        Empty(L.T("Choisis un preset dans la liste."));
        Load += async (_, _) => { Theme.DarkTitle(this); Theme.DarkScroll(list); Theme.DarkScroll(right); viewBar.Visible = view.Active; viewStatus.Text = view.Use3D ? view.GlError ?? view.GlInfo : L.T("Aperçu 3D désactivé (Réglages)."); await LoadList(); };
    }

    // ------------------------------------------------------------------ API locale

    async Task<JsonNode> Api(string path, JsonNode? body = null)
    {
        using var res = body == null ? await http.GetAsync(path)
            : await http.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await res.Content.ReadAsStringAsync();
        var node = text.Length > 0 ? JsonNode.Parse(text) : null;
        if (!res.IsSuccessStatusCode) throw new GfException(node?["erreur"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}");
        return node ?? new JsonObject();
    }

    static Icon? AppIcon() => AppIconStore.Get();

    static string Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static string StatusOf(JsonNode p) => p["inclure"] is JsonValue inc && inc.TryGetValue<bool>(out var b) && !b ? "exclu"
        : Str(p["validation"], "status") is { Length: > 0 } s ? s : "avoir";
    static string VerdictOf(JsonNode p) => Str(p["verdict"], "niveau") is { Length: > 0 } v ? v : "aucun";
    static Color VerdictColor(string v) => v switch
    {
        "vert" => Theme.Ok, "orange" => Theme.Warn, "rouge" => Theme.Err, "export" => Theme.Hex(0x6ea8ff), _ => Theme.Muted,
    };

    void Say(string text) => status.Text = text;

    async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Message(L.T("Erreur"), e.Message); }
    }

    // ------------------------------------------------------------------ liste

    async Task LoadList()
    {
        Say(L.T("Lecture des presets…"));
        await Guard(async () =>
        {
            presets = (await Api("api/presets")).AsArray();
            var cats = presets.Select(p => Str(p, "categorie")).Distinct().OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
            fCat.SetItems(["", .. cats], [L.T("Toutes les catégories"), .. cats]);
            fCat.Index = 0;
            Fill();
            Say(L.T("{0} presets", presets.Count));
        });
        // diagnostic : GF_NATIF_SHOT=<capture.png> [GF_NATIF_ID=<id>] -> image de la fenêtre avec ce preset ouvert, puis fermeture
        if (Environment.GetEnvironmentVariable("GF_NATIF_SHOT") is { Length: > 0 } shot)
        {
            var id = Environment.GetEnvironmentVariable("GF_NATIF_ID") is { Length: > 0 } i ? i : shown.Count > 0 ? Str(shown[0], "id") : null;
            if (id != null) { var k = shown.FindIndex(p => Str(p, "id") == id); if (k >= 0) list.SelectedIndex = k; else await Select(id); }
            // fiche analysée (jusqu'à 3 minutes), puis le temps de la dessiner
            for (int t = 0; t < 360 && (info == null || Str(info, "id") != id); t++) await Task.Delay(500);
            // tête 3D chargée (ligne d'état de la vue)
            for (int t = 0; t < 240 && !(view.HasHead || view.Status.Contains("erreur", StringComparison.OrdinalIgnoreCase) || !view.Active); t++) await Task.Delay(500);
            if (Environment.GetEnvironmentVariable("GF_NATIF_REF") == "1") { compare.Checked = true; for (int t = 0; t < 240 && !view.Status.Contains(L.T("Référence (à droite) : {0}", "").Trim()); t++) await Task.Delay(500); }
            if (Environment.GetEnvironmentVariable("GF_NATIF_RELIEF") == "0") relief.Checked = false;
            // GF_NATIF_POSTE=<poste> : poste ouvert ; GF_NATIF_ESSAI=arme : enregistre la première arme du jeu (test d'écriture)
            if (Environment.GetEnvironmentVariable("GF_NATIF_POSTE") is { Length: > 0 } poste)
            {
                SetStation(poste);
                for (int t = 0; t < 240 && (opts == null || combat == null) && poste != "visage"; t++) await Task.Delay(500);
                var essai = Environment.GetEnvironmentVariable("GF_NATIF_ESSAI");
                if (essai == "arme" && opts?["armes"] is JsonArray aw
                    && aw.OfType<JsonObject>().FirstOrDefault(w => Str(w, "mod").Length == 0) is { } first)
                    await SaveOption(new JsonObject { ["armes"] = new JsonArray(Str(first, "id")) }, rebuild: false);
                // GF_NATIF_ESSAI=combat : premier style et première classe (work\combat_choice.json) ;
                // GF_NATIF_ESSAI=archetype : premier archétype (combat_choice.json et options_choice.json)
                if (essai == "combat" && combat?["styles"] is JsonArray cs && combat["classes"] is JsonArray cc && cs.Count > 0 && cc.Count > 0)
                    await SaveCombat(Str(cs[0], "id"), Str(cc[0], "id"));
                if (essai == "archetype" && opts?["archetypes"] is JsonArray ar && ar.Count > 0)
                    await ApplyArchetype(Str(ar[0], "id"));
                // GF_NATIF_ESSAI=vie : statistiques fixes, mariage, taille 1,05, première voix de la liste (voice_choice.json)
                if (essai == "vie" && opts?["statsDefaut"] is JsonObject sd)
                {
                    await SaveOption(new JsonObject { ["stats"] = DefaultsWithoutClass(sd), ["mariage"] = true, ["taille"] = 1.05 });
                    if (voice?["voixListe"] is JsonArray vl && vl.Count > 0) await SaveVoice(Str(vl[0], "id"));
                }
                // GF_NATIF_ESSAI=fenetres : « Plusieurs followers » et « Mes packs » capturés (<shot>_lot.png, <shot>_packs.png), rien d'enregistré
                if (essai == "fenetres")
                {
                    async Task Capture(Form d, string suffix)
                    {
                        d.Show(this);
                        await Task.Delay(2500);
                        using (var b = new Bitmap(d.Width, d.Height))
                        {
                            d.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                            b.Save(Path.ChangeExtension(shot, null) + suffix, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        d.Close();
                    }
                    await Capture(new BatchDialog(this), "_lot.png");
                    await Capture(new PacksDialog(this, (await Api("api/packs")).AsArray().OfType<JsonObject>().ToList()), "_packs.png");
                    await Capture(new SettingsDialog(this, (await Api("api/settings")).AsObject()), "_reglages.png");
                }
                // GF_NATIF_ESSAI=peau : première couleur de la palette (options_choice.json)
                if (essai == "peau" && opts?["peauPalette"] is JsonArray pal && pal.Count > 0)
                    await SaveOption(new JsonObject { ["couleurPeau"] = Str(pal[0], "couleur") });
                // GF_NATIF_ESSAI=coiffure : première coiffure trouvée par GF_NATIF_RECHERCHE (options_choice.json)
                if (essai == "coiffure" && current != null)
                {
                    var hr = await Api($"api/coiffures/{current}?mod=&q={Uri.EscapeDataString(Environment.GetEnvironmentVariable("GF_NATIF_RECHERCHE") ?? "")}");
                    if (hr["resultats"] is JsonArray hl && hl.Count > 0)
                        await SaveOption(new JsonObject { ["coiffure"] = new JsonObject { ["type"] = Str(hl[0], "type"), ["id"] = Str(hl[0], "id") } });
                    for (int t = 0; t < 120 && !view.HasHead; t++) await Task.Delay(500);
                    await Task.Delay(4000);
                }
                // GF_NATIF_ESSAI=sort : ajoute le premier sort du jeu et le premier atout possible (options_choice.json)
                if (essai == "sort" && opts?["sorts"] is JsonArray sa && opts["atouts"] is JsonArray pa
                    && sa.OfType<JsonObject>().FirstOrDefault(s => Str(s, "mod").Length == 0) is { } s0
                    && pa.OfType<JsonObject>().FirstOrDefault(p => p["pnj"] is JsonValue v && v.TryGetValue<bool>(out var b) && b) is { } p0)
                {
                    await SaveOption(new JsonObject { ["sorts"] = Ids([.. Chosen("sorts").Append(Str(s0, "id")).Distinct()]) }, rebuild: false);
                    await SaveOption(new JsonObject { ["atouts"] = Ids([.. Chosen("atouts").Append(Str(p0, "id")).Distinct()]) }, rebuild: false);
                }
                // GF_NATIF_RECHERCHE=<texte> : recherche de pièces d'armure de mods
                if (Environment.GetEnvironmentVariable("GF_NATIF_RECHERCHE") is { Length: > 0 } rq && poste == "tenue") { pieceQ = rq; Detail(); }
                if (Environment.GetEnvironmentVariable("GF_NATIF_RECHERCHE") is { Length: > 0 } rq2 && poste == "coiffure") { hairQ = rq2; Detail(); }
                if (poste == "pnj")
                {
                    for (int t = 0; t < 600 && (replaceInfo == null || npcList == null); t++) await Task.Delay(500);
                    if (Environment.GetEnvironmentVariable("GF_NATIF_RECHERCHE") is { Length: > 0 } rq3) { npcQ = rq3; Detail(); }
                    // GF_NATIF_ESSAI=lot : dialogue du remplacement en lot sur le mod GF_NATIF_MOD, capturé à côté (<shot>_lot.png), rien d'enregistré
                    if (essai == "lot")
                    {
                        JsonObject allLot;
                        try { allLot = (await Api("api/replace-batch")).AsObject(); } catch (Exception) { allLot = []; }
                        var dlgLot = new ReplaceBatchDialog(this, allLot, "F", Environment.GetEnvironmentVariable("GF_NATIF_MOD") ?? "");
                        dlgLot.Show(this);
                        await Task.Delay(2500);
                        using (var b = new Bitmap(dlgLot.Width, dlgLot.Height))
                        {
                            dlgLot.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                            b.Save(Path.ChangeExtension(shot, null) + "_lot.png", System.Drawing.Imaging.ImageFormat.Png);
                        }
                        dlgLot.Close();
                    }
                    // GF_NATIF_ESSAI=pnj : remplace le premier PNJ libre trouvé (replace_choice.json), tête du PNJ à côté
                    if (essai == "pnj" && Npcs().Where(n => Str(n, "sexe") == Str(info, "sexe") && NpcFree(n) && Str(n, "tete").Length > 0
                        && (npcQ.Length == 0 || Str(n, "nom").Contains(npcQ, StringComparison.OrdinalIgnoreCase))).FirstOrDefault() is { } n0)
                    {
                        await SaveReplace(Str(n0, "id"));
                        for (int t = 0; t < 360 && !view.Status.Contains(L.T("Référence (à droite) : {0}", "").Trim()); t++) await Task.Delay(500);
                    }
                }
                // postes qui lisent leurs données à l'ouverture (skins, coiffures)
                for (int t = 0; t < 120 && ((poste == "corps" && (skinInfo == null || bodyPresets == null)) || (poste == "coiffure" && hairCat == null)); t++) await Task.Delay(500);
                await Task.Delay(1500);
                await Task.Delay(800);
            }
            if (Environment.GetEnvironmentVariable("GF_NATIF_CORPS") is { Length: > 0 } corps)
            {
                if (corps.Contains("sans-tenue")) outfit.Checked = false;
                if (corps.Contains("repos")) pose.Checked = false;
                bodyOn.Checked = true;
                for (int t = 0; t < 240 && view.Active && !(view.Status.StartsWith(L.T("Corps : {0} · poids {1}", "", "").Split(' ')[0]) || view.Status.Contains(L.T("Corps indisponible : {0}", "").Trim())); t++) await Task.Delay(500);
            }
            if (Environment.GetEnvironmentVariable("GF_NATIF_VUE") is { Length: > 0 } vue) { view.SetView(vue); await Task.Delay(1500); }
            // GF_NATIF_DEFIL=<pixels> : fiche défilée (bas des postes longs)
            if (int.TryParse(Environment.GetEnvironmentVariable("GF_NATIF_DEFIL"), out var defil)) { right.AutoScrollPosition = new Point(0, defil); await Task.Delay(400); }
            // GF_NATIF_ESSAI=defil : fiche défilée, focus sur un contrôle en bas, fiche reconstruite comme après un choix
            // (rien n'est enregistré) ; positions avant / après dans la sortie
            if (Environment.GetEnvironmentVariable("GF_NATIF_ESSAI") == "defil")
            {
                static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => All(x).Prepend(x));
                right.AutoScrollPosition = new Point(0, int.TryParse(Environment.GetEnvironmentVariable("GF_NATIF_POS"), out var pos0) ? pos0 : 100000); await Task.Delay(400);
                var before = -right.AutoScrollPosition.Y;
                // case du bas de la fiche : bouton de la souris enfoncé dessus comme un vrai clic, mais jamais relâché
                // (WM_LBUTTONDOWN seul : la case ne change pas, rien n'est enregistré)
                var want = Environment.GetEnvironmentVariable("GF_NATIF_CASE");
                var low = want is { Length: > 0 } ? All(right).OfType<Toggle>().FirstOrDefault(c => c.Visible && c.Text.Contains(want, StringComparison.OrdinalIgnoreCase))
                    : All(right).OfType<Toggle>().Where(c => c.Visible && c.PointToScreen(Point.Empty).Y > right.PointToScreen(Point.Empty).Y + right.Height / 2).LastOrDefault();
                if (low != null) { SendMessage(low.Handle, 0x0201, 1, (IntPtr)((5 << 16) | 5)); low.Capture = false; }
                await Task.Delay(400);
                var clicked = -right.AutoScrollPosition.Y;
                Detail(keepScroll: true);
                await Task.Delay(800);
                Mesure($"défilement avant {before}, après le clic {clicked}, après la fiche reconstruite {-right.AutoScrollPosition.Y} (case : {low?.Text ?? "aucune"})");
                // GF_NATIF_VRAICLIC=1 : vrai clic complet sur la même case (enregistré), puis second clic qui l'annule
                if (Environment.GetEnvironmentVariable("GF_NATIF_VRAICLIC") == "1" && low != null)
                {
                    var name = low.Text;
                    for (int k = 0; k < 2; k++)
                    {
                        right.AutoScrollPosition = new Point(0, before); await Task.Delay(400);
                        var box = All(right).OfType<Toggle>().FirstOrDefault(t => t.Text == name);
                        if (box == null) { Mesure("case introuvable après reconstruction"); break; }
                        var y0 = -right.AutoScrollPosition.Y;
                        SendMessage(box.Handle, 0x0201, 1, (IntPtr)((5 << 16) | 5));
                        box.Capture = false;
                        box.Checked = !box.Checked;   // même effet que OnClick (WM_LBUTTONUP envoyé seul ne fait pas de Click)
                        Application.DoEvents();
                        var y1 = -right.AutoScrollPosition.Y;
                        for (int t = 0; t < 100 && busy.Count > 0; t++) await Task.Delay(100);
                        await Task.Delay(1500);
                        Mesure($"vrai clic {k + 1} « {name} » coché={box.Checked} : avant {y0}, juste après {y1}, une fois enregistré {-right.AutoScrollPosition.Y}");
                    }
                }
            }
            // GF_NATIF_ESSAI=chargement : aperçu rechargé (rien n'est enregistré) ; barre de chargement pendant puis après
            if (Environment.GetEnvironmentVariable("GF_NATIF_ESSAI") == "chargement")
            {
                bodyOn.Checked = !bodyOn.Checked;
                await Task.Delay(500);
                Mesure($"pendant : barre {(loading.Shown ? "visible" : "cachée")}, {busy.Count} requête(s) en cours");
                for (int t = 0; t < 600 && busy.Count > 0; t++) await Task.Delay(100);
                await Task.Delay(300);
                Mesure($"après : barre {(loading.Shown ? "visible" : "cachée")}, {busy.Count} requête(s) en cours");
            }
            // GF_NATIF_LISTE=1 : ouvre la plus longue liste déroulante de la fiche (temps dans la sortie) ; la liste est une
            // fenêtre à part, invisible pour DrawToBitmap : capture de l'écran dans <capture>_liste.png
            if (Environment.GetEnvironmentVariable("GF_NATIF_LISTE") == "1")
            {
                static IEnumerable<Control> Under(Control c) => c.Controls.Cast<Control>().SelectMany(x => Under(x).Prepend(x));
                if (Under(right).OfType<Picker>().OrderByDescending(p => p.Items.Count).FirstOrDefault() is { } big)
                {
                    right.AutoScrollPosition = new Point(0, big.PointToScreen(Point.Empty).Y - right.PointToScreen(Point.Empty).Y - right.AutoScrollPosition.Y - 200); await Task.Delay(300);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    big.Open();
                    Application.DoEvents();
                    Mesure($"liste de {big.Items.Count} choix ouverte en {sw.ElapsedMilliseconds} ms");
                    await Task.Delay(1000);
                    using var sb = new Bitmap(Width, Height);
                    using (var g = Graphics.FromImage(sb)) g.CopyFromScreen(Location, Point.Empty, Size);
                    sb.Save(Path.Combine(Path.GetDirectoryName(shot) ?? "", Path.GetFileNameWithoutExtension(shot) + "_liste.png"));
                }
            }
            // GF_NATIF_BASCULE=<fichier> : aperçu 3D coupé puis remis trois fois, une ligne par état (la mémoire vidéo se lit de l'extérieur) ;
            // la capture se fait dans l'état « oui »
            if (Environment.GetEnvironmentVariable("GF_NATIF_BASCULE") is { Length: > 0 } flip)
                for (int k = 0; k < 6; k++)
                {
                    Apply3D(k % 2 == 1);
                    for (int t = 0; t < 120 && view.Use3D && !view.HasHead; t++) await Task.Delay(500);
                    await Task.Delay(500);
                    File.AppendAllText(flip, $"{DateTime.Now:HH:mm:ss} {Environment.ProcessId} {(view.Use3D ? "3D" : "sans")} actif={view.Active} tete={view.HasHead} etat={view.Status}\n");
                    await Task.Delay(k < 2 ? 7000 : 1500);
                }
            await Task.Delay(1200);
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            // DrawToBitmap ne voit pas l'image OpenGL : la vue 3D est relue et posée à sa place
            using (var snap = view.Snapshot())
                if (snap != null)
                {
                    var at = view.PointToScreen(Point.Empty);
                    using var g = Graphics.FromImage(bmp);
                    g.DrawImage(snap, at.X - Location.X, at.Y - Location.Y);
                }
            bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
            Close();
        }
    }

    void Fill()
    {
        var q = search.Box.Text.Trim().ToLowerInvariant();
        string fs = fStatus.Value ?? "", fv = fVerdict.Value ?? "", fc = fCat.Value ?? "";
        shown = presets.OfType<JsonObject>().Where(p =>
            (q.Length == 0 || (Str(p, "nom") + " " + Str(p, "preset")).ToLowerInvariant().Contains(q))
            && (fs.Length == 0 || StatusOf(p) == fs) && (fv.Length == 0 || VerdictOf(p) == fv)
            && (fc.Length == 0 || Str(p, "categorie") == fc)).ToList();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var p in shown) list.Items.Add(Str(p, "nom"));
        var sel = shown.FindIndex(p => Str(p, "id") == current);
        if (sel >= 0) list.SelectedIndex = sel;
        list.EndUpdate();
        int Count(string s) => presets.Count(p => p != null && StatusOf(p) == s);
        counts.Text = L.T("{0} validés · {1} rejetés · {2} à voir", Count("valide"), Count("rejete"), Count("avoir")) + "   ·   " + L.T("{0} affichés", shown.Count);
    }

    void DrawPreset(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= shown.Count) return;
        var p = shown[e.Index];
        var g = e.Graphics;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var r = e.Bounds;
        g.FillRectangle(new SolidBrush(Theme.Panel), r);
        if (sel) Theme.Box(g, new Rectangle(r.X + 1, r.Y + 2, r.Width - 4, r.Height - 4), Theme.Card, Theme.Accent, 7);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(VerdictColor(VerdictOf(p)))) g.FillEllipse(b, r.X + 10, r.Y + 11, 10, 10);
        var st = StatusOf(p);
        var badge = st switch { "valide" => L.T("validé"), "rejete" => L.T("rejeté"), "exclu" => L.T("exclu"), _ => L.T("à voir") };
        var bw = TextRenderer.MeasureText(badge, Theme.Small).Width + 12;
        var br = new Rectangle(r.Right - bw - 10, r.Y + 8, bw, 18);
        Theme.Box(g, br, st == "valide" ? Theme.Mix(Theme.Panel, Theme.Ok, 0.35) : st == "rejete" ? Theme.Mix(Theme.Panel, Theme.Err, 0.35) : Theme.Field, Color.Empty, 9);
        TextRenderer.DrawText(g, badge, Theme.Small, br, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, Str(p, "nom"), Theme.Bold, new Rectangle(r.X + 28, r.Y + 6, r.Width - bw - 46, 20), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        var sub = Str(p, "categorie") + " · " + Str(p, "race") + (p["tete"] is JsonValue t && t.TryGetValue<bool>(out var hasHead) && !hasHead ? " · " + L.T("pas de tête") : "");
        TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(r.X + 28, r.Y + 26, r.Width - 40, 16), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    // ------------------------------------------------------------------ fiche

    void Empty(string text)
    {
        right.Controls.Clear();
        right.Controls.Add(new Label { Text = text, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(20, 20) });
    }

    async Task Select(string id)
    {
        if (id.Length == 0) return;
        current = id;
        opts = null; coherence = null; combat = null; voice = null; skinInfo = null; bodyPresets = null; hairCat = null;
        replaceInfo = null; npcRef = null; donorList = null;
        Empty(L.T("Analyse en cours…"));
        Say(L.T("Analyse de {0}…", id));
        try { info = (await Api("api/preset/" + id)).AsObject(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Empty(e.Message); return; }
        if (id != current) return;
        // verdict à jour dans la liste
        if (presets.OfType<JsonObject>().FirstOrDefault(p => Str(p, "id") == id) is { } lp && info["verdict"] is JsonObject v)
        { lp["verdict"] = v.DeepClone(); list.Invalidate(); }
        Detail();
        Say(Str(info, "preset"));
        LoadHead();
    }

    /// <summary>Tête de la fiche ouverte dans la vue 3D, avec l'export RaceMenu associé à côté si « Export à côté » est coché.</summary>
    void LoadHead(bool keepView = false)
    {
        if (info == null || current == null) return;
        int? hc = info["couleurCheveux"] is JsonValue h && h.TryGetValue<int>(out var hv) ? hv : null;
        var imp = Str(info["import"], "path");
        bool body = bodyOn.Checked;
        string refPath = !body && compare.Checked && imp.Length > 0 ? Path.Combine(Context.CharGenRel, imp) : null;
        if (!body && compare.Checked && npcRef != null) refPath = npcRef;   // « Voir à côté » du poste Remplacer un PNJ
        if (Environment.GetEnvironmentVariable("GF_NATIF_DUMP") is { Length: > 0 } dumpLog)
            File.AppendAllText(dumpLog + ".log", $"{DateTime.Now:HH:mm:ss} LoadHead corps={body} ref={refPath}\n");
        if (!body && compare.Checked && refPath == null) viewStatus.Text = L.T("Pas d'export RaceMenu associé à comparer.");
        // formes de la coiffure du preset, remplacées par une coiffure ajoutée (poste Coiffure)
        var hairNames = new HashSet<string>();
        void Walk(JsonNode? hp)
        {
            if (hp == null) return;
            foreach (var k in new[] { "edid", "nom" }) if (Str(hp, k) is { Length: > 0 } n) hairNames.Add(n.ToLowerInvariant());
            foreach (var e in hp["extras"] as JsonArray ?? []) Walk(e);
        }
        foreach (var hp in (info["headparts"] as JsonArray ?? []).Where(x => Str(x, "type") == "Hair")) Walk(hp);
        HeadView.AddedHairReplaces = hairNames;
        view.Caption = Str(info, "nom") + "\n" + Str(info, "race") + " · " + Str(info, "sexe");
        view.Load(http, current, hc, Str(info, "peau"), refPath, body, outfit.Checked, pose.Checked, keepView);
    }

    /// <summary>Réglage « Aperçu 3D » appliqué tout de suite : « non » libère la vue OpenGL, « oui » la recrée et recharge le preset.</summary>
    void Apply3D(bool on)
    {
        if (view.Use3D == on) return;
        view.Use3D = on;
        viewBar.Visible = view.Active;
        if (info == null) viewStatus.Text = on ? view.GlError ?? view.GlInfo : L.T("Aperçu 3D désactivé (Réglages).");
        else { LoadHead(); Detail(keepScroll: true); }
    }

    void Detail(bool keepScroll = false)
    {
        if (info == null || current == null) return;
        var scroll = right.AutoScrollPosition;
        Redraw(right, false);   // fiche reconstruite sans clignoter (dessinée une fois à la fin)
        var p = presets.OfType<JsonObject>().FirstOrDefault(x => Str(x, "id") == current);
        var card = VStack.MakeCard();
        card.Width = Math.Max(400, right.ClientSize.Width - 40);
        card.Location = new Point(18, 14);
        var name = new TextLabel { Text = Str(info, "nom"), Font = Theme.Title };
        card.Controls.Add(name);
        var meta = $"{Str(info, "preset")}\n{Str(info, "race")} · {Str(info, "sexe")}" +
            (Str(info, "nom") != Str(info, "nomPreset") ? "\n" + L.T("Nom en jeu « {0} » · preset « {1} »", Str(info, "nom"), Str(info, "nomPreset")) : "");
        card.Controls.Add(new TextLabel { Text = meta, ForeColor = Theme.Muted, Font = Theme.Small });
        if (info["verdict"] is JsonObject vd)
            card.Controls.Add(new VerdictBox(L.T(Str(vd, "texte")), VerdictColor(Str(vd, "niveau"))));

        // validation
        var st = p != null ? StatusOf(p) : "avoir";
        card.Controls.Add(new TextLabel { Text = st switch { "valide" => L.T("Validé"), "rejete" => L.T("Rejeté"), _ => "" }, ForeColor = Theme.Accent, Font = Theme.Bold });
        var note = new Field(L.T("Note (facultative)"));
        note.Box.Text = Str(p?["validation"], "note");
        var actions = new HRow();
        var ok = new FlatButton(L.T("Valider")) { Primary = true };
        var bad = new FlatButton(L.T("Rejeter"));
        var reset = new FlatButton(L.T("Remettre à voir"));
        ok.Click += async (_, _) => await Validate("valide", note.Box.Text);
        bad.Click += async (_, _) => await Validate("rejete", note.Box.Text);
        reset.Click += async (_, _) => await Validate("", note.Box.Text);
        actions.Controls.Add(ok); actions.Controls.Add(bad); actions.Controls.Add(reset);
        card.Controls.Add(actions);
        card.Controls.Add(note);

        var more = new HRow();
        var solo = new FlatButton(L.T("Construire ce follower seul"));
        var web = new FlatButton(L.T("Ouvrir dans l'ancienne interface"));
        solo.Click += async (_, _) => await Build(new JsonObject { ["mode"] = "seul", ["id"] = current }, solo);
        web.Click += (_, _) => openWeb();
        more.Controls.Add(solo); more.Controls.Add(web);
        card.Controls.Add(more);

        // postes de l'atelier (étape 4) : onglets, puis le poste ouvert
        card.Controls.Add(Tabs());
        StationBody(card);
        right.SuspendLayout();
        right.Controls.Clear();
        // Location d'un enfant d'un panneau défilé = position à l'écran : (18, 14) seul posait la nouvelle fiche en haut
        // de la partie visible, donc on voyait son début après chaque choix, alors que la barre restait en bas (18/11,
        // reproduit avec de vrais clics) ; décalage de défilement courant ajouté, la fiche reste à sa place dans le contenu
        card.Location = new Point(18 + right.AutoScrollPosition.X, 14 + right.AutoScrollPosition.Y);
        right.Controls.Add(card);
        right.ResumeLayout();
        if (keepScroll)
        {
            right.PerformLayout();
            right.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
            // focus et mise en page encore en attente : position remise une fois tout traité, puis défilement rendu
            BeginInvoke(() => right.AutoScrollPosition = new Point(-scroll.X, -scroll.Y));
        }
        Redraw(right, true);
        right.Resize -= ResizeCard;
        right.Resize += ResizeCard;
    }

    /// <summary>Mesure d'un essai de capture (GF_NATIF_SHOT) : ajoutée à work\diag\natif\mesures.txt (une app fenêtrée n'a
    /// pas de console).</summary>
    static void Mesure(string line)
    {
        try { File.AppendAllText(Path.Combine("work", "diag", "natif", "mesures.txt"), DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine); }
        catch (IOException) { }
    }

    void ResizeCard(object? sender, EventArgs e)
    {
        if (right.Controls.Count > 0 && right.Controls[0] is VStack v) v.Width = Math.Max(400, right.ClientSize.Width - 40);
    }

    async Task Validate(string st, string note)
    {
        if (current == null) return;
        await Guard(async () =>
        {
            await Api("api/validate/" + current, new JsonObject { ["status"] = st, ["note"] = note });
            if (presets.OfType<JsonObject>().FirstOrDefault(x => Str(x, "id") == current) is { } p)
                p["validation"] = st.Length > 0 ? new JsonObject { ["status"] = st, ["note"] = note } : null;
            Fill(); Detail();
        });
    }

    // ------------------------------------------------------------------ actions

    async Task Build(JsonObject body, FlatButton btn)
    {
        var label = btn.Text;
        btn.Enabled = false; btn.Text = L.T("Construction…");
        Say(L.T("Construction…"));
        await Guard(async () =>
        {
            var r = await Api("api/build", body);
            Message(L.T("{0} : {1} follower(s)", Str(r, "plugin"), r["followers"]?.ToString() ?? "?"),
                Str(r, "zip") + "\n\n" + Str(r, "log") + "\n" + Str(r, "rapport"));
        });
        btn.Enabled = true; btn.Text = label;
        Say("");
    }

    async Task BuildPack()
    {
        int n = presets.Count(p => p != null && StatusOf(p) == "valide");
        if (n == 0) { Message(L.T("Construire un pack des validés"), L.T("Aucun follower validé.")); return; }
        using var dlg = new AskPack(n);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await Build(new JsonObject { ["mode"] = "pack", ["nom"] = dlg.PackName, ["pouvoir"] = dlg.Power }, buildPack);
    }

    void ToolsMenu(Control anchor)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenu(), ShowImageMargin = false, Font = Theme.Body, BackColor = Theme.Field, ForeColor = Theme.Text };
        void Add(string text, Func<Task> act) { var it = new ToolStripMenuItem(text) { ForeColor = Theme.Text }; it.Click += async (_, _) => await act(); menu.Items.Add(it); }
        Add(L.T("Rescanner"), Rescan);
        Add(L.T("Tout vérifier"), VerifyAll);
        Add(L.T("Réglages"), ShowSettings);
        Add(L.T("Ouvrir dans l'ancienne interface"), () => { openWeb(); return Task.CompletedTask; });
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(anchor, new Point(0, anchor.Height + 2));
    }

    async Task Rescan()
    {
        Say(L.T("Scan…"));
        await Guard(async () =>
        {
            var r = await Api("api/rescan", new JsonObject());
            await LoadList();
            string Names(string k) => string.Join(", ", (r[k] as JsonArray ?? []).Select(x => x?.ToString()));
            var nouveaux = (r["nouveaux"] as JsonArray)?.Count ?? 0;
            var disparus = (r["disparus"] as JsonArray)?.Count ?? 0;
            Message(L.T("Scan terminé : {0} presets", r["total"]?.ToString() ?? "?"),
                (nouveaux > 0 ? L.T("Nouveaux ({0}) : {1}", nouveaux, Names("nouveaux")) : L.T("Aucun nouveau preset.")) + "\n"
                + (disparus > 0 ? L.T("Disparus ({0}) : {1}", disparus, Names("disparus")) + "\n" : "")
                + "\n" + L.T("Les nouveaux exports (Export Head) sont pris en compte."));
        });
    }

    async Task ShowPrereq()
    {
        await Guard(async () =>
        {
            var items = (await Api("api/prerequisites")).AsArray().OfType<JsonObject>().ToList();
            var sb = new StringBuilder(L.T("Mods à installer et activer dans le jeu pour que tes followers fonctionnent. Après une installation, relance le Builder.") + "\n\n");
            foreach (var i in items)
            {
                bool ok = i["ok"]?.GetValue<bool>() == true, req = i["requis"]?.GetValue<bool>() == true;
                sb.AppendLine($"{(ok ? "✔" : req ? "✖" : "–")}  {Str(i, "nom")} — {(ok ? L.T("installé") : req ? L.T("manquant (requis)") : L.T("absent (facultatif)"))}");
                if (Str(i, "pourquoi") is { Length: > 0 } why) sb.AppendLine("     " + why);
                if (!ok && Str(i, "lien") is { Length: > 0 } link) sb.AppendLine("     " + link);
            }
            Message(L.T("Prérequis du jeu"), sb.ToString());
        });
    }

    // ShowPacks (Mes packs) : BuilderForm.Batch.cs

    // ShowSettings (Réglages) : BuilderForm.Settings.cs

    void Message(string title, string body)
    {
        using var dlg = new MessageDialog(title, body);
        dlg.ShowDialog(this);
    }
}

/// <summary>Icône « GF » de l'application (bouclier, src\GenerateFollower.ico, ressource app/GenerateFollower.ico).</summary>
static class AppIconStore
{
    static Icon? icon;
    public static Icon? Get()
    {
        if (icon != null) return icon;
        try
        {
            using var s = typeof(BuilderForm).Assembly.GetManifestResourceStream("app/GenerateFollower.ico");
            if (s != null) return icon = new Icon(s);
        }
        catch (Exception) { }
        try { return icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (Exception) { return null; }
    }
}

/// <summary>Bandeau du verdict, couleur du niveau.</summary>
sealed class VerdictBox : Painted
{
    readonly Color color;
    public VerdictBox(string text, Color color) { Text = text; this.color = color; Height = 40; }
    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Box(e.Graphics, ClientRectangle, Theme.Mix(Theme.Card, color, 0.22), color, 8);
        TextRenderer.DrawText(e.Graphics, Text, Theme.Bold, new Rectangle(12, 0, Width - 24, Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Fenêtre de dialogue sombre (titre, texte défilant, OK).</summary>
class DarkDialog : Form
{
    protected readonly Panel Body = new() { Dock = DockStyle.Fill, Padding = new Padding(16), BackColor = Theme.Bg };
    protected readonly FlowLayoutPanel Buttons = new() { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 9, 12, 9), BackColor = Theme.Panel };
    public DarkDialog(string title, Size size)
    {
        Text = title; BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body; Size = size; Icon = AppIconStore.Get();
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; ShowInTaskbar = false;
        Controls.Add(Body); Controls.Add(Buttons);
        Load += (_, _) => Theme.DarkTitle(this);
    }
    protected FlatButton AddButton(string text, DialogResult result, bool primary = false)
    {
        var b = new FlatButton(text) { Width = 140, Primary = primary, Margin = new Padding(8, 0, 0, 0) };
        b.Click += (_, _) => { DialogResult = result; Close(); };
        Buttons.Controls.Add(b);
        return b;
    }
}

sealed class MessageDialog : DarkDialog
{
    public MessageDialog(string title, string body) : base(title, new Size(720, 480))
    {
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body, Text = body.Replace("\r\n", "\n").Replace("\n", "\r\n") };
        Body.Controls.Add(box);
        AddButton("OK", DialogResult.OK, true);
        Shown += (_, _) => { Theme.DarkScroll(box); box.SelectionLength = 0; };
    }
}

sealed class AskPack : DarkDialog
{
    readonly Field name = new(L.T("Nom du pack"));
    readonly Toggle power = new(L.T("Pouvoir d'invocation dans le pack"), true);
    public string PackName => name.Box.Text.Trim().Length > 0 ? name.Box.Text.Trim() : "FollowerPack";
    public bool Power => power.Checked;
    public AskPack(int count) : base(L.T("Construire un pack des validés"), new Size(520, 260))
    {
        var v = new VStack { Dock = DockStyle.Top, BackColor = Theme.Bg, Gap = 10 };
        v.Controls.Add(new TextLabel { Text = L.T("{0} follower(s) validé(s) seront dans le pack.", count), ForeColor = Theme.Muted });
        name.Box.Text = "FollowerPack";
        v.Controls.Add(name); v.Controls.Add(power);
        Body.Controls.Add(v);
        AddButton(L.T("Annuler"), DialogResult.Cancel);
        AddButton(L.T("Construire"), DialogResult.OK, true);
    }
}

// SettingsDialog (Réglages) : BuilderForm.Settings.cs

/// <summary>Panneau de la fiche : un changement de focus ne le fait jamais défiler. Toute la fiche est une seule carte ;
/// un clic dedans (case, liste, bouton) lui donnait le focus et Windows faisait défiler jusqu'au haut de la carte, donc
/// en haut de la fiche (armes et armures de mods, presets OBody, mariage, statistiques fixes… 18/11) ; idem quand la
/// fiche est reconstruite après un choix et que le contrôle cliqué disparaît. Seuls la molette, la barre de défilement
/// et le code (AutoScrollPosition) la font défiler.</summary>
sealed class HoldScrollPanel : Panel
{
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
}

/// <summary>Compte les requêtes en cours vers l'API locale (barre de chargement).</summary>
sealed class BusyHandler : DelegatingHandler
{
    int count;
    public BusyHandler() : base(new SocketsHttpHandler()) { }
    public int Count => Volatile.Read(ref count);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref count);
        try { return await base.SendAsync(request, cancellationToken); }
        finally { Interlocked.Decrement(ref count); }
    }
}

/// <summary>Barre de chargement fine (3 px) : un segment qui glisse tant que <c>busy()</c> est vrai depuis plus de
/// 150 ms (les réponses instantanées ne font pas clignoter), fond de la barre d'état sinon. Hauteur fixe : rien ne bouge.</summary>
sealed class LoadingBar : Control
{
    readonly Func<bool> busy;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 30 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    long since = -1;
    bool shown;
    public bool Shown => shown;

    public LoadingBar(Func<bool> busy)
    {
        this.busy = busy;
        Dock = DockStyle.Bottom; Height = 3; BackColor = Theme.Panel;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        timer.Tick += (_, _) =>
        {
            bool b = busy();
            if (b && since < 0) since = clock.ElapsedMilliseconds;
            if (!b) since = -1;
            bool show = b && clock.ElapsedMilliseconds - since > 150;
            if (show || show != shown) { shown = show; Invalidate(); }
        };
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Panel);
        if (!shown || Width <= 0) return;
        int seg = Math.Max(60, Width / 4);
        double t = clock.ElapsedMilliseconds % 1400 / 1400.0;
        int x = (int)(t * (Width + seg)) - seg;
        using var b = new SolidBrush(Theme.Accent);
        e.Graphics.FillRectangle(b, x, 0, seg, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
