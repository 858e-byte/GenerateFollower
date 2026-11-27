using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>
/// Couleur de peau choisie dans le Builder (plan : work\diag\peau\PLAN.md). Option « couleurPeau » de
/// options_choice.json : "#RRGGBB" = couleur finale de la peau (celle du QNAM du PNJ) ; absente = couleur du preset.
/// ÉTAPE 1 SEULEMENT : choix, palette de la race et aperçu (corps + visage). Le build ne s'en sert pas encore (étape 3).
/// Race Erin : pas de choix (sa peau vient de son plugin SKSE, son visage est neutralisé à part).
/// </summary>
public static partial class Options
{
    public const string SkinToneOption = "couleurPeau";

    /// <summary>Couleur choisie (RGB), ou null : aucune, valeur illisible, ou race Erin.</summary>
    public static int? ChosenSkinTone(PresetRow row, JsonObject opts) =>
        !Erin.IsRace(row.Race) && ParseColor((opts[SkinToneOption] as JsonValue)?.GetValue<string>()) is { } rgb ? rgb : null;

    public static int? ParseColor(string? s) =>
        s != null && Regex.IsMatch(s, "^#?[0-9A-Fa-f]{6}$") ? int.Parse(s.TrimStart('#'), NumberStyles.HexNumber) : null;

    static string Hex(int rgb) => $"#{rgb & 0xFFFFFF:X6}";

    /// <summary>Palette du jeu pour ce preset : couleurs du masque de teinte « SkinTone » de sa race (celles que propose
    /// RaceMenu), mélangées sur un gris 128 selon leur intensité par défaut, comme <see cref="Jslot.SkinTone"/>. Race
    /// sans masque (race dérivée) : celle dont elle porte le nom (« 00UBE_NordRace » → « NordRace »).</summary>
    public static List<(int Rgb, string Name)> SkinPalette(Context ctx, PresetRow row)
    {
        var list = new List<(int, string)>();
        if (Erin.IsRace(row.Race) || ctx.Find<IRaceGetter>(row.Race) is not { } race) return list;
        var colors = PaletteOf(ctx, race, row.Female);
        if (colors.Count == 0 && race.EditorID is { } name)
            foreach (var r in ctx.Env.LoadOrder.PriorityOrder.Race().WinningOverrides())
                if (r.EditorID is { Length: > 4 } e && name.Length > e.Length && name.EndsWith(e, StringComparison.OrdinalIgnoreCase)
                    && (colors = PaletteOf(ctx, r, row.Female)).Count > 0) break;
        var seen = new HashSet<int>();
        foreach (var c in colors) if (seen.Add(c.Rgb)) list.Add(c);
        return list;
    }

    static List<(int Rgb, string Name)> PaletteOf(Context ctx, IRaceGetter race, bool female)
    {
        var list = new List<(int, string)>();
        var head = female ? race.HeadData?.Female : race.HeadData?.Male;
        foreach (var t in head?.TintMasks ?? [])
        {
            if (t.MaskType != TintAssets.TintMaskType.SkinTone) continue;
            foreach (var p in t.Presets)
            {
                if (!ctx.Cache.TryResolve<IColorRecordGetter>(p.Color.FormKey, out var col)) continue;
                double a = Math.Clamp(p.DefaultValue ?? 1f, 0, 1);
                int Mix(int v) => (int)Math.Round(128 * (1 - a) + v * a);
                list.Add(((Mix(col.Color.R) << 16) | (Mix(col.Color.G) << 8) | Mix(col.Color.B), col.Name?.String is { Length: > 0 } n ? n : col.EditorID ?? ""));
            }
        }
        return list;
    }

    static JsonArray SkinPaletteJson(Context ctx, PresetRow row) =>
        new(SkinPalette(ctx, row).Select(c => (JsonNode)new JsonObject { ["couleur"] = Hex(c.Rgb), ["nom"] = c.Name }).ToArray());

    static JsonNode? ChosenSkinToneJson(PresetRow row, JsonObject opts) => ChosenSkinTone(row, opts) is { } rgb ? Hex(rgb) : null;

    static void SaveSkinTone(Context ctx, PresetRow row, JsonNode? value)
    {
        if (value == null) { Set(ctx, row.Id, SkinToneOption, null); return; }
        if (Erin.IsRace(row.Race)) throw new GfException(L.T("Race Erin : la couleur de peau vient de la race, elle ne se choisit pas ici."));
        if (ParseColor((value as JsonValue)?.GetValue<string>()) is not { } rgb)
            throw new GfException(L.T("Valeur refusée pour {0} : {1}", SkinToneOption, value.ToJsonString()));
        Set(ctx, row.Id, SkinToneOption, JsonValue.Create(Hex(rgb)));
    }
}
