using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace GenerateFollower.Native;

/// <summary>
/// Poste « Coiffure » de la fiche native (plan work\diag\natif\PLAN.md, étape 4), comme la page : coiffure ajoutée par le
/// Builder (presets chauves surtout), portée comme une perruque. Le catalogue (des milliers de coiffures) reste sur le
/// serveur : /api/coiffures/&lt;id&gt; donne le nombre par mod, puis les résultats du filtre (?mod=, ?q=). Choix : option
/// « coiffure » (/api/options). L'aperçu 3D pose la coiffure choisie sur la tête (/api/coiffure-mesh, HeadView).
/// </summary>
public sealed partial class BuilderForm
{
    JsonObject? hairCat;          // /api/coiffures/<id> sans filtre (totaux, mods)
    string hairMod = "", hairQ = "";

    async Task LoadHairCat()
    {
        var id = current;
        if (id == null) return;
        JsonObject h;
        try { h = (await Api("api/coiffures/" + id)).AsObject(); } catch (Exception e) when (e is not OutOfMemoryException) { h = new JsonObject { ["erreur"] = e.Message }; }
        if (id != current) return;
        hairCat = h; hairMod = ""; hairQ = "";
        Detail(keepScroll: true);
    }

    void HairStation(VStack card)
    {
        if (hairCat == null) { card.Controls.Add(Muted(L.T("Lecture des coiffures…"))); _ = LoadHairCat(); return; }
        CoherenceBlock(card);
        card.Controls.Add(H2(L.T("Coiffure ajoutée")));
        if (Str(hairCat, "erreur") is { Length: > 0 } err) { card.Controls.Add(new TextLabel { Text = err, ForeColor = Theme.Err }); return; }
        bool bald = Bool(hairCat, "chauve");
        card.Controls.Add(Muted((bald ? L.T("Ce preset est chauve.") + " " : "")
            + L.T("Tu peux lui donner une coiffure de tes mods : {0} coiffures pour sa race, dont {1} perruque(s).", Num(hairCat, "total") ?? 0, Num(hairCat, "perruques") ?? 0)
            + (bald ? "" : "\n" + L.T("Ce preset a déjà une coiffure : celle choisie ici la remplacera."))
            + "\n" + L.T("Elle est portée comme une perruque (un casque la cache) et garde sa physique SMP si elle en a une. L'aperçu 3D la montre sur la tête ; reconstruis le pack pour l'avoir en jeu.")));
        var c = opts?["coiffureChoix"] as JsonObject;
        if (c != null)
        {
            bool unknown = Bool(c, "inconnue");
            card.Controls.Add(new ChosenRow(unknown ? L.T("Coiffure choisie introuvable dans tes mods.")
                : Str(c, "nom") + "  (" + (Str(c, "type") == "perruque" ? L.T("perruque") : L.T("coiffure")) + " · " + Str(c, "mod") + ")",
                unknown ? Theme.Warn : Theme.Ok, L.T("Retirer"), async () => await SaveOption(new JsonObject { ["coiffure"] = null })));
        }
        var results = new VStack { Gap = 2, BackColor = Theme.Card };
        card.Controls.Add(SearchRow(hairMod, hairQ, L.T("Chercher une coiffure…"), async (m, q) => { hairMod = m; hairQ = q; await FillHair(results); }));
        card.Controls.Add(results);
        _ = FillHair(results);
        card.Controls.Add(Muted(L.T("Cheveux à physique (SMP)") + " : " + L.T("Voix et vie") + " › " + L.T("Réglages avancés") + "."));
    }

    int hairAsk;

    async Task FillHair(VStack box)
    {
        var id = current;
        int ask = ++hairAsk;
        if (id == null) return;
        if (hairQ.Length == 0 && hairMod.Length == 0)
        {
            FillResults(box, null, L.T("Choisis un mod ou tape un nom pour voir les coiffures."), "", _ => new Toggle("", false));
            return;
        }
        JsonObject r;
        try { r = (await Api($"api/coiffures/{id}?mod={Uri.EscapeDataString(hairMod)}&q={Uri.EscapeDataString(hairQ)}")).AsObject(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (ask == hairAsk && !box.IsDisposed) FillResults(box, null, e.Message, "", _ => new Toggle("", false));
            return;
        }
        if (ask != hairAsk || id != current || box.IsDisposed) return;
        var hits = (r["resultats"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        bool oneMod = hits.Select(h => Str(h, "mod")).Distinct().Count() == 1;
        var c = opts?["coiffureChoix"];
        FillResults(box, hits, "", L.T("Aucune coiffure trouvée."), h =>
        {
            bool sel = c != null && Str(c, "id") == Str(h, "id") && Str(c, "type") == Str(h, "type");
            var tg = new Toggle(Str(h, "nom") + "  (" + (Str(h, "type") == "perruque" ? L.T("perruque") : L.T("coiffure")) + (oneMod ? "" : " · " + Str(h, "mod")) + ")", sel);
            tg.Changed += async (_, _) =>
            {
                if (!tg.Checked) return;   // choix unique : on retire par « Retirer » (la fiche redessinée recoche)
                await SaveOption(new JsonObject { ["coiffure"] = new JsonObject { ["type"] = Str(h, "type"), ["id"] = Str(h, "id") } });
            };
            return tg;
        });
        int found = (int)(Num(r, "trouves") ?? hits.Count);
        if (found > hits.Count) { box.Controls.Add(Muted(L.T("{0} résultats : précise la recherche.", found))); box.PerformLayout(); box.Parent?.PerformLayout(); }
    }
}
