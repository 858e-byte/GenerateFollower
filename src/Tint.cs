using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Teinte du visage (FaceTint) en C#, sans compression (plan autonome .NET, étape 4 ; remplace la partie teinte de
/// offline\make_head.py et tint.py) : la teinte exportée avec la tête RaceMenu si elle n'est pas uniforme (copiée telle
/// quelle), sinon les calques tintInfo du jslot (masque R × couleur ARGB sur un gris 128), mêmes calculs en double que
/// numpy. Écrite en RGBA 32 bits non compressé, comme les exports RaceMenu.
/// </summary>
public static class Tint
{
    /// <summary>Teintes des têtes du lot (après batch_heads.py, qui ne fait plus que le .nif) : refaites si absentes, si
    /// plus anciennes que le jslot, la fiche ou l'export, ou faites par l'ancien moteur (marque « .teinte » absente).</summary>
    public static void MakeAll(Context ctx, JsonArray jobs)
    {
        int made = 0, kept = 0, failed = 0;
        foreach (var job in jobs)
        {
            var preset = job!["preset"]!.GetValue<string>();
            var hp = job["hp"]!.GetValue<string>();
            var dds = Path.Combine(job["out"]!.GetValue<string>(), job["name"]!.GetValue<string>() + ".dds");
            var mark = dds + ".teinte";
            var jslot = ctx.PresetPath(preset);
            var newest = new[] { jslot, hp, Import(hp) }.Where(p => p != null && File.Exists(p)).Max(p => File.GetLastWriteTimeUtc(p!));
            // marque plus récente que la teinte : sinon la teinte a été refaite depuis par l'ancien moteur (GF_MOTEUR=python)
            if (File.Exists(dds) && File.Exists(mark) && File.GetLastWriteTimeUtc(dds) > newest && File.GetLastWriteTimeUtc(mark) >= File.GetLastWriteTimeUtc(dds))
            { kept++; continue; }
            try
            {
                var how = Make(ctx, jslot, hp, dds);
                File.WriteAllText(mark, how);
                made++;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failed++;
                if (File.Exists(dds)) File.Delete(dds);
                Console.WriteLine($"ECHEC  teinte {job["name"]}  {preset} : {e.Message}");
            }
        }
        Console.WriteLine($"Teintes : {made} faites, {kept} déjà à jour, {failed} échecs");
    }

    static string? Import(string hpJson) => JsonNode.Parse(File.ReadAllText(hpJson))?["import"]?.GetValue<string>();

    /// <summary>Teinte d'un preset dans dds ; rend « importée » ou « composée ».</summary>
    public static string Make(Context ctx, string jslotPath, string hpJson, string dds, int size = 1024)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dds)!);
        if (Import(hpJson) is { } imp && Path.ChangeExtension(imp, ".dds") is var impDds && File.Exists(impDds) && !Dds.IsUniform(impDds))
        {
            File.Copy(impDds, dds, true);
            return "importée";
        }
        var rgb = Compose(ctx, JsonNode.Parse(File.ReadAllText(jslotPath))!, size);
        Dds.WriteRgba(dds, new Dds.Image(size, size, ToBytes(rgb, size)));
        return "composée";
    }

    /// <summary>tint.compose (masque « r ») : couches empilées sur un gris neutre (128), RGB en double [0, 1].</summary>
    public static double[] Compose(Context ctx, JsonNode jslot, int size)
    {
        var o = new double[size * size * 3];
        Array.Fill(o, 128 / 255.0);
        foreach (var t in jslot["tintInfo"]?.AsArray() ?? [])
        {
            long c = (long)NativeTools.Num(t!["color"]!);
            double a = ((c >> 24) & 255) / 255.0;
            if (a <= 0) continue;
            double r = ((c >> 16) & 255) / 255.0, g = ((c >> 8) & 255) / 255.0, b = (c & 255) / 255.0;
            if (ctx.FindFile(@"textures\" + t["texture"]!.GetValue<string>()) is not { } p) continue;
            var m = Dds.Load(p, size).Rgba;
            for (int i = 0; i < size * size; i++)
            {
                double w = m[i * 4] / 255.0 * a;
                o[i * 3] = o[i * 3] * (1 - w) + r * w;
                o[i * 3 + 1] = o[i * 3 + 1] * (1 - w) + g * w;
                o[i * 3 + 2] = o[i * 3 + 2] * (1 - w) + b * w;
            }
        }
        return o;
    }

    /// <summary>(clip(rgb, 0, 1) × 255 + 0,5) tronqué, alpha opaque (tint.save_dds).</summary>
    static byte[] ToBytes(double[] rgb, int size)
    {
        var px = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            for (int k = 0; k < 3; k++) px[i * 4 + k] = (byte)(Math.Clamp(rgb[i * 3 + k], 0, 1) * 255 + 0.5);
            px[i * 4 + 3] = 255;
        }
        return px;
    }

    /// <summary>Teinte vers dstDir\dstName.dds (remplace la conversion texconv BC7) : non compressée, size × size
    /// (redimensionnée comme Pillow si besoin ; pixels recopiés tels quels sinon).</summary>
    public static void Convert(string src, string dstDir, string dstName, int size)
    {
        var img = Dds.Decode(src);
        if (img.Width != size || img.Height != size) img = Dds.Resize(img, size, size);
        Directory.CreateDirectory(dstDir);
        Dds.WriteRgba(Path.Combine(dstDir, dstName + ".dds"), img);
    }
}
