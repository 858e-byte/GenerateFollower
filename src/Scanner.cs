using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>Lit tous les .jslot, déduit sexe + race depuis les headparts, écrit work\presets.csv.</summary>
public static class Scanner
{
    static readonly string[] RacePriority =
        ["NordRace", "ImperialRace", "BretonRace", "RedguardRace", "HighElfRace", "WoodElfRace", "DarkElfRace", "OrcRace", "KhajiitRace", "ArgonianRace"];

    public static void Run(Context ctx)
    {
        if (!Directory.Exists(ctx.PresetsDir)) throw new GfException($"Dossier de presets introuvable : {ctx.PresetsDir}");

        var old = File.Exists(ctx.CsvPath) ? PresetTable.Read(ctx.CsvPath).ToDictionary(r => r.Id) : [];
        var playable = ctx.Env.LoadOrder.PriorityOrder.Race().WinningOverrides()
            .Where(r => r.Flags.HasFlag(Race.Flag.Playable) && r.EditorID != null && !r.EditorID.Contains("Vampire", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var playableKeys = playable.Select(r => r.FormKey).ToHashSet();
        Console.WriteLine($"  {playable.Count} races jouables (hors vampires)");

        var rows = new List<PresetRow>();
        var ids = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(ctx.PresetsDir, "*.jslot", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.ChangeExtension(Path.GetRelativePath(ctx.PresetsDir, file), null);
            var id = Hash(rel);
            if (!ids.Add(id)) throw new GfException($"Collision d'identifiant pour {rel}");
            var row = new PresetRow
            {
                Id = id,
                Preset = rel,
                Categorie = rel.Contains(Path.DirectorySeparatorChar) ? rel[..rel.IndexOf(Path.DirectorySeparatorChar)] : "(racine)",
                Nom = CleanName(Path.GetFileName(rel)),
            };
            try { Analyse(ctx, row, Jslot.Load(file), playable, playableKeys); }
            catch (Exception e) when (e is not GfException) { row.Statut = "ERREUR"; row.Details = e.Message; }

            row.Inclure = row.Statut is "OK" or "PARTIEL";
            if (old.TryGetValue(id, out var o) && o.Statut != "ERREUR")
            {   // garde les choix faits à la main dans le CSV
                if (o.Statut == row.Statut) row.Inclure = o.Inclure;   // statut changé : défaut recalculé
                if (o.Nom != "") row.Nom = o.Nom;
                if (o.Sexe != "") row.Sexe = o.Sexe;
                if (o.Race != "") row.Race = o.Race;
                if (o.Voix != "") row.Voix = o.Voix;
            }
            if (row.Statut == "SANS_VISAGE") row.Inclure = false;   // tête invisible en jeu : jamais inclus
            rows.Add(row);
        }

        PresetTable.Write(ctx.CsvPath, rows);
        Console.WriteLine($"\n{rows.Count} presets -> {ctx.CsvPath}");
        foreach (var g in rows.GroupBy(r => r.Statut)) Console.WriteLine($"  {g.Key,-15} {g.Count()}");
        Console.WriteLine($"  inclus          {rows.Count(r => r.Inclure)}");
        foreach (var g in rows.GroupBy(r => r.Race).OrderByDescending(g => g.Count())) Console.WriteLine($"  race {g.Key,-28} {g.Count()}");
        if (!ctx.ActivePlugins.Contains("UIExtensions.esp")) Console.WriteLine("ATTENTION : UIExtensions.esp n'est pas actif (menu d'invocation).");
    }

    static void Analyse(Context ctx, PresetRow row, Jslot j, List<IRaceGetter> playable, HashSet<FormKey> playableKeys)
    {
        var missing = j.Mods.Where(m => !ctx.ActivePlugins.Contains(m)).Distinct().ToList();
        var notes = new List<string>();

        // Sexe : flags Male/Female du headpart "face", sinon des autres headparts
        bool? female = null;
        var resolved = new List<(IHeadPartGetter Hp, int Type)>();
        foreach (var (key, type) in j.HeadParts)
            if (ctx.TryResolveRm<IHeadPartGetter>(key, out var hp)) resolved.Add((hp, type));
        foreach (var (hp, type) in resolved.OrderByDescending(x => x.Type == 1))
        {
            bool m = hp.Flags.HasFlag(HeadPart.Flag.Male), f = hp.Flags.HasFlag(HeadPart.Flag.Female);
            if (m != f) { female = f; break; }
        }
        if (female == null)
        {
            female = !row.Preset.StartsWith("Male", StringComparison.OrdinalIgnoreCase);
            notes.Add("sexe deviné");
        }
        row.Sexe = female.Value ? "F" : "M";

        // Race : chaque headpart vote pour les races de sa liste ValidRaces (3 pts),
        // la texture de tête départage (voir Score), puis l'ordre RacePriority.
        var votes = new Dictionary<FormKey, int>();
        int voters = 0;
        foreach (var (hp, _) in resolved)
        {
            if (hp.ValidRaces.IsNull || !ctx.Cache.TryResolve<IFormListGetter>(hp.ValidRaces.FormKey, out var list)) continue;
            var set = list.Items.Select(i => i.FormKey).Where(playableKeys.Contains).ToHashSet();
            if (set.Count == 0) continue;
            voters++;
            foreach (var k in set) votes[k] = votes.GetValueOrDefault(k) + 1;
        }
        FormKey? tex = j.HeadTexture is { } t && ctx.TryResolveRm<ITextureSetGetter>(t, out var ts) ? ts.FormKey : null;
        var best = playable.OrderByDescending(r => 3 * votes.GetValueOrDefault(r.FormKey) + Score(r, tex, female.Value))
            .ThenBy(r => PriorityOf(r.EditorID!))
            .ThenBy(r => r.EditorID)
            .FirstOrDefault();
        row.Race = best?.EditorID ?? ctx.Config.DefaultRace;
        if (voters == 0 && tex == null) notes.Add("race par défaut");
        else if (best != null && votes.GetValueOrDefault(best.FormKey) < voters) notes.Add("race à vérifier");

        row.Voix = DefaultVoice(ctx, row.Race, female.Value);

        if (missing.Count > 0) { row.Statut = "MODS_MANQUANTS"; notes.Insert(0, "manque : " + string.Join(", ", missing)); }
        else if (!resolved.Any(r => r.Hp.Type == HeadPart.TypeEnum.Face))
        {
            // Pas de visage résolu : le FaceGen n'aurait pas de tête (invisible en jeu)
            row.Statut = "SANS_VISAGE";
            notes.Insert(0, "aucun visage parmi les headparts résolus ; introuvables : "
                + string.Join(", ", j.HeadParts.Select(h => h.Key).Where(k => !ctx.TryResolveRm<IHeadPartGetter>(k, out _))));
        }
        else if (resolved.Count < j.HeadParts.Count)
        {
            // Plugin présent mais FormID disparu (plugin compacté) : RaceMenu l'ignore aussi en jeu,
            // la tête exportée et le PNJ auront donc les mêmes headparts.
            row.Statut = "PARTIEL";
            notes.Insert(0, "headparts ignorés : " + string.Join(", ", j.HeadParts.Select(h => h.Key).Where(k => !ctx.TryResolveRm<IHeadPartGetter>(k, out _))));
        }
        else row.Statut = "OK";
        row.Details = string.Join(" | ", notes);
    }

    static int Score(IRaceGetter race, FormKey? headTexture, bool female)
    {
        int s = 0;
        if (headTexture is { } tex)
        {
            var hd = female ? race.HeadData?.Female : race.HeadData?.Male;
            if (hd != null && (hd.DefaultFaceTexture.FormKey == tex || hd.FaceDetails.Any(f => f.FormKey == tex))) s += 10;
            if (race.FormKey.ModKey == tex.ModKey && tex.ModKey != ModKey.FromFileName("Skyrim.esm")) s += 5;
        }
        return s;
    }

    /// <summary>Rang dans RacePriority, en acceptant les variantes préfixées (ex. 00UBE_NordRace).</summary>
    static int PriorityOf(string edid)
    {
        for (int i = 0; i < RacePriority.Length; i++)
            if (edid.EndsWith(RacePriority[i], StringComparison.OrdinalIgnoreCase)) return i;
        return 99;
    }

    public static string DefaultVoice(Context ctx, string raceEdid, bool female)
    {
        string sx = female ? "Female" : "Male";
        string? v = raceEdid switch
        {
            var r when r.Contains("Khajiit") => sx + "Khajiit",
            var r when r.Contains("Argonian") => sx + "Argonian",
            var r when r.Contains("Orc") => sx + "Orc",
            var r when r.Contains("DarkElf") => sx + "DarkElf",
            var r when r.Contains("HighElf") => sx + "ElfHaughty",
            _ => null,
        };
        return v != null && ctx.Find<IVoiceTypeGetter>(v) != null ? v : (female ? ctx.Config.VoiceFemale : ctx.Config.VoiceMale);
    }

    internal static string CleanName(string file)
    {
        var n = file.TrimStart('!', '_', ' ').Replace('_', ' ').Trim();
        return n.Length == 0 ? file : n;
    }

    /// <summary>Identifiant stable (FNV-1a 32 bits du chemin relatif), sert de nom de fichier d'export.</summary>
    static string Hash(string rel)
    {
        uint h = 2166136261;
        foreach (var c in rel.ToLowerInvariant()) { h ^= c; h *= 16777619; }
        return h.ToString("X8");
    }
}
