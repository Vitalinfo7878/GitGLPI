using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public sealed class DroitsTests : IDisposable
{
    private readonly BaseDeTest _fabrique = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly AnnuaireDeTest _annuaire = new();
    private readonly IConfiguration _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["AzureAd:ClientId"] = "appli",
        ["Authentification:Administrateurs:0"] = "anthony.lelay@vitalrest.com",
        ["Authentification:Administrateurs:1"] = "informatique@vitalrest.com",
        ["Demandes:Valideurs:0"] = "thomas.verchere@groupe-vitalrest.com",
        ["Tickets:Administrateurs:0"] = "thomas.verchere@groupe-vitalrest.com",
    }).Build();
    private readonly ServiceDroits _service;
    private static readonly Utilisateur Anthony = new("Anthony Lelay", "anthony.lelay@vitalrest.com", true) { EstAdministrateur = true };
    private static readonly Utilisateur Lamine = new("Lamine Cisse", "lamine.cisse@vitalrest.com", true);

    public DroitsTests() => _service = new ServiceDroits(_fabrique, _cache, _config, _annuaire, NullLogger<ServiceDroits>.Instance);

    public void Dispose()
    {
        _cache.Dispose();
        _fabrique.Dispose();
    }

    private sealed class AnnuaireDeTest : IAnnuaireMicrosoft
    {
        public bool Disponible => true;
        public Task<IReadOnlyList<CompteAnnuaire>> LireComptesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CompteAnnuaire>>([]);
        public Task<IReadOnlyList<RoleApplication>> LireRolesApplicationAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RoleApplication>>(
        [
            new("lamine.cisse@vitalrest.com", "Lamine Cisse", "Gestionnaire"),
            new("informatique@vitalrest.com", "Informatique Vitalrest", "Gestionnaire"), // administrateur principal : pas de ligne
            new("thomas.verchere@groupe-vitalrest.com", "Thomas Verchère", "Valideur"),
        ]);
    }

    private async Task<Utilisateur> Connecter(params Claim[] claims)
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "name", "roles"));
        return Utilisateur.Depuis(await new AjoutRolesConfigures(_config, _fabrique, _cache).TransformAsync(p));
    }

    [Fact]
    public async Task Reprise_unique_des_roles_Entra_et_de_la_configuration()
    {
        Assert.Equal(2, await _service.InitialiserAsync());
        var droits = await _service.ListerAsync();
        var lamine = droits.Single(d => d.Email == "lamine.cisse@vitalrest.com");
        Assert.Equal([Droit.Gestionnaire], lamine.Droits);
        Assert.Equal("Lamine Cisse", lamine.Nom);
        var thomas = droits.Single(d => d.Email == "thomas.verchere@groupe-vitalrest.com");
        Assert.Equal([Droit.Valideur, Droit.AdminTickets, Droit.ConsultationInventaire], thomas.Droits);
        Assert.DoesNotContain(droits, d => d.Email == "informatique@vitalrest.com");

        // Une seule fois : même si la liste est vidée ensuite, rien n'est repris à nouveau
        await _service.SupprimerAsync(lamine.Id, Anthony);
        await _service.SupprimerAsync(thomas.Id, Anthony);
        Assert.Equal(0, await _service.InitialiserAsync());
        Assert.Empty(await _service.ListerAsync());
    }

    [Fact]
    public async Task Les_droits_viennent_de_la_page_et_non_du_jeton_Entra()
    {
        await _service.InitialiserAsync();

        // Administrateur principal : tous les droits du service informatique, et la page « Droits »
        var anthony = await Connecter(new Claim("preferred_username", "Anthony.Lelay@vitalrest.com"));
        Assert.True(anthony.EstAdministrateur);
        Assert.True(anthony.EstGestionnaire);
        Assert.True(anthony.VoitInventaire);
        Assert.False(anthony.EstValideur);

        // Thomas : valideur, tickets et consultation de l'inventaire, sans gestion du parc
        var thomas = await Connecter(new Claim("email", "thomas.verchere@vitalrest.com"), new Claim("preferred_username", "thomas.verchere@groupe-vitalrest.com"));
        Assert.True(thomas.EstValideur);
        Assert.True(thomas.EstAdminTickets);
        Assert.True(thomas.VoitInventaire);
        Assert.False(thomas.EstGestionnaire);
        Assert.False(thomas.EstAdministrateur);

        // Un rôle Entra ID resté sur le compte n'ouvre plus rien : seul compte ce qui est coché sur la page
        var ancien = await Connecter(new Claim("preferred_username", "ancien@vitalrest.com"), new Claim("roles", "Gestionnaire"));
        Assert.False(ancien.EstGestionnaire);

        // Retrait du droit : effet immédiat (cache vidé)
        var lamine = (await _service.ListerAsync()).Single(d => d.Email == "lamine.cisse@vitalrest.com");
        Assert.True((await Connecter(new Claim("email", "lamine.cisse@vitalrest.com"))).EstGestionnaire);
        await _service.ModifierAsync(lamine.Id, Droit.Gestionnaire, false, Anthony);
        await _service.ModifierAsync(lamine.Id, Droit.ConsultationInventaire, true, Anthony);
        var apres = await Connecter(new Claim("email", "lamine.cisse@vitalrest.com"));
        Assert.False(apres.EstGestionnaire);
        Assert.True(apres.VoitInventaire);

        await using var db = _fabrique.CreateDbContext();
        Assert.Equal(2, await db.Journal.CountAsync(j => j.Action == "Droits" && j.Details!.Contains("Lamine")));
    }

    [Fact]
    public async Task Gestion_des_droits_reservee_aux_administrateurs_principaux()
    {
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterAsync("nouveau@vitalrest.com", null, Lamine)); // gestionnaire, pas administrateur
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterAsync("informatique@vitalrest.com", null, Anthony)); // déjà administrateur
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterAsync("pas-une-adresse", null, Anthony));
        var d = await _service.AjouterAsync(" Nouveau@VitalRest.com ", "Nouvelle Personne", Anthony);
        Assert.Equal("nouveau@vitalrest.com", d.Email);
        Assert.Empty(d.Droits);
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.AjouterAsync("nouveau@vitalrest.com", null, Anthony));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.ModifierAsync(d.Id, Droit.Gestionnaire, true, Lamine));
    }

    [Fact]
    public async Task Consultation_de_l_inventaire_sans_gestion()
    {
        var services = new ServiceCollection().AddLogging();
        Roles.DefinirPolitiques(services.AddAuthorizationBuilder());
        var autorisation = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        ClaimsPrincipal Avec(params string[] roles) => new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "test"));

        Assert.True((await autorisation.AuthorizeAsync(Avec(Roles.ConsultationInventaire), Roles.PolitiqueInventaire)).Succeeded);
        Assert.False((await autorisation.AuthorizeAsync(Avec(Roles.ConsultationInventaire), Roles.PolitiqueGestion)).Succeeded);
        Assert.True((await autorisation.AuthorizeAsync(Avec(Roles.Gestionnaire), Roles.PolitiqueInventaire)).Succeeded);
        Assert.False((await autorisation.AuthorizeAsync(Avec(Roles.Valideur), Roles.PolitiqueInventaire)).Succeeded);
        Assert.False((await autorisation.AuthorizeAsync(Avec(Roles.Gestionnaire), Roles.PolitiqueAdministration)).Succeeded);
        Assert.True((await autorisation.AuthorizeAsync(Avec(Roles.Administrateur), Roles.PolitiqueAdministration)).Succeeded);
    }

    [Fact]
    public void Journal_d_inventaire_detaille_les_modifications()
    {
        var avant = new Materiel { NumInterne = "PC0001", Type = TypeMateriel.PcFixe, Modele = "Dell", NumSerie = "ABC", MotDePasseSession = "secret", AChanger = false };
        var apres = new Materiel { NumInterne = "PC0001", Type = TypeMateriel.PcFixe, Modele = "Dell OptiPlex", NumSerie = "ABC", MotDePasseSession = "nouveau", AChanger = true, RegionId = 3 };
        var d = JournalInventaire.Differences(avant, apres, id => id == 3 ? "DR OUEST" : null);
        Assert.Equal(["Modèle : Dell → Dell OptiPlex", "Région / service : — → DR OUEST", "Mot de passe de session modifié(e)", "À changer : non → oui"], d);
        Assert.DoesNotContain(d, x => x.Contains("secret") || x.Contains("nouveau"));

        Assert.Equal(DomaineJournal.Materiel, JournalInventaire.Domaine("Matériel supprimé"));
        Assert.Equal(DomaineJournal.Mouvement, JournalInventaire.Domaine("Sortie (attribution)"));
        Assert.Equal(DomaineJournal.CompteMicrosoft, JournalInventaire.Domaine("Compte Microsoft relié"));
        Assert.Equal(DomaineJournal.Autre, JournalInventaire.Domaine("Droits"));
    }

    [Fact]
    public async Task Notifications_par_mail_sans_droit_d_acces()
    {
        var config = new ConfigurationBuilder().AddConfiguration(_config).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:ReceptionAchat:0"] = " Pierre.Fragata@vitalrest.com ",
        }).Build();
        var service = new ServiceDroits(_fabrique, _cache, config, _annuaire, NullLogger<ServiceDroits>.Instance);
        Assert.Equal(1, await service.InitialiserNotificationsAsync());
        var pierre = Assert.Single(await service.ListerAsync());
        Assert.Equal("pierre.fragata@vitalrest.com", pierre.Email);
        Assert.Equal([Notification.ReceptionAchat], pierre.Notifications);
        Assert.Empty(pierre.Droits);

        // Abonné sans aucun accès supplémentaire
        var u = await Connecter(new Claim("preferred_username", "pierre.fragata@vitalrest.com"));
        Assert.False(u.EstGestionnaire || u.EstValideur || u.VoitInventaire);

        await using (var db = _fabrique.CreateDbContext())
        {
            Assert.Equal(["pierre.fragata@vitalrest.com"], await ServiceDroits.AbonnesAsync(db, Notification.ReceptionAchat));
            Assert.Empty(await ServiceDroits.AbonnesAsync(db, Notification.NouveauMateriel));
        }

        await Assert.ThrowsAsync<ErreurMetier>(() => service.ModifierAsync(pierre.Id, Notification.ReceptionAchat, false, Lamine));
        await service.ModifierAsync(pierre.Id, Notification.ReceptionAchat, false, Anthony);
        // Reprise unique : un désabonnement n'est pas annulé au redémarrage
        Assert.Equal(0, await service.InitialiserNotificationsAsync());
        await using (var db = _fabrique.CreateDbContext())
        {
            Assert.Empty(await ServiceDroits.AbonnesAsync(db, Notification.ReceptionAchat));
            Assert.Contains(await db.Journal.Select(j => j.Details).ToListAsync(), d => d == "pierre.fragata@vitalrest.com : notification « Réception d'achat » désactivée");
        }
    }
}
