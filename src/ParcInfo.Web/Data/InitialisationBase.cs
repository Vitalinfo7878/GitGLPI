using Microsoft.EntityFrameworkCore;

namespace ParcInfo.Web.Data;

public static class InitialisationBase
{
    /// <summary>
    /// PostgreSQL (Azure) : applique les migrations EF Core.
    /// SQLite (poste de développement / démo) : crée la base si elle n'existe pas.
    /// </summary>
    public static async Task AppliquerAsync(IServiceProvider services, string fournisseur)
    {
        var factory = services.GetRequiredService<IDbContextFactory<ParcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        if (fournisseur.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
            await db.Database.MigrateAsync();
        else
            await db.Database.EnsureCreatedAsync();
    }
}
