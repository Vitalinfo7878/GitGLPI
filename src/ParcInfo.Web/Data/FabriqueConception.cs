using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ParcInfo.Web.Data;

/// <summary>
/// Utilisée uniquement par « dotnet ef migrations » : les migrations ciblent PostgreSQL (Azure Database for PostgreSQL).
/// En local, la base SQLite est créée directement à partir du modèle.
/// </summary>
public sealed class FabriqueConception : IDesignTimeDbContextFactory<ParcDbContext>
{
    public ParcDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ParcDbContext>()
            .UseNpgsql("Host=localhost;Database=parcinfo;Username=parcinfo")
            .Options);
}
