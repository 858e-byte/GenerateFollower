using System.Text;

namespace GenerateFollower;

/// <summary>
/// Réécrit le chemin de la texture de teint (slot FaceTint) d'une tête exportée par RaceMenu.
/// L'export pointe vers "Data\SKSE\Plugins\CharGen\xxx.dds" ; un FaceGen de PNJ doit pointer
/// vers Actors\Character\FaceGenData\FaceTint\&lt;plugin&gt;\&lt;FormID&gt;.dds.
/// Format NIF 20.2.0.7 (Skyrim SE, BS version 100) : on modifie les blocs BSShaderTextureSet
/// et on met à jour la table des tailles de blocs de l'en-tête.
/// </summary>
public static class NifPatcher
{
    /// <summary>
    /// Renomme des chaînes de la table de l'en-tête (noms des formes et des nœuds), sans toucher aux blocs.
    /// Le fichier reste identique à l'octet près pour le reste : le moteur rejette les FaceGen dont le skin
    /// a été réécrit par PyNifly/nifly (tests 9 et 10), alors qu'il accepte l'export RaceMenu tel quel.
    /// </summary>
    /// <summary>Chaînes de la table de l'en-tête (noms des formes, nœuds et os).</summary>
    public static HashSet<string> ReadStrings(byte[] d)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PatchStrings(d, s => { all.Add(s); return null; }, out _);
        return all;
    }

    public static byte[] PatchStrings(byte[] d, Func<string, string?> rename, out int replaced)
    {
        replaced = 0;
        int p = Array.IndexOf(d, (byte)'\n') + 1;
        if (p <= 0 || !Encoding.ASCII.GetString(d, 0, p).StartsWith("Gamebryo File Format, Version 20.2.0.7"))
            throw new GfException("NIF non reconnu (attendu 20.2.0.7)");
        uint U32(int at) => BitConverter.ToUInt32(d, at);
        ushort U16(int at) => BitConverter.ToUInt16(d, at);
        p += 4 + 1;
        p += 4;                                          // user version
        int numBlocks = (int)U32(p); p += 4;
        uint bsVersion = U32(p); p += 4;
        int shortStrings = bsVersion >= 103 ? 4 : 3;
        for (int i = 0; i < shortStrings; i++) p += 1 + d[p];
        int numTypes = U16(p); p += 2;
        for (int i = 0; i < numTypes; i++) p += 4 + (int)U32(p);
        p += 2 * numBlocks + 4 * numBlocks;              // index des types, tailles des blocs
        int stringsPos = p;
        int numStrings = (int)U32(p); p += 8;
        var strings = new List<string>();
        for (int i = 0; i < numStrings; i++)
        {
            int len = (int)U32(p);
            var s = Encoding.Latin1.GetString(d, p + 4, len);
            p += 4 + len;
            var n = rename(s);
            if (n != null && n != s) { s = n; replaced++; }
            strings.Add(s);
        }
        var ms = new MemoryStream();
        ms.Write(d, 0, stringsPos);
        ms.Write(BitConverter.GetBytes(strings.Count));
        ms.Write(BitConverter.GetBytes(strings.Count == 0 ? 0 : strings.Max(x => Encoding.Latin1.GetByteCount(x))));
        foreach (var s in strings)
        {
            var b = Encoding.Latin1.GetBytes(s);
            ms.Write(BitConverter.GetBytes(b.Length));
            ms.Write(b);
        }
        ms.Write(d, p, d.Length - p);                    // groupes, blocs et pied de page inchangés
        return ms.ToArray();
    }

    /// <param name="remap">chemins de textures de l'export à remplacer (textures absentes chez l'utilisateur,
    /// voir work\texture_remap.json) ; renvoie null pour garder le chemin</param>
    public static byte[] PatchTintPath(byte[] d, string newPath, out int replaced, Func<string, string?>? remap = null)
    {
        int count = 0;
        var result = RewriteTextureSets(d, block => PatchTextureSet(block, newPath, ref count, remap));
        replaced = count;
        return result;
    }

    /// <summary>
    /// Remplace des chemins de textures (tous les slots de tous les BSShaderTextureSet) : <paramref name="map"/> renvoie
    /// le nouveau chemin, ou null pour garder l'ancien. Sert à donner au visage une diffuse propre au follower
    /// (overlays du visage cuits dedans, têtes non UBE).
    /// </summary>
    public static byte[] ReplaceTextures(byte[] d, Func<string, string?> map, out int replaced)
    {
        int count = 0;
        var result = RewriteTextureSets(d, block =>
        {
            int n = BitConverter.ToInt32(block, 0);
            var ms = new MemoryStream();
            ms.Write(block, 0, 4);
            int p = 4;
            for (int i = 0; i < n; i++)
            {
                int len = BitConverter.ToInt32(block, p);
                string tex = Encoding.ASCII.GetString(block, p + 4, len);
                p += 4 + len;
                if (tex.Length > 0 && map(tex) is { } other) { tex = other; count++; }
                var bytes = Encoding.ASCII.GetBytes(tex);
                ms.Write(BitConverter.GetBytes(bytes.Length));
                ms.Write(bytes);
            }
            ms.Write(block, p, block.Length - p);
            return ms.ToArray();
        });
        replaced = count;
        return result;
    }

    /// <summary>Réécrit chaque bloc BSShaderTextureSet et met à jour la table des tailles de blocs.</summary>
    static byte[] RewriteTextureSets(byte[] d, Func<byte[], byte[]> patch)
    {
        int p = Array.IndexOf(d, (byte)'\n') + 1;
        if (p <= 0 || !Encoding.ASCII.GetString(d, 0, p).StartsWith("Gamebryo File Format, Version 20.2.0.7"))
            throw new GfException("NIF non reconnu (attendu 20.2.0.7)");
        uint U32(int at) => BitConverter.ToUInt32(d, at);
        ushort U16(int at) => BitConverter.ToUInt16(d, at);

        p += 4 + 1;                     // version, endian
        uint userVersion = U32(p); p += 4;
        int numBlocks = (int)U32(p); p += 4;
        uint bsVersion = U32(p); p += 4;
        if (userVersion != 12) throw new GfException($"NIF user version {userVersion} inattendue");
        int shortStrings = bsVersion >= 103 ? 4 : 3;   // auteur, process script, export script (+ max filepath)
        for (int i = 0; i < shortStrings; i++) p += 1 + d[p];

        int numTypes = U16(p); p += 2;
        var typeNames = new string[numTypes];
        for (int i = 0; i < numTypes; i++) { int len = (int)U32(p); typeNames[i] = Encoding.ASCII.GetString(d, p + 4, len); p += 4 + len; }
        int typeIdxPos = p; p += 2 * numBlocks;
        int sizePos = p; p += 4 * numBlocks;
        int numStrings = (int)U32(p); p += 8;           // num strings, max length
        for (int i = 0; i < numStrings; i++) p += 4 + (int)U32(p);
        int numGroups = (int)U32(p); p += 4 + 4 * numGroups;

        var head = new MemoryStream();
        head.Write(d, 0, p);
        var blocks = new MemoryStream();
        var newSizes = new uint[numBlocks];
        int bp = p;
        for (int b = 0; b < numBlocks; b++)
        {
            int size = (int)U32(sizePos + 4 * b);
            string type = typeNames[U16(typeIdxPos + 2 * b) & 0x7FFF];
            byte[] block = d[bp..(bp + size)];
            if (type == "BSShaderTextureSet") block = patch(block);
            blocks.Write(block);
            newSizes[b] = (uint)block.Length;
            bp += size;
        }
        blocks.Write(d, bp, d.Length - bp);             // footer (racines)

        var outBytes = head.ToArray();
        for (int b = 0; b < numBlocks; b++) BitConverter.GetBytes(newSizes[b]).CopyTo(outBytes, sizePos + 4 * b);
        return [.. outBytes, .. blocks.ToArray()];
    }

    /// <summary>
    /// Cheveux SMP rigides : dans chaque skin (NiSkinInstance / BSDismemberSkinInstance), les références vers un
    /// os de physique de l'export (« hdtSSEPhysics_AutoRename_… », absent du squelette d'un PNJ : mèches étirées aux
    /// tests 8 et 11) sont redirigées vers « NPC Head [Head] », et la transformation de liaison de cet os (NiSkinData)
    /// est remplacée par celle de la tête. Modification en place : sommets, poids, partitions et tailles des blocs
    /// restent ceux de l'export ; les sommets suivent la tête, sans physique.
    /// </summary>
    public static byte[] PinPhysicsBones(byte[] src, out int redirected, out int fallback)
    {
        redirected = fallback = 0;
        var d = (byte[])src.Clone();
        int p = Array.IndexOf(d, (byte)'\n') + 1;
        if (p <= 0 || !Encoding.ASCII.GetString(d, 0, p).StartsWith("Gamebryo File Format, Version 20.2.0.7"))
            throw new GfException("NIF non reconnu (attendu 20.2.0.7)");
        uint U32(int at) => BitConverter.ToUInt32(d, at);
        int I32(int at) => BitConverter.ToInt32(d, at);
        ushort U16(int at) => BitConverter.ToUInt16(d, at);
        p += 4 + 1 + 4;
        int numBlocks = (int)U32(p); p += 4;
        uint bsVersion = U32(p); p += 4;
        for (int i = 0, n = bsVersion >= 103 ? 4 : 3; i < n; i++) p += 1 + d[p];
        int numTypes = U16(p); p += 2;
        var typeNames = new string[numTypes];
        for (int i = 0; i < numTypes; i++) { int len = (int)U32(p); typeNames[i] = Encoding.ASCII.GetString(d, p + 4, len); p += 4 + len; }
        int typeIdxPos = p; p += 2 * numBlocks;
        int sizePos = p; p += 4 * numBlocks;
        int numStrings = (int)U32(p); p += 8;
        var strings = new string[numStrings];
        for (int i = 0; i < numStrings; i++) { int len = (int)U32(p); strings[i] = Encoding.Latin1.GetString(d, p + 4, len); p += 4 + len; }
        int numGroups = (int)U32(p); p += 4 + 4 * numGroups;

        var off = new int[numBlocks];
        var type = new string[numBlocks];
        for (int b = 0; b < numBlocks; b++) { off[b] = p; type[b] = typeNames[U16(typeIdxPos + 2 * b) & 0x7FFF]; p += (int)U32(sizePos + 4 * b); }
        string? Name(int b) { int s = I32(off[b]); return s >= 0 && s < numStrings ? strings[s] : null; }
        bool IsNode(int b) => b >= 0 && b < numBlocks && type[b] == "NiNode";

        int head = Enumerable.Range(0, numBlocks).FirstOrDefault(b => IsNode(b) && Name(b) == "NPC Head [Head]", -1);
        bool IsPhys(int b) => IsNode(b) && (Name(b) ?? "").StartsWith("hdtSSEPhysics_AutoRename", StringComparison.OrdinalIgnoreCase);
        var skins = Enumerable.Range(0, numBlocks).Where(b => type[b] is "NiSkinInstance" or "BSDismemberSkinInstance").ToList();
        if (head < 0 || !skins.Any(s => Enumerable.Range(0, I32(off[s] + 12)).Any(i => IsPhys(I32(off[s] + 16 + 4 * i)))))
            return d;

        // NiSkinData : transformation globale (52 octets), nb d'os, a-poids ; puis par os : transformation (52) + sphère (16)
        // [+ nb de sommets (u16) + 6 octets par sommet si a-poids]
        List<int> Entries(int data)
        {
            int q = off[data] + 52;
            int n = (int)U32(q); bool hasW = d[q + 4] != 0; q += 5;
            var e = new List<int>();
            for (int i = 0; i < n; i++) { e.Add(q); q += 68; if (hasW) q += 2 + 6 * U16(q); }
            return e;
        }
        // Liaison de la tête, par transformation globale du skin (pour une forme qui n'a pas la tête parmi ses os)
        var headBind = new Dictionary<string, byte[]>();
        foreach (var s in skins)
        {
            int data = I32(off[s]), nb = I32(off[s] + 12);
            for (int i = 0; i < nb; i++)
                if (I32(off[s] + 16 + 4 * i) == head && type[data] == "NiSkinData")
                    headBind.TryAdd(Convert.ToHexString(d, off[data], 52), d[Entries(data)[i]..(Entries(data)[i] + 68)]);
        }
        foreach (var s in skins)
        {
            int data = I32(off[s]), nb = I32(off[s] + 12);
            if (type[data] != "NiSkinData") continue;
            var entries = Entries(data);
            byte[]? bind = null;
            for (int i = 0; i < nb; i++) if (I32(off[s] + 16 + 4 * i) == head) bind = d[entries[i]..(entries[i] + 68)];
            if (bind == null && !headBind.TryGetValue(Convert.ToHexString(d, off[data], 52), out bind))
            {
                bind = headBind.Values.FirstOrDefault();
                if (bind == null) continue;
                if (Enumerable.Range(0, nb).Any(i => IsPhys(I32(off[s] + 16 + 4 * i)))) fallback++;
            }
            for (int i = 0; i < nb; i++)
            {
                if (!IsPhys(I32(off[s] + 16 + 4 * i))) continue;
                BitConverter.GetBytes(head).CopyTo(d, off[s] + 16 + 4 * i);
                bind.CopyTo(d, entries[i]);
                redirected++;
            }
        }
        return d;
    }

    static byte[] PatchTextureSet(byte[] block, string newPath, ref int replaced, Func<string, string?>? remap)
    {
        int n = BitConverter.ToInt32(block, 0);
        var ms = new MemoryStream();
        ms.Write(block, 0, 4);
        int p = 4;
        for (int i = 0; i < n; i++)
        {
            int len = BitConverter.ToInt32(block, p);
            string tex = Encoding.ASCII.GetString(block, p + 4, len);
            p += 4 + len;
            if (tex.Contains("plugins\\chargen", StringComparison.OrdinalIgnoreCase) && tex.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                tex = newPath;
                replaced++;
            }
            else if (tex.Length > 0 && remap?.Invoke(tex) is { } other) tex = other;
            var bytes = Encoding.ASCII.GetBytes(tex);
            ms.Write(BitConverter.GetBytes(bytes.Length));
            ms.Write(bytes);
        }
        ms.Write(block, p, block.Length - p);
        return ms.ToArray();
    }
}
