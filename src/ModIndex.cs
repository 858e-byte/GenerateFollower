using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Index des mods du gestionnaire (offline\mods_index.py → work\mods_index.json) : plugins et textures de chaque mod,
/// activé ou non. Vortex : dossier de staging ; MO2 : dossier mods de l'instance, activation lue dans modlist.txt du
/// profil. Sert au Follower Builder à réparer une texture introuvable (même nom de fichier installé ailleurs, comme
/// EyesCunning\eyes4.dds → Eyes1\eyes4.dds pour Diabetox) et à dire quel mod fournit un plugin ou une texture manquante.
/// </summary>
public class ModIndex
{
    /// <summary>Fichier candidat ; Deployed = présent dans Data, ou sous MO2 lu directement dans un mod activé (le build
    /// copie depuis le Data virtuel).</summary>
    public record Candidate(string Mod, string Rel, bool Deployed);

    readonly Dictionary<string, List<(string Mod, string Rel)>> byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> pluginMods = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Data virtuel (Data, ou mods de MO2 lus directement) : « déployé » = visible par le jeu.</summary>
    readonly GameFiles files;
    readonly string manager;
    /// <summary>MO2 : modlist.txt du profil (null pour Vortex, ou profil introuvable).</summary>
    readonly Mo2.ModList? modList;

    /// <summary>MO2 : avertissement si l'outil ne voit pas les mods (lancé hors de MO2), sinon null.</summary>
    public string? Warning { get; }

    /// <summary>Dossier des mods du gestionnaire (réglages ; défauts : staging Vortex, mods de l'instance MO2).</summary>
    public static string ModsDir(Context ctx) => ctx.Settings.ResolvedModsDir();
    public static string IndexPath(Context ctx) => Path.Combine(ctx.WorkDir, "mods_index.json");

    /// <summary>Dossier des mods et plugins actifs : pour l'index des headparts des plugins inactifs.</summary>
    readonly string modsDir, headPartsPath;
    readonly HashSet<string> active;
    /// <summary>EditorID de headpart → (plugin, mod, type) dans les plugins installés mais inactifs ; construit à la demande.</summary>
    Dictionary<string, List<(string Plugin, string Mod, int Type)>>? headParts;

    ModIndex(Context ctx, JsonObject mods)
    {
        files = ctx.Files;
        manager = ctx.Settings.ManagerLabel;
        modsDir = ModsDir(ctx);
        headPartsPath = Path.Combine(ctx.WorkDir, "headparts_inactifs.json");
        active = ctx.ActivePlugins;
        if (ctx.Settings.Mo2Instance() is { } inst)
        {
            modList = Mo2.ReadModList(ctx.Settings.ResolvedMo2ProfileDir());
            Warning = Mo2.DataWarning(inst, modList, ctx.Files);
        }
        foreach (var (mod, node) in mods)
        {
            foreach (var p in node!["plugins"]!.AsArray())
                (pluginMods.TryGetValue(p!.GetValue<string>(), out var l) ? l : pluginMods[p.GetValue<string>()] = []).Add(mod);
            foreach (var d in node["dds"]!.AsArray())
            {
                var rel = d!.GetValue<string>();
                var name = Path.GetFileName(rel);
                (byName.TryGetValue(name, out var l) ? l : byName[name] = []).Add((mod, rel));
            }
        }
    }

    /// <summary>Met l'index à jour (incrémental, environ 0,5 s si rien n'a changé) et le charge ; null sans dossier de mods.</summary>
    public static ModIndex? Load(Context ctx, bool refresh)
    {
        var modsDir = ModsDir(ctx);
        if (!Directory.Exists(modsDir)) return null;
        if (refresh || !File.Exists(IndexPath(ctx)))
        {
            if (Context.PythonEngine) Py.Run(ctx, "mods_index.py", modsDir, IndexPath(ctx));
            else NativeTools.ModsIndex(modsDir, IndexPath(ctx));
        }
        return new ModIndex(ctx, JsonNode.Parse(File.ReadAllText(IndexPath(ctx)))!["mods"]!.AsObject());
    }

    /// <summary>
    /// Fichiers du même nom que la texture introuvable, les déployés (présents dans Data) d'abord, puis ceux dont le
    /// dossier ressemble le plus (dossiers en commun).
    /// </summary>
    public List<Candidate> Texture(string missing)
    {
        var name = Path.GetFileName(missing.Replace('/', '\\'));
        if (!byName.TryGetValue(name, out var hits)) return [];
        var wanted = missing.ToLowerInvariant().Split('\\', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return hits.Select(h => new Candidate(h.Mod, h.Rel, files.Find(h.Rel) != null))
            .OrderByDescending(c => c.Deployed)
            .ThenByDescending(c => c.Rel.ToLowerInvariant().Split('\\').Count(wanted.Contains))
            .ThenBy(c => c.Rel.Length).ToList();
    }

    /// <summary>« dans le mod Vortex « X », pas déployé (active-le) » / MO2 : mod désactivé ou outil lancé hors de MO2.</summary>
    public string TextureHint(string file, string mod)
    {
        if (modList == null) return L.T("{0} : dans le mod {1} « {2} », pas déployé (active-le)", file, manager, mod);
        return modList.IsEnabled(mod)
            ? files.Direct ? L.T("{0} : dans le mod MO2 « {1} », activé mais remplacé par un autre mod (priorité)", file, mod)
                : L.T("{0} : dans le mod MO2 « {1} », activé mais absent de Data (lance GenerateFollower depuis MO2)", file, mod)
            : L.T("{0} : dans le mod MO2 « {1} », désactivé dans le profil (coche-le dans MO2)", file, mod);
    }

    /// <summary>Mods qui contiennent ce plugin (installés, activés ou non).</summary>
    public List<string> Plugin(string plugin) => pluginMods.TryGetValue(plugin, out var l) ? l : [];

    /// <summary>
    /// Plugins installés mais inactifs qui définissent un headpart de cet EditorID (forme d'un export RaceMenu inconnue de
    /// l'ordre de chargement : yeux ou sourcils d'un mod désactivé depuis l'export). Index work\headparts_inactifs.json,
    /// relu seulement pour les plugins modifiés (taille, date).
    /// </summary>
    public List<(string Plugin, string Mod, int Type)> HeadPartSources(string editorId)
    {
        headParts ??= BuildHeadParts();
        return headParts.TryGetValue(editorId, out var l) ? l : [];
    }

    Dictionary<string, List<(string Plugin, string Mod, int Type)>> BuildHeadParts()
    {
        JsonObject old = new();
        try { if (File.Exists(headPartsPath)) old = JsonNode.Parse(File.ReadAllText(headPartsPath))?.AsObject() ?? new(); }
        catch (System.Text.Json.JsonException) { }
        var fresh = new JsonObject();
        var result = new Dictionary<string, List<(string, string, int)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (plugin, mods) in pluginMods)
        {
            if (active.Contains(plugin)) continue;
            foreach (var mod in mods)
            {
                var path = Path.Combine(modsDir, mod, plugin);
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                var key = mod + "\\" + plugin;
                var stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
                JsonArray parts;
                if (old[key] is JsonObject prev && prev["stamp"]?.GetValue<string>() == stamp && prev["parts"] is JsonArray p)
                    parts = (JsonArray)p.DeepClone();
                else
                {
                    parts = new JsonArray();
                    try
                    {
                        using var m = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(path, Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE, PluginText.Read);
                        foreach (var hp in m.HeadParts)
                            if (!string.IsNullOrEmpty(hp.EditorID) && hp.Type is { } t)
                                parts.Add(new JsonArray(hp.EditorID, (int)t));
                    }
                    catch (Exception) { }   // plugin illisible : ignoré, comme absent
                }
                fresh[key] = new JsonObject { ["stamp"] = stamp, ["parts"] = parts };
                foreach (var e in parts)
                {
                    var edid = e![0]!.GetValue<string>();
                    (result.TryGetValue(edid, out var l) ? l : result[edid] = []).Add((plugin, mod, e[1]!.GetValue<int>()));
                }
            }
        }
        try { File.WriteAllText(headPartsPath, fresh.ToJsonString()); } catch (IOException) { }
        return result;
    }

    /// <summary>Où trouver un plugin manquant : mod installé mais pas actif, ou absent (à télécharger).</summary>
    public string PluginHint(string plugin)
    {
        var mods = Plugin(plugin);
        if (mods.Count == 0) return L.T("{0} : absent de {1}, à télécharger (Nexus)", plugin, manager);
        var names = $"« {string.Join(" », « ", mods.Take(3))} »";
        if (modList == null) return L.T("{0} : installé dans {1} mais pas actif (mod {2}) : active-le", plugin, manager, names);
        return mods.Any(modList.IsEnabled)
            ? files.Direct ? L.T("{0} : mod MO2 {1} activé, mais plugin non coché (onglet Plugins de MO2)", plugin, names)
                : L.T("{0} : mod MO2 {1} activé, mais plugin non coché (onglet Plugins de MO2) ou outil lancé hors de MO2", plugin, names)
            : L.T("{0} : installé dans MO2 mais mod désactivé dans le profil ({1}) : coche-le", plugin, names);
    }
}
