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
                Console.WriteLine(L.T("ECHEC  teinte {0}  {1} : {2}", job["name"], preset, e.Message));
            }
        }
        Console.WriteLine(L.T("Teintes : {0} faites, {1} déjà à jour, {2} échecs", made, kept, failed));
    }

    static string? Import(string hpJson) => JsonNode.Parse(File.ReadAllText(hpJson))?["import"]?.GetValue<string>();

    /// <summary>Teinte d'un preset dans dds ; rend « importée » ou « composée ».</summary>
    public static string Make(Context ctx, string jslotPath, string hpJson, string dds, int size = 1024)
        => MakeFrom(ctx, jslotPath, Import(hpJson), dds, size);

    /// <summary>Même règle, avec l'export RaceMenu donné directement (aperçu d'un preset jamais construit : pas encore de
    /// fiche work\headparts).</summary>
    public static string MakeFrom(Context ctx, string jslotPath, string? importNif, string dds, int size = 1024)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dds)!);
        // essai « gabarit » (HeadWrite) : l'export vient d'un autre preset, sa teinte (maquillage) n'est pas celle de ce
        // preset ; GF_TEINTE_COMPOSEE=<ids> force la teinte calculée depuis le jslot
        bool composed = (Environment.GetEnvironmentVariable("GF_TEINTE_COMPOSEE") ?? "").Split(',')
            .Any(id => id.Length > 0 && Path.GetFileNameWithoutExtension(dds).EndsWith(id.Trim(), StringComparison.OrdinalIgnoreCase));
        // tête d'un autre preset (option « teteDonneur ») : même règle, sans variable d'essai (teinte GF_<id>.dds)
        var name = Path.GetFileNameWithoutExtension(dds);
        if (!composed && name.StartsWith("GF_", StringComparison.OrdinalIgnoreCase) && Options.ChosenDonor(ctx, name[3..]) != null) composed = true;
        if (!composed && importNif is { } imp && Path.ChangeExtension(imp, ".dds") is var impDds && File.Exists(impDds) && !Dds.IsUniform(impDds))
        {
            File.Copy(impDds, dds, true);
            return "importée";
        }
        var rgb = Compose(ctx, JsonNode.Parse(File.ReadAllText(jslotPath))!, size);
        Dds.WriteRgba(dds, new Dds.Image(size, size, ToBytes(rgb, size)));
        return "composée";
    }

    /// <summary>
    /// Teinte du visage avec une autre couleur de peau (option « couleurPeau », plan work\diag\peau\PLAN.md) : même
    /// règle que <see cref="MakeFrom"/>, puis <see cref="ApplyTone"/>. Rend « importée » ou « composée ».
    /// </summary>
    public static string WithSkinTone(Context ctx, string jslotPath, string? importNif, string dds, int newTone, int size = 1024)
    {
        var how = MakeFrom(ctx, jslotPath, importNif, dds, size);
        ApplyTone(ctx, jslotPath, dds, dds, how, newTone, size);
        return how;
    }

    /// <summary>
    /// Teinte src (faite par <see cref="MakeFrom"/>, « importée » ou « composée ») avec la couleur de peau newTone, dans
    /// dst (peut être le même fichier). Aperçu et build.
    /// - composée, preset avec un calque SkinTone : recomposée depuis le jslot, la couleur choisie à la place de ce calque
    ///   (opaque). Exact : le maquillage posé par-dessus ne change pas.
    /// - importée (la couleur de peau est cuite sous le maquillage de l'export), ou preset sans calque SkinTone :
    ///   <see cref="Recolor"/>, approché : le maquillage est recoloré dans la même proportion que la peau.
    /// Dans les deux cas le fond de la teinte est exactement newTone, la couleur du corps (QNAM).
    /// </summary>
    public static void ApplyTone(Context ctx, string jslotPath, string src, string dst, string how, int newTone, int size = 1024)
    {
        var old = Jslot.Load(jslotPath).SkinTone;
        if (how.Trim() == "composée" && old != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            Dds.WriteRgba(dst, new Dds.Image(size, size, ToBytes(Compose(ctx, JsonNode.Parse(File.ReadAllText(jslotPath))!, size, newTone), size)));
        }
        else Recolor(src, dst, old, newTone);
    }

    /// <summary>Teinte src recolorée dans dst (peut être le même fichier) : canal × nouvelle couleur / ancienne (celle du
    /// preset ; null = gris 128), plancher 16 sur l'ancienne. Utilisée par l'aperçu et par le build.</summary>
    public static void Recolor(string src, string dst, int? oldTone, int newTone)
    {
        int old = oldTone ?? 0x808080;
        var img = Dds.Decode(src);
        var px = (byte[])img.Rgba.Clone();
        for (int k = 0; k < 3; k++)
        {
            int shift = 16 - 8 * k;
            double f = ((newTone >> shift) & 0xFF) / (double)Math.Max(16, (old >> shift) & 0xFF);
            var lut = new byte[256];
            for (int v = 0; v < 256; v++) lut[v] = (byte)Math.Min(255, (int)(v * f + 0.5));
            for (int i = k; i < px.Length; i += 4) px[i] = lut[px[i]];
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        Dds.WriteRgba(dst, new Dds.Image(img.Width, img.Height, px));
    }

    /// <summary>tint.compose (masque « r ») : couches empilées sur un gris neutre (128), RGB en double [0, 1].</summary>
    public static double[] Compose(Context ctx, JsonNode jslot, int size, int? skinTone = null)
    {
        var o = new double[size * size * 3];
        Array.Fill(o, 128 / 255.0);
        foreach (var t in jslot["tintInfo"]?.AsArray() ?? [])
        {
            long c = (long)NativeTools.Num(t!["color"]!);
            // couleur de peau choisie : elle remplace le calque SkinTone du preset, opaque (comme Jslot.SkinTone le lit)
            if (skinTone is { } tone && (t["texture"]?.GetValue<string>() ?? "").EndsWith("skintone.dds", StringComparison.OrdinalIgnoreCase))
                c = 0xFF000000L | (uint)(tone & 0xFFFFFF);
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
