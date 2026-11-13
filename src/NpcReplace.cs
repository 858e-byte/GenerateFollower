using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Remplacement de PNJ existants par l'apparence de followers construits (méthode « plugin », validée en jeu le 01/11,
/// essai v7) : plugin ESL qui reprend la fiche du PNJ telle qu'elle est en jeu et n'en change que l'apparence (race,
/// parties de tête, couleurs, morphs, peau, taille, poids, script des overlays) ; enregistrements du follower dont
/// l'apparence dépend recopiés dans le plugin (autonome) ; tête précalculée copiée au chemin du PNJ d'origine
/// (FaceGeom\&lt;plugin d'origine&gt;\&lt;FormID&gt;.nif), teinte comprise. Rien n'est écrit dans le jeu : ZIP dans dist.
/// </summary>
public static class NpcReplace
{
    public const string PackName = "GF_Remplacements";
    const string Geom = @"meshes\actors\character\FaceGenData\FaceGeom";
    const string Tint = @"textures\actors\character\FaceGenData\FaceTint";
    static string Id(FormKey k) => k.ID.ToString("X8");

    /// <summary>Plugin de remplacement en cours de construction (un ou plusieurs PNJ).</summary>
    sealed class Converter
    {
        readonly Context ctx;
        readonly SkyrimMod mod;
        readonly string outDir;
        readonly Dictionary<FormKey, FormKey> map = [];
        readonly List<IMajorRecord> copies = [];
        readonly List<Npc> npcs = [];
        readonly HashSet<string> scripts = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> copiedRoots = new(StringComparer.OrdinalIgnoreCase);
        public int Files;

        public Converter(Context ctx, string plugin, string outDir, string description)
        {
            this.ctx = ctx;
            this.outDir = outDir;
            mod = new SkyrimMod(ModKey.FromFileName(plugin), SkyrimRelease.SkyrimSE);
            mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
            mod.ModHeader.Author = "GenerateFollower";
            mod.ModHeader.Description = description;
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
        }

        /// <summary>Le PNJ <paramref name="target"/> prend l'apparence de <paramref name="source"/> (follower du plugin
        /// <paramref name="sourceMod"/>, pack décompressé dans <paramref name="srcRoot"/>).</summary>
        public string Add(ISkyrimModGetter sourceMod, string srcRoot, INpcGetter source, INpcGetter target)
        {
            // apparence héritée d'un modèle (Traits, ex. Immersive Wenches) : sexe et voix du modèle
            var traits = NpcList.TraitsProvider(ctx, target) ?? throw new GfException(L.T("Apparence tirée au hasard dans une liste : PNJ non remplaçable."));
            if (source.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) != traits.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female))
                throw new GfException(L.T("Sexes différents : la voix du PNJ ne correspondrait pas."));
            // fiche telle qu'en jeu : les autres champs (IA, quêtes, voix, inventaire) et les corrections des autres
            // mods sont gardés
            var npc = mod.Npcs.GetOrAddAsOverride(target);
            if (traits != target)
            {
                // ce PNJ seulement cesse d'hériter des traits (le modèle peut servir à d'autres PNJ) ; il garde la voix
                // et le sexe qu'il héritait
                npc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
                npc.Voice.SetTo(traits.Voice.FormKeyNullable);
                if (traits.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)) npc.Configuration.Flags |= NpcConfiguration.Flag.Female;
                else npc.Configuration.Flags &= ~NpcConfiguration.Flag.Female;
            }
            npc.Race.SetTo(source.Race.FormKey);
            npc.HeadParts.Clear();
            foreach (var hp in source.HeadParts) npc.HeadParts.Add(new FormLink<IHeadPartGetter>(hp.FormKey));
            npc.HairColor.SetTo(source.HairColor.FormKeyNullable);
            npc.HeadTexture.SetTo(source.HeadTexture.FormKeyNullable);
            npc.WornArmor.SetTo(source.WornArmor.FormKeyNullable);
            npc.TextureLighting = source.TextureLighting;
            npc.TintLayers.Clear();
            foreach (var t in source.TintLayers) npc.TintLayers.Add(t.DeepCopy());
            npc.FaceMorph = source.FaceMorph?.DeepCopy();
            npc.FaceParts = source.FaceParts?.DeepCopy();
            npc.Height = source.Height;
            npc.Weight = source.Weight;
            npcs.Add(npc);

            // autonome : enregistrements du plugin source dont l'apparence dépend (parties de tête et formes annexes,
            // couleur de cheveux, peau, ArmorAddons, TextureSets…) recopiés sous de nouveaux FormID
            var byKey = sourceMod.EnumerateMajorRecords().ToDictionary(r => r.FormKey);
            var todo = new Stack<IMajorRecordGetter>();
            int before = copies.Count;
            void Need(FormKey k)
            {
                if (k.ModKey != sourceMod.ModKey || k == source.FormKey || map.ContainsKey(k) || !byKey.TryGetValue(k, out var r)) return;
                map[k] = mod.GetNextFormKey();
                todo.Push(r);
            }
            foreach (var l in npc.EnumerateFormLinks()) Need(l.FormKey);
            while (todo.Count > 0)
            {
                var r = todo.Pop();
                var copy = r.Duplicate(map[r.FormKey]);
                // parties de tête : EditorID gardé, la tête précalculée nomme ses formes d'après lui (sinon le jeu
                // régénère la tête, visage pâle sans teinte : essai v6)
                if (copy is not IHeadPart && copy.EditorID is { Length: > 0 } e) copy.EditorID = e + "_Rempl";
                copies.Add(copy);
                foreach (var l in r.EnumerateFormLinks()) Need(l.FormKey);
            }

            // scripts d'apparence du follower, autonomes (valeurs dans leurs propriétés), posés au chargement :
            // overlays (tatouages…), preset de corps OBody choisi dans le Builder (GF_FollowerBody) et transformations
            // d'os RaceMenu (GF_FollowerSkeleton)
            var ovl = source.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "GF_FollowerOverlays");
            var body = source.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == BodyPresets.Script);
            var skel = source.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == Skeleton.Script);
            foreach (var s in new[] { ovl, body, skel })
            {
                if (s == null) continue;
                npc.VirtualMachineAdapter ??= new VirtualMachineAdapter();
                npc.VirtualMachineAdapter.Scripts.RemoveAll(x => x.Name == s.Name);
                npc.VirtualMachineAdapter.Scripts.Add(s.DeepCopy());
                if (File.Exists(Path.Combine(srcRoot, "Scripts", s.Name + ".pex")) && scripts.Add(s.Name))
                    CopyFile(srcRoot, Path.Combine(srcRoot, "Scripts", s.Name + ".pex"));
            }

            // tête : au chemin du PNJ d'origine, chemin de teinte réécrit, teinte copiée au même nom
            var tintRel = Path.Combine(Tint, target.FormKey.ModKey.FileName, Id(target.FormKey) + ".dds");
            var dstNif = Path.Combine(outDir, Geom, target.FormKey.ModKey.FileName, Id(target.FormKey) + ".nif");
            Directory.CreateDirectory(Path.GetDirectoryName(dstNif)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(outDir, tintRel))!);
            var nif = NifPatcher.ReplaceTextures(File.ReadAllBytes(Path.Combine(srcRoot, Geom, sourceMod.ModKey.FileName, Id(source.FormKey) + ".nif")),
                t => t.Contains(@"\FaceTint\", StringComparison.OrdinalIgnoreCase) ? tintRel : null, out var n);
            File.WriteAllBytes(dstNif, nif);
            File.Copy(Path.Combine(srcRoot, Tint, sourceMod.ModKey.FileName, Id(source.FormKey) + ".dds"), Path.Combine(outDir, tintRel), true);

            // Erin Race : config du follower (SKSE\Plugins\ErinRace\<plugin>_<id>.ini, skin, réglages du preset) reprise
            // pour le PNJ remplacé : PluginName et ActorBaseID pointent sur lui (sinon Erin Race ne le configure pas)
            var erinIni = Path.Combine(srcRoot, "SKSE", "Plugins", "ErinRace", $"{Path.GetFileNameWithoutExtension(sourceMod.ModKey.FileName.String)}_{source.FormKey.ID:X}.ini");
            var erin = File.Exists(erinIni);
            if (erin)
            {
                var lines = File.ReadAllLines(erinIni).Select(l =>
                    l.TrimStart().StartsWith("PluginName", StringComparison.OrdinalIgnoreCase) ? $"PluginName = {target.FormKey.ModKey.FileName}"
                    : l.TrimStart().StartsWith("ActorBaseID", StringComparison.OrdinalIgnoreCase) ? $"ActorBaseID = 0x{target.FormKey.ID:X}"
                    : l).ToList();
                lines.Insert(0, $"# GenerateFollower: NPC replacement, {target.Name} ({target.FormKey}) gets the look of {source.Name}");
                var dir = Directory.CreateDirectory(Path.Combine(outDir, "SKSE", "Plugins", "ErinRace")).FullName;
                File.WriteAllLines(Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(mod.ModKey.FileName.String)}_{Path.GetFileNameWithoutExtension(target.FormKey.ModKey.FileName.String)}_{target.FormKey.ID:X}.ini"
                    .Replace(' ', '_')), lines, System.Text.Encoding.ASCII);
                Files++;
            }

            // fichiers des cheveux, perruques SMP, peaux… : sous meshes\GenerateFollower et textures\GenerateFollower
            // dans le pack source (chemins indépendants du plugin), recopiés une fois par pack
            if (copiedRoots.Add(srcRoot))
                foreach (var top in new[] { "meshes", "textures" })
                    if (Directory.Exists(Path.Combine(srcRoot, top, Standalone.SubFolder)))
                        foreach (var f in Directory.EnumerateFiles(Path.Combine(srcRoot, top, Standalone.SubFolder), "*", SearchOption.AllDirectories))
                            CopyFile(srcRoot, f);
            return $"{source.Name} → {target.Name} ({target.FormKey}) : race {source.Race.FormKey}, {npc.HeadParts.Count} parties de tête, " +
                $"{copies.Count - before} enregistrement(s) recopié(s), teinte {(n > 0 ? "réécrite" : "NON réécrite")}" +
                (ovl != null ? ", overlays" : "") + (body != null ? ", preset de corps" : "") + (erin ? ", config Erin Race" : "");
        }

        void CopyFile(string srcRoot, string f)
        {
            var dst = Path.Combine(outDir, Path.GetRelativePath(srcRoot, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
            Files++;
        }

        /// <summary>Liens réécrits, plugin écrit ; renvoie ses masters.</summary>
        public List<string> Finish()
        {
            foreach (var c in copies)
            {
                c.RemapLinks(map);
                ((IMod)mod).GetTopLevelGroup(c.Registration.GetterType).AddUntyped(c);
            }
            foreach (var npc in npcs) npc.RemapLinks(map);
            var path = Path.Combine(outDir, mod.ModKey.FileName);
            mod.BeginWrite.ToPath(path).WithLoadOrder(ctx.Env.LoadOrder).WithEmbeddedEncodings(PluginText.Write).Write();
            using var written = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, PluginText.Read);
            return written.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
        }

        public string Zip(string name)
        {
            var zip = Path.Combine(ctx.DistDir, name + ".zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(outDir, zip, CompressionLevel.Optimal, false);
            return zip;
        }
    }

    /// <summary>Fiche du PNJ telle qu'en jeu, sans nos propres plugins (un ancien GF_Remplacements par exemple).</summary>
    static INpcGetter? InGame(Context ctx, FormKey k) =>
        ctx.Cache.ResolveAllContexts<INpc, INpcGetter>(k)
            .FirstOrDefault(v => !v.ModKey.FileName.String.StartsWith(Standalone.Prefix, StringComparison.OrdinalIgnoreCase))?.Record;

    /// <summary>
    /// Paquet GF_Remplacements : les presets qui ont un PNJ choisi (work\replace_choice.json) sont construits comme des
    /// followers dans un pack de travail (out\_GF_RemplSource, ZIP supprimé), puis convertis en remplacements.
    /// </summary>
    public static JsonObject BuildPack(Context ctx)
    {
        var choices = NpcList.Choices(ctx).Select(kv => (Preset: kv.Key, Npc: kv.Value?.GetValue<string>())).Where(c => c.Npc != null).ToList();
        if (choices.Count == 0) throw new GfException(L.T("Aucun PNJ choisi : onglet « Remplacer un PNJ » d'un preset."));
        var report = new StringBuilder();
        report.AppendLine($"GenerateFollower - {PackName} - {DateTime.Now:yyyy-MM-dd HH:mm}");

        const string srcName = "_GF_RemplSource";
        var log = new StringWriter();
        var old = Console.Out;
        Console.SetOut(log);
        try
        {
            FollowerBuilder.Run(ctx, offline: true, only: choices.Select(c => c.Preset).ToHashSet(StringComparer.OrdinalIgnoreCase),
                zipName: srcName, fromExport: true, pluginName: "GF_RemplSource.esp", title: PackName, placedRefs: false);
        }
        finally { Console.SetOut(old); }
        var srcZip = Path.Combine(ctx.DistDir, srcName + ".zip");
        if (File.Exists(srcZip)) File.Delete(srcZip);
        var srcRoot = Path.Combine(ctx.OutDir, srcName);
        using var srcMod = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(srcRoot, "GF_RemplSource.esp"), SkyrimRelease.SkyrimSE, PluginText.Read);

        var conv = new Converter(ctx, PackName + ".esp", Path.Combine(ctx.OutDir, PackName),
            L.T("Remplacements de PNJ générés par GenerateFollower."));
        int done = 0;
        foreach (var (preset, npcKey) in choices)
        {
            var source = srcMod.Npcs.FirstOrDefault(n => n.EditorID == Standalone.Prefix + preset);
            var target = FormKey.TryFactory(npcKey!, out var fk) ? InGame(ctx, fk) : null;
            if (source == null) { report.AppendLine($"IGNORÉ {preset} : pas construit (pas d'export RaceMenu ? voir rapport_build.txt)"); continue; }
            if (target == null) { report.AppendLine($"IGNORÉ {preset} : PNJ {npcKey} introuvable dans le jeu"); continue; }
            try { report.AppendLine("OK " + conv.Add(srcMod, srcRoot, source, target)); done++; }
            catch (GfException e) { report.AppendLine($"IGNORÉ {preset} : {e.Message}"); }
        }
        if (done == 0) throw new GfException(L.T("Aucun remplacement construit :") + "\n" + report);
        var masters = conv.Finish();
        var zip = conv.Zip(PackName);
        report.AppendLine($"Masters : {string.Join(", ", masters)}");
        report.AppendLine($"{done} remplacement(s), {conv.Files} fichier(s) recopié(s) ; ZIP : {zip}");
        report.AppendLine("À installer comme un mod, APRÈS les autres mods qui modifient ces PNJ (ordre de chargement et fichiers).");
        File.WriteAllText(Path.Combine(ctx.WorkDir, "rapport_remplacements.txt"), report.ToString());
        return new JsonObject { ["zip"] = zip, ["remplacements"] = done, ["rapport"] = report.ToString(), ["log"] = log.ToString() };
    }

    /// <summary>Essai : un follower d'un pack (décompressé, ou Data si null) sur un PNJ → dist\&lt;name&gt;.zip.</summary>
    public static void Test(Context ctx, string sourceKey, string targetKey, string name, string? sourceDir = null)
    {
        var sourceForm = FormKey.Factory(sourceKey);
        var srcRoot = sourceDir ?? ctx.DataDir;
        using var sourceMod = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(srcRoot, sourceForm.ModKey.FileName), SkyrimRelease.SkyrimSE, PluginText.Read);
        var source = sourceMod.Npcs.First(n => n.FormKey == sourceForm);
        var target = InGame(ctx, FormKey.Factory(targetKey)) ?? throw new GfException("PNJ introuvable : " + targetKey);
        var conv = new Converter(ctx, name + ".esp", Path.Combine(ctx.WorkDir, "diag", "remplacement", name), $"Essai : apparence de {source.Name} sur {target.Name}.");
        Console.WriteLine(conv.Add(sourceMod, srcRoot, source, target));
        Console.WriteLine($"Masters : {string.Join(", ", conv.Finish())} ; {conv.Files} fichier(s)");
        Console.WriteLine($"ZIP : {conv.Zip(name)}");
    }
}
