using System.Drawing;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace GenerateFollower.Native;

/// <summary>
/// Fenêtres « Plusieurs followers » et « Mes packs » de la fenêtre native (plan work\diag\natif\PLAN.md, étape 4), comme la
/// page. Plusieurs followers : copie des choix du follower ouvert, archétype, tête d'un autre preset en lot (expérimental),
/// sur les presets affichés dans la liste (/api/batch, /api/donneurs-lot). Mes packs : reconstruire, ouvrir le dossier,
/// fiches des validés ou d'un pack (/api/sheets, export et import .gffiches.json).
/// </summary>
public sealed partial class BuilderForm
{
    static readonly JsonSerializerOptionsHolder Json = new();
    sealed class JsonSerializerOptionsHolder
    {
        public readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    }

    List<string> ShownIds(bool withCurrent = false) => shown.Select(p => Str(p, "id")).Where(id => withCurrent || id != current).ToList();

    // ------------------------------------------------------------------ Plusieurs followers

    async Task OpenBatch()
    {
        if (current == null) { new MessageDialog(L.T("Plusieurs followers"), L.T("Choisis d'abord un preset dans la liste.")).ShowDialog(this); return; }
        if (opts == null) { try { opts = (await Api("api/options/" + current)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { } }
        if (opts == null || Str(opts, "erreur").Length > 0) return;
        using var dlg = new BatchDialog(this);
        dlg.ShowDialog(this);
        // choix du follower ouvert peut-être changés (archétype, donneur…)
        if (current != null) await Select(current);
    }

    static (string Key, string Label)[] BatchParts() =>
    [
        ("combat", L.T("Style et classe")), ("voix", L.T("Voix (même sexe seulement)")), ("tenue", L.T("Tenue")), ("armes", L.T("Armes de départ")),
        ("sorts", L.T("Sorts")), ("atouts", L.T("Atouts")), ("avance", L.T("Réglages avancés")), ("mariage", L.T("Mariage")), ("lieu", L.T("Lieu de départ")),
        ("cheveux", L.T("Cheveux SMP")), ("corps", L.T("Preset de corps OBody (même sexe seulement)")),
    ];

    sealed class BatchDialog : DarkDialog
    {
        readonly BuilderForm f;
        public BatchDialog(BuilderForm f) : base(L.T("Plusieurs followers"), new Size(760, 720))
        {
            this.f = f;
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            var v = new VStack { BackColor = Theme.Bg, Gap = 8 };
            var ids = f.ShownIds();
            v.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Muted,
                Text = L.T("Source : {0}.", Str(f.info, "nom")) + " " + L.T("Agit sur les {0} autre(s) preset(s) affiché(s) dans la liste (utilise la recherche et les filtres pour choisir).", ids.Count) });
            var parts = BatchParts().Select(p => (p.Key, p.Label, Box: new Toggle(p.Label, false))).ToList();
            foreach (var p in parts) v.Controls.Add(p.Box);
            var copy = new FlatButton(L.T("Copier ce follower vers les presets affichés")) { Enabled = ids.Count > 0 };
            copy.Click += async (_, _) =>
            {
                var chosen = parts.Where(p => p.Box.Checked).ToList();
                if (chosen.Count == 0) { new MessageDialog(L.T("Plusieurs followers"), L.T("Coche au moins une partie à copier.")).ShowDialog(this); return; }
                await Run(new JsonObject { ["source"] = f.current, ["parts"] = new JsonArray(chosen.Select(p => (JsonNode)p.Key).ToArray()) },
                    L.T("Copier {0} de ce follower vers {1} preset(s) ? Leurs choix actuels pour ces parties seront remplacés.", string.Join(", ", chosen.Select(p => p.Label)), ids.Count));
            };
            v.Controls.Add(copy);
            var archs = (f.opts!["archetypes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var arch = GroupedPicker(archs, L.T("Choisir un archétype…"), "");
            var apply = new FlatButton(L.T("Appliquer aux presets affichés")) { Width = 260, Enabled = ids.Count > 0 };
            apply.Click += async (_, _) =>
            {
                var a = archs.FirstOrDefault(x => Str(x, "id") == arch.Value);
                if (a == null) return;
                await Run(new JsonObject { ["archetype"] = Str(a, "id"), ["parts"] = new JsonArray() },
                    L.T("Appliquer l'archétype {0} à {1} preset(s) ? Leur style, classe, tenue, armes, sorts et atouts seront remplacés.", Str(a, "nom"), ids.Count));
            };
            v.Controls.Add(new FixedEndRow(arch, apply));
            if (Bool(f.opts, "experimental")) DonorPart(v);
            scroll.Controls.Add(v);
            scroll.Resize += (_, _) => v.Width = scroll.ClientSize.Width - 4;
            Body.Controls.Add(scroll);
            AddButton(L.T("Fermer"), DialogResult.OK, true);
            Shown += (_, _) => { v.Width = scroll.ClientSize.Width - 4; Theme.DarkScroll(scroll); };
        }

        async Task Run(JsonObject body, string question)
        {
            var ids = f.ShownIds();
            if (ids.Count == 0 || new ConfirmDialog(L.T("Plusieurs followers"), question) { Size = new Size(640, 260) }.ShowDialog(this) != DialogResult.OK) return;
            body["ids"] = new JsonArray(ids.Select(x => (JsonNode)x).ToArray());
            try
            {
                var r = await f.Api("api/batch", body);
                new MessageDialog(L.T("Plusieurs followers"), L.T("{0} follower(s) modifié(s).", Num(r, "modifies") ?? 0)
                    + (Num(r, "voixIgnorees") is > 0 and var vi ? " " + L.T("Voix non copiée pour {0} follower(s) de l'autre sexe.", vi) : "")
                    + (Num(r, "corpsIgnores") is > 0 and var ci ? " " + L.T("Preset de corps non copié pour {0} follower(s) de l'autre sexe.", ci) : "")).ShowDialog(this);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
        }

        void DonorPart(VStack v)
        {
            var all = f.ShownIds(withCurrent: true);
            v.Controls.Add(new TextLabel { Text = L.T("Tête d'un autre preset (sans Export Head)"), Font = Theme.Bold });
            v.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Muted, Text = L.T("Donne à chaque preset affiché ({0}) dont la tête n'est pas prête son meilleur donneur (le plus de pièces en commun). Ceux qui ont déjà un donneur ne changent pas ; chaque choix se modifie ensuite au poste Visage.", all.Count) });
            var lv = new[] { ("export", L.T("Sans export (bleu)"), true), ("rouge", L.T("Ne passera pas en jeu (rouge)"), true), ("orange", L.T("Passera avec des défauts (orange)"), false) }
                .Select(x => (x.Item1, Box: new Toggle(x.Item2, x.Item3))).ToList();
            foreach (var x in lv) v.Controls.Add(x.Box);
            var go = new FlatButton(L.T("Donner leur meilleur donneur")) { Enabled = all.Count > 0 };
            go.Click += async (_, _) =>
            {
                var niveaux = lv.Where(x => x.Box.Checked).Select(x => x.Item1).ToList();
                if (niveaux.Count == 0) { new MessageDialog(L.T("Plusieurs followers"), L.T("Coche au moins un verdict.")).ShowDialog(this); return; }
                if (new ConfirmDialog(L.T("Plusieurs followers"), L.T("Chercher un donneur pour les presets affichés sans tête prête ({0}) ?", all.Count)).ShowDialog(this) != DialogResult.OK) return;
                go.Enabled = false; go.Text = L.T("Recherche…");
                try
                {
                    var r = await f.Api("api/donneurs-lot", new JsonObject { ["ids"] = new JsonArray(all.Select(x => (JsonNode)x).ToArray()), ["niveaux"] = new JsonArray(niveaux.Select(x => (JsonNode)x).ToArray()) });
                    var sans = (r["sansDonneur"] as JsonArray ?? []).Select(x => x?.ToString()).ToList();
                    new MessageDialog(L.T("Plusieurs followers"), L.T("{0} preset(s) ont reçu un donneur ; {1} sans donneur possible ; {2} avaient déjà un donneur ; {3} non concernés.", Num(r, "donnes") ?? 0, sans.Count, Num(r, "dejaDonneur") ?? 0, Num(r, "ignores") ?? 0)
                        + (Num(r, "nonVerifies") is > 0 and var nv ? " " + L.T("{0} jamais vérifiés : lance « Tout vérifier » puis recommence.", nv) : "")
                        + (sans.Count > 0 ? "\n" + L.T("Sans donneur : {0}", string.Join(", ", sans)) : "")
                        + "\n" + L.T("Les verdicts de la liste seront à jour après « Tout vérifier » ; les têtes sont refaites au build.")).ShowDialog(this);
                }
                catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
                go.Enabled = true; go.Text = L.T("Donner leur meilleur donneur");
            };
            v.Controls.Add(go);
        }
    }

    // ------------------------------------------------------------------ Mes packs

    async Task ShowPacks()
    {
        await Guard(async () =>
        {
            var packs = (await Api("api/packs")).AsArray().OfType<JsonObject>().ToList();
            using var dlg = new PacksDialog(this, packs);
            dlg.ShowDialog(this);
        });
    }

    /// <summary>Fiches de plusieurs followers en un fichier (.gffiches.json) : ids, ou vide pour les validés.</summary>
    async Task ExportSheets(IWin32Window owner, string ids, string name)
    {
        try
        {
            var pack = await Api("api/sheets" + (ids.Length > 0 ? "?ids=" + Uri.EscapeDataString(ids) : ""));
            var file = string.Concat(name.Select(c => "\\/:*?\"<>|".Contains(c) ? '_' : c)) + ".gffiches.json";
            var dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            using var dlg = new SaveFileDialog { FileName = file, Filter = "GF (*.gffiches.json)|*.gffiches.json|JSON|*.json", InitialDirectory = Directory.Exists(dl) ? dl : "" };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return;
            File.WriteAllText(dlg.FileName, pack.ToJsonString(Json.Indented), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(owner); }
    }

    async Task ImportSheets(IWin32Window owner)
    {
        using var dlg = new OpenFileDialog { Filter = "GF (*.gffiches.json;*.json)|*.gffiches.json;*.json" };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        var f = new FileInfo(dlg.FileName);
        if (f.Length > 8 << 20) { new MessageDialog(L.T("Importer des fiches…"), L.T("Fichier trop gros pour un fichier de fiches.")).ShowDialog(owner); return; }
        if (new ConfirmDialog(L.T("Importer des fiches…"), L.T("Appliquer les fiches de « {0} » aux presets du même nom ? Leurs choix actuels seront remplacés.", f.Name)).ShowDialog(owner) != DialogResult.OK) return;
        try
        {
            var body = JsonNode.Parse(File.ReadAllText(f.FullName)) ?? new JsonObject();
            var r = await Api("api/sheets", body);
            var res = (r["resultats"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var done = res.Where(x => Bool(x, "ok")).ToList();
            var left = res.Where(x => !Bool(x, "ok")).ToList();
            var sb = new StringBuilder(L.T("{0} fiche(s) appliquée(s) sur {1}.", done.Count, res.Count));
            foreach (var x in done.Where(x => (x["ignores"] as JsonArray)?.Count > 0))
                sb.Append($"\n\n{Str(x, "nom")} — ").Append(L.T("Ignoré (pas disponible chez toi) :")).Append("\n- ").Append(string.Join("\n- ", (x["ignores"] as JsonArray)!.Select(i => i?.ToString())));
            if (left.Count > 0) sb.Append("\n\n").Append(L.T("Fiches laissées de côté :")).Append("\n- ").Append(string.Join("\n- ", left.Select(x => $"{Str(x, "nom")} : {Str(x, "raison")}")));
            new MessageDialog(L.T("Importer des fiches…"), sb.ToString()).ShowDialog(owner);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(owner); return; }
        await LoadList();
        if (current != null) await Select(current);
    }

    sealed class PacksDialog : DarkDialog
    {
        readonly BuilderForm f;
        readonly VStack v = new() { BackColor = Theme.Bg, Gap = 10 };
        public PacksDialog(BuilderForm f, List<JsonObject> packs) : base(L.T("Mes packs"), new Size(860, 640))
        {
            this.f = f;
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            scroll.Controls.Add(v);
            // cartes à la nouvelle largeur, puis la liste refaite avec leurs nouvelles hauteurs
            void Relayout()
            {
                v.Width = scroll.ClientSize.Width - 4;
                foreach (Control c in v.Controls) c.PerformLayout();
                v.PerformLayout();
            }
            scroll.Resize += (_, _) => Relayout();
            Body.Controls.Add(scroll);
            AddButton(L.T("Fermer"), DialogResult.OK, true);
            Fill(packs);
            Shown += (_, _) => { Relayout(); Theme.DarkScroll(scroll); };
            relayout = Relayout;
        }
        readonly Action relayout;

        void Fill(List<JsonObject> packs)
        {
            v.SuspendLayout();
            foreach (Control c in v.Controls.Cast<Control>().ToList()) c.Dispose();
            v.Controls.Clear();
            // fiches de plusieurs followers en un fichier : celles des validés (ici) ou d'un pack (sur sa carte)
            var bar = new HRow { Height = 34 };
            var exp = new FlatButton(L.T("Exporter les fiches des validés"));
            exp.Click += async (_, _) => await f.ExportSheets(this, "", L.T("validés"));
            var imp = new FlatButton(L.T("Importer des fiches…"));
            imp.Click += async (_, _) => await f.ImportSheets(this);
            bar.Controls.Add(exp); bar.Controls.Add(imp);
            v.Controls.Add(bar);
            if (packs.Count == 0) v.Controls.Add(new TextLabel { Text = L.T("Aucun pack construit pour l'instant."), ForeColor = Theme.Muted });
            else v.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Muted, Text = L.T("« Reconstruire » refait le même ZIP (même plugin, même nom, mêmes followers) avec leurs choix actuels : à faire après avoir changé une voix, une tenue, un lieu… Installe-le ensuite par-dessus l'ancien.") });
            foreach (var p in packs)
            {
                var card = VStack.MakeCard();
                var zip = Str(p, "zip");
                card.Controls.Add(new TextLabel { Text = Str(p, "titre") is { Length: > 0 } t ? t : zip, Font = Theme.Bold });
                var followers = (p["followers"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                card.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Muted,
                    Text = $"{zip} · {Str(p, "date")} · {Num(p, "mo")} {L.T("Mo")}" + (Str(p, "probleme").Length > 0 ? "" :
                        $"\n{Str(p, "plugin")} · {L.T("{0} follower(s)", followers.Count)} : {string.Join(", ", followers.Select(x => Str(x, "nom") is { Length: > 0 } n ? n : Str(x, "id")))}"
                        + (Bool(p, "sansPouvoir") ? " · " + L.T("sans pouvoir d'invocation") : "")) });
                if (Str(p, "probleme") is { Length: > 0 } pb) card.Controls.Add(new TextLabel { Text = "⚠ " + pb, ForeColor = Theme.Warn, Font = Theme.Small });
                var missing = followers.Where(x => Str(x, "nom").Length == 0).Select(x => Str(x, "id")).ToList();
                if (missing.Count > 0) card.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Warn, Text = "⚠ " + L.T("Plus dans ta liste de presets : {0} (retiré(s) du pack si tu le reconstruis)", string.Join(", ", missing)) });
                var row = new HRow();
                var rb = new FlatButton(L.T("Reconstruire")) { Enabled = Str(p, "probleme").Length == 0 };
                var ob = new FlatButton(L.T("Ouvrir le dossier"));
                var sb = new FlatButton(L.T("Exporter ses fiches")) { Enabled = followers.Any(x => Str(x, "nom").Length > 0) };
                rb.Click += async (_, _) =>
                {
                    if (new ConfirmDialog(L.T("Reconstruire"), L.T("Reconstruire {0} avec les choix actuels de ses followers ? Le ZIP actuel sera remplacé.", zip)).ShowDialog(this) != DialogResult.OK) return;
                    rb.Enabled = false; rb.Text = L.T("Construction…");
                    try
                    {
                        var r = await f.Api("api/packs-rebuild", new JsonObject { ["zip"] = zip });
                        new MessageDialog(L.T("{0} : {1} follower(s)", Str(r, "plugin"), Num(r, "followers") ?? 0), $"{Str(r, "zip")}\n\n{Str(r, "log")}\n{Str(r, "rapport")}").ShowDialog(this);
                        Fill((await f.Api("api/packs")).AsArray().OfType<JsonObject>().ToList());
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { rb.Text = L.T("Reconstruire"); rb.Enabled = true; new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
                };
                ob.Click += async (_, _) =>
                {
                    try { await f.Api("api/packs-open", new JsonObject { ["zip"] = zip }); }
                    catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
                };
                sb.Click += async (_, _) => await f.ExportSheets(this, string.Join(",", followers.Where(x => Str(x, "nom").Length > 0).Select(x => Str(x, "id"))),
                    System.Text.RegularExpressions.Regex.Replace(zip, @"\.zip$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                row.Controls.Add(rb); row.Controls.Add(ob); row.Controls.Add(sb);
                card.Controls.Add(row);
                v.Controls.Add(card);
            }
            v.ResumeLayout();
            v.PerformLayout();
            relayout?.Invoke();
        }
    }
}
