namespace GenerateFollower;

/// <summary>
/// Décodage BC1 à BC5 (DXT1, DXT3, DXT5, ATI1/BC4, ATI2/BC5) comme DirectXTex, donc comme texconv
/// « -f R8G8B8A8_UNORM » : couleurs 5-6-5 en float, interpolation en float 32 bits (XMVectorLerp), alpha en float,
/// puis conversion en octet par saturation, × 255 et arrondi au plus proche (XMStoreUByteN4) ; BC4 et BC5 tronqués au lieu d'arrondis.
/// BC4 donne (r, r, r, 255) (vérifié avec texconv), BC5 (r, g, 0, 255) (sans exemple dans le corpus). Premier niveau seulement.
/// </summary>
static class Bc
{
    enum Kind { Bc1, Bc2, Bc3, Bc4, Bc5 }

    public static Dds.Image? TryDecode(byte[] d, int length)
    {
        if (length < 128 || BitConverter.ToUInt32(d, 0) != 0x20534444) return null;   // « DDS »
        int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
        uint pfFlags = BitConverter.ToUInt32(d, 80);
        if ((pfFlags & 4) == 0) return null;                                              // pas de FourCC
        var fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
        int offset = 128;
        Kind? kind = fourcc switch
        {
            "DXT1" => Kind.Bc1, "DXT2" or "DXT3" => Kind.Bc2, "DXT4" or "DXT5" => Kind.Bc3,
            "ATI1" or "BC4U" => Kind.Bc4, "ATI2" or "BC5U" => Kind.Bc5, _ => null,
        };
        if (fourcc == "DX10")
        {
            if (length < 148) return null;
            offset = 148;
            kind = BitConverter.ToUInt32(d, 128) switch
            {
                71 or 72 => Kind.Bc1, 74 or 75 => Kind.Bc2, 77 or 78 => Kind.Bc3, 80 => Kind.Bc4, 83 => Kind.Bc5, _ => null,
            };
        }
        if (kind is not { } k || w <= 0 || h <= 0) return null;
        int blockSize = k is Kind.Bc1 or Kind.Bc4 ? 8 : 16;
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        if (offset + (long)bw * bh * blockSize > length) return null;
        var rgba = new byte[w * h * 4];
        Span<float> px = stackalloc float[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int b = offset + (by * bw + bx) * blockSize;
                switch (k)
                {
                    case Kind.Bc1: Color(d, b, px, true); break;
                    case Kind.Bc2: Color(d, b + 8, px, false); ExplicitAlpha(d, b, px); break;
                    case Kind.Bc3: Color(d, b + 8, px, false); Interpolated(d, b, px, 3); break;
                    case Kind.Bc4:
                        Interpolated(d, b, px, 0);
                        // texconv rend BC4 en gris : (r, r, r, 255)
                        for (int i = 0; i < 16; i++) { px[i * 4 + 1] = px[i * 4 + 2] = px[i * 4]; px[i * 4 + 3] = 1; }
                        break;
                    case Kind.Bc5:
                        for (int i = 0; i < 16; i++) { px[i * 4 + 2] = 0; px[i * 4 + 3] = 1; }
                        Interpolated(d, b, px, 0);
                        Interpolated(d, b + 8, px, 1);
                        break;
                }
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int ox = bx * 4 + x, oy = by * 4 + y;
                        if (ox >= w || oy >= h) continue;
                        for (int c = 0; c < 4; c++)
                            rgba[(oy * w + ox) * 4 + c] = k is Kind.Bc4 or Kind.Bc5 ? Truncate(px[(y * 4 + x) * 4 + c]) : ToByte(px[(y * 4 + x) * 4 + c]);
                    }
            }
        return new Dds.Image(w, h, rgba);
    }

    /// <summary>
    /// DDS non compressé à masques (en-tête sans DX10) : RGB 24 bits (masques de teinte !COR), 32 bits, 16 bits (565, 4444…),
    /// luminance (L8, L16, A8L8) et alpha seul (A8). Chaque canal : (valeur & masque) décalée, ramenée sur 8 bits par
    /// valeur × 255 / max arrondi au plus proche ; canal absent : 0 (alpha : 255), luminance recopiée dans R, G et B.
    /// Null pour les autres formats.
    /// </summary>
    public static Dds.Image? TryDecodeMasked(byte[] d, int length)
    {
        if (length < 128 || BitConverter.ToUInt32(d, 0) != 0x20534444) return null;
        int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
        uint flags = BitConverter.ToUInt32(d, 80);
        if ((flags & 4) != 0 || (flags & (0x40 | 0x20000 | 0x2)) == 0) return null;   // FourCC, ou ni RGB ni luminance ni alpha
        int bpp = BitConverter.ToInt32(d, 88);
        if (bpp is not (8 or 16 or 24 or 32) || w <= 0 || h <= 0) return null;
        uint rm = BitConverter.ToUInt32(d, 92), gm = BitConverter.ToUInt32(d, 96), bm = BitConverter.ToUInt32(d, 100), am = BitConverter.ToUInt32(d, 104);
        bool lum = (flags & 0x20000) != 0, hasAlpha = (flags & 0x1) != 0 || (flags & 0x2) != 0;
        if ((flags & 0x2) != 0 && (flags & 0x40) == 0 && !lum) { am = am != 0 ? am : rm; rm = gm = bm = 0; }   // A8
        int stride = w * bpp / 8;
        if (128 + (long)stride * h > length) return null;
        static byte Chan(uint px, uint mask)
        {
            if (mask == 0) return 0;
            int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
            uint max = mask >> shift;
            uint v = (px & mask) >> shift;
            return max == 255 ? (byte)v : (byte)Math.Round(v * 255.0 / max, MidpointRounding.ToEven);
        }
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = 128 + y * stride + x * bpp / 8;
                uint px = bpp switch { 8 => d[p], 16 => BitConverter.ToUInt16(d, p), 24 => (uint)(d[p] | d[p + 1] << 8 | d[p + 2] << 16), _ => BitConverter.ToUInt32(d, p) };
                int o = (y * w + x) * 4;
                if (lum) { rgba[o] = rgba[o + 1] = rgba[o + 2] = Chan(px, rm); }
                else { rgba[o] = Chan(px, rm); rgba[o + 1] = Chan(px, gm); rgba[o + 2] = Chan(px, bm); }
                rgba[o + 3] = hasAlpha && am != 0 ? Chan(px, am) : (byte)255;
            }
        return new Dds.Image(w, h, rgba);
    }

    /// <summary>XMStoreUByteN4 : saturation, × 255, arrondi au plus proche (pair en cas d'égalité).</summary>
    static byte ToByte(float v)
    {
        if (!(v > 0)) v = 0;
        if (v > 1) v = 1;
        return (byte)MathF.Round(v * 255f, MidpointRounding.ToEven);
    }

    /// <summary>BC4 et BC5 (canaux R8 / R8G8 dans texconv) : × 255 puis troncature (vérifié sur eye_m.dds : 0 écart
    /// sur 65 536 pixels, contre 8 020 avec l'arrondi).</summary>
    static byte Truncate(float v)
    {
        if (!(v > 0)) v = 0;
        if (v > 1) v = 1;
        return (byte)(v * 255f);
    }

    /// <summary>Bloc couleur BC1 (DecodeBC1 de DirectXTex) ; isBc1 : mode 3 couleurs + transparent si c0 ≤ c1.</summary>
    static void Color(byte[] d, int b, Span<float> px, bool isBc1)
    {
        ushort w0 = BitConverter.ToUInt16(d, b), w1 = BitConverter.ToUInt16(d, b + 2);
        var c = new float[16];
        void Set(int i, ushort v)
        {
            c[i * 4] = ((v >> 11) & 31) * (1f / 31f);
            c[i * 4 + 1] = ((v >> 5) & 63) * (1f / 63f);
            c[i * 4 + 2] = (v & 31) * (1f / 31f);
            c[i * 4 + 3] = 1f;
        }
        Set(0, w0);
        Set(1, w1);
        void Lerp(int i, float t)
        {
            for (int k = 0; k < 4; k++) c[i * 4 + k] = (c[4 + k] - c[k]) * t + c[k];
        }
        if (isBc1 && w0 <= w1)
        {
            Lerp(2, 0.5f);
            for (int k = 0; k < 4; k++) c[12 + k] = 0;
        }
        else
        {
            Lerp(2, 1f / 3f);
            Lerp(3, 2f / 3f);
        }
        uint bits = BitConverter.ToUInt32(d, b + 4);
        for (int i = 0; i < 16; i++)
        {
            int idx = (int)((bits >> (i * 2)) & 3);
            for (int k = 0; k < 4; k++) px[i * 4 + k] = c[idx * 4 + k];
        }
    }

    /// <summary>Alpha explicite 4 bits (BC2).</summary>
    static void ExplicitAlpha(byte[] d, int b, Span<float> px)
    {
        ulong bits = BitConverter.ToUInt64(d, b);
        for (int i = 0; i < 16; i++) px[i * 4 + 3] = ((bits >> (i * 4)) & 15) * (1f / 15f);
    }

    /// <summary>Canal interpolé sur 8 niveaux (alpha de BC3, BC4, BC5), dans le canal ch.</summary>
    static void Interpolated(byte[] d, int b, Span<float> px, int ch)
    {
        byte a0 = d[b], a1 = d[b + 1];
        Span<float> f = stackalloc float[8];
        f[0] = a0 * (1f / 255f);
        f[1] = a1 * (1f / 255f);
        if (a0 > a1)
            for (int i = 1; i < 7; i++) f[i + 1] = (f[0] * (7 - i) + f[1] * i) * (1f / 7f);
        else
        {
            for (int i = 1; i < 5; i++) f[i + 1] = (f[0] * (5 - i) + f[1] * i) * (1f / 5f);
            f[6] = 0;
            f[7] = 1;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)d[b + 2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) px[i * 4 + ch] = f[(int)((bits >> (i * 3)) & 7)];
    }
}
