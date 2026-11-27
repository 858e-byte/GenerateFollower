using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Statistiques fixes (plan : work\diag\stats\PLAN.md ; demande d'un joueur : pouvoir décocher « Auto-calc stats »).
/// Option « stats » de options_choice.json :
/// { "niveau": 1-255, "sante": 1-10000, "magie": 0-10000, "vigueur": 0-10000, "competences": { "OneHanded": 0-100, … } }
/// (valeurs totales voulues en jeu ; les 18 compétences du jeu, noms de l'énumération Skill de Mutagen). Absente : Auto-calc
/// comme avant (ESP identique). Au build : drapeau AutoCalcStats retiré, niveau fixe, compétences dans DNAM
/// (PlayerSkills) ; santé, magie, vigueur en décalages ACBS (valeur voulue − départ de la race), le jeu ignorant celles de
/// DNAM sans Auto-calc (essai du 18/11). Le préremplissage reprend le calcul du jeu (étape 0 du plan, vérifié sur Skyrim.esm : compétences
/// exactes pour 96 % des PNJ, santé pour 99 %).
/// </summary>
public static partial class Options
{
    public const string StatsOption = "stats";
    public static readonly Skill[] AllSkills = Enum.GetValues<Skill>();
    const int StatMax = 10000;

    /// <summary>Classe du follower au build : celle choisie (Combat), sinon celle de config.json.</summary>
    static IClassGetter? FollowerClass(Context ctx, PresetRow row)
    {
        var edid = Combat.LoadChoices(ctx).GetValueOrDefault(row.Id)?.Classe ?? ctx.Config.FollowerClass;
        return ctx.Find<IClassGetter>(edid);
    }

    /// <summary>
    /// Ce que le jeu calculerait (Auto-calc) pour ce follower au niveau donné, d'après sa classe et sa race :
    /// compétence = min(100, 15 + bonus de race + arrondi((niveau − 1) × 8 × poids / somme des poids)) ;
    /// santé = départ de la race + (niveau − 1) × (5 + 10 × poids santé / total des poids d'attributs) ;
    /// magie, vigueur = départ + (niveau − 1) × 10 × poids / total.
    /// </summary>
    public static JsonObject StatsDefaults(Context ctx, PresetRow row, int level)
    {
        level = Math.Clamp(level, 1, 255);
        var cls = FollowerClass(ctx, row);
        var race = ctx.Find<IRaceGetter>(row.Race);
        var boosts = new Dictionary<Skill, int>();
        if (race != null)
            foreach (var b in new[] { race.SkillBoost0, race.SkillBoost1, race.SkillBoost2, race.SkillBoost3, race.SkillBoost4, race.SkillBoost5, race.SkillBoost6 })
                // bonus de race en ActorValue (« OneHanded »…) : même nom que la compétence ; 255 = emplacement vide
                if (b?.Skill is { } av && Enum.TryParse<Skill>(av.ToString(), out var sk)) boosts[sk] = boosts.GetValueOrDefault(sk) + b.Boost;
        double W = cls == null ? 0 : AllSkills.Sum(s => cls.SkillWeights.TryGetValue(s, out var w) ? w : 0);
        var skills = new JsonObject();
        foreach (var s in AllSkills)
        {
            double w = cls != null && cls.SkillWeights.TryGetValue(s, out var x) ? x : 0;
            skills[s.ToString()] = Math.Min(100, 15 + boosts.GetValueOrDefault(s) + (W > 0 ? (int)Math.Round((level - 1) * 8 * w / W, MidpointRounding.AwayFromZero) : 0));
        }
        double Stat(BasicStat s) => cls != null && cls.StatWeights.TryGetValue(s, out var v) ? v : 0;
        double tot = Stat(BasicStat.Health) + Stat(BasicStat.Magicka) + Stat(BasicStat.Stamina);
        double Start(BasicStat s) => race != null && race.Starting.TryGetValue(s, out var v) ? v : 50;
        double Share(BasicStat s) => tot > 0 ? Stat(s) / tot : 1 / 3.0;
        return new JsonObject
        {
            ["niveau"] = level,
            ["sante"] = (int)Math.Round(Start(BasicStat.Health) + (level - 1) * (5 + 10 * Share(BasicStat.Health))),
            ["magie"] = (int)Math.Round(Start(BasicStat.Magicka) + (level - 1) * 10 * Share(BasicStat.Magicka)),
            ["vigueur"] = (int)Math.Round(Start(BasicStat.Stamina) + (level - 1) * 10 * Share(BasicStat.Stamina)),
            ["competences"] = skills,
            ["classe"] = cls?.EditorID,
        };
    }

    /// <summary>Niveau de départ du préremplissage : celui des stats choisies, sinon le niveau fixe, sinon 20.</summary>
    static int StatsLevel(JsonObject opts) =>
        opts[StatsOption]?["niveau"] is JsonValue v && v.TryGetValue<int>(out var n) ? n
        : opts["niveau"] is JsonObject lv && lv["mode"]?.GetValue<string>() == "fixe" ? lv["fixe"]!.GetValue<int>() : 20;

    static JsonNode? StatsDefaultsJson(Context ctx, PresetRow row, JsonObject opts)
    {
        try { return StatsDefaults(ctx, row, StatsLevel(opts)); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    static void SaveStats(Context ctx, PresetRow row, JsonNode? value)
    {
        if (value == null) { Set(ctx, row.Id, StatsOption, null); return; }
        if (value is not JsonObject o) throw new GfException(L.T("Valeur refusée pour {0} : {1}", StatsOption, value.ToJsonString()));
        int Int(JsonNode? n, string what, int min, int max)
        {
            if (n is not JsonValue v || !v.TryGetValue<double>(out var d) || double.IsNaN(d)) throw new GfException(L.T("Valeur refusée pour {0} : {1}", what, n?.ToJsonString() ?? "null"));
            var i = (int)Math.Round(d);
            if (i < min || i > max) throw new GfException(L.T("{0} hors limites ({1} à {2}) : {3}", what, min, max, i));
            return i;
        }
        var clean = new JsonObject
        {
            ["niveau"] = Int(o["niveau"], L.T("Niveau"), 1, 255),
            ["sante"] = Int(o["sante"], L.T("Santé"), 1, StatMax),
            ["magie"] = Int(o["magie"], L.T("Magie"), 0, StatMax),
            ["vigueur"] = Int(o["vigueur"], L.T("Vigueur"), 0, StatMax),
        };
        var sk = o["competences"] as JsonObject ?? throw new GfException(L.T("Valeur refusée pour {0} : {1}", "competences", "null"));
        if (sk.Select(kv => kv.Key).FirstOrDefault(k => !Enum.TryParse<Skill>(k, out _)) is { } bad) throw new GfException(L.T("Compétence inconnue : {0}", bad));
        var skills = new JsonObject();
        foreach (var s in AllSkills) skills[s.ToString()] = Int(sk[s.ToString()], s.ToString(), 0, 100);
        clean["competences"] = skills;
        Set(ctx, row.Id, StatsOption, clean);
    }

    /// <summary>Au build (après les réglages avancés, dont il remplace le niveau) : statistiques fixes.</summary>
    static void ApplyStats(Context ctx, Npc npc, PresetRow row, JsonObject opts, StringBuilder report)
    {
        if (opts[StatsOption] is not JsonObject o || o["competences"] is not JsonObject sk) return;
        var c = npc.Configuration;
        c.Flags &= ~NpcConfiguration.Flag.AutoCalcStats;
        c.Level = new NpcLevel { Level = (short)o["niveau"]!.GetValue<int>() };
        c.CalcMinLevel = 0; c.CalcMaxLevel = 0;
        // Santé, magie, vigueur : sans Auto-calc, le jeu prend le départ de la race + le décalage ACBS (essai en jeu du
        // 18/11 : DNAM seul laissait 50 / 50 / 50, les compétences de DNAM, elles, sont bien lues). Décalage = voulu − départ.
        var race = ctx.Find<IRaceGetter>(row.Race);
        double Start(BasicStat s) => race != null && race.Starting.TryGetValue(s, out var v) ? v : 50;
        short Off(string key, BasicStat s) => (short)Math.Clamp(o[key]!.GetValue<int>() - (int)Math.Round(Start(s)), short.MinValue, short.MaxValue);
        c.HealthOffset = Off("sante", BasicStat.Health); c.MagickaOffset = Off("magie", BasicStat.Magicka); c.StaminaOffset = Off("vigueur", BasicStat.Stamina);
        var ps = new PlayerSkills
        {
            Health = (ushort)o["sante"]!.GetValue<int>(), Magicka = (ushort)o["magie"]!.GetValue<int>(), Stamina = (ushort)o["vigueur"]!.GetValue<int>(),
        };
        foreach (var s in AllSkills)
        {
            ps.SkillValues[s] = (byte)Math.Clamp(sk[s.ToString()]?.GetValue<int>() ?? 15, 0, 100);
            ps.SkillOffsets[s] = 0;
        }
        npc.PlayerSkills = ps;
        var top = AllSkills.OrderByDescending(s => ps.SkillValues[s]).Take(3).Select(s => $"{s} {ps.SkillValues[s]}");
        report.AppendLine(L.T("STATS   {0}  statistiques fixes : niveau {1}, santé {2}, magie {3}, vigueur {4} (décalages {5} / {6} / {7}) ; {8}",
            row.Id, o["niveau"], ps.Health, ps.Magicka, ps.Stamina, c.HealthOffset, c.MagickaOffset, c.StaminaOffset, string.Join(", ", top)));
    }
}
