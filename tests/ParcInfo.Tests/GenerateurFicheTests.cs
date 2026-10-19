using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public class GenerateurFicheTests
{
    private static Fiche FicheExemple(TypeFiche type, StatutReception reception)
    {
        var pc = new Materiel { NumInterne = "PC0104", Type = TypeMateriel.PcFixe, Modele = "Dell OptiPlex 3080", NumSerie = "ABC123", AnneeAcquisition = 2025 };
        var ecran = new Materiel { NumInterne = "E0114", Type = TypeMateriel.Ecran, Modele = "LG" };
        var personne = new Personne { NomComplet = "Camille TEST", Email = "camille.test@vitalrest.com" };
        return new Fiche
        {
            Type = type, Annee = 2026, Numero = 2057, Code = RegleMouvement.CodeFiche(type, 2026, 2057),
            DestinataireNom = personne.NomComplet, DestinataireEmail = personne.Email, StatutReception = reception,
            ReceptionConfirmeeLe = reception == StatutReception.Confirmee ? DateTime.UtcNow : null,
            ReceptionConfirmeePar = reception == StatutReception.Confirmee ? "Camille TEST" : null,
            ReceptionConfirmeeParEmail = reception == StatutReception.Confirmee ? personne.Email : null,
            Mouvements =
            [
                new Mouvement { Numero = 2057, Materiel = pc, Type = TypeMouvement.Attribution, Date = new DateOnly(2026, 10, 19), Expediteur = "SERVICE INFORMATIQUE", DestinationPersonne = personne, Region = "DR IDF" },
                new Mouvement { Numero = 2058, Materiel = ecran, Type = TypeMouvement.Attribution, Date = new DateOnly(2026, 10, 19), Expediteur = "SERVICE INFORMATIQUE", DestinationPersonne = personne, Region = "DR IDF" },
            ],
        };
    }

    [Theory]
    [InlineData(TypeFiche.Attribution, StatutReception.EnAttente)]
    [InlineData(TypeFiche.Attribution, StatutReception.Confirmee)]
    [InlineData(TypeFiche.Restitution, StatutReception.NonDemandee)]
    [InlineData(TypeFiche.Achat, StatutReception.NonDemandee)]
    [InlineData(TypeFiche.MiseAuRebut, StatutReception.NonDemandee)]
    public void Genere_un_pdf(TypeFiche type, StatutReception reception)
    {
        var pdf = new GenerateurFiche().Generer(FicheExemple(type, reception));
        Assert.True(pdf.Length > 5000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        var dossier = Environment.GetEnvironmentVariable("PARC_PDF_SORTIE");
        if (!string.IsNullOrEmpty(dossier)) File.WriteAllBytes(Path.Combine(dossier, $"{type}-{reception}.pdf"), pdf);
    }

    [Fact]
    public void Code_de_fiche_suit_la_nomenclature()
    {
        Assert.Equal("FIA2026 0042", RegleMouvement.CodeFiche(TypeFiche.Attribution, 2026, 42));
        Assert.Equal("FIRE2026 1914", RegleMouvement.CodeFiche(TypeFiche.MiseAuRebut, 2026, 1914));
    }
}
