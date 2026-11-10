using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;
using D = ParcInfo.Web.Services.DonneesActivite;

namespace ParcInfo.Tests;

public sealed class ServiceActiviteTests : IDisposable
{
    private readonly BaseDeTest _fabrique = new();
    public void Dispose() => _fabrique.Dispose();

    private static DateTime Utc(int a, int m, int j, int h = 10) => new(a, m, j, h, 0, 0, DateTimeKind.Utc);

    private static D Donnees() => new(
        [
            new(Utc(2026, 10, 5), Utc(2026, 10, 5, 12), Utc(2026, 10, 7), 5, "Imprimante / scanner"),
            new(Utc(2026, 10, 20), Utc(2026, 10, 21), null, null, "PC / poste de travail"),          // ouvert fin octobre
            new(Utc(2026, 10, 31, 23), null, null, null, "Autre : Badgeuse"),                         // 31/10 23 h UTC = 1er novembre à Paris
            new(Utc(2026, 9, 28), Utc(2026, 9, 28, 11), Utc(2026, 10, 2), 3, "Écran"),                 // résolu en octobre
        ],
        [
            new(Utc(2026, 10, 1), Utc(2026, 10, 3), StatutValidation.Validee, StatutRealisation.Realisee, Utc(2026, 10, 11), "PC portable;Ecran"),
            new(Utc(2026, 10, 15), Utc(2026, 10, 16), StatutValidation.Refusee, StatutRealisation.AFaire, null, "Imprimante"),
            new(Utc(2026, 10, 30), null, StatutValidation.EnAttente, StatutRealisation.AFaire, null, "PC portable"),
        ],
        [new(new DateOnly(2026, 10, 2), TypeMouvement.Achat), new(new DateOnly(2026, 10, 2), TypeMouvement.Achat),
         new(new DateOnly(2026, 10, 9), TypeMouvement.Attribution), new(new DateOnly(2026, 10, 20), TypeMouvement.MiseAuRebut),
         new(new DateOnly(2026, 11, 3), TypeMouvement.Restitution)],
        [new(Utc(2026, 9, 1), TypeMateriel.PcPortable, null), new(Utc(2026, 9, 1), TypeMateriel.Ecran, new DateOnly(2026, 10, 20)),
         new(Utc(2026, 10, 2), TypeMateriel.PcPortable, null), new(Utc(2026, 10, 2), TypeMateriel.PcPortable, null)]);

    [Fact]
    public void Indicateurs_d_un_mois()
    {
        var o = ServiceActivite.Calculer(Donnees(), new DateOnly(2026, 10, 1));
        Assert.Equal((2, 2, 1), (o.TicketsCrees, o.TicketsResolus, o.TicketsOuvertsFin));
        Assert.Equal(13.0, o.PriseEnChargeHeures);                    // tickets créés en octobre : (2 h + 24 h) / 2
        Assert.Equal((3.0, 4.0), (o.ResolutionJours, o.Satisfaction)); // résolus en octobre : (2 j + 4 j) / 2 ; notes (5 + 3) / 2
        Assert.Equal((3, 1, 1, 1, 1), (o.DemandesRecues, o.DemandesValidees, o.DemandesRefusees, o.DemandesRealisees, o.DemandesEnAttenteFin));
        Assert.Equal((1.5, 10.0), (o.ValidationJours, o.RealisationJours));
        Assert.Equal((3, 2, 1, 0, 1, 4), (o.ParcFin, o.Achats, o.Attributions, o.Restitutions, o.Rebuts, o.Mouvements));

        var n = ServiceActivite.Calculer(Donnees(), new DateOnly(2026, 11, 15));
        Assert.Equal((new DateOnly(2026, 11, 1), 1, 1), (n.Mois, n.TicketsCrees, n.Restitutions)); // le ticket du 31/10 23 h UTC compte en novembre

        var evolution = ServiceActivite.Evolution(Donnees(), new DateOnly(2026, 11, 1), 3);
        Assert.Equal([new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1)], evolution.Select(e => e.Mois));
        Assert.Equal([2, 3, 3], evolution.Select(e => e.ParcFin));

        var detail = ServiceActivite.Detail(Donnees(), new DateOnly(2026, 10, 1));
        Assert.Equal([("PC portable", 2), ("Ecran", 1), ("Imprimante", 1)], detail.DemandesParObjet.Select(b => (b.Libelle, b.Nombre)));
        Assert.Equal([("PC portable", 3)], detail.ParcParType.Select(b => (b.Libelle, b.Nombre)));
    }

    [Fact]
    public async Task Rapport_excel_reserve_aux_administrateurs()
    {
        await using (var db = _fabrique.CreateDbContext())
        {
            db.Tickets.AddRange(
                new Ticket { Numero = 1, Titre = "Imprimante", Description = "x", DemandeurNom = "Camille", DemandeurEmail = "c@v.com", DemandeurTelephone = "0", Lieu = "Siège", SaisiePar = "c@v.com", CreeLe = Utc(2026, 10, 5), ModifieLe = Utc(2026, 10, 5) },
                new Ticket { Numero = 2, Titre = "Rapatriement", Description = "x", DemandeurNom = "IT", DemandeurEmail = "i@v.com", DemandeurTelephone = "0", Lieu = "Siège", SaisiePar = "i@v.com", CreeLe = Utc(2026, 10, 6), ModifieLe = Utc(2026, 10, 6), Interne = true });
            await db.SaveChangesAsync();
        }
        var service = new ServiceActivite(_fabrique);
        await Assert.ThrowsAsync<ErreurMetier>(() => service.RapportAsync(new DateOnly(2026, 10, 1), new Utilisateur("Lamine", "l@v.com", true)));
        var octets = await service.RapportAsync(new DateOnly(2026, 10, 1), new Utilisateur("Anthony", "a@v.com", true) { EstAdministrateur = true });
        using var wb = new XLWorkbook(new MemoryStream(octets));
        Assert.Equal(["Synthèse", "Répartitions", "Tickets", "Demandes", "Mouvements"], wb.Worksheets.Select(w => w.Name));
        Assert.Contains("Octobre 2026", wb.Worksheet("Synthèse").Cell(1, 1).GetString());
        Assert.Equal(1, wb.Worksheet("Tickets").RowsUsed().Count() - 1); // le ticket interne n'est pas compté
    }

    [Fact]
    public async Task Rapport_envoye_aux_abonnes_une_fois_en_debut_de_mois()
    {
        await using (var db = _fabrique.CreateDbContext())
        {
            db.DroitsAcces.Add(new DroitAcces { Email = "thomas.verchere@groupe-vitalrest.com", NotifRapportMensuel = true });
            await db.SaveChangesAsync();
        }
        var notifications = new ServiceNotifications(new EnvoiCourrielSimule(NullLogger<EnvoiCourrielSimule>.Instance), _fabrique,
            new OptionsMessagerie { UrlApplication = "https://parc.test" }, NullLogger<ServiceNotifications>.Instance);
        var service = new ServiceActivite(_fabrique, notifications);

        Assert.Equal(0, await service.EnvoyerRapportDuMoisEcouleAsync(new DateOnly(2026, 11, 10))); // après la première semaine : rien
        Assert.Equal(1, await service.EnvoyerRapportDuMoisEcouleAsync(new DateOnly(2026, 12, 1)));  // rapport de novembre
        Assert.Equal(0, await service.EnvoyerRapportDuMoisEcouleAsync(new DateOnly(2026, 12, 2)));  // une seule fois
        await Assert.ThrowsAsync<ErreurMetier>(() => service.EnvoyerMaintenantAsync(new DateOnly(2026, 11, 1), new Utilisateur("Lamine", "l@v.com", true)));

        await using var lecture = _fabrique.CreateDbContext();
        var mail = Assert.Single(await lecture.Courriels.ToListAsync());
        Assert.Equal(("thomas.verchere@groupe-vitalrest.com", "Rapport d'activité informatique – Novembre 2026", "Rapport activite informatique 2026-11.xlsx"),
            (mail.Destinataire, mail.Sujet, mail.PiecesJointes));
        Assert.Contains("Tickets créés / résolus", System.Net.WebUtility.HtmlDecode(mail.CorpsHtml));
    }
}
