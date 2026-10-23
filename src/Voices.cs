using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Voix (VTYP) choisie dans le Builder pour un follower. Sans choix : voix de presets.csv (déduite de la race au scan,
/// Scanner.DefaultVoice), ESP inchangé. Choix : work\voice_choice.json { "id du preset": "EditorID de la voix" }.
/// Seules les voix génériques du jeu et des DLC sont proposées (pas de voix uniques, d'enfants ni de créatures).
///
/// Dialogue de follower (« Suis-moi », « Attends », échange, renvoi : quête DialogueFollower) : ses répliques ne sont
/// enregistrées que pour les voix des listes VoicesFollowerNeutral, VoicesFollowerRough et VoicesFollowerOrc de
/// Skyrim.esm (15 voix, vérifié dans Skyrim.esm, work\diag\voix\). Une autre voix n'a ces répliques que si un mod de
/// dialogue les ajoute (Relationship Dialogue Overhaul, Nether's Follower Framework…) : on le devine aux listes de voix
/// de follower des plugins actifs, d'où le niveau « mod » (à vérifier en jeu).
/// </summary>
public static class Voices
{
    static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");
    static readonly string[] Masters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];
    static readonly FormKey[] VanillaFollowerLists = [new(Skyrim, 0x0D23AC), new(Skyrim, 0x0D23AE), new(Skyrim, 0x0D8E1E)];
    static readonly FormKey PotentialFollower = new(Skyrim, 0x05C84D);

    /// <summary>Voix proposées : EditorID → nom lisible (traduit à chaque appel).</summary>
    static Dictionary<string, string> Labels() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["FemaleYoungEager"] = L.T("Jeune et enthousiaste"), ["FemaleEvenToned"] = L.T("Posée"), ["FemaleSultry"] = L.T("Sensuelle"),
        ["FemaleCondescending"] = L.T("Condescendante"), ["FemaleCommander"] = L.T("Autoritaire"), ["FemaleDarkElf"] = L.T("Elfe noire"),
        ["FemaleOrc"] = L.T("Orque (femme)"), ["FemaleElfHaughty"] = L.T("Elfe hautaine"), ["FemaleNord"] = L.T("Nordique (femme)"),
        ["FemaleKhajiit"] = L.T("Khajiit (femme)"), ["FemaleArgonian"] = L.T("Argonienne"), ["FemaleCommoner"] = L.T("Roturière"),
        ["FemaleShrill"] = L.T("Criarde"), ["FemaleCoward"] = L.T("Peureuse"), ["FemaleOldGrumpy"] = L.T("Vieille et grincheuse"),
        ["FemaleOldKindly"] = L.T("Vieille et bienveillante"), ["FemaleSoldier"] = L.T("Soldate"), ["DLC1FemaleVampire"] = L.T("Vampire (femme)"),
        ["DLC2FemaleDarkElfCommoner"] = L.T("Elfe noire roturière (Solstheim)"),
        ["MaleYoungEager"] = L.T("Jeune et enthousiaste"), ["MaleEvenToned"] = L.T("Posé"), ["MaleEvenTonedAccented"] = L.T("Posé, avec accent"),
        ["MaleNord"] = L.T("Nordique (homme)"), ["MaleDrunk"] = L.T("Ivrogne"), ["MaleBrute"] = L.T("Brute"), ["MaleDarkElf"] = L.T("Elfe noir"),
        ["MaleOrc"] = L.T("Orque (homme)"), ["MaleElfHaughty"] = L.T("Elfe hautain"), ["MaleKhajiit"] = L.T("Khajiit (homme)"),
        ["MaleArgonian"] = L.T("Argonien"), ["MaleCommander"] = L.T("Commandant"), ["MaleNordCommander"] = L.T("Commandant nordique"),
        ["MaleCommoner"] = L.T("Roturier"), ["MaleCommonerAccented"] = L.T("Roturier, avec accent"), ["MaleCondescending"] = L.T("Condescendant"),
        ["MaleCoward"] = L.T("Peureux"), ["MaleBandit"] = L.T("Bandit"), ["MaleForsworn"] = L.T("Parjure"), ["MaleGuard"] = L.T("Garde"),
        ["MaleSoldier"] = L.T("Soldat"), ["MaleSlyCynical"] = L.T("Rusé et cynique"), ["MaleOldGrumpy"] = L.T("Vieux et grincheux"),
        ["MaleOldKindly"] = L.T("Vieux et bienveillant"), ["MaleWarlock"] = L.T("Sorcier (voix de mage)"), ["DLC1MaleVampire"] = L.T("Vampire (homme)"),
        ["DLC2MaleDarkElfCommoner"] = L.T("Elfe noir roturier (Solstheim)"), ["DLC2MaleDarkElfCynical"] = L.T("Elfe noir cynique (Solstheim)"),
    };

    /// <summary>Dialogue de follower d'une voix : « jeu » (jeu de base), « mod » (ajouté par un plugin actif), « aucun ».
    /// RemovedBy : plugins dont la version gagnante d'une liste du jeu retire cette voix (Missing Follower Dialogue Fix
    /// retire FemaleSultry de VoicesFollowerNeutral chez l'utilisateur).</summary>
    public sealed record Dialogue(string Level, string? Plugins, string? RemovedBy = null);

    /// <summary>Voix d'un sexe : clé = EditorID ; Examples = PNJ uniques du jeu qui l'ont (followers d'abord).</summary>
    public sealed record Voice(string EditorId, FormKey Key, string Name, bool Female, Dialogue Dialogue, string Examples);

    static List<(string Edid, FormKey Key, bool Female)>? catalog;
    static Dictionary<FormKey, string>? examples;
    static DialogueSets? follower, marriage;

    /// <summary>Répliques de mariage (quête RelationshipMarriage) : conditionnées par ces six listes de Skyrim.esm
    /// (33 répliques chacune, work\diag\voix\refs.py) ; leur réunion = VoicesMarriageAll (26 voix, pas de Khajiit).</summary>
    static readonly FormKey[] VanillaMarriageLists =
        [new(Skyrim, 0x0CCD78), new(Skyrim, 0x0CCE3F), new(Skyrim, 0x0CCE4D), new(Skyrim, 0x0CCE4E), new(Skyrim, 0x0CCE4F), new(Skyrim, 0x0CCE50)];

    /// <summary>Voix d'un dialogue : listes du jeu (version de Skyrim.esm), voix ajoutées par des plugins actifs (versions
    /// gagnantes modifiées de ces listes, ou listes d'un mod dont l'EditorID contient le motif), voix retirées par un mod.</summary>
    sealed class DialogueSets
    {
        readonly HashSet<FormKey> vanilla = [];
        readonly Dictionary<FormKey, SortedSet<string>> added = [], removed = [];

        static void Add(Dictionary<FormKey, SortedSet<string>> d, FormKey k, string plugin) =>
            (d.TryGetValue(k, out var s) ? s : d[k] = new(StringComparer.OrdinalIgnoreCase)).Add(plugin);

        public DialogueSets(Context ctx, ISkyrimModGetter? skyrim, FormKey[] lists, string pattern, List<IFormListGetter> winning,
            Dictionary<FormKey, ModKey> winner)
        {
            foreach (var fk in lists)
                if (skyrim?.FormLists.FirstOrDefault(f => f.FormKey == fk) is { } fl)
                {
                    vanilla.UnionWith(fl.Items.Select(i => i.FormKey));
                    // version gagnante sans certaines voix de l'original : le mod qui la modifie les retire de ce dialogue
                    var won = ctx.Cache.ResolveAllContexts<IFormList, IFormListGetter>(fk).First();
                    if (won.ModKey != Skyrim)
                        foreach (var gone in fl.Items.Select(i => i.FormKey).Except(won.Record.Items.Select(i => i.FormKey)))
                            Add(removed, gone, won.ModKey.FileName.String);
                }
            foreach (var rec in winning)
            {
                bool vanillaList = lists.Contains(rec.FormKey);
                if (!vanillaList && (rec.EditorID?.Contains(pattern, StringComparison.OrdinalIgnoreCase) != true
                    || Masters.Contains(rec.FormKey.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase))) continue;
                if (vanillaList && winner[rec.FormKey] == Skyrim) continue;
                foreach (var i in rec.Items) Add(added, i.FormKey, winner[rec.FormKey].FileName.String);
            }
        }

        public Dialogue Of(FormKey voice)
        {
            var rem = removed.TryGetValue(voice, out var r) ? string.Join(", ", r) : null;
            return vanilla.Contains(voice) ? new("jeu", null, rem)
                : added.TryGetValue(voice, out var p) ? new("mod", string.Join(", ", p), rem) : new("aucun", null, rem);
        }
    }

    /// <summary>Calcul unique (Builder : serveur séquentiel) : voix du jeu, listes de dialogue, PNJ d'exemple.</summary>
    static void Load(Context ctx)
    {
        if (catalog != null) return;
        var labels = Labels();
        var masters = ctx.Env.LoadOrder.ListedOrder.Where(l => Masters.Contains(l.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase) && l.Mod != null)
            .Select(l => l.Mod!).ToList();
        catalog = masters.SelectMany(m => m.VoiceTypes).Where(v => v.EditorID != null && labels.ContainsKey(v.EditorID))
            .Select(v => (v.EditorID!, v.FormKey, v.Flags.HasFlag(VoiceType.Flag.Female))).DistinctBy(v => v.FormKey).ToList();

        var skyrim = masters.FirstOrDefault(m => m.ModKey == Skyrim);
        var winning = new List<IFormListGetter>();
        var winner = new Dictionary<FormKey, ModKey>();
        foreach (var fl in ctx.Env.LoadOrder.PriorityOrder.FormList().WinningContextOverrides())
        {
            winning.Add(fl.Record);
            winner[fl.Record.FormKey] = fl.ModKey;
        }
        follower = new(ctx, skyrim, VanillaFollowerLists, "VoicesFollower", winning, winner);
        marriage = new(ctx, skyrim, VanillaMarriageLists, "VoicesMarriage", winning, winner);

        // PNJ uniques et nommés du jeu par voix, followers d'abord
        var byVoice = new Dictionary<FormKey, List<(bool Follower, string Name)>>();
        foreach (var npc in masters.SelectMany(m => m.Npcs))
        {
            if (npc.Voice.IsNull || npc.Name?.String is not { Length: > 0 } name || !npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique)) continue;
            bool isFollower = npc.Factions.Any(f => f.Faction.FormKey == PotentialFollower);
            (byVoice.TryGetValue(npc.Voice.FormKey, out var l) ? l : byVoice[npc.Voice.FormKey] = []).Add((isFollower, name));
        }
        examples = byVoice.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value.OrderByDescending(x => x.Follower).ThenBy(x => x.Name)
            .Select(x => x.Name).Distinct().Take(4)));
    }

    static Dialogue DialogueOf(FormKey voice) => follower!.Of(voice);

    /// <summary>Extrait audio d'une voix du catalogue (WAV, work\voice_samples\, fait une fois) : réplique lue dans
    /// l'archive de voix du jeu (VoiceSample), convertie avec ffmpeg (PATH) ; offline\voice_sample.py avec GF_MOTEUR=python.
    /// Seules les voix du catalogue sont acceptées.</summary>
    public static string Sample(Context ctx, string edid)
    {
        var v = Known(ctx, edid);
        var file = Path.Combine(Context.Dir(Path.Combine(ctx.WorkDir, "voice_samples")), v.Edid + ".wav");
        if (File.Exists(file)) return file;
        if (Context.PythonEngine) Py.Run(ctx, "voice_sample.py", v.Edid, v.Key.ModKey.FileName.String.ToLowerInvariant(), file);
        else VoiceSample.ToWav(VoiceSample.Extract(ctx.DataDir, v.Edid, v.Key.ModKey.FileName.String).Xwm, file);
        return file;
    }

    /// <summary>Joue l'extrait d'une voix du catalogue sur la sortie son de Windows (XAudio2, sans ffmpeg) ; rend sa durée
    /// en secondes. Échoue (exception) sans XAudio2 : la page passe alors par Sample (WAV).</summary>
    public static double Play(Context ctx, string edid)
    {
        var v = Known(ctx, edid);
        return VoiceSample.Play(VoiceSample.Extract(ctx.DataDir, v.Edid, v.Key.ModKey.FileName.String).Xwm);
    }

    static (string Edid, FormKey Key, bool Female) Known(Context ctx, string edid)
    {
        Load(ctx);
        var v = catalog!.FirstOrDefault(x => x.Edid.Equals(edid, StringComparison.OrdinalIgnoreCase));
        if (v.Edid == null) throw new GfException(L.T("Voix inconnue : {0}", edid));
        return v;
    }

    /// <summary>Répliques de follower d'une voix (jeu, mod, aucun).</summary>
    public static Dialogue FollowerOf(Context ctx, FormKey voice) { Load(ctx); return follower!.Of(voice); }

    /// <summary>Répliques de mariage d'une voix (mêmes niveaux que le dialogue de follower).</summary>
    public static Dialogue MarriageOf(Context ctx, FormKey voice) { Load(ctx); return marriage!.Of(voice); }

    /// <summary>Voix finale d'un follower au build : voix choisie dans le Builder, sinon celle de presets.csv.</summary>
    public static FormKey? FinalVoice(Context ctx, PresetRow row)
    {
        Load(ctx);
        var choice = LoadChoices(ctx).GetValueOrDefault(row.Id);
        if (choice != null && catalog!.FirstOrDefault(v => v.Edid.Equals(choice, StringComparison.OrdinalIgnoreCase)) is { Edid: not null } c) return c.Key;
        return ctx.Find<IVoiceTypeGetter>(row.Voix)?.FormKey;
    }

    /// <summary>Voix proposées pour un sexe, du jeu de base au « sans dialogue », puis par nom.</summary>
    public static List<Voice> For(Context ctx, bool female)
    {
        Load(ctx);
        var labels = Labels();
        return catalog!.Where(v => v.Female == female)
            .Select(v => new Voice(v.Edid, v.Key, labels[v.Edid], v.Female, DialogueOf(v.Key), examples!.GetValueOrDefault(v.Key) ?? ""))
            .OrderBy(v => v.Dialogue.Level switch { "jeu" => 0, "mod" => 1, _ => 2 }).ThenBy(v => v.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Dialogue de follower d'une voix donnée par EditorID (voix de presets.csv), null si inconnue.</summary>
    public static Dialogue? DialogueOf(Context ctx, string edid)
    {
        Load(ctx);
        return ctx.Find<IVoiceTypeGetter>(edid) is { } v ? DialogueOf(v.FormKey) : null;
    }

    // ------------------------------------------------------------------ choix

    static string ChoicePath(Context ctx) => Path.Combine(ctx.WorkDir, "voice_choice.json");

    public static Dictionary<string, string> LoadChoices(Context ctx) =>
        File.Exists(ChoicePath(ctx))
            ? new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ChoicePath(ctx)))!, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mémorise la voix d'un preset (null : voix de presets.csv). Refuse une voix hors liste ou de l'autre sexe.</summary>
    public static void SaveChoice(Context ctx, PresetRow row, string? voice)
    {
        if (!string.IsNullOrEmpty(voice) && !For(ctx, row.Female).Any(v => v.EditorId.Equals(voice, StringComparison.OrdinalIgnoreCase)))
            throw new GfException(L.T("Voix inconnue ou d'un autre sexe : {0}", voice));
        var choices = LoadChoices(ctx);
        if (string.IsNullOrEmpty(voice)) choices.Remove(row.Id); else choices[row.Id] = voice;
        File.WriteAllText(ChoicePath(ctx), JsonSerializer.Serialize(choices.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(),
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ build

    /// <summary>Applique la voix choisie (rien sans choix : ESP identique) ; signale au rapport une voix finale sans
    /// dialogue de follower dans le jeu de base.</summary>
    public static void Apply(Context ctx, Npc npc, PresetRow row, string? choice, StringBuilder report)
    {
        Load(ctx);
        if (choice != null)
        {
            var v = For(ctx, row.Female).FirstOrDefault(x => x.EditorId.Equals(choice, StringComparison.OrdinalIgnoreCase));
            if (v != null && ctx.Cache.TryResolve<IVoiceTypeGetter>(v.Key, out var vt))
            {
                npc.Voice.SetTo(vt);
                report.AppendLine($"VOIX    {row.Id}  {v.EditorId} ({v.Key})");
            }
            else report.AppendLine($"ATTENTION {row.Id}  voix {choice} introuvable ou d'un autre sexe : voix de presets.csv gardée ({row.Voix})");
        }
        var d = DialogueOf(npc.Voice.FormKey);
        if (d.RemovedBy != null)
            report.AppendLine($"ATTENTION {row.Id}  voix retirée d'une liste de dialogue de follower du jeu par {d.RemovedBy} : à vérifier en jeu");
        if (d.Level != "jeu")
            report.AppendLine($"ATTENTION {row.Id}  voix sans répliques de follower dans le jeu de base" +
                (d.Level == "mod" ? $" (ajoutées par {d.Plugins})" : " (aucun mod de dialogue ne l'ajoute)"));
    }

    /// <summary>Fiche du Builder : voix du sexe du follower, choix, voix par défaut (presets.csv).</summary>
    public static JsonObject Info(Context ctx, PresetRow row)
    {
        var list = For(ctx, row.Female);
        var choice = LoadChoices(ctx).GetValueOrDefault(row.Id);
        var def = list.FirstOrDefault(v => v.EditorId.Equals(row.Voix, StringComparison.OrdinalIgnoreCase));
        JsonObject Dlg(Dialogue d) => new() { ["niveau"] = d.Level, ["plugins"] = d.Plugins, ["retireePar"] = d.RemovedBy };
        return new JsonObject
        {
            ["voix"] = choice,
            ["defaut"] = row.Voix, ["defautNom"] = def?.Name,
            ["defautDialogue"] = (def?.Dialogue ?? DialogueOf(ctx, row.Voix)) is { } dd ? Dlg(dd) : null,
            ["voixListe"] = new JsonArray(list.Select(v => (JsonNode)new JsonObject
            {
                ["id"] = v.EditorId, ["nom"] = v.Name, ["source"] = v.Key.ModKey.FileName.String, ["exemples"] = v.Examples,
                ["dialogue"] = Dlg(v.Dialogue),
            }).ToArray()),
        };
    }
}
