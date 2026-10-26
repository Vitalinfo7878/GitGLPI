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
    public async Task Attribution_exige_l_adresse_mail_du_destinataire()
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
            Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationSiteId = site,
        }, Informatique));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.CreerMouvementsAsync(new DemandeMouvement
        {
            Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationSiteId = site, EmailDetenteur = "pas-une-adresse",
        }, Informatique));

        var r = await _service.CreerMouvementsAsync(new DemandeMouvement
        {
            Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationSiteId = site, EmailDetenteur = "Site586@Vitalrest.com",
        }, Informatique);
        Assert.Equal("site586@vitalrest.com", r.Fiche!.DestinataireEmail);
        await using var verif = _fabrique.CreateDbContext();
        Assert.Equal("site586@vitalrest.com", (await verif.Sites.SingleAsync()).Email); // enregistrée sur le site
    }

    [Fact]
    public async Task Reception_d_achat_numerote_dans_la_nomenclature()
    {
        Donnees();
        var incomplet = await Assert.ThrowsAsync<ErreurMetier>(() => _service.ReceptionnerAchatAsync(new DemandeAchat
        {
            Materiels = [new() { Type = TypeMateriel.PcFixe, NumSerie = "S1", NumFacture = "F1" }, new() { Type = TypeMateriel.Ecran }],
        }, Informatique));
        Assert.Equal("À renseigner : le fournisseur ; le nom du PC (ligne 1) ; le n° de série (ligne 2) ; le n° de facture (ligne 2).", incomplet.Message);
        var r = await _service.ReceptionnerAchatAsync(new DemandeAchat
        {
            Fournisseur = "AMAZON",
            Materiels = [new() { Type = TypeMateriel.PcFixe, NumInterne = "PC0444", Modele = "Lenovo M75q", NumSerie = "S1", NumFacture = "F1" },
                new() { Type = TypeMateriel.PcPortable, NumInterne = "po0012", NumSerie = "S2", NumFacture = "F1" },
                new() { Type = TypeMateriel.Ecran, NumSerie = "S3", NumFacture = "F1" }],
        }, Informatique);
        Assert.Equal(TypeFiche.Achat, r.Fiche!.Type);
        await using var db = _fabrique.CreateDbContext();
        var nums = await db.Materiels.Where(m => m.Statut == StatutMateriel.EnStock).Select(m => m.NumInterne).OrderBy(n => n).ToListAsync();
        Assert.Equal(["E0001", "E0002", "PC0001", "PC0444", "PO0012"], nums);
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

    [Fact]
    public async Task Categorie_pc_appliquee_aux_seuls_pc()
    {
        var (pc, ecran, _) = Donnees();
        Assert.Equal(1, await _service.DefinirCategoriePcAsync([pc, ecran], CategoriePc.Enrole, Informatique));
        await using var db = _fabrique.CreateDbContext();
        Assert.Equal(CategoriePc.Enrole, (await db.Materiels.FindAsync(pc))!.CategoriePc);
        Assert.Null((await db.Materiels.FindAsync(ecran))!.CategoriePc);
    }

    [Fact]
    public async Task Materiel_non_inventorie_entre_en_stock()
    {
        Donnees();
        var incomplet = await Assert.ThrowsAsync<ErreurMetier>(() =>
            _service.AjouterMaterielNonInventorieAsync(new MaterielNonInventorie { Type = TypeMateriel.PcPortable }, Informatique));
        Assert.Equal("À renseigner : le nom du PC ; le n° de série.", incomplet.Message);
        await Assert.ThrowsAsync<ErreurMetier>(() =>
            _service.AjouterMaterielNonInventorieAsync(new MaterielNonInventorie { Type = TypeMateriel.PcFixe, NumInterne = "pc0001", NumSerie = "X" }, Informatique));

        var ecran = await _service.AjouterMaterielNonInventorieAsync(
            new MaterielNonInventorie { Type = TypeMateriel.Ecran, NumSerie = "CN123", Provenance = "Retrouvé au site 087" }, Informatique);
        Assert.Equal("E0002", ecran.NumInterne);
        await using var db = _fabrique.CreateDbContext();
        var m = await db.Materiels.Include(x => x.Mouvements).SingleAsync(x => x.Id == ecran.Id);
        Assert.Equal(StatutMateriel.EnStock, m.Statut);
        var mv = Assert.Single(m.Mouvements);
        Assert.Equal(TypeMouvement.EntreeInitiale, mv.Type);
        Assert.Equal(101, mv.Numero);
        Assert.Equal("Retrouvé au site 087", mv.Expediteur);
        Assert.Null(mv.FicheId);
    }

    [Fact]
    public void Numero_interne_suivant_propose()
    {
        Assert.Equal("PC0455", ServiceMouvements.ProchainNumero("PC", ["PC0453", "PC0454", "PO0999", "PC0012-DOUBLON-L3"]));
        Assert.Equal("PC0456", ServiceMouvements.ProchainNumero("PC", ["PC0454", "PC0455"])); // n° déjà réservés sur la réception
        Assert.Equal("E0001", ServiceMouvements.ProchainNumero("E", []));
    }
}
