using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Archétypes : un clic règle d'un coup style de combat, classe, tenue, sorts, atouts et armes de départ (tout reste
/// modifiable ensuite, champ par champ). Ne touche ni à la voix (liée à la race et au choix de l'utilisateur), ni au niveau,
/// au statut, au comportement, à la taille, au mariage ou au skin. L'archétype appliqué est noté ("archetype") pour la
/// fiche ; le build n'utilise que les champs qu'il a remplis. Chaque identifiant est contrôlé contre les catalogues (Combat,
/// tenues, sorts, atouts, armes) : commande « options archetypes ».
/// </summary>
public static partial class Options
{
    public sealed record Archetype(string Key, string Group, string Name, string Desc, string Style, string Class, string Outfit,
        string[] Weapons, string[] Spells, string[] Perks);

    public static List<Archetype> Archetypes() =>
    [
        new("guerrier_bouclier", L.T("Classiques"), L.T("Guerrier au bouclier"), L.T("Épée et bouclier, armure d'acier, encaisse et protège."),
            "csHumanBoss1H", "CombatWarrior1H", "ArmorSteelNoHelmetOutfit", ["SteelSword", "ArmorSteelShield"], [], ["Armsman00", "ShieldWall00", "Juggernaut00"]),
        new("guerrier_2m", L.T("Classiques"), L.T("Guerrier à deux mains"), L.T("Espadon et armure de plaques, frappe fort."),
            "csHumanBoss2H", "CombatWarrior2H", "ArmorSteelPlateNoHelmetOutfit", ["SteelGreatsword"], [], ["Barbarian00", "ChampionsStance", "Juggernaut00"]),
        new("berserker", L.T("Classiques"), L.T("Berserker"), L.T("Hache de bataille, armure légère, attaque sans relâche."),
            "csHumanBerserkerLvl1", "CombatBarbarian", "ArmorScaledNoHelmetOutfit", ["SteelBattleAxe"], [], ["Barbarian00", "Limbsplitter30", "DevastatingBlow"]),
        new("duelliste", L.T("Classiques"), L.T("Duelliste à deux armes"), L.T("Deux épées, armure elfique, rapide et offensif."),
            "csThalmorMeleeDual", "CombatRogue", "ArmorElvenNoHelmetOutfit", ["ElvenSword", "SteelSword"], [], ["Armsman00", "DualFlurry30", "DualSavagery", "AgileDefender00"]),
        new("archer", L.T("Classiques"), L.T("Archer"), L.T("Arc long et flèches d'acier, garde ses distances ; dague de secours."),
            "csHumanMissile", "CombatRanger", "ArmorLeatherNoHelmetOutfit", ["LongBow", "SteelArrow", "SteelDagger"], [], ["Overdraw00", "EagleEye30", "CriticalShot30", "AgileDefender00"]),
        new("assassin", L.T("Classiques"), L.T("Assassin"), L.T("Deux dagues, armure de la Confrérie noire, contourne l'ennemi."),
            "csMercerFreyMelee", "CombatAssassin", "DBArmorNoHood", ["ElvenDagger", "SteelDagger"], [], ["Stealth00", "Backstab", "AssassinsBlade", "DualFlurry30"]),
        new("mage_destruction", L.T("Classiques"), L.T("Mage de destruction"), L.T("Feu, givre et foudre à distance, bâton de boules de feu."),
            "csWarlock", "CombatMageDestruction", "ClothesMageRobesOutfitNoHood", ["StaffFirebolt"],
            ["Firebolt", "IceSpike", "LightningBolt", "FireRune", "FastHealing"], ["DestructionNovice00", "DestructionApprentice25", "AugmentedFlames", "Impact"]),
        new("conjurateur", L.T("Classiques"), L.T("Conjurateur"), L.T("Invoque un atronach et une épée liée, réanime les morts."),
            "csWERangedConjurer", "CombatMageConjurer", "NecromancerOutfitNoHood", [],
            ["ConjureFlameAtronach", "BoundSword", "ReanimateCorpse", "Firebolt"], ["ConjurationNovice00", "ConjurationApprentice25", "Summoner30", "Atromancy"]),
        new("guerisseur", L.T("Classiques"), L.T("Guérisseur"), L.T("Soigne le groupe, protège avec des barrières, masse d'acier."),
            "csHumanMagic", "CombatMystic", "MonkOutfitNoHood", ["SteelMace"],
            ["FastHealing", "HealOther", "WardSteadfast", "TurnLesserUndead"], ["RestorationNovice00", "RestorationApprentice25", "Recovery30", "Respite"]),
        new("lame_sort", L.T("Classiques"), L.T("Lame-sort"), L.T("Épée et sorts de base : flammes, étincelles, soins, peau de chêne."),
            "csSpellsword", "CombatSpellsword", "ArmorElvenNoHelmetOutfit", ["SteelSword"],
            ["Flames", "Sparks", "FastHealing", "Oakflesh"], ["Armsman00", "DestructionNovice00", "AugmentedFlames"]),

        new("eso_chevalier_dragon", L.T("Inspirés d'ESO"), L.T("Chevalier-dragon"), L.T("Épée, bouclier, armure de plaques et sorts de feu."),
            "eso_dragonknight", "CombatWarrior1H", "ArmorSteelPlateNoHelmetOutfit", ["SteelSword", "ArmorSteelShield"],
            ["Flames", "Firebolt", "flameCloak"], ["Armsman00", "ShieldWall00", "AugmentedFlames"]),
        new("eso_templier", L.T("Inspirés d'ESO"), L.T("Templier"), L.T("Masse, bouclier, armure impériale ; soins et lumière contre les morts-vivants."),
            "eso_templar", "CombatSpellsword", "ArmorImperialHeavyOutfitNoHelmet", ["SteelMace", "ArmorSteelShield"],
            ["FastHealing", "DLC1SunFire", "WardSteadfast", "TurnLesserUndead"], ["Armsman00", "RestorationNovice00", "Respite"]),
        new("eso_lame_noire", L.T("Inspirés d'ESO"), L.T("Lame noire"), L.T("Deux dagues, discrétion, peur et invisibilité."),
            "eso_nightblade", "CombatNightblade", "DBArmorNoHood", ["ElvenDagger", "SteelDagger"],
            ["Fear", "Invisibility", "Muffle"], ["Stealth00", "Backstab", "DualFlurry30", "AspectOfTerror"]),
        new("eso_sorcier", L.T("Inspirés d'ESO"), L.T("Sorcier (ESO)"), L.T("Foudre, atronach de tempête, épée liée et cape d'éclairs."),
            "eso_sorcerer", "CombatSorcerer", "ClothesMageRobesOutfitNoHood", [],
            ["LightningBolt", "ChainLightning", "ConjureStormAtronach", "BoundSword", "LightningCloak"], ["AugmentedShock", "ConjurationNovice00", "DestructionApprentice25"]),
        new("eso_gardien", L.T("Inspirés d'ESO"), L.T("Gardien"), L.T("Arc et hache, givre, soins et familier."),
            "eso_warden", "CombatRanger", "ArmorHideSimpleOutfit", ["HuntingBow", "IronArrow", "SteelWarAxe"],
            ["Frostbite", "IceSpike", "FastHealing", "ConjureFamiliar"], ["Overdraw00", "AugmentedFrost", "RestorationNovice00"]),
        new("eso_necromancien", L.T("Inspirés d'ESO"), L.T("Nécromancien"), L.T("Réanime les morts, bâton de givre."),
            "eso_necromancer", "CombatMageNecro", "NecromancerOutfitNoHood", ["StaffFrostbite"],
            ["ReanimateCorpse", "Revenant", "RaiseZombie", "Frostbite"], ["ConjurationNovice00", "Necromancy", "DarkSouls"]),
    ];

    /// <summary>Identifiants d'un archétype absents des catalogues (vide = archétype complet).</summary>
    public static List<string> CheckArchetype(Context ctx, Archetype a)
    {
        var bad = new List<string>();
        if (!Combat.Styles().Any(s => s.Key == a.Style)) bad.Add("style " + a.Style);
        if (!Combat.Classes().Any(c => c.EditorId == a.Class)) bad.Add("classe " + a.Class);
        if (!Outfits(ctx).Any(o => o.EditorId == a.Outfit)) bad.Add("tenue " + a.Outfit);
        bad.AddRange(a.Weapons.Where(w => !Weapons(ctx).Any(x => x.EditorId == w)).Select(w => "arme " + w));
        bad.AddRange(a.Spells.Where(s => !Spells(ctx).Any(x => x.EditorId == s)).Select(s => "sort " + s));
        bad.AddRange(a.Perks.Where(p => !Perks(ctx).Any(x => x.EditorId == p)).Select(p => "atout " + p));
        return bad;
    }

    static JsonArray ArchetypesJson(Context ctx) => new(Archetypes().Where(a => CheckArchetype(ctx, a).Count == 0).Select(a => (JsonNode)new JsonObject
    {
        ["id"] = a.Key, ["groupe"] = a.Group, ["nom"] = a.Name, ["desc"] = a.Desc,
        ["contenu"] = L.T("Style {0}, classe {1}, tenue {2}, {3} arme(s), {4} sort(s), {5} atout(s).",
            Combat.Styles().First(s => s.Key == a.Style).Name, Combat.Classes().First(c => c.EditorId == a.Class).Name,
            Outfits(ctx).First(o => o.EditorId == a.Outfit).Name, a.Weapons.Length, a.Spells.Length, a.Perks.Length),
    }).ToArray());

    /// <summary>Applique un archétype à un preset : remplace style, classe, tenue, armes, sorts et atouts.</summary>
    public static void ApplyArchetype(Context ctx, PresetRow row, string key)
    {
        var a = Archetypes().FirstOrDefault(x => x.Key == key) ?? throw new GfException(L.T("Archétype inconnu : {0}", key));
        if (CheckArchetype(ctx, a) is { Count: > 0 } bad) throw new GfException(L.T("Archétype incomplet ({0}) : {1}", key, string.Join(", ", bad)));
        Combat.SaveChoice(ctx, row.Id, a.Style, a.Class);
        Set(ctx, row.Id, "tenue", JsonValue.Create(a.Outfit));
        Set(ctx, row.Id, "armes", a.Weapons.Length > 0 ? new JsonArray(a.Weapons.Select(x => (JsonNode)x).ToArray()) : null);
        Set(ctx, row.Id, "sorts", a.Spells.Length > 0 ? new JsonArray(a.Spells.Select(x => (JsonNode)x).ToArray()) : null);
        Set(ctx, row.Id, "atouts", a.Perks.Length > 0 ? new JsonArray(a.Perks.Select(x => (JsonNode)x).ToArray()) : null);
        Set(ctx, row.Id, "archetype", JsonValue.Create(a.Key));
    }
}
