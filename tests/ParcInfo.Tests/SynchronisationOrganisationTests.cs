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
        // Comptes de sites absents de l'inventaire ou non reliés : retrouvés par le code en tête de leur nom
        new() { Id = "s087", NomAffichage = "087 - EHPAD LES LILAS", Email = "site087@vitalrest.com" },
        new() { Id = "s999", NomAffichage = "999 - NOUVEAU SITE", Email = "site999@vitalrest.com" },
        new() { Id = "s777a", NomAffichage = "777 - CUISINE", Email = "site777@vitalrest.com" },
        new() { Id = "s777b", NomAffichage = "777 - SELF", Email = "self777@vitalrest.com" },
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
        new("g-co", "REGION_CENTRE_OUEST_SITES", true, null, new HashSet<string> { "s501" }, "REGION_CENTRE_OUEST_SITES", "REGION_CENTRE_OUEST_SITES@vitalrest.com", true),
        new("g-idf", "REGION_IDF_SITES", true, null, new HashSet<string> { "s455", "s440" }, "REGION_IDF_SITES", "REGION_IDF_SITES@vitalrest.com", true),
        new("g-sud", "REGION_SUD_SITES", false, "groupe synchronisé depuis l'Active Directory local", new HashSet<string>()),
        new("g-ouest", "REGION_OUEST_SITES", true, null, new HashSet<string> { "s440" }, Adresse: "REGION_OUEST_SITES@vitalrest.com", Exchange: true),
        new("g-agences", "REGION_SITES_AGENCES_IDF", true, null, new HashSet<string> { "s455" }), // jamais touché
    ];

    [Fact]
    public void Changements_calcules_depuis_le_fichier()
    {
        List<LigneOrganisation> lignes =
        [
            new("455", "FOYER HORIZON", true, "CENTRE OUEST", "bruno.secteur@vitalrest.com"), // change de région et de responsable
            new("440", "EHPAD SAINTE MONIQUE", true, "IDF", "b.secteur@vitalrest.com"),         // déjà à jour (autre adresse du même RS)
            new("440-1", "EHPAD SAINTE MONIQUE (bis)", true, "IDF AL", "bruno.secteur@vitalrest.com"), // IDF AL = groupe IDF : pas de conflit
            new("440-5", "SAINTE MONIQUE 5", true, "CENTRE OUEST", "bruno.secteur@vitalrest.com"), // pas de compte à son code : celui du 440, qui fait foi
            new("501", "POLE GERIATRIQUE", false, "CENTRE OUEST", "alice.ancien@vitalrest.com"),  // fermé : sort des groupes
            new("87", "EHPAD LES LILAS", true, "CENTRE OUEST", "inconnu@vitalrest.com"),          // fiche sans compte relié : compte trouvé par son nom
            new("999", "NOUVEAU", true, "IDF", "bruno.secteur@vitalrest.com"),                    // absent de l'inventaire : compte trouvé par son nom
            new("777", "DEUX COMPTES", true, "IDF", "bruno.secteur@vitalrest.com"),               // deux comptes à ce code : laissé de côté
            new("123", "AUCUN COMPTE", true, "IDF", "bruno.secteur@vitalrest.com"),
        ];
        var plan = ServiceSynchronisationOrganisation.Calculer(lignes, Sites, Comptes, Groupes());

        string[] Textes(T type) => plan.Changements.Where(c => c.Type == type).Select(ServiceSynchronisationOrganisation.Texte).ToArray();
        Assert.Equal(["455 - FOYER HORIZON (455) : responsable Alice ANCIEN → Bruno SECTEUR", "999 - NOUVEAU SITE (999) : responsable aucun → Bruno SECTEUR"], Textes(T.Responsable));
        Assert.Equal(["455 - FOYER HORIZON (455) ajouté au groupe REGION_CENTRE_OUEST_SITES", "087 - EHPAD LES LILAS (87) ajouté au groupe REGION_CENTRE_OUEST_SITES",
            "999 - NOUVEAU SITE (999) ajouté au groupe REGION_IDF_SITES"], Textes(T.AjoutGroupe));
        // 440 reste dans IDF mais sort de l'ancien groupe OUEST ; jamais de retrait des REGION_SITES_AGENCES_
        Assert.Equal(["440 - EHPAD SAINTE MONIQUE (440, 440-1, 440-5) retiré du groupe REGION_OUEST_SITES", "455 - FOYER HORIZON (455) retiré du groupe REGION_IDF_SITES",
            "501 - POLE GERIATRIQUE (501) retiré du groupe REGION_CENTRE_OUEST_SITES"], Textes(T.RetraitGroupe));
        // Listes de diffusion : par Exchange, avec les adresses
        var ajout455 = plan.Changements.Single(c => c.Type == T.AjoutGroupe && c.CompteId == "s455");
        Assert.Equal((true, "REGION_CENTRE_OUEST_SITES@vitalrest.com", "site455@vitalrest.com"), (ajout455.ParExchange, ajout455.GroupeAdresse, ajout455.CompteAdresse));
        Assert.Equal(["440-5 → 440 - EHPAD SAINTE MONIQUE", "87 → 087 - EHPAD LES LILAS", "999 → 999 - NOUVEAU SITE"], plan.RapprochementsParNom);
        // Fiches de l'inventaire : responsable de secteur (nom de l'annuaire, ou son adresse s'il est inconnu)
        Assert.Equal([("087", "inconnu@vitalrest.com"), ("440-1", "Bruno SECTEUR"), ("455", "Bruno SECTEUR")],
            plan.Changements.Where(c => c.Type == T.ResponsableInventaire).Select(c => (c.Sites, c.Apres)).ToArray());

        Assert.Equal((9, 8, 1, 5), (plan.Lignes, plan.Ouverts, plan.Fermes, plan.ComptesConcernes));
        Assert.Contains(plan.Problemes, p => p.Contains("1 site(s) ouvert(s) sans compte Microsoft trouvé") && p.EndsWith(": 123."));
        Assert.Contains(plan.Problemes, p => p.Contains("plusieurs comptes à leur code") && p.Contains("777 (777 - CUISINE / 777 - SELF)"));
        // 440-5 (CENTRE OUEST) partage le compte du 440 (IDF) : le site principal fait foi, signalé
        Assert.Contains(plan.Problemes, p => p.Contains("régions différentes") && p.Contains("440, 440-1, 440-5") && p.Contains("440 (IDF)"));
        Assert.Contains(("IDF AL", "REGION_IDF_SITES"), plan.Correspondances);
    }

    [Fact]
    public void Liste_de_diffusion_signalee_non_modifiable_et_responsable_inconnu()
    {
        var plan = ServiceSynchronisationOrganisation.Calculer(
            [new("455", null, true, "SUD EST", "personne@vitalrest.com")], Sites, Comptes, Groupes());
        var ajout = Assert.Single(plan.Changements, c => c.Type == T.AjoutGroupe);
        Assert.Equal(("REGION_SUD_SITES", "groupe synchronisé depuis l'Active Directory local"), (ajout.Apres, ajout.Blocage));
        Assert.Single(plan.Changements, c => c.Type == T.RetraitGroupe && c.Avant == "REGION_IDF_SITES");
        Assert.DoesNotContain(plan.Changements, c => c.Type == T.Responsable);
        Assert.Contains(plan.Problemes, p => p.Contains("introuvable(s) dans l'annuaire") && p.Contains("personne@vitalrest.com"));
    }

    [Fact]
    public void Region_sans_groupe_le_site_reste_dans_ses_groupes()
    {
        var plan = ServiceSynchronisationOrganisation.Calculer([new("455", null, true, "NORD EST", null)], Sites, Comptes, Groupes());
        Assert.DoesNotContain(plan.Changements, c => c.Type is T.AjoutGroupe or T.RetraitGroupe);
        Assert.Contains(plan.Problemes, p => p.Contains("Groupe introuvable") && p.Contains("NORD EST (REGION_NE_SITES)"));
    }

    [Fact]
    public void Lecture_du_fichier_organisation()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Organisation");
        string[] entetes = ["Nom", "Nom Commercial", "Date Ouverture", "Etat", "DO", "Région", "Email CG", "Email RS", "Date Fermeture"];
        for (var i = 0; i < entetes.Length; i++) ws.Cell(1, i + 1).Value = entetes[i];
        object[][] lignes = [["455", "FOYER HORIZON", "01/07/2023", "Open", "FRANCE SUD", "CENTRE OUEST", "cg@vitalrest.com", "Bruno.Secteur@vitalrest.com"],
            ["501-3", "ESCALE", "01/06/2024", "Close", "FRANCE SUD", "CENTRE OUEST", "", "None"],
            ["330", "FERME EN FAIT", "01/01/2020", "Open", "FRANCE NORD", "IDF", "", "rs@vitalrest.com"]];
        for (var l = 0; l < lignes.Length; l++)
            for (var c = 0; c < lignes[l].Length; c++) ws.Cell(l + 2, c + 1).Value = lignes[l][c].ToString();
        ws.Cell(4, 9).Value = new DateTime(2026, 10, 31); // « Open » mais fermé depuis : compté fermé
        ws.Cell(2, 9).Value = "31/12/2027";             // fermeture à venir : reste ouvert
        using var flux = new MemoryStream();
        wb.SaveAs(flux);
        flux.Position = 0;
        Assert.Equal([new LigneOrganisation("455", "FOYER HORIZON", true, "CENTRE OUEST", "bruno.secteur@vitalrest.com"), new LigneOrganisation("501-3", "ESCALE", false, "CENTRE OUEST", null),
                new LigneOrganisation("330", "FERME EN FAIT", false, "IDF", "rs@vitalrest.com")],
            ServiceSynchronisationOrganisation.Lire(flux, new DateOnly(2026, 11, 6)));
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
        public Task AjouterMembreAsync(ChangementOrganisation c, CancellationToken ct = default) { Faites.Add($"+{c.GroupeId} {c.CompteId}"); return Task.CompletedTask; }
        public Task RetirerMembreAsync(ChangementOrganisation c, CancellationToken ct = default) =>
            c.GroupeId == "g-idf" ? throw new ODataError { Error = new MainError { Code = "Authorization_RequestDenied", Message = "Insufficient privileges" } }
            : Task.Run(() => Faites.Add($"-{c.GroupeId} {c.CompteId}"));
        public Task<string> TesterExchangeAsync(string groupe, CancellationToken ct = default) => Task.FromResult("ok");
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
        var plan = await service.AnalyserAsync([new("455", null, true, "SUD EST", "bruno.secteur@vitalrest.com"), new("501", null, false, "CENTRE OUEST", null)]);
        var admin = new Utilisateur("Anthony Lelay", "anthony.lelay@vitalrest.com", true) { EstAdministrateur = true };

        await Assert.ThrowsAsync<ErreurMetier>(() => service.AppliquerAsync(plan, new Utilisateur("Lamine", "lamine@vitalrest.com", true)));
        var resultats = await service.AppliquerAsync(plan, admin);

        // Ajout au groupe SUD non modifiable (synchronisé de l'AD local) : pas tenté
        Assert.Equal(["manager s455=rs-b", "-g-co s501"], ecriture.Faites);
        var echec = Assert.Single(resultats, r => r.Erreur is not null);
        Assert.Contains("Droits d'écriture non accordés", echec.Erreur);
        Assert.Equal("REGION_IDF_SITES", echec.Changement.Avant);
        await using var lecture = fabrique.CreateDbContext();
        Assert.Equal("Bruno SECTEUR", (await lecture.Sites.SingleAsync(x => x.Code == "455")).Responsable);
        Assert.Equal("rs-b", (await lecture.ComptesMicrosoft.SingleAsync(x => x.Id == "s455")).ResponsableId);
        var journal = await lecture.Journal.Where(j => j.Action == "Synchronisation organisation").Select(j => j.Details).ToListAsync();
        Assert.Contains("455 - FOYER HORIZON (455) : responsable Alice ANCIEN → Bruno SECTEUR", journal);
        Assert.Contains(journal, d => d!.StartsWith("Fichier Organisation appliqué : 3 changement(s) fait(s), 1 en échec, 1 non modifiable(s)"));
    }

    [Fact]
    public void Correspondance_par_alias_et_sous_site_par_le_compte_du_site_principal()
    {
        // Nom affiché différent, alias de messagerie REGION_IDF_SITES ; IDF AL va dans le groupe de l'IDF
        List<GroupeRegion> groupes = [new("g-idf", "Région IDF – sites", true, null, new HashSet<string>(), "REGION_IDF_SITES")];
        var cfg = CorrespondanceGroupes.ParDefaut();
        Assert.Equal("REGION_IDF_SITES", cfg.GroupeDe(" IDF AL "));
        Assert.Equal("REGION_SUD_SITES", cfg.GroupeDe("sud est"));
        Assert.True(cfg.Concerne(groupes[0]));
        Assert.False(cfg.Concerne(new("x", "REGION_SITES_AGENCES_IDF", true, null, new HashSet<string>())));

        // Deux comptes au code 619 : le sous-site prend celui relié au site 619 dans l'inventaire
        List<CompteMicrosoft> comptes = [new() { Id = "env", NomAffichage = "619 - Envol Garrigue Ari" }, new() { Id = "diet", NomAffichage = "619 - Diet envol Garrigue ari" }];
        List<Site> sites = [new() { Id = 1, Code = "619", EntraObjectId = "env" }];
        var plan = ServiceSynchronisationOrganisation.Calculer([new("619-1", null, true, "IDF AL", null)], sites, comptes, groupes);
        var ajout = plan.Changements.Single();
        Assert.Equal(("619-1", "env", "Région IDF – sites"), (ajout.Sites, ajout.CompteId, ajout.Apres));
        Assert.DoesNotContain(plan.Problemes, p => p.Contains("plusieurs comptes"));
    }

    [Fact]
    public void Droits_lus_dans_le_jeton()
    {
        static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jeton = $"{B64("{\"alg\":\"none\"}")}.{B64("{\"roles\":[\"Exchange.ManageAsApp\"],\"tid\":\"t\"}")}.sig";
        Assert.Equal(["Exchange.ManageAsApp"], EcritureAnnuaireGraph.RolesDuJeton(jeton));
        Assert.Empty(EcritureAnnuaireGraph.RolesDuJeton("pas-un-jeton"));
    }
}
