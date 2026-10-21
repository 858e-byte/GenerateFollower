using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Génère les têtes FaceGen hors jeu (scripts Python de offline\) au lieu de l'export RaceMenu en jeu.
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
            var jslot = Jslot.Load(Path.Combine(ctx.PresetsDir, r.Preset + ".jslot"));
            var effective = HeadParts.Effective(ctx, jslot, ctx.Find<Mutagen.Bethesda.Skyrim.IRaceGetter>(r.Race), r.Female);
            var import = HeadImports.Resolve(ctx, r, effective).Path;
            var json = Lookup.HeadPartsJson(ctx, r.Preset, r.Race, r.Female, import == null ? null : Path.Combine(ctx.CharGenDir, import))
                .ToJsonString(new() { WriteIndented = true });
            // ne réécrit la fiche que si elle change (le lot Python s'en sert pour savoir quoi régénérer)
            if (!File.Exists(hpPath) || File.ReadAllText(hpPath) != json) File.WriteAllText(hpPath, json);
            jobs.Add(new JsonObject { ["preset"] = r.Preset, ["race"] = r.Race, ["hp"] = hpPath, ["out"] = outDir, ["name"] = r.ExportName });
        }
        var jobsPath = Path.Combine(ctx.WorkDir, "heads_jobs.json");
        File.WriteAllText(jobsPath, jobs.ToJsonString());

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
}
