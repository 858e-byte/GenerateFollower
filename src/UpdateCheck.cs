using System.Text;
using System.Text.Json.Nodes;

namespace GenerateFollower;

/// <summary>
/// Alerte de nouvelle version : au démarrage du Builder, la dernière version publiée est demandée à GitHub (Release
/// « latest » du dépôt public), en arrière-plan et au plus une fois par jour (work\update_check.json). La page affiche
/// un lien quand elle est plus récente que l'application. Rien n'est envoyé d'autre que la requête elle-même (pas
/// d'identifiant, pas de données du joueur) ; aucune mise à jour n'est téléchargée ni installée. Désactivable : réglage
/// « Vérifier les nouvelles versions » = Non, ou variable GF_SANS_MAJ=1. Hors ligne ou en erreur : rien n'est affiché.
/// </summary>
public static class UpdateCheck
{
    const string Repo = "858e-byte/GenerateFollower";
    const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
    const string Page = "https://github.com/" + Repo + "/releases/latest";
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    static volatile string? latest;
    static volatile string url = Page;
    /// <summary>« désactivé », « en cours », « fait », « échec : … » (rapport de diagnostic).</summary>
    public static volatile string State = "pas lancé";

    static string CachePath(Context ctx) => Path.Combine(ctx.WorkDir, "update_check.json");

    public static void Start(Context ctx)
    {
        if (!ctx.Settings.UseUpdateCheck || Environment.GetEnvironmentVariable("GF_SANS_MAJ") is { Length: > 0 })
        {
            State = "désactivé";
            return;
        }
        // réponse du jour gardée : pas de requête à chaque lancement
        try
        {
            var cache = CachePath(ctx);
            if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < MaxAge && JsonNode.Parse(File.ReadAllText(cache)) is JsonObject o)
            {
                Accept(o["version"]?.GetValue<string>(), o["url"]?.GetValue<string>());
                State = "fait (réponse du jour)";
                return;
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
        State = "en cours";
        // le serveur du Builder répond une requête à la fois : la vérification ne doit jamais le faire attendre
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("GenerateFollower");   // exigé par GitHub ; nom générique, sans version ni identifiant
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var o = JsonNode.Parse(await http.GetStringAsync(Api)) as JsonObject;
                Accept(o?["tag_name"]?.GetValue<string>(), o?["html_url"]?.GetValue<string>());
                if (latest != null)
                    File.WriteAllText(CachePath(ctx), new JsonObject { ["version"] = latest, ["url"] = url }.ToJsonString(), new UTF8Encoding(false));
                State = latest != null ? "fait" : "échec : réponse sans version";
            }
            catch (Exception e) when (e is not OutOfMemoryException) { State = "échec : " + e.GetType().Name; }
        });
    }

    static void Accept(string? tag, string? link)
    {
        if (Parse(tag) == null) return;
        latest = tag!.Trim().TrimStart('v', 'V');
        // lien affiché dans la page : seulement vers le dépôt officiel
        url = link != null && link.StartsWith("https://github.com/" + Repo + "/", StringComparison.OrdinalIgnoreCase) ? link : Page;
    }

    /// <summary>« 1.2.1 », « v1.3.0 », « 0.15.0-beta », « 1.2.1+abc » → (numéro, version finale ?) ; null si illisible.</summary>
    public static (Version Number, bool Final)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimStart('v', 'V');
        int plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        int dash = t.IndexOf('-');
        var number = dash >= 0 ? t[..dash] : t;
        if (!number.Contains('.')) number += ".0";
        return Version.TryParse(number, out var v) ? (v, dash < 0) : null;
    }

    /// <summary>La version publiée est-elle plus récente que celle de l'application ? (même numéro : une version finale
    /// remplace une bêta.)</summary>
    public static bool IsNewer(string? published, string? current)
    {
        if (Parse(published) is not { } p || Parse(current) is not { } c) return false;
        int cmp = p.Number.CompareTo(c.Number);
        return cmp > 0 || cmp == 0 && p.Final && !c.Final;
    }

    public static JsonObject Json() => new()
    {
        ["version"] = SupportReport.Version, ["derniere"] = latest, ["url"] = url,
        ["nouvelle"] = IsNewer(latest, SupportReport.Version), ["etat"] = State,
    };
}
