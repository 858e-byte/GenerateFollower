using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Style de combat (CSTY, champ ZNAM du PNJ) et classe (CLAS) choisis dans le Builder pour chaque follower.
/// Choix : work\combat_choice.json { "id du preset": { "style": clé, "classe": EditorID } } ; sans choix, le PNJ reste
/// tel que le build l'a toujours fait (classe de config.json, aucun style : le jeu applique son style par défaut).
/// Styles du jeu : enregistrements de Skyrim.esm et des DLC, référencés par FormKey (aucune dépendance de plus).
/// Styles « inspirés d'ESO » : copies d'un style du jeu aux réglages modifiés, créées dans le plugin du follower
/// seulement quand un follower les utilise (GF_CS_&lt;clé&gt;). En Skyrim, la classe n'a pas de style de combat :
/// elle règle les compétences (stats calculées), le style règle le comportement (armes préférées, attaque, défense).
/// Ni l'un ni l'autre n'ajoute de sorts ou d'équipement.
/// </summary>
public static class Combat
{
    static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm"), Dawnguard = ModKey.FromFileName("Dawnguard.esm"),
        Dragonborn = ModKey.FromFileName("Dragonborn.esm");

    /// <summary>Style proposé. Vanilla : FormKey du jeu. ESO : Base = style du jeu copié, puis multiplicateurs.</summary>
    public sealed record Style(string Key, string Group, string Name, string Desc, FormKey? Vanilla = null, Tuning? Eso = null);

    /// <summary>Réglages d'un style personnalisé (multiplicateurs d'équipement 0-10, attaque/défense 0-1).</summary>
    public sealed record Tuning(FormKey Base, float Melee, float Magic, float Ranged, float Shout, float Staff, float Unarmed,
        float Offense, float Defense, bool Flanking = false, bool DualWield = false);

    public sealed record ClassChoice(string EditorId, string Group, string Name, string Desc, FormKey Key);

    static FormKey Sk(uint id) => new(Skyrim, id);

    /// <summary>Styles proposés, dans l'ordre affiché (textes traduits à chaque appel : L.Init peut changer de langue).</summary>
    public static List<Style> Styles() =>
    [
        new("csHumanBoss1H", L.T("Mêlée"), L.T("Guerrier, une main et bouclier"), L.T("Le style de Lydia et des housecarls : équilibré, garde haute."), Sk(0x03DECE)),
        new("csHumanBoss2H", L.T("Mêlée"), L.T("Guerrier, arme à deux mains"), L.T("Le style de Mjoll, Uthgerd et Stenvar : très défensif, frappe fort."), Sk(0x03DECF)),
        new("csHumanMeleeLvl3", L.T("Mêlée"), L.T("Combattant équilibré"), L.T("Mêlée simple, attaque et défense moyennes (Cosnach)."), Sk(0x068849)),
        new("csHumanMeleeLvl4", L.T("Mêlée"), L.T("Combattant aguerri"), L.T("Mêlée plus offensive et plus défensive (Borgakh)."), Sk(0x068847)),
        new("csHumanMeleeLvl7", L.T("Mêlée"), L.T("Combattant d'élite"), L.T("Le plus offensif et le plus défensif des styles de mêlée génériques."), Sk(0x0E6C50)),
        new("csHumanBerserkerLvl1", L.T("Mêlée"), L.T("Berserker"), L.T("Attaque beaucoup, se protège peu."), Sk(0x03D2C8)),
        new("csForswornBerserkerLow", L.T("Mêlée"), L.T("Berserker à deux armes"), L.T("Offensif, autorisé à manier deux armes (Jenassa)."), Sk(0x10F546)),
        new("csThalmorMeleeDual", L.T("Mêlée"), L.T("Duelliste à deux armes"), L.T("Le style des Thalmor : offensif et défensif, deux armes autorisées."), Sk(0x0F960C)),
        new("csDelphine", L.T("Mêlée"), L.T("Lame"), L.T("Le style de Delphine : offensif, deux armes autorisées, peu de magie."), Sk(0x10D4BE)),
        new("DLC2FreaCombatStyle", L.T("Mêlée"), L.T("Frea (Skaal)"), L.T("Très offensive, préfère nettement la mêlée, deux armes autorisées."), new FormKey(Dragonborn, 0x02BF15)),
        new("csHumanTankLvl1", L.T("Mêlée"), L.T("Tank prudent"), L.T("Peu agressif : attaque et défense basses."), Sk(0x03CF5A)),
        new("csAlftandHumanTank", L.T("Mêlée"), L.T("Tank à bouclier"), L.T("Très défensif, évite l'arc."), Sk(0x105949)),
        new("csMercerFreyMelee", L.T("Mêlée"), L.T("Assassin flanqueur"), L.T("Le style de Mercer Frey : contourne l'ennemi, deux armes autorisées."), Sk(0x103938)),
        new("csTGBrynjolf", L.T("Mêlée"), L.T("Voleur de la Guilde"), L.T("Le style de Brynjolf : préfère la mêlée, très défensif."), Sk(0x1019AC)),
        new("csWEBrawler", L.T("Mêlée"), L.T("Bagarreur (mains nues)"), L.T("Se bat à mains nues, très offensif."), Sk(0x10555D)),

        new("csHumanMissile", L.T("Distance"), L.T("Archer"), L.T("Le style d'Aela et de Faendal : préfère l'arc, garde ses distances."), Sk(0x03BE1D)),
        new("csCWSoldierMissile", L.T("Distance"), L.T("Archer pur"), L.T("Le style des archers de la guerre civile : l'arc presque toujours."), Sk(0x0D3A0E)),
        new("csMercerFreyRanged", L.T("Distance"), L.T("Archer flanqueur"), L.T("Le style de Mercer Frey à l'arc : contourne et tire."), Sk(0x08F1E6)),
        new("DLC1LD_csKatria", L.T("Distance"), L.T("Tireuse d'élite (Katria)"), L.T("Préfère nettement l'arc, deux armes autorisées en mêlée."), new FormKey(Dawnguard, 0x004D4D)),

        new("csHumanMagic", L.T("Magie"), L.T("Mage de combat"), L.T("Le style de Brelyna et Belrand : sorts d'abord, mêlée au besoin."), Sk(0x03BE1C)),
        new("csWERangedConjurer", L.T("Magie"), L.T("Conjurateur"), L.T("Presque uniquement des sorts, reste à distance."), Sk(0x10554E)),
        new("csWarlock", L.T("Magie"), L.T("Sorcier à distance"), L.T("Sorts et distance, évite la mêlée."), Sk(0x044CD0)),
        new("csPenitusOculatusBattlemage", L.T("Magie"), L.T("Mage de bataille"), L.T("Sorts et mêlée, défensif."), Sk(0x04C2AA)),
        new("csSpellsword", L.T("Magie"), L.T("Lame-sort"), L.T("Préfère nettement la mêlée, lance des sorts entre deux coups."), Sk(0x107812)),
        new("DLC1csSerana", L.T("Magie"), L.T("Serana"), L.T("Le style de Serana : la magie avant tout, des cris si elle en a."), new FormKey(Dawnguard, 0x011F34)),
        new("csVampireMagic", L.T("Magie"), L.T("Mage vampire"), L.T("Magie et bâtons, peu de mêlée."), Sk(0x02DFB5)),

        new("csGreybeard", L.T("Cris"), L.T("Grise-Barbe"), L.T("Préfère les cris (s'il en connaît)."), Sk(0x10F72C)),
        new("dlc2DBAncientDragonbornCS", L.T("Cris"), L.T("Ancien Enfant de dragon"), L.T("Les cris avant tout, très offensif."), new FormKey(Dragonborn, 0x0265B0)),

        // Inspirés des classes d'Elder Scrolls Online : réglages à nous, à partir d'un style du jeu
        new("eso_dragonknight", L.T("Inspirés d'ESO"), L.T("Chevalier-dragon"), L.T("Mêlée lourde, cris et sorts au besoin ; offensif."), null,
            new(Sk(0x03DECE), Melee: 5, Magic: 2, Ranged: 0.5f, Shout: 3, Staff: 1, Unarmed: 0.5f, Offense: 0.85f, Defense: 0.5f)),
        new("eso_templar", L.T("Inspirés d'ESO"), L.T("Templier"), L.T("Mêlée et magie à parts égales, défensif."), null,
            new(Sk(0x03DECE), Melee: 3, Magic: 3, Ranged: 0.5f, Shout: 1, Staff: 1, Unarmed: 0.5f, Offense: 0.6f, Defense: 0.7f)),
        new("eso_nightblade", L.T("Inspirés d'ESO"), L.T("Lame noire"), L.T("Contourne l'ennemi, mêlée rapide à deux armes, un peu de magie ; peu défensif."), null,
            new(Sk(0x103938), Melee: 5, Magic: 1.5f, Ranged: 1, Shout: 1, Staff: 1, Unarmed: 0.5f, Offense: 0.8f, Defense: 0.2f, Flanking: true, DualWield: true)),
        new("eso_sorcerer", L.T("Inspirés d'ESO"), L.T("Sorcier (ESO)"), L.T("Sorts et bâtons à distance, presque jamais de mêlée."), null,
            new(Sk(0x03BE1C), Melee: 0.3f, Magic: 8, Ranged: 1, Shout: 1, Staff: 3, Unarmed: 0.3f, Offense: 0.7f, Defense: 0.4f)),
        new("eso_warden", L.T("Inspirés d'ESO"), L.T("Gardien"), L.T("Arc, sorts et mêlée selon la distance ; équilibré."), null,
            new(Sk(0x1034F0), Melee: 2, Magic: 3, Ranged: 2, Shout: 1, Staff: 1, Unarmed: 0.5f, Offense: 0.6f, Defense: 0.55f)),
        new("eso_necromancer", L.T("Inspirés d'ESO"), L.T("Nécromancien"), L.T("Sorts (conjuration) et bâtons, reste en retrait."), null,
            new(Sk(0x10554E), Melee: 0.5f, Magic: 6, Ranged: 0.5f, Shout: 1, Staff: 3, Unarmed: 0.3f, Offense: 0.65f, Defense: 0.35f)),
    ];

    /// <summary>Classes de combat du jeu. La fiche y ajoute les compétences principales, lues dans l'enregistrement
    /// (voir Skills) ; Desc ne cite que des followers du jeu vérifiés (lookup npc).</summary>
    public static List<ClassChoice> Classes() =>
    [
        new("CombatWarrior1H", L.T("Guerriers"), L.T("Guerrier une main"), L.T("Lydia, Golldir."), Sk(0x013176)),
        new("CombatWarrior2H", L.T("Guerriers"), L.T("Guerrier deux mains"), L.T("Mjoll, Uthgerd, Stenvar, Benor."), Sk(0x01CE15)),
        new("CombatBarbarian", L.T("Guerriers"), L.T("Barbare"), L.T("Vorstag, Cosnach, Ahtar."), Sk(0x01CE16)),
        new("CombatMonk", L.T("Guerriers"), L.T("Moine"), "", Sk(0x013182)),
        new("Blade", L.T("Guerriers"), L.T("Lame"), "", Sk(0x021A74)),
        new("CombatRanger", L.T("Agiles"), L.T("Rôdeur"), L.T("Jenassa, Annekke."), Sk(0x013181)),
        new("CombatScout", L.T("Agiles"), L.T("Éclaireur"), "", Sk(0x01317D)),
        new("CombatThief", L.T("Agiles"), L.T("Voleur"), "", Sk(0x01317E)),
        new("CombatAssassin", L.T("Agiles"), L.T("Assassin"), "", Sk(0x01317F)),
        new("CombatRogue", L.T("Agiles"), L.T("Roublard"), "", Sk(0x013180)),
        new("CombatNightingale", L.T("Agiles"), L.T("Rossignol"), "", Sk(0x0FB0DA)),
        new("CombatSpellsword", L.T("Hybrides"), L.T("Lame-sort"), "", Sk(0x013177)),
        new("CombatWitchblade", L.T("Hybrides"), L.T("Lame de sorcière"), "", Sk(0x013178)),
        new("CombatNightblade", L.T("Hybrides"), L.T("Lame de la nuit"), "", Sk(0x01317C)),
        new("DLC2NPCClassTeldryn", L.T("Hybrides"), L.T("Teldryn Sero"), L.T("Teldryn Sero."), new FormKey(Dragonborn, 0x038561)),
        new("CombatMageDestruction", L.T("Mages"), L.T("Mage de destruction"), "", Sk(0x01E7D0)),
        new("CombatMageElemental", L.T("Mages"), L.T("Mage élémentaire"), L.T("Illia."), Sk(0x01317A)),
        new("CombatMageConjurer", L.T("Mages"), L.T("Conjurateur"), "", Sk(0x01CE14)),
        new("CombatMageNecro", L.T("Mages"), L.T("Nécromancien"), "", Sk(0x0C969F)),
        new("CombatSorcerer", L.T("Mages"), L.T("Sorcier"), "", Sk(0x013179)),
        new("CombatMystic", L.T("Mages"), L.T("Mystique"), L.T("Brelyna Maryon."), Sk(0x01317B)),
    ];

    /// <summary>Compétences principales d'une classe (les plus forts poids), traduites.</summary>
    public static string Skills(IClassGetter cls, int count = 4) =>
        string.Join(", ", cls.SkillWeights.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(count).Select(kv => SkillName(kv.Key)));

    static string SkillName(Skill s) => s switch
    {
        Skill.OneHanded => L.T("une main"), Skill.TwoHanded => L.T("deux mains"), Skill.Archery => L.T("archerie"),
        Skill.Block => L.T("parade"), Skill.Smithing => L.T("forge"), Skill.HeavyArmor => L.T("armure lourde"),
        Skill.LightArmor => L.T("armure légère"), Skill.Pickpocket => L.T("vol à la tire"), Skill.Lockpicking => L.T("crochetage"),
        Skill.Sneak => L.T("discrétion"), Skill.Alchemy => L.T("alchimie"), Skill.Speech => L.T("éloquence"),
        Skill.Alteration => L.T("altération"), Skill.Conjuration => L.T("conjuration"), Skill.Destruction => L.T("destruction"),
        Skill.Illusion => L.T("illusion"), Skill.Restoration => L.T("guérison"), Skill.Enchanting => L.T("enchantement"),
        _ => s.ToString(),
    };

    // ------------------------------------------------------------------ choix

    static string ChoicePath(Context ctx) => Path.Combine(ctx.WorkDir, "combat_choice.json");

    public sealed record Choice(string? Style, string? Classe);

    public static Dictionary<string, Choice> LoadChoices(Context ctx)
    {
        var all = new Dictionary<string, Choice>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(ChoicePath(ctx))) return all;
        if (JsonNode.Parse(File.ReadAllText(ChoicePath(ctx))) is JsonObject o)
            foreach (var (id, v) in o)
                all[id] = new(v?["style"]?.GetValue<string>(), v?["classe"]?.GetValue<string>());
        return all;
    }

    /// <summary>Mémorise le style et la classe d'un preset (null : défaut). Les deux null : entrée retirée.</summary>
    public static void SaveChoice(Context ctx, string presetId, string? style, string? classe)
    {
        if (!string.IsNullOrEmpty(style) && !Styles().Any(s => s.Key == style)) throw new GfException(L.T("Style de combat inconnu : {0}", style));
        if (!string.IsNullOrEmpty(classe) && !Classes().Any(c => c.EditorId == classe)) throw new GfException(L.T("Classe inconnue : {0}", classe));
        var all = LoadChoices(ctx);
        if (string.IsNullOrEmpty(style) && string.IsNullOrEmpty(classe)) all.Remove(presetId);
        else all[presetId] = new(string.IsNullOrEmpty(style) ? null : style, string.IsNullOrEmpty(classe) ? null : classe);
        var o = new JsonObject();
        foreach (var (id, c) in all.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var e = new JsonObject();
            if (c.Style != null) e["style"] = c.Style;
            if (c.Classe != null) e["classe"] = c.Classe;
            o[id] = e;
        }
        File.WriteAllText(ChoicePath(ctx), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ build

    /// <summary>Applique le choix au PNJ (rien sans choix : ESP identique). Un style ESO est créé une fois par plugin.</summary>
    public static void Apply(Context ctx, SkyrimMod mod, Npc npc, PresetRow row, Choice choice, StringBuilder report)
    {
        if (choice.Classe != null)
        {
            var c = Classes().FirstOrDefault(x => x.EditorId == choice.Classe);
            if (c != null && ctx.Cache.TryResolve<IClassGetter>(c.Key, out var cls))
            {
                npc.Class.SetTo(cls);
                report.AppendLine($"COMBAT  {row.Id}  classe {c.EditorId} ({c.Key})");
            }
            else report.AppendLine($"ATTENTION {row.Id}  classe {choice.Classe} introuvable : classe de config.json gardée");
        }
        if (choice.Style == null) return;
        var s = Styles().FirstOrDefault(x => x.Key == choice.Style);
        if (s == null) { report.AppendLine($"ATTENTION {row.Id}  style de combat {choice.Style} inconnu : aucun style"); return; }
        if (s.Vanilla is { } fk)
        {
            if (!ctx.Cache.TryResolve<ICombatStyleGetter>(fk, out var cs)) { report.AppendLine($"ATTENTION {row.Id}  style {s.Key} ({fk}) introuvable : aucun style"); return; }
            npc.CombatStyle.SetTo(cs);
            report.AppendLine($"COMBAT  {row.Id}  style {s.Key} ({fk})");
            return;
        }
        var t = s.Eso!;
        var edid = "GF_CS_" + s.Key;
        var made = mod.CombatStyles.FirstOrDefault(x => x.EditorID == edid);
        if (made == null)
        {
            // copie de l'original du jeu (pas d'un mod qui le modifierait) : même résultat chez tous les joueurs
            var src = Original(ctx, t.Base);
            if (src == null) { report.AppendLine($"ATTENTION {row.Id}  style de base {t.Base} introuvable : aucun style"); return; }
            made = mod.CombatStyles.DuplicateInAsNewRecord(src, edid);
            made.EquipmentScoreMultMelee = t.Melee;
            made.EquipmentScoreMultMagic = t.Magic;
            made.EquipmentScoreMultRanged = t.Ranged;
            made.EquipmentScoreMultShout = t.Shout;
            made.EquipmentScoreMultStaff = t.Staff;
            made.EquipmentScoreMultUnarmed = t.Unarmed;
            made.OffensiveMult = t.Offense;
            made.DefensiveMult = t.Defense;
            var flags = made.Flags & ~(CombatStyle.Flag.Dueling | CombatStyle.Flag.Flanking | CombatStyle.Flag.AllowDualWielding);
            flags |= t.Flanking ? CombatStyle.Flag.Flanking : CombatStyle.Flag.Dueling;
            if (t.DualWield) flags |= CombatStyle.Flag.AllowDualWielding;
            made.Flags = flags;
        }
        npc.CombatStyle.SetTo(made);
        report.AppendLine($"COMBAT  {row.Id}  style {s.Key} ({edid}, copie de {t.Base})");
    }

    /// <summary>
    /// Profil d'un style (multiplicateurs d'équipement mêlée, magie, distance) : réglages ESO, ou style du jeu tel que défini
    /// par son plugin d'origine. Null si le style est inconnu ou introuvable.
    /// </summary>
    public static (float Melee, float Magic, float Ranged)? Profile(Context ctx, string key)
    {
        var s = Styles().FirstOrDefault(x => x.Key == key);
        if (s?.Eso is { } t) return (t.Melee, t.Magic, t.Ranged);
        if (s?.Vanilla is { } fk && Original(ctx, fk) is { } cs)
            return (cs.EquipmentScoreMultMelee, cs.EquipmentScoreMultMagic, cs.EquipmentScoreMultRanged);
        return null;
    }

    /// <summary>Style à distance : préfère l'arc (multiplicateur distance ≥ 2 et ≥ mêlée).</summary>
    public static bool IsRanged((float Melee, float Magic, float Ranged) p) => p.Ranged >= 2 && p.Ranged >= p.Melee;

    /// <summary>Style magique : préfère les sorts (multiplicateur magie ≥ 2 et ≥ mêlée).</summary>
    public static bool IsMagic((float Melee, float Magic, float Ranged) p) => p.Magic >= 2 && p.Magic >= p.Melee;

    /// <summary>Enregistrement tel que défini par son plugin d'origine (Skyrim.esm, DLC), sinon la version gagnante.</summary>
    static ICombatStyleGetter? Original(Context ctx, FormKey fk)
    {
        if (ctx.Env.LoadOrder.ListedOrder.FirstOrDefault(l => l.ModKey == fk.ModKey)?.Mod is { } m
            && m.CombatStyles.FirstOrDefault(x => x.FormKey == fk) is { } orig) return orig;
        return ctx.Cache.TryResolve<ICombatStyleGetter>(fk, out var won) ? won : null;
    }

    /// <summary>Fiche du Builder : listes, choix actuel et défauts.</summary>
    public static JsonObject Info(Context ctx, PresetRow row)
    {
        var choice = LoadChoices(ctx).GetValueOrDefault(row.Id);
        var cls = ctx.Config.FollowerClass;
        var def = Classes().FirstOrDefault(c => c.EditorId == cls);
        return new JsonObject
        {
            ["style"] = choice?.Style, ["classe"] = choice?.Classe,
            ["classeDefaut"] = def != null ? $"{def.Name} ({cls})" : cls,
            ["styles"] = new JsonArray(Styles().Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Key, ["groupe"] = s.Group, ["nom"] = s.Name, ["desc"] = s.Desc,
                ["edid"] = s.Vanilla != null ? s.Key : "GF_CS_" + s.Key,
                ["source"] = s.Vanilla is { } fk ? fk.ModKey.FileName.String : L.T("créé dans le plugin du follower"),
            }).ToArray()),
            ["classes"] = new JsonArray(Classes().Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.EditorId, ["groupe"] = c.Group, ["nom"] = c.Name, ["desc"] = c.Desc, ["source"] = c.Key.ModKey.FileName.String,
                ["competences"] = ctx.Cache.TryResolve<IClassGetter>(c.Key, out var g) ? Skills(g) : null,
            }).ToArray()),
        };
    }
}
