using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace GenerateFollower;

/// <summary>
/// Lieu de départ : le follower est dans le monde dès le départ (auberge, commerce, temple, palais, place d'une ville) au
/// lieu d'attendre l'invocation. Aucune cellule n'est modifiée : au démarrage de la quête, GF_MainQuest le crée puis le
/// déplace (MoveTo) près d'une porte de Skyrim.esm ou d'un DLC. Les portes à téléportation sont des références persistantes :
/// elles existent même quand la cellule n'est pas chargée. Position = point d'arrivée de la porte d'en face (celui où le
/// joueur apparaît en entrant), donc sur le navmesh ; il est donné en décalage par rapport à la porte, lu dans la version
/// gagnante (un mod qui déplace la porte est pris en compte au build). Ensuite le follower flâne sur place (package par défaut).
/// Pas de grottes, forts ni maisons du joueur. Choix : options_choice.json, "lieu": "&lt;EditorID de cellule&gt;[:dehors]".
/// </summary>
public static partial class Options
{
    const string Outside = ":dehors";

    /// <summary>Cellules intérieures proposées, par groupe (EditorID du jeu). Sans ennemis : pas de catacombes, de Ragged
    /// Flagon, de quartier général du Thalmor, de Midden, ni d'Helgen (détruite au début du jeu).</summary>
    static readonly (string Group, string[] Cells)[] PlaceCells =
    [
        ("Auberges", ["RiverwoodSleepingGiantInn", "WhiterunBanneredMare", "SolitudeWinkingSkeever", "WindhelmCandlehearthHall",
            "WindhelmNewGnisisCornerclub", "RiftenBeeandBarb", "MarkarthSilverBloodInn", "FalkreathDeadMansDrink", "MorthalMoorsideInn",
            "DawnstarWindpeakInn", "WinterholdTheFrozenHearth", "RoriksteadFrostfruitInn", "IvarsteadVilemyrInn",
            "DragonBridgeFourShieldsTavern", "KynesgroveBraidwoodInn", "OldHroldanInn", "NightgateInn", "DLC2RRTheRetchingNetch"]),
        ("Commerces", ["RiverwoodRiverwoodTrader", "WhiterunBelethorsGeneralGoods", "WhiterunWarmaidens", "WhiterunArcadiasCauldron",
            "WhiterunDrunkenHuntsman", "SolitudeRadiantRaiment", "SolitudeBitsandPieces", "SolitudeAngelinesAromatics", "SolitudeFletcher",
            "SolitudeBlackSmith", "WindhelmWhitePhial", "WindhelmSadrisUsedWares", "RiftenPawnedPrawn", "RiftenElgrimsElixirs",
            "RiftenBlacksmith", "MarkarthArnleifandSonsTradingCompany", "MarkarthHagsCure", "FalkreathGrayPineGoods",
            "FalkreathGraveConcoctions", "DawnstarMortarandPestle", "MorthalThaumaturgistsHut"]),
        ("Temples", ["WhiterunTempleofKynareth", "SolitudeTempleoftheDivines", "RiftenTempleofMara", "WindhelmTempleofTalos",
            "MarkarthTempleofDibella", "HighHrothgar"]),
        ("Palais et guildes", ["WhiterunDragonsreach", "SolitudeBluePalace", "WindhelmPalaceoftheKings", "RiftenMistveilKeep",
            "MarkarthUnderStoneKeep", "WhiterunJorrvaskr", "SolitudeBardsCollege", "WinterholdCollegeHallofTheElements"]),
        // maisons du joueur (28/10, demande de l'utilisateur) ; celles de Hearthfire ne sont meublées qu'une fois construites
        ("Maisons du joueur", ["WhiterunBreezehome", "RiftenHoneyside", "MarkarthVlindrelHall", "WindhelmHjerim", "SolitudeProudspireManor",
            "DLC2RRSeverinHouse", "BYOHHouse1Falkreath", "BYOHHouse2Hjaalmarch", "BYOHHouse3Pale"]),
    ];

    /// <summary>Places des villes et villages : devant la porte de cette cellule (côté extérieur).</summary>
    static readonly string[] PlaceOutside =
    [
        "RiverwoodSleepingGiantInn", "WhiterunBanneredMare", "SolitudeWinkingSkeever", "WindhelmCandlehearthHall", "RiftenBeeandBarb",
        "MarkarthSilverBloodInn", "FalkreathDeadMansDrink", "MorthalMoorsideInn", "DawnstarWindpeakInn", "WinterholdTheFrozenHearth",
        "RoriksteadFrostfruitInn", "IvarsteadVilemyrInn", "DragonBridgeFourShieldsTavern", "KynesgroveBraidwoodInn", "DLC2RRTheRetchingNetch",
    ];

    /// <summary>Lieu du catalogue : Anchor = porte (référence persistante du jeu ou d'un mod), décalage du point d'arrivée en
    /// unités, angle en degrés ; Mod = plugin du lieu s'il vient d'un mod (master du pack).</summary>
    public sealed record PlaceChoice(string Id, string Group, string Name, FormKey Anchor, float Dx, float Dy, float Dz, float Angle, string? Mod = null);

    static List<PlaceChoice>? places;
    /// <summary>Lieux du catalogue introuvables chez l'utilisateur (cellule ou porte absente).</summary>
    public static readonly List<string> MissingPlaces = [];

    public static List<PlaceChoice> Places(Context ctx)
    {
        if (places != null) return places;
        var cells = new Dictionary<string, ICellGetter>(StringComparer.OrdinalIgnoreCase);
        // portes de toutes les versions de la cellule : une DLC qui modifie la cellule (HearthFires, Update.esm…) ne reprend
        // pas ses références, elles restent dans Skyrim.esm
        var doors = new Dictionary<string, List<IPlacedObjectGetter>>(StringComparer.OrdinalIgnoreCase);
        var interiorDoors = new HashSet<FormKey>();
        foreach (var m in MasterMods(ctx))   // ordre de chargement : la dernière version d'une cellule l'emporte
            foreach (var cell in m.EnumerateMajorRecords<ICellGetter>())
            {
                if (cell.Flags.HasFlag(Cell.Flag.IsInteriorCell) != true || cell.EditorID == null) continue;
                cells[cell.EditorID] = cell;
                var mine = doors.TryGetValue(cell.EditorID, out var l) ? l : doors[cell.EditorID] = [];
                foreach (var p in cell.Persistent.OfType<IPlacedObjectGetter>())
                    if (p.TeleportDestination != null && interiorDoors.Add(p.FormKey)) mine.Add(p);
            }
        var list = new List<PlaceChoice>();
        MissingPlaces.Clear();
        var entries = PlaceCells.SelectMany(g => g.Cells.Select(c => (Group: GroupName(g.Group), Cell: c, Out: false)))
            .Concat(PlaceOutside.Select(c => (Group: L.T("Villes et villages (dehors)"), Cell: c, Out: true)));
        foreach (var (group, edid, outside) in entries)
        {
            var id = edid + (outside ? Outside : "");
            // porte de la cellule qui mène dehors (et non vers une cave ou un étage), versions gagnantes
            IPlacedObjectGetter? inDoor = null, outDoor = null;
            if (cells.TryGetValue(edid, out var cell))
                foreach (var p in doors[edid].OrderBy(p => p.FormKey.ID))
                {
                    if (!ctx.Cache.TryResolve<IPlacedObjectGetter>(p.FormKey, out var din) || din.TeleportDestination is not { } td
                        || interiorDoors.Contains(td.Door.FormKey) || !ctx.Cache.TryResolve<IPlacedObjectGetter>(td.Door.FormKey, out var dout)
                        || dout.TeleportDestination?.Door.FormKey != din.FormKey || din.Placement == null || dout.Placement == null) continue;
                    (inDoor, outDoor) = (din, dout);
                    break;
                }
            if (cell == null || inDoor == null || outDoor == null) { MissingPlaces.Add(id); continue; }
            // dedans : près de la porte intérieure, au point d'arrivée de la porte extérieure ; dehors : l'inverse
            var (anchor, arrival) = outside ? (outDoor, inDoor.TeleportDestination!) : (inDoor, outDoor.TeleportDestination!);
            var d = arrival.Position - anchor.Placement!.Position;
            var name = cell.Name?.String ?? edid;
            var town = Town(ctx, cell.Location.TryResolve(ctx.Cache, out var loc) ? loc : null);
            name = outside ? L.T("{0}, devant : {1}", town ?? name, name) : town != null && !name.Contains(town) ? $"{name} ({town})" : name;
            if (edid.StartsWith("BYOHHouse", StringComparison.OrdinalIgnoreCase)) name = L.T("{0} (une fois construite)", name);
            list.Add(new(id, group, name, anchor.FormKey, d.X, d.Y, d.Z, (float)(arrival.Rotation.Z * 180 / Math.PI)));
        }
        list.AddRange(ModPlaces(ctx));
        return places = list;
    }

    /// <summary>Lieux des mods du joueur (28/10) : cellules intérieures créées par un plugin actif hors jeu de base et DLC, avec
    /// une porte persistante vers l'extérieur ; dedans, au point d'arrivée de cette porte. Classées d'après les mots-clés
    /// LocType* de leur lieu : sûres (maison, auberge, commerce…), sans type (« à vérifier » : le mod ne dit rien), hostiles
    /// (donjon, camp… : écartées). Groupe = plugin, qui devient master du pack si le lieu est choisi. Identifiant = FormKey
    /// de la cellule (« 000800:Mod.esp »).</summary>
    static List<PlaceChoice> ModPlaces(Context ctx)
    {
        var masters = Masters.Select(m => ModKey.FromFileName(m)).ToHashSet();
        var list = new List<PlaceChoice>();
        foreach (var l in ctx.Env.LoadOrder.ListedOrder.Where(l => l.Mod != null && l.Enabled && !masters.Contains(l.ModKey)))
            foreach (var cell in l.Mod!.EnumerateMajorRecords<ICellGetter>())
            {
                if (cell.FormKey.ModKey != l.ModKey || cell.Flags.HasFlag(Cell.Flag.IsInteriorCell) != true) continue;
                IPlacedObjectGetter? inDoor = null, outDoor = null;
                foreach (var p in cell.Persistent.OfType<IPlacedObjectGetter>().Where(p => p.TeleportDestination != null).OrderBy(p => p.FormKey.ID))
                {
                    if (!ctx.Cache.TryResolve<IPlacedObjectGetter>(p.FormKey, out var din) || din.TeleportDestination is not { } td
                        || !ctx.Cache.TryResolveSimpleContext<IPlacedObjectGetter>(td.Door.FormKey, out var c) || c.Parent?.Record is not ICellGetter parent
                        || parent.Flags.HasFlag(Cell.Flag.IsInteriorCell) || c.Record.TeleportDestination?.Door.FormKey != din.FormKey
                        || din.Placement == null || c.Record.Placement == null) continue;
                    (inDoor, outDoor) = (din, c.Record);
                    break;
                }
                if (inDoor == null || outDoor == null) continue;
                var win = ctx.Cache.TryResolve<ICellGetter>(cell.FormKey, out var w) ? w : cell;
                var kws = new List<string>();
                ILocationGetter? loc = win.Location.TryResolve(ctx.Cache, out var lc) ? lc : null;
                foreach (var k in loc?.Keywords ?? []) if (k.TryResolve(ctx.Cache, out var kw) && kw.EditorID != null) kws.Add(kw.EditorID);
                if (kws.Any(ModCatalogSurvey.HostileTypes.Contains)) continue;
                bool typed = kws.Any(ModCatalogSurvey.SafeTypes.Contains);
                var name = win.Name?.String is { Length: > 0 } n ? n : win.EditorID ?? cell.FormKey.ToString();
                if (loc?.Name?.String is { Length: > 0 } ln && !name.Contains(ln) && !ln.Contains(name)) name = $"{name} ({ln})";
                if (!typed) name = L.T("{0} — à vérifier", name);
                var arrival = outDoor.TeleportDestination!;
                var d = arrival.Position - inDoor.Placement!.Position;
                var plugin = l.ModKey.FileName.String;
                list.Add(new(cell.FormKey.ToString(), L.T("Mod : {0}", plugin), name,inDoor.FormKey, d.X, d.Y, d.Z, (float)(arrival.Rotation.Z * 180 / Math.PI), plugin));
            }
        return list.OrderBy(p => p.Group, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Nom du groupe dans la langue du Builder (textes littéraux : offline\i18n_keys.py les trouve).</summary>
    static string GroupName(string group) => group switch
    {
        "Auberges" => L.T("Auberges"), "Commerces" => L.T("Commerces"), "Temples" => L.T("Temples"),
        "Palais et guildes" => L.T("Palais et guildes"), "Maisons du joueur" => L.T("Maisons du joueur"), _ => group,
    };

    /// <summary>Ville ou village d'un lieu : le lieu lui-même ou son premier parent qui a le mot-clé LocTypeCity, LocTypeTown
    /// ou LocTypeSettlement (null sinon : High Hrothgar n'est dans aucune ville).</summary>
    static string? Town(Context ctx, ILocationGetter? loc)
    {
        for (int depth = 0; loc != null && depth < 5; depth++)
        {
            if ((loc.Keywords ?? []).Any(k => k.TryResolve(ctx.Cache, out var kw) && kw.EditorID is "LocTypeCity" or "LocTypeTown" or "LocTypeSettlement"))
                return loc.Name?.String;
            loc = loc.ParentLocation.TryResolve(ctx.Cache, out var parent) ? parent : null;
        }
        return null;
    }

    static JsonArray PlacesJson(Context ctx) => new(Places(ctx)
        .Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["groupe"] = p.Group, ["nom"] = p.Name, ["source"] = p.Anchor.ModKey.FileName.String, ["mod"] = p.Mod }).ToArray());

    static void SavePlace(Context ctx, PresetRow row, JsonNode? value)
    {
        var id = value?.GetValue<string>();
        if (!string.IsNullOrEmpty(id) && !Places(ctx).Any(p => p.Id == id)) throw new GfException(L.T("Lieu inconnu : {0}", id));
        Set(ctx, row.Id, "lieu", string.IsNullOrEmpty(id) ? null : JsonValue.Create(id));
    }

    /// <summary>Lieu de départ choisi pour un follower (null : invocation seulement). Lieu disparu : signalé au rapport.</summary>
    public static PlaceChoice? PlaceFor(Context ctx, PresetRow row, JsonObject? opts, StringBuilder report)
    {
        if (opts?["lieu"]?.GetValue<string>() is not { } id) return null;
        var p = Places(ctx).FirstOrDefault(x => x.Id == id);
        if (p == null) { report.AppendLine($"ATTENTION {row.Id}  lieu {id} introuvable : invocation seulement"); return null; }
        report.AppendLine($"OPTION  {row.Id}  lieu de départ {id} (porte {p.Anchor}, décalage {p.Dx:0} {p.Dy:0} {p.Dz:0}, angle {p.Angle:0})");
        return p;
    }
}
