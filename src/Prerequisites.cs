using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Prérequis en jeu des followers générés, montrés dans le Builder (avant : seulement dans la console et le rapport de
/// support) : un mod manquant est la cause classique de « mon follower n'apparaît pas ». Requis : SKSE, PapyrusUtil
/// (StorageUtil : followers retrouvés et placés, GF_MainQuest), UIExtensions (menu d'invocation), RaceMenu (maquillages
/// et tatouages, NiOverride). Selon les presets et les choix : UBE (presets UBE), OBody NG (preset de corps choisi),
/// Faster HDT-SMP (cheveux à physique animés). Liens vérifiés le 01/11/2026 ; OBody NG (page « adulte » sur Nexus) sans lien.
/// </summary>
public static class Prerequisites
{
    public sealed record Item(string Name, bool Ok, bool Required, string Why, string? Url);

    public static List<Item> Check(Context ctx, IReadOnlyList<PresetRow> rows)
    {
        var game = Path.GetDirectoryName(ctx.DataDir) ?? "";
        bool Dll(string dll) => ctx.Files.Find(Path.Combine("SKSE", "Plugins", dll)) != null;
        const string nexus = "https://www.nexusmods.com/skyrimspecialedition/mods/";
        var list = new List<Item>
        {
            new("SKSE64", File.Exists(Path.Combine(game, "skse64_loader.exe")), true,
                L.T("Nécessaire aux trois mods suivants. Lance le jeu avec skse64_loader.exe (ou depuis ton gestionnaire de mods)."), nexus + "30379"),
            new("PapyrusUtil SE", Dll("PapyrusUtil.dll"), true,
                L.T("Retrouve et place les followers (lieu de départ, invocation)."), nexus + "13048"),
            new("UIExtensions", ctx.ActivePlugins.Contains("UIExtensions.esp"), true,
                L.T("Menu du pouvoir d'invocation (choix du follower)."), nexus + "17561"),
            new("RaceMenu", Dll("skee64.dll") && ctx.ActivePlugins.Contains("RaceMenu.esp"), true,
                L.T("Maquillages, tatouages et peintures du visage (overlays) ; sert aussi à créer les presets."), nexus + "19080"),
        };
        int ube = rows.Count(r => r.Race.StartsWith("00UBE_", StringComparison.OrdinalIgnoreCase));
        if (ube > 0)
            list.Add(new("UBE (UBE_AllRace.esp)", ctx.ActivePlugins.Contains("UBE_AllRace.esp"), true,
                L.T("Race de {0} preset(s) UBE de ta liste.", ube), null));
        var all = Options.LoadAll(ctx);
        int body = rows.Count(r => all[r.Id]?[BodyPresets.Option] != null);
        if (body > 0)
            list.Add(new("OBody NG", ctx.ActivePlugins.Contains("OBody.esp"), false,
                L.T("Applique le preset de corps choisi pour {0} follower(s).", body), null));
        list.Add(new("Faster HDT-SMP", SmpHair.SmpInstalled(ctx), false,
            L.T("Cheveux à physique (SMP) animés ; sans lui, ils sont rigides."), nexus + "57339"));
        return list;
    }

    public static JsonArray Json(Context ctx, IReadOnlyList<PresetRow> rows) => new(Check(ctx, rows).Select(i => (JsonNode)new JsonObject
    {
        ["nom"] = i.Name, ["ok"] = i.Ok, ["requis"] = i.Required, ["pourquoi"] = i.Why, ["lien"] = i.Url,
    }).ToArray());
}
