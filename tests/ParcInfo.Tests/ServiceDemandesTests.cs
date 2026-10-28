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

        var r = await _service.DeciderAsync(d.IdPublic, true, null, Valideur);
        Assert.Equal(StatutValidation.Validee, r.Demande.StatutValidation);
        Assert.Equal(StatutRealisation.AFaire, r.Demande.StatutRealisation);
        Assert.Equal("Thomas Verchère", r.Demande.DecisionPar);

        // Le second clic (autre valideur) est refusé et ne change rien
        var ex = await Assert.ThrowsAsync<ErreurMetier>(() => _service.DeciderAsync(d.IdPublic, false, null,
            new Utilisateur("Autre valideur", "autre.valideur@vitalrest.com", false, true)));
        Assert.Contains("déjà", ex.Message);

        await using var db = _fabrique.CreateDbContext();
        Assert.Equal(StatutValidation.Validee, (await db.Demandes.SingleAsync()).StatutValidation);
        // Seul le service informatique est prévenu : il informe lui-même le demandeur
        var decision = await db.Courriels.Where(c => c.Sujet.Contains("validée")).Select(c => c.Destinataire).ToListAsync();
        Assert.Equal(["informatique@vitalrest.com"], decision);
    }

    [Fact]
    public async Task Refus_avec_commentaire_interne_sans_mail_au_demandeur()
    {
        var d = (await _service.CreerAsync(Complet(), Salarie)).Demande;
        var r = await _service.DeciderAsync(d.IdPublic, false, "Budget dépassé, voir en janvier", Valideur);
        Assert.Equal(StatutValidation.Refusee, r.Demande.StatutValidation);
        Assert.Equal("Budget dépassé, voir en janvier", r.Demande.CommentaireDecision);
        await using var db = _fabrique.CreateDbContext();
        Assert.False(await db.Courriels.AnyAsync(c => c.Destinataire == "camille.test@vitalrest.com" && c.Sujet.Contains("refusée")));
        Assert.Contains("Budget dépassé", System.Net.WebUtility.HtmlDecode((await db.Courriels.SingleAsync(c => c.Sujet.Contains("refusée"))).CorpsHtml)); // mail au service informatique
    }

    [Fact]
    public async Task Le_valideur_commente_puis_doit_decider_une_fois_qu_on_lui_a_repondu()
    {
        var d = (await _service.CreerAsync(Complet(), Salarie)).Demande;
        await _service.AjouterMessageAsync(d.IdPublic, "Validez-vous ?", Valideur);
        await _service.AjouterMessageAsync(d.IdPublic, "Je précise : pour le site 440", Valideur); // toujours possible sans réponse
        await _service.AjouterMessageAsync(d.IdPublic, "Oui", Informatique);

        var messages = await _service.ListerMessagesAsync(d.Id, Valideur);
        Assert.True(ServiceDemandes.ValideurDoitDecider(Valideur, d, messages));
        var ex = await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterMessageAsync(d.IdPublic, "Vraiment ?", Valideur));
        Assert.Contains("validez ou refusez", ex.Message);
        await _service.AjouterMessageAsync(d.IdPublic, "Complément", Informatique); // l'informatique peut toujours écrire

        await _service.DeciderAsync(d.IdPublic, true, "OK suite à votre réponse", Valideur);
        var validee = await _service.ListerMessagesAsync(d.Id, Valideur);
        Assert.False(ServiceDemandes.ValideurDoitDecider(Valideur, (await _service.CreerAsync(Complet(), Salarie)).Demande, []));
        Assert.Equal(4, validee.Count);
    }

    [Fact]
    public async Task Relance_des_demandes_en_attente_depuis_une_semaine()
    {
        var ancienne = (await _service.CreerAsync(Complet(), Salarie)).Demande;
        var recente = (await _service.CreerAsync(Complet(), Salarie)).Demande;
        var decidee = (await _service.CreerAsync(Complet(), Salarie)).Demande;
        await _service.DeciderAsync(decidee.IdPublic, true, null, Valideur);
        await using (var db = _fabrique.CreateDbContext())
        {
            await db.Demandes.Where(x => x.Id != recente.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.CreeLe, DateTime.UtcNow.AddDays(-8)));
        }

        var maintenant = DateTime.UtcNow;
        Assert.Equal(1, await _service.RelancerAsync(maintenant));          // seule la demande en attente depuis plus de 7 jours
        Assert.Equal(0, await _service.RelancerAsync(maintenant.AddDays(1))); // pas de nouvelle relance le lendemain
        Assert.Equal(2, await _service.RelancerAsync(maintenant.AddDays(7))); // une semaine plus tard : la première à nouveau, la récente à son tour

        await using var db2 = _fabrique.CreateDbContext();
        var relances = await db2.Courriels.Where(c => c.Sujet.StartsWith("Relance")).ToListAsync();
        Assert.Equal(2, relances.Count);
        Assert.All(relances, c => Assert.Equal("thomas.verchere@groupe-vitalrest.com", c.Destinataire));
        Assert.Contains($"/demandes/{ancienne.IdPublic}?choix=valider", relances[0].CorpsHtml);
        Assert.Contains($"n° {ancienne.Numero}", relances[0].Sujet);
        Assert.Contains("2 demandes", relances[1].Sujet);
    }

    [Fact]
    public async Task Envoi_du_materiel_previent_le_demandeur()
    {
        var d = (await _service.CreerAsync(Complet(), Salarie)).Demande;
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.NotifierEnvoiAsync(d.Id, Informatique)); // pas encore validée
        await _service.DeciderAsync(d.IdPublic, true, null, Valideur);
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.NotifierEnvoiAsync(d.Id, Valideur));      // réservé à l'informatique

        var r = await _service.NotifierEnvoiAsync(d.Id, Informatique);
        Assert.Equal(StatutRealisation.EnCoursEnvoi, r.Demande.StatutRealisation);
        Assert.NotNull(r.Demande.EnvoiNotifieLe);
        await using var db = _fabrique.CreateDbContext();
        var mail = await db.Courriels.SingleAsync(c => c.Sujet.Contains("envoyé"));
        Assert.Equal("camille.test@vitalrest.com", mail.Destinataire);
        Assert.Contains("2 à 3 jours ouvrés", mail.CorpsHtml);
        Assert.Contains("Merci de nous renvoyer l'ancien matériel", System.Net.WebUtility.HtmlDecode(mail.CorpsHtml)); // remplacement
    }

    [Theory]
    [InlineData("2026-10-30", 2, "2026-11-03")] // vendredi + 2 : lundi 2, mardi 3 (week-end)
    [InlineData("2026-11-10", 2, "2026-11-13")] // mardi + 2 : le 11 novembre est férié
    [InlineData("2026-05-13", 1, "2026-05-15")] // mercredi + 1 : jeudi 14 = Ascension
    public void Jours_ouvres(string depart, int n, string attendu) =>
        Assert.Equal(DateOnly.Parse(attendu), JoursOuvres.Ajouter(DateOnly.Parse(depart), n));

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

    [Fact]
    public async Task Remplacement_cree_un_ticket_interne_de_rapatriement_suivi_jusqu_au_bout()
    {
        int pc;
        await using (var db = _fabrique.CreateDbContext())
        {
            var p = new Personne { NomComplet = "Camille TEST", Email = "camille.test@vitalrest.com" };
            var m = new Materiel { NumInterne = "PC0450", Type = TypeMateriel.PcPortable, Modele = "Dell", Statut = StatutMateriel.Attribue, Personne = p };
            db.Materiels.AddRange(m,
                new Materiel { NumInterne = "E0031", Type = TypeMateriel.Ecran, Statut = StatutMateriel.Attribue, Personne = p },
                new Materiel { NumInterne = "PC0001", Type = TypeMateriel.PcFixe, Statut = StatutMateriel.Attribue, Personne = p }, // type non demandé
                new Materiel { NumInterne = "PO0002", Type = TypeMateriel.PcPortable, Statut = StatutMateriel.EnStock });       // pas à elle
            await db.SaveChangesAsync();
            pc = m.Id;
        }
        // Demande d'un PC portable et d'un écran en remplacement : l'ancien matériel est retrouvé dans l'inventaire
        var d = (await _service.CreerAsync(Complet(), Salarie)).Demande;
        Assert.Equal("E0031 – Écran ; PC0450 – PC portable Dell", d.MaterielRemplace);

        Ticket t;
        await using (var db = _fabrique.CreateDbContext())
        {
            Assert.Equal(["E0031", "PC0450"], await db.Tickets.OrderBy(x => x.MaterielReference).Select(x => x.MaterielReference!).ToListAsync());
            t = await db.Tickets.SingleAsync(x => x.MaterielId == pc);
        }
        Assert.True(t.Interne);
        Assert.Equal(d.Id, t.DemandeId);
        Assert.Equal(pc, t.MaterielId);
        Assert.Equal(CategorieTicket.Poste, t.Categorie);
        Assert.Contains("PC0450", t.Titre);
        Assert.False(ServiceTickets.PeutVoir(Salarie, t));            // interne : pas visible du demandeur
        Assert.True(ServiceTickets.PeutVoir(Valideur with { EstAdminTickets = true }, t));

        // Validation, puis retour de l'ancien PC et mise au rebut : tout est noté sur le ticket
        await _service.DeciderAsync(d.IdPublic, true, null, Valideur);
        var mouvements = new ServiceMouvements(_fabrique, new GenerateurFiche(), NullLogger<ServiceMouvements>.Instance);
        await mouvements.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Restitution, MaterielIds = [pc] }, Informatique);
        await mouvements.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.MiseAuRebut, MaterielIds = [pc], Commentaire = "Carte mère HS" }, Informatique);

        await using (var db = _fabrique.CreateDbContext())
        {
            t = await db.Tickets.Include(x => x.Messages).SingleAsync(x => x.MaterielId == pc);
            var traces = t.Messages.Where(m => m.Systeme).Select(m => m.Texte).ToList();
            Assert.Contains(traces, x => x.Contains("validée"));
            Assert.Contains(traces, x => x.Contains("Entrée (dans le SI)") && x.Contains("En stock"));
            Assert.Contains(traces, x => x.Contains("Mise au rebut") && x.Contains("Rebuté"));
            Assert.Equal(StatutTicket.EnCours, t.Statut);
        }

        // Hors statistiques d'assistance
        var tickets = new ServiceTickets(_fabrique);
        Assert.Equal(0, (await tickets.AnalyserAsync(null)).Crees);
    }

    [Fact]
    public async Task Remplacement_refuse_clot_le_ticket_de_rapatriement()
    {
        var d = (await _service.CreerAsync(Complet(), Salarie)).Demande; // rien à son nom dans l'inventaire : un ticket « à identifier »
        Assert.Null(d.MaterielRemplace);
        await _service.DeciderAsync(d.IdPublic, false, null, Valideur);
        await using var db = _fabrique.CreateDbContext();
        var t = await db.Tickets.SingleAsync();
        Assert.Null(t.MaterielId);
        Assert.Contains("aucun PC portable / Écran attribué à camille.test@vitalrest.com", t.Description);
        Assert.Equal(StatutTicket.Clos, t.Statut);
        Assert.Contains("refusée", t.Solution);
    }

    [Fact]
    public async Task Pas_de_ticket_hors_remplacement_de_materiel()
    {
        var f = Complet();
        f.Concerne = "Une ouverture de poste";
        await _service.CreerAsync(f, Salarie);
        f = Complet();
        f.Objets = ["Adresse email"]; // remplacement sans matériel
        await _service.CreerAsync(f, Salarie);
        await using var db = _fabrique.CreateDbContext();
        Assert.False(await db.Tickets.AnyAsync());
    }
}
