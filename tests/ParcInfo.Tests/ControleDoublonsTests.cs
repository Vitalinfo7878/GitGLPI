using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public sealed class ControleDoublonsTests : IDisposable
{
    private readonly BaseDeTest _fabrique = new();
    private static readonly Utilisateur Informatique = new("Service Informatique", "informatique@vitalrest.com", true, true);

    public void Dispose() => _fabrique.Dispose();

    private static LigneControle L(string num, TypeMateriel type, string? serie, string? detenteur = null, StatutMateriel statut = StatutMateriel.Attribue) =>
        new(0, num, type, null, serie, statut, detenteur, detenteur);

    [Fact]
    public void Doublons_de_numero_de_serie_de_nom_et_de_detenteur()
    {
        var groupes = ControleDoublons.Analyser(
        [
            L("PC0001", TypeMateriel.PcFixe, "ABC-123", "Camille"),
            L("PC0001-DOUBLON-L12", TypeMateriel.PcFixe, "XYZ999"),
            L("PC0002", TypeMateriel.PcFixe, "abc 123", "Site 087"),                 // même série, écrite autrement
            L("PO0001", TypeMateriel.PcPortable, "P1111", "Camille"),               // PC fixe + portable : normal
            L("I0001", TypeMateriel.Imprimante, "IMP1", "Site 087"),
            L("I0002", TypeMateriel.Imprimante, "IMP2", "Site 087"),                // 2 imprimantes sur le même site
            L("I0003", TypeMateriel.Imprimante, "IMP3", "Site 087", StatutMateriel.EnStock), // en stock : ignorée
            L("E0001", TypeMateriel.Ecran, "N/A"), L("E0002", TypeMateriel.Ecran, "n/a"),  // séries factices : ignorées
            L("E0003", TypeMateriel.Ecran, "0000"), L("E0004", TypeMateriel.Ecran, "0000"),
        ]);

        Assert.Equal(["PC0001", "PC0001-DOUBLON-L12"], Assert.Single(groupes, g => g.Type == TypeDoublon.NumInterne).Materiels.Select(m => m.NumInterne));
        Assert.Equal(["PC0001", "PC0002"], Assert.Single(groupes, g => g.Type == TypeDoublon.NumSerie).Materiels.Select(m => m.NumInterne));
        var detenteur = Assert.Single(groupes, g => g.Type == TypeDoublon.MemeDetenteur);
        Assert.Equal(["I0001", "I0002"], detenteur.Materiels.Select(m => m.NumInterne));
        Assert.Equal("Site 087 : 2 × Imprimante", detenteur.Titre);
    }

    [Fact]
    public async Task Pas_un_doublon_tant_que_le_groupe_ne_change_pas()
    {
        int site;
        await using (var db = _fabrique.CreateDbContext())
        {
            var s = new Site { Code = "087" };
            db.Sites.Add(s);
            db.Materiels.AddRange(
                new Materiel { NumInterne = "I0001", Type = TypeMateriel.Imprimante, Statut = StatutMateriel.Attribue, Site = s },
                new Materiel { NumInterne = "I0002", Type = TypeMateriel.Imprimante, Statut = StatutMateriel.Attribue, Site = s });
            await db.SaveChangesAsync();
            site = s.Id;
        }
        var controle = new ControleDoublons(_fabrique);
        var g = Assert.Single(await controle.ListerAsync());
        await controle.IgnorerAsync(g, Informatique);
        Assert.Empty(await controle.ListerAsync());

        // Une troisième imprimante arrive sur le site : le groupe change, il réapparaît
        await using (var db = _fabrique.CreateDbContext())
        {
            db.Materiels.Add(new Materiel { NumInterne = "I0003", Type = TypeMateriel.Imprimante, Statut = StatutMateriel.Attribue, SiteId = site });
            await db.SaveChangesAsync();
        }
        Assert.Equal(3, Assert.Single(await controle.ListerAsync()).Materiels.Count);
    }
}
