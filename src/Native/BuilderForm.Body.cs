using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace GenerateFollower.Native;

/// <summary>
/// Poste « Corps et peau » de la fiche native (plan work\diag\natif\PLAN.md, étape 4), comme la page : skin (vignettes du
/// corps et du visage), couleur de peau (palette de la race ou couleur libre), preset OBody. Routes : /api/skins, /api/skin,
/// /api/skins-refresh, /api/skin-texture, /api/options (couleurPeau, corps), /api/bodypresets.
/// </summary>
public sealed partial class BuilderForm
{
    JsonObject? skinInfo;     // /api/skins/<id>
    JsonObject? bodyPresets;  // /api/bodypresets/<id>
    JsonObject? gloss;        // /api/skingloss/<id> (brillance du skin au cou)
    string? glossKey;         // preset|skin mesuré ou en cours de mesure
    string bodyFilter = "";
    string? bodyFamily, bodyFamilyFor;   // famille de corps affichée pour ce follower ("" : tous les corps ; sinon celle du follower)

    async Task LoadBodyInfo()
    {
        var id = current;
        if (id == null) return;
        JsonObject s, b;
        try { s = (await Api("api/skins/" + id)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { s = new JsonObject { ["erreur"] = e.Message }; }
        try { b = (await Api("api/bodypresets/" + id)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { b = new JsonObject { ["erreur"] = e.Message }; }
        if (id != current) return;
        skinInfo = s; bodyPresets = b;
        Detail(keepScroll: true);
    }

    void BodyStation(VStack card)
    {
        if (skinInfo == null || bodyPresets == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); _ = LoadBodyInfo(); return; }
        CoherenceBlock(card);
        if (view.Use3D) card.Controls.Add(Muted(L.T("L'aperçu passe en corps entier.")));
        SkinBlock(card);
        ToneBlock(card);
        BodyPresetBlock(card);
    }

    // ------------------------------------------------------------------ skin

    void SkinBlock(VStack card)
    {
        var si = skinInfo!;
        card.Controls.Add(H2(L.T("Peau (skin)")));
        if (Str(si, "erreur") is { Length: > 0 } err) { card.Controls.Add(new TextLabel { Text = err, ForeColor = Theme.Err }); return; }
        var skins = (si["skins"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        card.Controls.Add(Muted(L.T("Corps du follower : <b>{0}</b> ({1}) · {2} skin(s) compatible(s)", Str(si, "corps"), Str(si, "sexe"), skins.Count).Replace("<b>", "").Replace("</b>", "")));
        if (Bool(si, "erin"))
            card.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Warn, Text = L.T("Race Erin : « Peau du jeu » = la peau anime d'Erin. Un skin ne change que le corps (le CBBE 3BA d'Erin) : le visage anime garde sa texture, la différence peut se voir au cou.") });
        var pick = new Picker();
        pick.SetItems(["", .. skins.Select(x => Str(x, "id"))],
            [L.T("Peau du jeu (par défaut)"), .. skins.Select(x => Str(x, "nom") + " (" + Str(x, "source") + ", " + L.T("{0} Mo", Num(x, "mo") ?? 0) + ")")]);
        pick.Index = Math.Max(0, pick.Items.IndexOf(Str(si, "choix")));
        var refresh = new FlatButton(L.T("Chercher les skins")) { Width = 170 };
        card.Controls.Add(new FixedEndRow(pick, refresh));
        if (Bool(si, "choixInconnu"))
            card.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Err, Text = L.T("Le skin choisi n'est plus trouvé (mod ou archive déplacé) : peau du jeu au build.") });
        var s = skins.FirstOrDefault(x => Str(x, "id") == Str(si, "choix"));
        if (s != null)
        {
            var roles = (s["roles"] as JsonArray ?? []).Select(r => r?.ToString() ?? "").ToList();
            var thumbs = new ThumbRow();
            if (roles.Contains("body_d")) thumbs.Add(http, "api/skin-texture?skin=" + Uri.EscapeDataString(Str(s, "id")) + "&role=body_d", L.T("corps"));
            if (roles.Contains("head_d")) thumbs.Add(http, "api/skin-texture?skin=" + Uri.EscapeDataString(Str(s, "id")) + "&role=head_d", L.T("visage"));
            if (thumbs.Controls.Count > 0) card.Controls.Add(thumbs);
            card.Controls.Add(Muted(Str(s, "chemin") + "\n" + (roles.Contains("head_d")
                ? L.T("Textures : {0}. Corps, mains, pieds et visage : copiés dans le mod du follower au build, sans réduction. Les autres PNJ et le joueur ne changent pas.", string.Join(", ", roles))
                : L.T("Textures : {0}. Corps, mains, pieds : copiés dans le mod du follower au build, sans réduction. Les autres PNJ et le joueur ne changent pas.", string.Join(", ", roles)))));
        }
        if (s != null) GlossBlock(card, Str(s, "id"));
        pick.Changed += async (_, _) =>
        {
            var id = current;
            if (id == null) return;
            Say(L.T("Enregistrement…"));
            try { await Api("api/skin/" + id, new JsonObject { ["skin"] = pick.Value is { Length: > 0 } v ? v : null }); }
            catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
            try { skinInfo = (await Api("api/skins/" + id)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { }
            Say(Str(info, "preset"));
            if (id != current) return;
            Detail(keepScroll: true);
            LoadHead(keepView: true);   // skin sur le visage et le corps
        };
        refresh.Click += async (_, _) =>
        {
            refresh.Enabled = false; refresh.Text = L.T("Recherche…");
            try
            {
                var r = await Api("api/skins-refresh", new JsonObject());
                if (current != null) skinInfo = (await Api("api/skins/" + current)).AsObject();
                Detail(keepScroll: true);
                new MessageDialog("Skins", L.T("{0} skins trouvés (tous corps confondus).", Num(r, "total") ?? 0)).ShowDialog(this);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this);
                refresh.Enabled = true; refresh.Text = L.T("Chercher les skins");
            }
        };
    }

    /// <summary>Option « brillanceCou » : montrée seulement si le corps du skin n'a pas la brillance de sa tête au cou.</summary>
    void GlossBlock(VStack card, string skin)
    {
        var key = current + "|" + skin;
        if (glossKey != key) { glossKey = key; gloss = null; _ = LoadGloss(key); return; }
        if (gloss == null || !Bool(gloss, "ecart")) return;
        card.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Warn, Text = "⚠ " + L.T("Dans ce skin, le corps et la tête n'ont pas la même brillance au cou (tête {0}, corps {1} sur 255) : le raccord peut se voir en jeu.", Num(gloss, "tete") ?? 0, Num(gloss, "corps") ?? 0) });
        var tg = new Toggle(L.T("Fondre la brillance au cou"), Bool(gloss, "choix"));
        card.Controls.Add(tg);
        card.Controls.Add(Muted(L.T("Au prochain build, le cou de la tête prend la brillance du corps et le visage garde la sienne. L'aperçu 3D ne montre pas la brillance. La normal map de la tête est livrée sans compression (environ 85 Mo en 4K).")));
        tg.Changed += async (_, _) =>
        {
            gloss!["choix"] = tg.Checked;
            await SaveOption(new JsonObject { ["brillanceCou"] = tg.Checked ? true : null });
        };
    }

    async Task LoadGloss(string key)
    {
        var id = current;
        if (id == null) return;
        JsonObject g;
        try { g = (await Api("api/skingloss/" + id)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { return; }
        if (glossKey != key || id != current) return;
        gloss = g;
        if (Bool(g, "ecart") && station == "corps") Detail(keepScroll: true);
    }

    // ------------------------------------------------------------------ couleur de peau

    void ToneBlock(VStack card)
    {
        if (Bool(skinInfo, "erin") || opts == null || !opts.ContainsKey("peauPalette")) return;
        var chosen = Str(opts, "peauChoix");
        var preset = Str(info, "peau");
        var pal = (opts["peauPalette"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        card.Controls.Add(H2(L.T("Couleur de peau")));
        card.Controls.Add(Muted((chosen.Length > 0 ? L.T("Couleur choisie : {0}", chosen) + " · " : "")
            + (preset.Length > 0 ? L.T("Couleur du preset : {0}", preset) : L.T("Le preset n'a pas de couleur de peau."))));
        if (pal.Count > 0)
        {
            var sw = new Swatches(pal.Select(p => (Str(p, "couleur"), Str(p, "nom"))).ToList(), chosen);
            sw.Picked += async c => await SaveOption(new JsonObject { ["couleurPeau"] = c });
            card.Controls.Add(sw);
            card.Controls.Add(Muted(L.T("Palette de la race : {0} couleurs.", pal.Count)));
        }
        var free = new FlatButton(L.T("Autre couleur :").TrimEnd(' ', ':') + "…") { Width = 200 };
        free.Click += async (_, _) =>
        {
            using var dlg = new ColorDialog { FullOpen = true, Color = ParseColor(chosen.Length > 0 ? chosen : preset.Length > 0 ? preset : "#808080") };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            await SaveOption(new JsonObject { ["couleurPeau"] = $"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}" });
        };
        var row = new HRow { Height = 34 };
        row.Controls.Add(free);
        if (chosen.Length > 0)
        {
            var reset = new FlatButton(L.T("Revenir à la couleur du preset"));
            reset.Click += async (_, _) => await SaveOption(new JsonObject { ["couleurPeau"] = null });
            row.Controls.Add(reset);
        }
        card.Controls.Add(row);
        card.Controls.Add(Muted(L.T("Appliquée au prochain pack construit, au corps et au visage. Le maquillage du visage est recoloré avec la peau.")));
    }

    static Color ParseColor(string hex)
    {
        try { return ColorTranslator.FromHtml(hex.StartsWith('#') ? hex : "#" + hex); } catch (Exception) { return Color.Gray; }
    }

    // ------------------------------------------------------------------ preset OBody

    void BodyPresetBlock(VStack card)
    {
        var b = bodyPresets!;
        card.Controls.Add(H2(L.T("Corps (preset OBody)")));
        if (Str(b, "erreur") is { Length: > 0 } err) { card.Controls.Add(new TextLabel { Text = err, ForeColor = Theme.Err }); return; }
        card.Controls.Add(Muted(L.T("Sans choix, OBody NG donne en jeu un preset au hasard. Le preset choisi est attribué au follower à son premier chargement, sans modifier ta config OBody ; tu peux toujours le changer ensuite dans le menu d'OBody.")));
        if (!Bool(b, "obody"))
            card.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Warn, Text = L.T("OBody NG n'est pas actif : ce choix n'aura pas d'effet en jeu.") });
        var all = (b["presets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var chosen = Str(b, "choix");
        // rangement par corps (UBE, CBBE / 3BA…) : par défaut celui du follower, s'il a des presets
        var own = Str(b, "familleFollower");
        string[] order = ["UBE", "CBBE", "UNP", "HIMBO", "AUTRE"];
        var counts = all.GroupBy(p => Str(p, "famille")).ToDictionary(g => g.Key, g => g.Count());
        var families = order.Where(counts.ContainsKey).ToList();
        string FamilyName(string f) => f switch { "CBBE" => "CBBE / 3BA", "UNP" => "UNP / BHUNP", "AUTRE" => L.T("Autres"), _ => f };
        var family = bodyFamilyFor == current && bodyFamily != null ? bodyFamily : (own.Length > 0 && counts.ContainsKey(own) ? own : "");
        if (family.Length > 0 && !counts.ContainsKey(family)) family = "";
        var famPick = new Picker { Width = 230 };
        famPick.SetItems(["", .. families],
            [L.T("Tous les corps ({0})", all.Count), .. families.Select(f => f == own
                ? L.T("{0} ({1}) — corps du follower", FamilyName(f), counts[f]) : FamilyName(f) + " (" + counts[f] + ")")]);
        famPick.Index = Math.Max(0, famPick.Items.IndexOf(family));
        var filter = new Field(L.T("Filtrer…")) { Width = 160 };
        filter.Box.Text = bodyFilter;
        var pick = new Picker();
        void Fill()
        {
            var q = bodyFilter;
            var shown = all.Where(p => Str(p, "nom") == chosen
                || (family.Length == 0 || Str(p, "famille") == family) && (q.Length == 0 || (Str(p, "nom") + " " + Str(p, "set")).Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            pick.SetItems(["", .. shown.Select(p => Str(p, "nom"))],
                [L.T("Au hasard (OBody, par défaut)"), .. shown.Select(p => Str(p, "nom") + (Str(p, "set") is { Length: > 0 } st ? " — " + st : ""))]);
            pick.Index = Math.Max(0, pick.Items.IndexOf(chosen));
        }
        Fill();
        pick.Changed += async (_, _) =>
        {
            if ((pick.Value ?? "") == chosen) return;
            var id = current;
            if (id == null) return;
            Say(L.T("Enregistrement…"));
            try { opts = (await Api("api/options/" + id, new JsonObject { ["corps"] = pick.Value is { Length: > 0 } v ? v : null })).AsObject(); }
            catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
            try { bodyPresets = (await Api("api/bodypresets/" + id)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { }
            try { coherence = (await Api("api/coherence/" + id)).AsArray(); } catch (Exception e) when (e is not OutOfMemoryException) { }
            Say(Str(info, "preset"));
            if (id != current) return;
            Detail(keepScroll: true);
            // aperçu : corps entier avec le nouveau preset
            if (!bodyOn.Checked) bodyOn.Checked = true; else LoadHead(keepView: true);
        };
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) => { timer.Stop(); bodyFilter = filter.Box.Text.Trim(); Fill(); };
        filter.Box.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
        famPick.Changed += (_, _) => { if ((famPick.Value ?? "") == family) return; family = bodyFamily = famPick.Value ?? ""; bodyFamilyFor = current; Fill(); };
        if (families.Count > 1) card.Controls.Add(new FixedStartRow(famPick, new Panel { BackColor = Theme.Card }));
        var row = new FixedStartRow(filter, pick);
        row.Disposed += (_, _) => timer.Dispose();
        card.Controls.Add(row);
        card.Controls.Add(Muted(L.T("{0} presets compatibles (sexe et corps du follower), dans CalienteTools\\BodySlide\\SliderPresets.", all.Count)
            + (families.Count > 1 ? "\n" + L.T("Rangés par corps d'après leur set BodySlide. En jeu, OBody NG ne fait pas ce tri.") : "")));
        if (Bool(b, "choixInconnu"))
            card.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Err, Text = L.T("Le preset choisi n'est plus trouvé : OBody en choisira un au hasard.") });
    }
}

/// <summary>Un contrôle de largeur fixe, suivi d'un autre qui prend le reste (filtre + liste).</summary>
sealed class FixedStartRow : Panel
{
    readonly Control start, main;
    public FixedStartRow(Control start, Control main)
    {
        this.start = start; this.main = main;
        Height = 34; BackColor = Theme.Card;
        Controls.Add(start); Controls.Add(main);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (main == null) return;
        int sw = start.Width;
        start.SetBounds(0, 0, sw, Height);
        main.SetBounds(sw + 6, 0, Math.Max(50, Width - sw - 6), Height);
    }
}

/// <summary>Vignettes de textures (skin), chargées en arrière-plan.</summary>
sealed class ThumbRow : Panel
{
    public ThumbRow() { Height = 172; BackColor = Theme.Card; }
    public void Add(HttpClient http, string url, string caption)
    {
        var t = new Thumb(caption);
        Controls.Add(t);
        t.SetBounds((Controls.Count - 1) * 160, 0, 150, 172);
        _ = t.Load(http, url);
    }
}

sealed class Thumb : Painted
{
    readonly string caption;
    Bitmap? img;
    public Thumb(string caption) { this.caption = caption; Font = Theme.Small; }
    public async Task Load(HttpClient http, string url)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            using var b = new Bitmap(ms);
            img = new Bitmap(b, 150, 150);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { img = null; }
        if (!IsDisposed) Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var r = new Rectangle(0, 0, 150, 150);
        Theme.Box(e.Graphics, r, Color.FromArgb(34, 34, 34), Theme.Line, 6);
        if (img != null) e.Graphics.DrawImage(img, r);
        TextRenderer.DrawText(e.Graphics, caption, Font, new Rectangle(0, 152, 150, 20), Theme.Muted, TextFormatFlags.HorizontalCenter);
    }
    protected override void Dispose(bool disposing) { if (disposing) img?.Dispose(); base.Dispose(disposing); }
}

/// <summary>Palette de couleurs (pastilles) ; la couleur choisie est entourée.</summary>
sealed class Swatches : Painted
{
    readonly List<(string Color, string Name)> items;
    readonly string chosen;
    readonly List<Rectangle> rects = [];
    readonly ToolTip tip = new();
    int hover = -1;
    public event Action<string>? Picked;
    public Swatches(List<(string, string)> items, string chosen)
    {
        this.items = items; this.chosen = chosen;
        Height = 30; Cursor = Cursors.Hand;
    }
    int Arrange(int width)
    {
        rects.Clear();
        int x = 0, y = 0;
        foreach (var _ in items)
        {
            if (x > 0 && x + 26 > width) { x = 0; y += 30; }
            rects.Add(new Rectangle(x, y, 26, 26));
            x += 30;
        }
        return y + 30;
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        int h = Arrange(Math.Max(60, Width));
        if (h != Height) Height = h;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (rects.Count != items.Count) Arrange(Math.Max(60, Width));
        for (int i = 0; i < items.Count; i++)
        {
            bool sel = chosen.Length > 0 && string.Equals(items[i].Color, chosen, StringComparison.OrdinalIgnoreCase);
            Theme.Box(e.Graphics, rects[i], BuilderColor(items[i].Color), sel ? Theme.Accent : i == hover ? Theme.Text : Theme.FieldLine, 5);
            if (sel) using (var p = new Pen(Theme.Accent, 2)) e.Graphics.DrawRectangle(p, Rectangle.Inflate(rects[i], -1, -1));
        }
    }
    static Color BuilderColor(string hex) { try { return ColorTranslator.FromHtml(hex); } catch (Exception) { return Color.Gray; } }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = rects.FindIndex(r => r.Contains(e.Location));
        if (h == hover) return;
        hover = h; Invalidate();
        tip.SetToolTip(this, h >= 0 ? (items[h].Name.Length > 0 ? items[h].Name + " (" + items[h].Color + ")" : items[h].Color) : "");
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = rects.FindIndex(r => r.Contains(e.Location));
        if (i >= 0) Picked?.Invoke(items[i].Color);
    }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
}
