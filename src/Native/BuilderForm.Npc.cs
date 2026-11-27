using System.Drawing;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace GenerateFollower.Native;

/// <summary>
/// Postes « Finition » et « Remplacer un PNJ » de la fiche native (plan work\diag\natif\PLAN.md, étape 4), comme la page.
/// Finition : résumé des choix (liens vers les postes), fiche partagée (export / import .gffiche.json, /api/sheet).
/// Remplacer un PNJ : PNJ choisi (/api/replace), recherche dans les PNJ du jeu (/api/npcs), tête du PNJ à côté, paquet
/// GF_Remplacements (/api/replace-build), remplacement en lot (/api/replace-batch, dialogue ReplaceBatchDialog).
/// </summary>
public sealed partial class BuilderForm
{
    JsonObject? replaceInfo;          // /api/replace/<id>
    static JsonObject? npcList;       // /api/npcs (gros : lu une fois)
    static bool npcLoading;
    string? npcRef;                   // tête du PNJ remplacé montrée à côté
    string npcMod = "", npcQ = "";
    bool npcFreeOnly;

    // ------------------------------------------------------------------ Finition

    string Named(JsonNode? list, string id) => (list as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => Str(x, "id") == id) is { } x ? Str(x, "nom") : "";

    /// <summary>Résumé d'une ligne des choix d'un groupe de postes (tabSummary de la page).</summary>
    string Summary(string tab)
    {
        var ch = opts?["choix"];
        int N(string k) => (ch?[k] as JsonArray)?.Count ?? 0;
        switch (tab)
        {
            case "apparence":
                var skin = (skinInfo?["skins"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => Str(x, "id") == Str(skinInfo, "choix"));
                int pieces = (opts?["tenueModsChoix"] as JsonArray)?.Count ?? 0;
                return string.Join(" · ",
                    skin != null ? Str(skin, "nom") : L.T("Peau par défaut"),
                    Str(bodyPresets, "choix") is { Length: > 0 } bp ? bp : L.T("Corps au hasard (OBody)"),
                    (Named(opts?["tenues"], Str(ch, "tenue")) is { Length: > 0 } t ? t : L.T("Tenue par défaut")) + (pieces > 0 ? " + " + L.T("{0} pièce(s) de mods", pieces) : ""));
            case "combat":
                return string.Join(" · ",
                    Named(combat?["styles"], Str(combat, "style")) is { Length: > 0 } s ? s : L.T("Style par défaut"),
                    Named(combat?["classes"], Str(combat, "classe")) is { Length: > 0 } c ? c : L.T("Classe par défaut"),
                    L.T("{0} arme(s)", N("armes")), L.T("{0} sort(s)", N("sorts")), L.T("{0} atout(s)", N("atouts")));
            default:
                var v = (voice?["voixListe"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => Str(x, "id") == Str(voice, "voix"));
                return string.Join(" · ",
                    L.T("Voix : {0}", v != null ? Str(v, "nom") + (Str(v, "mod") is { Length: > 0 } m ? $" ({m})" : "") : Str(voice, "defautNom") is { Length: > 0 } dn ? dn : Str(voice, "defaut") is { Length: > 0 } d ? d : "?"),
                    Named(opts?["lieux"], Str(ch, "lieu")) is { Length: > 0 } l ? l : L.T("Invocation seulement"),
                    ch?["mariage"] is JsonValue mv && mv.TryGetValue<bool>(out var mb) && mb ? L.T("Peut être épousé") : L.T("Pas de mariage"));
        }
    }

    void FinishStation(VStack card)
    {
        if (skinInfo == null || bodyPresets == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); _ = LoadBodyInfo(); return; }
        CoherenceBlock(card);
        card.Controls.Add(H2(L.T("Résumé")));
        foreach (var (station, tab) in new[] { ("corps", "apparence"), ("combat", "combat"), ("vie", "vie") })
        {
            var link = new LinkButton("• " + Summary(tab)) { Font = Theme.Small, Height = 22 };
            link.Click += (_, _) => SetStation(station);
            card.Controls.Add(link);
        }
        card.Controls.Add(H2(L.T("Validation")));
        card.Controls.Add(Muted(L.T("Un follower validé entre dans « Construire un pack des validés ».")
            + " " + L.T("Valider") + " / " + L.T("Rejeter") + " / " + L.T("Note (facultative)") + " : " + L.T("en haut de la fiche.")));
        card.Controls.Add(H2(L.T("Autres actions")));
        var row = new HRow { Height = 34 };
        var solo = new FlatButton(L.T("Construire ce follower seul"));
        solo.Click += async (_, _) => await Build(new JsonObject { ["mode"] = "seul", ["id"] = current }, solo);
        var exp = new FlatButton(L.T("Exporter la fiche"));
        exp.Click += async (_, _) => await ExportSheet();
        var imp = new FlatButton(L.T("Importer une fiche…"));
        imp.Click += async (_, _) => await ImportSheet();
        row.Controls.Add(solo); row.Controls.Add(exp); row.Controls.Add(imp);
        card.Controls.Add(row);
        card.Controls.Add(Muted(L.T("Enregistre les choix de ce follower (voix, tenue, sorts, lieu…) dans un fichier à partager") + ".\n"
            + L.T("Reprend les choix d'une fiche exportée (par toi ou un autre joueur) pour ce follower") + "."));
    }

    async Task ExportSheet()
    {
        var id = current;
        if (id == null) return;
        try
        {
            var sheet = await Api("api/sheet/" + id);
            var name = string.Concat((Str(info, "nom") is { Length: > 0 } n ? n : id).Select(c => "\\/:*?\"<>|".Contains(c) ? '_' : c)) + ".gffiche.json";
            using var dlg = new SaveFileDialog { FileName = name, Filter = "GF (*.gffiche.json)|*.gffiche.json|JSON|*.json",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } up && Directory.Exists(Path.Combine(up, "Downloads")) ? Path.Combine(up, "Downloads") : "" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dlg.FileName, sheet.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
            Say(dlg.FileName);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
    }

    async Task ImportSheet()
    {
        var id = current;
        if (id == null) return;
        using var dlg = new OpenFileDialog { Filter = "GF (*.gffiche.json;*.json)|*.gffiche.json;*.json" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var f = new FileInfo(dlg.FileName);
        if (f.Length > 1 << 20) { new MessageDialog(L.T("Importer une fiche…"), L.T("Fichier trop gros pour une fiche de follower.")).ShowDialog(this); return; }
        if (new ConfirmDialog(L.T("Importer une fiche…"), L.T("Remplacer les choix de {0} par ceux de la fiche « {1} » ? Ce qui n'est pas dans la fiche revient au défaut.", Str(info, "nom"), f.Name)).ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var body = JsonNode.Parse(File.ReadAllText(f.FullName)) ?? throw new GfException(L.T("Fichier trop gros pour une fiche de follower."));
            var r = await Api("api/sheet/" + id, body);
            var applied = (r["appliques"] as JsonArray ?? []).Select(x => x?.ToString()).ToList();
            var ignored = (r["ignores"] as JsonArray ?? []).Select(x => x?.ToString()).ToList();
            new MessageDialog(L.T("Importer une fiche…"), L.T("Appliqué : {0}", applied.Count > 0 ? string.Join(", ", applied) : L.T("rien"))
                + (ignored.Count > 0 ? "\n\n" + L.T("Ignoré (pas disponible chez toi) :") + "\n- " + string.Join("\n- ", ignored) : "")).ShowDialog(this);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
        // nom en jeu repris de la fiche, puis tout relu
        try
        {
            var fresh = await Api("api/presets");
            var arr = fresh as JsonArray ?? fresh["presets"] as JsonArray ?? [];
            if (arr.OfType<JsonObject>().FirstOrDefault(x => Str(x, "id") == id) is { } np
                && presets.OfType<JsonObject>().FirstOrDefault(x => Str(x, "id") == id) is { } lp) { lp["nom"] = Str(np, "nom"); list.Invalidate(); }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        if (id == current) await Select(id);
    }

    // ------------------------------------------------------------------ Remplacer un PNJ

    async Task LoadReplace()
    {
        var id = current;
        if (id == null) return;
        JsonObject r;
        try { r = (await Api("api/replace/" + id)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { r = new JsonObject { ["erreur"] = e.Message }; }
        if (id != current) return;
        replaceInfo = r;
        Detail(keepScroll: true);
        await LoadNpcs();
    }

    async Task LoadNpcs()
    {
        if (npcList != null || npcLoading) return;
        npcLoading = true;
        try { npcList = (await Api("api/npcs")).AsObject(); }
        catch (Exception e) when (e is not OutOfMemoryException) { npcList = new JsonObject { ["erreur"] = e.Message, ["pnj"] = new JsonArray() }; }
        npcLoading = false;
        if (station == "pnj") Detail(keepScroll: true);
    }

    static List<JsonObject> Npcs() => (npcList?["pnj"] as JsonArray ?? []).OfType<JsonObject>().ToList();
    static bool NpcFree(JsonObject n) => n["skypatcher"] == null && n["plugins"] == null && Str(n, "visage").Length == 0;
    static string Joined(JsonNode? a) => string.Join(", ", (a as JsonArray ?? []).Select(x => x?.ToString()));

    /// <summary>Remarques sur un PNJ : niveau (erreur = impossible), texte (npcMarks de la page).</summary>
    List<(string Level, string Text)> NpcMarks(JsonObject n)
    {
        var m = new List<(string, string)>();
        if (n["skypatcher"] != null) m.Add(("erreur", L.T("Déjà remplacé par SkyPatcher ({0}) : SkyPatcher passe après les plugins, ce remplacement serait écrasé en jeu.", Joined(n["skypatcher"]))));
        if (n["plugins"] != null) m.Add(("attention", L.T("Apparence déjà changée par {0} : installe GF_Remplacements après, dans l'ordre de chargement.", Joined(n["plugins"]))));
        if (Str(n, "visage") is { Length: > 0 } v) m.Add(("attention", L.T("Tête déjà fournie par un mod ({0}) : la nôtre doit passer après (installe GF_Remplacements après ce mod).", v)));
        if (Str(info, "race") is { Length: > 0 } race && Str(n, "race") is { Length: > 0 } nr && nr != race)
            m.Add(("info", L.T("Race différente : {0} deviendra {1}. Quelques dialogues du jeu tiennent compte de la race.", Str(n, "raceNom") is { Length: > 0 } rn ? rn : nr, race)));
        if (Str(n, "monde") == "absent") m.Add(("attention", L.T("Placé nulle part dans le monde : il n'apparaît que par une quête ou un script (ou c'est du contenu coupé). Tu risques de ne jamais le croiser.")));
        if (Str(n, "monde") == "desactive") m.Add(("info", L.T("Absent au début de la partie : il n'apparaît qu'à un moment du jeu (quête).")));
        if (Bool(n, "essentiel")) m.Add(("info", L.T("PNJ essentiel (quêtes) : seule son apparence change.")));
        return m;
    }

    static string NpcLabel(JsonObject n) => Str(n, "nom") + "  (" + Str(n, "edid") + " · " + Str(n, "plugin") + " · " + (Str(n, "raceNom") is { Length: > 0 } rn ? rn : Str(n, "race")) + ")";
    static Color MarkColor(string lvl) => lvl switch { "erreur" => Theme.Err, "attention" => Theme.Warn, _ => Theme.Muted };

    void NpcStation(VStack card)
    {
        if (replaceInfo == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); _ = LoadReplace(); return; }
        card.Controls.Add(H2(L.T("Remplacer un PNJ du jeu")));
        if (Str(replaceInfo, "erreur") is { Length: > 0 } err) { card.Controls.Add(new TextLabel { Text = err, ForeColor = Theme.Err }); return; }
        card.Controls.Add(Muted(L.T("Le PNJ choisi prend l'apparence de ce preset (visage, cheveux, corps, peau, taille). Il garde sa voix, son rôle, ses quêtes et ses dialogues. Un paquet à part, GF_Remplacements, que tu peux retirer pour revenir au PNJ d'origine.")));
        bool noExport = Str(info?["verdict"], "niveau") == "export";
        if (noExport)
            card.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Err, Text = L.T("Pas d'export RaceMenu pour ce preset : le jeu n'accepte que les têtes exportées. Dans RaceMenu, charge le preset puis « Export Head » sous le nom du preset.") });

        // PNJ choisi
        var chosen = Str(replaceInfo, "choix");
        if (chosen.Length == 0) card.Controls.Add(Muted(L.T("Aucun PNJ remplacé (par défaut) : le follower est un nouveau PNJ.")));
        else
        {
            var n = Npcs().FirstOrDefault(x => Str(x, "id") == chosen);
            card.Controls.Add(new ChosenRow(L.T("Remplace :") + " " + (n != null ? NpcLabel(n) : chosen), Theme.Ok, L.T("Retirer"), async () => await SaveReplace(null)));
            if (view.Use3D && n != null && Str(n, "tete") is { Length: > 0 } tete)
            {
                var show = new FlatButton(L.T("Voir à côté")) { Width = 160 };
                show.Click += (_, _) => ShowReplacedNpc(tete);
                card.Controls.Add(new FixedEndRow(new Panel { BackColor = Theme.Card }, show));
            }
            if (n != null) foreach (var (lv, t) in NpcMarks(n)) card.Controls.Add(new TextLabel { Text = t, Font = Theme.Small, ForeColor = MarkColor(lv) });
            else if (npcList != null) card.Controls.Add(new TextLabel { Text = L.T("PNJ introuvable dans ton jeu (mod retiré ?)."), Font = Theme.Small, ForeColor = Theme.Err });
        }

        // paquet et lot
        var row = new HRow { Height = 34 };
        int total = (int)(Num(replaceInfo, "total") ?? 0);
        var build = new FlatButton(L.T("Construire GF_Remplacements ({0} PNJ choisi(s), tous presets)", total)) { Enabled = total > 0 };
        build.Click += async (_, _) =>
        {
            build.Enabled = false; build.Text = L.T("Construction en cours…");
            try
            {
                var r = await Api("api/replace-build", new JsonObject());
                new MessageDialog("GF_Remplacements", L.T("{0} remplacement(s) dans {1}. Installe ce ZIP comme un mod, après les autres mods qui modifient ces PNJ.", Num(r, "remplacements") ?? 0, Str(r, "zip")) + "\n\n" + Str(r, "rapport")).ShowDialog(this);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
            Detail(keepScroll: true);
        };
        var batch = new FlatButton(L.T("Remplacer en lot…"));
        batch.Click += async (_, _) => await OpenReplaceBatch();
        card.Controls.Add(build);   // texte long : chacun sur sa ligne
        card.Controls.Add(batch);
        if (noExport) return;

        // recherche
        var sexe = Str(info, "sexe");
        var results = new VStack { Gap = 4, BackColor = Theme.Card };
        card.Controls.Add(SearchRow(npcMod, npcQ, L.T("Chercher un PNJ (nom, EditorID, plugin)…"), (m, q) => { npcMod = m; npcQ = q; FillNpcs(results, sexe); }));
        var free = new Toggle(L.T("Seulement les libres"), npcFreeOnly);
        free.Changed += (_, _) => { npcFreeOnly = free.Checked; FillNpcs(results, sexe); };
        card.Controls.Add(free);
        card.Controls.Add(results);
        FillNpcs(results, sexe);
    }

    void FillNpcs(VStack box, string sexe)
    {
        box.SuspendLayout();
        foreach (Control c in box.Controls.Cast<Control>().ToList()) c.Dispose();
        box.Controls.Clear();
        if (npcList == null) { box.Controls.Add(Muted(L.T("Lecture des PNJ de ton jeu…"))); _ = LoadNpcs(); }
        else if (Str(npcList, "erreur") is { Length: > 0 } err) box.Controls.Add(new TextLabel { Text = err, ForeColor = Theme.Err });
        else
        {
            var chosen = Str(replaceInfo, "choix");
            var l = Npcs().Where(n => Str(n, "sexe") == sexe && Str(n, "id") != chosen).ToList();
            if (npcMod.Length > 0) l = l.Where(n => ModMatch(Str(n, "plugin"), npcMod)).ToList();
            if (npcFreeOnly) l = l.Where(NpcFree).ToList();
            box.Controls.Add(Muted(L.T("{0} PNJ du même sexe ({1} libres : ni SkyPatcher, ni autre remplaçant). Même sexe seulement : la voix du PNJ ne change pas.", l.Count, l.Count(NpcFree))));
            if (npcQ.Length == 0 && npcMod.Length == 0) box.Controls.Add(Muted(L.T("Choisis un mod ou tape un nom pour chercher.")));
            else
            {
                if (npcQ.Length > 0) l = l.Where(n => $"{Str(n, "nom")} {Str(n, "edid")} {Str(n, "plugin")}".Contains(npcQ, StringComparison.OrdinalIgnoreCase)).ToList();
                // un mod entier s'affiche sans recherche (jusqu'à 300 PNJ, ici 120 : contrôles natifs) ; une recherche, 40 résultats
                int max = npcMod.Length > 0 && npcQ.Length == 0 ? 120 : 40;
                l = l.OrderBy(n => Str(n, "nom"), StringComparer.CurrentCultureIgnoreCase).ToList();
                var taken = replaceInfo?["pris"] as JsonObject;
                foreach (var n in l.Take(max))
                {
                    var marks = NpcMarks(n);
                    bool blocked = marks.Any(m => m.Level == "erreur");
                    var color = blocked ? Theme.Err : marks.Any(m => m.Level == "attention") ? Theme.Warn : Theme.Ok;
                    var nid = Str(n, "id");
                    var text = NpcLabel(n) + string.Concat(marks.Select(m => "\n" + m.Text));
                    if (taken?[nid] is JsonValue tv && tv.TryGetValue<string>(out var by))
                    {
                        var who = presets.OfType<JsonObject>().FirstOrDefault(p => Str(p, "id") == by) is { } bp ? Str(bp, "nom") : by;
                        box.Controls.Add(new TextLabel { Text = text + "\n" + L.T("déjà pris par le preset {0}", who), Font = Theme.Small, ForeColor = Theme.Muted });
                    }
                    else
                    {
                        var r = new ChosenRow(text, color, L.T("Choisir"), async () => await SaveReplace(nid), mark: "");
                        r.SetEnabled(!blocked);
                        box.Controls.Add(r);
                    }
                }
                if (l.Count > max) box.Controls.Add(Muted(L.T("{0} autres : précise la recherche.", l.Count - max)));
                else if (l.Count == 0) box.Controls.Add(Muted(L.T("Aucun PNJ trouvé.")));
            }
        }
        box.ResumeLayout();
        box.PerformLayout();
        box.Parent?.PerformLayout();
    }

    async Task SaveReplace(string? npc)
    {
        var id = current;
        if (id == null) return;
        Say(L.T("Enregistrement…"));
        try { replaceInfo = (await Api("api/replace/" + id, new JsonObject { ["pnj"] = npc })).AsObject(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this);
            try { replaceInfo = (await Api("api/replace/" + id)).AsObject(); } catch (Exception e2) when (e2 is not OutOfMemoryException) { }
        }
        Say(Str(info, "preset"));
        if (id != current) return;
        Detail(keepScroll: true);
        if (npc != null && Npcs().FirstOrDefault(x => Str(x, "id") == npc) is { } n && Str(n, "tete") is { Length: > 0 } tete) ShowReplacedNpc(tete);
        else if (npc == null && npcRef != null) { npcRef = null; if (compare.Checked) LoadHead(keepView: true); }
    }

    /// <summary>Tête du PNJ remplacé à côté de celle du follower (vue tête seule, cheveux compris).</summary>
    void ShowReplacedNpc(string tete)
    {
        npcRef = tete;
        if (bodyOn.Checked) bodyOn.Checked = false;
        if (!compare.Checked) compare.Checked = true;   // recharge la vue
        else LoadHead();
    }

    async Task OpenReplaceBatch()
    {
        await LoadNpcs();
        for (int t = 0; t < 600 && npcLoading; t++) await Task.Delay(200);
        JsonObject all;
        try { all = (await Api("api/replace-batch")).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { all = []; }
        using var dlg = new ReplaceBatchDialog(this, all, Str(info, "sexe") == "M" ? "M" : "F", npcMod);
        dlg.ShowDialog(this);
        if (current != null)
        {
            try { replaceInfo = (await Api("api/replace/" + current)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { }
            if (station == "pnj") Detail(keepScroll: true);
        }
    }

    /// <summary>Remplacement en lot : PNJ d'un mod, un follower chacun (automatique ou à la main), enregistré d'un coup.</summary>
    sealed class ReplaceBatchDialog : DarkDialog
    {
        readonly BuilderForm f;
        JsonObject all;
        string sexe, mod;
        bool libres, valides = true;
        readonly VStack box = new() { Gap = 6, BackColor = Theme.Bg };
        readonly Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
        List<(JsonObject N, bool Blocked, string Was, Toggle Check, Picker Pick)> rows = [];

        public ReplaceBatchDialog(BuilderForm f, JsonObject all, string sexe, string mod) : base(L.T("Remplacer des PNJ en lot"), new Size(980, 760))
        {
            this.f = f; this.all = all; this.sexe = sexe; this.mod = mod;
            scroll.Controls.Add(box);
            Body.Controls.Add(scroll);
            scroll.Resize += (_, _) => box.Width = scroll.ClientSize.Width - 4;
            var save = AddButtonNoClose(L.T("Enregistrer"), true);
            save.Click += async (_, _) => await Save();
            AddButton(L.T("Fermer"), DialogResult.Cancel);
            Shown += (_, _) => { Theme.DarkScroll(scroll); box.Width = scroll.ClientSize.Width - 4; Render(); };
        }

        FlatButton AddButtonNoClose(string text, bool primary)
        {
            var b = new FlatButton(text) { Width = 140, Primary = primary, Margin = new Padding(8, 0, 0, 0) };
            Buttons.Controls.Add(b);
            return b;
        }

        List<JsonObject> Candidates() => f.presets.OfType<JsonObject>().Where(p => Str(p, "sexe") == sexe && Str(p["verdict"], "niveau") != "export"
            && Str(p, "statut") != "SANS_VISAGE" && (!valides || Str(p["validation"], "status") == "valide")).ToList();

        static bool RaceMatch(JsonObject p, JsonObject n) => Str(p, "race").EndsWith(Str(n, "race"), StringComparison.OrdinalIgnoreCase);

        void Render()
        {
            box.SuspendLayout();
            foreach (Control c in box.Controls.Cast<Control>().ToList()) c.Dispose();
            box.Controls.Clear();
            box.Controls.Add(new TextLabel { Text = L.T("Choisis un mod, coche des PNJ, puis attribue tes followers (automatiquement ou à la main). Rien n'est enregistré avant « Enregistrer ». Ensuite, « Construire GF_Remplacements » fait le paquet de tous les remplacements."), ForeColor = Theme.Muted, Font = Theme.Small });
            var top = new HRow { Height = 34 };
            var pSexe = new Picker();
            pSexe.SetItems(["F", "M"], [L.T("Femmes"), L.T("Hommes")]);
            pSexe.Index = sexe == "M" ? 1 : 0;
            pSexe.Changed += (_, _) => { sexe = pSexe.Value ?? "F"; mod = ""; Render(); };
            var mods = Npcs().Where(n => Str(n, "sexe") == sexe).GroupBy(n => Str(n, "plugin")).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var pMod = new Picker();
            pMod.SetItems(["", .. mods.Select(g => g.Key)], [L.T("Choisir un mod…"), .. mods.Select(g => $"{g.Key} ({g.Count()})")]);
            pMod.Index = Math.Max(0, pMod.Items.IndexOf(mod));
            pMod.Changed += (_, _) => { mod = pMod.Value ?? ""; Render(); };
            top.Controls.Add(pSexe); top.Controls.Add(pMod);
            box.Controls.Add(top);
            var opts = new HRow { Height = 26 };
            var tLib = new Toggle(L.T("Seulement les libres"), libres);
            tLib.Changed += (_, _) => { libres = tLib.Checked; Render(); };
            var tVal = new Toggle(L.T("Followers validés seulement"), valides);
            tVal.Changed += (_, _) => { valides = tVal.Checked; Render(); };
            opts.Controls.Add(tLib); opts.Controls.Add(tVal);
            box.Controls.Add(opts);

            var byNpc = all.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Value!.ToString(), kv => kv.Key);
            var l = Npcs().Where(n => Str(n, "sexe") == sexe && (mod.Length == 0 || Str(n, "plugin") == mod)).ToList();
            if (libres) l = l.Where(n => NpcFree(n) || byNpc.ContainsKey(Str(n, "id"))).ToList();
            l = l.OrderBy(n => Str(n, "nom"), StringComparer.CurrentCultureIgnoreCase).ToList();
            var shown = mod.Length > 0 ? l.Take(300).ToList() : [];
            var cands = Candidates();
            box.Controls.Add(new TextLabel
            {
                ForeColor = Theme.Muted, Font = Theme.Small,
                Text = mod.Length > 0 ? L.T("{0} PNJ · {1} follower(s) possible(s) (même sexe, avec export RaceMenu).", l.Count, cands.Count) : L.T("Choisis un mod pour voir ses PNJ."),
            });
            rows = [];
            if (shown.Count > 0)
            {
                var acts = new HRow { Height = 34 };
                var tAll = new FlatButton(L.T("Tout cocher"));
                var auto = new FlatButton(L.T("Attribuer automatiquement"));
                var clear = new FlatButton(L.T("Vider les cochés"));
                acts.Controls.Add(tAll); acts.Controls.Add(auto); acts.Controls.Add(clear);
                box.Controls.Add(acts);
                foreach (var n in shown)
                {
                    bool blocked = f.NpcMarks(n).Any(m => m.Level == "erreur");
                    var was = byNpc.GetValueOrDefault(Str(n, "id")) ?? "";
                    var list = cands.ToList();
                    if (was.Length > 0 && list.All(p => Str(p, "id") != was))
                        list.Insert(0, f.presets.OfType<JsonObject>().FirstOrDefault(p => Str(p, "id") == was) ?? new JsonObject { ["id"] = was, ["nom"] = was });
                    var check = new Toggle(Str(n, "nom") + "  (" + Str(n, "edid") + " · " + (Str(n, "raceNom") is { Length: > 0 } rn ? rn : Str(n, "race")) + ")"
                        + (blocked ? "  — " + L.T("Bloqué : SkyPatcher") : ""), was.Length > 0) { Enabled = !blocked };
                    var pick = new Picker { Enabled = !blocked };
                    pick.SetItems(["", .. list.Select(p => Str(p, "id"))], ["—", .. list.Select(p => Str(p, "nom") + (Str(p, "race") is { Length: > 0 } r ? " · " + r : ""))]);
                    pick.Index = Math.Max(0, pick.Items.IndexOf(was));
                    pick.Changed += (_, _) => { if (pick.Value is { Length: > 0 }) check.Checked = true; };
                    var line = new HRow { Height = 34 };
                    line.Controls.Add(check); line.Controls.Add(pick);
                    box.Controls.Add(line);
                    rows.Add((n, blocked, was, check, pick));
                }
                if (l.Count > shown.Count) box.Controls.Add(new TextLabel { Text = L.T("{0} autres non affichés.", l.Count - shown.Count), ForeColor = Theme.Muted, Font = Theme.Small });
                tAll.Click += (_, _) => { bool on = rows.Any(r => !r.Blocked && !r.Check.Checked); foreach (var r in rows) if (!r.Blocked) r.Check.Checked = on; };
                clear.Click += (_, _) => { foreach (var r in rows) if (r.Check.Checked) r.Pick.Index = 0; };
                auto.Click += (_, _) =>
                {
                    // followers déjà pris : remplaçants d'un PNJ hors de ce tableau, ou choisis dans une autre ligne
                    var shownIds = rows.Select(r => Str(r.N, "id")).ToHashSet();
                    var used = all.Where(kv => kv.Value is JsonValue && !shownIds.Contains(kv.Value!.ToString())).Select(kv => kv.Key).ToHashSet();
                    foreach (var r in rows) if (r.Pick.Value is { Length: > 0 } v) used.Add(v);
                    int missing = 0;
                    foreach (var r in rows)
                    {
                        if (!r.Check.Checked || r.Pick.Value is { Length: > 0 }) continue;
                        var freeC = cands.Where(p => !used.Contains(Str(p, "id"))).ToList();
                        var p = freeC.FirstOrDefault(x => RaceMatch(x, r.N)) ?? freeC.FirstOrDefault();
                        if (p == null) { missing++; continue; }
                        r.Pick.Index = Math.Max(0, r.Pick.Items.IndexOf(Str(p, "id"))); used.Add(Str(p, "id"));
                    }
                    if (missing > 0) new MessageDialog(L.T("Remplacer des PNJ en lot"), L.T("Pas assez de followers libres : {0} PNJ coché(s) sans follower.", missing)).ShowDialog(this);
                };
            }
            box.ResumeLayout();
            box.PerformLayout();
        }

        async Task Save()
        {
            string Name(string id) => f.presets.OfType<JsonObject>().FirstOrDefault(p => Str(p, "id") == id) is { } p ? Str(p, "nom") : id;
            var wants = rows.Select(r => r.Check.Checked ? r.Pick.Value ?? "" : "").ToList();
            var dup = wants.Where(w => w.Length > 0).GroupBy(w => w).FirstOrDefault(g => g.Count() > 1)?.Key;
            if (dup != null) { new MessageDialog(L.T("Remplacer des PNJ en lot"), L.T("{0} est choisi pour plusieurs PNJ : un follower ne remplace qu'un seul PNJ.", Name(dup))).ShowDialog(this); return; }
            var choix = new JsonObject();
            var lines = new List<string>();
            // retraits d'abord, attributions ensuite : un follower qui passe d'une ligne à une autre garde sa nouvelle attribution
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Was.Length > 0 && wants[i] != rows[i].Was) { choix[rows[i].Was] = null; lines.Add(L.T("{0} ne remplace plus {1}", Name(rows[i].Was), Str(rows[i].N, "nom"))); }
            for (int i = 0; i < rows.Count; i++)
            {
                var want = wants[i];
                if (want == rows[i].Was || want.Length == 0) continue;
                if (all[want] is JsonValue ov && ov.ToString() is { } old && old != Str(rows[i].N, "id") && rows.All(x => Str(x.N, "id") != old))
                    lines.Add(L.T("{0} ne remplace plus {1}", Name(want), Npcs().FirstOrDefault(x => Str(x, "id") == old) is { } on ? Str(on, "nom") : old));
                choix[want] = Str(rows[i].N, "id"); lines.Add($"{Str(rows[i].N, "nom")} → {Name(want)}");
            }
            if (lines.Count == 0) { new MessageDialog(L.T("Remplacer des PNJ en lot"), L.T("Aucun changement.")).ShowDialog(this); return; }
            if (new ConfirmDialog(L.T("Remplacer des PNJ en lot"), L.T("Enregistrer ces {0} changement(s) ?", lines.Count) + "\n\n" + string.Join("\n", lines.Take(12)) + (lines.Count > 12 ? "\n…" : "")) { Size = new Size(640, 420) }.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                all = (await f.Api("api/replace-batch", new JsonObject { ["choix"] = choix })).AsObject();
                Render();
                new MessageDialog(L.T("Remplacer des PNJ en lot"), L.T("{0} changement(s) enregistré(s). {1} PNJ remplacé(s) en tout : « Construire GF_Remplacements » pour faire le paquet.", lines.Count, all.Count)).ShowDialog(this);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
        }
    }
}
