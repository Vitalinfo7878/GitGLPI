using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public sealed class ServiceDemandesTests : IDisposable
{
    private readonly BaseDeTest _fabrique = new();
    private readonly ServiceDemandes _service;
    private readonly OptionsDemandes _options = new()
    {
        Destinataires = ["informatique@vitalrest.com", "thomas.verchere@groupe-vitalrest.com"],
        Valideurs = ["thomas.verchere@groupe-vitalrest.com"],
        PremierNumero = 592,
    };

    private static readonly Utilisateur Salarie = new("Camille TEST", "camille.test@vitalrest.com", false);
    private static readonly Utilisateur Valideur = new("Thomas Verchère", "thomas.verchere@groupe-vitalrest.com", false, true);
    private static readonly Utilisateur Informatique = new("Service Informatique", "informatique@vitalrest.com", true);

    public ServiceDemandesTests()
    {
        var notifications = new ServiceNotifications(new EnvoiCourrielSimule(NullLogger<EnvoiCourrielSimule>.Instance), _fabrique,
            new OptionsMessagerie { UrlApplication = "https://parc.test" }, NullLogger<ServiceNotifications>.Instance);
        _service = new ServiceDemandes(_fabrique, notifications, _options, NullLogger<ServiceDemandes>.Instance);
    }

    public void Dispose() => _fabrique.Dispose();

    private static FormulaireDemande Complet() => new()
    {
        DemandeurNom = "Camille TEST",
        Objets = ["PC portable", "Ecran"],
        Concerne = "Un remplacement",
        Raison = "Écran du PC cassé",
        AdresseLivraison = "Site 440 – 12 rue de la Paix, 75002 Paris",
        DemandeurEmail = "Camille.Test@vitalrest.com",
        DemandeurTelephone = "06 12 34 56 78",
    };

    [Fact]
    public void Formulaire_vide_signale_toutes_les_questions_obligatoires()
    {
        var erreurs = new FormulaireDemande().Valider();
        Assert.Equal(
            new[] { "AdresseLivraison", "Concerne", "DemandeurEmail", "DemandeurNom", "DemandeurTelephone", "Objets", "Raison" },
            erreurs.Keys.Order().ToArray());
    }

    [Fact]
    public void Questions_facultatives_et_controles()
    {
        Assert.Empty(Complet().Valider());

        var f = Complet();
        f.Objets = [FormulaireDemande.Autre];
        f.Concerne = FormulaireDemande.Autre;
        f.AdresseMailACreer = "pas-une-adresse";
        f.DemandeurTelephone = "0612";
        f.Echeance = DateTime.Today.AddDays(-1);
        var e = f.Valider();
        Assert.Contains("ObjetAutre", e.Keys);
        Assert.Contains("ConcerneAutre", e.Keys);
        Assert.Contains("AdresseMailACreer", e.Keys);
        Assert.Contains("DemandeurTelephone", e.Keys);
        Assert.Contains("Echeance", e.Keys);
    }

    [Fact]
    public void Reponses_trop_longues_refusees()
    {
        var f = Complet();
        f.AdresseLivraison = new string('x', 401);
        f.Raison = new string('x', FormulaireDemande.LongueurRaison + 1);
        var e = f.Valider();
        Assert.Contains("AdresseLivraison", e.Keys);
        Assert.Contains("Raison", e.Keys);
    }

    [Fact]
    public void Texte_des_choix_avec_autre()
    {
        var f = Complet();
        f.Objets = ["Ecran", FormulaireDemande.Autre, "PC portable"];
        f.ObjetAutre = "Souris";
        f.Concerne = FormulaireDemande.Autre;
        f.ConcerneAutre = "Nouveau collaborateur";
        Assert.Equal("PC portable ; Ecran ; Autre : Souris", f.ObjetsTexte());
        Assert.Equal("Autre : Nouveau collaborateur", f.ConcerneTexte());
    }

    [Fact]
    public async Task Creation_numerote_et_previent_les_valideurs_et_le_demandeur()
    {
        var r1 = await _service.CreerAsync(Complet(), Salarie);
        var r2 = await _service.CreerAsync(Complet(), Salarie);
        Assert.Equal(592, r1.Demande.Numero);
        Assert.Equal(593, r2.Demande.Numero);
        Assert.Equal(StatutValidation.EnAttente, r1.Demande.StatutValidation);
        Assert.Equal("camille.test@vitalrest.com", r1.Demande.DemandeurEmail);

        await using var db = _fabrique.CreateDbContext();
        var courriels = await db.Courriels.Where(c => c.Sujet.Contains("592")).ToListAsync();
        Assert.Equal(
            new[] { "camille.test@vitalrest.com", "informatique@vitalrest.com", "thomas.verchere@groupe-vitalrest.com" },
            courriels.Select(c => c.Destinataire).Order().ToArray());
        // Seul le valideur reçoit les boutons ; le service informatique est simplement informé
        var avecBoutons = courriels.First(c => c.Destinataire == "thomas.verchere@groupe-vitalrest.com");
        Assert.Contains($"/demandes/{r1.Demande.IdPublic}?choix=valider", avecBoutons.CorpsHtml);
        Assert.Contains($"/demandes/{r1.Demande.IdPublic}?choix=refuser", avecBoutons.CorpsHtml);
        var information = courriels.First(c => c.Destinataire == "informatique@vitalrest.com");
        Assert.DoesNotContain("choix=", information.CorpsHtml);
        Assert.Contains($"/demandes/{r1.Demande.IdPublic}", information.CorpsHtml);
    }

    [Fact]
    public async Task Formulaire_incomplet_refuse()
    {
        var f = Complet();
        f.Raison = " ";
        var ex = await Assert.ThrowsAsync<ErreurFormulaire>(() => _service.CreerAsync(f, Salarie));
        Assert.Contains("Raison", ex.Erreurs.Keys);
    }

    [Fact]
    public async Task Validation_reservee_aux_valideurs_et_unique()
    {
        var d = (await _service.CreerAsync(Complet(), Salarie)).Demande;

        await Assert.ThrowsAsync<ErreurMetier>(() => _service.DeciderAsync(d.IdPublic, true, null, Salarie));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.DeciderAsync(d.IdPublic, true, null, Informatique)); // seul le valideur décide

        var r = await _service.DeciderAsync(d.IdPublic, true, "OK, PC Dell", Valideur);
        Assert.Equal(StatutValidation.Validee, r.Demande.StatutValidation);
        Assert.Equal(StatutRealisation.AFaire, r.Demande.StatutRealisation);
        Assert.Equal("Thomas Verchère", r.Demande.DecisionPar);

        // Le second clic (autre valideur) est refusé et ne change rien
        var ex = await Assert.ThrowsAsync<ErreurMetier>(() => _service.DeciderAsync(d.IdPublic, false, "Non",
            new Utilisateur("Autre valideur", "autre.valideur@vitalrest.com", false, true)));
        Assert.Contains("déjà", ex.Message);

        await using var db = _fabrique.CreateDbContext();
        Assert.Equal(StatutValidation.Validee, (await db.Demandes.SingleAsync()).StatutValidation);
        // Le demandeur est prévenu, ainsi que l'autre boîte (pas celle de la personne qui a décidé)
        var decision = await db.Courriels.Where(c => c.Sujet.Contains("validée")).Select(c => c.Destinataire).ToListAsync();
        Assert.Contains("camille.test@vitalrest.com", decision);
        Assert.Contains("informatique@vitalrest.com", decision);
        Assert.DoesNotContain("thomas.verchere@groupe-vitalrest.com", decision);
    }

    [Fact]
    public async Task Refus_exige_un_motif()
    {
        var d = (await _service.CreerAsync(Complet(), Salarie)).Demande;
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.DeciderAsync(d.IdPublic, false, "", Valideur));
        var r = await _service.DeciderAsync(d.IdPublic, false, "Doublon de la demande 590", Valideur);
        Assert.Equal(StatutValidation.Refusee, r.Demande.StatutValidation);
    }

    [Fact]
    public async Task Traitement_reserve_a_l_informatique_et_previent_a_la_realisation()
    {
        var d = (await _service.CreerAsync(Complet(), Salarie)).Demande;
        await Assert.ThrowsAsync<ErreurMetier>(() =>
            _service.MettreAJourTraitementAsync(d.Id, Priorite.Haute, StatutRealisation.EnCours, null, Informatique)); // pas encore validée
        await _service.DeciderAsync(d.IdPublic, true, null, Valideur);
        await Assert.ThrowsAsync<ErreurMetier>(() =>
            _service.MettreAJourTraitementAsync(d.Id, Priorite.Haute, StatutRealisation.EnCours, null, Valideur));

        var r = await _service.MettreAJourTraitementAsync(d.Id, Priorite.Haute, StatutRealisation.Realisee, "PC0450 envoyé", Informatique);
        Assert.NotNull(r.Demande.RealiseeLe);
        await using var db = _fabrique.CreateDbContext();
        Assert.True(await db.Courriels.AnyAsync(c => c.Sujet.Contains("réalisée") && c.Destinataire == "camille.test@vitalrest.com"));
    }

    [Fact]
    public void Visibilite_d_une_demande()
    {
        var d = new Demande { SaisiePar = "camille.test@vitalrest.com", DemandeurEmail = "camille.test@vitalrest.com" };
        Assert.True(ServiceDemandes.PeutVoir(Salarie, d));
        Assert.True(ServiceDemandes.PeutVoir(Valideur, d));
        Assert.True(ServiceDemandes.PeutVoir(Informatique, d));
        Assert.False(ServiceDemandes.PeutVoir(new Utilisateur("Autre", "autre@vitalrest.com", false), d));
    }

    [Fact]
    public async Task Mail_du_demandeur_toujours_celui_du_compte_connecte()
    {
        var f = Complet();
        f.DemandeurEmail = "quelquun.dautre@vitalrest.com";
        var d = (await _service.CreerAsync(f, Salarie)).Demande;
        Assert.Equal("camille.test@vitalrest.com", d.DemandeurEmail);
    }

    [Fact]
    public async Task Compte_de_site_ne_peut_pas_faire_de_demande() =>
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.CreerAsync(Complet(), new Utilisateur("Site 087", "site087@vitalrest.com", false) { EstSite = true }));

    [Fact]
    public async Task Echanges_internes_entre_valideur_et_informatique()
    {
        var d = (await _service.CreerAsync(Complet(), Salarie)).Demande;

        // Le valideur pose sa question : le service informatique est prévenu par mail
        await _service.AjouterMessageAsync(d.IdPublic, "Validez-vous ?", Valideur);
        // Le service informatique répond : le valideur reçoit la réponse
        await _service.AjouterMessageAsync(d.IdPublic, "Oui, le PC actuel a 7 ans.", Informatique);

        Assert.Equal(["Validez-vous ?", "Oui, le PC actuel a 7 ans."], (await _service.ListerMessagesAsync(d.Id, Valideur)).Select(m => m.Texte));
        Assert.Equal(2, (await _service.ListerMessagesAsync(d.Id, Informatique)).Count);
        // Jamais visibles ni accessibles au demandeur
        Assert.Empty(await _service.ListerMessagesAsync(d.Id, Salarie));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterMessageAsync(d.IdPublic, "Et alors ?", Salarie));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterMessageAsync(d.IdPublic, " ", Valideur));

        await using var db = _fabrique.CreateDbContext();
        var mails = await db.Courriels.Where(c => c.Sujet.Contains("message de")).ToListAsync();
        Assert.Equal("informatique@vitalrest.com", Assert.Single(mails, c => c.Sujet.Contains("Thomas")).Destinataire);
        Assert.Equal("thomas.verchere@groupe-vitalrest.com", Assert.Single(mails, c => c.Sujet.Contains("Service Informatique")).Destinataire);
        Assert.Contains("Validez-vous ?", mails.First(c => c.Sujet.Contains("Thomas")).CorpsHtml);
        Assert.DoesNotContain(mails, c => c.Destinataire == "camille.test@vitalrest.com");
    }
}
