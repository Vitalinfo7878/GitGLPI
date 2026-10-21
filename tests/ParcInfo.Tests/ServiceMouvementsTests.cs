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

    public ServiceMouvementsTests()
    {
        _service = new ServiceMouvements(_fabrique, new GenerateurFiche(), NullLogger<ServiceMouvements>.Instance);
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
    public async Task Attribution_puis_restitution()
    {
        var (pc, ecran, personne) = Donnees();

        var r = await _service.CreerMouvementsAsync(new DemandeMouvement
        {
            Type = TypeMouvement.Attribution, MaterielIds = [pc, ecran], DestinationPersonneId = personne,
        }, Informatique);

        Assert.Equal([101, 102], r.Numeros);
        Assert.NotNull(r.Fiche);
        Assert.Equal("FIA" + DateTime.Today.Year + " 0101", r.Fiche!.Code);
        Assert.Equal("camille.test@vitalrest.com", r.Fiche.DestinataireEmail);
        Assert.Null(r.Avertissement);

        await using (var db = _fabrique.CreateDbContext())
        {
            var m = await db.Materiels.FindAsync(pc);
            Assert.Equal(StatutMateriel.Attribue, m!.Statut);
            Assert.Equal(personne, m.PersonneId);
            Assert.NotNull((await db.Fiches.SingleAsync()).Pdf);
            Assert.Empty(await db.Courriels.ToListAsync()); // aucun mail envoyé au destinataire
        }

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
    public async Task Attribution_a_un_site_sans_adresse_mail()
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
        var r = await _service.CreerMouvementsAsync(new DemandeMouvement
        {
            Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationSiteId = site,
        }, Informatique);
        Assert.Equal(TypeFiche.Attribution, r.Fiche!.Type);
        Assert.Null(r.Fiche.DestinataireEmail);
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
    public async Task Suppression_d_un_materiel_et_de_son_historique()
    {
        var (pc, ecran, personne) = Donnees();
        var r = await _service.CreerMouvementsAsync(new DemandeMouvement
        {
            Type = TypeMouvement.Attribution, MaterielIds = [pc, ecran], DestinationPersonneId = personne,
        }, Informatique);
        await using (var db = _fabrique.CreateDbContext())
        {
            db.Tickets.Add(new Ticket { Numero = 1, Titre = "Écran HS", MaterielId = pc, MaterielReference = "PC0001" });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ErreurMetier>(() => _service.SupprimerMaterielAsync(pc, new Utilisateur("Autre", "autre@vitalrest.com", false)));
        await _service.SupprimerMaterielAsync(pc, Informatique);

        await using (var db = _fabrique.CreateDbContext())
        {
            Assert.Null(await db.Materiels.FindAsync(pc));
            Assert.DoesNotContain(await db.Mouvements.ToListAsync(), m => m.MaterielId == pc);
            Assert.Single(await db.Mouvements.Where(m => m.MaterielId == ecran && m.Type == TypeMouvement.Attribution).ToListAsync());
            Assert.Single(await db.Fiches.ToListAsync()); // la fiche émise est conservée
            var t = await db.Tickets.SingleAsync();
            Assert.Null(t.MaterielId);
            Assert.Equal("PC0001", t.MaterielReference);
            Assert.Contains(await db.Journal.ToListAsync(), j => j.Action == "Matériel supprimé" && j.Details!.StartsWith("PC0001"));
        }
        Assert.NotNull(r.Fiche);
    }
}
