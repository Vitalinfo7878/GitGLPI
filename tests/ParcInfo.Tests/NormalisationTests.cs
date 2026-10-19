using ParcInfo.Web.Domain;
using ParcInfo.Web.Import;

namespace ParcInfo.Tests;

public class NormalisationTests
{
    [Theory]
    [InlineData("Pc Portable", null, TypeMateriel.PcPortable)]
    [InlineData("PC portable", null, TypeMateriel.PcPortable)]
    [InlineData("Pc Fixe", null, TypeMateriel.PcFixe)]
    [InlineData("ecran", null, TypeMateriel.Ecran)]
    [InlineData("Écran", null, TypeMateriel.Ecran)]
    [InlineData("Imprimante", null, TypeMateriel.Imprimante)]
    [InlineData(null, "PO0080", TypeMateriel.PcPortable)]
    [InlineData(null, "LPW110411221452", TypeMateriel.PcPortable)]
    [InlineData(null, "DTW101", TypeMateriel.PcFixe)]
    [InlineData(null, "E0088", TypeMateriel.Ecran)]
    [InlineData(null, "I0074", TypeMateriel.Imprimante)]
    public void Type_de_materiel(string? appareil, string? num, TypeMateriel attendu) =>
        Assert.Equal(attendu, Normalisation.TypeMateriel(appareil, num));

    [Theory]
    [InlineData("W11", "Windows 11")]
    [InlineData("11", "Windows 11")]
    [InlineData("w10", "Windows 10")]
    [InlineData("WXP", "Windows XP")]
    [InlineData("W8.1", "Windows 8.1")]
    [InlineData("?", null)]
    public void Version_windows(string brut, string? attendu) => Assert.Equal(attendu, Normalisation.VersionWindows(brut));

    [Theory]
    [InlineData(" DR  CENTRE-OUEST", "DR CENTRE-OUEST")]
    [InlineData("DR-SUD OUEST", "DR SUD-OUEST")]
    [InlineData("Site IDF", "SITE IDF")]
    [InlineData("DR NORD EST", "DR NE")]
    [InlineData("DR NE (Home Office)", "DR NE")]
    [InlineData("0", null)]
    public void Region(string brut, string? attendu) => Assert.Equal(attendu, Normalisation.Region(brut));

    [Theory]
    [InlineData("STOCK", StatutMateriel.EnStock)]
    [InlineData("REBUT", StatutMateriel.Rebute)]
    [InlineData("VOLER", StatutMateriel.VolePerdu)]
    [InlineData("REBUT REPARATION CANON", StatutMateriel.EnReparation)]
    [InlineData("RETOUR AMAZON", StatutMateriel.RenvoyeFournisseur)]
    public void Statut_depuis_le_lieu(string lieu, StatutMateriel attendu) => Assert.Equal(attendu, Normalisation.StatutDepuisLieu(lieu));

    [Theory]
    [InlineData("586", TypeDetenteur.Site, "586")]
    [InlineData("S012", TypeDetenteur.Site, "S012")]
    [InlineData("440-1", TypeDetenteur.Site, "440-1")]
    [InlineData("g124", TypeDetenteur.Site, "G124")]
    [InlineData("619 DIET", TypeDetenteur.Site, "619")]
    [InlineData("Alexandre Benard", TypeDetenteur.Personne, "Alexandre Benard")]
    [InlineData("Rodolph ALBINA / 338", TypeDetenteur.Personne, "Rodolph ALBINA")]
    [InlineData("oui", TypeDetenteur.Aucun, null)]
    [InlineData("REBUT", TypeDetenteur.Aucun, null)]
    [InlineData("DR NORD-EST", TypeDetenteur.Aucun, null)]
    [InlineData("?", TypeDetenteur.Aucun, null)]
    public void Detenteur(string brut, TypeDetenteur type, string? valeur)
    {
        var d = ParcInfo.Web.Import.Detenteur.Classer(brut, new HashSet<string> { "DR NE", "SITE IDF" });
        Assert.Equal(type, d.Type);
        Assert.Equal(valeur, d.Valeur);
    }

    [Fact]
    public void Meme_personne_quel_que_soit_l_ordre_et_la_casse()
    {
        Assert.Equal(Normalisation.ClePersonne("FAZIO Ludovic"), Normalisation.ClePersonne("Ludovic Fazio"));
        Assert.Equal(Normalisation.ClePersonne("Aurélie SAEZ"), Normalisation.ClePersonne("Aurelie Saez"));
    }

    [Theory]
    [InlineData("SORTIE INITIALE", TypeMouvement.SortieInitiale)]
    [InlineData("SORTIE", TypeMouvement.Attribution)]
    [InlineData("SORTIE (remboursement)", TypeMouvement.Attribution)]
    [InlineData("ENTREE", TypeMouvement.Restitution)]
    [InlineData("ENTRE", TypeMouvement.Restitution)]
    [InlineData("ENTREE (SI)", TypeMouvement.EntreeInitiale)]
    [InlineData("ACHAT", TypeMouvement.Achat)]
    [InlineData("MISE AU REBUT", TypeMouvement.MiseAuRebut)]
    public void Type_de_mouvement_excel(string brut, TypeMouvement attendu) => Assert.Equal(attendu, ImportateurParc.TypeDepuisExcel(brut));
}
