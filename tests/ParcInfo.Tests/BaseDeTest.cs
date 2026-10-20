using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ParcInfo.Web.Data;

namespace ParcInfo.Tests;

/// <summary>
/// Base de données jetable pour un test.
/// Par défaut SQLite en mémoire ; si la variable PARC_TEST_POSTGRES contient une chaîne de connexion à un serveur
/// PostgreSQL, une base temporaire y est créée avec les migrations (comme en production), puis supprimée.
/// </summary>
public sealed class BaseDeTest : IDbContextFactory<ParcDbContext>, IDisposable
{
    private readonly DbContextOptions<ParcDbContext> _options;
    private readonly SqliteConnection? _sqlite;
    private readonly string? _serveurPostgres;
    private readonly string? _nomBase;

    public BaseDeTest()
    {
        _serveurPostgres = Environment.GetEnvironmentVariable("PARC_TEST_POSTGRES");
        if (string.IsNullOrEmpty(_serveurPostgres))
        {
            _sqlite = new SqliteConnection("Data Source=:memory:");
            _sqlite.Open();
            _options = new DbContextOptionsBuilder<ParcDbContext>().UseSqlite(_sqlite).Options;
            using var db = CreateDbContext();
            db.Database.EnsureCreated();
        }
        else
        {
            _nomBase = $"parc_test_{Guid.NewGuid():N}";
            var cs = new NpgsqlConnectionStringBuilder(_serveurPostgres) { Database = _nomBase }.ConnectionString;
            _options = new DbContextOptionsBuilder<ParcDbContext>().UseNpgsql(cs).Options;
            using var db = CreateDbContext();
            db.Database.Migrate();
        }
    }

    public ParcDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        _sqlite?.Dispose();
        if (_nomBase is null) return;
        NpgsqlConnection.ClearAllPools();
        using var cnx = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_serveurPostgres) { Database = "postgres" }.ConnectionString);
        cnx.Open();
        using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_nomBase}\" WITH (FORCE)", cnx);
        cmd.ExecuteNonQuery();
    }
}
