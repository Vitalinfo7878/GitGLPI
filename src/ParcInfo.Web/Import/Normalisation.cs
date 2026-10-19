using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Import;

/// <summary>
/// Harmonisation des valeurs saisies à la main dans l'Excel (« Pc Portable » / « PC portable », « W11 » / « 11 »…).
/// </summary>
public static partial class Normalisation
{
    private static readonly HashSet<string> ValeursVides = ["", "0", "?", "-", "#N/A", "#NUM!", "#REF!", "#VALEUR!", "#VALUE!"];

    /// <summary>Nettoie un texte : espaces insécables, espaces multiples, valeurs vides (« 0 », « ? »…).</summary>
    public static string? Texte(string? valeur)
    {
        if (valeur is null) return null;
        var s = EspacesMultiples().Replace(valeur.Replace(' ', ' '), " ").Trim();
        return ValeursVides.Contains(s) ? null : s;
    }

    public static string SansAccents(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Cle(string s) => SansAccents(s).ToLowerInvariant();

    public static TypeMateriel? TypeMateriel(string? appareil, string? numInterne)
    {
        var a = Texte(appareil);
        if (a != null)
        {
            var k = Cle(a);
            if (k.Contains("portable")) return Domain.TypeMateriel.PcPortable;
            if (k.Contains("fixe") || k.Contains("pc")) return Domain.TypeMateriel.PcFixe;
            if (k.Contains("ecran")) return Domain.TypeMateriel.Ecran;
            if (k.Contains("imprimante")) return Domain.TypeMateriel.Imprimante;
        }
        return TypeDepuisNumero(numInterne);
    }

    /// <summary>Déduit le type depuis la nomenclature : PO = portable, PC = fixe, E = écran, I = imprimante (+ anciennes LPW / DTW / DAW).</summary>
    public static TypeMateriel? TypeDepuisNumero(string? numInterne)
    {
        var n = Texte(numInterne)?.ToUpperInvariant();
        if (n is null) return null;
        if (n.StartsWith("PO") || n.StartsWith("LP") || n.StartsWith("LAPTOP")) return Domain.TypeMateriel.PcPortable;
        if (n.StartsWith("PC") || n.StartsWith("DT") || n.StartsWith("DA") || n.StartsWith("AIO") || n.StartsWith("DESKTOP")) return Domain.TypeMateriel.PcFixe;
        if (Regex.IsMatch(n, @"^E\d")) return Domain.TypeMateriel.Ecran;
        if (Regex.IsMatch(n, @"^I\d")) return Domain.TypeMateriel.Imprimante;
        return null;
    }

    public static bool? OuiNon(string? valeur)
    {
        var v = Texte(valeur);
        if (v is null) return null;
        return Cle(v) switch
        {
            "oui" or "ok" or "yes" or "pui" or "o" or "labtech" => true,
            "non" or "no" or "n" => false,
            _ => null,
        };
    }

    public static string? VersionWindows(string? valeur)
    {
        var v = Texte(valeur);
        if (v is null) return null;
        var k = Cle(v).Replace("windows", "").Replace(" ", "");
        if (k.StartsWith('w')) k = k[1..];
        return k switch
        {
            "11" => "Windows 11",
            "10" => "Windows 10",
            "8.1" or "81" => "Windows 8.1",
            "8" => "Windows 8",
            "7" => "Windows 7",
            "xp" => "Windows XP",
            _ => v,
        };
    }

    public static string? EditionWindows(string? valeur)
    {
        var v = Texte(valeur);
        if (v is null) return null;
        return Cle(v) switch
        {
            "pro" or "professionnel" => "Pro",
            "famille" or "home" => "Famille",
            _ => v,
        };
    }

    public static string? Licence(string? valeur)
    {
        var v = Texte(valeur);
        if (v is null) return null;
        var k = Cle(v);
        if (k is "exchange" or "exchange online" or "excange" or "echange") return "Exchange Online";
        if (k is "premium" or "business premium") return "Business Premium";
        if (k is "business standard") return "Business Standard";
        if (k is "oui") return null;
        return v;
    }

    /// <summary>« DR  CENTRE-OUEST », « DR-SUD OUEST », « Site IDF », « DR NORD EST » → noms homogènes.</summary>
    public static string? Region(string? valeur)
    {
        var v = Texte(valeur);
        if (v is null) return null;
        var s = SansAccents(v).ToUpperInvariant();
        s = s.Replace("(HOME OFFICE)", "").Trim();
        s = Regex.Replace(s, @"^DR-", "DR ");
        s = Regex.Replace(s, @"\bSUD[ -]OUEST\b", "SUD-OUEST");
        s = Regex.Replace(s, @"\bSUD[ -]EST\b", "SUD-EST");
        s = Regex.Replace(s, @"\bCENTRE[ -]OUEST\b", "CENTRE-OUEST");
        s = Regex.Replace(s, @"\bNORD[ -]EST\b", "NE");
        s = EspacesMultiples().Replace(s, " ").Trim();
        return s.Length == 0 || s.All(char.IsDigit) ? null : s;
    }

    /// <summary>Valeurs de « SERVICE / LIEU » qui ne sont pas des régions mais des états.</summary>
    public static StatutMateriel? StatutDepuisLieu(string? regionNormalisee) => regionNormalisee switch
    {
        null => null,
        "STOCK" => StatutMateriel.EnStock,
        "REBUT" or "MISE AU REBUT" => StatutMateriel.Rebute,
        "VOLER" or "VOLE" or "VOL" => StatutMateriel.VolePerdu,
        _ when regionNormalisee.Contains("REPARATION") => StatutMateriel.EnReparation,
        _ when regionNormalisee.StartsWith("RETOUR ") => StatutMateriel.RenvoyeFournisseur,
        _ => null,
    };

    public static int? Annee(object? valeur)
    {
        int? a = valeur switch
        {
            DateTime d => d.Year,
            double x => (int)x,
            string s when int.TryParse(Texte(s), out var i) => i,
            _ => null,
        };
        return a is >= 1990 and <= 2100 ? a : null;
    }

    public static DateOnly? Date(object? valeur)
    {
        DateTime? d = valeur switch
        {
            DateTime dt => dt,
            double x when x > 0 => DateTime.FromOADate(x),
            string s when DateTime.TryParse(Texte(s), CultureInfo.GetCultureInfo("fr-FR"), DateTimeStyles.None, out var p) => p,
            _ => null,
        };
        return d is { Year: >= 1990 and <= 2100 } ? DateOnly.FromDateTime(d.Value) : null;
    }

    public static int? Note(object? valeur)
    {
        int? n = valeur switch
        {
            double x => (int)Math.Round(x),
            string s when int.TryParse(Texte(s), out var i) => i,
            _ => null,
        };
        return n is >= 0 and <= 5 ? n : null;
    }

    public static string? NumInterne(string? valeur) => Texte(valeur)?.ToUpperInvariant();

    /// <summary>Codes de sites : 586, S012, G124, 440-1, A36…</summary>
    [GeneratedRegex(@"^[A-Z]{0,2}\s?\d+[A-Z]?(-\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CodeSiteRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex EspacesMultiples();

    public static bool EstCodeSite(string valeur) => CodeSiteRegex().IsMatch(valeur.Trim());

    /// <summary>Clé de rapprochement des noms : « FAZIO Ludovic » et « Ludovic Fazio » donnent la même clé.</summary>
    public static string ClePersonne(string nom)
    {
        var mots = Regex.Split(Cle(nom), @"[^a-z]+").Where(m => m.Length > 0).Order();
        return string.Join(' ', mots);
    }
}

/// <summary>Résultat du classement de la colonne « SITES / UTILISATEURS ».</summary>
public sealed record Detenteur(TypeDetenteur Type, string? Valeur, string? Remarque)
{
    public static readonly Detenteur Aucun = new(TypeDetenteur.Aucun, null, null);

    /// <summary>Valeurs manifestement saisies dans la mauvaise colonne.</summary>
    private static readonly HashSet<string> ValeursParasites =
        ["OUI", "NON", "OK", "BUSINESS PREMIUM", "BUSINESS STANDARD", "EXCHANGE", "EXCHANGE ONLINE", "E3", "PRO", "FAMILLE"];

    public static Detenteur Classer(string? brut, ISet<string> nomsRegions)
    {
        var v = Normalisation.Texte(brut);
        if (v is null) return Aucun;
        var maj = Normalisation.SansAccents(v).ToUpperInvariant();
        if (maj is "REBUT" or "STOCK" or "SERVICE INFORMATIQUE" || nomsRegions.Contains(Normalisation.Region(v) ?? ""))
            return Aucun;
        if (ValeursParasites.Contains(maj))
            return new(TypeDetenteur.Aucun, null, $"Valeur « {v} » ignorée dans la colonne SITES / UTILISATEURS");
        if (Normalisation.EstCodeSite(v))
            return new(TypeDetenteur.Site, v.Replace(" ", "").ToUpperInvariant(), null);

        // « Rodolph ALBINA / 338 » : on garde la première partie et on conserve la valeur d'origine en remarque
        var slash = v.IndexOf('/');
        if (slash > 0)
            return Classer(v[..slash], nomsRegions) with { Remarque = $"Valeur d'origine « {v} »" };

        // « 619 DIET » : code de site suivi d'un libellé
        var espace = v.IndexOf(' ');
        if (espace > 0 && Normalisation.EstCodeSite(v[..espace]))
            return new(TypeDetenteur.Site, v[..espace].ToUpperInvariant(), $"Valeur d'origine « {v} »");

        return new(TypeDetenteur.Personne, v, null);
    }
}
