using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Allocators;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// FormID fixes d'un build à l'autre : chaque EditorID garde le FormID qu'il a reçu la première fois, mémorisé par
/// plugin dans work\formids\&lt;plugin&gt;.json. Ajouter ou retirer un follower d'un pack ne décale donc plus les
/// autres (les sauvegardes qui les contiennent restent valides). Un FormID déjà attribué n'est jamais réutilisé.
/// </summary>
public sealed class StableFormIds : IFormKeyAllocator
{
    const uint First = 0x800, Last = 0xFFF;   // plage d'un ESL
    readonly ModKey _modKey;
    readonly string _path;
    readonly Dictionary<string, uint> _map;
    readonly HashSet<uint> _used;
    readonly HashSet<uint> _given = [];   // FormID déjà attribués pendant ce build
    uint _next = First;

    public StableFormIds(Context ctx, SkyrimMod mod)
    {
        _modKey = mod.ModKey;
        _path = Path.Combine(Context.Dir(Path.Combine(ctx.WorkDir, "formids")), mod.ModKey.FileName + ".json");
        _map = File.Exists(_path)
            ? new Dictionary<string, uint>(JsonSerializer.Deserialize<Dictionary<string, uint>>(File.ReadAllText(_path))!, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        _used = [.. _map.Values];
        mod.SetAllocator(this);
    }

    public FormKey GetNextFormKey() => new(_modKey, NewId());

    public FormKey GetNextFormKey(string? editorID)
    {
        if (string.IsNullOrEmpty(editorID)) return GetNextFormKey();
        // Deux enregistrements de types différents peuvent avoir le même EditorID (headpart et TextureSet GF_Eyes4) :
        // le second est mémorisé sous « EditorID#2 »
        var key = editorID;
        for (int n = 2; _map.TryGetValue(key, out var taken) && !_given.Add(taken); n++) key = $"{editorID}#{n}";
        if (!_map.TryGetValue(key, out var id)) { _map[key] = id = NewId(); _given.Add(id); }
        return new FormKey(_modKey, id);
    }

    uint NewId()
    {
        while (_used.Contains(_next)) _next++;
        if (_next > Last) throw new GfException($"{_modKey.FileName} : plus de FormID libres dans la plage ESL (0x800-0xFFF). Crée un autre pack.");
        _used.Add(_next);
        return _next++;
    }

    /// <summary>Plus grand FormID attribué + 1, pour l'en-tête du plugin.</summary>
    public uint NextFree => _used.Count == 0 ? First : _used.Max() + 1;

    public void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_map.OrderBy(kv => kv.Value).ToDictionary(), new JsonSerializerOptions { WriteIndented = true }));
}
