using Microsoft.EntityFrameworkCore;

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
    }
}
