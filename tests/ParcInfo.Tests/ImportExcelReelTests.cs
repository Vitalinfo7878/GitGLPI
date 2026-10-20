using ParcInfo.Web.Domain;
using ParcInfo.Web.Import;
using Xunit.Abstractions;

namespace ParcInfo.Tests;

/// <summary>
/// Test sur le vrai classeur, exécuté seulement si la variable d'environnement PARC_EXCEL pointe vers le fichier
/// (le fichier contient des données personnelles et n'est pas versionné).
/// </summary>
public class ImportExcelReelTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Import_du_classeur_reel()
    {
        var chemin = Environment.GetEnvironmentVariable("PARC_EXCEL");
        if (string.IsNullOrEmpty(chemin) || !File.Exists(chemin)) return;

        var chrono = System.Diagnostics.Stopwatch.StartNew();
        using var f = File.OpenRead(chemin);
        var (inv, mvts) = LecteurExcel.Lire(f);
        output.WriteLine($"Lecture : {chrono.ElapsedMilliseconds} ms, {inv.Count} lignes inventaire, {mvts.Count} mouvements");

        var (rapport, materiels) = new ImportateurParc().Construire(inv, mvts, "test");
        output.WriteLine($"Matériels {rapport.Materiels}, sites {rapport.Sites}, personnes {rapport.Personnes}, régions {rapport.Regions}, mouvements {rapport.Mouvements}, anomalies {rapport.Anomalies.Count}");
        foreach (var g in materiels.GroupBy(m => (m.Type, m.Statut)).OrderBy(g => g.Key))
            output.WriteLine($"  {g.Key.Type} / {g.Key.Statut} : {g.Count()}");
        output.WriteLine("Parc sites : " + materiels.Count(m => m.Site != null) + ", parc personnes : " + materiels.Count(m => m.Personne != null));
        output.WriteLine("Régions : " + string.Join(", ", materiels.Select(m => m.Region?.Nom).Distinct().Order()));
        foreach (var a in rapport.Anomalies.GroupBy(a => a.Message.Split(':')[0].Length > 60 ? "divers" : a.Message.Split(':')[0]).OrderByDescending(g => g.Count()).Take(15))
            output.WriteLine($"  [{a.Count()}] {a.First().Feuille} L{a.First().Ligne} {a.First().Message}");
        var personnes = materiels.Where(m => m.Personne != null).Select(m => m.Personne!.NomComplet).Distinct().Order().Take(400);
        output.WriteLine("Personnes : " + string.Join(" | ", personnes));

        Assert.True(rapport.Materiels > 1000);

        // Enregistrement réel (SQLite, ou PostgreSQL si PARC_TEST_POSTGRES est défini) : vérifie notamment les longueurs de colonnes
        using var baseTest = new BaseDeTest();
        using var db = baseTest.CreateDbContext();
        var enregistre = await new ImportateurParc().ImporterAsync(db, inv, mvts, "test");
        Assert.True(enregistre.Enregistre);
        Assert.Equal(rapport.Materiels, db.Materiels.Count());
        Assert.Equal(rapport.Mouvements, db.Mouvements.Count());
        Assert.Contains(materiels, m => m.Statut == StatutMateriel.Rebute);
        Assert.All(materiels, m => Assert.False(m.Site != null && m.Personne != null));
    }
}
