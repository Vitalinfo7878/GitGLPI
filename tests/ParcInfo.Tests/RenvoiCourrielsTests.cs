using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public sealed class RenvoiCourrielsTests : IDisposable
{
    private readonly BaseDeTest _fabrique = new();

    public void Dispose() => _fabrique.Dispose();

    /// <summary>Messagerie en panne tant que <see cref="EnPanne"/> est vrai.</summary>
    private sealed class EnvoiInstable : IEnvoiCourriel
    {
        public bool EnPanne { get; set; } = true;
        public List<string> Envoyes { get; } = [];

        public Task EnvoyerAsync(string destinataire, string sujet, string corpsHtml, CancellationToken ct = default, IReadOnlyList<PieceJointe>? pieces = null)
        {
            if (EnPanne) throw new InvalidOperationException("attachments: StartArray expected");
            Envoyes.Add($"{destinataire} : {sujet}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Courriels_en_echec_renvoyes_une_seule_fois()
    {
        var envoi = new EnvoiInstable();
        var notifications = new ServiceNotifications(envoi, _fabrique, new OptionsMessagerie { UrlApplication = "https://parc.test" }, NullLogger<ServiceNotifications>.Instance);
        var d = new Demande { Numero = 600, DemandeurNom = "Camille TEST", Objets = "PC portable", DemandeurEmail = "camille.test@vitalrest.com", AdresseLivraison = "Site 087" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => notifications.DemandeRealiseeAsync(d));
        await using (var db = _fabrique.CreateDbContext())
        {
            // Fiche d'attribution en échec : pas renvoyée d'ici (elle repart d'elle-même)
            db.Courriels.Add(new Courriel { Destinataire = "site087@vitalrest.com", Sujet = "Fiche d'attribution", CorpsHtml = "x", Erreur = "panne", PiecesJointes = "FIA.pdf", EnvoyeLe = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        List<int> ids;
        await using (var db = _fabrique.CreateDbContext())
            ids = await db.Courriels.Select(c => c.Id).ToListAsync();

        Assert.Equal(0, await notifications.RenvoyerAsync(ids)); // toujours en panne : rien ne part, une nouvelle trace d'échec
        envoi.EnPanne = false;
        Assert.Equal(1, await notifications.RenvoyerAsync(ids));
        Assert.Equal(0, await notifications.RenvoyerAsync(ids)); // déjà renvoyé
        Assert.Equal(["camille.test@vitalrest.com : Demande informatique n° 600 réalisée"], envoi.Envoyes);

        await using var lecture = _fabrique.CreateDbContext();
        var traces = await lecture.Courriels.OrderBy(c => c.Id).ToListAsync();
        Assert.NotNull(traces[0].RenvoyeLe);
        Assert.Null(traces[1].RenvoyeLe);
        Assert.Contains(traces, c => c.Erreur is null && c.Destinataire == "camille.test@vitalrest.com");
    }
}
