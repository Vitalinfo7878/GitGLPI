using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models.ODataErrors;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;
using T = ParcInfo.Web.Services.TypeChangementOrganisation;

namespace ParcInfo.Tests;

public sealed class SynchronisationOrganisationTests
{
    private static readonly List<CompteMicrosoft> Comptes =
    [
        new() { Id = "s455", NomAffichage = "455 - FOYER HORIZON", Email = "site455@vitalrest.com", ResponsableId = "rs-a", ResponsableNom = "Alice ANCIEN" },
        new() { Id = "s440", NomAffichage = "440 - EHPAD SAINTE MONIQUE", Email = "site440@vitalrest.com", ResponsableId = "rs-b", ResponsableNom = "Bruno SECTEUR" },
        new() { Id = "s501", NomAffichage = "501 - POLE GERIATRIQUE", Email = "site501@vitalrest.com" },
        new() { Id = "rs-a", NomAffichage = "Alice ANCIEN", Email = "alice.ancien@vitalrest.com" },
        new() { Id = "rs-b", NomAffichage = "Bruno SECTEUR", Email = "bruno.secteur@vitalrest.com", AutresAdresses = "b.secteur@vitalrest.com" },
    ];

    private static readonly List<Site> Sites =
    [
        new() { Id = 1, Code = "455", Nom = "FOYER HORIZON", EntraObjectId = "s455" },
        new() { Id = 2, Code = "440", Nom = "EHPAD SAINTE MONIQUE", EntraObjectId = "s440", Responsable = "Bruno SECTEUR" },
        new() { Id = 3, Code = "440-1", Nom = "EHPAD SAINTE MONIQUE (bis)", EntraObjectId = "s440" },
        new() { Id = 4, Code = "501", Nom = "POLE GERIATRIQUE", EntraObjectId = "s501" },
        new() { Id = 5, Code = "087", Nom = "EHPAD LES LILAS" },
    ];

    private static List<GroupeRegion> Groupes() =>
    [
        new("g-co", "REGION_SITE_CENTRE_OUEST", true, null, new HashSet<string> { "s501" }),
        new("g-idf", "REGION_SITE_IDF", true, null, new HashSet<string> { "s455", "s440" }),
        new("g-idfal", "REGION_SITE_IDF_AL", false, "liste de diffusion Exchange", new HashSet<string>()),
    ];

    [Fact]
    public void Changements_calcules_depuis_le_fichier()
    {
        List<LigneOrganisation> lignes =
        [
            new("455", "FOYER HORIZON", true, "CENTRE OUEST", "bruno.secteur@vitalrest.com"), // change de région et de responsable
            new("440", "EHPAD SAINTE MONIQUE", true, "IDF", "b.secteur@vitalrest.com"),         // déjà à jour (autre adresse du même RS)
            new("440-1", "EHPAD SAINTE MONIQUE (bis)", true, "IDF AL", "bruno.secteur@vitalrest.com"), // sous-site : le site principal fait foi
            new("501", "POLE GERIATRIQUE", false, "CENTRE OUEST", "alice.ancien@vitalrest.com"),  // fermé : sort des groupes
            new("87", "EHPAD LES LILAS", true, "SUD EST", "inconnu@vitalrest.com"),               // pas de compte Microsoft
            new("999", "NOUVEAU", true, "IDF", "bruno.secteur@vitalrest.com"),                    // absent de l'inventaire
        ];
        var plan = ServiceSynchronisationOrganisation.Calculer(lignes, Sites, Comptes, Groupes());

        string[] Textes(T type) => plan.Changements.Where(c => c.Type == type).Select(ServiceSynchronisationOrganisation.Texte).ToArray();
        Assert.Equal(["455 - FOYER HORIZON (455) : responsable Alice ANCIEN → Bruno SECTEUR"], Textes(T.Responsable));
        Assert.Equal(["455 - FOYER HORIZON (455) ajouté au groupe REGION_SITE_CENTRE_OUEST"], Textes(T.AjoutGroupe));
        Assert.Equal(["455 - FOYER HORIZON (455) retiré du groupe REGION_SITE_IDF", "501 - POLE GERIATRIQUE (501) retiré du groupe REGION_SITE_CENTRE_OUEST"], Textes(T.RetraitGroupe));
        // Fiches de l'inventaire : responsable de secteur (nom de l'annuaire, ou son adresse s'il est inconnu)
        Assert.Equal([("087", "inconnu@vitalrest.com"), ("440-1", "Bruno SECTEUR"), ("455", "Bruno SECTEUR")],
            plan.Changements.Where(c => c.Type == T.ResponsableInventaire).Select(c => (c.Sites, c.Apres)).ToArray());

        Assert.Equal((6, 5, 1, 3), (plan.Lignes, plan.Ouverts, plan.Fermes, plan.ComptesConcernes));
        Assert.Contains(plan.Problemes, p => p.Contains("absent(s) de l'inventaire : 999"));
        Assert.Contains(plan.Problemes, p => p.Contains("sans compte Microsoft relié") && p.Contains("087"));
        Assert.Contains(plan.Problemes, p => p.Contains("régions différentes") && p.Contains("440"));
        Assert.Contains(("IDF AL", "REGION_SITE_IDF_AL"), plan.Correspondances);
        Assert.Contains(("SUD EST", (string?)null), plan.Correspondances);
    }

    [Fact]
    public void Liste_de_diffusion_signalee_non_modifiable_et_responsable_inconnu()
    {
        var plan = ServiceSynchronisationOrganisation.Calculer(
            [new("455", null, true, "Idf-Al", "personne@vitalrest.com")], Sites, Comptes, Groupes());
        var ajout = Assert.Single(plan.Changements, c => c.Type == T.AjoutGroupe);
        Assert.Equal(("REGION_SITE_IDF_AL", "liste de diffusion Exchange"), (ajout.Apres, ajout.Blocage));
        Assert.Single(plan.Changements, c => c.Type == T.RetraitGroupe && c.Avant == "REGION_SITE_IDF");
        Assert.DoesNotContain(plan.Changements, c => c.Type == T.Responsable);
        Assert.Contains(plan.Problemes, p => p.Contains("introuvable(s) dans l'annuaire") && p.Contains("personne@vitalrest.com"));
    }

    [Fact]
    public void Region_sans_groupe_le_site_reste_dans_ses_groupes()
    {
        var plan = ServiceSynchronisationOrganisation.Calculer([new("455", null, true, "NORD EST", null)], Sites, Comptes, Groupes());
        Assert.DoesNotContain(plan.Changements, c => c.Type is T.AjoutGroupe or T.RetraitGroupe);
        Assert.Contains(plan.Problemes, p => p.Contains("Aucun groupe REGION_SITE_") && p.Contains("NORD EST"));
    }

    [Fact]
    public void Lecture_du_fichier_organisation()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Organisation");
        string[] entetes = ["Nom", "Nom Commercial", "Date Ouverture", "Etat", "DO", "Région", "Email CG", "Email RS"];
        for (var i = 0; i < entetes.Length; i++) ws.Cell(1, i + 1).Value = entetes[i];
        object[][] lignes = [["455", "FOYER HORIZON", "01/07/2023", "Open", "FRANCE SUD", "CENTRE OUEST", "cg@vitalrest.com", "Bruno.Secteur@vitalrest.com"],
            ["501-3", "ESCALE", "01/06/2024", "Close", "FRANCE SUD", "CENTRE OUEST", "", "None"]];
        for (var l = 0; l < lignes.Length; l++)
            for (var c = 0; c < lignes[l].Length; c++) ws.Cell(l + 2, c + 1).Value = lignes[l][c].ToString();
        using var flux = new MemoryStream();
        wb.SaveAs(flux);
        flux.Position = 0;
        Assert.Equal([new LigneOrganisation("455", "FOYER HORIZON", true, "CENTRE OUEST", "bruno.secteur@vitalrest.com"), new LigneOrganisation("501-3", "ESCALE", false, "CENTRE OUEST", null)],
            ServiceSynchronisationOrganisation.Lire(flux));
    }

    private sealed class AnnuaireDeTest : IAnnuaireMicrosoft
    {
        public bool Disponible => true;
        public Task<IReadOnlyList<CompteAnnuaire>> LireComptesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CompteAnnuaire>>([]);
        public Task<IReadOnlyList<GroupeRegion>> LireGroupesAsync(string prefixe, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GroupeRegion>>(Groupes());
    }

    /// <summary>Écritures notées ; le retrait du groupe IDF est refusé comme par Graph sans droits.</summary>
    private sealed class EcritureDeTest : IEcritureAnnuaire
    {
        public List<string> Faites { get; } = [];
        public Task DefinirResponsableAsync(string compteId, string responsableId, CancellationToken ct = default) { Faites.Add($"manager {compteId}={responsableId}"); return Task.CompletedTask; }
        public Task AjouterMembreAsync(string groupeId, string compteId, CancellationToken ct = default) { Faites.Add($"+{groupeId} {compteId}"); return Task.CompletedTask; }
        public Task RetirerMembreAsync(string groupeId, string compteId, CancellationToken ct = default) =>
            groupeId == "g-idf" ? throw new ODataError { Error = new MainError { Code = "Authorization_RequestDenied", Message = "Insufficient privileges" } }
            : Task.Run(() => Faites.Add($"-{groupeId} {compteId}"));
    }

    [Fact]
    public async Task Application_des_changements()
    {
        using var fabrique = new BaseDeTest();
        await using (var db = fabrique.CreateDbContext())
        {
            db.ComptesMicrosoft.AddRange(Comptes.Select(c => new CompteMicrosoft { Id = c.Id, NomAffichage = c.NomAffichage, Email = c.Email, AutresAdresses = c.AutresAdresses, ResponsableId = c.ResponsableId, ResponsableNom = c.ResponsableNom }));
            db.Sites.AddRange(Sites.Select(x => new Site { Code = x.Code, Nom = x.Nom, EntraObjectId = x.EntraObjectId, Responsable = x.Responsable }));
            await db.SaveChangesAsync();
        }
        var ecriture = new EcritureDeTest();
        var service = new ServiceSynchronisationOrganisation(fabrique, new AnnuaireDeTest(), ecriture, NullLogger<ServiceSynchronisationOrganisation>.Instance);
        var plan = await service.AnalyserAsync([new("455", null, true, "IDF AL", "bruno.secteur@vitalrest.com"), new("501", null, false, "CENTRE OUEST", null)]);
        var admin = new Utilisateur("Anthony Lelay", "anthony.lelay@vitalrest.com", true) { EstAdministrateur = true };

        await Assert.ThrowsAsync<ErreurMetier>(() => service.AppliquerAsync(plan, new Utilisateur("Lamine", "lamine@vitalrest.com", true)));
        var resultats = await service.AppliquerAsync(plan, admin);

        // Ajout au groupe IDF AL non modifiable (liste de diffusion) : pas tenté
        Assert.Equal(["manager s455=rs-b", "-g-co s501"], ecriture.Faites);
        var echec = Assert.Single(resultats, r => r.Erreur is not null);
        Assert.Contains("Droits d'écriture non accordés", echec.Erreur);
        Assert.Equal("REGION_SITE_IDF", echec.Changement.Avant);
        await using var lecture = fabrique.CreateDbContext();
        Assert.Equal("Bruno SECTEUR", (await lecture.Sites.SingleAsync(x => x.Code == "455")).Responsable);
        Assert.Equal("rs-b", (await lecture.ComptesMicrosoft.SingleAsync(x => x.Id == "s455")).ResponsableId);
        var journal = await lecture.Journal.Where(j => j.Action == "Synchronisation organisation").Select(j => j.Details).ToListAsync();
        Assert.Contains("455 - FOYER HORIZON (455) : responsable Alice ANCIEN → Bruno SECTEUR", journal);
        Assert.Contains(journal, d => d!.StartsWith("Fichier Organisation appliqué : 3 changement(s) fait(s), 1 en échec, 1 non modifiable(s)"));
    }
}
