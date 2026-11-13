using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;

namespace GenerateFollower;

/// <summary>
/// Encodage des textes des plugins (noms d'objets, de PNJ…). Mutagen lit par défaut en occidental (1252) : les mods
/// traduits en chinois, russe, japonais… (souvent enregistrés en UTF-8 par xTranslator) sortaient en caractères
/// brouillés (signalement d'un joueur, 12/11). Réglage « pluginEncoding » : vide = automatique (UTF-8, et repli texte
/// par texte sur l'encodage régional de la langue du jeu), sinon « utf8 », « 1252 », « 1250 », « 1251 », « 932 ».
/// Langue du jeu : sLanguage de Skyrim.ini (aussi pour les .STRINGS du jeu de base).
/// </summary>
public static class PluginText
{
    public static Language GameLanguage { get; private set; } = Language.English;
    public static string Setting { get; private set; } = "";
    static IMutagenEncoding read = MutagenEncoding._utf8_1252;
    static IMutagenEncoding write = MutagenEncoding._1252;

    /// <summary>Langues dont le jeu lit les textes des plugins en occidental (1252) ; les autres en UTF-8.</summary>
    static readonly HashSet<Language> Western = [Language.English, Language.French, Language.German, Language.Italian,
        Language.Spanish, Language.Spanish_Mexico, Language.Portuguese_Brazil, Language.Danish, Language.Finnish,
        Language.Norwegian, Language.Swedish];

    public static void Configure(Settings settings)
    {
        GameLanguage = DetectLanguage();
        Setting = (settings.PluginEncoding ?? "").Trim().ToLowerInvariant();
        // UTF-8, et repli texte par texte sur l'encodage régional de la langue du jeu. Pas MutagenEncoding._utf8_1252 :
        // essayé le 12/11, il ne se replie pas (UTF-8 tolérant) et abîmait les noms occidentaux (« Épée » → « �p�e »)
        var utf8WithFallback = new Utf8OrFallback(GameLanguage switch
        {
            Language.Russian => MutagenEncoding._1251,
            Language.Polish or Language.Czech or Language.Hungarian => MutagenEncoding._1250,
            Language.Japanese => MutagenEncoding._932,
            Language.Greek => MutagenEncoding._1253,
            Language.Turkish => MutagenEncoding._1254,
            Language.Arabic => MutagenEncoding._1256,
            _ => MutagenEncoding._1252,
        });
        (read, write) = Setting switch
        {
            "utf8" => (MutagenEncoding._utf8, MutagenEncoding._utf8),
            "1252" => (MutagenEncoding._1252, MutagenEncoding._1252),
            "1250" => (MutagenEncoding._1250, MutagenEncoding._1250),
            "1251" => (MutagenEncoding._1251, MutagenEncoding._1251),
            "932" => (MutagenEncoding._932, MutagenEncoding._932),
            // automatique : lecture UTF-8 avec repli ; écriture comme le jeu lit pour sa langue (occidental : rien ne
            // change pour les plugins anglais/français ; sinon UTF-8)
            _ => (utf8WithFallback, Western.Contains(GameLanguage) ? MutagenEncoding._1252 : MutagenEncoding._utf8),
        };
        GameTextIsUtf8 = false;
    }

    /// <summary>Vrai si les noms du jeu de base sont en écriture non occidentale (Observe).</summary>
    public static bool GameTextIsUtf8 { get; private set; }

    /// <summary>
    /// Après l'ouverture du jeu, en automatique : beaucoup de joueurs chinois remplacent les .STRINGS anglais par des
    /// .STRINGS chinois en UTF-8 et gardent sLanguage=ENGLISH (capture du joueur, 13/11 : noms des tenues du jeu
    /// brouillés). Si les noms d'armures de Skyrim.esm contiennent des caractères non occidentaux, le jeu affiche de
    /// l'UTF-8 : nos plugins sont alors écrits en UTF-8 aussi.
    /// </summary>
    public static void Observe(Mutagen.Bethesda.Skyrim.ISkyrimModGetter? skyrimEsm)
    {
        if (Setting != "" || skyrimEsm == null) return;
        try
        {
            GameTextIsUtf8 = skyrimEsm.Armors.Take(300).Select(a => a.Name?.String ?? "")
                .Any(s => s.Any(c => c > '˿'));
        }
        catch (Exception) { return; }   // .STRINGS illisibles : on garde le choix selon la langue
        if (GameTextIsUtf8) write = MutagenEncoding._utf8;
    }

    /// <summary>Encodage des .STRINGS (textes localisés du jeu et des mods localisés), pour toute langue : le même que
    /// pour les textes intégrés (UTF-8 avec repli en automatique). Mutagen lisait les .STRINGS « anglais » en 1252.</summary>
    sealed class Provider : IMutagenEncodingProvider
    {
        public IMutagenEncoding GetEncoding(Mutagen.Bethesda.GameRelease release, Language language) => read;
    }

    /// <summary>Langue du jeu : sLanguage de Skyrim.ini (Mes documents\My Games\Skyrim Special Edition[ GOG]).</summary>
    static Language DetectLanguage()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var dir in new[] { "Skyrim Special Edition", "Skyrim Special Edition GOG", "Skyrim Special Edition EPIC" })
        {
            var ini = Path.Combine(docs, "My Games", dir, "Skyrim.ini");
            if (!File.Exists(ini)) continue;
            try
            {
                foreach (var raw in File.ReadLines(ini))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("sLanguage", StringComparison.OrdinalIgnoreCase)) continue;
                    var v = line[(line.IndexOf('=') + 1)..].Trim();
                    if (v.Equals("CHINESE", StringComparison.OrdinalIgnoreCase)) return Language.Chinese;
                    if (Enum.TryParse<Language>(v, true, out var lang)) return lang;
                }
            }
            catch (IOException) { }
        }
        return Language.English;
    }

    /// <summary>Paramètres de lecture des textes (environnement du jeu).</summary>
    public static StringsReadParameters Strings => new() { NonLocalizedEncodingOverride = read, TargetLanguage = GameLanguage, EncodingProvider = new Provider() };

    /// <summary>Paramètres pour lire un plugin à part (CreateFromBinaryOverlay).</summary>
    public static BinaryReadParameters Read => new() { StringsParam = Strings };

    /// <summary>Encodages d'écriture de nos plugins (textes intégrés, non localisés).</summary>
    public static EncodingBundle Write => new(MutagenEncoding._1252, write);

    /// <summary>Lecture : UTF-8 strict si le texte en est (octets valides), sinon l'encodage de repli. Un texte
    /// occidental avec accents (« é » = un octet 0xE9) n'est presque jamais de l'UTF-8 valide.</summary>
    public sealed class Utf8OrFallback(IMutagenEncoding fallback) : IMutagenEncoding
    {
        static readonly System.Text.UTF8Encoding Strict = new(false, true);
        public string GetString(ReadOnlySpan<byte> bytes)
        {
            try { return Strict.GetString(bytes); }
            catch (System.Text.DecoderFallbackException) { return fallback.GetString(bytes); }
        }
        // écriture (non utilisée : nos plugins s'écrivent avec PluginText.Write) : UTF-8
        public int GetByteCount(ReadOnlySpan<char> chars) => Strict.GetByteCount(chars);
        public int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes) => Strict.GetBytes(chars, bytes);
    }

    public static string Describe() => L.T("Textes des plugins : {0} ; langue du jeu : {1}",
        Setting switch { "" => L.T("automatique (UTF-8, sinon encodage régional)"), "utf8" => "UTF-8", var s => "Windows-" + s }, GameLanguage)
        + (GameTextIsUtf8 ? " " + L.T("(textes du jeu non occidentaux : plugins écrits en UTF-8)") : "");
}
