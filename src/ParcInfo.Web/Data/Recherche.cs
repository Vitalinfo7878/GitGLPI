namespace ParcInfo.Web.Data;

public static class Recherche
{
    /// <summary>
    /// Motif pour EF.Functions.Like (insensible à la casse sous SQLite comme sous SQL Server),
    /// contrairement à string.Contains qui est sensible à la casse sous SQLite.
    /// </summary>
    public static string Motif(string texte) => "%" + texte.Trim().Replace("%", "") + "%";
}
