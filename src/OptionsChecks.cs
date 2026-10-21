using System.Text.Json.Nodes;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Cohérence des choix d'un follower (fiche du Builder et rapport de build) : un style d'archer sans arc, un style magique
/// sans sorts ou un mariage avec une voix sans répliques donnent un follower qui « ne fait rien » en jeu. Niveau
/// « attention » (verdict orange) quand le choix ne peut pas marcher tel quel ; « info » quand il marche mal ou dépend d'un mod.
/// Rien n'est contrôlé pour un follower sans choix.
/// </summary>
public static partial class Options
{
    public static List<(string Level, string Text)> Coherence(Context ctx, PresetRow row)
    {
        var res = new List<(string, string)>();
        var opts = For(ctx, row.Id);
        var combat = Combat.LoadChoices(ctx).GetValueOrDefault(row.Id);
        var kinds = (opts["armes"] as JsonArray ?? []).Select(x => x?.GetValue<string>())
            .Select(id => Weapons(ctx).FirstOrDefault(w => w.EditorId == id)?.Kind).Where(k => k != null).ToHashSet();
        var spellIds = (opts["sorts"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").ToList();

        // style de combat ↔ armes et sorts
        if (combat?.Style is { } styleKey && Combat.Profile(ctx, styleKey) is { } prof)
        {
            var styleName = Combat.Styles().First(s => s.Key == styleKey).Name;
            if (Combat.IsRanged(prof) && !kinds.Contains("arc"))
                res.Add(("attention", L.T("Style à distance « {0} » sans arc dans les armes de départ : le follower ne tirera que si tu lui donnes un arc et des flèches.", styleName)));
            if (Combat.IsMagic(prof) && spellIds.Count == 0)
                res.Add(("attention", L.T("Style magique « {0} » sans aucun sort : le follower ne lancera rien. Ajoute des sorts (section Sorts) ou choisis un autre style.", styleName)));
        }
        if (kinds.Contains("arc") && !kinds.Contains("fleches"))
            res.Add(("attention", L.T("Arc sans flèches dans les armes de départ : ajoute des flèches, sinon le follower ne pourra pas tirer.")));

        // sorts ↔ classe : une école que la classe n'entraîne pas reste à un niveau bas (coût en magie élevé)
        if (spellIds.Count > 0)
        {
            var classId = combat?.Classe ?? ctx.Config.FollowerClass;
            var cls = Combat.Classes().FirstOrDefault(c => c.EditorId == classId) is { } cc && ctx.Cache.TryResolve<IClassGetter>(cc.Key, out var g1) ? g1
                : ctx.Find<IClassGetter>(classId);
            if (cls != null)
            {
                var weak = Spells(ctx).Where(s => spellIds.Contains(s.EditorId))
                    .Where(s => Enum.TryParse<Skill>(s.School, out var sk) && (!cls.SkillWeights.TryGetValue(sk, out var w) || w == 0))
                    .Select(s => s.Name).ToList();
                if (weak.Count > 0)
                {
                    // classe telle qu'utilisée en jeu (version gagnante) : signaler le plugin qui la modifie
                    var winner = ctx.Cache.ResolveAllContexts<IClass, IClassGetter>(cls.FormKey).First().ModKey.FileName.String;
                    var modded = Masters.Contains(winner, StringComparer.OrdinalIgnoreCase) ? "" : " " + L.T("(classe modifiée par {0})", winner);
                    res.Add(("info", L.T("Sorts d'écoles que la classe {0} n'entraîne pas : {1}. Leur coût en magie sera élevé : le follower les lancera peu. Une classe de mage convient mieux.", cls.EditorID, string.Join(", ", weak)) + modded));
                }
            }
        }

        // mariage ↔ voix
        if (opts["mariage"]?.GetValue<bool>() == true && Voices.FinalVoice(ctx, row) is { } voice)
        {
            var d = Voices.MarriageOf(ctx, voice);
            if (d.Level == "aucun")
                res.Add(("attention", L.T("Épousable, mais sa voix n'a pas de répliques de mariage : la demande en mariage ne sera pas proposée. Change de voix (section Voix).")));
            else if (d.Level == "mod")
                res.Add(("info", L.T("Épousable : répliques de mariage de sa voix ajoutées par {0} (absentes du jeu de base).", d.Plugins)));
        }
        return res;
    }
}
