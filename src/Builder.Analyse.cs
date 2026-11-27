using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>Follower Builder : analyse d'un preset (headparts, formes attendues, export RaceMenu, verdict « prêt pour le jeu »).</summary>
public static partial class Builder
{
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
        // tête d'un autre preset (option « teteDonneur ») : ce sont les pièces du donneur que le follower portera
        var donor = HeadWrite.DonorRow(ctx, row);
        var effective = HeadParts.Effective(ctx, HeadWrite.DonorJslot(ctx, row.Preset) ?? j, race, row.Female);
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
            var (phys, facePhys) = SmpHair.PhysicsBones(strings);
            // tête « SMP Head » : os de physique du visage dans l'export ; le follower garde son visage et ses expressions,
            // ces os sont ramenés sur la tête (lèvres sans physique, pas encore prise en charge)
            var smpNow = ctx.Bodies.SmpFor(row.Race);
            if (facePhys > 0)
                Check("info", L.T("Tête « SMP Head » : {0} os de physique du visage (lèvres, joues, gorge) dans l'export. Le follower garde son visage et ses expressions ; les lèvres n'auront pas de physique.", facePhys)
                    + (smpNow == false ? " " + L.T("Note : SMP Head n'est plus installé chez toi (cet export date d'avant). Ce follower n'en a pas besoin : sa tête vient de l'export.") : ""));
            else if (smpNow == true)
                Check("info", L.T("Export fait sans SMP Head (avant de l'installer, ou avec une autre tête), alors qu'il est installé maintenant. Ce follower n'en a pas besoin : sa tête vient de l'export. Refais « Export Head » si tu veux ses os de visage."));
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
        // export abîmé (textures du visage sur d'autres pièces) : réparé au build, dit ici
        if (export != null && ExportRepair.Damaged(ctx, export) is { Count: > 0 } damaged)
            Check("info", L.T("Export abîmé : la texture du visage a aussi été écrite sur {0} (vu avec RaceMenu 0.5.0 b7) ; en jeu, la tête serait dessinée deux fois. Le build remet leurs textures d'origine.", string.Join(", ", damaged)));
        // tête d'un autre preset : visage du preset écrit sur la tête du donneur (même calcul que le build), sinon dit
        bool? faceWritten = null;
        if (donor != null && export != null)
        {
            var dp = DonorPreview(ctx, row, export);
            // calcul impossible (tête jamais générée : pas de fiche de pièces) : on ne sait pas encore, le donneur n'est pas en cause
            bool unknown = dp is { } q && q.Log.Split('\n').Any(l => l.StartsWith("ERREUR ", StringComparison.Ordinal));
            faceWritten = unknown ? null : dp is { } p && HeadWrite.FaceWritten(p.Log);
            if (unknown)
                Check("attention", L.T("Tête de {0} (sans Export Head) : clique « Générer / régénérer la tête » pour vérifier que le visage de ce preset peut y être écrit.", donor.Nom));
            else if (faceWritten == true)
                Check("info", L.T("Tête de {0} (sans Export Head) : yeux, sourcils, cils et cheveux de {0}, visage, teinte et couleurs de ce preset ; poids et cou de {0}.", donor.Nom));
            else
                Check("attention", L.T("Tête de {0} (sans Export Head) : le visage de ce preset n'a pas pu être écrit sur cette tête ; le follower aura le visage de {0}. Essaie un autre donneur.", donor.Nom));
        }

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
                : donor != null ? new JsonObject { ["niveau"] = "vert", ["texte"] = "Prêt pour le jeu, avec la tête d'un autre preset." }
                : new JsonObject { ["niveau"] = "vert", ["texte"] = "Prêt pour le jeu." };
        verdict["date"] = DateTime.Now.ToString("s");
        SaveVerdict(ctx, row.Id, verdict);

        return new JsonObject
        {
            ["verdict"] = verdict.DeepClone(),
            ["id"] = row.Id, ["nom"] = row.Nom, ["nomPreset"] = row.NomPreset, ["preset"] = row.Preset, ["categorie"] = row.Categorie,
            ["race"] = row.Race, ["sexe"] = row.Sexe, ["voix"] = row.Voix, ["statut"] = row.Statut, ["details"] = row.Details, ["statutTexte"] = Scanner.StatusText(row),
            // couleur des cheveux du preset (0xRRGGBB) : le jeu teinte les formes « Hair Tint » du FaceGen avec elle
            ["couleurCheveux"] = j.HairColor,
            // cheveux SMP (section Avancé) : { smp, installe, xml, perruque, fabriquee, pieces }, null sans export
            ["cheveuxSmp"] = smpState ?? new JsonObject { ["smp"] = false, ["installe"] = SmpHair.SmpInstalled(ctx) },
            ["peau"] = j.SkinTone is { } st ? $"#{st & 0xFFFFFF:X6}" : null,
            ["donneur"] = donor == null ? null : new JsonObject { ["id"] = donor.Id, ["nom"] = donor.Nom, ["visageEcrit"] = faceWritten },
            ["experimental"] = ctx.Settings.UseExperimental,
            ["headparts"] = parts, ["facegen"] = face, ["verifications"] = checks, ["dependances"] = deps, ["import"] = import,
        };
    }
}
