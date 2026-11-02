using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public sealed class EtiquettesUpsTests
{
    private static string Texte(Demande d, IEnumerable<Materiel> attribues, DateOnly jour) => EtiquettesUps.Texte(d, attribues, jour).Replace("\r\n", "\n");

    [Fact]
    public void Remplacement_avec_destinataire_de_livraison_un_mardi()
    {
        var d = new Demande
        {
            DemandeurNom = "Camille TEST", Objets = "PC portable;Ecran", DestinataireLivraison = "Marie DURAND (cheffe gérante)",
            AdresseLivraison = "Site 440 – Restaurant Les Tilleuls, 12 rue de la Paix, 75002 Paris",
        };
        Assert.Equal("""
            Bonjour Laetitia,

            Peux tu stp préparer 2 étiquettes UPS pour demain 14h :

            Pc portable -5kg
            Ecran -10kg

            Marie DURAND (cheffe gérante)
            Site 440 – Restaurant Les Tilleuls, 12 rue de la Paix, 75002 Paris

            Merci

            """, Texte(d, [], new DateOnly(2026, 11, 3)));
    }

    [Fact]
    public void Ouverture_de_site_avec_le_materiel_rattache_un_vendredi()
    {
        var d = new Demande { DemandeurNom = "Thomas VERCHÈRE", Objets = "PC fixe (site);Ecran;Imprimante", AdresseLivraison = "Site 087 – EHPAD Les Lilas, 4 avenue des Roses, 69003 Lyon" };
        Materiel M(TypeMateriel t) => new() { Type = t };
        Assert.Equal("""
            Bonjour Laetitia,

            Peux tu stp préparer 5 étiquettes UPS pour lundi 14h :

            Pc Fixe -5kg
            Pc Fixe -5kg
            Ecran -10kg
            Ecran -10kg
            Imprimante -10kg

            Thomas VERCHÈRE
            Site 087 – EHPAD Les Lilas, 4 avenue des Roses, 69003 Lyon

            Merci

            """, Texte(d, [M(TypeMateriel.Imprimante), M(TypeMateriel.Ecran), M(TypeMateriel.PcFixe), M(TypeMateriel.Ecran), M(TypeMateriel.PcFixe)], new DateOnly(2026, 11, 6)));
    }

    [Fact]
    public void Un_seul_colis_sans_les_demandes_non_materielles_la_veille_d_un_jour_ferie()
    {
        var d = new Demande { DemandeurNom = "Dominique MARTIN", Objets = "PC portable;Adresse email", AdresseLivraison = "Siège – 10 rue du Commerce, 92100 Boulogne-Billancourt" };
        Assert.Equal("""
            Bonjour Laetitia,

            Peux tu stp préparer 1 étiquette UPS pour jeudi 14h :

            Pc portable -5kg

            Dominique MARTIN
            Siège – 10 rue du Commerce, 92100 Boulogne-Billancourt

            Merci

            """, Texte(d, [], new DateOnly(2026, 11, 10))); // 11 novembre férié
    }
}
