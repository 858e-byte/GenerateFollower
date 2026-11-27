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

    /// <summary>Langues de l'interface (code, nom dans la langue), dans l'ordre du choix des Réglages.</summary>
    public static readonly (string Code, string Name)[] Languages =
        [("fr", "Français"), ("en", "English"), ("es", "Español"), ("ru", "Русский"), ("zh", "简体中文"), ("ja", "日本語"), ("ko", "한국어")];

    static string root = "";
    static Dictionary<string, string>? english;

    public static void Init(string root, string language)
    {
        L.root = root;
        // automatique : langue de Windows si elle est traduite, sinon anglais
        var windows = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Lang = string.IsNullOrWhiteSpace(language)
            ? (windows == "fr" || (windows != "en" && Languages.Any(l => l.Code == windows) && File.Exists(FilePath(root, windows))) ? windows : "en")
            : language.Trim().ToLowerInvariant();
        map = Load(root, Lang);
        english = null;
        Loaded = true;
    }

    static Dictionary<string, string> Load(string root, string lang)
    {
        var file = FilePath(root, lang);
        return lang != "fr" && File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [] : [];
    }

    /// <summary>Texte vu en jeu (écrit dans un plugin) : dans la langue de l'interface si le jeu sait l'afficher
    /// (encodage d'écriture des plugins, PluginText), sinon en anglais. Ex. interface en chinois, Skyrim occidental (1252) :
    /// des « ??? » en jeu, donc de l'anglais.</summary>
    public static string G(string fr, params object?[] args)
    {
        var text = T(fr, args);
        if (PluginText.CanWrite(text)) return text;
        english ??= Load(root, "en");
        var en = english.TryGetValue(fr, out var t) && !string.IsNullOrEmpty(t) ? t : fr;
        return args.Length == 0 ? en : string.Format(CultureInfo.InvariantCulture, en, args);
    }

    /// <summary>Texte traduit ; {0}, {1}… remplacés par <paramref name="args"/>.</summary>
    public static string T(string fr, params object?[] args)
    {
        var text = map.TryGetValue(fr, out var t) && !string.IsNullOrEmpty(t) ? t : fr;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }
}
