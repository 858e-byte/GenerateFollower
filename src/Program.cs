using GenerateFollower;

// filet de sécurité : toute erreur imprévue écrit crash.txt et s'affiche (CrashGuard.cs)
CrashGuard.Install(args);
// sans argument (double-clic sur GenerateFollower.exe) : l'application, Follower Builder dans sa fenêtre
var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "app";
// exe sans console (application Windows) : les commandes en ligne écrivent dans la console d'où on les lance
if (cmd != "app") ParentConsole.Attach();
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
                    var j = Jslot.Load(ctx.PresetPath(r.Preset));
                    var c = HeadImports.Resolve(ctx, r, HeadParts.Effective(ctx, j, ctx.Find<Mutagen.Bethesda.Skyrim.IRaceGetter>(r.Race), r.Female));
                    Console.WriteLine($"{r.Id}  {r.Preset,-50} -> {c.Path ?? "-"}  ({c.Reason})");
                }
            }
            break;
        case "outils":
            // outils <nom> <arguments…> : outils C# qui remplacent les scripts de offline\, même sortie (développeur :
            // comparaison avec work\diag\autonome_dotnet\golden). Listes : fichier JSON comme pour les scripts.
            using (var ctx = Context.Open())
            {
                List<string> ListOf(string f) => System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(f))!;
                HeadGen? heads = null;
                static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i > 0 && i + 1 < a.Length ? a[i + 1] : null; }
                // outils lot <travaux.json> <sortie.json> : [[nom, arguments…], …] dans un seul processus (le chargement de
                // l'ordre de chargement est long) ; sortie : un résultat texte par travail, ou « ERREUR : … »
                if (args[1] == "lot")
                {
                    var jobs = System.Text.Json.JsonSerializer.Deserialize<List<string[]>>(File.ReadAllText(args[2]))!;
                    var results = new System.Text.Json.Nodes.JsonArray();
                    foreach (var job in jobs)
                        try { results.Add(Run(job)); }
                        catch (Exception e) when (e is not OutOfMemoryException) { results.Add("ERREUR : " + e.Message); }
                    File.WriteAllText(args[3], results.ToJsonString());
                    Console.WriteLine($"{jobs.Count} travaux");
                    break;
                }
                Console.WriteLine(Run(args[1..]));
                string Run(string[] a) => a[0] switch
                {
                    "find_files" => NativeTools.FindFiles(ctx, ListOf(a[1])).ToJsonString(),
                    "nif_shapes" => NativeTools.NifShapes(ctx, ListOf(a[1])).ToJsonString(),
                    "head_shader" => NativeTools.HeadShader(a[1]).ToJsonString(),
                    "export_textures" => NativeTools.ExportTextures(ctx, a[1]).ToJsonString(),
                    "export_mesh" => Write(a[2], NativeTools.ExportMesh(a[1]).ToJsonString()),
                    "compare_heads" => NativeTools.CompareHeads(a[1], a[2]).ToJsonString(),
                    "index_heads" => NativeTools.IndexHeads([a[1], .. a[3..]], a[2]),
                    "mods_index" => NativeTools.ModsIndex(a[1], a[2]),
                    "copy_assets" => NativeTools.CopyAssets(ctx, ListOf(a[1]), a[2], Opt(a, "--sous-dossier"), Opt(a, "--manifeste")),
                    // pixels avant compression (comparaison au bit près avec les scripts) : MD5 des octets RGBA
                    "tint_rgba" => Md5(new Dds.Image(int.Parse(a[2]), int.Parse(a[2]), TintBytes(ctx, a[1], int.Parse(a[2])))),
                    "bake_rgba" => Md5(NativeTools.BakePixels(ctx, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(a[1]))!.AsObject()).Image),
                    "tint_make" => Tint.Make(ctx, a[1], a[2], a[3]),
                    // skins_index <mods_index.json> <dossier des mods> <dossier des archives> <7z.exe|-> <sortie.json> <F> <M> <Erin>
                    "skins_index" => SkinsIndex.Build(a[1], a[2], a[3], a[4] == "-" ? null : a[4], a[5], a[6], a[7], a[8]),
                    // archive_list <archive> : .dds listés par SharpCompress (JSON [[chemin, taille]])
                    "archive_list" => new System.Text.Json.Nodes.JsonArray(SkinsIndex.ListDds(a[1], null).Select(d => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonArray(d.Path, d.Size)).ToArray()).ToJsonString(),
                    "archive_extract" => SkinsIndex.Extract(a[1], a[2], a[3]) ?? "ok",
                    // voix : voice_xwm <EditorID> <plugin> <sortie.xwm> | voice_wav … <sortie.wav> | voice_play <EditorID> <plugin>
                    "voice_xwm" => Done2(() => { var (pick, x) = VoiceSample.Extract(ctx.DataDir, a[1], a[2]); File.WriteAllBytes(a[3], x); return pick; }),
                    "voice_wav" => Done(() => VoiceSample.ToWav(VoiceSample.Extract(ctx.DataDir, a[1], a[2]).Xwm, a[3])),
                    "voice_play" => Done2(() =>
                    {
                        var d = VoiceSample.Play(VoiceSample.Extract(ctx.DataDir, a[1], a[2]).Xwm);
                        // compté pendant la lecture : XAudio2 remet le compteur à zéro à la fin de l'extrait
                        Thread.Sleep(300);
                        return $"durée {d:F2} s, {VoiceSample.SamplesPlayed} échantillons joués après 0,3 s";
                    }),
                    // head_mesh <preset> <race> <fiche headparts.json> <chemin de teinte> <sortie.json> : tête générée (aperçu)
                    "head_mesh" => Write(a[5], (heads ??= new HeadGen(ctx)).Mesh(a[1], a[2], a[3], a[4]).ToJsonString()),
                    "dds_png" => Done(() => NativeTools.DdsPng(ctx, a[1], a[2], int.Parse(a[3]))),
                    "dds_rgba" => DdsDigest(a[1..].Select(f => Path.IsPathRooted(f) ? f : ctx.FindFile(f) ?? f).ToArray()),
                    _ => throw new GfException("outil inconnu : " + a[0]),
                };
                static string Write(string path, string text) { File.WriteAllText(path, text); return "ok"; }
                static string Done(Action act) { act(); return "ok"; }
                static string Done2(Func<string> act) => act();
                static string Md5(Dds.Image img) => $"{img.Width}\t{img.Height}\t{Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(img.Rgba))}";
                static byte[] TintBytes(Context ctx, string jslot, int size)
                {
                    var rgb = Tint.Compose(ctx, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(jslot))!, size);
                    var px = new byte[size * size * 4];
                    for (int i = 0; i < size * size; i++)
                    {
                        for (int k = 0; k < 3; k++) px[i * 4 + k] = (byte)(Math.Clamp(rgb[i * 3 + k], 0, 1) * 255 + 0.5);
                        px[i * 4 + 3] = 255;
                    }
                    return px;
                }
                // dds_rgba <fichier…> : largeur, hauteur et MD5 des pixels RGBA décodés (comparaison avec texconv)
                static string DdsDigest(string[] files) => string.Join("\n", files.Select(f =>
                {
                    try { var d = Dds.Decode(f); return $"{f}\t{d.Width}\t{d.Height}\t{Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(d.Rgba))}"; }
                    catch (GfException e) { return $"{f}\terreur\t{e.Message}"; }
                }));
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
            // Follower Builder dans sa propre fenêtre (WebView2) ; fermer la fenêtre arrête tout.
            // app --navigateur : dans le navigateur par défaut (Linux/Proton, ou si la fenêtre ne s'ouvre pas)
            return AppWindow.Run(args.Contains("--navigateur") || args.Contains("--browser"));
        case "serve":
            // Follower Builder : application locale, serve [port] (5180 par défaut)
            using (var ctx = Context.Open()) Builder.Serve(ctx, args.Length > 1 && int.TryParse(args[1], out var port) ? port : 5180);
            break;
        case "vdata":
            // Diagnostic : vdata [chemin relatif…] -> racines du Data virtuel (MO2 lu directement), archives, et pour chaque
            // chemin le fichier gagnant et sa racine
            using (var ctx = Context.Open())
            {
                var f = ctx.Files;
                Console.WriteLine($"mode {f.Mode}, {f.Roots.Count} racines : " + string.Join(" | ", f.Roots.Take(8).Select(r => r.Name)) + (f.Roots.Count > 8 ? " | …" : ""));
                var order = ctx.Env.LoadOrder.ListedOrder.Select(l => l.ModKey.FileName.String).Where(ctx.ActivePlugins.Contains).ToList();
                Console.WriteLine($"plugins actifs {order.Count} ; BSA : {f.Archives(order)?.Count.ToString() ?? "(règle de Data)"}");
                // fichier des scripts Python (GF_VDATA), pour les essayer à la main
                if (f.Direct) f.WriteForPython(Path.Combine(ctx.WorkDir, "vdata.json"), order);
                // banc d'essai : vdata --oracle <gagnants.json> -> mod gagnant de chaque fichier comparé à celui de Vortex
                if (args.Length > 2 && args[1] == "--oracle")
                {
                    var expected = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[2]))!;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var wrong = expected.Where(kv => !string.Equals(f.Owner(kv.Key), kv.Value, StringComparison.OrdinalIgnoreCase)).Take(100000).ToList();
                    Console.WriteLine($"oracle : {expected.Count - wrong.Count}/{expected.Count} gagnants identiques en {watch.Elapsed.TotalSeconds:F1} s");
                    foreach (var kv in wrong.Take(10)) Console.WriteLine($"  {kv.Key} : attendu {kv.Value}, trouvé {f.Owner(kv.Key) ?? "-"}");
                    break;
                }
                foreach (var rel in args.Skip(1))
                {
                    Console.WriteLine($"{rel} -> {f.Owner(rel) ?? "-"} : {f.Find(rel) ?? "introuvable (loose)"}");
                    // plugin : chargé par Mutagen ? nombre d'enregistrements, et un enregistrement résolu par le cache
                    if (!rel.Contains('\\') && Mutagen.Bethesda.Plugins.ModKey.TryFromFileName(rel, out var key))
                    {
                        var mod = ctx.Env.LoadOrder.ListedOrder.FirstOrDefault(l => l.ModKey == key)?.Mod;
                        var first = mod?.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey.ModKey == key);
                        Console.WriteLine(mod == null ? "  pas chargé"
                            : $"  chargé : {mod.EnumerateMajorRecords().Count()} enregistrements ; {first?.EditorID ?? first?.FormKey.ToString()} résolu : {first != null && ctx.Cache.TryResolve(first.FormKey, first.Registration.GetterType, out _)}");
                    }
                }
            }
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
        case "releve-mods":
            // Debug, lecture seule : sorts et lieux que les mods ajouteraient aux catalogues -> work\diag\releve_mods.txt
            using (var ctx = Context.Open()) ModCatalogSurvey.Run(ctx, Path.Combine(ctx.WorkDir, "diag", "releve_mods.txt"));
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
        case "combat":
            // combat : liste des styles et classes ; combat <id du preset> <style | aucun> [classe | aucun] : mémorise le choix
            // (work\combat_choice.json, sans argument de classe : classe déjà choisie gardée)
            using (var ctx = Context.Open())
            {
                if (args.Length < 2)
                {
                    foreach (var s in Combat.Styles())
                    {
                        var pr = Combat.Profile(ctx, s.Key);
                        var kind = pr is { } q ? (Combat.IsRanged(q) ? "distance " : "") + (Combat.IsMagic(q) ? "magie" : "") : "?";
                        Console.WriteLine($"style   {s.Key,-28} {s.Group,-16} {s.Name}  [{pr?.Melee:0.##}/{pr?.Magic:0.##}/{pr?.Ranged:0.##}] {kind}");
                    }
                    foreach (var c in Combat.Classes()) Console.WriteLine($"classe  {c.EditorId,-28} {c.Group,-16} {c.Name}");
                    break;
                }
                var row = PresetTable.Read(ctx.CsvPath).FirstOrDefault(r => r.Id == args[1]) ?? throw new GfException($"preset {args[1]} inconnu");
                string? Arg(string a) => a == "aucun" ? null : a;
                var old = Combat.LoadChoices(ctx).GetValueOrDefault(row.Id);
                // combat <id> seul : affiche le choix sans le changer
                if (args.Length > 2) Combat.SaveChoice(ctx, row.Id, Arg(args[2]), args.Length > 3 ? Arg(args[3]) : old?.Classe);
                var now = Combat.LoadChoices(ctx).GetValueOrDefault(row.Id);
                Console.WriteLine($"{row.Nom} : style {now?.Style ?? "par défaut"}, classe {now?.Classe ?? ctx.Config.FollowerClass + " (config.json)"}");
            }
            break;

        case "options":
            // options [id] : catalogues des options (tenues…) ; avec un id, ses choix (work\options_choice.json)
            using (var ctx = Context.Open())
            {
                if (args.Length > 1 && args[1] == "archetypes")
                {
                    foreach (var a in Options.Archetypes())
                        Console.WriteLine($"archétype {a.Key,-22} {(Options.CheckArchetype(ctx, a) is { Count: > 0 } bad ? "INCOMPLET : " + string.Join(", ", bad) : "OK")}");
                    break;
                }
                if (args.Length > 1) { Console.WriteLine(Options.For(ctx, args[1]).ToJsonString()); break; }
                foreach (var o in Options.Outfits(ctx)) Console.WriteLine($"tenue   {o.EditorId,-36} {o.Group,-18} {o.Name}  [{o.Items}]");
                foreach (var w in Options.Weapons(ctx)) Console.WriteLine($"arme    {w.EditorId,-36} {w.Group,-18} {w.Name} x{w.Count}  ({w.Key})");
                foreach (var w in Options.MissingWeapons) Console.WriteLine($"arme    ABSENTE {w}");
                foreach (var p in Options.ArmorPieces(ctx)) Console.WriteLine($"piece   {p.Id,-44} {p.Slot,-10} {p.Type,-10} {p.Name}  ({p.Key})");
                foreach (var p in Options.Perks(ctx)) Console.WriteLine($"atout   {p.EditorId,-36} {p.Skill,-14} {p.Ranks}  {p.Name}  ({p.Key})");
                foreach (var s in Options.Spells(ctx)) Console.WriteLine($"sort    {s.EditorId,-36} {s.School,-12} {s.Level,3}  {s.Name}  ({s.Key})");
                foreach (var p in Options.Places(ctx)) Console.WriteLine($"lieu    {p.Id,-44} {p.Group,-28} {p.Name}  (porte {p.Anchor}, {p.Dx:0} {p.Dy:0} {p.Dz:0}, {p.Angle:0}°)");
                foreach (var p in Options.MissingPlaces) Console.WriteLine($"lieu    ABSENT {p}");
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
                  combat    styles de combat et classes ; combat <id> <style|aucun> [classe|aucun] : choix d'un follower
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
