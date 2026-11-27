using System.Drawing;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace GenerateFollower.Native;

/// <summary>
/// Poste « Voix et vie » de la fiche native (plan work\diag\natif\PLAN.md, étape 4), comme la page : nom en jeu, voix (avec
/// écoute), lieu de départ, mariage, réglages avancés (niveau, comportement, taille, cheveux SMP), statistiques fixes.
/// Routes : /api/rename, /api/voice, /api/voice-play, /api/voice-sample, /api/options.
/// </summary>
public sealed partial class BuilderForm
{
    JsonObject? voice;   // /api/voice/<id>

    static bool Bool(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static double? Num(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    void LifeStation(VStack card)
    {
        var o = opts!;
        var ch = o["choix"] as JsonObject ?? new JsonObject();
        CoherenceBlock(card);
        ModsWarning(card, []);

        // ---- nom en jeu
        card.Controls.Add(H2(L.T("Nom en jeu")));
        var name = new Field(L.T("Nom du follower en jeu"));
        name.Box.Text = Str(info, "nom");
        var rename = new FlatButton(L.T("Nom en jeu")) { Width = 140 };
        card.Controls.Add(new FixedEndRow(name, rename));
        card.Controls.Add(Muted(L.T("Nom affiché en jeu. Le preset (.jslot) et son export ne sont pas modifiés.")));
        async Task Rename()
        {
            var nom = name.Box.Text.Trim();
            if (nom.Length == 0 || nom == Str(info, "nom") || current == null) return;
            var id = current;
            try { await Api("api/rename/" + id, new JsonObject { ["nom"] = nom }); }
            catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this); return; }
            if (presets.OfType<JsonObject>().FirstOrDefault(p => Str(p, "id") == id) is { } lp) { lp["nom"] = nom; list.Invalidate(); }
            await ReloadInfo(id);
        }
        rename.Click += async (_, _) => await Rename();
        name.Box.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Rename(); } };

        VoiceBlock(card);
        PlaceBlock(card, o, ch);
        MarriageBlock(card, o);
        AdvancedBlock(card, o, ch);
        StatsBlock(card, o);
    }

    /// <summary>Fiche relue (verdict, nom, vérifications) puis redessinée ; la liste suit le verdict.</summary>
    async Task ReloadInfo(string id)
    {
        try { info = (await Api("api/preset/" + id)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        if (id != current) return;
        if (presets.OfType<JsonObject>().FirstOrDefault(p => Str(p, "id") == id) is { } lp && info?["verdict"] is JsonObject v)
        { lp["verdict"] = v.DeepClone(); list.Invalidate(); }
        Detail(keepScroll: true);
    }

    // ------------------------------------------------------------------ voix

    static string DialogueLevel(JsonNode? d)
    {
        if (d == null) return "";
        var lvl = Str(d, "niveau");
        var text = lvl switch
        {
            "jeu" => "✔ " + L.T("Répliques de follower du jeu de base (« Suis-moi », « Attends », échange)."),
            "mod" => "⚠ " + L.T("Pas de répliques de follower dans le jeu de base : elles semblent ajoutées chez toi par {0}. Sans ce mod (ou chez un autre joueur), ce follower ne pourrait pas être recruté par le dialogue. À vérifier en jeu.", Str(d, "plugins")),
            _ => "⚠ " + L.T("Aucune réplique de follower pour cette voix, ni dans le jeu ni dans tes mods : impossible de recruter ce follower par le dialogue. Choisis une voix du premier groupe."),
        };
        if (Str(d, "retireePar") is { Length: > 0 } by)
            text += "\n⚠ " + L.T("Voix retirée d'une liste de dialogue de follower du jeu par {0} (le mod la gère sans doute autrement) : à vérifier en jeu.", by);
        return text;
    }

    void VoiceBlock(VStack card)
    {
        card.Controls.Add(H2(L.T("Voix")));
        if (voice == null) { card.Controls.Add(Muted(L.T("Lecture des choix…"))); return; }
        if (Str(voice, "erreur") is { Length: > 0 } err) { card.Controls.Add(new TextLabel { Text = err, ForeColor = Theme.Err }); return; }
        var all = (voice["voixListe"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        // voix du jeu par niveau de dialogue, puis un groupe par pack de voix, puis les voix sans dialogue (ordre de la page)
        var ordered = new List<(JsonObject V, string Group)>();
        List<JsonObject> Game(string lvl) => all.Where(v => Str(v, "mod").Length == 0 && Str(v["dialogue"], "niveau") == lvl).ToList();
        foreach (var v in Game("jeu")) ordered.Add((v, L.T("Dialogue de follower du jeu de base")));
        foreach (var v in Game("mod")) ordered.Add((v, L.T("Dialogue de follower ajouté par un mod")));
        foreach (var m in all.Select(v => Str(v, "mod")).Where(m => m.Length > 0).Distinct())
            foreach (var v in all.Where(v => Str(v, "mod") == m)) ordered.Add((v, L.T("Pack de voix : {0}", m)));
        foreach (var v in Game("aucun")) ordered.Add((v, L.T("Sans dialogue de follower")));
        var def = Str(voice, "defaut");
        var pick = new Picker();
        pick.SetItems(["", .. ordered.Select(x => Str(x.V, "id"))],
            [L.T("Par défaut : {0}", Str(voice, "defautNom") is { Length: > 0 } dn ? $"{dn} ({def})" : def),
             .. ordered.Select(x => x.Group + " · " + Str(x.V, "nom")
                + (Str(x.V, "mod").Length > 0 && Str(x.V["dialogue"], "niveau") == "aucun" ? " — " + L.T("sans dialogue de follower") : ""))]);
        pick.Index = Math.Max(0, pick.Items.IndexOf(Str(voice, "voix")));
        var play = new FlatButton("▶ " + L.T("Écouter")) { Width = 120 };
        card.Controls.Add(new FixedEndRow(pick, play));
        var chosen = all.FirstOrDefault(v => Str(v, "id") == Str(voice, "voix"));
        var shown = chosen ?? all.FirstOrDefault(v => Str(v, "id") == def);
        var rep = chosen != null ? chosen["repliques"] : voice["defautRepliques"];
        var lines = new List<string>();
        if (shown != null) lines.Add(Str(shown, "id") + " · " + Str(shown, "source") + (Str(shown, "exemples") is { Length: > 0 } ex ? " · " + L.T("Utilisée par {0}", ex) : ""));
        if (rep?["total"] is JsonValue tv && tv.TryGetValue<int>(out var total) && total > 0)
        {
            var parts = Str(rep, "detail").Split(", ");
            lines.Add(L.T("{0} répliques enregistrées pour cette voix ({1}).", total, string.Join(", ", parts.Take(6)) + (parts.Length > 6 ? "…" : "")));
        }
        var dl = DialogueLevel(chosen != null ? chosen["dialogue"] : voice["defautDialogue"]);
        card.Controls.Add(Muted(string.Join("\n", lines)));
        if (dl.Length > 0) card.Controls.Add(new TextLabel { Text = dl, Font = Theme.Small, ForeColor = dl.StartsWith('✔') ? Theme.Ok : Theme.Warn });
        play.Click += async (_, _) => await PlayVoice(pick.Value is { Length: > 0 } v ? v : def, play);
        pick.Changed += async (_, _) => await SaveVoice(pick.Value);
    }

    System.Media.SoundPlayer? voicePlayer;

    /// <summary>Une réplique de la voix : jouée par Windows côté serveur (XAudio2), sinon WAV (ffmpeg) joué ici.</summary>
    async Task PlayVoice(string id, FlatButton b)
    {
        b.Enabled = false;
        try
        {
            var played = await Api("api/voice-play?voix=" + Uri.EscapeDataString(id), new JsonObject());
            if (Bool(played, "ok")) return;
            using var res = await http.GetAsync("api/voice-sample?voix=" + Uri.EscapeDataString(id));
            if (!res.IsSuccessStatusCode)
            {
                var t = await res.Content.ReadAsStringAsync();
                string msg;
                try { msg = JsonNode.Parse(t)?["erreur"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}"; } catch (Exception) { msg = $"HTTP {(int)res.StatusCode}"; }
                throw new GfException(msg);
            }
            var bytes = await res.Content.ReadAsByteArrayAsync();
            voicePlayer?.Stop(); voicePlayer?.Dispose();
            voicePlayer = new System.Media.SoundPlayer(new MemoryStream(bytes));
            voicePlayer.Play();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { new MessageDialog(L.T("Écouter"), L.T("Écoute impossible : {0}", e.Message)).ShowDialog(this); }
        finally { b.Enabled = true; }
    }

    /// <summary>Voix enregistrée (work\voice_choice.json) ; le mariage, les vérifications et le verdict en dépendent.</summary>
    async Task SaveVoice(string? v)
    {
        var id = current;
        if (id == null) return;
        Say(L.T("Enregistrement…"));
        try { voice = (await Api("api/voice/" + id, new JsonObject { ["voix"] = v is { Length: > 0 } ? v : null })).AsObject(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            new MessageDialog(L.T("Erreur"), e.Message).ShowDialog(this);
            try { voice = (await Api("api/voice/" + id)).AsObject(); } catch (Exception e2) when (e2 is not OutOfMemoryException) { }
        }
        try { opts = (await Api("api/options/" + id)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        try { coherence = (await Api("api/coherence/" + id)).AsArray(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        Say(Str(info, "preset"));
        await ReloadInfo(id);
    }

    // ------------------------------------------------------------------ lieu, mariage

    void PlaceBlock(VStack card, JsonObject o, JsonObject ch)
    {
        var places = (o["lieux"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var id = Str(ch, "lieu");
        card.Controls.Add(H2(L.T("Lieu de départ")));
        var pick = GroupedPicker(places, L.T("Aucun : apparaît seulement quand tu l'invoques (par défaut)"), id);
        card.Controls.Add(pick);
        if (id.Length > 0 && places.All(p => Str(p, "id") != id))
            card.Controls.Add(new TextLabel { Text = "⚠ " + L.T("Lieu introuvable dans ton jeu : {0}. Le follower sera seulement invocable.", id), ForeColor = Theme.Err, Font = Theme.Small });
        card.Controls.Add(Muted(L.T("Le follower est déjà à cet endroit au début de la partie (ou à l'ajout du mod), près de la porte, et y vit (il mange, s'assoit, discute, dort s'il trouve un lit). Quand tu le renvoies, il y retourne. Aucune cellule du jeu n'est modifiée ; le pouvoir d'invocation marche toujours.")
            + (places.Any(p => Str(p, "mod").Length > 0) ? "\n" + L.T("Les lieux de tes mods sont groupés par mod. « À vérifier » : le mod ne dit pas de quel lieu il s'agit, il peut y avoir des ennemis.") : "")));
        pick.Changed += async (_, _) => await SaveOption(new JsonObject { ["lieu"] = pick.Value is { Length: > 0 } v ? v : null });
    }

    void MarriageBlock(VStack card, JsonObject o)
    {
        var d = o["mariageVoix"];
        card.Controls.Add(H2(L.T("Mariage")));
        var tg = new Toggle(L.T("Épousable"), o["choix"]?["mariage"] is JsonValue mv && mv.TryGetValue<bool>(out var m) && m);
        card.Controls.Add(tg);
        if (d != null)
        {
            var lvl = Str(d, "niveau");
            card.Controls.Add(new TextLabel
            {
                Font = Theme.Small,
                ForeColor = lvl == "jeu" ? Theme.Ok : lvl == "mod" ? Theme.Warn : Theme.Err,
                Text = lvl == "jeu" ? "✔ " + L.T("La voix de ce follower a les répliques de mariage du jeu de base.")
                    : lvl == "mod" ? "⚠ " + L.T("Pas de répliques de mariage pour cette voix dans le jeu de base : elles semblent ajoutées chez toi par {0}. À vérifier en jeu.", Str(d, "plugins"))
                    : "⚠ " + L.T("Aucune réplique de mariage pour cette voix : ce follower ne pourra pas être épousé par le dialogue. Choisis une autre voix (section Voix)."),
            });
            if (Str(d, "retireePar") is { Length: > 0 } by)
                card.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Warn, Text = "⚠ " + L.T("Voix retirée d'une liste de mariage du jeu par {0} : à vérifier en jeu.", by) });
        }
        card.Controls.Add(Muted(L.T("En jeu : porte l'amulette de Mara et parle-lui. Le follower rejoint alors les PNJ épousables du jeu.")));
        tg.Changed += async (_, _) => await SaveOption(new JsonObject { ["mariage"] = tg.Checked ? true : null });
    }

    // ------------------------------------------------------------------ réglages avancés

    void AdvancedBlock(VStack card, JsonObject o, JsonObject ch)
    {
        var av = o["avance"] as JsonObject ?? new JsonObject();
        card.Controls.Add(H2(L.T("Réglages avancés")));
        var lv = ch["niveau"] as JsonObject;
        bool fixedLv = Str(lv, "mode") == "fixe";
        card.Controls.Add(Muted(L.T("Niveau")));
        var mode = new Picker();
        mode.SetItems(["joueur", "fixe"], [L.T("Suit le niveau du joueur"), L.T("Niveau fixe")]);
        mode.Index = fixedLv ? 1 : 0;
        mode.Changed += async (_, _) => await SaveOption(new JsonObject { ["niveau"] = mode.Value == "fixe" ? new JsonObject { ["mode"] = "fixe", ["fixe"] = 10 } : null });
        card.Controls.Add(mode);
        var row = new HRow { Height = 34 };
        if (fixedLv)
        {
            var fx = new NumInput(L.T("Niveau"), Num(lv, "fixe") ?? 10, 1, 255, 0);
            fx.Committed += async v => await SaveOption(new JsonObject { ["niveau"] = new JsonObject { ["mode"] = "fixe", ["fixe"] = (int)v } });
            row.Controls.Add(fx);
        }
        else
        {
            var mult = new NumInput(L.T("Multiplicateur"), Num(lv, "mult") ?? 1, Num(av, "multMin") ?? 0.1, Num(av, "multMax") ?? 10, 2);
            var min = new NumInput(L.T("Min"), Num(lv, "min") ?? 1, 1, 255, 0);
            var max = new NumInput(L.T("Max"), Num(lv, "max") ?? 81, 1, 255, 0);
            async Task Save() => await SaveOption(new JsonObject { ["niveau"] = new JsonObject { ["mode"] = "joueur", ["mult"] = mult.Value, ["min"] = (int)min.Value, ["max"] = (int)max.Value } });
            mult.Committed += async _ => await Save(); min.Committed += async _ => await Save(); max.Committed += async _ => await Save();
            row.Controls.Add(mult); row.Controls.Add(min); row.Controls.Add(max);
        }
        card.Controls.Add(row);
        foreach (var (key, label) in new[] { ("statut", L.T("Statut")), ("agressivite", L.T("Agressivité")), ("courage", L.T("Courage")), ("moralite", L.T("Moralité")), ("assistance", L.T("Assistance")) })
        {
            var items = (av[key] as JsonArray ?? []).OfType<JsonObject>().ToList();
            card.Controls.Add(Muted(label));
            var p = new Picker();
            p.SetItems(["", .. items.Select(x => Str(x, "id"))], [L.T("Par défaut"), .. items.Select(x => Str(x, "nom"))]);
            p.Index = Math.Max(0, p.Items.IndexOf(Str(ch, key)));
            p.Changed += async (_, _) => await SaveOption(new JsonObject { [key] = p.Value is { Length: > 0 } v ? v : null }, rebuild: false);
            card.Controls.Add(p);
        }
        double tMin = Num(av, "tailleMin") ?? 0.5, tMax = Num(av, "tailleMax") ?? 2;
        var height = new NumInput(L.T("Taille ({0} à {1}, 1 = normale)", tMin, tMax), Num(ch, "taille") ?? 1, tMin, tMax, 2);
        height.Committed += async v =>
        {
            await SaveOption(new JsonObject { ["taille"] = v });
            if (bodyOn.Checked) LoadHead(keepView: true);   // taille en jeu du corps entier
        };
        card.Controls.Add(height);
        card.Controls.Add(Muted(L.T("Sans changement, le follower garde les réglages habituels : niveau du joueur (1 à 81), protégé, non agressif, courageux, taille 1.")));
        HairSmpBlock(card, o);
    }

    /// <summary>Cheveux à physique (SMP) : seulement si l'export a des cheveux SMP (comme la page).</summary>
    void HairSmpBlock(VStack card, JsonObject o)
    {
        var s = info?["cheveuxSmp"];
        if (!Bool(s, "smp")) return;
        var c = o["choix"]?["cheveuxSmp"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) ? cb : (bool?)null;
        bool installed = Bool(s, "installe");
        bool on = c ?? installed;
        card.Controls.Add(Muted(L.T("Cheveux à physique (SMP)")));
        var p = new Picker();
        p.SetItems(["", "anime", "rigide"], [L.T("Automatique") + (installed ? "" : " " + L.T("(Faster HDT-SMP non détecté)")), L.T("Animés"), L.T("Rigides")]);
        p.Index = c == true ? 1 : c == false ? 2 : 0;
        p.Changed += async (_, _) => await SaveOption(new JsonObject { ["cheveuxSmp"] = p.Value switch { "anime" => true, "rigide" => false, _ => null } });
        card.Controls.Add(p);
        var wig = Str(s, "perruque");
        var state = !on
            ? (c == false ? L.T("Rigides : ils suivent la tête, sans mouvement (comme avant).") : L.T("Faster HDT-SMP non détecté : cheveux rigides. Choisis « Animés » pour forcer."))
            : wig.Length == 0 ? L.T("Pas de fichier de physique (.xml) trouvé pour cette coiffure : cheveux rigides.")
            : Bool(s, "fabriquee") ? L.T("Animés : perruque fabriquée à partir de la coiffure ({0} + {1} pièce(s)).", wig, Num(s, "pieces") ?? 0)
            : L.T("Animés : perruque de l'auteur du pack ({0}).", wig);
        card.Controls.Add(Muted(state));
        if (on && wig.Length > 0 && !installed)
            card.Controls.Add(new TextLabel { Font = Theme.Small, ForeColor = Theme.Warn, Text = "⚠ " + L.T("Faster HDT-SMP n'est pas détecté : la perruque restera immobile en jeu tant que SMP n'est pas installé.") });
        card.Controls.Add(Muted(L.T("Animés : la coiffure est portée comme une perruque (celle de l'auteur du pack de cheveux si elle est installée, sinon fabriquée à partir de la coiffure). Un casque la cache. Demande Faster HDT-SMP en jeu.")));
    }

    // ------------------------------------------------------------------ statistiques fixes

    static List<(string Group, (string Key, string Label)[] Skills)> StatGroups() =>
    [
        (L.T("Combat"), [("OneHanded", L.T("Une main")), ("TwoHanded", L.T("Deux mains")), ("Archery", L.T("Archerie")), ("Block", L.T("Parade")), ("Smithing", L.T("Forge")), ("HeavyArmor", L.T("Armure lourde"))]),
        (L.T("Discrétion"), [("LightArmor", L.T("Armure légère")), ("Pickpocket", L.T("Vol à la tire")), ("Lockpicking", L.T("Crochetage")), ("Sneak", L.T("Discrétion")), ("Alchemy", L.T("Alchimie")), ("Speech", L.T("Éloquence"))]),
        (L.T("Magie"), [("Alteration", L.T("Altération")), ("Conjuration", L.T("Conjuration")), ("Destruction", L.T("Destruction")), ("Illusion", L.T("Illusion")), ("Restoration", L.T("Guérison")), ("Enchanting", L.T("Enchantement"))]),
    ];

    static JsonObject DefaultsWithoutClass(JsonNode d)
    {
        var o = d.DeepClone().AsObject();
        o.Remove("classe");
        return o;
    }

    void StatsBlock(VStack card, JsonObject o)
    {
        var d = o["statsDefaut"] as JsonObject;
        if (d == null) return;
        var s = o["statsChoix"] as JsonObject;
        card.Controls.Add(H2(L.T("Statistiques fixes")));
        var on = new Toggle(L.T("Statistiques fixes (pas de calcul automatique)"), s != null);
        on.Changed += async (_, _) => await SaveOption(new JsonObject { ["stats"] = on.Checked ? DefaultsWithoutClass(d) : null });
        card.Controls.Add(on);
        if (s == null)
        {
            card.Controls.Add(Muted(L.T("Sans cette case, le jeu calcule les compétences, la santé, la magie et la vigueur d'après la classe et le niveau (Auto-calc), comme avant.")));
            return;
        }
        card.Controls.Add(Muted(L.T("Préremplies avec ce que le jeu calcule pour sa classe ({0}), sa race et son niveau. Le follower ne progressera plus avec le niveau du joueur, et le niveau ci-dessous remplace celui des réglages avancés.", Str(d, "classe"))));
        var inputs = new Dictionary<string, NumInput>();
        JsonObject Form()
        {
            var r = new JsonObject { ["competences"] = new JsonObject() };
            foreach (var (k, n) in inputs)
                if (k.StartsWith("c:")) r["competences"]![k[2..]] = (int)n.Value; else r[k] = (int)n.Value;
            return r;
        }
        async Task Save() => await SaveOption(new JsonObject { ["stats"] = Form() });
        var top = new HRow { Height = 34 };
        foreach (var (k, label, min, max) in new[] { ("niveau", L.T("Niveau"), 1, 255), ("sante", L.T("Santé"), 1, 10000), ("magie", L.T("Magie"), 0, 10000), ("vigueur", L.T("Vigueur"), 0, 10000) })
        {
            var n = new NumInput(label, Num(s, k) ?? 0, min, max, 0);
            n.Committed += async _ => await Save();
            inputs[k] = n; top.Controls.Add(n);
        }
        card.Controls.Add(top);
        foreach (var (g, skills) in StatGroups())
        {
            card.Controls.Add(new TextLabel { Text = g, Font = Theme.Bold });
            foreach (var chunk in skills.Chunk(3))
            {
                var row = new HRow { Height = 34 };
                foreach (var (k, label) in chunk)
                {
                    var n = new NumInput(label, Num(s["competences"], k) ?? 0, 0, 100, 0) { Hint = L.T("Calcul du jeu : {0}", Num(d["competences"], k) ?? 0) };
                    n.Committed += async _ => await Save();
                    inputs["c:" + k] = n; row.Controls.Add(n);
                }
                card.Controls.Add(row);
            }
        }
        var reset = new FlatButton(L.T("Recalculer d'après la classe"));
        reset.Click += async (_, _) =>
        {
            // valeurs du jeu au niveau affiché : le niveau est enregistré d'abord, puis le calcul relu à ce niveau
            await Save();
            if (opts?["statsDefaut"] is JsonObject d2) await SaveOption(new JsonObject { ["stats"] = DefaultsWithoutClass(d2) });
        };
        card.Controls.Add(reset);
    }
}

/// <summary>Champ numérique avec son libellé à gauche ; la valeur est enregistrée à Entrée ou en quittant le champ.</summary>
sealed class NumInput : Panel
{
    readonly TextLabel label;
    readonly Field field = new("");
    readonly double min, max;
    readonly int decimals;
    double value;
    public string? Hint;
    public event Action<double>? Committed;
    public double Value => value;

    public NumInput(string text, double v, double min, double max, int decimals)
    {
        this.min = min; this.max = max; this.decimals = decimals; value = v;
        BackColor = Theme.Card; Height = 34;
        label = new TextLabel { Text = text, ForeColor = Theme.Muted, Font = Theme.Small,
            Flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding };
        field.Box.Text = Format(v);
        field.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); } };
        field.Box.Leave += (_, _) => Commit();
        Controls.Add(label); Controls.Add(field);
        var tip = new ToolTip();
        HandleCreated += (_, _) => { if (Hint != null) { tip.SetToolTip(label, Hint); tip.SetToolTip(field.Box, Hint); } };
        Disposed += (_, _) => tip.Dispose();
    }

    string Format(double v) => v.ToString(decimals == 0 ? "0" : "0.##", CultureInfo.CurrentCulture);

    void Commit()
    {
        var t = field.Box.Text.Trim().Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { field.Box.Text = Format(value); return; }
        v = Math.Clamp(Math.Round(v, decimals), min, max);
        field.Box.Text = Format(v);
        if (Math.Abs(v - value) < 1e-9) return;
        value = v;
        Committed?.Invoke(v);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (label == null) return;   // Height réglé dans le constructeur, avant la création du libellé
        int lw = TextRenderer.MeasureText(label.Text, label.Font).Width;
        int fw = Math.Clamp(Width - lw - 8, 50, 90);
        label.SetBounds(0, 0, Math.Max(10, Width - fw - 6), Height);
        field.SetBounds(Width - fw, 0, fw, Height);
    }
}
