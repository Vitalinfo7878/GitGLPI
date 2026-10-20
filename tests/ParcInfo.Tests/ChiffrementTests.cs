using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public sealed class ChiffrementTests : IDisposable
{
    // Même clé pour tous les tests (le réglage est global à l'application)
    private const string Cle = "q83vEjRWeJq83vEjRWeJq83vEjRWeJq83vEjRWeJq80=";
    private readonly BaseDeTest _fabrique = new();

    public ChiffrementTests() => ChiffrementSecrets.Configurer(Cle);

    public void Dispose() => _fabrique.Dispose();

    private string? ValeurBrute(int id)
    {
        using var db = _fabrique.CreateDbContext();
        return db.Database.SqlQuery<string>($"SELECT \"MotDePasseSession\" AS \"Value\" FROM \"Materiels\" WHERE \"Id\" = {id}").AsEnumerable().Single();
    }

    [Fact]
    public void Aller_retour_et_nonce_aleatoire()
    {
        var a = ChiffrementSecrets.Chiffrer("Vital2026!é");
        var b = ChiffrementSecrets.Chiffrer("Vital2026!é");
        Assert.StartsWith(ChiffrementSecrets.Prefixe, a);
        Assert.NotEqual(a, b);
        Assert.Equal("Vital2026!é", ChiffrementSecrets.Dechiffrer(a));
        Assert.Equal(a, ChiffrementSecrets.Chiffrer(a)); // pas de double chiffrement
        Assert.Equal("ancien", ChiffrementSecrets.Dechiffrer("ancien")); // valeur encore en clair
    }

    [Fact]
    public void Valeur_alteree_conservee_telle_quelle()
    {
        var c = ChiffrementSecrets.Chiffrer("secret")!;
        var alteree = c[..^4] + (c[^4] == 'A' ? "B" : "A") + c[^3..];
        Assert.Equal(alteree, ChiffrementSecrets.Dechiffrer(alteree));
        Assert.Throws<InvalidOperationException>(() => ChiffrementSecrets.Configurer("trop-court"));
    }

    [Fact]
    public async Task Mot_de_passe_chiffre_en_base_et_lisible_dans_l_application()
    {
        int id;
        await using (var db = _fabrique.CreateDbContext())
        {
            var m = new Materiel { NumInterne = "PC0001", Type = TypeMateriel.PcFixe, MotDePasseSession = "Vital2026!" };
            db.Materiels.Add(m);
            await db.SaveChangesAsync();
            id = m.Id;
        }
        var brute = ValeurBrute(id);
        Assert.StartsWith(ChiffrementSecrets.Prefixe, brute);
        Assert.DoesNotContain("Vital2026", brute);
        await using (var db = _fabrique.CreateDbContext())
            Assert.Equal("Vital2026!", (await db.Materiels.SingleAsync()).MotDePasseSession);
    }

    [Fact]
    public async Task Mots_de_passe_en_clair_chiffres_au_demarrage()
    {
        int id;
        await using (var db = _fabrique.CreateDbContext())
        {
            var m = new Materiel { NumInterne = "PC0002", Type = TypeMateriel.PcFixe };
            db.Materiels.Add(m);
            await db.SaveChangesAsync();
            id = m.Id;
            // Valeur en clair écrite directement, comme avant la mise en place du chiffrement
            await db.Database.ExecuteSqlAsync($"UPDATE \"Materiels\" SET \"MotDePasseSession\" = 'Ancien1' WHERE \"Id\" = {id}");
        }
        Assert.Equal("Ancien1", ValeurBrute(id));

        await using (var db = _fabrique.CreateDbContext())
        {
            Assert.Equal(1, await InitialisationBase.ChiffrerMotsDePasseAsync(db));
            Assert.Equal(0, await InitialisationBase.ChiffrerMotsDePasseAsync(db));
        }
        Assert.StartsWith(ChiffrementSecrets.Prefixe, ValeurBrute(id));
        await using (var db = _fabrique.CreateDbContext())
            Assert.Equal("Ancien1", (await db.Materiels.SingleAsync()).MotDePasseSession);
    }
}
