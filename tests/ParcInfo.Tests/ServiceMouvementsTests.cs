using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public sealed class ServiceMouvementsTests : IDisposable
{
    private readonly BaseDeTest _fabrique = new();
    private readonly ServiceMouvements _service;

    private static readonly Utilisateur Informatique = new("Service Informatique", "informatique@vitalrest.com", true);
    private static readonly Utilisateur Camille = new("Camille TEST", "camille.test@vitalrest.com", false);
    private static readonly Utilisateur Autre = new("Autre", "autre@vitalrest.com", false);

    public ServiceMouvementsTests()
    {
        var options = new OptionsMessagerie { UrlApplication = "https://parc.test" };
        var notifications = new ServiceNotifications(new EnvoiCourrielSimule(NullLogger<EnvoiCourrielSimule>.Instance), _fabrique, options, NullLogger<ServiceNotifications>.Instance);
        _service = new ServiceMouvements(_fabrique, new GenerateurFiche(), notifications, NullLogger<ServiceMouvements>.Instance);
    }

    public void Dispose() => _fabrique.Dispose();

    private (int Pc, int Ecran, int Personne) Donnees()
    {
        using var db = _fabrique.CreateDbContext();
        var p = new Personne { NomComplet = "Camille TEST", Email = "camille.test@vitalrest.com" };
        var pc = new Materiel { NumInterne = "PC0001", Type = TypeMateriel.PcFixe, Statut = StatutMateriel.EnStock, MotDePasseSession = "secret" };
        var ecran = new Materiel { NumInterne = "E0001", Type = TypeMateriel.Ecran, Statut = StatutMateriel.EnStock };
        db.AddRange(p, pc, ecran);
        db.Mouvements.Add(new Mouvement { Numero = 100, Materiel = pc, Type = TypeMouvement.EntreeInitiale });
        db.SaveChanges();
        return (pc.Id, ecran.Id, p.Id);
    }

    [Fact]
    public async Task Attribution_avec_confirmation_puis_restitution()
    {
        var (pc, ecran, personne) = Donnees();

        var r = await _service.CreerMouvementsAsync(new DemandeMouvement
        {
            Type = TypeMouvement.Attribution, MaterielIds = [pc, ecran], DestinationPersonneId = personne, DemanderConfirmation = true,
        }, Informatique);

        Assert.Equal([101, 102], r.Numeros);
        Assert.NotNull(r.Fiche);
        Assert.Equal("FIA" + DateTime.Today.Year + " 0101", r.Fiche!.Code);
        Assert.Equal(StatutReception.EnAttente, r.Fiche.StatutReception);
        Assert.Null(r.AvertissementCourriel);

        await using (var db = _fabrique.CreateDbContext())
        {
            var m = await db.Materiels.FindAsync(pc);
            Assert.Equal(StatutMateriel.Attribue, m!.Statut);
            Assert.Equal(personne, m.PersonneId);
            Assert.NotNull((await db.Fiches.SingleAsync()).Pdf);
            Assert.Equal("camille.test@vitalrest.com", (await db.Courriels.SingleAsync()).Destinataire);
        }

        // Un autre utilisateur ne peut pas confirmer à la place du destinataire
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.ConfirmerReceptionAsync(r.Fiche.IdPublic, Autre));
        var fiche = await _service.ConfirmerReceptionAsync(r.Fiche.IdPublic, Camille);
        Assert.Equal(StatutReception.Confirmee, fiche.StatutReception);
        Assert.Equal("Camille TEST", fiche.ReceptionConfirmeePar);
        Assert.All(fiche.Mouvements, m => Assert.NotNull(m.DateReception));

        var retour = await _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Restitution, MaterielIds = [pc] }, Informatique);
        Assert.Equal(TypeFiche.Restitution, retour.Fiche!.Type);
        await using (var db = _fabrique.CreateDbContext())
        {
            var m = await db.Materiels.FindAsync(pc);
            Assert.Equal(StatutMateriel.EnStock, m!.Statut);
            Assert.Null(m.PersonneId);
            var mv = await db.Mouvements.SingleAsync(x => x.Numero == retour.Numeros[0]);
            Assert.Equal("Camille TEST", mv.Expediteur);
            Assert.Equal(RegleMouvement.ServiceInformatique, mv.DestinationLibre);
        }
    }

    [Fact]
    public async Task Mouvement_impossible_depuis_ce_statut()
    {
        var (pc, _, _) = Donnees();
        var ex = await Assert.ThrowsAsync<ErreurMetier>(() =>
            _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Restitution, MaterielIds = [pc] }, Informatique));
        Assert.Contains("PC0001", ex.Message);
    }

    [Fact]
    public async Task Attribution_sans_destinataire_refusee()
    {
        var (pc, _, _) = Donnees();
        await Assert.ThrowsAsync<ErreurMetier>(() =>
            _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Attribution, MaterielIds = [pc] }, Informatique));
    }

    [Fact]
    public async Task Confirmation_impossible_sans_adresse_mail()
    {
        var (pc, _, _) = Donnees();
        int site;
        await using (var db = _fabrique.CreateDbContext())
        {
            var s = new Site { Code = "586" };
            db.Sites.Add(s);
            await db.SaveChangesAsync();
            site = s.Id;
        }
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.CreerMouvementsAsync(new DemandeMouvement
        {
            Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationSiteId = site, DemanderConfirmation = true,
        }, Informatique));

        // Sans confirmation, l'attribution au site passe
        var r = await _service.CreerMouvementsAsync(new DemandeMouvement
        {
            Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationSiteId = site, DemanderConfirmation = false,
        }, Informatique);
        Assert.Equal(StatutReception.NonDemandee, r.Fiche!.StatutReception);
    }

    [Fact]
    public async Task Reception_d_achat_numerote_dans_la_nomenclature()
    {
        Donnees();
        var r = await _service.ReceptionnerAchatAsync(new DemandeAchat
        {
            Fournisseur = "AMAZON",
            Materiels = [new() { Type = TypeMateriel.PcFixe, Modele = "Lenovo M75q" }, new() { Type = TypeMateriel.PcFixe }, new() { Type = TypeMateriel.Ecran }],
        }, Informatique);
        Assert.Equal(TypeFiche.Achat, r.Fiche!.Type);
        await using var db = _fabrique.CreateDbContext();
        var nums = await db.Materiels.Where(m => m.Statut == StatutMateriel.EnStock).Select(m => m.NumInterne).OrderBy(n => n).ToListAsync();
        Assert.Equal(["E0001", "E0002", "PC0001", "PC0002", "PC0003"], nums);
    }

    [Fact]
    public void Prochain_numero_interne()
    {
        Assert.Equal("PC0444", ServiceMouvements.ProchainNumero("PC", ["PC0443", "PC0099", "PCANCIEN2", "PO0500"]));
        Assert.Equal("PO0001", ServiceMouvements.ProchainNumero("PO", []));
    }

    [Fact]
    public void Mot_de_passe_visible_seulement_par_le_detenteur_sans_reception_en_attente()
    {
        var m = new Materiel { Statut = StatutMateriel.Attribue, Personne = new Personne { Email = "camille.test@vitalrest.com" } };
        Assert.True(ServiceMouvements.PeutVoirMotDePasse(Camille, m, []));
        Assert.False(ServiceMouvements.PeutVoirMotDePasse(Autre, m, []));
        Assert.False(ServiceMouvements.PeutVoirMotDePasse(Camille, m, [new Fiche()]));
        Assert.True(ServiceMouvements.PeutVoirMotDePasse(Informatique, m, [new Fiche()]));
    }

}
