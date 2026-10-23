using BCnEncoder.Decoder;
using BCnEncoder.Shared.ImageFiles;

namespace GenerateFollower;

/// <summary>
/// Textures DDS en C# (BCnEncoder.Net, remplace texconv et Pillow des scripts de offline\) : décodage du premier niveau
/// en RGBA 8 bits, comme « texconv -f R8G8B8A8_UNORM » puis PIL.
/// </summary>
public static class Dds
{
    public sealed record Image(int Width, int Height, byte[] Rgba);

    public static Image Decode(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Decode(fs, path);
    }

    public static Image Decode(Stream stream, string name)
    {
        // BC1 à BC5 : décodeur qui calcule comme DirectXTex (texconv) ; les autres formats (BC7, non compressés) : BCnEncoder,
        // déjà identique à texconv au pixel près
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.GetBuffer();
        if (Bc.TryDecode(data, (int)ms.Length) is { } bc) return bc;
        if (Bc.TryDecodeMasked(data, (int)ms.Length) is { } masked) return masked;
        ms.Position = 0;
        stream = ms;
        DdsFile dds;
        try { dds = DdsFile.Load(stream); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new GfException($"DDS illisible : {name} ({e.Message})"); }
        int w = (int)dds.header.dwWidth, h = (int)dds.header.dwHeight;
        var decoder = new BcDecoder();
        if (!decoder.IsSupportedFormat(dds)) throw new GfException($"format DDS non pris en charge : {name}");
        var px = decoder.Decode(dds);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < px.Length && i < w * h; i++)
        {
            rgba[i * 4] = px[i].r;
            rgba[i * 4 + 1] = px[i].g;
            rgba[i * 4 + 2] = px[i].b;
            rgba[i * 4 + 3] = px[i].a;
        }
        return new Image(w, h, rgba);
    }

    /// <summary>
    /// DDS non compressé RGBA 32 bits, un seul niveau, avec l'en-tête des teintes exportées par RaceMenu (drapeaux
    /// 0x2100F, masques R ff / G ff00 / B ff0000 / A ff000000) : les octets de l'image sont écrits tels quels.
    /// </summary>
    public static void WriteRgba(string path, Image img)
    {
        var h = new byte[128];
        void U(int at, uint v) => BitConverter.TryWriteBytes(h.AsSpan(at), v);
        U(0, 0x20534444);                  // « DDS »
        U(4, 124);
        U(8, 0x2100F);                     // CAPS | HEIGHT | WIDTH | PITCH | PIXELFORMAT | MIPMAPCOUNT
        U(12, (uint)img.Height);
        U(16, (uint)img.Width);
        U(20, (uint)img.Width * 4);
        U(24, 1);                          // profondeur
        U(28, 1);                          // un niveau
        U(76, 32);                         // DDS_PIXELFORMAT
        U(80, 0x41);                       // RGB | ALPHAPIXELS
        U(88, 32);
        U(92, 0xFF); U(96, 0xFF00); U(100, 0xFF0000); U(104, 0xFF000000);
        U(108, 0x1000);                    // TEXTURE
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            fs.Write(h);
            fs.Write(img.Rgba);
        }
        File.Move(tmp, path, true);
    }

    /// <summary>PNG (aperçu 3D du Builder), System.Drawing.</summary>
    public static void WritePng(string path, Image img)
    {
        using var bmp = new System.Drawing.Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, img.Width, img.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        var row = new byte[img.Width * 4];
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                int s = (y * img.Width + x) * 4;
                row[x * 4] = img.Rgba[s + 2];      // BGRA en mémoire
                row[x * 4 + 1] = img.Rgba[s + 1];
                row[x * 4 + 2] = img.Rgba[s];
                row[x * 4 + 3] = img.Rgba[s + 3];
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
        }
        bmp.UnlockBits(data);
        var tmp = path + ".tmp";
        bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
        File.Move(tmp, path, true);
    }

    /// <summary>DDS BC7 (BCnEncoder), avec ou sans mipmaps : diffuses cuites (overlays du visage), comme texconv
    /// « -f BC7_UNORM -m 0 ».</summary>
    public static void WriteBc7(string path, Image img, bool mipmaps)
    {
        var enc = new BCnEncoder.Encoder.BcEncoder();
        enc.OutputOptions.Format = BCnEncoder.Shared.CompressionFormat.Bc7;
        enc.OutputOptions.Quality = BCnEncoder.Encoder.CompressionQuality.Balanced;
        enc.OutputOptions.GenerateMipMaps = mipmaps;
        enc.OutputOptions.FileFormat = BCnEncoder.Shared.OutputFileFormat.Dds;
        enc.OutputOptions.DdsBc1WriteAlphaFlag = false;
        var dds = enc.EncodeToDds(img.Rgba, img.Width, img.Height, BCnEncoder.Encoder.PixelFormat.Rgba32);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) dds.Write(fs);
        File.Move(tmp, path, true);
    }

    /// <summary>tint.load_dds : texture décodée puis redimensionnée en size × size comme Pillow (Image.BILINEAR).</summary>
    public static Image Load(string path, int size)
    {
        var img = Decode(path);
        return img.Width == size && img.Height == size ? img : Resize(img, size, size);
    }

    /// <summary>Teinte d'export « uniforme » (exports UBE : gris uni, inutilisables) : écart-type des canaux RGB d'une
    /// réduction 64 × 64 inférieur à 0,01 (index_heads.py).</summary>
    public static bool IsUniform(string path)
    {
        var img = Load(path, 64);
        double sum = 0, sum2 = 0;
        int n = 0;
        for (int i = 0; i < img.Rgba.Length; i += 4)
            for (int c = 0; c < 3; c++) { double v = img.Rgba[i + c] / 255.0; sum += v; sum2 += v * v; n++; }
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sum2 / n - mean * mean)) < 0.01;
    }

    // ------------------------------------------------------------------ redimensionnement de Pillow

    const int PrecisionBits = 32 - 8 - 2;

    /// <summary>
    /// Image.resize(size, Image.BILINEAR) de Pillow sur une image RGBA, au bit près : alpha prémultiplié le temps du
    /// redimensionnement (RGBA → RGBa → RGBA), filtre triangle élargi en réduction, coefficients en virgule fixe
    /// (22 bits), passe horizontale puis verticale (Resample.c, Convert.c).
    /// </summary>
    public static Image Resize(Image src, int width, int height)
    {
        var pre = (byte[])src.Rgba.Clone();
        for (int i = 0; i < pre.Length; i += 4)
        {
            int a = pre[i + 3];
            for (int c = 0; c < 3; c++) { int tmp = pre[i + c] * a + 128; pre[i + c] = (byte)(((tmp >> 8) + tmp) >> 8); }
        }
        int w = src.Width, h = src.Height;
        var (ybounds, ykk, yk) = Coeffs(h, height);
        // passe horizontale seulement sur les lignes utiles à la passe verticale (comme Pillow)
        int yFirst = ybounds[0], yLast = ybounds[(height - 1) * 2] + ybounds[(height - 1) * 2 + 1];
        byte[] tmpImg;
        int tw = w, th = h;
        if (width != w)
        {
            var (xb, xkk, xk) = Coeffs(w, width);
            th = yLast - yFirst;
            tmpImg = new byte[width * th * 4];
            for (int y = 0; y < th; y++)
                for (int x = 0; x < width; x++)
                {
                    int xmin = xb[x * 2], xmax = xb[x * 2 + 1];
                    for (int c = 0; c < 4; c++)
                    {
                        long ss = 1L << (PrecisionBits - 1);
                        for (int k = 0; k < xmax; k++) ss += pre[((y + yFirst) * w + xmin + k) * 4 + c] * (long)xk[x * xkk + k];
                        tmpImg[(y * width + x) * 4 + c] = Clip8(ss);
                    }
                }
            for (int i = 0; i < height; i++) ybounds[i * 2] -= yFirst;
            tw = width;
        }
        else { tmpImg = pre; yFirst = 0; }
        byte[] outImg;
        if (height != h)
        {
            outImg = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int ymin = ybounds[y * 2], ymax = ybounds[y * 2 + 1];
                for (int x = 0; x < tw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        long ss = 1L << (PrecisionBits - 1);
                        for (int k = 0; k < ymax; k++) ss += tmpImg[((ymin + k) * tw + x) * 4 + c] * (long)yk[y * ykk + k];
                        outImg[(y * width + x) * 4 + c] = Clip8(ss);
                    }
            }
        }
        else outImg = tmpImg;
        // RGBa → RGBA
        for (int i = 0; i < outImg.Length; i += 4)
        {
            int a = outImg[i + 3];
            if (a == 255 || a == 0) continue;
            for (int c = 0; c < 3; c++) outImg[i + c] = (byte)Math.Min(255, 255 * outImg[i + c] / a);
        }
        return new Image(width, height, outImg);
    }

    static byte Clip8(long ss)
    {
        long v = ss >> PrecisionBits;
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    /// <summary>precompute_coeffs + normalize_coeffs_8bpc de Pillow (filtre bilinéaire, support 1).</summary>
    static (int[] Bounds, int Kk, int[] K) Coeffs(int inSize, int outSize)
    {
        double scale = (double)inSize / outSize;
        double filterscale = Math.Max(scale, 1.0);
        double support = 1.0 * filterscale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;
        var kk = new double[outSize * ksize];
        var bounds = new int[outSize * 2];
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale;
            double ww = 0, ss = 1.0 / filterscale;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;
            for (int x = 0; x < xmax; x++)
            {
                double t = (x + xmin - center + 0.5) * ss;
                if (t < 0) t = -t;
                double wv = t < 1.0 ? 1.0 - t : 0.0;
                kk[xx * ksize + x] = wv;
                ww += wv;
            }
            for (int x = 0; x < xmax; x++) if (ww != 0) kk[xx * ksize + x] /= ww;
            for (int x = xmax; x < ksize; x++) kk[xx * ksize + x] = 0;
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        var k = new int[kk.Length];
        for (int i = 0; i < kk.Length; i++)
            k[i] = kk[i] < 0 ? (int)(-0.5 + kk[i] * (1 << PrecisionBits)) : (int)(0.5 + kk[i] * (1 << PrecisionBits));
        return (bounds, ksize, k);
    }
}
