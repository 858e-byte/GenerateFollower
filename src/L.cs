using System.Globalization;
using System.Text.Json;

namespace GenerateFollower;

/// <summary>
/// Traduction des textes affichés dans le Follower Builder. Le texte source est le français, qui sert de clé :
/// builder\i18n\&lt;langue&gt;.json = { "texte français avec {0}": "translated text with {0}" }. Le même fichier sert à
/// l'interface (index.html, fonction T) et au serveur (L.T). Texte absent du fichier = texte français. Langue : réglage
/// « language » (fr, en…), sinon celle de Windows (français si Windows est en français, sinon anglais).
/// Les rapports de build et la console restent en français.
/// </summary>
public static class L
{
    static Dictionary<string, string> map = [];

    /// <summary>Langue utilisée (« fr », « en »…).</summary>
    public static string Lang { get; private set; } = "fr";

    /// <summary>Init déjà appelé (sinon textes en français).</summary>
    public static bool Loaded { get; private set; }

    public static string FilePath(string root, string lang) => Path.Combine(AppResources.Dir(root), "builder", "i18n", lang + ".json");

    public static void Init(string root, string language)
    {
        Lang = string.IsNullOrWhiteSpace(language)
            ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "fr" : "en") : language.Trim().ToLowerInvariant();
        var file = FilePath(root, Lang);
        map = Lang != "fr" && File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [] : [];
        Loaded = true;
    }

    /// <summary>Texte traduit ; {0}, {1}… remplacés par <paramref name="args"/>.</summary>
    public static string T(string fr, params object?[] args)
    {
        var text = map.TryGetValue(fr, out var t) && !string.IsNullOrEmpty(t) ? t : fr;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }
}
