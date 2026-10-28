using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Relevé en lecture seule (commande « releve-mods ») : sorts et lieux que les mods du joueur ajouteraient aux catalogues
/// de la fiche (aujourd'hui limités à Skyrim.esm et aux DLC). Sorts = enseignés par un livre de sorts d'un mod ; lieux =
/// cellules intérieures créées par un mod, avec une porte persistante qui mène dehors, classées d'après les mots-clés
/// LocType* de leur lieu. N'écrit que le rapport.
/// </summary>
public static class ModCatalogSurvey
{
    internal static readonly HashSet<string> SafeTypes = ["LocTypeHouse", "LocTypePlayerHouse", "LocTypeInn", "LocTypeStore", "LocTypeTemple",
        "LocTypeDwelling", "LocTypeGuild", "LocTypeCastle", "LocTypeFarm", "LocTypeHabitation", "LocTypeTown", "LocTypeCity"];
    internal static readonly HashSet<string> HostileTypes = ["LocTypeDungeon", "LocTypeClearable", "LocTypeBanditCamp", "LocTypeDraugrCrypt",
        "LocTypeFalmerHive", "LocTypeVampireLair", "LocTypeWarlockLair", "LocTypeAnimalDen", "LocTypeForswornCamp",
        "LocTypeDragonPriestLair", "LocTypeHagravenNest", "LocTypeGiantCamp", "LocTypeSprigganGrove", "LocTypeDwarvenAutomatons",
        "LocTypeMilitaryFort", "LocTypeWerewolfLair", "LocTypeMine", "LocTypeJail"];

    public static void Run(Context ctx, string outPath)
    {
        var masters = Options.Masters.Select(m => ModKey.FromFileName(m)).ToHashSet();
        var mods = ctx.Env.LoadOrder.ListedOrder.Where(l => l.Mod != null && l.Enabled && !masters.Contains(l.ModKey)).Select(l => l.Mod!).ToList();
        var r = new StringBuilder();
        r.AppendLine($"Relevé des mods ({DateTime.Now:dd/MM/yyyy HH:mm}) : {mods.Count} plugins hors jeu de base et DLC");

        // ---- sorts : livres de sorts des mods (versions gagnantes), sorts que le catalogue actuel n'a pas
        var known = Options.Spells(ctx).Where(s => s.Mod == null).Select(s => s.Key).ToHashSet();
        var spells = new List<(string Plugin, string School, int Level, string Name, string Edid, string Book)>();
        foreach (var book in ctx.Env.LoadOrder.PriorityOrder.Book().WinningOverrides())
        {
            if (book.Teaches is not IBookSpellGetter teach || known.Contains(teach.Spell.FormKey)) continue;
            if (!ctx.Cache.TryResolve<ISpellGetter>(teach.Spell.FormKey, out var sp)) continue;
            var main = sp.Effects.Select(e => ctx.Cache.TryResolve<IMagicEffectGetter>(e.BaseEffect.FormKey, out var me) ? me : null)
                .FirstOrDefault(me => me != null && me.MagicSkill is ActorValue.Alteration or ActorValue.Conjuration or ActorValue.Destruction
                    or ActorValue.Illusion or ActorValue.Restoration);
            spells.Add((sp.FormKey.ModKey.FileName, main?.MagicSkill.ToString() ?? "(sans école)", (int)(main?.MinimumSkillLevel ?? 0),
                sp.Name?.String ?? "", sp.EditorID ?? "", book.FormKey.ModKey.FileName));
        }
        spells = spells.DistinctBy(s => (s.Plugin, s.Edid)).ToList();
        r.AppendLine();
        r.AppendLine($"=== SORTS de mods enseignés par un livre : {spells.Count} (dans {spells.Select(s => s.Plugin).Distinct().Count()} plugins)");
        foreach (var g in spells.GroupBy(s => s.Plugin).OrderByDescending(g => g.Count()))
        {
            r.AppendLine($"--- {g.Key} : {g.Count()} sorts ({string.Join(", ", g.GroupBy(s => s.School).Select(x => $"{x.Key} {x.Count()}"))})");
            foreach (var s in g.OrderBy(s => s.School).ThenBy(s => s.Level))
                r.AppendLine($"      {s.School,-12} {s.Level,3}  {s.Name}  [{s.Edid}]" + (s.Book != s.Plugin ? $"  (livre : {s.Book})" : ""));
        }

        // ---- lieux : cellules intérieures créées par un mod
        var interiorDoors = new HashSet<FormKey>();
        var cells = new List<(ISkyrimModGetter Mod, ICellGetter Cell)>();
        foreach (var l in ctx.Env.LoadOrder.ListedOrder.Where(l => l.Mod != null && l.Enabled))
            foreach (var cell in l.Mod!.EnumerateMajorRecords<ICellGetter>())
            {
                if (cell.Flags.HasFlag(Cell.Flag.IsInteriorCell) != true) continue;
                foreach (var p in cell.Persistent.OfType<IPlacedObjectGetter>()) if (p.TeleportDestination != null) interiorDoors.Add(p.FormKey);
                if (cell.FormKey.ModKey == l.ModKey && !masters.Contains(l.ModKey)) cells.Add((l.Mod, cell));
            }
        var rows = new List<(string Plugin, string Kind, string Line)>();
        foreach (var (mod, cell) in cells)
        {
            var outside = cell.Persistent.OfType<IPlacedObjectGetter>().Where(p => p.TeleportDestination is { } td && !interiorDoors.Contains(td.Door.FormKey)).ToList();
            if (outside.Count == 0) continue;   // pas de porte vers l'extérieur : cave, étage, cellule de test
            var kws = new List<string>();
            string? locName = null;
            if (ctx.Cache.TryResolve<ICellGetter>(cell.FormKey, out var win) && win.Location.TryResolve(ctx.Cache, out var loc))
            {
                locName = loc.Name?.String;
                foreach (var k in loc.Keywords ?? []) if (k.TryResolve(ctx.Cache, out var kw) && kw.EditorID?.StartsWith("LocType") == true) kws.Add(kw.EditorID!);
            }
            string kind = kws.Any(HostileTypes.Contains) ? "HOSTILE" : kws.Any(SafeTypes.Contains) ? "SUR" : "SANS TYPE";
            var name = (win ?? cell).Name?.String ?? "";
            rows.Add((mod.ModKey.FileName, kind, $"      {kind,-9} {cell.EditorID,-40} {name,-36} {locName}  [{string.Join(",", kws.Select(k => k[7..]))}]"));
        }
        r.AppendLine();
        r.AppendLine($"=== LIEUX : cellules intérieures créées par un mod, avec une porte vers l'extérieur : {rows.Count} " +
            $"(sûres {rows.Count(x => x.Kind == "SUR")}, sans type {rows.Count(x => x.Kind == "SANS TYPE")}, hostiles {rows.Count(x => x.Kind == "HOSTILE")})");
        foreach (var g in rows.GroupBy(x => x.Plugin).OrderByDescending(g => g.Count(x => x.Kind != "HOSTILE")))
        {
            r.AppendLine($"--- {g.Key} : {g.Count(x => x.Kind == "SUR")} sûres, {g.Count(x => x.Kind == "SANS TYPE")} sans type, {g.Count(x => x.Kind == "HOSTILE")} hostiles");
            foreach (var x in g.OrderBy(x => x.Kind)) r.AppendLine(x.Line);
        }
        File.WriteAllText(outPath, r.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"Sorts : {spells.Count}, lieux : {rows.Count} -> {outPath}");
    }
}
