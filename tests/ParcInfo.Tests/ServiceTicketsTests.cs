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
    public async Task Administrateurs_des_tickets_configures()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tickets:Administrateurs:0"] = "thomas.verchere@groupe-vitalrest.com" })
            .Build();
        var p = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "thomas.verchere@groupe-vitalrest.com")], "test"));
        var u = Utilisateur.Depuis(await new AjoutRolesConfigures(config).TransformAsync(p));
        Assert.True(u.EstAdminTickets);
        Assert.False(u.EstGestionnaire);
        Assert.False(Utilisateur.Depuis(new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "x@vitalrest.com")], "test"))).EstAdminTickets);
    }
}
