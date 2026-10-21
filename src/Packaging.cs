using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace GenerateFollower;

public static class Packaging
{
    /// <summary>
    /// Scripts compilés (.pex) : ceux livrés dans papyrus\pex s'ils sont à jour (cas de la version publique, même avec
    /// un Creation Kit installé) ; sinon compile les .psc avec le compilateur du Creation Kit (il faut aussi les sources
    /// RaceMenu et UIExtensions de deps\, qui ne sont pas livrées) et met à jour papyrus\pex. Renvoie le dossier des .pex.
    /// </summary>
    public static string CompilePapyrus(Context ctx)
    {
        var game = Path.GetDirectoryName(ctx.DataDir)!;
        var compiler = Path.Combine(game, "Papyrus Compiler", "PapyrusCompiler.exe");
        var shipped = Path.Combine(ctx.PapyrusDir, "pex");
        var stale = Directory.EnumerateFiles(ctx.PapyrusDir, "*.psc")
            .Where(psc => Path.Combine(shipped, Path.GetFileNameWithoutExtension(psc) + ".pex") is var pex
                          && (!File.Exists(pex) || File.GetLastWriteTimeUtc(pex) < File.GetLastWriteTimeUtc(psc)))
            .Select(Path.GetFileNameWithoutExtension).ToList();
        if (stale.Count == 0) return shipped;
        var sources = new[] { Path.Combine(ctx.Root, "deps", "racemenu", "scripts", "source"), Path.Combine(ctx.Root, "deps", "uiext", "scripts", "source") };
        if (!File.Exists(compiler) || !sources.All(Directory.Exists))
            throw new GfException($"Scripts compilés absents ou plus anciens que leur source dans {shipped} : {string.Join(", ", stale)}. "
                + "Il faut le compilateur Papyrus du Creation Kit et les sources RaceMenu/UIExtensions de deps\\ (version de développement).");
        var pex = Context.Dir(Path.Combine(ctx.WorkDir, "pex"));
        var imports = string.Join(';',
            ctx.PapyrusDir,
            Path.Combine(ctx.Root, "deps", "racemenu", "scripts", "source"),
            Path.Combine(ctx.Root, "deps", "uiext", "scripts", "source"),
            Path.Combine(ctx.DataDir, "Scripts", "Source"),
            Path.Combine(ctx.DataDir, "Source", "Scripts"));
        var psi = new ProcessStartInfo(compiler)
        {
            ArgumentList = { ctx.PapyrusDir, "-all", "-q", $"-f={Path.Combine(ctx.Root, "deps", "TESV_Papyrus_Flags.flg")}", $"-i={imports}", $"-o={pex}" },
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new GfException("Échec de compilation Papyrus :\n" + output);
        // .pex livrés avec l'application tenus à jour (seulement les scripts dont la source existe), sans le nom
        // d'utilisateur Windows ni le nom du PC que le compilateur inscrit dans chaque .pex
        Directory.CreateDirectory(shipped);
        foreach (var file in Directory.EnumerateFiles(pex, "*.pex")) SanitizePex(file);
        foreach (var psc in Directory.EnumerateFiles(ctx.PapyrusDir, "*.psc"))
        {
            var name = Path.GetFileNameWithoutExtension(psc) + ".pex";
            if (File.Exists(Path.Combine(pex, name))) File.Copy(Path.Combine(pex, name), Path.Combine(shipped, name), true);
        }
        return pex;
    }

    /// <summary>
    /// En-tête d'un .pex Skyrim (gros-boutiste) : magic FA57C0DE, version (2 o), id du jeu (2 o), date de compilation
    /// (8 o), puis trois chaînes (longueur sur 2 o + octets) : fichier source, nom d'utilisateur, nom du PC. Les deux
    /// dernières sont remplacées par « user » et « pc » (le jeu ne les lit pas ; la suite du fichier est séquentielle).
    /// </summary>
    public static void SanitizePex(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 18 || d[0] != 0xFA || d[1] != 0x57 || d[2] != 0xC0 || d[3] != 0xDE) return;
        int p = 16;
        int Len(int at) => (d[at] << 8) | d[at + 1];
        p += 2 + Len(p);                     // fichier source : gardé
        int start = p;
        p += 2 + Len(p);                     // nom d'utilisateur
        p += 2 + Len(p);                     // nom du PC
        if (p > d.Length) return;
        static byte[] Str(string s) => [(byte)(s.Length >> 8), (byte)s.Length, .. System.Text.Encoding.ASCII.GetBytes(s)];
        byte[] result = [.. d[..start], .. Str("user"), .. Str("pc"), .. d[p..]];
        if (!result.AsSpan().SequenceEqual(d)) File.WriteAllBytes(path, result);
    }

    /// <summary>Chemin de texconv : réglages, sinon celui de SSEEdit dans Data.</summary>
    public static string Texconv(Context ctx)
    {
        var texconv = ctx.Settings.ResolvedTexconv(ctx.DataDir);
        if (!File.Exists(texconv)) throw new GfException("texconv introuvable : indique son chemin dans les Réglages du Builder.");
        return texconv;
    }

    /// <summary>Redimensionne/compresse une texture de teint vers dstDir\dstName.dds avec texconv (DirectXTex).</summary>
    public static void ConvertTint(Context ctx, string src, string dstDir, string dstName)
    {
        var texconv = Texconv(ctx);

        var tmp = Context.Dir(Path.Combine(ctx.WorkDir, "tint_tmp"));
        var tmpSrc = Path.Combine(tmp, dstName + ".dds");
        File.Copy(src, tmpSrc, true);
        int size = ctx.Config.TintSize;
        var psi = new ProcessStartInfo(texconv)
        {
            ArgumentList = { "-nologo", "-y", "-f", ctx.Config.TintFormat, "-w", size.ToString(), "-h", size.ToString(), "-m", "1", "-o", dstDir, tmpSrc },
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        File.Delete(tmpSrc);
        var dst = Path.Combine(dstDir, dstName + ".dds");
        if (proc.ExitCode != 0 || !File.Exists(dst))
            throw new GfException($"texconv a échoué sur {src} :\n{output}");
        // texconv écrit l'extension en majuscules (.DDS) : on remet .dds
        var written = Directory.EnumerateFiles(dstDir, dstName + ".*").First();
        if (Path.GetFileName(written) != dstName + ".dds")
        {
            File.Move(written, dst + ".tmp");
            File.Move(dst + ".tmp", dst);
        }
    }

    public static void CopyPex(string pexDir, string modDir, params string[] scripts)
    {
        var dst = Context.Dir(Path.Combine(modDir, "Scripts"));
        foreach (var s in scripts) File.Copy(Path.Combine(pexDir, s + ".pex"), Path.Combine(dst, s + ".pex"), true);
    }

    /// <summary>Fichier JSON au format JsonUtil (PapyrusUtil) dans SKSE\Plugins\StorageUtilData.</summary>
    public static void WriteJsonUtil(string path, Dictionary<string, List<string>> strings,
        Dictionary<string, List<int>>? ints = null, Dictionary<string, List<float>>? floats = null)
    {
        var root = new JsonObject();
        JsonObject Section<T>(Dictionary<string, List<T>> d)
        {
            var o = new JsonObject();
            foreach (var (k, v) in d) o[k.ToLowerInvariant()] = new JsonArray(v.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            return o;
        }
        root["stringList"] = Section(strings);
        if (ints != null) root["intList"] = Section(ints);
        if (floats != null) root["floatList"] = Section(floats);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new() { WriteIndented = true }));
    }

    public static string Zip(Context ctx, string modDir, string name)
    {
        var zip = Path.Combine(ctx.DistDir, name + ".zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(modDir, zip, CompressionLevel.Optimal, false);
        return zip;
    }

    public static string CleanModDir(Context ctx, string name)
    {
        var dir = Path.Combine(ctx.OutDir, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        return Context.Dir(dir);
    }
}
