using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Génère les têtes FaceGen hors jeu au lieu de l'export RaceMenu en jeu : teinte (Tint) et tête générée de l'aperçu en
/// JSON (HeadGen) ; avec GF_MOTEUR=python, scripts de offline\ (batch_heads.py, .nif + teinte).
/// Ne lit que le dossier du jeu ; tout est écrit dans work\heads_offline.
/// </summary>
public static class OfflineHeads
{
    public static string Generate(Context ctx, List<PresetRow> rows)
    {
        var hpDir = Context.Dir(Path.Combine(ctx.WorkDir, "headparts"));
        var outDir = Context.Dir(Path.Combine(ctx.WorkDir, "heads_offline"));
        var jobs = new JsonArray();
        HeadImports.RefreshIndex(ctx);
        foreach (var r in rows)
        {
            var hpPath = Path.Combine(hpDir, r.Id + ".json");
            // Tête RaceMenu compagnon (Import Head) : sa géométrie remplace celle calculée depuis le jslot
            var jslot = Jslot.Load(ctx.PresetPath(r.Preset));
            var effective = HeadParts.Effective(ctx, jslot, ctx.Find<Mutagen.Bethesda.Skyrim.IRaceGetter>(r.Race), r.Female);
            var import = HeadImports.Resolve(ctx, r, effective).Path;
            var json = Lookup.HeadPartsJson(ctx, r.Preset, r.Race, r.Female, import == null ? null : ctx.CharGenPath(import))
                .ToJsonString(new() { WriteIndented = true });
            // ne réécrit la fiche que si elle change (le lot Python s'en sert pour savoir quoi régénérer)
            if (!File.Exists(hpPath) || File.ReadAllText(hpPath) != json) File.WriteAllText(hpPath, json);
            jobs.Add(new JsonObject { ["preset"] = r.Preset, ["race"] = r.Race, ["hp"] = hpPath, ["out"] = outDir, ["name"] = r.ExportName });
        }
        var jobsPath = Path.Combine(ctx.WorkDir, "heads_jobs.json");
        File.WriteAllText(jobsPath, jobs.ToJsonString());

        // moteur C# : teinte (Tint) et tête générée de l'aperçu en JSON (HeadGen), sans Python ni .nif
        if (!Context.PythonEngine)
        {
            Tint.MakeAll(ctx, jobs);
            Previews(ctx, jobs);
            return outDir;
        }
        Console.WriteLine($"Génération hors jeu de {rows.Count} têtes (≈3 s chacune)...");
        var psi = Py.Start(ctx, "batch_heads.py", jobsPath);
        using var proc = Process.Start(psi)!;
        // progression du lot : recopiée dans la console (ou app.log pour l'application)
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
        proc.BeginOutputReadLine();
        var err = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        File.WriteAllText(Path.Combine(ctx.WorkDir, "heads_offline_erreurs.txt"), err.Result);
        if (proc.ExitCode != 0) throw new GfException($"Le générateur de têtes a échoué (voir work\\heads_offline_erreurs.txt).");
        return outDir;
    }

    /// <summary>
    /// Têtes générées pour l'aperçu 3D (GF_&lt;id&gt;.json, format d'export_mesh) : refaites si absentes ou plus anciennes que le
    /// jslot, la fiche ou l'export importé. Jamais livrées en jeu. Les erreurs vont dans work\heads_offline_erreurs.txt,
    /// comme pour le lot Python ; une tête en échec n'arrête pas le build (seul l'aperçu manque).
    /// </summary>
    static void Previews(Context ctx, JsonArray jobs)
    {
        var heads = new HeadGen(ctx);
        var errors = new System.Text.StringBuilder();
        int made = 0, kept = 0, failed = 0;
        foreach (var job in jobs)
        {
            var preset = job!["preset"]!.GetValue<string>();
            var hp = job["hp"]!.GetValue<string>();
            var name = job["name"]!.GetValue<string>();
            var outFile = Path.Combine(job["out"]!.GetValue<string>(), name + ".json");
            var import = JsonNode.Parse(File.ReadAllText(hp))?["import"]?.GetValue<string>();
            var newest = new[] { ctx.PresetPath(preset), hp, import }.Where(p => p != null && File.Exists(p)).Max(p => File.GetLastWriteTimeUtc(p!));
            if (File.Exists(outFile) && File.GetLastWriteTimeUtc(outFile) > newest) { kept++; continue; }
            try
            {
                var mesh = heads.Mesh(preset, job["race"]!.GetValue<string>(), hp, $@"Data\SKSE\Plugins\CharGen\{name}.dds");
                var tmp = outFile + ".tmp";
                File.WriteAllText(tmp, mesh.ToJsonString());
                File.Move(tmp, outFile, true);
                made++;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failed++;
                errors.AppendLine($"{name}  {preset} : {e.Message}");
                Console.WriteLine($"ECHEC  aperçu {name}  {preset} : {e.Message}");
            }
        }
        File.WriteAllText(Path.Combine(ctx.WorkDir, "heads_offline_erreurs.txt"), errors.ToString());
        Console.WriteLine($"Têtes générées (aperçu) : {made} faites, {kept} déjà à jour, {failed} échecs");
    }
}
