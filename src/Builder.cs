using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Follower Builder : application locale (http://localhost:5180) pour inspecter chaque preset hors jeu,
/// comme dans le Creation Kit : headparts et validité pour la race, défauts de race, aperçu 3D de la tête,
/// vérification du FaceGen, puis validation et ZIP des followers validés.
/// Lit le dossier du jeu ; n'écrit que dans le projet (work\viewer, work\validation.json, out\, dist\).
/// </summary>
public static partial class Builder
{
    public static void Serve(Context ctx, int port = 5180)
    {
        var prefix = $"http://localhost:{port}/";
        var web = Path.Combine(AppResources.Dir(ctx.Root), "builder");
        var cache = Context.Dir(Path.Combine(ctx.WorkDir, "viewer"));
        // presets et exports à jour dès l'ouverture (nouveaux jslot, Export Head faits depuis le dernier scan)
        Console.WriteLine(L.T("Scan des presets et index des têtes RaceMenu de CharGen..."));
        Rescan(ctx);
        UpdateCheck.Start(ctx);   // en arrière-plan : dernière version publiée (désactivable dans les Réglages)
        using var http = new HttpListener();
        http.Prefixes.Add(prefix);
        http.Start();
        Console.WriteLine(L.T("Follower Builder : {0}  (Ctrl+C pour arrêter)", prefix));
        while (http.IsListening)
        {
            var c = http.GetContext();
            try { Handle(ctx, c, web, cache); }
            catch (Exception e)
            {
                Console.WriteLine(L.T("ERREUR {0} : {1}", c.Request.Url?.AbsolutePath, e.Message));
                // joint au rapport de diagnostic ; pas les cas normaux affichés par l'interface (texture absente en violet,
                // pas d'écart mesuré sans tête générée)
                if (!System.Text.RegularExpressions.Regex.IsMatch(c.Request.Url?.AbsolutePath ?? "", "^/api/(texture|skin-texture|compare)"))
                    SupportReport.LogError(ctx, $"{c.Request.Url?.AbsolutePath} : {e}");
                try { Send(c, 500, new JsonObject { ["erreur"] = e.Message }); } catch { c.Response.Abort(); }
            }
        }
    }

    /// <summary>Scan des presets (presets.csv, noms en jeu conservés) et index des exports de CharGen ; renvoie le
    /// journal du scan.</summary>
    static string Rescan(Context ctx)
    {
        var log = new StringWriter();
        var console = Console.Out;
        Console.SetOut(log);   // serveur séquentiel : une requête à la fois
        try
        {
            // presets introuvables (pas de RaceMenu, jeu introuvable) : le Builder s'ouvre quand même, problème affiché
            scanProblem = null;
            try { Scanner.Run(ctx); HeadImports.RefreshIndex(ctx); }
            catch (GfException e)
            {
                scanProblem = e.Message + "\n" + L.T("Installe RaceMenu (presets dans Data\\SKSE\\Plugins\\CharGen\\Presets) ou indique le dossier Data de Skyrim dans les Réglages, puis « Rescanner ».");
                Console.WriteLine(L.T("ERREUR : {0}", e.Message));
                // première fois : liste vide ; une liste existante (jeu déplacé un moment) est gardée telle quelle
                if (!File.Exists(ctx.CsvPath)) PresetTable.Write(ctx.CsvPath, []);
            }
            mods = ModIndex.Load(ctx, refresh: true);
            jslotHashes.Clear();
        }
        finally { Console.SetOut(console); }
        return log.ToString();
    }

    /// <summary>Scan impossible au dernier « Rescanner » (dossier de presets introuvable…), sinon null.</summary>
    static string? scanProblem;

    /// <summary>Mods du gestionnaire (textures, plugins) : réparations proposées et mods manquants.</summary>
    static ModIndex? mods;

    /// <summary>Catalogue des skins (chargé à la première demande, refait par « Chercher les skins »).</summary>
    static Skins? skins;

    /// <summary>Empreinte MD5 des jslot (chemin → date, empreinte) : presets identiques (jslot écrasé par un autre).</summary>
    static readonly Dictionary<string, (DateTime Date, string Hash)> jslotHashes = new(StringComparer.OrdinalIgnoreCase);

    static string JslotHash(string path)
    {
        var date = File.GetLastWriteTimeUtc(path);
        if (jslotHashes.TryGetValue(path, out var h) && h.Date == date) return h.Hash;
        var hash = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path)));
        jslotHashes[path] = (date, hash);
        return hash;
    }

    static void Handle(Context ctx, HttpListenerContext c, string web, string cache)
    {
        var path = c.Request.Url!.AbsolutePath.TrimEnd('/');
        var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var post = c.Request.HttpMethod == "POST";

        if (seg.Length == 0 || seg[0] != "api")
        {
            var file = Path.GetFullPath(Path.Combine(web, seg.Length == 0 ? "index.html" : string.Join('\\', seg)));
            if (!file.StartsWith(Path.GetFullPath(web)) || !File.Exists(file)) { Send(c, 404, new JsonObject { ["erreur"] = "introuvable" }); return; }
            SendFile(c, file);
            return;
        }

        // Sécurité : seule la page du Builder (même origine) peut appeler l'API. Sans ce contrôle, un site ouvert dans le
        // navigateur pendant que le Builder tourne pourrait envoyer des POST à localhost (CSRF : réglages, builds…).
        // Le navigateur signale l'appelant (Sec-Fetch-Site, Origin) ; les outils en ligne de commande n'envoient ni l'un ni l'autre.
        var fetchSite = c.Request.Headers["Sec-Fetch-Site"];
        var origin = c.Request.Headers["Origin"];
        var self = c.Request.Url.GetLeftPart(UriPartial.Authority);
        if (fetchSite is "cross-site" or "same-site" || origin != null && !string.Equals(origin, self, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(L.T("REFUSÉ {0} {1} : appel d'un autre site ({2})", c.Request.HttpMethod, path, origin ?? fetchSite));
            Send(c, 403, new JsonObject { ["erreur"] = "appel refusé : autre site" });
            return;
        }

        var rows = PresetTable.Read(ctx.CsvPath);
        PresetRow Row(string id) => rows.FirstOrDefault(r => r.Id == id) ?? throw new GfException($"preset {id} inconnu");
        var validation = LoadValidation(ctx);
        var verdicts = LoadVerdicts(ctx);

        switch (seg.ElementAtOrDefault(1))
        {
            case "presets":
                Send(c, 200, new JsonArray(rows.Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = r.Id, ["nom"] = r.Nom, ["nomPreset"] = r.NomPreset, ["categorie"] = r.Categorie, ["preset"] = r.Preset, ["race"] = r.Race,
                    ["sexe"] = r.Sexe, ["inclure"] = r.Inclure, ["statut"] = r.Statut,
                    // tête affichable : export RaceMenu associé au dernier build (fiche), ou tête générée
                    ["tete"] = File.Exists(HeadNif(ctx, r)) || HasExport(ctx, r),
                    ["validation"] = validation[r.Id]?.DeepClone(),
                    // dernier verdict « prêt pour le jeu » calculé pour ce preset (null : jamais vérifié)
                    ["verdict"] = verdicts[r.Id]?.DeepClone(),
                }).ToArray()));
                return;

            case "preset" when seg.Length > 2:
                Send(c, 200, Analyse(ctx, Row(seg[2]), cache));
                return;

            case "mesh" when seg.Length > 2:
                {
                    var shown = Row(seg[2]);
                    var mesh = Mesh(ctx, ShownHead(ctx, shown), cache);
                    SendFile(c, Erin.IsRace(shown.Race) ? Erin.PreviewHead(ctx, shown, mesh, cache) : mesh);
                }
                return;

            case "body" when seg.Length > 2:
                // corps entier de l'aperçu : corps, mains, pieds (et queue) de la peau de la race
                // ?preset= : preset BodySlide appliqué par les morphs du corps, comme OBody en jeu
                // ?tenue=1 : avec la tenue portée
                Send(c, 200, Body(ctx, Row(seg[2]), cache, c.Request.QueryString["preset"], c.Request.QueryString["tenue"] == "1"));
                return;

            case "bodypresets" when seg.Length > 2:
                // presets de corps qu'OBody NG peut donner au follower, et choix (option « corps »)
                Send(c, 200, BodyPresets.Info(ctx, Row(seg[2])));
                return;

            case "mesh-file":
                {
                    // référence (export RaceMenu d'une Erin) : montrée comme en jeu, visage par défaut du mod
                    var mesh = Mesh(ctx, Resolve(ctx, c.Request.QueryString["path"]), cache);
                    SendFile(c, Erin.HasErinHead(mesh) ? Erin.PreviewHead(ctx, null, mesh, cache) : mesh);
                }
                return;

            case "compare" when seg.Length > 2:
                // écart de forme du visage entre notre tête et une référence (export RaceMenu) ; tête d'un autre preset :
                // la tête affichée (export du donneur avec le visage du preset), pas la tête générée, qui reprend la
                // géométrie de l'export importé et donnait toujours 0
                {
                    var cmpRow = Row(seg[2]);
                    var cmpHead = HeadWrite.DonorRow(ctx, cmpRow) != null && ShownHead(ctx, cmpRow) is var shown && shown.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
                        ? shown : HeadNif(ctx, cmpRow);
                    Send(c, 200, Context.PythonEngine
                        ? JsonNode.Parse(Py.Run(ctx, "compare_heads.py", cmpHead, Resolve(ctx, c.Request.QueryString["path"])))!
                        : NativeTools.CompareHeads(cmpHead, Resolve(ctx, c.Request.QueryString["path"])));
                }
                return;

            case "texture":
                SendFile(c, Texture(ctx, c.Request.QueryString["path"] ?? "", cache));
                return;

            case "settings" when post:
                // Écran « Réglages » : chemins de l'installation, enregistrés dans settings.json
                {
                    var before = ctx.Settings;
                    var next = JsonSerializer.Deserialize<Settings>(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new GfException("réglages illisibles");
                    next.Root = ctx.Root;   // outils livrés dans deps\
                    next.Save(ctx.Root);
                    ctx.Settings = next;
                    L.Init(ctx.Root, next.Language);
                    bool modsChanged = next.ResolvedModsDir() != before.ResolvedModsDir() || next.ModManager != before.ModManager
                        || next.ResolvedMo2ProfileDir() != before.ResolvedMo2ProfileDir();
                    if (modsChanged) mods = ModIndex.Load(ctx, refresh: true);
                    // sources des skins changées (mods, archives, 7-Zip) : catalogue refait tout de suite, sinon il fallait
                    // aussi cliquer « Chercher les skins » (retour du premier test de la bêta, 19/10)
                    var messages = new List<string>();
                    if (modsChanged || next.ResolvedArchivesDir() != before.ResolvedArchivesDir() || next.ResolvedSevenZip() != before.ResolvedSevenZip())
                    {
                        skins = Skins.Load(ctx, refresh: true);
                        messages.Add(L.T("Catalogue des skins refait : {0} skins ({1} dans des archives).", skins.All.Count, skins.All.Count(s => s.Source == "archive")));
                    }
                    else skins = null;
                    var answer = SettingsJson(ctx);
                    if (!string.Equals(next.GameDataDir.Trim(), before.GameDataDir.Trim(), StringComparison.OrdinalIgnoreCase))
                        messages.Add(L.T("Dossier du jeu modifié : redémarre le Follower Builder pour relire l'ordre de chargement."));
                    // MO2 : instance, profil ou mode changés -> plugins et Data virtuel (GameFiles) relus au démarrage
                    else if ((next.IsMo2 || before.IsMo2) && (modsChanged || !string.Equals(next.Mo2Mode.Trim(), before.Mo2Mode.Trim(), StringComparison.OrdinalIgnoreCase)))
                        messages.Add(L.T("Réglages de Mod Organizer 2 modifiés : redémarre le Follower Builder pour relire les mods et l'ordre de chargement du profil."));
                    if (!string.Equals((next.PluginEncoding ?? "").Trim(), (before.PluginEncoding ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                        messages.Add(L.T("Encodage des textes des plugins modifié : redémarre le Follower Builder pour relire les noms des mods."));
                    if (messages.Count > 0) answer["message"] = string.Join("\n\n", messages);
                    // l'interface se recharge pour prendre la nouvelle langue
                    if (!string.Equals(next.Language.Trim(), before.Language.Trim(), StringComparison.OrdinalIgnoreCase)) answer["recharger"] = true;
                    Send(c, 200, answer);
                }
                return;

            case "diagnostic" when post:
                // Rapport de diagnostic (bêta) : ZIP dans le dossier de sortie, à joindre à un retour
                {
                    var (zip, text) = SupportReport.Create(ctx);
                    Send(c, 200, new JsonObject { ["zip"] = zip, ["texte"] = text });
                }
                return;

            case "i18n":
                // Traductions de l'interface : langue utilisée et dictionnaire { texte français : traduction }
                {
                    var file = L.FilePath(ctx.Root, L.Lang);
                    Send(c, 200, new JsonObject
                    {
                        ["lang"] = L.Lang,
                        ["textes"] = L.Lang != "fr" && File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : new JsonObject(),
                    });
                }
                return;

            case "settings":
                Send(c, 200, SettingsJson(ctx));
                return;

            case "rescan" when post:
                {
                    var scanLog = Rescan(ctx);
                    var after = PresetTable.Read(ctx.CsvPath);
                    var added = after.Where(r => rows.All(o => o.Id != r.Id)).Select(r => (JsonNode)r.Nom).ToArray();
                    var removed = rows.Where(r => after.All(n => n.Id != r.Id)).Select(r => (JsonNode)r.Nom).ToArray();
                    Send(c, 200, new JsonObject
                    {
                        ["total"] = after.Count, ["nouveaux"] = new JsonArray(added), ["disparus"] = new JsonArray(removed),
                        ["journal"] = scanLog.Trim(),
                    });
                }
                return;

            case "remap" when post:
                // Réparation acceptée dans le Builder : texture de l'export → texture installée (work\texture_remap.json,
                // utilisé par le build et par l'aperçu)
                {
                    var remapBody = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())!;
                    string from = remapBody["from"]!.GetValue<string>(), to = remapBody["to"]!.GetValue<string>();
                    var remapFile = Path.Combine(ctx.WorkDir, "texture_remap.json");
                    var map = File.Exists(remapFile) ? JsonNode.Parse(File.ReadAllText(remapFile))!.AsObject() : [];
                    map[from] = to;
                    File.WriteAllText(remapFile, map.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                    Send(c, 200, new JsonObject { ["ok"] = true });
                }
                return;

            case "faceoverlays" when seg.Length > 2:
                // Overlays RaceMenu visibles du visage (Face [Ovl0..2], alpha > 0, vraie texture), dans l'ordre : l'aperçu
                // les dessine sur la tête comme RaceMenu (texture × couleur, opacité = alpha)
                {
                    var r = Row(seg[2]);
                    var layers = new JsonArray();
                    foreach (var g in Jslot.Load(ctx.PresetPath(r.Preset)).Overrides.GroupBy(v => v.Node)
                                 .Select(g => (G: g, M: System.Text.RegularExpressions.Regex.Match(g.Key, @"^Face \[Ovl(\d+)\]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                                 .Where(x => x.M.Success && int.Parse(x.M.Groups[1].Value) < 3).OrderBy(x => int.Parse(x.M.Groups[1].Value)))
                    {
                        var vals = g.G.ToList();
                        float alpha = vals.LastOrDefault(v => v.Key == 8)?.Data is { } a ? (float)a.GetValue<double>() : 1f;
                        var tex = vals.LastOrDefault(v => v.Key == 9 && v.Index == 0)?.Data?.GetValue<string>();
                        if (alpha <= 0.001f || string.IsNullOrWhiteSpace(tex) || Overlays.NormTexture(tex).EndsWith(@"overlays\default.dds", StringComparison.OrdinalIgnoreCase)) continue;
                        int? color = vals.LastOrDefault(v => v.Key == 7)?.Data is { } col ? unchecked((int)(long)col.GetValue<double>()) & 0xFFFFFF : null;
                        layers.Add(new JsonObject { ["node"] = g.G.Key, ["tex"] = Overlays.NormTexture(tex), ["color"] = color, ["alpha"] = alpha });
                    }
                    Send(c, 200, layers);
                }
                return;

            case "bodyoverlays" when seg.Length > 2:
                // Overlays RaceMenu visibles du corps, des mains et des pieds (Body/Hands/Feet [Ovl#], index < iNumOverlays
                // de skee64.ini), mêmes règles que le visage ; « zone » dit sur quelles formes du corps les poser
                {
                    var r = Row(seg[2]);
                    var limits = new Overlays(ctx);
                    var layers = new JsonArray();
                    foreach (var g in Jslot.Load(ctx.PresetPath(r.Preset)).Overrides.GroupBy(v => v.Node)
                                 .Select(g => (G: g, M: System.Text.RegularExpressions.Regex.Match(g.Key, @"^(Body|Hands|Feet) \[Ovl(\d+)\]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                                 .Where(x => x.M.Success && int.Parse(x.M.Groups[2].Value) < limits.Count(x.M.Groups[1].Value))
                                 .OrderBy(x => x.M.Groups[1].Value).ThenBy(x => int.Parse(x.M.Groups[2].Value)))
                    {
                        var vals = g.G.ToList();
                        float alpha = vals.LastOrDefault(v => v.Key == 8)?.Data is { } a ? (float)a.GetValue<double>() : 1f;
                        var tex = vals.LastOrDefault(v => v.Key == 9 && v.Index == 0)?.Data?.GetValue<string>();
                        if (alpha <= 0.001f || string.IsNullOrWhiteSpace(tex) || Overlays.NormTexture(tex).EndsWith(@"overlays\default.dds", StringComparison.OrdinalIgnoreCase)) continue;
                        int? color = vals.LastOrDefault(v => v.Key == 7)?.Data is { } col ? unchecked((int)(long)col.GetValue<double>()) & 0xFFFFFF : null;
                        layers.Add(new JsonObject { ["node"] = g.G.Key, ["zone"] = g.M.Groups[1].Value.ToLowerInvariant(), ["tex"] = Overlays.NormTexture(tex), ["color"] = color, ["alpha"] = alpha });
                    }
                    Send(c, 200, layers);
                }
                return;

            case "skins" when seg.Length > 2:
                // Skins compatibles avec le corps du follower (UBE, CBBE/3BA, HIMBO) et skin choisi (work\skins_choice.json)
                {
                    var r = Row(seg[2]);
                    skins ??= Skins.Load(ctx);
                    var choice = Skins.LoadChoices(ctx).GetValueOrDefault(r.Id);
                    Send(c, 200, new JsonObject
                    {
                        ["corps"] = Skins.BodyLabel(Skins.BodyOf(ctx, r)), ["sexe"] = r.Sexe, ["choix"] = choice,
                        ["erin"] = Erin.IsRace(r.Race),
                        ["choixInconnu"] = choice != null && skins.Get(choice) == null,
                        ["skins"] = new JsonArray(skins.For(r).Select(s => (JsonNode)new JsonObject
                        {
                            ["id"] = s.Id, ["nom"] = s.Name, ["source"] = s.Source is "mod" or "vortex" ? L.T("mod {0}", ctx.Settings.ManagerLabel) : L.T("archive"), ["chemin"] = s.Path,
                            ["roles"] = new JsonArray(s.Files.Keys.Select(k => (JsonNode)k).ToArray()),
                            ["mo"] = Math.Round(s.Sizes.Values.Sum() / 1048576.0),
                        }).ToArray()),
                    });
                }
                return;

            case "skingloss" when seg.Length > 2:
                // Brillance de la tête et du corps du skin choisi au raccord du cou (option « brillanceCou »)
                skins ??= Skins.Load(ctx);
                Send(c, 200, SkinGloss.Info(ctx, Row(seg[2]), skins));
                return;

            case "skin" when post && seg.Length > 2:
                // {"skin": id | null} : skin du follower au prochain build
                {
                    var r = Row(seg[2]);
                    skins ??= Skins.Load(ctx);
                    var id = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())?["skin"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id) && !skins.For(r).Any(s => s.Id == id)) throw new GfException(L.T("Skin inconnu ou incompatible avec le corps de ce follower."));
                    Skins.SaveChoice(ctx, r.Id, id);
                    Send(c, 200, new JsonObject { ["ok"] = true });
                }
                return;

            case "combat" when seg.Length > 2:
                // Style de combat et classe : GET = listes et choix (work\combat_choice.json) ;
                // POST {"style": clé | null, "classe": EditorID | null} = choix du prochain build
                {
                    var r = Row(seg[2]);
                    if (post)
                    {
                        var b = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd());
                        Combat.SaveChoice(ctx, r.Id, b?["style"]?.GetValue<string>(), b?["classe"]?.GetValue<string>());
                    }
                    Send(c, 200, Combat.Info(ctx, r));
                }
                return;

            case "voice" when seg.Length > 2:
                // Voix : GET = voix du sexe du follower, dialogue de follower de chacune, choix (work\voice_choice.json) ;
                // POST {"voix": EditorID | null} = voix du prochain build
                {
                    var r = Row(seg[2]);
                    if (post)
                        Voices.SaveChoice(ctx, r, JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())?["voix"]?.GetValue<string>());
                    Send(c, 200, Voices.Info(ctx, r));
                }
                return;

            case "options" when seg.Length > 2:
                // Options (tenue, sorts, atouts, niveau, comportement, taille, mariage) : GET = catalogues et choix
                // (work\options_choice.json) ; POST {"cle": valeur | null} = choix du prochain build
                {
                    var r = Row(seg[2]);
                    if (post && JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd()) is JsonObject ob) Options.Save(ctx, r, ob);
                    Send(c, 200, Options.Info(ctx, r));
                }
                return;

            case "archetype" when post && seg.Length > 2:
                // {"archetype": clé} : remplace style, classe, tenue, armes, sorts et atouts du preset
                {
                    var r = Row(seg[2]);
                    var key = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())?["archetype"]?.GetValue<string>() ?? "";
                    Options.ApplyArchetype(ctx, r, key);
                    Send(c, 200, Options.Info(ctx, r));
                }
                return;

            case "batch" when post:
                // {"ids": [...], "source": id | null, "archetype": clé | null, "parts": [...]} : plusieurs followers à la fois
                {
                    var b = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd()) as JsonObject
                        ?? throw new GfException("requête invalide");
                    var batchIds = (b["ids"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").Distinct().ToList();
                    var targets = batchIds.Select(Row).ToList();   // id inconnu : erreur, rien n'est modifié
                    var src = b["source"]?.GetValue<string>() is { } sid ? Row(sid) : null;
                    var parts = (b["parts"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").ToList();
                    Send(c, 200, Options.Batch(ctx, targets, src, b["archetype"]?.GetValue<string>(), parts));
                }
                return;

            case "voice-play" when post:
                // Extrait d'une voix joué par Windows (XAudio2, sans ffmpeg) sur ce PC ; en cas d'échec, la page demande le
                // WAV (voice-sample) et le joue elle-même
                try { Send(c, 200, new JsonObject { ["ok"] = true, ["duree"] = Voices.Play(ctx, c.Request.QueryString["voix"] ?? "") }); }
                catch (Exception e) when (e is not GfException and not OutOfMemoryException) { Send(c, 200, new JsonObject { ["ok"] = false, ["erreur"] = e.Message }); }
                return;

            case "voice-sample":
                // Extrait d'une voix du jeu (?voix=EditorID d'une voix du catalogue) : WAV converti par ffmpeg, en cache
                SendFile(c, Voices.Sample(ctx, c.Request.QueryString["voix"] ?? ""));
                return;

            case "coherence" when seg.Length > 2:
                // cohérence des choix du follower (rapide : mise à jour de la fiche après chaque changement)
                Send(c, 200, new JsonArray(Options.Coherence(ctx, Row(seg[2]))
                    .Select(k => (JsonNode)new JsonObject { ["niveau"] = k.Level, ["texte"] = k.Text }).ToArray()));
                return;

            case "skins-refresh" when post:
                // Nouveau skin installé dans le gestionnaire de mods ou téléchargé : catalogue refait (archives déjà listées en cache)
                skins = Skins.Load(ctx, refresh: true);
                Send(c, 200, new JsonObject { ["total"] = skins.All.Count });
                return;

            case "skin-texture":
                // Texture d'un skin (?skin=&role=head_d) pour l'aperçu : extraite de l'archive au besoin, en PNG
                {
                    skins ??= Skins.Load(ctx);
                    var sk = skins.Get(c.Request.QueryString["skin"]) ?? throw new GfException("skin inconnu");
                    var file = skins.SourceFile(sk, c.Request.QueryString["role"] ?? "") ?? throw new GfException("ce skin n'a pas cette texture");
                    SendFile(c, Texture(ctx, file, cache));
                }
                return;

            case "donneurs-lot" when post:
                // {"ids": [...], "niveaux": ["export", "rouge", "orange"]} : meilleur donneur pour chaque preset sans tête prête
                {
                    var b = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd()) as JsonObject
                        ?? throw new GfException("requête invalide");
                    var donorIds = (b["ids"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").Distinct().ToList();
                    var levels = (b["niveaux"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").ToHashSet();
                    Send(c, 200, Options.BatchDonors(ctx, donorIds.Select(Row).ToList(), levels));
                }
                return;

            case "donneurs" when seg.Length > 2:
                // Tête d'un autre preset : donneurs possibles (exports acceptés, même sexe, même tête), du meilleur au moins bon
                {
                    var r = Row(seg[2]);
                    var (ranked, partCount, faceName) = HeadWrite.Candidates(ctx, r);
                    Send(c, 200, new JsonObject
                    {
                        ["tete"] = faceName, ["pieces"] = partCount, ["choix"] = Options.ChosenDonor(ctx, r.Id),
                        ["donneurs"] = new JsonArray(ranked.Take(15).Select(d => (JsonNode)new JsonObject
                        {
                            ["id"] = d.Row.Id, ["nom"] = d.Row.Nom, ["race"] = d.Row.Race, ["communes"] = d.Common, ["formes"] = d.Shapes,
                            ["export"] = ctx.CharGenRelOf(d.Import), ["visage"] = d.FaceOk,
                        }).ToArray()),
                    });
                }
                return;

            case "tint" when seg.Length > 2:
                // Teinte du visage livrée en jeu : celle que le build convertit (work\heads_offline\GF_<id>.dds : teinte
                // de l'export, ou calculée depuis le jslot si l'export n'en a pas ou si elle est uniforme), sinon celle
                // de l'export (?path=)
                {
                    var tintRow = Row(seg[2]);
                    // ?peau=RRGGBB : aperçu d'une couleur de peau choisie (option « couleurPeau ») ; teinte refaite à part
                    // dans work\tint_tmp, la teinte du build n'est pas touchée
                    if (Options.ParseColor(c.Request.QueryString["peau"]) is { } peau && !Erin.IsRace(tintRow.Race))
                    {
                        var jslotPath = ctx.PresetPath(tintRow.Preset);
                        var eff = HeadParts.Effective(ctx, Jslot.Load(jslotPath), ctx.Find<IRaceGetter>(tintRow.Race), tintRow.Female);
                        var export = ExportPath(ctx, tintRow, eff);
                        var tinted = Path.Combine(Context.Dir(Path.Combine(ctx.WorkDir, "tint_tmp")), $"peau_{tintRow.ExportName}_{peau:X6}.dds");
                        var newest = new[] { jslotPath, export == null ? null : Path.ChangeExtension(export, ".dds") }
                            .Where(p => p != null && File.Exists(p)).Max(p => File.GetLastWriteTimeUtc(p!));
                        if (!File.Exists(tinted) || File.GetLastWriteTimeUtc(tinted) < newest)
                            Tint.WithSkinTone(ctx, jslotPath, export, tinted, peau);
                        SendFile(c, Texture(ctx, tinted, cache));
                        return;
                    }
                    var built =Path.Combine(ctx.WorkDir, "heads_offline", tintRow.ExportName + ".dds");
                    // preset jamais construit ni régénéré : la teinte n'existe pas encore, et celle de l'export est souvent
                    // absente ou neutre (UBE) : la tête de l'aperçu était noire ou d'une autre couleur que le corps tant que
                    // le follower n'avait pas été construit (signalement d'un joueur, 14/11). On la fait ici, comme le build.
                    if (!File.Exists(built))
                        try
                        {
                            var jslotPath = ctx.PresetPath(tintRow.Preset);
                            var eff = HeadParts.Effective(ctx, Jslot.Load(jslotPath), ctx.Find<IRaceGetter>(tintRow.Race), tintRow.Female);
                            File.WriteAllText(built + ".teinte", Tint.MakeFrom(ctx, jslotPath, ExportPath(ctx, tintRow, eff), built));
                        }
                        catch (Exception e) when (e is not OutOfMemoryException) { Console.WriteLine(L.T("teinte de l'aperçu non faite pour {0} : {1}", tintRow.Id, e.Message)); }
                    var tintPath = File.Exists(built) ? built : c.Request.QueryString["path"] ?? "";
                    SendFile(c, Texture(ctx, tintPath, cache));
                }
                return;

            case "references":
                // Têtes exportées par RaceMenu (CharGen\**\*.nif), à superposer pour comparer
                var refs = ctx.Files.Enumerate(Context.CharGenRel, "*.nif", recursive: true)
                    .Select(f => Path.Combine(Context.CharGenRel, f.Rel)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                Send(c, 200, new JsonArray(refs.Select(f => (JsonNode)f).ToArray()));
                return;

            case "generate" when post && seg.Length > 2:
                var row = Row(seg[2]);
                // installation neuve : work\heads_offline n'existe pas encore (File.Delete échoue sur un dossier absent)
                if (File.Exists(HeadNif(ctx, row))) File.Delete(HeadNif(ctx, row));
                OfflineHeads.Generate(ctx, [row]);
                Send(c, 200, new JsonObject { ["ok"] = File.Exists(HeadNif(ctx, row)), ["erreurs"] = File.ReadAllText(Path.Combine(ctx.WorkDir, "heads_offline_erreurs.txt")) });
                return;

            case "import" when post && seg.Length > 2:
                // choix de la tête RaceMenu importée : {"mode": "auto"|"manuel"|"aucun", "path": "<relatif à CharGen>"}
                var imp = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())!;
                HeadImports.SaveChoice(ctx, seg[2], imp["mode"]?.GetValue<string>() ?? "auto", imp["path"]?.GetValue<string>());
                Send(c, 200, new JsonObject { ["ok"] = true });
                return;

            case "validate" when post && seg.Length > 2:
                var body = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())!;
                var status = body["status"]?.GetValue<string>() ?? "";
                if (status == "") validation.Remove(seg[2]);
                else validation[seg[2]] = new JsonObject { ["status"] = status, ["note"] = body["note"]?.GetValue<string>() ?? "", ["date"] = DateTime.Now.ToString("s") };
                SaveValidation(ctx, validation);
                Send(c, 200, new JsonObject { ["ok"] = true });
                return;

            case "rename" when post && seg.Length > 2:
                // nom du follower en jeu, gardé dans presets.csv (les scans suivants le conservent)
                var nom = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())?["nom"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(nom) || nom.Contains(';')) throw new GfException(L.T("Nom vide ou contenant « ; »."));
                Row(seg[2]).Nom = nom;
                PresetTable.Write(ctx.CsvPath, rows);
                Send(c, 200, new JsonObject { ["ok"] = true });
                return;

            case "sheet" when seg.Length > 2:
                // fiche partagée (SheetShare) : GET = la fiche du preset ; POST (fiche d'un autre joueur, 1 Mo au plus) =
                // appliquée au preset, avec la liste de ce qui est appliqué et ignoré ; le nom en jeu va dans presets.csv
                {
                    var r = Row(seg[2]);
                    skins ??= Skins.Load(ctx);
                    if (!post) { Send(c, 200, SheetShare.Export(ctx, r, skins)); return; }
                    if (c.Request.ContentLength64 > 1 << 20) throw new GfException(L.T("Fichier trop gros pour une fiche de follower."));
                    JsonObject sheet;
                    try { sheet = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd()) as JsonObject ?? throw new GfException(""); }
                    catch (Exception e) when (e is System.Text.Json.JsonException or GfException) { throw new GfException(L.T("Ce fichier n'est pas une fiche de follower GenerateFollower.")); }
                    var (applied, skipped, name) = SheetShare.Import(ctx, r, sheet, skins);
                    if (name != null && name != r.Nom) { r.Nom = name; PresetTable.Write(ctx.CsvPath, rows); applied.Insert(0, L.T("Nom en jeu : {0}", name)); }
                    Send(c, 200, new JsonObject
                    {
                        ["appliques"] = new JsonArray(applied.Select(a => (JsonNode)JsonValue.Create(a)).ToArray()),
                        ["ignores"] = new JsonArray(skipped.Select(a => (JsonNode)JsonValue.Create(a)).ToArray()),
                    });
                }
                return;

            case "coiffures" when seg.Length > 2:
                // coiffures que le Builder peut ajouter à ce follower (Options.Hairs) : ?mod= et ?q= filtrent
                Send(c, 200, Options.HairsInfo(ctx, Row(seg[2]), c.Request.QueryString["mod"], c.Request.QueryString["q"]));
                return;

            case "coiffure-mesh" when seg.Length > 2:
                // géométrie de la coiffure ajoutée choisie pour ce follower, dans le repère de la tête (aperçu 3D)
                Send(c, 200, AddedHairMesh(ctx, Row(seg[2]), cache));
                return;

            case "update":
                // dernière version publiée (UpdateCheck, vérifiée en arrière-plan au démarrage) : réponse immédiate
                Send(c, 200, UpdateCheck.Json());
                return;

            case "sheets":
                // fiches de plusieurs followers en un fichier (.gffiches.json) : GET ?ids=a,b (sans ids : les validés) ;
                // POST (8 Mo au plus) = chaque fiche appliquée au preset du même fichier .jslot chez ce joueur
                {
                    skins ??= Skins.Load(ctx);
                    if (!post)
                    {
                        var wanted = (c.Request.QueryString["ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var chosen = wanted.Length > 0
                            ? wanted.Select(id => rows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).Where(r => r != null).Select(r => r!).ToList()
                            : rows.Where(r => validation[r.Id]?["status"]?.GetValue<string>() == "valide").ToList();
                        if (chosen.Count == 0) throw new GfException(L.T("Aucun follower validé."));
                        Send(c, 200, SheetShare.ExportPack(ctx, chosen, skins));
                        return;
                    }
                    if (c.Request.ContentLength64 > 8 << 20) throw new GfException(L.T("Fichier trop gros pour un fichier de fiches."));
                    JsonObject pack;
                    try { pack = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd()) as JsonObject ?? throw new GfException(""); }
                    catch (Exception e) when (e is System.Text.Json.JsonException or GfException) { throw new GfException(L.T("Ce fichier n'est pas un fichier de fiches GenerateFollower.")); }
                    var (results, renamed) = SheetShare.ImportPack(ctx, rows, pack, skins);
                    if (renamed) PresetTable.Write(ctx.CsvPath, rows);
                    Send(c, 200, new JsonObject { ["resultats"] = results });
                }
                return;

            case "build" when post:
                // {"mode": "seul", "id": …} -> GF_<nom du follower>.esp ; {"mode": "pack", "nom": …} -> GF_<nom>.esp avec les validés
                var req = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd() is { Length: > 0 } s ? s : "{}")!;
                HashSet<string> ids;
                string title;
                if (req["mode"]?.GetValue<string>() == "seul")
                {
                    var one = Row(req["id"]?.GetValue<string>() ?? throw new GfException("id manquant"));
                    ids = new(StringComparer.OrdinalIgnoreCase) { one.Id };
                    title = one.Nom;
                }
                else
                {
                    ids = validation.Where(kv => kv.Value?["status"]?.GetValue<string>() == "valide").Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (ids.Count == 0) throw new GfException(L.T("Aucun follower validé."));
                    title = req["nom"]?.GetValue<string>()?.Trim() is { Length: > 0 } n ? n : "FollowerPack";
                }
                var pluginName = FollowerBuilder.PluginFor(title);
                // {"pouvoir": false} : pack sans pouvoir d'invocation (si tous les followers ont un lieu de départ)
                Send(c, 200, RunBuild(ctx, ids, Path.GetFileNameWithoutExtension(pluginName), pluginName, title, req["pouvoir"]?.GetValue<bool>() ?? true));
                return;

            case "replace-build" when post:
                // paquet GF_Remplacements (tous les presets qui ont un PNJ choisi)
                Send(c, 200, NpcReplace.BuildPack(ctx));
                return;

            case "replace" when seg.Length > 2:
                // PNJ remplacé par ce preset : GET = choix et PNJ pris ailleurs ; POST {"pnj": FormKey | null}
                {
                    var r = Row(seg[2]);
                    if (post) NpcList.SaveChoice(ctx, r.Id, JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())?["pnj"]?.GetValue<string>());
                    Send(c, 200, NpcList.ChoiceInfo(ctx, r.Id));
                }
                return;

            case "replace-batch":
                // remplacement en lot : GET = tous les choix { preset : PNJ } ; POST {"choix": { preset : PNJ | null }}
                {
                    if (post)
                    {
                        var lot = JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())?["choix"] as JsonObject
                            ?? throw new GfException("choix manquants");
                        foreach (var (id, _) in lot) Row(id);   // preset inconnu : erreur avant toute écriture
                        NpcList.SaveChoices(ctx, lot);
                    }
                    Send(c, 200, NpcList.Choices(ctx));
                }
                return;

            case "npcs":
                // PNJ remplaçables (remplacement de PNJ, lecture seule ; ?refresh=1 pour relire)
                Send(c, 200, NpcList.Json(ctx, c.Request.QueryString["refresh"] == "1"));
                return;

            case "prerequisites":
                // mods dont les followers ont besoin en jeu (Prerequisites) : présent ou non, requis ou selon les choix
                Send(c, 200, Prerequisites.Json(ctx, rows));
                return;

            case "packs":
                // packs déjà construits (Packs) : followers retrouvés dans la liste des presets
                Send(c, 200, new JsonArray(Packs.List(ctx).Select(p => (JsonNode)new JsonObject
                {
                    ["zip"] = p.Zip, ["date"] = p.Date.ToString("yyyy-MM-dd HH:mm"), ["mo"] = Math.Round(p.Size / 1048576.0, 1),
                    ["plugin"] = p.Plugin, ["titre"] = p.Title, ["probleme"] = p.Problem, ["sansPouvoir"] = p.NoPower,
                    ["followers"] = new JsonArray(p.Ids.Select(id => (JsonNode)new JsonObject
                    {
                        ["id"] = id, ["nom"] = rows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Nom,
                    }).ToArray()),
                }).ToArray()));
                return;

            case "packs-rebuild" when post:
                // {"zip": nom} : même ZIP, même plugin, même titre, mêmes followers (ceux qui existent encore), choix actuels
                {
                    var p = Packs.Get(ctx, JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())?["zip"]?.GetValue<string>() ?? "");
                    if (p.Plugin == null) throw new GfException(L.T("Ce pack ne peut pas être reconstruit : {0}", p.Problem ?? ""));
                    var keep = p.Ids.Where(id => rows.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (keep.Count == 0) throw new GfException(L.T("Aucun follower de ce pack n'est encore dans ta liste de presets."));
                    Send(c, 200, RunBuild(ctx, keep, Path.GetFileNameWithoutExtension(p.Zip), p.Plugin, p.Title ?? Path.GetFileNameWithoutExtension(p.Plugin), !p.NoPower, p.PlacedRefs));
                }
                return;

            case "packs-open" when post:
                // {"zip": nom} : l'Explorateur sur ce ZIP (dossier de sortie seulement)
                {
                    var p = Packs.Get(ctx, JsonNode.Parse(new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd())?["zip"]?.GetValue<string>() ?? "");
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p.Path}\"");
                    Send(c, 200, new JsonObject { ["ok"] = true });
                }
                return;
        }
        Send(c, 404, new JsonObject { ["erreur"] = L.T("API inconnue : {0}", path) });
    }

    /// <summary>Construit un pack (sortie console capturée pour la page) ; FaceGen = export RaceMenu modifié en binaire
    /// (seule méthode acceptée par le jeu, tests 11 à 13).</summary>
    static JsonObject RunBuild(Context ctx, HashSet<string> ids, string zipName, string pluginName, string title, bool summonPower = true, bool? placedRefs = null)
    {
        var log = new StringWriter();
        var old = Console.Out;
        Console.SetOut(log);
        try { FollowerBuilder.Run(ctx, offline: true, only: ids, zipName: zipName, fromExport: true, pluginName: pluginName, title: title, summonPower: summonPower, placedRefs: placedRefs); }
        finally { Console.SetOut(old); }
        Console.Write(log);
        return new JsonObject
        {
            ["plugin"] = pluginName, ["zip"] = Path.Combine(ctx.DistDir, zipName + ".zip"), ["followers"] = ids.Count,
            ["log"] = log.ToString(), ["rapport"] = File.ReadAllText(Path.Combine(ctx.WorkDir, "rapport_build.txt")),
        };
    }

    /// <summary>Réglages enregistrés, valeurs utilisées (détectées si le champ est vide) et présence des fichiers.</summary>
    static JsonObject SettingsJson(Context ctx)
    {
        var s = ctx.Settings;
        var python = s.ResolvedPython();
        var used = new Dictionary<string, (string Value, bool Ok)>
        {
            ["language"] = (L.Lang, true),
            ["gameDataDir"] = (ctx.DataDir, Directory.Exists(ctx.DataDir)),
            ["modsDir"] = (s.ResolvedModsDir(), Directory.Exists(s.ResolvedModsDir())),
            ["mo2Profile"] = (s.ResolvedMo2ProfileDir(), !s.IsMo2 || File.Exists(Path.Combine(s.ResolvedMo2ProfileDir(), "modlist.txt"))),
            // mode réellement utilisé depuis le démarrage : mods lus dans le dossier de MO2, ou Data seulement
            ["mo2Mode"] = (!s.IsMo2 ? "" : ctx.Files.Direct ? L.T("mods lus dans le dossier de MO2 ({0} racines)", ctx.Files.Roots.Count) : L.T("Data seulement"), true),
            ["archivesDir"] = (s.ResolvedArchivesDir(), s.ResolvedArchivesDir() == "" || Directory.Exists(s.ResolvedArchivesDir())),
            ["outputDir"] = (s.ResolvedOutputDir(ctx.Root), true),
            ["sevenZip"] = (s.ResolvedSevenZip(), File.Exists(s.ResolvedSevenZip())),
            // encodage réellement utilisé depuis le démarrage et langue du jeu lue dans Skyrim.ini
            ["pluginEncoding"] = (PluginText.Describe(), true),
        };
        // texconv, Python et PyNifly ne servent qu'à l'ancien moteur (GF_MOTEUR=python) : la page ne les montre pas sinon
        if (Context.PythonEngine)
        {
            used["texconv"] = (s.ResolvedTexconv(ctx.DataDir), File.Exists(s.ResolvedTexconv(ctx.DataDir)));
            // « py » ou « python » : lanceur cherché dans le PATH, non vérifié ici
            used["python"] = (python, !Path.IsPathRooted(python) || File.Exists(python));
            used["pyNifly"] = (s.ResolvedPyNifly(ctx.Root), Directory.Exists(Path.Combine(s.ResolvedPyNifly(ctx.Root), "pyn")));
        }
        var inst = s.Mo2Instance();
        var modList = inst != null ? Mo2.ReadModList(s.ResolvedMo2ProfileDir()) : null;
        var seen = inst != null && modList != null ? Mo2.PluginsInData(inst, modList, ctx.Files) : (0, 0);
        return new JsonObject
        {
            // MO2 : diagnostic (instance, profil, lancé depuis MO2, plugins des mods activés visibles dans Data)
            ["mo2"] = inst == null ? null : new JsonObject
            {
                ["instance"] = inst.Dir, ["profil"] = s.ResolvedMo2ProfileDir(), ["lanceDepuisMo2"] = Mo2.UnderVfs(),
                ["mode"] = ctx.Files.Direct ? "direct" : "vfs", ["racines"] = ctx.Files.Roots.Count,
                ["modsActives"] = modList?.Enabled.Count ?? 0, ["pluginsDesMods"] = seen.Item1, ["pluginsVusDansData"] = seen.Item2,
            },
            // MO2 lancé hors de MO2 (Data sans les mods), corps ou tête différents de l'installation validée en jeu :
            // affiché au démarrage du Builder et dans les Réglages
            // jeu introuvable : pas d'avertissement de corps (rien n'est installé dans un dossier Data absent)
            ["avertissement"] = string.Join("\n\n", new[] { ctx.StartupProblem, scanProblem, inst != null ? Mo2.DataWarning(inst, modList, ctx.Files) : null,
                ctx.StartupProblem == null ? ctx.Bodies.Warning : null }
                .Where(w => w != null)) is { Length: > 0 } warn ? warn : null,
            // corps et tête détectés dans Data
            ["installation"] = ctx.Bodies.Summary,
            ["fichier"] = Settings.FilePath(ctx.Root),
            ["version"] = SupportReport.Version,
            // « dotnet » : texconv, Python et PyNifly inutiles (champs cachés par la page)
            ["moteur"] = Context.PythonEngine ? "python" : "dotnet",
            ["valeurs"] = JsonNode.Parse(JsonSerializer.Serialize(s, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })),
            ["utilises"] = new JsonObject(used.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject { ["valeur"] = kv.Value.Value, ["ok"] = kv.Value.Ok }))),
        };
    }

    static string Hash(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    static string VerdictsPath(Context ctx) => Path.Combine(ctx.WorkDir, "verdicts.json");

    static JsonObject LoadVerdicts(Context ctx)
        => File.Exists(VerdictsPath(ctx)) ? JsonNode.Parse(File.ReadAllText(VerdictsPath(ctx)))!.AsObject() : [];

    static void SaveVerdict(Context ctx, string id, JsonObject verdict)
    {
        var all = LoadVerdicts(ctx);
        all[id] = verdict.DeepClone();
        File.WriteAllText(VerdictsPath(ctx), all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    static string ValidationPath(Context ctx) => Path.Combine(ctx.WorkDir, "validation.json");

    static JsonObject LoadValidation(Context ctx)
        => File.Exists(ValidationPath(ctx)) ? JsonNode.Parse(File.ReadAllText(ValidationPath(ctx)))!.AsObject() : [];

    static void SaveValidation(Context ctx, JsonObject v)
        => File.WriteAllText(ValidationPath(ctx), v.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    static void Send(HttpListenerContext c, int code, JsonNode body)
    {
        c.Response.StatusCode = code;
        c.Response.ContentType = "application/json; charset=utf-8";
        Write(c, Encoding.UTF8.GetBytes(body.ToJsonString()));
    }

    static void Write(HttpListenerContext c, byte[] bytes)
    {
        c.Response.ContentLength64 = bytes.Length;
        if (c.Request.HttpMethod != "HEAD") c.Response.OutputStream.Write(bytes);
        c.Response.Close();
    }

    static void SendFile(HttpListenerContext c, string file)
    {
        c.Response.ContentType = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8", ".png" => "image/png", ".wav" => "audio/wav", _ => "application/octet-stream",
        };
        Write(c, File.ReadAllBytes(file));
    }
}
