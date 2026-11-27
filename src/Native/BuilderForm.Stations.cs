using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace GenerateFollower.Native;

/// <summary>
/// Postes de l'atelier dans la fiche native (plan work\diag\natif\PLAN.md, étape 4), comme les postes de la page : une rangée
/// d'onglets, puis le poste ouvert. Les choix passent par l'API locale (/api/options/&lt;id&gt;, même enregistrement que la page :
/// work\options_choice.json ; style et classe : /api/combat, work\combat_choice.json). Fait : Visage (vérifications),
/// Tenue et armes, Combat (archétype, style, classe). Les autres postes renvoient à l'ancienne
/// interface en attendant.
/// </summary>
public sealed partial class BuilderForm
{
    static List<(string Key, string Label)> Stations() =>
    [
        ("visage", L.T("Visage")), ("corps", L.T("Corps et peau")), ("coiffure", L.T("Coiffure")), ("tenue", L.T("Tenue et armes")),
        ("combat", L.T("Combat")), ("magie", L.T("Sorts et atouts")), ("vie", L.T("Voix et vie")), ("finir", L.T("Finition")),
        ("pnj", L.T("Remplacer un PNJ")),
    ];

    string station = "visage";
    JsonObject? opts;          // /api/options/<id> du preset ouvert (choix et catalogues)
    JsonArray? coherence;      // /api/coherence/<id>
    JsonObject? combat;        // /api/combat/<id> (style, classe, listes)
    string? optsLoading;
    string pieceMod = "", pieceQ = "", weaponMod = "", weaponQ = "";
    Action? refreshTexts;      // textes du poste mis à jour sans redessiner la fiche (tenue du jeu, armes du jeu)

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>Suspend (false) ou reprend (true) le dessin d'un contrôle et de ses enfants : la fiche reconstruite apparaît
    /// d'un coup, sans clignoter.</summary>
    static void Redraw(Control c, bool on)
    {
        if (!c.IsHandleCreated) return;
        SendMessage(c.Handle, 0x000B, on ? 1 : 0, IntPtr.Zero);   // WM_SETREDRAW
        if (on) { c.Invalidate(true); c.Update(); }
    }

    Control Tabs()
    {
        var t = new TabStrip(Stations(), station);
        t.Picked += SetStation;
        return t;
    }

    void SetStation(string k)
    {
        if (k == station) return;
        station = k;
        // l'aperçu se cadre sur ce qu'on règle, comme la page : corps entier pour le corps et la tenue, tête pour le visage
        if (k is "corps" or "tenue") bodyOn.Checked = true;
        else if (k is "visage" or "coiffure") bodyOn.Checked = false;
        Detail();
    }

    static TextLabel Muted(string text) => new() { Text = text, ForeColor = Theme.Muted, Font = Theme.Small };
    static TextLabel H2(string text) => new() { Text = text, Font = Theme.H2, ForeColor = Theme.Muted };

    void StationBody(VStack card)
    {
        refreshTexts = null;
        switch (station)
        {
            case "visage": FaceStation(card); break;
            case "tenue":
                if (opts == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); LoadOptions(); }
                else if (Str(opts, "erreur") is { Length: > 0 } err) card.Controls.Add(new TextLabel { Text = err, ForeColor = Theme.Err });
                else OutfitStation(card);
                break;
            case "combat":
                if (opts == null || combat == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); LoadOptions(); }
                else CombatStation(card);
                break;
            case "magie":
                if (opts == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); LoadOptions(); }
                else if (Str(opts, "erreur") is { Length: > 0 } err2) card.Controls.Add(new TextLabel { Text = err2, ForeColor = Theme.Err });
                else MagicStation(card);
                break;
            case "coiffure":
                if (opts == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); LoadOptions(); }
                else HairStation(card);
                break;
            case "finir":
                if (opts == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); LoadOptions(); }
                else FinishStation(card);
                break;
            case "pnj": NpcStation(card); break;
            case "corps":
                if (opts == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); LoadOptions(); }
                else BodyStation(card);
                break;
            case "vie":
                if (opts == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); LoadOptions(); }
                else if (Str(opts, "erreur") is { Length: > 0 } err3) card.Controls.Add(new TextLabel { Text = err3, ForeColor = Theme.Err });
                else LifeStation(card);
                break;
            default:
                card.Controls.Add(Muted(L.T("Ce poste n'est pas encore dans la fenêtre native : « Ouvrir dans l'ancienne interface » le donne.")));
                break;
        }
    }

    async void LoadOptions()
    {
        var id = current;
        if (id == null || optsLoading == id) return;
        optsLoading = id;
        JsonObject o;
        try { o = (await Api("api/options/" + id)).AsObject(); }
        catch (Exception e) when (e is not OutOfMemoryException) { o = new JsonObject { ["erreur"] = e.Message }; }
        JsonArray c = [];
        try { c = (await Api("api/coherence/" + id)).AsArray(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        JsonObject cb;
        try { cb = (await Api("api/combat/" + id)).AsObject(); }
        catch (Exception e) when (e is not OutOfMemoryException) { cb = new JsonObject { ["erreur"] = e.Message }; }
        JsonObject vo;
        try { vo = (await Api("api/voice/" + id)).AsObject(); }
        catch (Exception e) when (e is not OutOfMemoryException) { vo = new JsonObject { ["erreur"] = e.Message }; }
        optsLoading = null;
        if (id != current) return;
        opts = o; coherence = c; combat = cb; voice = vo;
        Detail();
    }

    /// <summary>Enregistre un choix (même route que la page), relit la cohérence, redessine le poste ; tenue changée : corps
    /// rechargé s'il est affiché.</summary>
    async Task SaveOption(JsonObject body, bool rebuild = true)
    {
        var id = current;
        if (id == null) return;
        Say(L.T("Enregistrement…"));
        try { opts = (await Api("api/options/" + id, body)).AsObject(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this);
            try { opts = (await Api("api/options/" + id)).AsObject(); } catch (Exception e2) when (e2 is not OutOfMemoryException) { }
        }
        var before = coherence?.ToJsonString();
        try { coherence = (await Api("api/coherence/" + id)).AsArray(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        if (id != current) return;
        Say(Str(info, "preset"));
        // choix déjà visible dans son contrôle (liste, case) : seuls les textes du poste changent, sauf si la cohérence change
        if (!rebuild && refreshTexts != null && before == coherence?.ToJsonString()) refreshTexts();
        else Detail(keepScroll: true);
        // corps rechargé avec la nouvelle tenue, caméra laissée où elle est
        if ((body.ContainsKey("tenue") || body.ContainsKey("tenueMods")) && bodyOn.Checked) LoadHead(keepView: true);
        // couleur de peau : visage et corps reteints ; coiffure ajoutée : tête rechargée
        // (couleur seule : teinte refaite sur place, bien plus rapide qu'un rechargement)
        else if (body.ContainsKey("coiffure") || (body.ContainsKey("couleurPeau") && !view.Retint(http, current, Str(info, "peau")))) LoadHead(keepView: true);
    }

    // ------------------------------------------------------------------ Visage

    static TextLabel Check(JsonObject c)
    {
        var lvl = Str(c, "niveau");
        var color = lvl switch { "erreur" => Theme.Err, "attention" => Theme.Warn, "ok" => Theme.Ok, _ => Theme.Text };
        var mark = lvl switch { "erreur" => "✖", "attention" => "⚠", "ok" => "✔", _ => "•" };
        return new TextLabel { Text = mark + "  " + Str(c, "texte"), ForeColor = color, Font = Theme.Small };
    }

    // ------------------------------------------------------------------ Combat (archétype, style, classe)

    /// <summary>Liste à choix « groupe · nom » avec une première ligne « par défaut » (les groupes de la page, à plat).</summary>
    static Picker GroupedPicker(List<JsonObject> list, string first, string chosen)
    {
        var p = new Picker();
        p.SetItems(["", .. list.Select(x => Str(x, "id"))], [first, .. list.Select(x => Str(x, "groupe") + " · " + Str(x, "nom"))]);
        p.Index = Math.Max(0, p.Items.IndexOf(chosen ?? ""));
        return p;
    }

    void CombatStation(VStack card)
    {
        if (coherence is { Count: > 0 })
        {
            card.Controls.Add(H2(L.T("Cohérence des choix")));
            foreach (var c in coherence.OfType<JsonObject>()) card.Controls.Add(Check(c));
        }

        // archétype : règle d'un coup style, classe, tenue, armes, sorts et atouts
        if (Str(opts, "erreur").Length == 0)
        {
            var archs = (opts!["archetypes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var cur = archs.FirstOrDefault(a => Str(a, "id") == Str(opts["choix"], "archetype"));
            card.Controls.Add(H2(L.T("Archétype")));
            var pick = GroupedPicker(archs, L.T("Choisir un archétype…"), "");
            var apply = new FlatButton(L.T("Appliquer")) { Width = 120 };
            card.Controls.Add(new FixedEndRow(pick, apply));
            const string keep = "Règle d'un coup style de combat, classe, tenue, armes, sorts et atouts (modifiables ensuite). La voix, le niveau, le comportement, la taille et le mariage ne changent pas.";
            var desc = Muted((cur != null ? L.T("Dernier archétype appliqué : {0}", Str(cur, "nom")) + "\n" : "") + L.T(keep));
            card.Controls.Add(desc);
            pick.Changed += (_, _) =>
            {
                var a = archs.FirstOrDefault(x => Str(x, "id") == pick.Value);
                desc.Text = a != null ? Str(a, "nom") + " : " + Str(a, "desc") + "\n" + Str(a, "contenu") : L.T(keep);
                desc.Parent?.PerformLayout();
            };
            apply.Click += async (_, _) =>
            {
                if (pick.Value is not { Length: > 0 } key) return;
                if (new ConfirmDialog(L.T("Archétype"), L.T("Remplacer le style, la classe, la tenue, les armes, les sorts et les atouts de ce follower ?")).ShowDialog(this) != DialogResult.OK) return;
                await ApplyArchetype(key);
            };
        }

        // style et classe
        card.Controls.Add(H2(L.T("Combat")));
        if (Str(combat, "erreur") is { Length: > 0 } err) { card.Controls.Add(new TextLabel { Text = err, ForeColor = Theme.Err }); return; }
        var styles = (combat!["styles"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var classes = (combat["classes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        card.Controls.Add(Muted(L.T("Style de combat")));
        var ps = GroupedPicker(styles, L.T("Par défaut du jeu (aucun style choisi)"), Str(combat, "style"));
        card.Controls.Add(ps);
        var styleText = Muted("");
        card.Controls.Add(styleText);
        card.Controls.Add(Muted(L.T("Classe")));
        var pc = GroupedPicker(classes, L.T("Par défaut : {0}", Str(combat, "classeDefaut")), Str(combat, "classe"));
        card.Controls.Add(pc);
        var classText = Muted("");
        card.Controls.Add(classText);
        void Texts()
        {
            var s = styles.FirstOrDefault(x => Str(x, "id") == Str(combat, "style"));
            styleText.Text = s != null ? Str(s, "desc") + "\n" + Str(s, "edid") + " · " + Str(s, "source")
                : L.T("Sans style choisi, le jeu applique son style par défaut. Les followers du jeu en ont tous un (Lydia : guerrier une main et bouclier).");
            var c = classes.FirstOrDefault(x => Str(x, "id") == Str(combat, "classe"));
            var cDesc = c == null ? "" : string.Join(" ", new[]
            {
                Str(c, "competences").Length > 0 ? L.T("Compétences : {0}.", Str(c, "competences")) : "",
                Str(c, "desc").Length > 0 ? L.T("Utilisée par {0}", Str(c, "desc")) : "",
            }.Where(x => x.Length > 0));
            classText.Text = (c != null ? cDesc + "\n" + Str(c, "id") + " · " + Str(c, "source") + "\n" : "")
                + L.T("La classe règle les compétences (stats calculées), le style règle le comportement en combat. Aucun sort ni équipement n'est ajouté : un style de mage n'a d'effet que si le follower connaît des sorts.");
            styleText.Parent?.PerformLayout();
        }
        Texts();
        refreshTexts = Texts;
        ps.Changed += async (_, _) => await SaveCombat(ps.Value, pc.Value);
        pc.Changed += async (_, _) => await SaveCombat(ps.Value, pc.Value);
    }

    /// <summary>Style et classe enregistrés (work\combat_choice.json, même route que la page) ; seuls les textes changent,
    /// sauf si la cohérence change.</summary>
    async Task SaveCombat(string? style, string? classe)
    {
        var id = current;
        if (id == null) return;
        Say(L.T("Enregistrement…"));
        try
        {
            combat = (await Api("api/combat/" + id, new JsonObject
            { ["style"] = style is { Length: > 0 } ? style : null, ["classe"] = classe is { Length: > 0 } ? classe : null })).AsObject();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this);
            try { combat = (await Api("api/combat/" + id)).AsObject(); } catch (Exception e2) when (e2 is not OutOfMemoryException) { }
        }
        var before = coherence?.ToJsonString();
        try { coherence = (await Api("api/coherence/" + id)).AsArray(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        if (id != current) return;
        Say(Str(info, "preset"));
        if (refreshTexts != null && before == coherence?.ToJsonString()) refreshTexts();
        else Detail(keepScroll: true);
    }

    /// <summary>Archétype appliqué (style, classe, tenue, armes, sorts, atouts) ; corps rechargé : la tenue change.</summary>
    async Task ApplyArchetype(string key)
    {
        var id = current;
        if (id == null) return;
        Say(L.T("Enregistrement…"));
        try
        {
            opts = (await Api("api/archetype/" + id, new JsonObject { ["archetype"] = key })).AsObject();
            combat = (await Api("api/combat/" + id)).AsObject();
            coherence = (await Api("api/coherence/" + id)).AsArray();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); }
        if (id != current) return;
        Say(Str(info, "preset"));
        Detail(keepScroll: true);
        if (bodyOn.Checked) LoadHead(keepView: true);
    }

    // ------------------------------------------------------------------ Sorts et atouts

    readonly HashSet<string> openFolds = [];   // groupes ouverts (école, arbre), gardés quand la fiche est redessinée
    string spellMod = "", spellQ = "";

    /// <summary>Groupe repliable dont l'état ouvert est retenu par sa clé ; ouvert d'office s'il contient un choix.</summary>
    Fold KeptFold(string key, Func<string> title, bool hasChoice, Func<IEnumerable<Control>> make)
    {
        var f = new Fold(title, hasChoice || openFolds.Contains(key), make);
        f.Toggled += open => { if (open) openFolds.Add(key); else openFolds.Remove(key); };
        return f;
    }

    void CoherenceBlock(VStack card)
    {
        if (coherence is not { Count: > 0 }) return;
        card.Controls.Add(H2(L.T("Cohérence des choix")));
        foreach (var c in coherence.OfType<JsonObject>()) card.Controls.Add(Check(c));
    }

    void ModsWarning(VStack card, List<Action> refreshers)
    {
        var t = new TextLabel { ForeColor = Theme.Warn, Font = Theme.Small };
        void Set()
        {
            var m = RequiredMods();
            t.Text = m.Count > 0 ? "⚠ " + L.T("Le pack nécessitera : {0}. Si tu désinstalles l'un de ces mods, le pack ne se chargera plus.", string.Join(", ", m)) : "";
            t.Visible = m.Count > 0;
        }
        Set();
        refreshers.Add(Set);
        card.Controls.Add(t);
    }

    void MagicStation(VStack card)
    {
        var o = opts!;
        var refreshers = new List<Action>();
        CoherenceBlock(card);
        ModsWarning(card, refreshers);

        // ---- sorts
        var spells = (o["sorts"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var game = spells.Where(s => Str(s, "mod").Length == 0).ToList();
        var modSpells = spells.Where(s => Str(s, "mod").Length > 0).ToList();
        card.Controls.Add(H2(L.T("Sorts")));
        var spellSum = Muted("");
        void SpellSum()
        {
            var ids = Chosen("sorts").ToHashSet();
            spellSum.Text = ids.Count > 0 ? L.T("{0} sort(s) choisi(s) : {1}", ids.Count, string.Join(", ", spells.Where(s => ids.Contains(Str(s, "id"))).Select(s => Str(s, "nom"))))
                : L.T("Aucun sort (par défaut).");
        }
        SpellSum(); refreshers.Add(SpellSum);
        card.Controls.Add(spellSum);
        var chosenS = Chosen("sorts").ToHashSet();
        Toggle SpellBox(JsonObject s, bool rebuild)
        {
            var id = Str(s, "id");
            var src = Str(s, "source");
            var note = (Str(s, "mod").Length > 0 ? Str(s, "ecole") + ", " : "") + Str(s, "niveauNom")
                + (Str(s, "mod").Length == 0 && src != "Skyrim.esm" && src.Length > 0 ? " · " + src : "")
                + (Str(s, "mod").Length > 0 ? " · " + Str(s, "mod") : "");
            var tg = new Toggle(Str(s, "nom") + "  (" + note + ")", Chosen("sorts").Contains(id));
            tg.Changed += async (_, _) =>
            {
                var ids = Chosen("sorts");
                if (tg.Checked) { if (!ids.Contains(id)) ids.Add(id); } else ids.Remove(id);
                await SaveOption(new JsonObject { ["sorts"] = Ids(ids) }, rebuild);
            };
            return tg;
        }
        var folds = new List<Fold>();
        foreach (var g in game.GroupBy(s => Str(s, "ecole")))
        {
            var list = g.ToList();
            string Title() { int n = list.Count(s => Chosen("sorts").Contains(Str(s, "id"))); return g.Key + (n > 0 ? $" ({n})" : ""); }
            var f = KeptFold("sort:" + g.Key, Title, list.Any(s => chosenS.Contains(Str(s, "id"))), () => list.Select(s => (Control)SpellBox(s, false)));
            folds.Add(f); card.Controls.Add(f);
        }
        if (modSpells.Count > 0)
        {
            card.Controls.Add(new TextLabel { Text = L.T("Sorts des mods ({0} sorts, {1} mods)", modSpells.Count, modSpells.Select(s => Str(s, "mod")).Distinct().Count()), Font = Theme.Bold });
            card.Controls.Add(Muted(L.T("Choisir un sort d'un mod rend ce mod obligatoire pour le pack.")));
            foreach (var s in modSpells.Where(s => chosenS.Contains(Str(s, "id"))))
            {
                var id = Str(s, "id");
                card.Controls.Add(new ChosenRow(Str(s, "nom") + "  (" + Str(s, "ecole") + ", " + Str(s, "niveauNom") + " · " + Str(s, "mod") + ")", Theme.Ok, L.T("Retirer"),
                    async () => { var ids = Chosen("sorts"); ids.Remove(id); await SaveOption(new JsonObject { ["sorts"] = Ids(ids) }); }));
            }
            var results = new VStack { Gap = 2, BackColor = Theme.Card };
            void FillSpells()
            {
                List<JsonObject>? hits = null;
                if (spellQ.Length > 0 || spellMod.Length > 0)
                    hits = modSpells.Where(s => ModMatch(Str(s, "mod"), spellMod) && (spellQ.Length == 0 || Str(s, "nom").Contains(spellQ, StringComparison.OrdinalIgnoreCase)
                        || Str(s, "mod").Contains(spellQ, StringComparison.OrdinalIgnoreCase))).ToList();
                FillResults(results, hits, L.T("Choisis un mod ou tape un nom pour voir les sorts."), L.T("Aucun sort trouvé."), s => SpellBox(s, true));
            }
            card.Controls.Add(SearchRow(spellMod, spellQ, L.T("Chercher un sort ou un mod…"), (m, q) => { spellMod = m; spellQ = q; FillSpells(); }));
            card.Controls.Add(results);
            FillSpells();
        }
        card.Controls.Add(Muted(L.T("Sorts appris par les livres du jeu, des DLC et de tes mods. Un follower ne lance que les sorts des écoles où il est doué : choisis aussi une classe de mage et un style de combat magique. Les sorts de mods qui reposent surtout sur des scripts peuvent ne rien faire chez un follower.")));

        // ---- atouts
        var perks = (o["atouts"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var chosenP = Chosen("atouts").ToHashSet();
        card.Controls.Add(H2(L.T("Atouts")));
        var perkSum = Muted("");
        var perkMods = Muted("");
        var perkWarn = new TextLabel { ForeColor = Theme.Warn, Font = Theme.Small, Text = "⚠ " + L.T("SkyPatcher n'est pas installé : les atouts de mods ne seront pas donnés en jeu tant qu'il ne l'est pas.") };
        bool skyPatcher = o["skyPatcher"] is JsonValue sp && sp.TryGetValue<bool>(out var spv) && spv;
        void PerkSum()
        {
            var ids = Chosen("atouts").ToHashSet();
            perkSum.Text = ids.Count > 0 ? L.T("{0} atout(s) choisi(s) : {1}", ids.Count, string.Join(", ", perks.Where(p => ids.Contains(Str(p, "id"))).Select(p => Str(p, "nom"))))
                : L.T("Aucun atout (par défaut).");
            var cm = perks.Where(p => ids.Contains(Str(p, "id")) && Str(p, "mod").Length > 0)
                .SelectMany(p => (Str(p, "modsAtout") is { Length: > 0 } ma ? ma : Str(p, "source")).Split(", ")).Distinct().ToList();
            perkMods.Text = cm.Count > 0 ? L.T("Atouts de mods ({0}) : donnés en jeu par SkyPatcher. Le pack ne dépend pas de ces mods : si tu en désinstalles un, ses atouts sont simplement ignorés.", string.Join(", ", cm)) : "";
            perkMods.Visible = cm.Count > 0;
            perkWarn.Visible = cm.Count > 0 && !skyPatcher;
        }
        PerkSum(); refreshers.Add(PerkSum);
        card.Controls.Add(perkSum);
        Toggle PerkBox(JsonObject p)
        {
            var id = Str(p, "id");
            bool pnj = p["pnj"] is JsonValue pv && pv.TryGetValue<bool>(out var b) && b;
            bool off = !pnj && !Chosen("atouts").Contains(id);
            int rangs = p["rangs"] is JsonValue rv && rv.TryGetValue<int>(out var r) ? r : 1;
            var notes = new[]
            {
                rangs > 1 ? L.T("{0} rangs", rangs) : "",
                Str(p, "mod").Length > 0 ? (Str(p, "modsAtout") is { Length: > 0 } ma ? ma : Str(p, "source")) : "",
                p["horsArbre"] is JsonValue hv && hv.TryGetValue<bool>(out var h) && h ? L.T("hors de l'arbre de {0}", Str(p, "arbreMod")) : "",
                pnj ? "" : L.T("sans effet sur un PNJ"),
            }.Where(x => x.Length > 0).ToList();
            var tg = new Toggle(Str(p, "nom") + (notes.Count > 0 ? "  (" + string.Join(" · ", notes) + ")" : ""), Chosen("atouts").Contains(id)) { Enabled = !off };
            tg.Changed += async (_, _) =>
            {
                var ids = Chosen("atouts");
                if (tg.Checked) { if (!ids.Contains(id)) ids.Add(id); } else ids.Remove(id);
                await SaveOption(new JsonObject { ["atouts"] = Ids(ids) }, rebuild: false);
            };
            return tg;
        }
        foreach (var g in perks.GroupBy(p => Str(p, "competence")))
        {
            var list = g.OrderBy(p => p["horsArbre"] is JsonValue hv && hv.TryGetValue<bool>(out var h) && h ? 1 : 0).ToList();
            string Title() { int n = list.Count(p => Chosen("atouts").Contains(Str(p, "id"))); return g.Key + (n > 0 ? $" ({n})" : ""); }
            var f = KeptFold("atout:" + g.Key, Title, list.Any(p => chosenP.Contains(Str(p, "id"))), () => list.Select(p => (Control)PerkBox(p)));
            folds.Add(f); card.Controls.Add(f);
        }
        card.Controls.Add(perkMods);
        card.Controls.Add(perkWarn);
        var treeMods = perks.Select(p => Str(p, "arbreMod")).Where(x => x.Length > 0).Distinct().ToList();
        card.Controls.Add(Muted(L.T("Atouts des arbres de compétences tels qu'ils sont dans ton jeu") + (treeMods.Count > 0 ? " (" + string.Join(", ", treeMods) + ")" : "") + ". "
            + L.T("Un atout à plusieurs rangs est donné au rang maximal. Les atouts réservés au joueur (forge, crochetage, commerce…) sont grisés : ils n'ont aucun effet sur un follower.")));

        refreshTexts = () =>
        {
            foreach (var r in refreshers) r();
            foreach (var f in folds) f.RefreshTitle();
            spellSum.Parent?.PerformLayout();
        };
    }

    // ------------------------------------------------------------------ Tenue et armes

    List<string> Chosen(string key) => (opts?["choix"]?[key] as JsonArray ?? []).Select(x => x?.ToString()).OfType<string>().ToList();

    void OutfitStation(VStack card)
    {
        var o = opts!;
        var ch = o["choix"] as JsonObject ?? new JsonObject();
        if (coherence is { Count: > 0 })
        {
            card.Controls.Add(H2(L.T("Cohérence des choix")));
            foreach (var c in coherence.OfType<JsonObject>()) card.Controls.Add(Check(c));
        }
        var modsText = new TextLabel { ForeColor = Theme.Warn, Font = Theme.Small };
        void ModsText()
        {
            var m = RequiredMods();
            modsText.Text = m.Count > 0 ? "⚠ " + L.T("Le pack nécessitera : {0}. Si tu désinstalles l'un de ces mods, le pack ne se chargera plus.", string.Join(", ", m)) : "";
            modsText.Visible = m.Count > 0;
        }
        ModsText();
        card.Controls.Add(modsText);
        if (view.Use3D) card.Controls.Add(Muted(L.T("L'aperçu passe en corps entier, avec la tenue choisie.")));

        // tenue du jeu
        card.Controls.Add(H2(L.T("Tenue")));
        var outfits = (o["tenues"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var pick = new Picker();
        pick.SetItems(["", .. outfits.Select(t => Str(t, "id"))],
            [L.T("Par défaut : {0}", Str(o, "tenueDefaut")), .. outfits.Select(t => Str(t, "groupe") + " · " + Str(t, "nom"))]);
        var chosenOutfit = Str(ch, "tenue");
        pick.Index = Math.Max(0, pick.Items.IndexOf(chosenOutfit));
        pick.Changed += async (_, _) => await SaveOption(new JsonObject { ["tenue"] = pick.Value is { Length: > 0 } v ? v : null }, rebuild: false);
        card.Controls.Add(pick);
        string OutfitText()
        {
            var t = outfits.FirstOrDefault(x => Str(x, "id") == Str(opts?["choix"], "tenue"));
            return (t != null ? L.T("Pièces : {0}", Str(t, "pieces")) + "\n" + Str(t, "id") + " · " + Str(t, "source") + "\n" : "")
                + L.T("Tenue portée par défaut. Le follower prend quand même l'équipement que tu lui donnes s'il est meilleur.");
        }
        var outfitText = Muted(OutfitText());
        card.Controls.Add(outfitText);

        // tenue composée de pièces des mods
        var pieces = (o["piecesMods"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (pieces.Count > 0)
        {
            int nMods = pieces.Select(p => Str(p, "mod")).Distinct().Count();
            card.Controls.Add(new TextLabel { Text = L.T("Tenue composée de pièces de tes mods") + " · " + L.T("{0} pièces dans {1} mods", pieces.Count, nMods), Font = Theme.Bold });
            card.Controls.Add(Muted(L.T("Coche une pièce par emplacement (torse, tête, mains, pieds…). La tenue composée remplace la tenue choisie ci-dessus. Le mod de chaque pièce devient obligatoire pour le pack.")));
            var chosenPieces = (o["tenueModsChoix"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (chosenPieces.Count == 0) card.Controls.Add(Muted(L.T("Aucune pièce choisie.")));
            foreach (var p in chosenPieces)
            {
                var id = Str(p, "id");
                bool race = p["race"] is JsonValue rv && (bool)rv;
                card.Controls.Add(new ChosenRow(Str(p, "nom") + "  (" + Str(p, "emplacement") + " · " + Str(p, "mod") + ")"
                    + (race ? "" : "\n⚠ " + L.T("Pas de modèle pour la race de ce follower : invisible en jeu sans patch.")), race ? Theme.Ok : Theme.Warn,
                    L.T("Retirer"), async () => { var ids = Chosen("tenueMods"); ids.Remove(id); await SaveOption(new JsonObject { ["tenueMods"] = Ids(ids) }); }));
            }
            var results = new VStack { Gap = 2, BackColor = Theme.Card };
            card.Controls.Add(SearchRow(pieceMod, pieceQ, L.T("Chercher une pièce…"), (m, q) => { pieceMod = m; pieceQ = q; FillPieces(results); }));
            card.Controls.Add(results);
            FillPieces(results);
        }

        // armes
        var weapons = (o["armes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var chosenW = Chosen("armes").ToHashSet();
        card.Controls.Add(H2(L.T("Armes de départ")));
        string WeaponText()
        {
            var ids = Chosen("armes").ToHashSet();
            return ids.Count > 0
                ? L.T("Dans l'inventaire : {0}", string.Join(", ", weapons.Where(w => ids.Contains(Str(w, "id"))).Select(w => Str(w, "nom"))))
                : L.T("Aucune arme (par défaut) : le follower se bat avec ce que tu lui donnes.");
        }
        var weaponText = Muted(WeaponText());
        card.Controls.Add(weaponText);
        // tenue du jeu, armes du jeu : le choix est déjà visible dans la liste ou la case, seuls ces textes changent
        refreshTexts = () =>
        {
            outfitText.Text = OutfitText(); weaponText.Text = WeaponText(); ModsText();
            outfitText.Parent?.PerformLayout();
        };
        foreach (var g in weapons.Where(w => Str(w, "mod").Length == 0).GroupBy(w => Str(w, "groupe")))
        {
            card.Controls.Add(new TextLabel { Text = g.Key, Font = Theme.Bold, ForeColor = Theme.Text });
            foreach (var w in g) card.Controls.Add(WeaponToggle(w, chosenW, Str(w, "nom"), rebuild: false));
        }
        var modW = weapons.Where(w => Str(w, "mod").Length > 0).ToList();
        if (modW.Count > 0)
        {
            card.Controls.Add(new TextLabel { Text = L.T("Armes des mods ({0} objets, {1} mods)", modW.Count, modW.Select(w => Str(w, "mod")).Distinct().Count()), Font = Theme.Bold });
            card.Controls.Add(Muted(L.T("Seulement les objets de base : ni enchantés, ni variantes, ni objets de quête. Choisir une arme d'un mod rend ce mod obligatoire pour le pack.")));
            foreach (var w in modW.Where(w => chosenW.Contains(Str(w, "id"))))
            {
                var id = Str(w, "id");
                card.Controls.Add(new ChosenRow(Str(w, "nom") + "  (" + Str(w, "groupe") + " · " + Str(w, "mod") + ")", Theme.Ok, L.T("Retirer"),
                    async () => { var ids = Chosen("armes"); ids.Remove(id); await SaveOption(new JsonObject { ["armes"] = Ids(ids) }); }));
            }
            var results = new VStack { Gap = 2, BackColor = Theme.Card };
            card.Controls.Add(SearchRow(weaponMod, weaponQ, L.T("Chercher une arme de mod…"), (m, q) => { weaponMod = m; weaponQ = q; FillWeapons(results, modW); }));
            card.Controls.Add(results);
            FillWeapons(results, modW);
        }
        card.Controls.Add(Muted(L.T("Objets ajoutés à l'inventaire. Le follower équipe la meilleure arme qu'il a ; un style d'archer a besoin d'un arc et de flèches.")));
    }

    static JsonNode? Ids(List<string> ids) => ids.Count > 0 ? new JsonArray(ids.Select(x => (JsonNode)x).ToArray()) : null;

    Toggle WeaponToggle(JsonObject w, HashSet<string> chosen, string label, bool rebuild = true)
    {
        var id = Str(w, "id");
        var tg = new Toggle(label, chosen.Contains(id));
        tg.Changed += async (_, _) =>
        {
            var ids = Chosen("armes");
            if (tg.Checked) { if (!ids.Contains(id)) ids.Add(id); } else ids.Remove(id);
            await SaveOption(new JsonObject { ["armes"] = Ids(ids) }, rebuild);
        };
        return tg;
    }

    /// <summary>Mods dont le pack dépendra à cause des choix (sorts, lieu, armes, pièces d'armure), comme la page.</summary>
    List<string> RequiredMods()
    {
        var o = opts!;
        var mods = new List<string>();
        void Add(string m) { if (m.Length > 0 && !mods.Contains(m)) mods.Add(m); }
        var spells = Chosen("sorts").ToHashSet();
        foreach (var s in (o["sorts"] as JsonArray ?? []).OfType<JsonObject>()) if (spells.Contains(Str(s, "id"))) Add(Str(s, "mod"));
        var place = Str(o["choix"], "lieu");
        foreach (var p in (o["lieux"] as JsonArray ?? []).OfType<JsonObject>()) if (place.Length > 0 && Str(p, "id") == place) Add(Str(p, "mod"));
        var weapons = Chosen("armes").ToHashSet();
        foreach (var w in (o["armes"] as JsonArray ?? []).OfType<JsonObject>()) if (weapons.Contains(Str(w, "id"))) Add(Str(w, "mod"));
        foreach (var p in (o["tenueModsChoix"] as JsonArray ?? []).OfType<JsonObject>()) Add(Str(p, "mod"));
        return mods;
    }

    /// <summary>Filtre de mod + recherche par nom (attente de 250 ms après la frappe).</summary>
    Control SearchRow(string mod, string q, string hint, Action<string, string> changed)
    {
        var row = new HRow { Height = 34 };
        var fMod = new Field(L.T("Mod : tape une partie du nom (vide = tous les mods)"));
        var fQ = new Field(hint);
        fMod.Box.Text = mod; fQ.Box.Text = q;
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) => { timer.Stop(); changed(fMod.Box.Text.Trim(), fQ.Box.Text.Trim()); };
        fMod.Box.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
        fQ.Box.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
        row.Disposed += (_, _) => timer.Dispose();
        row.Controls.Add(fMod); row.Controls.Add(fQ);
        return row;
    }

    const int MaxResults = 80;

    void FillResults(VStack box, List<JsonObject>? hits, string emptyHint, string none, Func<JsonObject, Toggle> make)
    {
        box.SuspendLayout();
        foreach (Control c in box.Controls.Cast<Control>().ToList()) c.Dispose();
        box.Controls.Clear();
        if (hits == null) box.Controls.Add(Muted(emptyHint));
        else
        {
            foreach (var h in hits.Take(MaxResults)) box.Controls.Add(make(h));
            if (hits.Count > MaxResults) box.Controls.Add(Muted(L.T("{0} résultats : précise la recherche.", hits.Count)));
            else if (hits.Count == 0) box.Controls.Add(Muted(none));
        }
        box.ResumeLayout();
        box.PerformLayout();
        box.Parent?.PerformLayout();
    }

    static bool ModMatch(string mod, string filter) => filter.Length == 0 || mod.Contains(filter, StringComparison.OrdinalIgnoreCase);

    void FillPieces(VStack box)
    {
        var all = (opts?["piecesMods"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        List<JsonObject>? hits = null;
        if (pieceQ.Length > 0 || pieceMod.Length > 0)
            hits = all.Where(p => ModMatch(Str(p, "mod"), pieceMod) && (pieceQ.Length == 0 || Str(p, "nom").Contains(pieceQ, StringComparison.OrdinalIgnoreCase)
                || Str(p, "mod").Contains(pieceQ, StringComparison.OrdinalIgnoreCase))).ToList();
        bool oneMod = hits != null && hits.Select(p => Str(p, "mod")).Distinct().Count() == 1;
        var chosen = Chosen("tenueMods").ToHashSet();
        FillResults(box, hits, L.T("Choisis un mod ou tape un nom pour voir les pièces."), L.T("Aucune pièce trouvée."), p =>
        {
            var id = Str(p, "id");
            var tg = new Toggle(Str(p, "nom") + "  (" + Str(p, "emplacement") + ", " + Str(p, "type") + (oneMod ? "" : " · " + Str(p, "mod")) + ")", chosen.Contains(id));
            tg.Changed += async (_, _) =>
            {
                var ids = Chosen("tenueMods");
                if (tg.Checked) { if (!ids.Contains(id)) ids.Add(id); } else ids.Remove(id);
                await SaveOption(new JsonObject { ["tenueMods"] = Ids(ids) });
            };
            return tg;
        });
    }

    void FillWeapons(VStack box, List<JsonObject> modW)
    {
        List<JsonObject>? hits = null;
        if (weaponQ.Length > 0 || weaponMod.Length > 0)
            hits = modW.Where(w => ModMatch(Str(w, "mod"), weaponMod) && (weaponQ.Length == 0 || Str(w, "nom").Contains(weaponQ, StringComparison.OrdinalIgnoreCase)
                || Str(w, "mod").Contains(weaponQ, StringComparison.OrdinalIgnoreCase) || Str(w, "groupe").Contains(weaponQ, StringComparison.OrdinalIgnoreCase))).ToList();
        bool oneMod = hits != null && hits.Select(w => Str(w, "mod")).Distinct().Count() == 1;
        var chosen = Chosen("armes").ToHashSet();
        FillResults(box, hits, L.T("Choisis un mod ou tape un nom pour voir les armes."), L.T("Aucune arme trouvée."),
            w => WeaponToggle(w, chosen, Str(w, "nom") + "  (" + Str(w, "groupe") + (oneMod ? "" : " · " + Str(w, "mod")) + ")"));
    }
}

/// <summary>Onglets des postes, sur plusieurs lignes si la fiche est étroite.</summary>
sealed class TabStrip : Painted
{
    readonly List<(string Key, string Label)> items;
    readonly string selected;
    readonly List<Rectangle> rects = [];
    int hover = -1;
    public event Action<string>? Picked;

    public TabStrip(List<(string, string)> items, string selected)
    {
        this.items = items; this.selected = selected;
        Font = Theme.Small; Height = 30; Cursor = Cursors.Hand;
    }

    int Arrange(int width)
    {
        rects.Clear();
        int x = 0, y = 0;
        foreach (var (_, label) in items)
        {
            int w = TextRenderer.MeasureText(label, Font).Width + 20;
            if (x > 0 && x + w > width) { x = 0; y += 32; }
            rects.Add(new Rectangle(x, y, w, 28));
            x += w + 6;
        }
        return y + 30;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        int h = Arrange(Math.Max(100, Width));
        if (h != Height) Height = h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (rects.Count != items.Count) Arrange(Math.Max(100, Width));
        for (int i = 0; i < items.Count; i++)
        {
            bool on = items[i].Key == selected;
            Theme.Box(e.Graphics, rects[i], on ? Theme.Accent : Theme.Field, on ? Theme.Accent : i == hover ? Theme.Accent : Theme.FieldLine, 7);
            TextRenderer.DrawText(e.Graphics, items[i].Label, on ? Theme.Bold : Font, rects[i], on ? Theme.Bg : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = rects.FindIndex(r => r.Contains(e.Location));
        if (h != hover) { hover = h; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = rects.FindIndex(r => r.Contains(e.Location));
        if (i >= 0) Picked?.Invoke(items[i].Key);
    }
}

/// <summary>Groupe repliable (les &lt;details&gt; de la page) : titre cliquable, contenu créé à la première ouverture.</summary>
sealed class Fold : VStack
{
    readonly FoldHeader header;
    readonly VStack body = new() { Gap = 2, BackColor = Theme.Card };
    readonly Func<IEnumerable<Control>> make;
    bool built;
    public event Action<bool>? Toggled;
    public Fold(Func<string> title, bool open, Func<IEnumerable<Control>> make)
    {
        this.make = make;
        Gap = 2; BackColor = Theme.Card;
        header = new FoldHeader(title) { Open = open };
        header.Click += (_, _) => SetOpen(!header.Open);
        Controls.Add(header); Controls.Add(body);
        body.Visible = false;
        if (open) SetOpen(true, notify: false);
    }
    public void SetOpen(bool open, bool notify = true)
    {
        header.Open = open; header.Invalidate();
        if (open && !built) { built = true; body.SuspendLayout(); foreach (var c in make()) body.Controls.Add(c); body.ResumeLayout(); }
        body.Visible = open;
        if (notify) Toggled?.Invoke(open);
        for (Control? p = this; p != null; p = p.Parent) p.PerformLayout();
    }
    public void RefreshTitle() => header.Invalidate();
}

sealed class FoldHeader : Painted
{
    readonly Func<string> title;
    public bool Open;
    public FoldHeader(Func<string> title) { this.title = title; Height = 26; Cursor = Cursors.Hand; Font = Theme.Bold; }
    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, (Open ? "▾  " : "▸  ") + title(), Font, ClientRectangle, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

/// <summary>Un contrôle qui prend la largeur, suivi d'un autre de largeur fixe (liste + bouton).</summary>
sealed class FixedEndRow : Panel
{
    readonly Control main, end;
    public FixedEndRow(Control main, Control end)
    {
        this.main = main; this.end = end;
        Height = 34; BackColor = Theme.Card;
        Controls.Add(main); Controls.Add(end);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        int ew = end.Width;
        main.SetBounds(0, 0, Math.Max(50, Width - ew - 6), Height);
        end.SetBounds(Width - ew, 0, ew, Height);
    }
}

/// <summary>Question Oui / Annuler (remplace le confirm() de la page).</summary>
sealed class ConfirmDialog : DarkDialog
{
    public ConfirmDialog(string title, string question) : base(title, new Size(560, 220))
    {
        Body.Controls.Add(new TextLabel { Text = question, Dock = DockStyle.Fill });
        AddButton(L.T("Annuler"), DialogResult.Cancel);
        AddButton("OK", DialogResult.OK, true);
    }
}

/// <summary>Élément choisi (pièce d'armure, arme de mod) avec son bouton Retirer.</summary>
sealed class ChosenRow : Panel
{
    readonly TextLabel label;
    readonly FlatButton button;
    public ChosenRow(string text, Color color, string action, Func<Task> onClick, string mark = "✔  ")
    {
        BackColor = Theme.Card;
        label = new TextLabel { Text = mark + text, ForeColor = color, Font = Theme.Small };
        button = new FlatButton(action) { Height = 28 };
        button.Click += async (_, _) => { button.Enabled = false; await onClick(); };
        Controls.Add(label); Controls.Add(button);
        Height = 34;
    }
    public void SetEnabled(bool on) => button.Enabled = on;
    protected override void OnLayout(LayoutEventArgs e)
    {
        int bw = 90;
        int h = Math.Max(30, label.Need(Math.Max(50, Width - bw - 10)));
        label.SetBounds(0, 0, Math.Max(50, Width - bw - 10), h);
        button.SetBounds(Width - bw, 0, bw, 28);
        if (Height != h) Height = h;
    }
}
