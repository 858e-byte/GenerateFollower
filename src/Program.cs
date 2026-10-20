using GenerateFollower;

// sans argument (double-clic sur GenerateFollower.exe) : l'application, Follower Builder dans sa fenêtre
var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "app";
try
{
    switch (cmd)
    {
        case "scan":
            using (var ctx = Context.Open()) Scanner.Run(ctx);
            break;
        case "exporter":
            using (var ctx = Context.Open()) ExporterBuilder.Run(ctx);
            break;
        case "build":
            using (var ctx = Context.Open())
            {
                // build [--offline] [--only id1,id2] [--zip nom] [--heads <dossier>] [--diag] [--from-export] [--pack <nom>]
                // --pack <nom> : plugin GF_<nom>.esp, ZIP GF_<nom>.zip, pouvoir « Invoquer : <nom> »
                string? Opt(string name) { int i = Array.IndexOf(args, name); return i > 0 && i + 1 < args.Length ? args[i + 1] : null; }
                if (Opt("--heads") is { } heads) ctx.HeadsOverride = heads;
                var only = Opt("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var pack = Opt("--pack");
                var pluginName = pack != null ? FollowerBuilder.PluginFor(pack) : null;
                FollowerBuilder.Run(ctx, args.Contains("--offline"), only, Opt("--zip") ?? (pluginName != null ? Path.GetFileNameWithoutExtension(pluginName) : "GenerateFollower"),
                    args.Contains("--diag"), args.Contains("--from-export"), pluginName, pack);
            }
            break;
        case "heads":
            // heads [--only id1,id2] : (re)génère seulement les têtes hors jeu (work\heads_offline), sans ESP ni ZIP
            using (var ctx = Context.Open())
            {
                int i = Array.IndexOf(args, "--only");
                var ids = i > 0 && i + 1 < args.Length ? args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
                OfflineHeads.Generate(ctx, PresetTable.Read(ctx.CsvPath).Where(r => ids == null ? r.Inclure : ids.Contains(r.Id)).ToList());
            }
            break;
        case "imports":
            // imports : tête RaceMenu associée à chaque preset inclus (Import Head), pour contrôle
            using (var ctx = Context.Open())
            {
                HeadImports.RefreshIndex(ctx);
                foreach (var r in PresetTable.Read(ctx.CsvPath).Where(r => r.Inclure))
                {
                    var j = Jslot.Load(Path.Combine(ctx.PresetsDir, r.Preset + ".jslot"));
                    var c = HeadImports.Resolve(ctx, r, HeadParts.Effective(ctx, j, ctx.Find<Mutagen.Bethesda.Skyrim.IRaceGetter>(r.Race), r.Female));
                    Console.WriteLine($"{r.Id}  {r.Preset,-50} -> {c.Path ?? "-"}  ({c.Reason})");
                }
            }
            break;
        case "diag":
            // Rapport de diagnostic (bêta) : dist\GF_Diagnostic_<date>.zip
            using (var ctx = Context.Open())
            {
                var (zip, text) = SupportReport.Create(ctx);
                Console.WriteLine(text);
                Console.WriteLine("-> " + zip);
            }
            break;
        case "app":
            // Follower Builder dans sa propre fenêtre (Edge en mode application) ; fermer la fenêtre arrête tout
            using (var ctx = Context.Open()) return AppWindow.Run(ctx);
        case "serve":
            // Follower Builder : application locale, serve [port] (5180 par défaut)
            using (var ctx = Context.Open()) Builder.Serve(ctx, args.Length > 1 && int.TryParse(args[1], out var port) ? port : 5180);
            break;
        case "patchnif":
            // Test : patchnif <source.nif> <cible.nif> <nouveau chemin dds>
            File.WriteAllBytes(args[2], NifPatcher.PatchTintPath(File.ReadAllBytes(args[1]), args[3], out var n));
            Console.WriteLine($"{n} chemin(s) remplacé(s)");
            break;
        case "inspect":
            // Debug : relit un ESP généré
            Lookup.Inspect(args[1]);
            break;
        case "headparts":
            // Debug : headparts <preset relatif> [race EditorID] -> JSON
            using (var ctx = Context.Open()) Lookup.HeadParts(ctx, args[1], args.Length > 2 ? args[2] : null);
            break;
        case "checkhp":
            // Debug : checkhp <preset relatif> <race EditorID> [F|M] -> validité race/sexe des headparts
            using (var ctx = Context.Open()) Lookup.CheckHeadParts(ctx, args[1], args[2], args.Length < 4 || args[3] != "M");
            break;
        case "tints":
            // Debug : tints <race EditorID>
            using (var ctx = Context.Open()) Lookup.Tints(ctx, args[1]);
            break;
        case "lookup":
            // Debug : lookup <TypeMutagen> <filtre EditorID>
            using (var ctx = Context.Open()) Lookup.Run(ctx, args[1], args.Length > 2 ? args[2] : "");
            break;

        case "skins":
            // skins [--refresh] [id du preset] : catalogue des skins (compatibles avec ce preset)
            using (var ctx = Context.Open())
            {
                var skins = Skins.Load(ctx, args.Contains("--refresh"));
                var id = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
                var row = id == null ? null : PresetTable.Read(ctx.CsvPath).FirstOrDefault(r => r.Id == id) ?? throw new GfException($"preset {id} inconnu");
                if (row != null) Console.WriteLine($"{row.Nom} : corps {Skins.BodyLabel(Skins.BodyOf(ctx, row))} {row.Sexe}, skin choisi : {Skins.LoadChoices(ctx).GetValueOrDefault(row.Id) ?? "peau du jeu"}");
                foreach (var s in row == null ? skins.All : skins.For(row))
                    Console.WriteLine($"{s.Id}  {s.Body,-5} {s.Sex}  {s.Source,-7}  {s.Name}  [{string.Join(",", s.Files.Keys)}]");
            }
            break;
        case "skin":
            // skin <id du preset> <id du skin | aucun> : mémorise le choix (work\skins_choice.json)
            using (var ctx = Context.Open())
            {
                var row = PresetTable.Read(ctx.CsvPath).FirstOrDefault(r => r.Id == args[1]) ?? throw new GfException($"preset {args[1]} inconnu");
                var skin = args[2] == "aucun" ? null : Skins.Load(ctx).For(row).FirstOrDefault(s => s.Id == args[2]) ?? throw new GfException($"skin {args[2]} inconnu ou incompatible avec {row.Nom}");
                Skins.SaveChoice(ctx, row.Id, skin?.Id);
                Console.WriteLine($"{row.Nom} : {skin?.Name ?? "peau du jeu"}");
            }
            break;

        case "overrides":
            // Debug : overrides <EditorID de race> -> plugins qui la modifient, peau et textures
            using (var ctx = Context.Open()) Lookup.Overrides(ctx, args[1]);
            break;
        default:
            Console.WriteLine("""
                GenerateFollower - génère des followers Skyrim SE depuis les presets RaceMenu

                  scan      lit les presets, déduit race/sexe, écrit work\presets.csv
                  exporter  construit dist\GF_Exporter.zip (quête d'export des têtes en jeu)
                  build     construit dist\GenerateFollower.zip (ESP ESL + FaceGen + scripts)
                            --offline           têtes générées hors jeu, sans export RaceMenu
                            --only id1,id2      lot de test ; --zip nom : nom du ZIP
                  app       (sans argument) Follower Builder dans sa propre fenêtre
                  diag      rapport de diagnostic (dist\GF_Diagnostic_<date>.zip) à joindre à un retour de bêta
                  serve     Follower Builder : inspection et validation des presets, http://localhost:5180
                """);
            return 1;
    }
    return 0;
}
catch (GfException e)
{
    Console.Error.WriteLine("ERREUR : " + e.Message);
    return 2;
}
