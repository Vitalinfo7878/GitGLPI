using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public sealed class ServiceTicketsTests : IDisposable
{
    private readonly BaseDeTest _fabrique = new();
    private readonly ServiceTickets _service;

    private static readonly Utilisateur Salarie = new("Camille TEST", "camille.test@vitalrest.com", false);
    private static readonly Utilisateur Autre = new("Dominique AUTRE", "dominique.autre@vitalrest.com", false);
    private static readonly Utilisateur Informatique = new("Service Informatique", "informatique@vitalrest.com", true, true) { EstAdminTickets = true };

    public ServiceTicketsTests() => _service = new ServiceTickets(_fabrique);

    public void Dispose() => _fabrique.Dispose();

    private static FormulaireTicket Complet() => new()
    {
        DemandeurNom = "Camille TEST",
        DemandeurEmail = "Camille.Test@vitalrest.com",
        DemandeurTelephone = "06 12 34 56 78",
        Lieu = "Site 087",
        Categorie = CategorieTicket.Imprimante,
        Impact = ImpactTicket.Bloquant,
        Portee = PorteeTicket.Site,
        Titre = "L'imprimante n'imprime plus",
        Description = "Bourrage papier permanent",
        MaterielReference = "i0055",
    };

    [Fact]
    public void Formulaire_vide_signale_toutes_les_questions_obligatoires()
    {
        var erreurs = new FormulaireTicket().Valider();
        Assert.Equal(
            new[] { "Categorie", "DemandeurEmail", "DemandeurNom", "DemandeurTelephone", "Description", "Impact", "Lieu", "Portee", "Titre" },
            erreurs.Keys.Order().ToArray());
        Assert.Empty(Complet().Valider());
    }

    [Theory]
    [InlineData(ImpactTicket.Bloquant, PorteeTicket.Personne, PrioriteTicket.Haute)]
    [InlineData(ImpactTicket.Bloquant, PorteeTicket.Site, PrioriteTicket.Critique)]
    [InlineData(ImpactTicket.Genant, PorteeTicket.Personne, PrioriteTicket.Normale)]
    [InlineData(ImpactTicket.Genant, PorteeTicket.Plusieurs, PrioriteTicket.Haute)]
    [InlineData(ImpactTicket.Mineur, PorteeTicket.Site, PrioriteTicket.Basse)]
    public void Priorite_proposee(ImpactTicket impact, PorteeTicket portee, PrioriteTicket attendue) =>
        Assert.Equal(attendue, Ticket.PrioriteProposee(impact, portee));

    [Fact]
    public async Task Cycle_complet_avec_analyse_et_avis()
    {
        await using (var db = _fabrique.CreateDbContext())
        {
            db.Materiels.Add(new Materiel { NumInterne = "I0055", Type = TypeMateriel.Imprimante });
            await db.SaveChangesAsync();
        }

        var t = await _service.CreerAsync(Complet(), Salarie);
        Assert.Equal(1, t.Numero);
        Assert.Equal(PrioriteTicket.Critique, t.Priorite);
        Assert.Equal("I0055", t.MaterielReference);
        Assert.NotNull(t.MaterielId);
        Assert.Equal("camille.test@vitalrest.com", t.DemandeurEmail);

        // Un autre salarié ne voit pas le ticket
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterMessageAsync(t.Id, "Bonjour", false, Autre));
        // Seul le service informatique peut écrire une note interne
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterMessageAsync(t.Id, "Note", true, Salarie));
        await _service.AjouterMessageAsync(t.Id, "Câble réseau à vérifier", true, Informatique);

        // Résolution refusée sans analyse
        var m = new MiseAJourTicket { Statut = StatutTicket.Resolu, Priorite = PrioriteTicket.Haute, Categorie = CategorieTicket.Imprimante, MaterielReference = "I0055" };
        var ex = await Assert.ThrowsAsync<ErreurFormulaire>(() => _service.MettreAJourAsync(t.Id, m, Informatique));
        Assert.Equal(new[] { "Cause", "ModeResolution", "Solution" }, ex.Erreurs.Keys.Order().ToArray());

        m.Cause = CauseTicket.Materiel;
        m.Solution = "Rouleau d'entraînement remplacé";
        m.ModeResolution = ModeResolution.SurSite;
        m.TempsPasseMinutes = 45;
        m.AssigneA = "Service Informatique";
        await _service.MettreAJourAsync(t.Id, m, Informatique);

        // Le demandeur signale que le problème persiste, puis confirme après une nouvelle résolution
        await _service.SignalerNonResoluAsync(t.Id, "Toujours des bourrages", Salarie);
        await _service.MettreAJourAsync(t.Id, m, Informatique);
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.ConfirmerResolutionAsync(t.Id, 5, null, Informatique));
        await _service.ConfirmerResolutionAsync(t.Id, 4, "Rapide", Salarie);

        await using (var db = _fabrique.CreateDbContext())
        {
            var final = await db.Tickets.Include(x => x.Messages).SingleAsync();
            Assert.Equal(StatutTicket.Clos, final.Statut);
            Assert.Equal(1, final.NbReouvertures);
            Assert.Equal(4, final.SatisfactionNote);
            Assert.NotNull(final.PrisEnChargeLe);
            Assert.NotNull(final.ResoluLe);
            Assert.NotNull(final.ClosLe);
            Assert.Single(final.Messages, x => x.Interne);
            Assert.Contains(final.Messages, x => x.Systeme && x.Texte.Contains("persiste"));
        }
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterMessageAsync(t.Id, "Encore ?", false, Salarie));

        var a = await _service.AnalyserAsync(null);
        Assert.Equal(1, a.Crees);
        Assert.Equal(0, a.Ouverts);
        Assert.Equal(1, a.Resolus);
        Assert.Equal(45, a.TempsPasseTotalMinutes);
        Assert.Equal(4, a.SatisfactionMoyenne);
        Assert.Equal(1.0, a.TauxReouverture);
        Assert.Equal(0.0, a.TauxADistance);
        Assert.Equal("Panne matérielle", Assert.Single(a.ParCause).Libelle);
        Assert.Equal(1, a.ParMois[^1].Crees);
        Assert.Equal(1, a.ParMois[^1].Resolus);
    }

    [Fact]
    public async Task Reponse_du_demandeur_relance_le_ticket()
    {
        var t = await _service.CreerAsync(Complet(), Salarie);
        Assert.Null(t.MaterielId); // n° absent de l'inventaire : conservé tel quel
        await _service.MettreAJourAsync(t.Id, new MiseAJourTicket { Statut = StatutTicket.AttenteDemandeur, Priorite = t.Priorite, Categorie = t.Categorie }, Informatique);
        await _service.AjouterMessageAsync(t.Id, "Voici la photo du message d'erreur", false, Salarie);
        await using var db = _fabrique.CreateDbContext();
        Assert.Equal(StatutTicket.EnCours, (await db.Tickets.SingleAsync()).Statut);
    }

    [Fact]
    public async Task Mail_du_demandeur_toujours_celui_du_compte_connecte()
    {
        var f = Complet();
        f.DemandeurEmail = "quelquun.dautre@vitalrest.com";
        var t = await _service.CreerAsync(f, Salarie);
        Assert.Equal("camille.test@vitalrest.com", t.DemandeurEmail);
    }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];

    [Fact]
    public async Task Photos_jointes_au_ticket_et_aux_echanges()
    {
        // Création avec une photo ; un faux fichier (script renommé en .jpg) est refusé
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.CreerAsync(Complet(), Salarie, photos: [new("ecran.jpg", "image/jpeg", "<script>"u8.ToArray())]));
        var t = await _service.CreerAsync(Complet(), Salarie, photos: [new("C:\\Users\\camille\\ecran.jpg", "image/jpeg", Jpeg)]);

        // Réponse du demandeur avec photo seule ; note interne avec photo
        await _service.AjouterMessageAsync(t.Id, null, false, Salarie, photos: [new("imprimante.png", "image/png", Png)]);
        await _service.AjouterMessageAsync(t.Id, "Photo du câblage", true, Informatique, photos: [new("baie.png", "image/png", Png)]);
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterMessageAsync(t.Id, "Trop", false, Salarie,
            photos: Enumerable.Range(0, Photos.MaxParEnvoi + 1).Select(i => new PhotoEnvoyee($"{i}.jpg", "image/jpeg", Jpeg)).ToList()));

        var duDemandeur = await _service.ListerPhotosAsync(t.Id, Salarie);
        Assert.Equal(["ecran.jpg", "imprimante.png"], duDemandeur.Select(p => p.Nom)); // nom sans le chemin du poste ; pas la photo de la note interne
        Assert.Equal(3, (await _service.ListerPhotosAsync(t.Id, Informatique)).Count);

        var interne = (await _service.ListerPhotosAsync(t.Id, Informatique)).Single(p => p.Nom == "baie.png");
        Assert.Null(await _service.LirePhotoAsync(interne.Id, Salarie));
        Assert.Null(await _service.LirePhotoAsync(duDemandeur[0].Id, Autre)); // pas son ticket
        var lue = await _service.LirePhotoAsync(duDemandeur[0].Id, Salarie);
        Assert.Equal(("image/jpeg", Jpeg.Length), (lue!.TypeContenu, lue.Taille));

        await using var db = _fabrique.CreateDbContext();
        Assert.Contains(await db.MessagesTickets.Select(m => m.Texte).ToListAsync(), x => x == "Photo jointe");
    }

    [Theory]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 }, null)] // SVG : refusé
    public void Type_reel_d_apres_le_contenu(byte[] octets, string? attendu) => Assert.Equal(attendu, Photos.TypeReel(octets));
}
