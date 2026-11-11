using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Atouts d'un follower : ceux des arbres de compétences utiles en combat, tels qu'ils sont dans l'ordre de chargement
/// (version gagnante de l'AVIF : un mod qui refait les arbres, Adamant, Ordinator, Vokrii…, y montre ses atouts et ses
/// noms ; 10/11). Les atouts du jeu de base qui ne sont plus dans l'arbre gagnant restent proposés (« hors de l'arbre »)
/// pour ne pas perdre les anciens choix ni les archétypes. Un atout à plusieurs rangs (Armsman 1-5…) est une chaîne
/// d'enregistrements (NextPerk) : le choisir donne tous ses rangs, comme un joueur qui l'a monté au maximum.
/// Choix : options_choice.json, "atouts": [EditorID du premier rang…]. Au build : atouts du jeu de base et des DLC dans
/// la liste d'atouts du PNJ (PRKR, rang 1 pour chaque enregistrement de la chaîne) ; atouts d'un mod par SkyPatcher
/// (SKSE\Plugins\SkyPatcher\npc\GenerateFollower\&lt;plugin&gt;.ini, perksToAdd) : le mod ne devient pas master du pack,
/// et un atout dont le mod a été désinstallé est simplement ignoré au chargement.
/// </summary>
public static partial class Options
{
    /// <summary>Arbres proposés (EditorID de l'AVIF → compétence affichée).</summary>
    static readonly string[] PerkTrees =
    [
        "AVOneHanded", "AVTwoHanded", "AVMarksman", "AVBlock", "AVHeavyArmor", "AVLightArmor", "AVSneak",
        "AVAlteration", "AVConjuration", "AVDestruction", "AVMysticism", "AVRestoration",
    ];

    static string TreeName(string tree) => tree switch
    {
        "AVOneHanded" => L.T("Une main"), "AVTwoHanded" => L.T("Deux mains"), "AVMarksman" => L.T("Archerie"), "AVBlock" => L.T("Parade"),
        "AVHeavyArmor" => L.T("Armure lourde"), "AVLightArmor" => L.T("Armure légère"), "AVSneak" => L.T("Discrétion"),
        "AVAlteration" => L.T("Altération"), "AVConjuration" => L.T("Conjuration"), "AVDestruction" => L.T("Destruction"),
        "AVMysticism" => L.T("Illusion"), "AVRestoration" => L.T("Guérison"), _ => tree,
    };

    /// <param name="InTree">dans l'arbre de l'ordre de chargement (false : atout du jeu de base retiré par un mod d'atouts)</param>
    /// <param name="ForNpc">au moins un effet qui agit sur un PNJ (sinon : forge, crochetage, prix, activation… ou script)</param>
    public sealed record PerkChoice(string EditorId, FormKey Key, string Skill, string Name, int Ranks, List<FormKey> Chain,
        bool InTree, bool ForNpc, string? TreeMod);

    static List<PerkChoice>? perks;

    /// <summary>Mots des points d'entrée qui ne servent qu'au joueur (menus, artisanat, vol, commerce, activation).</summary>
    static readonly string[] PlayerOnlyEntryWords =
    [
        "Activate", "Lockpick", "Pickpocket", "Price", "Barter", "Buy", "Sell", "Smithing", "Tempering", "Potion", "Ingredient",
        "Poison", "Enchant", "SoulGem", "Soul", "Speech", "Persuasion", "Intimidation", "Favor", "Book", "Recipe", "Map",
        "Player", "Lock", "Bribe", "Harvest", "Gold", "Murder", "Pickup", "Mount", "Horse",
    ];

    /// <summary>
    /// Un atout agit-il sur un PNJ ? Oui s'il a un effet « capacité » (sort ajouté), ou un point d'entrée qui n'est pas
    /// réservé au joueur (dégâts, armure, coût et puissance des sorts, parade, attaque sournoise…). Un atout sans effet agit
    /// par les conditions d'autres enregistrements (effets de sorts « HasPerk » : Mage Armor, Deep Freeze…) : compté comme
    /// utile. Seuls les atouts dont tous les effets sont des effets « quête » ou réservés au joueur sont écartés.
    /// </summary>
    static bool WorksOnNpc(IPerkGetter perk)
    {
        if (perk.Effects.Count == 0) return true;
        foreach (var e in perk.Effects)
        {
            if (e is IPerkAbilityEffectGetter) return true;
            if (e is IAPerkEntryPointEffectGetter ep)
            {
                var name = ep.EntryPoint.ToString();
                if (!PlayerOnlyEntryWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        return false;
    }

    /// <summary>Effets de la chaîne d'un atout (diagnostic : « options » en ligne de commande).</summary>
    public static string PerkEffectsText(Context ctx, PerkChoice p) => string.Join(", ", p.Chain
        .Select(k => ctx.Cache.TryResolve<IPerkGetter>(k, out var perk) ? perk : null).Where(x => x != null)
        .SelectMany(perk => perk!.Effects.Select(e => e switch
        {
            IAPerkEntryPointEffectGetter ep => ep.EntryPoint.ToString(),
            IPerkAbilityEffectGetter => "Ability",
            IPerkQuestEffectGetter => "Quest",
            _ => e.GetType().Name,
        }).Concat(perk.VirtualMachineAdapter != null ? ["Script"] : []))
        .Distinct());

    public static List<PerkChoice> Perks(Context ctx)
    {
        if (perks != null) return perks;
        var masters = MasterMods(ctx);
        var avifs = new Dictionary<string, IActorValueInformationGetter>(StringComparer.OrdinalIgnoreCase);
        // EditorID d'origine des atouts du jeu de base : identifiant stable des choix et des archétypes (Adamant renomme
        // ShieldWall00 en MAG_…)
        var baseIds = new Dictionary<FormKey, string>();
        foreach (var m in masters)
        {
            foreach (var a in m.ActorValueInformation) if (a.EditorID != null) avifs[a.EditorID] = a;
            foreach (var p in m.Perks) if (p.EditorID != null) baseIds[p.FormKey] = p.EditorID;
        }
        // version gagnante d'un atout (nom, effets, rang suivant modifiés par un mod d'atouts)
        IPerkGetter? Perk(FormKey k) => !k.IsNull && ctx.Cache.TryResolve<IPerkGetter>(k, out var p) ? p : null;
        var list = new List<PerkChoice>();
        foreach (var tree in PerkTrees)
        {
            if (!avifs.TryGetValue(tree, out var original)) continue;
            var winner = ctx.Cache.TryResolve<IActorValueInformationGetter>(original.FormKey, out var w) ? w : original;
            // mod qui refait l'arbre : la dernière version hors jeu de base, sans les patchs fusionnés (Bashed Patch…)
            var treeMod = ctx.Cache.ResolveAllContexts<ActorValueInformation, IActorValueInformationGetter>(original.FormKey)
                .Select(c => c.ModKey.FileName.String)
                .FirstOrDefault(m => !Masters.Contains(m, StringComparer.OrdinalIgnoreCase)
                    && !m.StartsWith("Bashed Patch", StringComparison.OrdinalIgnoreCase) && !m.StartsWith("Synthesis", StringComparison.OrdinalIgnoreCase)
                    && !m.StartsWith("Smashed Patch", StringComparison.OrdinalIgnoreCase));
            var skill = TreeName(tree);
            void Add(IEnumerable<IActorValuePerkNodeGetter> nodes, bool inTree)
            {
                foreach (var node in nodes)
                {
                    var perk = Perk(node.Perk.FormKey);
                    if (perk?.EditorID == null) continue;
                    if (list.Any(p => p.Key == perk.FormKey)) continue;
                    // chaîne des rangs : NextPerk jusqu'au dernier (protégée contre une boucle)
                    var chain = new List<FormKey> { perk.FormKey };
                    var ranks = new List<IPerkGetter> { perk };
                    for (var next = perk.NextPerk.FormKeyNullable; next is { } n && !n.IsNull && !chain.Contains(n) && Perk(n) is { } np; next = np.NextPerk.FormKeyNullable)
                    {
                        chain.Add(n);
                        ranks.Add(np);
                    }
                    list.Add(new(baseIds.GetValueOrDefault(perk.FormKey, perk.EditorID), perk.FormKey, skill, perk.Name?.String ?? perk.EditorID, chain.Count, chain,
                        inTree, ranks.Any(WorksOnNpc), treeMod));
                }
            }
            Add(winner.PerkTree, true);
            // atouts du jeu de base retirés de l'arbre par le mod d'atouts : gardés, marqués
            if (!ReferenceEquals(winner, original)) Add(original.PerkTree, false);
        }
        return perks = list.DistinctBy(p => p.EditorId).ToList();
    }

    static bool IsBasePerk(FormKey k) => Masters.Contains(k.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase);

    static JsonArray PerksJson(Context ctx) => new(Perks(ctx).Select(p => (JsonNode)new JsonObject
    {
        ["id"] = p.EditorId, ["competence"] = p.Skill, ["nom"] = p.Name, ["rangs"] = p.Ranks, ["source"] = p.Key.ModKey.FileName.String,
        ["mod"] = p.Chain.Any(k => !IsBasePerk(k)),
        // mods des rangs (Rune Mage : rang 1 de Skyrim.esm, rang 2 d'Adamant.esp)
        ["modsAtout"] = string.Join(", ", p.Chain.Where(k => !IsBasePerk(k)).Select(k => k.ModKey.FileName.String).Distinct()),["horsArbre"] = !p.InTree, ["pnj"] = p.ForNpc, ["arbreMod"] = p.TreeMod,
    }).ToArray());

    static void SavePerks(Context ctx, PresetRow row, JsonNode? value)
    {
        var ids = value is JsonArray a ? a.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList() : [];
        var known = Perks(ctx).Select(p => p.EditorId).ToHashSet();
        if (ids.FirstOrDefault(i => !known.Contains(i)) is { } bad) throw new GfException(L.T("Atout inconnu : {0}", bad));
        Set(ctx, row.Id, "atouts", ids.Count == 0 ? null : new JsonArray(ids.Select(i => (JsonNode)i).ToArray()));
    }

    static void ApplyPerks(Context ctx, Npc npc, PresetRow row, JsonObject opts, StringBuilder report)
    {
        if (opts["atouts"] is not JsonArray ids) return;
        var byId = Perks(ctx).ToDictionary(p => p.EditorId);
        var added = new List<string>();
        foreach (var id in ids.Select(x => x?.GetValue<string>() ?? ""))
        {
            if (!byId.TryGetValue(id, out var p)) { report.AppendLine($"ATTENTION {row.Id}  atout {id} inconnu : ignoré"); continue; }
            foreach (var fk in p.Chain)
            {
                // atout d'un mod : par SkyPatcher (ModPerks), pas dans le plugin (le mod deviendrait master du pack)
                if (!IsBasePerk(fk)) continue;
                if (!ctx.Cache.TryResolve<IPerkGetter>(fk, out var perk)) { report.AppendLine($"ATTENTION {row.Id}  atout {fk} introuvable : ignoré"); continue; }
                if (npc.Perks?.Any(x => x.Perk.FormKey == fk) == true) continue;
                (npc.Perks ??= []).Add(new PerkPlacement { Perk = perk.ToLink(), Rank = 1 });
            }
            added.Add(p.Ranks > 1 ? $"{id} ({p.Ranks} rangs)" : id);
        }
        if (added.Count > 0) report.AppendLine($"OPTION  {row.Id}  atouts {string.Join(", ", added)}");
    }

    /// <summary>Atouts de mods choisis pour ce preset (enregistrements de toute la chaîne des rangs).</summary>
    public static List<FormKey> ModPerks(Context ctx, string presetId)
    {
        if (For(ctx, presetId)["atouts"] is not JsonArray ids) return [];
        var byId = Perks(ctx).ToDictionary(p => p.EditorId);
        return ids.Select(x => x?.GetValue<string>() ?? "").Where(byId.ContainsKey)
            .SelectMany(i => byId[i].Chain).Where(k => !IsBasePerk(k)).Distinct().ToList();
    }

    /// <summary>SkyPatcher installé (SKSE\Plugins\SkyPatcher.dll) : sans lui, les atouts de mods ne sont pas donnés.</summary>
    public static bool SkyPatcherInstalled(Context ctx) => ctx.FindFile(@"SKSE\Plugins\SkyPatcher.dll") != null;

    /// <summary>
    /// Fichier SkyPatcher du pack : une ligne par follower qui a des atouts de mods
    /// (filterByNpcs=&lt;plugin du pack&gt;|&lt;FormID&gt;:perksToAdd=&lt;mod&gt;|&lt;FormID&gt;,…).
    /// </summary>
    public static void WriteModPerks(Context ctx, IEnumerable<(PresetRow Row, Npc Npc)> built, string plugin, string modDir, StringBuilder report)
    {
        var lines = new List<string>();
        foreach (var (row, npc) in built)
        {
            var keys = ModPerks(ctx, row.Id).Where(k => ctx.Cache.TryResolve<IPerkGetter>(k, out _)).ToList();
            if (keys.Count == 0) continue;
            // commentaire en ASCII seulement (lecture de l'ini par SkyPatcher)
            lines.Add("; " + new string(row.Nom.Select(c => c < 128 && !char.IsControl(c) ? c : '_').ToArray()));
            lines.Add($"filterByNpcs={plugin}|{npc.FormKey.ID:X}:perksToAdd=" + string.Join(",", keys.Select(k => $"{k.ModKey.FileName}|{k.ID:X}")));
            report.AppendLine($"OPTION  {row.Id}  atouts de mods par SkyPatcher : {string.Join(", ", keys.Select(k => k.ModKey.FileName.String).Distinct())}");
        }
        if (lines.Count == 0) return;
        var dir = Path.Combine(modDir, "SKSE", "Plugins", "SkyPatcher", "npc", "GenerateFollower");
        Directory.CreateDirectory(dir);
        lines.Insert(0, "; GenerateFollower: perks from mods for the followers of this pack (SkyPatcher, perksToAdd). A missing mod is skipped.");
        File.WriteAllLines(Path.Combine(dir, Path.GetFileNameWithoutExtension(plugin) + ".ini"), lines, new UTF8Encoding(false));
        if (!SkyPatcherInstalled(ctx))
            report.AppendLine("ATTENTION " + L.T("Atouts de mods : SkyPatcher n'est pas installé, ils ne seront pas donnés en jeu tant qu'il ne l'est pas."));
    }
}
