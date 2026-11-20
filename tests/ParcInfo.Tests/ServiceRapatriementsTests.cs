using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

[Collection("Chiffrement")]
public sealed class ServiceRapatriementsTests : IDisposable
{
    private readonly BaseDeTest _fabrique = new();
    private readonly ServiceRapatriements _service;

    private static readonly Utilisateur Informatique = new("Service Informatique", "informatique@vitalrest.com", true) { EstAdminTickets = true };

    public ServiceRapatriementsTests()
    {
        _service = new ServiceRapatriements(_fabrique, new ServiceMouvements(_fabrique, new GenerateurFiche(), NullLogger<ServiceMouvements>.Instance));
    }

    public void Dispose() => _fabrique.Dispose();

    private static DateOnly Aujourdhui => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris")));

    /// <summary>Rapatriement d'un PC attribué à Camille ; renvoie (ticket, PC).</summary>
    private (int Ticket, int Pc) Donnees(bool avecMateriel = true)
    {
        using var db = _fabrique.CreateDbContext();
        var p = new Personne { NomComplet = "Camille TEST", Email = "camille.test@vitalrest.com" };
        var pc = new Materiel { NumInterne = "PC0001", Type = TypeMateriel.PcPortable, Statut = StatutMateriel.Attribue, Personne = p };
        db.AddRange(p, pc);
        db.Mouvements.Add(new Mouvement { Numero = 100, Materiel = pc, Type = TypeMouvement.Attribution, DestinationPersonne = p });
        var t = new Ticket
        {
            Numero = 7, Interne = true, Titre = "Rapatriement de l'ancien matériel PC0001", Description = "…", Lieu = "Site 087",
            DemandeurNom = "Camille TEST", DemandeurEmail = "camille.test@vitalrest.com", DemandeurTelephone = "0600000000",
            SaisiePar = ServiceTickets.AuteurApplication, Statut = StatutTicket.Nouveau, CreeLe = DateTime.UtcNow, ModifieLe = DateTime.UtcNow,
            Materiel = avecMateriel ? pc : null, MaterielReference = avecMateriel ? "PC0001" : null,
        };
        db.Tickets.Add(t);
        db.SaveChanges();
        return (t.Id, pc.Id);
    }

    [Fact]
    public async Task Relances_datees_dans_le_suivi()
    {
        var (id, _) = Donnees();
        var hier = Aujourdhui.AddDays(-1);
        await _service.NoterSuiviAsync(id, SuiviRapatriement.RelanceTelephone, hier, "Messagerie, rappeler jeudi", Informatique);
        await _service.NoterSuiviAsync(id, SuiviRapatriement.RelanceMail, Aujourdhui, null, Informatique);

        await Assert.ThrowsAsync<ErreurMetier>(() => _service.NoterSuiviAsync(id, SuiviRapatriement.RelanceMail, Aujourdhui.AddDays(1), null, Informatique));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.NoterSuiviAsync(id, SuiviRapatriement.Note, Aujourdhui, " ", Informatique));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.NoterSuiviAsync(id, SuiviRapatriement.Rapatrie, Aujourdhui, null, Informatique));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.NoterSuiviAsync(id, SuiviRapatriement.RelanceMail, Aujourdhui, null, new Utilisateur("Camille", "camille.test@vitalrest.com", false)));

        var t = (await _service.ChargerAsync(id, Informatique))!;
        var relances = t.Messages.Where(m => ServiceRapatriements.EstRelance(m.Suivi)).OrderBy(m => m.Date).ToList();
        Assert.Equal([SuiviRapatriement.RelanceTelephone, SuiviRapatriement.RelanceMail], relances.Select(m => m.Suivi!.Value));
        Assert.Equal(hier, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(relances[0].Date, TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"))));
        Assert.Equal("Messagerie, rappeler jeudi", relances[0].Texte);
        Assert.Equal("Relance mail", relances[1].Texte);
        Assert.Equal((StatutTicket.EnCours, "Service Informatique"), (t.Statut, t.AssigneA));
        Assert.Equal("En cours", ServiceRapatriements.Etat(t));
    }

    [Fact]
    public async Task Materiel_rapatrie_enregistre_l_entree_et_termine()
    {
        var (id, pc) = Donnees();
        var message = await _service.MarquerRapatrieAsync(id, Aujourdhui, enregistrerEntree: true, "Écran rayé", Informatique);
        Assert.Contains("entrée dans le SI enregistrée", message);

        await using var db = _fabrique.CreateDbContext();
        var m = await db.Materiels.FindAsync(pc);
        Assert.Equal((StatutMateriel.EnStock, (int?)null), (m!.Statut, m.PersonneId));
        var entree = await db.Mouvements.SingleAsync(x => x.MaterielId == pc && x.Type == TypeMouvement.Restitution);
        Assert.Equal(Aujourdhui, entree.Date);
        Assert.Equal("Rapatriement n° 7", entree.DocumentReference);

        var t = (await _service.ChargerAsync(id, Informatique))!;
        Assert.Equal(StatutTicket.Clos, t.Statut);
        Assert.Equal("Rapatrié", ServiceRapatriements.Etat(t));
        Assert.StartsWith($"Matériel PC0001 rapatrié le {Aujourdhui:dd/MM/yyyy} : Écran rayé", t.Solution);
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.MarquerRapatrieAsync(id, Aujourdhui, false, null, Informatique));
    }

    [Fact]
    public async Task Entree_impossible_sans_materiel_identifie_puis_identification()
    {
        var (id, _) = Donnees(avecMateriel: false);
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.MarquerRapatrieAsync(id, Aujourdhui, enregistrerEntree: true, null, Informatique));
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.DefinirMaterielAsync(id, "PC9999", Informatique));

        await _service.DefinirMaterielAsync(id, "pc0001", Informatique);
        var t = (await _service.ChargerAsync(id, Informatique))!;
        Assert.Equal("PC0001", t.Materiel?.NumInterne);
        Assert.Contains(t.Messages, m => m.Texte.Contains("PC0001"));
    }

    [Fact]
    public async Task Clos_sans_rapatriement_puis_rouvert()
    {
        var (id, _) = Donnees();
        await Assert.ThrowsAsync<ErreurMetier>(() => _service.CloreSansRapatriementAsync(id, "  ", Informatique));
        await _service.CloreSansRapatriementAsync(id, "Matériel perdu par le site", Informatique);
        var t = (await _service.ChargerAsync(id, Informatique))!;
        Assert.Equal(("Clos", "Clos sans rapatriement : Matériel perdu par le site"), (ServiceRapatriements.Etat(t), t.Solution));

        await _service.RouvrirAsync(id, Informatique);
        t = (await _service.ChargerAsync(id, Informatique))!;
        Assert.Equal(("En cours", (string?)null), (ServiceRapatriements.Etat(t), t.Solution));
        await _service.NoterSuiviAsync(id, SuiviRapatriement.RelanceMail, Aujourdhui, null, Informatique);
    }
}
