using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public sealed class ServiceComptesMicrosoftTests : IDisposable
{
    private readonly BaseDeTest _fabrique = new();
    private readonly AnnuaireDeTest _annuaire = new();
    private readonly ServiceComptesMicrosoft _service;
    private static readonly Utilisateur Informatique = new("Service Informatique", "informatique@vitalrest.com", true, true);

    public ServiceComptesMicrosoftTests() =>
        _service = new ServiceComptesMicrosoft(_fabrique, _annuaire, NullLogger<ServiceComptesMicrosoft>.Instance);

    public void Dispose() => _fabrique.Dispose();

    private sealed class AnnuaireDeTest : IAnnuaireMicrosoft
    {
        public List<CompteAnnuaire> Comptes { get; } = [];
        public bool Disponible => true;
        public Task<IReadOnlyList<CompteAnnuaire>> LireComptesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CompteAnnuaire>>(Comptes);
    }

    private static CompteAnnuaire Compte(string id, string nom, string mail, bool actif = true, string? poste = null, IReadOnlyList<string>? alias = null) =>
        new(id, nom, null, null, mail, mail, alias ?? [], poste, "Exploitation", null, null, null, "06 00 00 00 00", null, null, null,
            "m1", "Thomas Verchère", ["Tous les salariés"], ["Microsoft 365 Business Standard"], actif, null);

    [Fact]
    public async Task Rapprochements_certains_automatiques_et_doutes_a_trancher()
    {
        _annuaire.Comptes.AddRange(
        [
            Compte("a1", "Camille Dupont", "camille.dupont@vitalrest.com", poste: "Chef gérant"),
            Compte("a2", "Dominique Martin", "d.martin@vitalrest.com"),
            Compte("a3", "Dominique Martinez", "d.martinez@vitalrest.com"),
            Compte("a4", "Claude Bernard", "claude.bernard@vitalrest.com", alias: ["cbernard@groupe-vitalrest.com"]),
            Compte("a5", "Site 087 - EHPAD Les Tilleuls", "site087@vitalrest.com"),
            Compte("a6", "Site 440 - Résidence", "residence440@vitalrest.com"),
        ]);
        int dupont, martin, bernard, s087, s440;
        await using (var db = _fabrique.CreateDbContext())
        {
            var p1 = new Personne { NomComplet = "DUPONT Camille" };                                // nom identique et unique → automatique
            var p2 = new Personne { NomComplet = "MARTIN D." };                                     // doute → à trancher
            var p3 = new Personne { NomComplet = "BERNARD C.", Email = "CBernard@groupe-vitalrest.com" }; // alias → automatique
            var site087 = new Site { Code = "087" };
            var site440 = new Site { Code = "440", Email = "residence440@vitalrest.com" };
            db.AddRange(p1, p2, p3, site087, site440);
            db.Materiels.Add(new Materiel { NumInterne = "PC0001", Type = TypeMateriel.PcFixe, Statut = StatutMateriel.Attribue, Personne = p1 });
            await db.SaveChangesAsync();
            (dupont, martin, bernard, s087, s440) = (p1.Id, p2.Id, p3.Id, site087.Id, site440.Id);
        }

        var r = await _service.SynchroniserAsync("test");
        Assert.Equal(6, r.Comptes);
        Assert.Equal(3, r.ReliesAutomatiquement);

        await using (var db = _fabrique.CreateDbContext())
        {
            var p1 = await db.Personnes.FindAsync(dupont);
            Assert.Equal("a1", p1!.EntraObjectId);
            Assert.Equal("Camille Dupont", p1.NomComplet); // nom d'affichage Microsoft
            Assert.Equal("camille.dupont@vitalrest.com", p1.Email);
            Assert.Equal("a4", (await db.Personnes.FindAsync(bernard))!.EntraObjectId);
            Assert.Null((await db.Personnes.FindAsync(martin))!.EntraObjectId);
            var site = await db.Sites.FindAsync(s440);
            Assert.Equal("a6", site!.EntraObjectId);
            Assert.Equal("Site 440 - Résidence", site.Libelle);
            Assert.Null((await db.Sites.FindAsync(s087))!.EntraObjectId);
        }

        var aTrancher = await _service.ListerATrancherAsync();
        Assert.Equal(2, aTrancher.Count);
        var martinATrancher = aTrancher.Single(x => x.Type == TypeDetenteur.Personne);
        Assert.Equal("a2", martinATrancher.Candidats[0].Compte.Id);
        Assert.Contains(martinATrancher.Candidats, c => c.Compte.Id == "a3");
        Assert.Equal("a5", aTrancher.Single(x => x.Type == TypeDetenteur.Site).Candidats[0].Compte.Id); // site087@ → code 087

        await _service.RelierPersonneAsync(martin, "a2", Informatique);
        await _service.RelierSiteAsync(s087, "a5", Informatique);
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.RelierSiteAsync(s440, "a1", Informatique)); // compte d'une personne
        Assert.Empty(await _service.ListerATrancherAsync());
    }

    [Fact]
    public async Task Doublon_fusionne_et_depart_detecte()
    {
        _annuaire.Comptes.Add(Compte("a1", "Camille Dupont", "camille.dupont@vitalrest.com"));
        int principale, doublon;
        await using (var db = _fabrique.CreateDbContext())
        {
            var p1 = new Personne { NomComplet = "Camille DUPONT", Email = "camille.dupont@vitalrest.com" };
            var p2 = new Personne { NomComplet = "C. DUPONT" };
            db.AddRange(p1, p2);
            db.Materiels.AddRange(
                new Materiel { NumInterne = "PC0001", Type = TypeMateriel.PcFixe, Statut = StatutMateriel.Attribue, Personne = p1 },
                new Materiel { NumInterne = "PC0002", Type = TypeMateriel.PcPortable, Statut = StatutMateriel.Attribue, Personne = p2 });
            await db.SaveChangesAsync();
            (principale, doublon) = (p1.Id, p2.Id);
        }
        await _service.SynchroniserAsync("test");

        var message = await _service.RelierPersonneAsync(doublon, "a1", Informatique);
        Assert.Contains("fusionné", message);
        await using (var db = _fabrique.CreateDbContext())
        {
            Assert.Null(await db.Personnes.FindAsync(doublon));
            Assert.Equal(2, await db.Materiels.CountAsync(m => m.PersonneId == principale));
        }

        // Le compte est désactivé dans Entra : départ à traiter tant que le matériel n'est pas rendu
        _annuaire.Comptes[0] = Compte("a1", "Camille Dupont", "camille.dupont@vitalrest.com", actif: false);
        var r = await _service.SynchroniserAsync("test");
        Assert.Equal(1, r.Departs);
        var (personne, materiels) = Assert.Single(await _service.ListerDepartsAsync());
        Assert.Equal(principale, personne.Id);
        Assert.Equal(2, materiels.Count);

        // Compte supprimé de l'annuaire : conservé localement, marqué supprimé
        _annuaire.Comptes.Clear();
        await _service.SynchroniserAsync("test");
        await using (var db = _fabrique.CreateDbContext())
            Assert.True((await db.ComptesMicrosoft.SingleAsync()).Supprime);
    }

    [Fact]
    public async Task Aucun_compte_puis_revoir()
    {
        _annuaire.Comptes.Add(Compte("a1", "Camille Dupont", "camille.dupont@vitalrest.com"));
        int id;
        await using (var db = _fabrique.CreateDbContext())
        {
            var p = new Personne { NomComplet = "Stagiaire" };
            db.Personnes.Add(p);
            await db.SaveChangesAsync();
            id = p.Id;
        }
        await _service.SynchroniserAsync("test");
        Assert.Single(await _service.ListerATrancherAsync());
        await _service.DefinirSansCompteAsync(TypeDetenteur.Personne, id, true, Informatique);
        Assert.Empty(await _service.ListerATrancherAsync());
        await _service.DefinirSansCompteAsync(TypeDetenteur.Personne, id, false, Informatique);
        Assert.Single(await _service.ListerATrancherAsync());

        var p2 = await _service.PersonnePourCompteAsync("a1");
        Assert.Equal("Camille Dupont", p2.NomComplet);
        Assert.Equal(p2.Id, (await _service.PersonnePourCompteAsync("a1")).Id);
    }

    [Fact]
    public async Task Sous_sites_partagent_le_compte_du_site()
    {
        _annuaire.Comptes.Add(Compte("a1", "440 - EHPAD SAINTE MONIQUE", "ndbs.saintemonique@vitalrest.com"));
        int principal, sousSite, aRelier;
        await using (var db = _fabrique.CreateDbContext())
        {
            var s1 = new Site { Code = "440", Email = "ndbs.saintemonique@vitalrest.com" };
            var s2 = new Site { Code = "440-1", Email = "NDBS.SainteMonique@vitalrest.com" };
            var s3 = new Site { Code = "440-2" };
            db.Sites.AddRange(s1, s2, s3);
            await db.SaveChangesAsync();
            (principal, sousSite, aRelier) = (s1.Id, s2.Id, s3.Id);
        }

        var r = await _service.SynchroniserAsync("test");
        Assert.Equal(2, r.ReliesAutomatiquement); // même adresse : les deux sites sont reliés au même compte

        // Le site encore à trancher se voit proposer le compte déjà relié, signalé comme partagé
        var x = Assert.Single(await _service.ListerATrancherAsync());
        Assert.Contains("déjà relié au site 440, 440-1", Assert.Single(x.Candidats).Raison);
        Assert.Contains("partagé", await _service.RelierSiteAsync(aRelier, "a1", Informatique));

        await using var db2 = _fabrique.CreateDbContext();
        Assert.Equal("440 - EHPAD SAINTE MONIQUE", (await db2.Sites.FindAsync(principal))!.Libelle);
        Assert.Equal("440 - EHPAD SAINTE MONIQUE (440-1)", (await db2.Sites.FindAsync(sousSite))!.Libelle);
        Assert.Equal(3, await db2.Sites.CountAsync(s => s.EntraObjectId == "a1"));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.PersonnePourCompteAsync("a1"));
    }

    [Theory]
    [InlineData("087", "Site 087 - EHPAD", "Site 087 - EHPAD")]
    [InlineData("087", "Site 87 - EHPAD", "Site 87 - EHPAD")]
    [InlineData("440-1", "440 - EHPAD SAINTE MONIQUE", "440 - EHPAD SAINTE MONIQUE (440-1)")]
    [InlineData("440", "440-1 Annexe", "440-1 Annexe (440)")]
    public void Libelle_du_site_relie(string code, string nomAffichage, string attendu) =>
        Assert.Equal(attendu, Site.LibelleDe(code, nomAffichage, true));

    [Theory]
    [InlineData("DUPONT Camille", "Camille Dupont")]
    [InlineData("Hélène  O'Brien", "obrien helene")]
    public void Cle_de_nom_insensible_a_l_ordre_et_aux_accents(string a, string b) =>
        Assert.Equal(ServiceComptesMicrosoft.CleNom(a), ServiceComptesMicrosoft.CleNom(b.Replace("obrien", "o brien")));
}
