using System.Text;

namespace GenerateFollower;

/// <summary>Une ligne de work\presets.csv. Les colonnes inclure/nom/sexe/race/voix sont modifiables à la main.</summary>
public class PresetRow
{
    public string Id = "";
    public bool Inclure;
    public string Categorie = "";
    public string Preset = "";      // chemin relatif à CharGen\Presets, sans .jslot
    public string Nom = "";
    public string Sexe = "F";       // F ou M
    public string Race = "";        // EditorID
    public string Voix = "";        // EditorID de VoiceType
    public string Statut = "";
    public string Details = "";

    public bool Female => Sexe.Trim().ToUpperInvariant().StartsWith('F');
    public string ExportName => "GF_" + Id;
    /// <summary>Nom tiré du fichier .jslot (celui du scan), indépendant du nom en jeu choisi dans le Builder : sert à
    /// retrouver l'export RaceMenu, qui porte le nom du preset.</summary>
    public string NomPreset => Scanner.CleanName(System.IO.Path.GetFileName(Preset));
}

public static class PresetTable
{
    static readonly string[] Header = ["id", "inclure", "categorie", "preset", "nom", "sexe", "race", "voix", "statut", "details"];

    public static List<PresetRow> Read(string path)
    {
        if (!File.Exists(path)) throw new GfException($"{path} introuvable : lance d'abord « scan ».");
        var rows = new List<PresetRow>();
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = Split(line);
            if (f.Count < Header.Length) throw new GfException($"Ligne CSV incomplète : {line}");
            rows.Add(new PresetRow
            {
                Id = f[0], Inclure = f[1].Trim().Equals("oui", StringComparison.OrdinalIgnoreCase),
                Categorie = f[2], Preset = f[3], Nom = f[4], Sexe = f[5], Race = f[6], Voix = f[7],
                Statut = f[8], Details = f[9],
            });
        }
        return rows;
    }

    public static void Write(string path, IEnumerable<PresetRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', Header));
        foreach (var r in rows)
            sb.AppendLine(string.Join(';', new[] { r.Id, r.Inclure ? "oui" : "non", r.Categorie, r.Preset, r.Nom, r.Sexe, r.Race, r.Voix, r.Statut, r.Details }.Select(Quote)));
        // BOM UTF-8 pour qu'Excel affiche correctement les accents
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    static string Quote(string s) => s.IndexOfAny([';', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    static List<string> Split(string line)
    {
        var res = new List<string>();
        var cur = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') q = false;
                else cur.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ';') { res.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        res.Add(cur.ToString());
        return res;
    }
}
