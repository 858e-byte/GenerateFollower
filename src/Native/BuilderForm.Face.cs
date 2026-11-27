using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace GenerateFollower.Native;

/// <summary>
/// Poste « Visage » de la fiche native (plan work\diag\natif\PLAN.md, étape 4), comme la page : marche à suivre quand le
/// preset pose problème, tête d'un autre preset (fonctions expérimentales seulement), export RaceMenu associé (/api/import),
/// génération de la tête (/api/generate), détails techniques (vérifications avec réparation de texture /api/remap,
/// headparts, formes FaceGen).
/// </summary>
public sealed partial class BuilderForm
{
    JsonObject? donorList;   // /api/donneurs/<id>
    bool techOpen;

    void FaceStation(VStack card)
    {
        var i = info!;
        DonorBlock(card, i);
        AdviceBlock(card, i);
        card.Controls.Add(Muted(Str(i, "preset") + "\n" + Str(i, "race") + " · " + Str(i, "sexe")
            + (Str(i, "peau") is { Length: > 0 } pe ? " · " + L.T("peau") + " " + pe : "")
            + "\n" + L.T("statut du scan :") + " " + Str(i, "statutTexte")));

        // ---- export RaceMenu
        card.Controls.Add(H2(L.T("Export RaceMenu (visage du follower en jeu)")));
        var imp = i["import"] as JsonObject ?? [];
        var mode = Str(imp, "mode");
        var path = Str(imp, "path");
        var cands = (imp["candidats"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var keys = new List<string>();
        var labels = new List<string>();
        if (mode == "donneur") { keys.Add("donneur"); labels.Add(L.T("Tête prise sur un autre preset : {0}", path)); }
        keys.Add("auto"); labels.Add(mode == "auto" && path.Length > 0 ? L.T("Automatique : {0}", path) : L.T("Automatique : aucun trouvé"));
        keys.Add("aucun"); labels.Add(L.T("Aucun (tête générée : rejetée par le jeu)"));
        foreach (var c in cands) { keys.Add("m:" + Str(c, "path")); labels.Add(Str(c, "path") + " (" + Str(c, "raison") + ")"); }
        if (mode == "manuel" && cands.All(c => Str(c, "path") != path)) { keys.Add("m:" + path); labels.Add(path); }
        keys.Add("autre"); labels.Add(L.T("Autre fichier de CharGen…"));
        var pick = new Picker { Enabled = mode != "donneur" };
        pick.SetItems(keys, labels);
        pick.Index = Math.Max(0, keys.IndexOf(mode switch { "donneur" => "donneur", "manuel" => "m:" + path, "aucun" => "aucun", _ => "auto" }));
        pick.Changed += async (_, _) =>
        {
            var v = pick.Value ?? "auto";
            JsonObject body;
            if (v == "autre")
            {
                var p = AskText(L.T("Autre fichier de CharGen…"), L.T("Chemin du .nif relatif à SKSE\\Plugins\\CharGen (ex. Head Sculpts\\Female\\Nord\\Lydia.nif) :"));
                if (string.IsNullOrWhiteSpace(p)) { Detail(keepScroll: true); return; }
                body = new JsonObject { ["mode"] = "manuel", ["path"] = p.Trim() };
            }
            else body = v.StartsWith("m:") ? new JsonObject { ["mode"] = "manuel", ["path"] = v[2..] } : new JsonObject { ["mode"] = v };
            var id = current;
            if (id == null) return;
            try { await Api("api/import/" + id, body); }
            catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
            await ReloadInfo(id);
            LoadHead(keepView: true);
        };
        card.Controls.Add(pick);
        card.Controls.Add(Muted(L.T("Sans export : dans RaceMenu, charge le preset puis « Export Head » sous le nom du preset.")));
        var gen = new FlatButton(L.T("Générer / régénérer la tête"));
        gen.Click += async (_, _) => await Generate(gen);
        card.Controls.Add(gen);

        // ---- détails techniques (ouverts d'office si le verdict n'est pas vert)
        var lvl = Str(i["verdict"], "niveau");
        var fold = new Fold(() => L.T("Détails techniques"), techOpen || (lvl.Length > 0 && lvl != "vert"), () => TechDetails(i));
        fold.Toggled += open => techOpen = open;
        card.Controls.Add(fold);
    }

    IEnumerable<Control> TechDetails(JsonObject i)
    {
        yield return H2(L.T("Vérifications (règles du Creation Kit)"));
        foreach (var c in (i["verifications"] as JsonArray ?? []).OfType<JsonObject>())
        {
            yield return Check(c);
            foreach (var r in (c["reparations"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var from = Str(r, "from"); var to = Str(r, "to");
                var row = new ChosenRow(from.Split('\\').Last() + " → " + to + "  (" + Str(r, "mod") + ")", Theme.Muted, L.T("Utiliser"),
                    async () => await Remap(from, to), mark: "↳ ");
                yield return row;
            }
        }
        yield return H2("Headparts");
        var lines = new List<string>();
        void Part(JsonNode p, int depth)
        {
            string yes = L.T("oui"), no = L.T("non");
            var raceOk = p["raceOk"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) ? (rb ? yes : no) : "—";
            var tags = (Str(p, "origine") == "défaut de la race" ? " [" + L.T("défaut race") + "]" : "") + (Bool(p, "recree") ? " [" + L.T("recréé") + "]" : "");
            lines.Add(new string(' ', depth * 3) + (depth > 0 ? "↳ " : "") + Str(p, "edid") + tags + " · " + Str(p, "type")
                + " · " + L.T("Modèle") + " " + (Bool(p, "modele") ? yes : no) + " · Race " + raceOk + " · " + L.T("Sexe") + " " + (Bool(p, "sexeOk") ? yes : no));
            foreach (var x in p["extras"] as JsonArray ?? []) if (x != null) Part(x, depth + 1);
        }
        foreach (var p in (i["headparts"] as JsonArray ?? []).OfType<JsonObject>()) Part(p, 0);
        yield return new TextLabel { Text = string.Join("\n", lines), Font = Theme.Small, ForeColor = Theme.Text };
        yield return H2(L.T("FaceGen : formes attendues / présentes"));
        var fg = i["facegen"] as JsonObject;
        if (Str(fg, "export") is { Length: > 0 } ex) yield return Muted(L.T("Visage = {0} (exporté le {1}) : voir les vérifications ci-dessus.", ex, Str(fg, "date")));
        else if (Bool(fg, "formes"))
        {
            var missing = (fg!["manquantes"] as JsonArray ?? []).Select(x => x?.ToString()).ToHashSet();
            var fl = (fg["attendues"] as JsonArray ?? []).Select(x => x?.ToString() ?? "")
                .Select(a => a + " : " + (missing.Contains(a) ? L.T("absente") : L.T("présente"))).ToList();
            fl.AddRange((fg["enTrop"] as JsonArray ?? []).Select(x => (x?.ToString() ?? "") + " : " + L.T("en trop")));
            yield return new TextLabel { Text = string.Join("\n", fl), Font = Theme.Small, ForeColor = missing.Count > 0 ? Theme.Warn : Theme.Text };
            yield return Muted(L.T("NIF généré le {0}", Str(fg, "date")));
        }
        else yield return Muted(L.T("Pas de tête générée."));
    }

    async Task Remap(string from, string to)
    {
        var id = current;
        if (id == null) return;
        try { await Api("api/remap", new JsonObject { ["from"] = from, ["to"] = to }); }
        catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); return; }
        await Select(id);   // revérification et textures relues
    }

    async Task Generate(FlatButton b)
    {
        var id = current;
        if (id == null) return;
        b.Enabled = false; b.Text = L.T("Génération…");
        try
        {
            var r = await Api("api/generate/" + id, new JsonObject());
            if (!Bool(r, "ok")) new MessageDialog(L.T("Échec de la génération"), r["erreurs"]?.ToString() ?? "").ShowDialog(this);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
        if (presets.OfType<JsonObject>().FirstOrDefault(p => Str(p, "id") == id) is { } lp) lp["tete"] = true;
        if (id == current) await Select(id);
    }

    /// <summary>Marche à suivre quand le preset pose problème (verdict orange, rouge ou sans export, exclu, mods manquants).</summary>
    void AdviceBlock(VStack card, JsonObject i)
    {
        var p = presets.OfType<JsonObject>().FirstOrDefault(x => Str(x, "id") == current);
        var name = Str(i, "preset").Split('\\').Last();
        var lvl = Str(i["verdict"], "niveau");
        var st = Str(i, "statut");
        bool problem = lvl is "rouge" or "orange" or "export" || (p?["inclure"] is JsonValue inc && inc.TryGetValue<bool>(out var ib) && !ib)
            || st.Contains("MODS_MANQUANTS") || st.Contains("SANS_VISAGE") || st.Contains("PARTIEL");
        if (!problem) return;
        var box = new VStack { CardLook = true, Pad = 10, Gap = 6, Fill = Theme.Mix(Theme.Card, Theme.Warn, 0.08), Border = Theme.Warn };
        box.BackColor = box.Fill;
        box.Controls.Add(new TextLabel { Text = lvl == "export" ? L.T("Comment construire la tête de ce preset") : L.T("Comment corriger ce preset"), Font = Theme.Bold });
        if (lvl == "export")
            box.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Warn,
                Text = "💡 " + L.T("Astuce : dans RaceMenu, une fois le preset chargé, utilise « Export Head » pour sauvegarder (exporter) la tête, en lui donnant <b>exactement le même nom que ton preset</b> : « {0} ». Le Builder la retrouve alors tout seul.", name).Replace("<b>", "").Replace("</b>", "") });
        var steps = new[]
        {
            L.T("Dans RaceMenu, charge le preset « {0} ».", name),
            L.T("Si le visage est déformé, utilise « Import Head » avec le .nif fourni avec le preset."),
            L.T("Remplace les pièces qui manquent ou s'affichent mal (yeux, sourcils, cheveux, bouche…) par des pièces installées chez toi : le preset sera alors reconstruit avec tes propres mods."),
            L.T("Sauvegarde le preset sous le même nom, puis fais « Export Head » sous le nom exact « {0} ».", name),
            L.T("Reviens ici et clique « Rescanner » : la fiche est revérifiée avec ton nouvel export."),
        };
        box.Controls.Add(new TextLabel { Font = Theme.Small, Text = string.Join("\n", steps.Select((s, k) => $"{k + 1}. {s}")) });
        box.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Muted, Text = L.T("Le follower reprend exactement ce que RaceMenu affiche chez toi au moment de l'export.") });
        card.Controls.Add(box);
    }

    /// <summary>Tête d'un autre preset (option « teteDonneur », fonctions expérimentales seulement), comme la page.</summary>
    void DonorBlock(VStack card, JsonObject i)
    {
        var d = i["donneur"] as JsonObject;
        var lvl = Str(i["verdict"], "niveau");
        if (!Bool(i, "experimental") || (d == null && lvl == "vert")) return;
        var box = new VStack { CardLook = true, Pad = 10, Gap = 6, Fill = Theme.Mix(Theme.Card, Theme.Accent, 0.06), Border = Theme.Accent };
        box.BackColor = box.Fill;
        box.Controls.Add(new TextLabel { Text = L.T("Tête d'un autre preset (sans Export Head)") + "  [" + L.T("expérimental") + "]", Font = Theme.Bold });
        var row = new HRow { Height = 34 };
        if (d != null)
        {
            // visage non écrit (export du donneur fait ailleurs) : le dire ici, pas seulement dans les détails techniques
            if (d["visageEcrit"] is JsonValue fw && fw.TryGetValue<bool>(out var written) && !written)
            {
                box.Fill = Theme.Mix(Theme.Card, Theme.Err, 0.10); box.Border = Theme.Err; box.BackColor = box.Fill;
                box.Controls.Add(new TextLabel { Font = Theme.Bold, ForeColor = Theme.Err, Text = "⚠ " + L.T("Le visage de ce preset n'a pas pu être écrit sur la tête de {0} : en jeu, le follower aura le visage de {0}.", Str(d, "nom")) });
                box.Controls.Add(new TextLabel { Font = Theme.Small, Text = L.T("Cause la plus courante : l'export de {0} a été fait chez un autre joueur (téléchargé avec le preset) et ne correspond pas à ton jeu. Choisis un autre donneur, ou refais « Export Head » de {0} chez toi.", Str(d, "nom")) });
            }
            else box.Controls.Add(new TextLabel { Font = Theme.Small, Text = L.T("Ce follower prend la tête de <b>{0}</b> : ses yeux, sourcils, cils et cheveux, avec le visage, la teinte et les couleurs de ce preset.", Str(d, "nom")).Replace("<b>", "").Replace("</b>", "") });
            var change = new FlatButton(L.T("Choisir un autre donneur"));
            change.Click += async (_, _) => await LoadDonors();
            var remove = new FlatButton(L.T("Ne plus utiliser de donneur"));
            remove.Click += async (_, _) => { remove.Enabled = false; remove.Text = L.T("Génération…"); await SetDonor(null); };
            row.Controls.Add(change); row.Controls.Add(remove);
        }
        else
        {
            box.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Muted, Text = L.T("Sans refaire « Export Head », ce preset peut prendre la tête déjà acceptée par le jeu d'un autre de tes presets (même sexe, même tête). Son visage, sa teinte et ses couleurs sont gardés ; yeux, sourcils, cils et cheveux viennent du donneur (la coiffure se change au poste Coiffure).") });
            var find = new FlatButton(L.T("Chercher un donneur"));
            find.Click += async (_, _) => { find.Enabled = false; find.Text = L.T("Recherche…"); await LoadDonors(); };
            row.Controls.Add(find);
        }
        box.Controls.Add(row);
        var list = donorList != null && Str(donorList, "id") == current ? donorList : null;
        if (list != null)
        {
            var donors = (list["donneurs"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (Str(list, "erreur") is { Length: > 0 } err) box.Controls.Add(new TextLabel { Text = err, ForeColor = Theme.Err, Font = Theme.Small });
            else if (donors.Count == 0)
                box.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Err, Text = L.T("Aucun donneur : aucun de tes presets prêts pour le jeu n'a la même tête ({0}). Un seul « Export Head » d'un preset avec cette tête suffira.", Str(list, "tete")) });
            else
            {
                box.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Muted, Text = L.T("{0} donneur(s) possible(s), le meilleur en premier (le plus de pièces en commun) :", donors.Count) });
                var pick = new Picker();
                pick.SetItems(donors.Select(x => Str(x, "id")).ToList(),
                    donors.Select(x => Str(x, "nom") + " (" + Str(x, "race") + " · " + L.T("{0}/{1} pièces en commun", Num(x, "communes") ?? 0, Num(list, "pieces") ?? 0) + ")"
                        + (x["visage"] is JsonValue fv && fv.TryGetValue<bool>(out var ok) && !ok ? "  ⚠ " + L.T("visage non transférable") : "")).ToList());
                pick.Index = Math.Max(0, d != null ? pick.Items.IndexOf(Str(d, "id")) : 0);
                var use = new FlatButton(L.T("Prendre cette tête")) { Width = 180, Primary = true };
                use.Click += async (_, _) => { if (pick.Value is { Length: > 0 } v) { use.Enabled = false; use.Text = L.T("Génération…"); await SetDonor(v); } };
                box.Controls.Add(new FixedEndRow(pick, use));
            }
        }
        card.Controls.Add(box);
    }

    async Task LoadDonors()
    {
        var id = current;
        if (id == null) return;
        JsonObject r;
        try { r = (await Api("api/donneurs/" + id)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { r = new JsonObject { ["erreur"] = e.Message }; }
        r["id"] = id;
        donorList = r;
        if (id == current) Detail(keepScroll: true);
    }

    async Task SetDonor(string? donorId)
    {
        var id = current;
        if (id == null) return;
        try { opts = (await Api("api/options/" + id, new JsonObject { ["teteDonneur"] = donorId })).AsObject(); }
        catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); return; }
        donorList = null;
        // pièces, teinte et aperçu refaits pour le nouveau donneur
        try
        {
            var r = await Api("api/generate/" + id, new JsonObject());
            if (!Bool(r, "ok")) new MessageDialog(L.T("Échec de la génération"), r["erreurs"]?.ToString() ?? "").ShowDialog(this);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
        if (id == current) await Select(id);
    }

    /// <summary>Petite saisie de texte (remplace le prompt() de la page).</summary>
    string? AskText(string title, string question)
    {
        using var dlg = new TextAskDialog(title, question);
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.Value : null;
    }

    sealed class TextAskDialog : DarkDialog
    {
        readonly Field input = new("");
        public string Value => input.Box.Text;
        public TextAskDialog(string title, string question) : base(title, new Size(640, 230))
        {
            var v = new VStack { Dock = DockStyle.Top, BackColor = Theme.Bg, Gap = 10 };
            v.Controls.Add(new TextLabel { Text = question, ForeColor = Theme.Muted });
            v.Controls.Add(input);
            Body.Controls.Add(v);
            AddButton(L.T("Annuler"), DialogResult.Cancel);
            AddButton("OK", DialogResult.OK, true);
            Shown += (_, _) => input.Box.Focus();
        }
    }
}
