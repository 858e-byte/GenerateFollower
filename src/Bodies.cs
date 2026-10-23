namespace GenerateFollower;

/// <summary>
/// Corps et tête installés chez le joueur, lus dans Data ou les mods de MO2 (au lieu de 3BA/HIMBO supposés) : noms des formes de
/// meshes\actors\character\character assets\femalebody_1.nif et malebody_1.nif (table de chaînes de l'en-tête NIF),
/// plugins actifs pour la tête. Les familles de corps disent quelles textures de peau vont avec (mêmes UV) :
/// - femmes : CBBE (CBBE, 3BA, 3BBB), UNP (UUNP, BHUNP, UNP), VANILLA ;
/// - hommes : HIMBO, MALE (vanilla, SOS, TNG… : UV vanilla).
/// Validé en jeu seulement avec 3BA + HIMBO + UBE + High Poly Head (installation de l'utilisateur).
/// </summary>
public record Bodies(string Female, string FemaleDetail, string Male, string MaleDetail, string Head, string HeadDetail, bool Ube,
    string ErinFemale = "CBBE", string? ErinFemaleDetail = null)
{
    public const string AssetsDir = @"meshes\actors\character\character assets";
    /// <summary>Corps propre à la race Erin (option CBBE/3BA ou BHUNP de son installateur, ou GT Softbody), commun aux Erin.</summary>
    public const string ErinDir = @"meshes\actors\Erin";

    /// <summary>Familles validées en jeu (installation de référence).</summary>
    public bool Validated => Female == "CBBE" && Male == "HIMBO" && Head == "HPH";

    public static Bodies Detect(Context ctx)
    {
        var (female, fDetail) = Classify(ctx, AssetsDir, "femalebody_1.nif", true);
        var (male, mDetail) = Classify(ctx, AssetsDir, "malebody_1.nif", false);
        // corps des Erin femmes : CBBE 3BA par défaut (archive d'Erin Race), BHUNP au choix de son installateur
        var erinNif = ctx.Files.PathOf(Path.Combine(ErinDir, "femalebody_1.nif"));
        var (erin, eDetail) = File.Exists(erinNif) ? Classify(ctx, ErinDir, "femalebody_1.nif", true) : ("CBBE", (string?)null);
        // tête des races vanilla : High Poly Head (plugin), sinon tête du jeu (ou autre mod de tête : à vérifier)
        var hph = ctx.ActivePlugins.Contains("High Poly Head.esm");
        var headNif = ctx.Files.PathOf(Path.Combine(AssetsDir, "femalehead.nif"));
        // auteur de HPH dans les infos d'export de l'en-tête du NIF
        var creator = File.Exists(headNif) && System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(headNif) is var hb ? hb[..Math.Min(300, hb.Length)] : []).Contains("dmejillones")
            ? L.T(", femalehead.nif de HPH") : "";
        var head = hph ? "HPH" : "VANILLA";
        var hDetail = hph ? L.T("High Poly Head.esm actif") + creator
            : File.Exists(headNif) ? L.T("femalehead.nif remplacé en loose (mod de tête inconnu), pas de High Poly Head.esm") : L.T("tête du jeu");
        // non reconnu (corps CBBE 3BA de GT Softbody pour Erin, sans marque) : corps CBBE 3BA d'Erin supposé
        if (erin == "VANILLA") (erin, eDetail) = ("CBBE", L.T("corps non reconnu : CBBE 3BA d'Erin supposé"));
        return new Bodies(female, fDetail, male, mDetail, head, hDetail, ctx.ActivePlugins.Contains("UBE_AllRace.esp"), erin, eDetail);
    }

    /// <summary>Famille du corps d'après les noms des formes du NIF ; absent en loose = corps du jeu (BSA).</summary>
    static (string Family, string Detail) Classify(Context ctx, string dir, string file, bool female)
    {
        // Data, ou sous MO2 lu directement le mod de corps gagnant
        var path = ctx.Files.PathOf(Path.Combine(dir, file));
        if (!File.Exists(path)) return (female ? "VANILLA" : "MALE", L.T("{0} absent en loose : corps du jeu", file));
        HashSet<string> names;
        try { names = NifPatcher.ReadStrings(File.ReadAllBytes(path)); }
        catch (Exception e) { return (female ? "VANILLA" : "MALE", L.T("{0} illisible ({1})", file, e.Message)); }
        bool Has(Func<string, bool> f) => names.Any(f);
        bool Eq(string s, string v) => s.Equals(v, StringComparison.OrdinalIgnoreCase);
        bool Contains(string s, string v) => s.Contains(v, StringComparison.OrdinalIgnoreCase);
        if (female)
        {
            if (Has(s => Eq(s, "3BA") || Contains(s, "3BBB"))) return ("CBBE", L.T("{0} (forme « {1} »)", "3BA", "3BA"));
            if (Has(s => Eq(s, "CBBE") || Contains(s, "CBBE"))) return ("CBBE", L.T("{0} (forme « {1} »)", "CBBE", "CBBE"));
            if (Has(s => Contains(s, "BHUNP"))) return ("UNP", "BHUNP");
            // « BaseShape », ou « BaseShapeVagina2 »… (corps BHUNP d'Erin de GT Softbody)
            if (Has(s => s.StartsWith("BaseShape", StringComparison.OrdinalIgnoreCase) || Contains(s, "UUNP") || Contains(s, "UNP")))
                return ("UNP", L.T("{0} (forme « {1} »)", "UUNP / BHUNP", "BaseShape"));
            // GT Softbody, option CBBE (forme « Softbody », sans marque CBBE ; sa version BHUNP a des formes BaseShape…)
            if (Has(s => Eq(s, "Softbody"))) return ("CBBE", L.T("{0} (forme « {1} »)", "GT Softbody CBBE 3BA", "Softbody"));
            return ("VANILLA", L.T("corps non reconnu (textures vanilla supposées)"));
        }
        if (Has(s => Contains(s, "HIMBO"))) return ("HIMBO", L.T("{0} (forme « {1} »)", "HIMBO", "HIMBO - Body HP"));
        if (Has(s => Contains(s, "SOS") || Contains(s, "Schlong"))) return ("MALE", "SOS (UV vanilla)");
        if (Has(s => Contains(s, "TNG"))) return ("MALE", "TNG (UV vanilla)");
        return ("MALE", L.T("corps masculin vanilla ou non reconnu (UV vanilla supposées)"));
    }

    /// <summary>Libellé d'une famille de corps pour l'interface.</summary>
    public static string Label(string family) => family switch
    {
        "CBBE" => "CBBE / 3BA", "UNP" => "UUNP / BHUNP", "VANILLA" => L.T("vanilla (femmes)"), "MALE" => L.T("vanilla / SOS / TNG (hommes)"), _ => family,
    };

    /// <summary>Résumé pour la console, les Réglages et le rapport de diagnostic.</summary>
    public string Summary => L.T("Corps féminin : {0} ({1}) ; corps masculin : {2} ({3}) ; tête des races vanilla : {4} ({5}) ; UBE : {6}.",
        Label(Female), FemaleDetail, Label(Male), MaleDetail, Head == "HPH" ? "High Poly Head" : L.T("du jeu"), HeadDetail, Ube ? L.T("oui") : L.T("non"))
        + (ErinFemaleDetail != null ? " " + L.T("Corps des Erin : {0} ({1}).", Label(ErinFemale), ErinFemaleDetail) : "");

    /// <summary>Avertissement si l'installation diffère de celle validée en jeu, sinon null.</summary>
    public string? Warning
    {
        get
        {
            var w = new List<string>();
            if (Female != "CBBE") w.Add(L.T("corps féminin {0} : seuls les skins pour ce corps sont proposés (non testé en jeu)", Label(Female)));
            if (Male != "HIMBO") w.Add(L.T("corps masculin {0} : skins pour HIMBO écartés (non testé en jeu)", Label(Male)));
            if (Head != "HPH") w.Add(L.T("pas de High Poly Head : têtes des races vanilla non testées en jeu (exports RaceMenu faits avec ta tête actuelle)"));
            return w.Count == 0 ? null : L.T("Installation différente de celle validée en jeu (3BA, HIMBO, High Poly Head) : {0}.", string.Join(" ; ", w));
        }
    }
}
