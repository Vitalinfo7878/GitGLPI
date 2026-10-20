using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Services;

namespace ParcInfo.Web.Data;

public static class InitialisationBase
{
    /// <summary>
    /// Espace (schéma) PostgreSQL de l'application. Sur Azure, le schéma « public » d'une nouvelle base appartient à un compte
    /// système : l'application range donc ses tables dans son propre schéma, qu'elle crée elle-même (le compte de l'application
    /// est propriétaire de sa base).
    /// </summary>
    public const string SchemaPostgres = "parcinfo";

    /// <summary>
    /// PostgreSQL (Azure) : crée le schéma de l'application si besoin, puis applique les migrations EF Core.
    /// SQLite (poste de développement / démo) : crée la base si elle n'existe pas.
    /// </summary>
    public static async Task AppliquerAsync(IServiceProvider services, string fournisseur)
    {
        var factory = services.GetRequiredService<IDbContextFactory<ParcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        if (fournisseur.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA IF NOT EXISTS \"{SchemaPostgres}\"");
            await db.Database.MigrateAsync();
        }
        else
            await db.Database.EnsureCreatedAsync();
        await ChiffrerMotsDePasseAsync(db);
    }

    /// <summary>Chiffre les mots de passe de session encore en clair (import antérieur à la clé de chiffrement).</summary>
    public static async Task<int> ChiffrerMotsDePasseAsync(ParcDbContext db)
    {
        if (!ChiffrementSecrets.Actif) return 0;
        // Lecture de la valeur brute de la colonne : le convertisseur rendrait le texte en clair dans les deux cas
        var ids = await db.Database.SqlQueryRaw<int>(
            $"SELECT \"Id\" AS \"Value\" FROM \"Materiels\" WHERE \"MotDePasseSession\" IS NOT NULL AND \"MotDePasseSession\" NOT LIKE '{ChiffrementSecrets.Prefixe}%'")
            .ToListAsync();
        if (ids.Count == 0) return 0;
        var materiels = await db.Materiels.Where(m => ids.Contains(m.Id)).ToListAsync();
        foreach (var m in materiels) db.Entry(m).Property(x => x.MotDePasseSession).IsModified = true;
        await db.SaveChangesAsync();
        return materiels.Count;
    }
}
