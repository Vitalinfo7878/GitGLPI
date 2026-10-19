using ClosedXML.Excel;

namespace ParcInfo.Web.Import;

public sealed record LigneInventaire(
    int Ligne,
    string? Detenteur, string? ServiceLieu, string? NumInterne, string? Appareil, string? Modele, string? NumSerie,
    string? Configuration, object? Annee, object? DateMiseEnPlace, string? ProfilMicrosoft, string? Licence,
    string? OfficeEnDur, object? Note, string? MotDePasse, string? ConnexionAdmin, string? VersionWindows,
    string? AChanger, string? EditionWindows, string? Labtech, string? Commentaire, string? CleOffice,
    string? NumImmo, string? NumFacture, string? Telephone, string? ResponsableSite);

public sealed record LigneMouvement(
    int Ligne,
    object? Numero, string? NumInterne, string? Appareil, string? Modele, string? NumSerie, string? Type,
    object? Date, string? Expediteur, string? DestinataireEmail, string? Destination, string? DocumentReference,
    string? Region, object? DateReception);

/// <summary>
/// Lit les feuilles INVENTAIRE et MOUVEMENT du classeur « Suivi du stock ».
/// Les cellules calculées (RECHERCHE / INDEX…) sont lues via leur dernière valeur calculée par Excel.
/// </summary>
public static class LecteurExcel
{
    public const string FeuilleInventaire = "INVENTAIRE";
    public const string FeuilleMouvement = "MOUVEMENT";

    public static (List<LigneInventaire> Inventaire, List<LigneMouvement> Mouvements) Lire(Stream fichier)
    {
        using var wb = new XLWorkbook(fichier);
        if (!wb.TryGetWorksheet(FeuilleInventaire, out var inv))
            throw new InvalidOperationException($"La feuille « {FeuilleInventaire} » est introuvable dans le fichier.");
        if (!wb.TryGetWorksheet(FeuilleMouvement, out var mvt))
            throw new InvalidOperationException($"La feuille « {FeuilleMouvement} » est introuvable dans le fichier.");
        return (LireInventaire(inv), LireMouvements(mvt));
    }

    private static List<LigneInventaire> LireInventaire(IXLWorksheet ws)
    {
        var lignes = new List<LigneInventaire>();
        var derniere = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= derniere; r++)
        {
            var row = ws.Row(r);
            string? T(int c) => Texte(row.Cell(c));
            object? V(int c) => Valeur(row.Cell(c));
            var l = new LigneInventaire(r,
                T(1), T(2), T(3), T(4), T(5), T(6), T(7), V(8), V(9), T(10), T(11), T(12), V(13), T(14), T(15),
                T(16), T(17), T(18), T(19), T(20), T(21), T(22), T(23), T(25), T(26));
            // Une ligne sans numéro, sans appareil, sans modèle et sans n° de série est une ligne vide
            if (Normalisation.Texte(l.NumInterne) is null && Normalisation.Texte(l.Appareil) is null
                && Normalisation.Texte(l.Modele) is null && Normalisation.Texte(l.NumSerie) is null)
                continue;
            lignes.Add(l);
        }
        return lignes;
    }

    private static List<LigneMouvement> LireMouvements(IXLWorksheet ws)
    {
        // L'en-tête « Numéro mouvement » est en ligne 7 dans le classeur actuel ; on la recherche pour être tolérant.
        var entete = ws.Column(1).CellsUsed().FirstOrDefault(c => Texte(c)?.StartsWith("Numéro mouvement", StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new InvalidOperationException("En-tête « Numéro mouvement » introuvable dans la feuille MOUVEMENT.");
        var lignes = new List<LigneMouvement>();
        var derniere = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = entete.Address.RowNumber + 1; r <= derniere; r++)
        {
            var row = ws.Row(r);
            string? T(int c) => Texte(row.Cell(c));
            object? V(int c) => Valeur(row.Cell(c));
            if (Normalisation.Texte(T(2)) is null) continue;
            lignes.Add(new LigneMouvement(r, V(1), T(2), T(3), T(4), T(5), T(7), V(8), T(9), T(10), T(11), T(12), T(13), V(14)));
        }
        return lignes;
    }

    private static object? Valeur(IXLCell cell)
    {
        XLCellValue v;
        try { v = cell.HasFormula ? cell.CachedValue : cell.Value; }
        catch { return null; }
        return v.Type switch
        {
            XLDataType.Blank => null,
            XLDataType.Boolean => v.GetBoolean(),
            XLDataType.Number => v.GetNumber(),
            XLDataType.Text => v.GetText(),
            XLDataType.DateTime => v.GetDateTime(),
            XLDataType.TimeSpan => v.GetTimeSpan().ToString(),
            XLDataType.Error => null, // #NUM!, #N/A… : traité comme vide
            _ => null,
        };
    }

    private static string? Texte(IXLCell cell) => Valeur(cell) switch
    {
        null => null,
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd"),
        var o => o.ToString(),
    };
}
