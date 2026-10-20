namespace ParcInfo.Web.Data;

public static class Recherche
{
    /// <summary>
    /// Texte recherché, à comparer avec <c>colonne.ToLower().Contains(...)</c> :
    /// recherche insensible à la casse qui se traduit de la même façon sous PostgreSQL et SQLite.
    /// </summary>
    public static string Texte(string texte) => texte.Trim().ToLowerInvariant();
}
