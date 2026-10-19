using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ParcInfo.Web.Data;

/// <summary>
/// Utilisée uniquement par « dotnet ef migrations » : les migrations ciblent SQL Server (Azure SQL).
/// En local, la base SQLite est créée directement à partir du modèle.
/// </summary>
public sealed class FabriqueConception : IDesignTimeDbContextFactory<ParcDbContext>
{
    public ParcDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ParcDbContext>()
            .UseSqlServer("Server=localhost;Database=ParcInfo;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}
