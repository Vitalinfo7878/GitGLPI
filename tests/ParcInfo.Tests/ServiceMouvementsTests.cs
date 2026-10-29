using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

[Collection("Chiffrement")]
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

    [Fact]
    public async Task Correction_du_detenteur_sans_fiche()
    {
        var (pc, ecran, personne) = Donnees();
        int autre;
        await using (var db = _fabrique.CreateDbContext())
        {
            var p = new Personne { NomComplet = "Dominique AUTRE", Email = "dominique.autre@vitalrest.com" };
            db.Personnes.Add(p);
            await db.SaveChangesAsync();
            autre = p.Id;
        }
        // Matériel en stock : une correction n'a pas lieu d'être (attribution normale avec fiche)
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.CreerMouvementsAsync(new DemandeMouvement
            { Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationPersonneId = autre, Correction = true }, Informatique));

        await _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationPersonneId = personne }, Informatique);
        var r = await _service.CreerMouvementsAsync(new DemandeMouvement
            { Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationPersonneId = autre, Expediteur = "Camille TEST", Correction = true, Commentaire = "Erreur d'import" }, Informatique);

        Assert.Null(r.Fiche);
        await using var db2 = _fabrique.CreateDbContext();
        Assert.Equal(autre, (await db2.Materiels.FindAsync(pc))!.PersonneId);
        Assert.Equal(1, await db2.Fiches.CountAsync()); // seule la fiche de la première attribution
        var mv = await db2.Mouvements.SingleAsync(m => m.Numero == r.Numeros[0]);
        Assert.Equal($"{ServiceMouvements.MentionCorrection} — Erreur d'import", mv.Commentaire);
        Assert.Equal("Camille TEST", mv.Expediteur);
        Assert.True(await db2.Journal.AnyAsync(j => j.Action == "Correction du détenteur"));
    }

    [Fact]
    public async Task Suppression_annulee_depuis_le_journal()
    {
        var (pc, _, personne) = Donnees();
        var r = await _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationPersonneId = personne }, Informatique);
        int demande;
        await using (var db = _fabrique.CreateDbContext())
        {
            var d = new Demande { Numero = 592, DemandeurNom = "Camille TEST", CreeLe = DateTime.UtcNow, ModifieLe = DateTime.UtcNow };
            d.Materiels.Add(new DemandeMateriel { MaterielId = pc, Le = DateTime.UtcNow, Par = "Informatique" });
            db.Demandes.Add(d);
            await db.SaveChangesAsync();
            demande = d.Id;
        }
        await _service.SupprimerMaterielAsync(pc, Informatique);

        int journal;
        await using (var db = _fabrique.CreateDbContext())
        {
            Assert.False(await db.Materiels.AnyAsync(x => x.Id == pc));
            var j = await db.Journal.SingleAsync(x => x.Action == JournalInventaire.MaterielSupprime);
            if (ChiffrementSecrets.Actif) Assert.DoesNotContain("secret", j.Instantane!); // mot de passe chiffré dans le journal, comme en base
            journal = j.Id;
        }
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.RestaurerAsync(journal, new Utilisateur("Thomas", "thomas@vitalrest.com", false)));

        Assert.Contains("2 mouvement(s)", await _service.RestaurerAsync(journal, Informatique));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.RestaurerAsync(journal, Informatique)); // une seule fois

        await using var db2 = _fabrique.CreateDbContext();
        var m = await db2.Materiels.Include(x => x.Mouvements).ThenInclude(x => x.Fiche).SingleAsync(x => x.NumInterne == "PC0001");
        Assert.Equal(pc, m.Id); // même identifiant : l'historique du journal reste rattaché
        Assert.True(await db2.DemandesMateriels.AnyAsync(x => x.DemandeId == demande && x.MaterielId == pc)); // demande rattachée à nouveau
        Assert.Equal("secret", m.MotDePasseSession);
        Assert.Equal(StatutMateriel.Attribue, m.Statut);
        Assert.Equal(personne, m.PersonneId);
        Assert.Equal([100, 101], m.Mouvements.Select(x => x.Numero).Order());
        Assert.Equal(r.Fiche!.Code, m.Mouvements.Single(x => x.Numero == 101).Fiche!.Code); // la fiche conservée est rattachée à nouveau
        var trace = await db2.Journal.SingleAsync(x => x.Action == JournalInventaire.MaterielRestaure);
        Assert.Equal(m.Id, trace.MaterielId);
        Assert.NotNull((await db2.Journal.SingleAsync(x => x.Id == journal)).RestaureLe);

        // Le matériel restauré reçoit de nouveaux mouvements normalement (identifiants et numéros sans conflit)
        await _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Restitution, MaterielIds = [pc] }, Informatique);
        Assert.Equal("E0002", (await _service.AjouterMaterielNonInventorieAsync(new MaterielNonInventorie { Type = TypeMateriel.Ecran, NumSerie = "Y" }, Informatique)).NumInterne);
    }

    [Fact]
    public async Task Modification_annulee_puis_retablie()
    {
        var (pc, _, _) = Donnees();
        int journal;
        await using (var db = _fabrique.CreateDbContext())
        {
            // Comme la fiche matériel : instantané de l'état d'avant, puis modification
            var m = await db.Materiels.SingleAsync(x => x.Id == pc);
            var avant = InstantaneMateriel.Depuis(m, null).Serialiser();
            m.Modele = "Dell OptiPlex"; m.NumSerie = "SN-NOUVEAU"; m.MotDePasseSession = "autre"; m.AChanger = true;
            var j = new Journal { Date = DateTime.UtcNow, Utilisateur = "Informatique", Action = JournalInventaire.MaterielModifie, MaterielId = pc, Instantane = avant };
            db.Journal.Add(j);
            await db.SaveChangesAsync();
            journal = j.Id;
        }

        Assert.Contains("valeur(s) restaurée(s)", await _service.RestaurerAsync(journal, Informatique));
        int restauration;
        await using (var db = _fabrique.CreateDbContext())
        {
            var m = await db.Materiels.SingleAsync(x => x.Id == pc);
            Assert.Null(m.Modele);
            Assert.Null(m.NumSerie);
            Assert.Equal("secret", m.MotDePasseSession);
            Assert.False(m.AChanger);
            var trace = await db.Journal.SingleAsync(x => x.Action == JournalInventaire.MaterielRestaure);
            Assert.Contains("Modèle : Dell OptiPlex → —", trace.Details);
            Assert.Contains("Mot de passe de session modifié(e)", trace.Details);
            restauration = trace.Id;
        }

        // La restauration s'annule à son tour
        await _service.RestaurerAsync(restauration, Informatique);
        await using var db2 = _fabrique.CreateDbContext();
        var apres = await db2.Materiels.SingleAsync(x => x.Id == pc);
        Assert.Equal("Dell OptiPlex", apres.Modele);
        Assert.Equal("autre", apres.MotDePasseSession);
    }

    [Fact]
    public async Task Restauration_refusee_si_le_numero_interne_est_repris()
    {
        var (pc, _, _) = Donnees();
        await _service.SupprimerMaterielAsync(pc, Informatique);
        await _service.AjouterMaterielNonInventorieAsync(new MaterielNonInventorie { Type = TypeMateriel.PcFixe, NumInterne = "PC0001", NumSerie = "X" }, Informatique);
        await using var db = _fabrique.CreateDbContext();
        var journal = (await db.Journal.SingleAsync(x => x.Action == JournalInventaire.MaterielSupprime)).Id;
        var ex = await Assert.ThrowsAsync<ErreurMetier>(() => _service.RestaurerAsync(journal, Informatique));
        Assert.Contains("PC0001", ex.Message);
    }

    [Fact]
    public async Task Doublon_d_attribution_supprime_puis_restaure()
    {
        var (pc, _, personne) = Donnees();
        var a1 = await _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationPersonneId = personne }, Informatique);
        var a2 = await _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationPersonneId = personne }, Informatique);
        int doublon;
        await using (var db = _fabrique.CreateDbContext())
            doublon = (await db.Mouvements.SingleAsync(x => x.Numero == a2.Numeros[0])).Id;

        await Assert.ThrowsAsync<ErreurMetier>(() => _service.SupprimerMouvementAsync(doublon, new Utilisateur("Thomas", "thomas@vitalrest.com", false)));
        var message = await _service.SupprimerMouvementAsync(doublon, Informatique);
        Assert.Contains($"fiche {a2.Fiche!.Code} supprimée", message);
        Assert.DoesNotContain("revient", message); // même état qu'après le premier : rien ne change

        int journal;
        await using (var db = _fabrique.CreateDbContext())
        {
            Assert.Equal([100, a1.Numeros[0]], await db.Mouvements.Where(x => x.MaterielId == pc).OrderBy(x => x.Numero).Select(x => x.Numero).ToListAsync());
            var m = await db.Materiels.FindAsync(pc);
            Assert.Equal((StatutMateriel.Attribue, personne), (m!.Statut, m.PersonneId));
            Assert.False(await db.Fiches.AnyAsync(f => f.Code == a2.Fiche.Code));
            Assert.True(await db.Fiches.AnyAsync(f => f.Code == a1.Fiche!.Code));
            journal = (await db.Journal.SingleAsync(j => j.Action == JournalInventaire.MouvementSupprime)).Id;
        }

        // Restauration : même numéro, même fiche (PDF compris)
        await _service.RestaurerAsync(journal, Informatique);
        await using var db2 = _fabrique.CreateDbContext();
        var restaure = await db2.Mouvements.Include(x => x.Fiche).SingleAsync(x => x.Numero == a2.Numeros[0]);
        Assert.Equal(a2.Fiche.Code, restaure.Fiche!.Code);
        Assert.NotNull(restaure.Fiche.Pdf);
        Assert.Equal(personne, restaure.DestinationPersonneId);
        Assert.True(await db2.Journal.AnyAsync(j => j.Action == JournalInventaire.MouvementRestaure && j.MaterielId == pc));
    }

    [Fact]
    public async Task Dernier_mouvement_supprime_le_materiel_reprend_l_etat_precedent()
    {
        var (pc, _, personne) = Donnees();
        await _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Attribution, MaterielIds = [pc], DestinationPersonneId = personne }, Informatique);
        var retour = await _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Restitution, MaterielIds = [pc] }, Informatique);
        int id;
        await using (var db = _fabrique.CreateDbContext())
            id = (await db.Mouvements.SingleAsync(x => x.Numero == retour.Numeros[0])).Id;

        var message = await _service.SupprimerMouvementAsync(id, Informatique);
        Assert.Contains("revient à « Attribué » (Camille TEST)", message);
        await using (var db = _fabrique.CreateDbContext())
        {
            var m = await db.Materiels.FindAsync(pc);
            Assert.Equal((StatutMateriel.Attribue, personne), (m!.Statut, m.PersonneId));
        }

        // Restaurer le retour remet le matériel en stock
        await using (var db = _fabrique.CreateDbContext())
            await _service.RestaurerAsync((await db.Journal.SingleAsync(j => j.Action == JournalInventaire.MouvementSupprime)).Id, Informatique);
        await using var db2 = _fabrique.CreateDbContext();
        var apres = await db2.Materiels.FindAsync(pc);
        Assert.Equal((StatutMateriel.EnStock, (int?)null), (apres!.Statut, apres.PersonneId));
    }

    [Fact]
    public async Task Historique_du_materiel_d_une_personne()
    {
        var (pc, ecran, camille) = Donnees();
        int dominique, importe;
        await using (var db = _fabrique.CreateDbContext())
        {
            var p = new Personne { NomComplet = "Dominique MARTIN", Email = "dominique.martin@vitalrest.com" };
            var m = new Materiel { NumInterne = "PC0099", Type = TypeMateriel.PcFixe, Statut = StatutMateriel.Attribue, PersonneId = camille }; // repris de l'Excel, sans mouvement
            db.AddRange(p, m);
            await db.SaveChangesAsync();
            (dominique, importe) = (p.Id, m.Id);
        }
        Task Attribuer(int materiel, int personne) => _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Attribution, MaterielIds = [materiel], DestinationPersonneId = personne }, Informatique);

        await Attribuer(pc, camille);
        await Attribuer(pc, camille); // doublon : une seule période
        await _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Restitution, MaterielIds = [pc] }, Informatique);
        await Attribuer(pc, dominique);
        await Attribuer(ecran, camille);

        await using var db2 = _fabrique.CreateDbContext();
        var h = await HistoriqueDetenteur.CalculerAsync(db2, [camille], []);
        Assert.Equal(["E0001", "PC0099"], h.Where(x => x.EnCours).Select(x => x.Materiel.NumInterne).Order());
        Assert.Null(h.Single(x => x.Materiel.Id == importe).Arrivee);
        var rendu = Assert.Single(h, x => !x.EnCours);
        Assert.Equal(pc, rendu.Materiel.Id);
        Assert.Equal(TypeMouvement.Restitution, rendu.Depart!.Type);
        Assert.Equal(TypeMouvement.Attribution, rendu.Arrivee!.Type);

        var hd = await HistoriqueDetenteur.CalculerAsync(db2, [dominique], []);
        Assert.Equal(pc, Assert.Single(hd, x => x.EnCours).Materiel.Id);
    }

    [Fact]
    public async Task PC_portable_partage_de_teletravail()
    {
        var (pc, ecran, camille) = Donnees();
        int portable;
        await using (var db = _fabrique.CreateDbContext())
        {
            var m = new Materiel { NumInterne = "PO0120", Type = TypeMateriel.PcPortable, Statut = StatutMateriel.Attribue, PersonneId = camille };
            db.Materiels.Add(m);
            await db.SaveChangesAsync();
            portable = m.Id;
        }
        var poste = await _service.PostePartageAsync(" Télétravail Compta 1 ");
        Assert.Equal(poste.Id, (await _service.PostePartageAsync("télétravail compta 1")).Id); // pas de doublon
        Assert.True(poste.Partage && poste.SansCompteMicrosoft);

        // Changement de détenteur sans adresse mail ; un écran ne peut pas aller sur un poste partagé
        await _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Attribution, MaterielIds = [portable], DestinationPersonneId = poste.Id, Correction = true }, Informatique);
        var ex = await Assert.ThrowsAsync<ErreurMetier>(() =>
            _service.CreerMouvementsAsync(new DemandeMouvement { Type = TypeMouvement.Attribution, MaterielIds = [ecran], DestinationPersonneId = poste.Id }, Informatique));
        Assert.Contains("seuls des PC portables", ex.Message);

        await using var db2 = _fabrique.CreateDbContext();
        var m2 = await db2.Materiels.Include(x => x.Personne).SingleAsync(x => x.Id == portable);
        Assert.Equal("Télétravail Compta 1", m2.DetenteurLibelle);
        Assert.Null(m2.Personne!.Email);
        Assert.Contains("Télétravail Achat", await _service.PostesPartagesAsync(["Télétravail Achat"]));
        Assert.Contains("Télétravail Compta 1", await _service.PostesPartagesAsync([]));
    }

    [Fact]
    public async Task Consultation_de_l_inventaire_renseigne_les_numeros_comptables()
    {
        var (pc, _, _) = Donnees();
        var thomas = new Utilisateur("Thomas Verchère", "thomas.verchere@groupe-vitalrest.com", false, true) { VoitInventaire = true };
        await Assert.ThrowsAsync<ErreurMetier>(() =>
            _service.ModifierNumerosComptablesAsync(pc, "F-2026-118", "IMMO-42", new Utilisateur("Camille", "camille@vitalrest.com", false)));

        Assert.True(await _service.ModifierNumerosComptablesAsync(pc, " F-2026-118 ", "IMMO-42", thomas));
        Assert.False(await _service.ModifierNumerosComptablesAsync(pc, "F-2026-118", "IMMO-42", thomas)); // rien de changé

        await using var db = _fabrique.CreateDbContext();
        var m = await db.Materiels.FindAsync(pc);
        Assert.Equal(("F-2026-118", "IMMO-42", "secret"), (m!.NumFacture, m.NumImmo, m.MotDePasseSession)); // le reste est intact
        var j = await db.Journal.SingleAsync(x => x.Action == JournalInventaire.MaterielModifie);
        Assert.Equal("PC0001 : N° immobilisation : — → IMMO-42 ; N° de facture : — → F-2026-118", j.Details);

        // Restaurable par le service informatique
        await _service.RestaurerAsync(j.Id, Informatique);
        await using var db2 = _fabrique.CreateDbContext();
        Assert.Null((await db2.Materiels.FindAsync(pc))!.NumFacture);
    }
}
