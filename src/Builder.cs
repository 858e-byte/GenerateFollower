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
public static class Builder
{
    public static void Serve(Context ctx, int port = 5180)
    {
        var prefix = $"http://localhost:{port}/";
        var web = Path.Combine(AppResources.Dir(ctx.Root), "builder");
        var cache = Context.Dir(Path.Combine(ctx.WorkDir, "viewer"));
        // presets et exports à jour dès l'ouverture (nouveaux jslot, Export Head faits depuis le dernier scan)
        Console.WriteLine("Scan des presets et index des têtes RaceMenu de CharGen...");
        Rescan(ctx);
        using var http = new HttpListener();
        http.Prefixes.Add(prefix);
        http.Start();
        Console.WriteLine($"Follower Builder : {prefix}  (Ctrl+C pour arrêter)");
        while (http.IsListening)
        {
            var c = http.GetContext();
            try { Handle(ctx, c, web, cache); }
            catch (Exception e)
            {
                Console.WriteLine($"ERREUR {c.Request.Url?.AbsolutePath} : {e.Message}");
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
                Console.WriteLine("ERREUR : " + e.Message);
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
            Console.WriteLine($"REFUSÉ {c.Request.HttpMethod} {path} : appel d'un autre site ({origin ?? fetchSite})");
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
                // écart de forme du visage entre notre tête et une référence (export RaceMenu)
                Send(c, 200, Context.PythonEngine
                    ? JsonNode.Parse(Py.Run(ctx, "compare_heads.py", HeadNif(ctx, Row(seg[2])), Resolve(ctx, c.Request.QueryString["path"])))!
                    : NativeTools.CompareHeads(HeadNif(ctx, Row(seg[2])), Resolve(ctx, c.Request.QueryString["path"])));
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

            case "tint" when seg.Length > 2:
                // Teinte du visage livrée en jeu : celle que le build convertit (work\heads_offline\GF_<id>.dds : teinte
                // de l'export, ou calculée depuis le jslot si l'export n'en a pas ou si elle est uniforme), sinon celle
                // de l'export (?path=)
                {
                    var built = Path.Combine(ctx.WorkDir, "heads_offline", Row(seg[2]).ExportName + ".dds");
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

    /// <summary>
    /// Ce que le Creation Kit verrait pour ce preset : headparts (validité race/sexe, extras), défauts de race
    /// ajoutés, formes attendues dans le FaceGen et comparaison avec le NIF généré.
    /// </summary>
    /// <summary>Type de headpart (numérotation RaceMenu = Mutagen) : 0 divers, 1 visage, 2 yeux, 3 cheveux, 4 barbe, 5 cicatrice, 6 sourcils.</summary>
    static string PartLabel(int type) => type switch
    {
        1 => L.T("visage"), 2 => L.T("yeux"), 3 => L.T("cheveux"), 4 => L.T("barbe"), 5 => L.T("cicatrice"), 6 => L.T("sourcils"),
        _ => L.T("divers"),
    };

    static JsonObject Analyse(Context ctx, PresetRow row, string cache)
    {
        var j = Jslot.Load(ctx.PresetPath(row.Preset));
        var race = ctx.Find<IRaceGetter>(row.Race);
        var effective = HeadParts.Effective(ctx, j, race, row.Female);
        // Mode autonome : dépendances autorisées, et nom de nos headparts recréés (GF_…)
        var allowed = ctx.Config.Standalone ? Standalone.Allowed(ctx, race, Standalone.Face(effective)) : null;
        var checks = new JsonArray();
        void Check(string level, string text) => checks.Add(new JsonObject { ["niveau"] = level, ["texte"] = text });
        if (race == null) Check("erreur", L.T("Race {0} introuvable dans l'ordre de chargement.", row.Race));

        JsonObject Part(IHeadPartGetter hp, string origin)
        {
            bool sexOk = !(hp.Flags.HasFlag(HeadPart.Flag.Male) || hp.Flags.HasFlag(HeadPart.Flag.Female))
                || (row.Female ? hp.Flags.HasFlag(HeadPart.Flag.Female) : hp.Flags.HasFlag(HeadPart.Flag.Male));
            bool? raceOk = null;
            string raceList = "";
            if (!hp.ValidRaces.IsNull && race != null && ctx.Cache.TryResolve<IFormListGetter>(hp.ValidRaces.FormKey, out var fl))
            {
                raceOk = fl.Items.Any(i => i.FormKey == race.FormKey);
                raceList = fl.EditorID ?? "";
            }
            var extras = new JsonArray();
            foreach (var e in hp.ExtraParts)
                if (ctx.Cache.TryResolve<IHeadPartGetter>(e.FormKey, out var x)) extras.Add(Part(x, "extra"));
            bool recreated = allowed != null && Standalone.MustCopy(ctx, hp, allowed, race);
            return new JsonObject
            {
                ["edid"] = hp.EditorID, ["nom"] = allowed != null ? Standalone.Name(ctx, hp, allowed, race) : hp.EditorID,
                ["recree"] = recreated, ["plugin"] = hp.FormKey.ModKey.FileName.String,
                ["formKey"] = hp.FormKey.ToString(), ["type"] = hp.Type?.ToString(),
                ["modele"] = hp.Model?.File.ToString(), ["origine"] = origin, ["sexeOk"] = sexOk,
                ["raceOk"] = raceOk, ["listeRaces"] = raceList, ["extras"] = extras,
            };
        }

        var parts = new JsonArray();
        var unresolved = new List<(string Key, int Type)>();
        var fromPreset = new HashSet<FormKey>();
        foreach (var (key, type) in j.HeadParts)
        {
            if (ctx.TryResolveRm<IHeadPartGetter>(key, out var hp)) fromPreset.Add(hp.FormKey);
            else unresolved.Add((key.ToString(), type));
        }
        foreach (var hp in effective) parts.Add(Part(hp, fromPreset.Contains(hp.FormKey) ? "preset" : "défaut de la race"));
        // Presets identiques au bit près (jslot écrasé par un autre dans RaceMenu : Malina, 000eria et 00akiratt étaient
        // devenus des copies d'akiraimperial)
        var own = ctx.PresetPath(row.Preset);
        if (File.Exists(own))
        {
            var mine = JslotHash(own);
            var twins = PresetTable.Read(ctx.CsvPath).Where(r => r.Id != row.Id)
                .Where(r => File.Exists(ctx.PresetPath(r.Preset)) && JslotHash(ctx.PresetPath(r.Preset)) == mine)
                .Select(r => r.Preset).ToList();
            if (twins.Count > 0)
                Check("attention", L.T("Copie exacte d'un autre preset : {0}. Si ce n'est pas voulu, l'un d'eux a été écrasé en "
                    + "sauvegardant dans RaceMenu : restaure l'original (archive du mod) ou exclus le doublon.", string.Join(", ", twins)));
        }

        // race Erin : prérequis, et headparts créés en jeu par son plugin SKSE (pas perdus)
        int erinParts = 0;
        if (Erin.IsRace(row.Race))
            Check("info", L.T("Race Erin : le follower demande le mod Erin Race (ErinRace.esm et son plugin SKSE) dans le jeu. Tête anime : la texture du visage n'est jamais remplacée par un skin."));
        foreach (var (u, type) in unresolved)
        {
            if (FormKey.TryFactory(u, out var uk) && Erin.IsDynamicHeadPart(ctx, uk, row.Race)) { erinParts++; continue; }
            // « 0E8834:Kala's Eyes - Improved.esp » : le plugin est-il installé dans le gestionnaire mais pas actif ?
            var plugin = u[(u.IndexOf(':') + 1)..];
            if (ctx.ActivePlugins.Contains(plugin))
                Check("info", L.T("Pièce du preset ({0}) introuvable dans {1} : plugin actif, mais la pièce n'y est plus (mod mis à jour ou compacté). {2} ignorée, comme dans RaceMenu.", PartLabel(type), plugin, u));
            else
                Check("attention", L.T("Pièce du preset ({0}) : plugin {1} manquant ({2}). Sans lui, le follower prend la pièce de la race, comme ce preset dans RaceMenu chez toi.", PartLabel(type), plugin, u)
                    + (mods != null ? $" {mods.PluginHint(plugin)}." : ""));
        }
        if (erinParts > 0)
            Check("info", L.T("{0} headpart(s) créé(s) en jeu par le plugin SKSE d'Erin Race (yeux 3D, sourcils, cicatrices…) : absents de tout plugin, normal pour cette race. Leur forme est dans l'export RaceMenu.", erinParts));

        // Mods absents (statut MODS_MANQUANTS) : exclu du ZIP complet par défaut, au choix de l'utilisateur
        var absent = j.Mods.Where(m => !ctx.ActivePlugins.Contains(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (absent.Count > 0)
        {
            var forFace = j.HeadParts.Select(h => h.Key.ModKey.FileName.String).Where(m => absent.Contains(m, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var other = absent.Except(forFace, StringComparer.OrdinalIgnoreCase).ToList();
            Check("attention", L.T("Mods du preset absents de ton installation : {0}.", string.Join(", ", absent)) + " "
                + (forFace.Count > 0 ? L.T("Headparts perdus (tête, cheveux, yeux, queue… de {0}) : le follower sera comme ce preset dans RaceMenu chez toi, sans eux (une pièce manquante du même type est remplacée par celle de la race).", string.Join(", ", forFace)) + " " : "")
                + (other.Count > 0 ? L.T("Autres références ({0}) : sans effet sur le visage ; le corps n'est pas encore géré par le générateur.", string.Join(", ", other)) + " " : "")
                + (row.Inclure ? "" : L.T("Exclu du ZIP complet par défaut : valide-le ici pour l'inclure dans le ZIP des validés."))
                + (mods != null ? " " + L.T("Où les trouver : {0}.", string.Join(" ; ", absent.Select(mods.PluginHint))) : ""));
        }

        // Formes attendues : headparts effectifs avec modèle, extras compris
        var expected = new List<string>();
        var invalid = new List<string>();
        void Walk(JsonNode p)
        {
            if (p["modele"] != null && p["nom"]?.GetValue<string>() is { } n && !expected.Contains(n)) expected.Add(n);
            // un headpart recréé prend la liste de races de la tête : il devient valide
            bool recreated = p["recree"]?.GetValue<bool>() == true;
            if (!recreated && (p["raceOk"]?.GetValue<bool>() == false || p["sexeOk"]?.GetValue<bool>() == false)) invalid.Add(p["edid"]?.GetValue<string>() ?? "?");
            foreach (var e in p["extras"]!.AsArray()) Walk(e!);
        }
        foreach (var p in parts) Walk(p!);
        foreach (var p in parts.Where(p => p!["origine"]!.GetValue<string>() != "preset"))
            Check("info", L.T("Ajouté par défaut par la race (type absent du preset) : {0}", p!["edid"]));
        if (invalid.Count > 0)
            // gardés tels quels : les recréer pour la race faisait rejeter le FaceGen (Psynwen, 19/10) ; le jeu les accepte
            Check("info", L.T("Hors de la liste de races ou de sexe ({0}), gardés tels quels (le jeu les accepte : la bouche par défaut des Hautes-Elfes est dans ce cas) : {1}", invalid.Count, string.Join(", ", invalid)));

        // Dépendances : plugins dont le follower aura besoin en jeu
        var deps = new JsonArray();
        if (allowed != null)
        {
            var used = effective.Where(h => allowed.Contains(h.FormKey.ModKey)).Select(h => h.FormKey.ModKey)
                .Append(race?.FormKey.ModKey ?? ModKey.Null).Where(k => !k.IsNull).Distinct().ToList();
            var recreatedFrom = new HashSet<string>();
            void Plugins(JsonNode p) { if (p["recree"]!.GetValue<bool>()) recreatedFrom.Add(p["plugin"]!.GetValue<string>()); foreach (var e in p["extras"]!.AsArray()) Plugins(e!); }
            foreach (var p in parts) Plugins(p!);
            foreach (var k in allowed.OrderBy(k => k.FileName.String)) if (ctx.Env.LoadOrder.ContainsKey(k)) deps.Add(k.FileName.String);
            Check("ok", L.T("Autonome : dépend seulement de {0}.", string.Join(", ", deps.Select(d => d!.GetValue<string>())))
                + (recreatedFrom.Count > 0 ? " " + L.T("Recréés dans le plugin du follower ou du pack (fichiers copiés) : headparts de {0}.", string.Join(", ", recreatedFrom.OrderBy(x => x))) : ""));
        }

        // Tête RaceMenu compagnon (Import Head) : choisie automatiquement ou à la main
        var choice = HeadImports.Resolve(ctx, row, effective);
        var import = new JsonObject
        {
            ["mode"] = choice.Mode, ["path"] = choice.Path, ["raison"] = choice.Reason,
            ["candidats"] = new JsonArray(HeadImports.Suggest(ctx, row, effective).Take(8)
                .Select(s => (JsonNode)new JsonObject { ["path"] = s.Path, ["raison"] = s.Reason }).ToArray()),
        };
        Check("info", choice.Path != null
            ? L.T("Export RaceMenu associé : CharGen\\{0} ({1}).", choice.Path, choice.Reason)
            : L.T("Aucun export RaceMenu associé ({0}).", choice.Reason));

        // FaceGen = export RaceMenu (seule méthode acceptée par le jeu, tests 11 à 13) : mêmes vérifications que le build
        var face = new JsonObject { ["attendues"] = new JsonArray(expected.Select(e => (JsonNode)e).ToArray()) };
        var export = ExportPath(ctx, row, effective);
        JsonObject? smpState = null;
        if (export == null)
        {
            Check("erreur", L.T("Pas d'export RaceMenu : la tête générée depuis le jslot est rejetée par le jeu (tête par défaut). "
                + "Charge le preset dans RaceMenu et fais « Export Head » sous le nom « {0} ».", Path.GetFileName(row.Preset)));
        }
        else
        {
            face["export"] = ctx.Files.RelOf(export);
            var strings = NifPatcher.ReadStrings(File.ReadAllBytes(export));
            var notes = new StringBuilder();
            var matched = FollowerBuilder.MatchExport(ctx, effective, strings, row, notes);
            // race Erin : son plugin SKSE reconstruit la tête en jeu depuis la fiche du PNJ (« Facegen NiNode Update » dans
            // ErinRace.log) : un écart entre l'export et le preset ne fait pas rejeter la tête (erina-suz, erin-rakia,
            // erin-celian vérifiées en jeu par l'utilisateur, 09/11) ; c'est la pièce du preset qui s'affiche
            bool erin = Erin.IsRace(row.Race);
            // Textures de l'export introuvables (vérifiées plus bas) : leur dossier sert aussi d'indice pour une pièce inconnue
            JsonArray? missingTextures = null;
            string? textureError = null;
            try
            {
                missingTextures = (Context.PythonEngine ? JsonNode.Parse(Py.Run(ctx, "export_textures.py", export))! : NativeTools.ExportTextures(ctx, export))["introuvables"]!.AsArray();
            }
            catch (Exception e) { textureError = e.Message; }
            // Pièces de l'export inconnues de l'ordre de chargement : yeux, sourcils… d'un mod désactivé ou désinstallé
            // depuis l'export ((R) Pike : « _Eyes02_HP », textures dans [namida1126]Yvonne Assets). Le NIF ne garde que le
            // nom de la pièce : le plugin est cherché parmi les plugins installés mais inactifs.
            var unknown = new List<string>();
            try
            {
                unknown = JsonNode.Parse(File.ReadAllText(Mesh(ctx, export, cache)))!["shapes"]!.AsArray()
                    .Select(s => s!["name"]!.GetValue<string>())
                    .Where(s => !string.IsNullOrEmpty(s) && !ctx.Cache.TryResolve<IHeadPartGetter>(s, out _) && !(erin && Erin.DynamicShapeType(s) != null)
                        && !(erin && s.Contains("Erin", StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception) { }
            foreach (var line in notes.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var text = line.Trim().Split("  ", 3, StringSplitOptions.RemoveEmptyEntries).Last();
                if (line.StartsWith("ATTENTION") && erin)
                    Check("info", L.T("FaceGen : {0}. Race Erin : sans effet en jeu, son plugin reconstruit la tête avec les pièces du preset.", text));
                else if (line.StartsWith("ATTENTION")) Check("erreur", L.T("FaceGen : {0} et aucun headpart du même type ne le remplace : le jeu rejettera probablement la tête.", text)
                    + " " + (unknown.Count > 0
                        ? L.T("L'export porte à la place des pièces d'un mod qui n'est pas actif chez toi (détail ci-dessous) : active ce mod, ou charge le preset dans RaceMenu et refais « Export Head ».")
                        : L.T("Ré-exporte avec ce headpart chargé (souvent : des yeux ou des sourcils d'un mod absent au moment de l'export).")));
                else Check("info", L.T("FaceGen : {0}.", text));
            }
            if (unknown.Count > 0)
            {
                var details = new List<string>();
                foreach (var s in unknown)
                {
                    var sources = mods?.HeadPartSources(s) ?? [];
                    if (sources.Count > 0)
                    {
                        var plugins = sources.Select(x => x.Plugin).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        details.Add(L.T("{0} ({1}) vient de {2} : {3}", s, PartLabel(sources[0].Type), string.Join(", ", plugins), mods!.PluginHint(plugins[0])));
                        continue;
                    }
                    // aucun plugin installé : le dossier de ses textures introuvables donne souvent le nom du mod
                    var folders = (missingTextures ?? []).Where(t => string.Equals(t!["forme"]?.GetValue<string>(), s, StringComparison.OrdinalIgnoreCase))
                        .Select(t => Path.GetDirectoryName(t!["texture"]!.GetValue<string>())).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    details.Add(mods == null
                        ? L.T("{0} : plugin inconnu (aucun plugin actif ne le définit)", s)
                        : L.T("{0} : aucun plugin installé chez toi ne le définit (mod désinstallé ?)", s)
                          + (folders.Count > 0 ? ", " + L.T("indice : ses textures sont dans {0}", string.Join(", ", folders)) : ""));
                }
                Check(erin ? "attention" : "erreur", L.T("FaceGen : pièce(s) de l'export inconnue(s) de ton ordre de chargement : {0}.", string.Join(" ; ", details))
                    + " " + (erin ? L.T("Race Erin : la tête n'est pas rejetée, mais le follower aura les pièces du preset.")
                        : L.T("Le jeu rejettera probablement la tête tant que ce mod n'est pas actif.")));
            }
            // Pièce en trop : headpart dans l'export mais pas sur le PNJ. Le jeu rejette aussi la tête (nyridieRd ube, test
            // GF_Skins_Diag du 19/10 : export « UBE Nyridie » fait depuis un autre preset, avec PIE_AANoHorns en plus)
            var npcParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddPart(IHeadPartGetter h)
            {
                if (h.EditorID != null && !npcParts.Add(h.EditorID)) return;
                foreach (var e in h.ExtraParts) if (ctx.Cache.TryResolve<IHeadPartGetter>(e.FormKey, out var x)) AddPart(x);
            }
            matched.ForEach(AddPart);
            var extra = strings.Where(s => !npcParts.Contains(s) && ctx.Cache.TryResolve<IHeadPartGetter>(s, out var hp)
                && hp.Model?.File is { } f && !string.IsNullOrEmpty(f.ToString())).ToList();
            if (extra.Count > 0 && erin)
                Check("attention", L.T("FaceGen : pièce(s) de l'export absente(s) du preset : {0}. Race Erin : la tête n'est pas rejetée, mais "
                    + "le follower aura les pièces du preset, pas celles-ci. Si l'export vient d'un autre preset, choisis le bon export ci-dessous.",
                    string.Join(", ", extra)));
            else if (extra.Count > 0)
                Check("erreur", L.T("FaceGen : pièce(s) de l'export absente(s) du preset : {0}. Le jeu rejette la tête : "
                    + "cet export a sans doute été fait depuis un autre preset. Choisis le bon export ci-dessous, ou charge ce preset dans "
                    + "RaceMenu et fais « Export Head » sous le nom « {1} ».", string.Join(", ", extra), Path.GetFileName(row.Preset)));
            // Preset modifié après l'export (jslot écrasé dans RaceMenu sans refaire « Export Head ») : le follower aura
            // la tête de l'export, pas les changements du preset
            var jslotPath = ctx.PresetPath(row.Preset);
            if (File.Exists(jslotPath) && File.GetLastWriteTime(jslotPath) > File.GetLastWriteTime(export).AddMinutes(1))
                Check("attention", L.T("Export à refaire : le preset a été modifié le {0:g}, après son export "
                    + "({1:g}). Le follower aura la tête de l'export. Si tu as changé le visage ou des pièces, "
                    + "refais « Export Head » sous le nom « {2} ».", File.GetLastWriteTime(jslotPath), File.GetLastWriteTime(export), Path.GetFileName(row.Preset)));
            // Teinte livrée avec l'export (même nom, .dds) : sinon le build calcule la teinte depuis le jslot
            if (!File.Exists(Path.ChangeExtension(export, ".dds")))
                Check("info", L.T("Pas de teinte .dds à côté de l'export : la couleur du visage est calculée depuis le preset."));
            int phys = strings.Count(s => s.StartsWith("hdtSSEPhysics_AutoRename", StringComparison.OrdinalIgnoreCase));
            if (phys > 0)
            {
                // cheveux SMP : perruque (de l'auteur du pack, ou fabriquée à partir de la coiffure) selon le choix du follower
                smpState = SmpHair.Probe(ctx, row.Id, SmpHair.PartsOf(ctx, effective), export);
                var smpChoice = SmpHair.Choice(Options.For(ctx, row.Id));
                bool installed = SmpHair.SmpInstalled(ctx), on = smpChoice ?? installed;
                var wigName = smpState["perruque"]?.GetValue<string>();
                bool made = smpState["fabriquee"]?.GetValue<bool>() == true;
                if (!on)
                    Check("info", smpChoice == false
                        ? L.T("Cheveux à physique SMP : rigides (choix « Rigides », section Avancé). Ils suivent la tête, sans mouvement.")
                        : L.T("Cheveux à physique SMP : rigides, car Faster HDT-SMP n'est pas détecté (SKSE\\Plugins\\hdtSMP64.dll). Choisis « Animés » dans la section Avancé pour forcer."));
                else if (wigName == null)
                    Check("info", L.T("Cheveux à physique SMP : rigides, aucun fichier de physique (.xml) trouvé pour cette coiffure."));
                else
                {
                    Check("info", made
                        ? L.T("Cheveux à physique SMP : animés, avec une perruque fabriquée à partir de la coiffure ({0}).", wigName)
                        : L.T("Cheveux à physique SMP : animés, avec la perruque de l'auteur du pack ({0}).", wigName));
                    if (!installed)
                        Check("attention", L.T("« Animés » est choisi mais Faster HDT-SMP n'est pas détecté : la perruque restera immobile en jeu tant que SMP n'est pas installé."));
                }
            }
            if (textureError != null) Check("info", L.T("Vérification des textures impossible : {0}", textureError));
            else
            {
                // « textures\actors\NULL » : emplacement laissé vide exprès par le plugin Erin Race (base des yeux 3D), sans effet en jeu
                var tex = missingTextures!
                    .Where(t => !(Erin.IsRace(row.Race) && Path.GetFileName(t!["texture"]!.GetValue<string>()).Equals("NULL", StringComparison.OrdinalIgnoreCase))).ToList();
                if (tex.Count > 0)
                {
                    // Réparation : une texture du même nom installée ailleurs (déployée dans Data) → bouton qui l'ajoute à
                    // texture_remap.json ; sinon, le mod du gestionnaire qui la contient sans être déployé
                    var actions = new JsonArray();
                    var hints = new List<string>();
                    foreach (var missingTex in tex.Select(t => t!["texture"]!.GetValue<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var cands = mods?.Texture(missingTex) ?? [];
                        var best = cands.FirstOrDefault(x => x.Deployed);
                        var key = missingTex.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? missingTex[9..] : missingTex;
                        if (best != null)
                            actions.Add(new JsonObject { ["from"] = key, ["to"] = best.Rel, ["mod"] = best.Mod });
                        else if (cands.Count > 0)
                            hints.Add(mods!.TextureHint(Path.GetFileName(missingTex), cands[0].Mod));
                        else
                            hints.Add(L.T("{0} : introuvable dans tes mods (dossier attendu : {1})", Path.GetFileName(missingTex), Path.GetDirectoryName(key)));
                    }
                    checks.Add(new JsonObject
                    {
                        ["niveau"] = "attention",
                        ["texte"] = L.T("Textures de l'export introuvables chez toi (affichées en violet en jeu) : {0}.",
                                string.Join(", ", tex.Select(t => $"{t!["texture"]} ({t["forme"]})").Distinct()))
                            + (actions.Count > 0 ? " " + L.T("Remplacement proposé ci-dessous (texture du même nom installée chez toi).") : "")
                            + (hints.Count > 0 ? " " + string.Join(" ; ", hints) + "." : ""),
                        ["reparations"] = actions,
                    });
                }
            }
            Check("ok", L.T("FaceGen = export RaceMenu CharGen\\{0}, utilisé tel quel (méthode validée en jeu).", ctx.CharGenRelOf(export)));
        }
        var nif = export ?? HeadNif(ctx, row);
        if (export == null && File.Exists(nif))
        {
            var mesh = JsonNode.Parse(File.ReadAllText(Mesh(ctx, nif, cache)))!["shapes"]!.AsArray();
            var shapes = mesh.Select(s => s!["name"]!.GetValue<string>()).ToList();
            var missing = expected.Where(e => !shapes.Contains(e)).ToList();
            var extra = shapes.Where(s => !expected.Contains(s)).ToList();
            var stat = mesh.Where(s => s!["block"]!.GetValue<string>() != "BSDynamicTriShape").Select(s => s!["name"]!.GetValue<string>()).ToList();
            face["formes"] = new JsonArray(mesh.Select(s => (JsonNode)new JsonObject { ["nom"] = s!["name"]!.GetValue<string>(), ["bloc"] = s["block"]!.GetValue<string>() }).ToArray());
            face["manquantes"] = new JsonArray(missing.Select(e => (JsonNode)e).ToArray());
            face["enTrop"] = new JsonArray(extra.Select(e => (JsonNode)e).ToArray());
            if (missing.Count > 0) Check("erreur", L.T("FaceGen : formes attendues absentes du NIF : {0}. "
                + "Si leurs headparts sont maintenant résolus, la tête est probablement périmée : clique sur « Générer / régénérer la tête ».", string.Join(", ", missing)));
            if (extra.Count > 0) Check("attention", L.T("FaceGen : formes sans headpart correspondant : {0}", string.Join(", ", extra)));
            if (stat.Count > 0) Check("erreur", L.T("FaceGen : formes non dynamiques (BSTriShape) : {0}", string.Join(", ", stat)));
            if (missing.Count == 0 && extra.Count == 0 && stat.Count == 0) Check("info", L.T("Tête générée (aperçu 3D seulement, non utilisable en jeu) : {0} formes, une par headpart attendu.", shapes.Count));
            face["date"] = File.GetLastWriteTime(nif).ToString("g");
        }
        else if (export == null) Check("info", L.T("Tête générée absente : seulement utile pour l'aperçu 3D (bouton « Générer la tête »)."));
        if (export != null) face["date"] = File.GetLastWriteTime(export).ToString("g");

        // Voix finale (choisie, sinon presets.csv) : sans répliques de follower nulle part, recrutement impossible par le
        // dialogue (attention : verdict orange) ; répliques apportées par un mod de l'utilisateur : information seulement
        if (Voices.FinalVoice(ctx, row) is { } voiceKey)
        {
            var vd = Voices.FollowerOf(ctx, voiceKey);
            var vname = ctx.Cache.TryResolve<IVoiceTypeGetter>(voiceKey, out var vt) ? vt.EditorID : voiceKey.ToString();
            if (vd.Level == "aucun")
                Check("attention", L.T("Voix {0} sans répliques de follower (ni dans le jeu ni dans tes mods) : impossible de le recruter par le dialogue. Change de voix (section Voix).", vname));
            else if (vd.Level == "mod")
                Check("info", L.T("Voix {0} : répliques de follower ajoutées par {1} (absentes du jeu de base : un joueur sans ce mod ne pourrait pas le recruter).", vname, vd.Plugins));
            if (vd.RemovedBy != null)
                Check("info", L.T("Voix {0} retirée d'une liste de dialogue de follower du jeu par {1} : à vérifier en jeu.", vname, vd.RemovedBy));
        }
        // cohérence des choix (style ↔ armes et sorts, sorts ↔ classe, mariage ↔ voix)
        foreach (var (level, text) in Options.Coherence(ctx, row)) Check(level, text);

        // Verdict « prêt pour le jeu » : rouge = la tête sera rejetée ou il manque l'export ; orange = passera, avec un
        // défaut visible (texture violette, export plus ancien que le preset…) ; vert = rien à signaler
        // Sans export (.nif) : pas un défaut du preset, la tête reste à construire dans RaceMenu (niveau à part)
        // Textes en français (mémorisés dans work\verdicts.json) : l'interface les traduit (builder\i18n)
        var levels = checks.Select(k => k!["niveau"]!.GetValue<string>()).ToList();
        var verdict = export == null
            ? new JsonObject { ["niveau"] = "export", ["texte"] = "Pas encore d'export : tête à construire dans RaceMenu (Export Head)." }
            : levels.Contains("erreur")
            ? new JsonObject { ["niveau"] = "rouge", ["texte"] = "Ne passera pas en jeu : il manque une pièce dans l'export (voir les erreurs ci-dessous)." }
            : levels.Contains("attention")
                ? new JsonObject { ["niveau"] = "orange", ["texte"] = "Passera en jeu, avec des défauts à corriger : voir ci-dessous." }
                : new JsonObject { ["niveau"] = "vert", ["texte"] = "Prêt pour le jeu." };
        verdict["date"] = DateTime.Now.ToString("s");
        SaveVerdict(ctx, row.Id, verdict);

        return new JsonObject
        {
            ["verdict"] = verdict.DeepClone(),
            ["id"] = row.Id, ["nom"] = row.Nom, ["nomPreset"] = row.NomPreset, ["preset"] = row.Preset, ["categorie"] = row.Categorie,
            ["race"] = row.Race, ["sexe"] = row.Sexe, ["voix"] = row.Voix, ["statut"] = row.Statut, ["details"] = row.Details,
            // couleur des cheveux du preset (0xRRGGBB) : le jeu teinte les formes « Hair Tint » du FaceGen avec elle
            ["couleurCheveux"] = j.HairColor,
            // cheveux SMP (section Avancé) : { smp, installe, xml, perruque, fabriquee, pieces }, null sans export
            ["cheveuxSmp"] = smpState ?? new JsonObject { ["smp"] = false, ["installe"] = SmpHair.SmpInstalled(ctx) },
            ["peau"] = j.SkinTone is { } st ? $"#{st & 0xFFFFFF:X6}" : null,
            ["headparts"] = parts, ["facegen"] = face, ["verifications"] = checks, ["dependances"] = deps, ["import"] = import,
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

    /// <summary>Tête générée (aperçu 3D) : GF_&lt;id&gt;.json, déjà au format de l'aperçu (moteur C#, HeadGen) ; .nif d'assemble.py
    /// avec GF_MOTEUR=python.</summary>
    static string HeadNif(Context ctx, PresetRow r) => Path.Combine(ctx.WorkDir, "heads_offline", r.ExportName + (Context.PythonEngine ? ".nif" : ".json"));

    /// <summary>Export RaceMenu associé au preset (automatique ou choisi à la main), ou null.</summary>
    static string? ExportPath(Context ctx, PresetRow row, List<IHeadPartGetter> effective)
        => HeadImports.Resolve(ctx, row, effective).Path is { } p && ctx.CharGenPath(p) is var file && File.Exists(file) ? file : null;

    static bool HasExport(Context ctx, PresetRow r)
    {
        var fiche = Path.Combine(ctx.WorkDir, "headparts", r.Id + ".json");
        return File.Exists(fiche) && JsonNode.Parse(File.ReadAllText(fiche))?["import"]?.GetValue<string>() is { } p && File.Exists(p);
    }

    /// <summary>Tête affichée en 3D : l'export RaceMenu s'il existe (c'est lui qui part en jeu), sinon la tête générée.</summary>
    static string ShownHead(Context ctx, PresetRow row)
    {
        var race = ctx.Find<IRaceGetter>(row.Race);
        var effective = HeadParts.Effective(ctx, Jslot.Load(ctx.PresetPath(row.Preset)), race, row.Female);
        return ExportPath(ctx, row, effective) ?? HeadNif(ctx, row);
    }

    /// <summary>Chemin d'un .nif à afficher : absolu, ou relatif à Data (loose seulement ; Data virtuel sous MO2 lu
    /// directement).</summary>
    static string Resolve(Context ctx, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) throw new GfException("chemin manquant");
        if (Path.IsPathRooted(rel)) return File.Exists(rel) ? rel : throw new GfException($"introuvable : {rel}");
        // loose d'abord, puis fichiers déjà extraits des BSA (offline\bsa_cache\<archive>\...)
        var candidates = new[] { ctx.Files.PathOf(rel) }
            .Concat(Directory.Exists(Path.Combine(ctx.Root, "offline", "bsa_cache"))
                ? Directory.EnumerateDirectories(Path.Combine(ctx.Root, "offline", "bsa_cache")).Select(d => Path.Combine(d, rel))
                : []);
        // moteur C# : sinon extrait des BSA à la demande (work\bsa_cache)
        return candidates.FirstOrDefault(File.Exists) ?? (Context.PythonEngine ? null : ctx.Bsa.Find(rel))
            ?? throw new GfException($"introuvable (loose ou BSA déjà extrait) : {rel}");
    }

    /// <summary>Géométrie JSON d'un .nif (NativeTools.ExportMesh, ou offline\export_mesh.py avec GF_MOTEUR=python), en
    /// cache tant que le .nif ne change pas.</summary>
    static string Mesh(Context ctx, string nif, string cache)
    {
        if (!File.Exists(nif)) throw new GfException($"NIF introuvable : {nif}");
        // tête générée par le moteur C# : déjà la géométrie de l'aperçu
        if (nif.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return nif;
        // version du format : v2 = teinte FaceGen (tint) ; v3 = position par l'os de la tête (yeux de thorRg sous la
        // tête). Les anciens JSON en cache sont refaits.
        var outFile = Path.Combine(Context.Dir(Path.Combine(cache, "mesh")), Hash(nif + "|v7") + ".json");   // v4 : hairTint ; v5 : normal, spéculaire ; v6 : peau (os, poids) ; v7 : poids des NiTriShape, parents des os
        if (!File.Exists(outFile) || File.GetLastWriteTimeUtc(outFile) < File.GetLastWriteTimeUtc(nif))
        {
            if (Context.PythonEngine) Py.Run(ctx, "export_mesh.py", nif, outFile);
            else File.WriteAllText(outFile, NativeTools.ExportMesh(nif).ToJsonString());
        }
        return outFile;
    }

    /// <summary>
    /// Corps de base du follower pour l'aperçu « corps entier », habillé comme en jeu : ArmorAddons de la peau de la race
    /// (WNAM) pour le corps, les mains, les pieds et la queue (le premier pour la race, par emplacement), mesh du sexe du
    /// follower ; sommets mélangés entre _0 et _1 selon le poids du preset ; diffuse = texture alternative de la forme,
    /// sinon SkinTexture de l'ArmorAddon (formes de peau), sinon celle du NIF. C'est le corps installé (BodySlide) ; avec
    /// <paramref name="presetName"/>, les morphs de son .tri reçoivent les valeurs du preset, comme OBody en jeu.
    /// <paramref name="outfit"/> : tenue portée (Options.WornArmors), ArmorAddons de chaque armure pour la race (sinon pour
    /// sa race d'armure), chargés de la même façon ; les parties nues dont l'emplacement est pris par une armure ne sont
    /// pas dessinées, comme en jeu.
    /// </summary>
    static JsonObject Body(Context ctx, PresetRow row, string cache, string? presetName = null, bool outfit = false)
    {
        var race = ctx.Find<IRaceGetter>(row.Race) ?? throw new GfException(L.T("Race introuvable : {0}", row.Race));
        if (!ctx.Cache.TryResolve<IArmorGetter>(race.Skin.FormKey, out var skin))
            throw new GfException(L.T("Peau de la race {0} introuvable : pas de corps à afficher.", race.EditorID ?? row.Race));
        var weight = Math.Clamp(Jslot.Load(ctx.PresetPath(row.Preset)).Weight, 0f, 100f);
        var body = Skins.BodyOf(ctx, row);
        var erin = Erin.IsRace(row.Race);
        var preset = string.IsNullOrEmpty(presetName) ? null
            : BodyPresets.For(ctx, row).FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase))
              ?? throw new GfException(L.T("Preset de corps inconnu ou incompatible avec ce follower : {0}", presetName));
        var values = preset != null ? BodyPresets.Values(preset, weight) : null;
        int morphed = 0, sliders = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const BipedObjectFlag slots = BipedObjectFlag.Body | BipedObjectFlag.Hands | BipedObjectFlag.Feet | BipedObjectFlag.Tail;
        BipedObjectFlag seen = 0;
        var shapes = new JsonArray();
        var meshes = new JsonArray();
        bool ForRace(IArmorAddonGetter aa, FormKey r) => aa.Race.FormKeyNullable == r || aa.AdditionalRaces.Any(x => x.FormKey == r);

        // tenue : armures portées et emplacements qu'elles occupent
        BipedObjectFlag covered = 0;
        var pieces = new JsonArray();
        var worn = outfit ? Options.WornArmors(ctx, row) : ("", new List<IArmorGetter>());
        foreach (var armor in worn.Item2)
        {
            var addons = armor.Armature.Select(l => ctx.Cache.TryResolve<IArmorAddonGetter>(l.FormKey, out var a) ? a : null).OfType<IArmorAddonGetter>().ToList();
            var mine = addons.Where(a => ForRace(a, race.FormKey)).ToList();
            if (mine.Count == 0 && !race.ArmorRace.IsNull) mine = addons.Where(a => ForRace(a, race.ArmorRace.FormKey)).ToList();
            // race Erin : armures portées comme une Nordique (proxy du plugin Erin, voir OptionsModItems.PieceFitsRace)
            if (mine.Count == 0 && erin && ctx.Find<IRaceGetter>("NordRace") is { } nord)
                mine = addons.Where(a => ForRace(a, nord.FormKey) || (!nord.ArmorRace.IsNull && ForRace(a, nord.ArmorRace.FormKey))).ToList();
            int n = mine.Sum(aa => AddModel(aa, "armor"));
            // emplacements pris même sans modèle pour la race (pièce invisible en jeu : le corps nu n'y est pas non plus)
            covered |= armor.BodyTemplate?.FirstPersonFlags ?? 0;
            pieces.Add(new JsonObject
            {
                ["nom"] = armor.Name?.String ?? armor.EditorID, ["formes"] = n,
                // pièce sans modèle : races de ses ArmorAddons (diagnostic)
                ["races"] = n > 0 ? null : string.Join(", ", addons.SelectMany(a => a.AdditionalRaces.Select(r => r.FormKey).Prepend(a.Race.FormKey))
                    .Distinct().Select(k => ctx.Cache.TryResolve<IRaceGetter>(k, out var rr) ? rr.EditorID : k.ToString())),
            });
        }

        foreach (var link in skin.Armature)
        {
            if (!ctx.Cache.TryResolve<IArmorAddonGetter>(link.FormKey, out var aa) || !ForRace(aa, race.FormKey)) continue;
            var flags = aa.BodyTemplate?.FirstPersonFlags ?? 0;
            if ((flags & slots) == 0 || (flags & (BipedObjectFlag.Head | BipedObjectFlag.Hair)) != 0 || (flags & seen & slots) != 0) continue;
            seen |= flags & slots;
            if ((flags & covered) != 0) continue;   // sous une armure
            AddModel(aa, flags.HasFlag(BipedObjectFlag.Body) ? "body" : flags.HasFlag(BipedObjectFlag.Hands) ? "hands" : flags.HasFlag(BipedObjectFlag.Feet) ? "feet" : "tail");
        }

        // Formes d'un ArmorAddon (mesh du sexe du follower), ajoutées à shapes ; renvoie leur nombre
        int AddModel(IArmorAddonGetter aa, string part)
        {
            var model = row.Female ? aa.WorldModel?.Female : aa.WorldModel?.Male;
            var rel = model?.File?.DataRelativePath.Path;
            if (string.IsNullOrEmpty(rel) || ctx.FindMesh(rel) is not { } nif) return 0;
            int added = 0;
            // poids : le jeu mélange les sommets de _0 (poids 0) et _1 (poids 100)
            var m = System.Text.RegularExpressions.Regex.Match(nif, @"_([01])\.nif$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string? thin = null, fat = nif;
            if (m.Success)
            {
                var other = ctx.FindMesh(System.Text.RegularExpressions.Regex.Replace(rel, @"_[01]\.nif$", m.Groups[1].Value == "1" ? "_0.nif" : "_1.nif",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                (thin, fat) = m.Groups[1].Value == "1" ? (other, nif) : (nif, other ?? nif);
            }
            var fatShapes = JsonNode.Parse(File.ReadAllText(Mesh(ctx, fat, cache)))!["shapes"]!.AsArray();
            var thinShapes = thin != null && thin != fat ? JsonNode.Parse(File.ReadAllText(Mesh(ctx, thin, cache)))!["shapes"]!.AsArray() : null;
            var skinSet = (row.Female ? aa.SkinTexture?.Female : aa.SkinTexture?.Male) is { IsNull: false } st
                && ctx.Cache.TryResolve<ITextureSetGetter>(st.FormKey, out var txst) ? txst : null;
            var skinTex = skinSet?.Diffuse?.DataRelativePath.Path;
            meshes.Add(new JsonObject { ["partie"] = part, ["mesh"] = rel, ["mixte"] = thinShapes != null });
            var tri = values != null && BodyPresets.BodyTri(ctx, fat) is { } triPath ? BodyPresets.ReadTri(triPath) : null;
            foreach (var s in fatShapes.Select(n => n!.AsObject()))
            {
                var name = s["name"]!.GetValue<string>();
                if (thinShapes?.FirstOrDefault(t => t!["name"]!.GetValue<string>() == name) is { } t
                    && t["verts"]!.AsArray() is var tv && s["verts"]!.AsArray() is var fv && tv.Count == fv.Count)
                {
                    var mixed = new JsonArray();
                    for (int i = 0; i < fv.Count; i++)
                        mixed.Add(Math.Round(tv[i]!.GetValue<double>() + (fv[i]!.GetValue<double>() - tv[i]!.GetValue<double>()) * weight / 100.0, 4));
                    s["verts"] = mixed;
                }
                // preset BodySlide : décalage de chaque morph × valeur du curseur (forme du .tri du même nom)
                if (tri != null && values != null && tri.TryGetValue(name, out var morphs))
                {
                    var v = s["verts"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
                    foreach (var (slider, val) in values)
                    {
                        if (val == 0 || !morphs.TryGetValue(slider, out var offsets)) continue;
                        used.Add(slider);
                        foreach (var (i, dx, dy, dz) in offsets)
                            if (i * 3 + 2 < v.Length) { v[i * 3] += dx * val; v[i * 3 + 1] += dy * val; v[i * 3 + 2] += dz * val; }
                    }
                    s["verts"] = new JsonArray(v.Select(x => (JsonNode)Math.Round(x, 4)).ToArray());
                    morphed++;
                }
                var diffuse = s["diffuse"]?.GetValue<string>() ?? "";
                // forme de peau (shader Skin Tint) : teinte de peau du PNJ (QNAM) et texture de peau de la race
                var skinShape = s["shader"]?.GetValue<int>() == 5;
                var altSet = model!.AlternateTextures?.FirstOrDefault(a => a.Name == name) is { } at
                    && ctx.Cache.TryResolve<ITextureSetGetter>(at.NewTexture.FormKey, out var atx) ? atx : null;
                var shownSet = altSet ?? (skinShape ? skinSet : null);
                static string Tex(string p) => p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? p : @"textures\" + p;
                if (shownSet?.Diffuse?.DataRelativePath.Path is { Length: > 0 } shown)
                {
                    s["diffuse"] = Tex(shown);
                    // le jeu prend tout le jeu de textures : normal map et brillance suivent la diffuse
                    if (shownSet.NormalOrGloss?.DataRelativePath.Path is { Length: > 0 } n) s["normal"] = Tex(n);
                    if (shownSet.BacklightMaskOrSpecular?.DataRelativePath.Path is { Length: > 0 } sp) s["specular"] = Tex(sp);
                }
                s["skinTint"] = skinShape;
                // partie du skin choisi (body, hands), d'après la diffuse d'origine comme au build ; pour la peau visible
                // d'une armure, c'est skinZone ci-dessous (le jeu y met aussi le skin du follower)
                s["skinPart"] = part == "armor" ? null : Skins.SkinPart(body, row.Sexe, diffuse, erin);
                // peau visible d'une armure (shader Skin Tint, texture du corps ou des mains) : zone des overlays du corps,
                // que SKEE pose aussi sur elle en jeu
                if (part == "armor" && skinShape) s["skinZone"] = Skins.SkinPart(body, row.Sexe, diffuse, erin);
                s["part"] = part;
                shapes.Add(s.DeepClone());
                added++;
            }
            return added;
        }
        if (shapes.Count == 0) throw new GfException(L.T("Aucun mesh de corps trouvé dans la peau de la race {0}.", race.EditorID ?? row.Race));
        // os de la tête dans le squelette de la race (pose de repos) : les têtes (exports RaceMenu, FaceGen) sont dans le
        // repère de cet os, le jeu les y attache. Matrice 4×4 par lignes (rotation × échelle, translation).
        JsonArray? headBone = null;
        var bones = new JsonArray();
        static JsonArray Rows(Xform g) => [g.R[0, 0] * g.S, g.R[0, 1] * g.S, g.R[0, 2] * g.S, g.Tx, g.R[1, 0] * g.S, g.R[1, 1] * g.S, g.R[1, 2] * g.S, g.Ty,
            g.R[2, 0] * g.S, g.R[2, 1] * g.S, g.R[2, 2] * g.S, g.Tz, 0, 0, 0, 1];
        var skel = (row.Female ? race.SkeletalModel?.Female : race.SkeletalModel?.Male)?.File?.DataRelativePath.Path;
        if (!string.IsNullOrEmpty(skel) && ctx.FindMesh(skel) is { } skelNif)
        {
            var sk = Nif.Load(skelNif);
            if (sk.Node("NPC Head [Head]") is { } hb) headBone = Rows(sk.GlobalTransform(hb));
            // os du squelette au repos (pose de l'aperçu) : nom, parent, matrice globale par lignes
            foreach (var n in sk.File.Blocks.OfType<NiflySharp.Blocks.NiNode>().Where(n => !string.IsNullOrEmpty(n.Name?.String)))
                bones.Add(new JsonObject { ["nom"] = n.Name!.String, ["parent"] = sk.File.GetParentNode(n)?.Name?.String, ["m"] = Rows(sk.GlobalTransform(n)) });
        }
        sliders = values?.Count(kv => kv.Value != 0) ?? 0;
        return new JsonObject
        {
            ["shapes"] = shapes, ["poids"] = weight, ["corps"] = Skins.BodyLabel(body), ["meshes"] = meshes, ["osTete"] = headBone, ["squelette"] = skel,
            // hauteur de la race pour ce sexe (le jeu multiplie la taille du PNJ par elle : Hauts-Elfes 1,1…)
            ["hauteurRace"] = row.Female ? race.Height.Female : race.Height.Male,
            ["os"] = bones,
            // preset appliqué : curseurs non nuls du preset, dont trouvés dans les .tri du corps (les autres sont d'un autre corps)
            ["preset"] = preset?.Name, ["curseurs"] = sliders, ["curseursAppliques"] = used.Count, ["formesMorphees"] = morphed,
            // tenue : nom, pièces (formes dessinées : 0 = pas de modèle pour la race), cheveux sous un casque ou une capuche
            ["tenue"] = outfit ? worn.Item1 : null, ["pieces"] = pieces,
            ["raceArmure"] = race.ArmorRace.IsNull ? null : ctx.Cache.TryResolve<IRaceGetter>(race.ArmorRace.FormKey, out var ar) ? ar.EditorID : race.ArmorRace.FormKey.ToString(),
            ["cacheCheveux"] = (covered & (BipedObjectFlag.Hair | BipedObjectFlag.Head)) != 0,
        };
    }

    /// <summary>Texture du jeu convertie en PNG 1024² (NativeTools.DdsPng, ou offline\dds_png.py avec GF_MOTEUR=python),
    /// en cache.</summary>
    static string Texture(Context ctx, string rel, string cache)
    {
        rel = Remap(ctx, rel);
        // teinte d'un export RaceMenu : « Data\SKSE\Plugins\CharGen\…\x.dds », relative au dossier du jeu (PyNifly
        // y ajoute « textures\ »)
        var bare = rel.StartsWith(@"textures\Data\", StringComparison.OrdinalIgnoreCase) ? rel[9..] : rel;
        if (bare.StartsWith(@"Data\", StringComparison.OrdinalIgnoreCase)) rel = ctx.Files.PathOf(bare[5..]);
        // un fichier absolu peut changer (tête régénérée) : sa date entre dans la clé du cache
        var key = Path.IsPathRooted(rel) && File.Exists(rel) ? rel + "|" + File.GetLastWriteTimeUtc(rel).Ticks : rel;
        // « |r2 » : PNG refaits depuis la réduction sans alpha prémultiplié (02/11 : relief des normal maps abîmé avant)
        var outFile = Path.Combine(Context.Dir(Path.Combine(cache, "tex")), Hash((key + "|r2").ToLowerInvariant()) + ".png");
        if (!File.Exists(outFile))
        {
            if (Context.PythonEngine) Py.Run(ctx, "dds_png.py", rel, outFile, "1024");
            else NativeTools.DdsPng(ctx, rel, outFile, 1024);
        }
        return outFile;
    }

    /// <summary>Texture de remplacement de work\texture_remap.json, comme au build : l'aperçu montre ce que le jeu
    /// affichera (Psynwen : diffuse highelffemale absente, tête noire sans redirection). Clés brutes du .nif, avec
    /// ou sans « textures\ ».</summary>
    static string Remap(Context ctx, string rel)
    {
        var path = Path.Combine(ctx.WorkDir, "texture_remap.json");
        if (!File.Exists(path)) return rel;
        var raw = rel.Replace('/', '\\');
        var bare = raw.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? raw[9..] : raw;
        foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!)
            if (kv.Key.Equals(raw, StringComparison.OrdinalIgnoreCase) || kv.Key.Equals(bare, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return rel;
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
