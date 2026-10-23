using System.Diagnostics;

namespace GenerateFollower;

/// <summary>Lance un script Python de offline\ (PyNifly, texconv…).</summary>
public static class Py
{
    /// <summary>
    /// Processus Python pour un script de offline\ ; les chemins de l'installation passent par l'environnement
    /// (GF_DATA, GF_VDATA, GF_PYNIFLY, GF_TEXCONV, lus par offline\gfpaths.py).
    /// </summary>
    public static ProcessStartInfo Start(Context ctx, string script, params string[] args)
    {
        var psi = new ProcessStartInfo(ctx.Settings.ResolvedPython())
        {
            WorkingDirectory = Path.Combine(ctx.Root, "offline"),
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            // exe sans console : sans cela Windows ouvre une fenêtre de console (ses outils, texconv, 7-Zip, ffmpeg, en héritent)
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(Path.Combine(ctx.Root, "offline", script));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GF_DATA"] = ctx.DataDir;
        // MO2 lu directement : racines du Data virtuel et BSA dans l'ordre de chargement (offline\gfpaths.py)
        if (ctx.Files.Direct)
        {
            var vdata = Path.Combine(ctx.WorkDir, "vdata.json");
            ctx.Files.WriteForPython(vdata, ctx.Env.LoadOrder.ListedOrder.Select(l => l.ModKey.FileName.String).Where(ctx.ActivePlugins.Contains));
            psi.Environment["GF_VDATA"] = vdata;
        }
        psi.Environment["GF_PYNIFLY"] = ctx.Settings.ResolvedPyNifly(ctx.Root);
        psi.Environment["GF_TEXCONV"] = ctx.Settings.ResolvedTexconv(ctx.DataDir);
        return psi;
    }

    public static string Run(Context ctx, string script, params string[] args)
    {
        using var proc = Process.Start(Start(ctx, script, args))!;
        var err = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new GfException($"{script} a échoué :\n{output}{err.Result}");
        return output;
    }
}
