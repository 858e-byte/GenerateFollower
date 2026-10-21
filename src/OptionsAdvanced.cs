using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Réglages avancés d'un follower : niveau, statut (protégé, essentiel, mortel), comportement (agressivité, courage,
/// moralité, assistance) et taille. Défauts du build (inchangés sans choix) : niveau du joueur ×1 (1 à 81), protégé,
/// non agressif, courageux, moralité « n'importe quel crime » (défaut du format), aide les alliés, taille 1.
/// L'agressivité est limitée à « non agressif » / « agressif » : au-delà, le PNJ attaquerait des innocents, voire le joueur.
/// </summary>
public static partial class Options
{
    // valeurs acceptées (clé enregistrée → valeur du jeu)
    static readonly Dictionary<string, Aggression> Aggressions = new() { ["Unaggressive"] = Aggression.Unaggressive, ["Aggressive"] = Aggression.Aggressive };
    static readonly Dictionary<string, Confidence> Confidences = Enum.GetValues<Confidence>().ToDictionary(v => v.ToString());
    static readonly Dictionary<string, Responsibility> Moralities = Enum.GetValues<Responsibility>().ToDictionary(v => v.ToString());
    static readonly Dictionary<string, Assistance> Assistances = Enum.GetValues<Assistance>().ToDictionary(v => v.ToString());
    static readonly string[] Statuses = ["protege", "essentiel", "mortel"];
    const float HeightMin = 0.8f, HeightMax = 1.2f, MultMin = 0.25f, MultMax = 3f;

    static JsonObject AdvancedJson() => new()
    {
        ["agressivite"] = Choices(("Unaggressive", L.T("Non agressif (défaut)")), ("Aggressive", L.T("Agressif : attaque les ennemis à vue"))),
        ["courage"] = Choices(("Cowardly", L.T("Lâche")), ("Cautious", L.T("Prudent")), ("Average", L.T("Moyen")),
            ("Brave", L.T("Courageux (défaut)")), ("Foolhardy", L.T("Téméraire"))),
        ["moralite"] = Choices(("AnyCrime", L.T("Tout crime (défaut)")), ("ViolenceAgainstEnemies", L.T("Violence contre les ennemis")),
            ("PropertyCrimeOnly", L.T("Vols seulement")), ("NoCrime", L.T("Aucun crime"))),
        ["assistance"] = Choices(("HelpsNobody", L.T("N'aide personne")), ("HelpsAllies", L.T("Aide les alliés (défaut)")),
            ("HelpsFriendsAndAllies", L.T("Aide amis et alliés"))),
        ["statut"] = Choices(("protege", L.T("Protégé (défaut) : seul le joueur peut le tuer")), ("essentiel", L.T("Essentiel : ne meurt jamais")),
            ("mortel", L.T("Mortel"))),
        ["tailleMin"] = HeightMin, ["tailleMax"] = HeightMax, ["multMin"] = MultMin, ["multMax"] = MultMax,
    };

    static JsonArray Choices(params (string Id, string Name)[] items) =>
        new(items.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id, ["nom"] = i.Name }).ToArray());

    /// <summary>Contrôle et enregistre une option avancée ; valeur vide ou null = défaut (option retirée).</summary>
    static void SaveAdvanced(Context ctx, PresetRow row, string key, JsonNode? value)
    {
        switch (key)
        {
            case "agressivite": case "courage": case "moralite": case "assistance": case "statut":
                var s = value?.GetValue<string>();
                if (string.IsNullOrEmpty(s)) { Set(ctx, row.Id, key, null); return; }
                bool ok = key switch
                {
                    "agressivite" => Aggressions.ContainsKey(s), "courage" => Confidences.ContainsKey(s), "moralite" => Moralities.ContainsKey(s),
                    "assistance" => Assistances.ContainsKey(s), _ => Statuses.Contains(s),
                };
                if (!ok) throw new GfException(L.T("Valeur refusée pour {0} : {1}", key, s));
                Set(ctx, row.Id, key, JsonValue.Create(s));
                return;
            case "taille":
                if (value == null) { Set(ctx, row.Id, key, null); return; }
                var h = (float)value.GetValue<double>();
                if (float.IsNaN(h) || h < HeightMin || h > HeightMax) throw new GfException(L.T("Taille hors limites ({0} à {1}) : {2}", HeightMin, HeightMax, h));
                Set(ctx, row.Id, key, Math.Abs(h - 1f) < 0.0001f ? null : JsonValue.Create(Math.Round(h, 3)));
                return;
            case "niveau":
                if (value is not JsonObject lv) { Set(ctx, row.Id, key, null); return; }
                var mode = lv["mode"]?.GetValue<string>();
                if (mode == "fixe")
                {
                    var n = lv["fixe"]?.GetValue<int>() ?? 0;
                    if (n < 1 || n > 255) throw new GfException(L.T("Niveau fixe hors limites (1 à 255) : {0}", n));
                    Set(ctx, row.Id, key, new JsonObject { ["mode"] = "fixe", ["fixe"] = n });
                }
                else if (mode == "joueur")
                {
                    var mult = (float)(lv["mult"]?.GetValue<double>() ?? 1);
                    var min = lv["min"]?.GetValue<int>() ?? 1;
                    var max = lv["max"]?.GetValue<int>() ?? 81;
                    if (float.IsNaN(mult) || mult < MultMin || mult > MultMax) throw new GfException(L.T("Multiplicateur hors limites ({0} à {1}) : {2}", MultMin, MultMax, mult));
                    if (min < 1 || max > 255 || min > max) throw new GfException(L.T("Niveaux minimum et maximum incohérents : {0} à {1}", min, max));
                    // valeurs du build par défaut : option retirée (ESP identique)
                    Set(ctx, row.Id, key, Math.Abs(mult - 1f) < 0.0001f && min == 1 && max == 81 ? null
                        : new JsonObject { ["mode"] = "joueur", ["mult"] = Math.Round(mult, 2), ["min"] = min, ["max"] = max });
                }
                else throw new GfException(L.T("Valeur refusée pour {0} : {1}", key, mode ?? ""));
                return;
        }
    }

    static void ApplyAdvanced(Npc npc, PresetRow row, JsonObject opts, StringBuilder report)
    {
        var done = new List<string>();
        if (opts["niveau"] is JsonObject lv)
        {
            if (lv["mode"]?.GetValue<string>() == "fixe")
            {
                npc.Configuration.Level = new NpcLevel { Level = (short)lv["fixe"]!.GetValue<int>() };
                done.Add($"niveau fixe {lv["fixe"]}");
            }
            else
            {
                npc.Configuration.Level = new PcLevelMult { LevelMult = (float)lv["mult"]!.GetValue<double>() };
                npc.Configuration.CalcMinLevel = (short)lv["min"]!.GetValue<int>();
                npc.Configuration.CalcMaxLevel = (short)lv["max"]!.GetValue<int>();
                done.Add(string.Format(CultureInfo.InvariantCulture, "niveau joueur x{0} ({1}-{2})", lv["mult"], lv["min"], lv["max"]));
            }
        }
        if (opts["statut"]?.GetValue<string>() is { } st)
        {
            var f = npc.Configuration.Flags & ~(NpcConfiguration.Flag.Essential | NpcConfiguration.Flag.Protected);
            npc.Configuration.Flags = st switch { "essentiel" => f | NpcConfiguration.Flag.Essential, "mortel" => f, _ => f | NpcConfiguration.Flag.Protected };
            done.Add("statut " + st);
        }
        if (opts["agressivite"]?.GetValue<string>() is { } ag && Aggressions.TryGetValue(ag, out var a)) { npc.AIData.Aggression = a; done.Add("agressivité " + ag); }
        if (opts["courage"]?.GetValue<string>() is { } co && Confidences.TryGetValue(co, out var c)) { npc.AIData.Confidence = c; done.Add("courage " + co); }
        if (opts["moralite"]?.GetValue<string>() is { } mo && Moralities.TryGetValue(mo, out var m)) { npc.AIData.Responsibility = m; done.Add("moralité " + mo); }
        if (opts["assistance"]?.GetValue<string>() is { } asg && Assistances.TryGetValue(asg, out var ass)) { npc.AIData.Assistance = ass; done.Add("assistance " + asg); }
        if (opts["taille"] is JsonValue tv)
        {
            npc.Height = (float)tv.GetValue<double>();
            done.Add(string.Format(CultureInfo.InvariantCulture, "taille {0}", npc.Height));
        }
        if (done.Count > 0) report.AppendLine($"OPTION  {row.Id}  {string.Join(", ", done)}");
    }
}
