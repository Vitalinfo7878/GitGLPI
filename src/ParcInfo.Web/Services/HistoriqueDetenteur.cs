using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

/// <summary>
/// Période pendant laquelle une personne (ou un site) a détenu un matériel : du mouvement qui le lui a remis
/// au mouvement suivant (retour, réattribution, rebut…), ou toujours en cours.
/// </summary>
public sealed record PeriodeDetention(Materiel Materiel, Mouvement? Arrivee, Mouvement? Depart, bool EnCours)
{
    public DateOnly? Du => Arrivee?.Date;
    public DateOnly? Au => Depart?.Date;
}

/// <summary>Historique du matériel d'une personne ou d'un site (fiche « Annuaire »).</summary>
public static class HistoriqueDetenteur
{
    public static async Task<List<PeriodeDetention>> CalculerAsync(ParcDbContext db, IReadOnlyCollection<int> personnes, IReadOnlyCollection<int> sites,
        CancellationToken ct = default)
    {
        bool VersLui(Mouvement x) => (x.DestinationPersonneId is { } p && personnes.Contains(p)) || (x.DestinationSiteId is { } s && sites.Contains(s));

        // Matériel détenu un jour : reçu par un mouvement, ou détenu aujourd'hui
        var ids = await db.Mouvements.Where(x => (x.DestinationPersonneId != null && personnes.Contains(x.DestinationPersonneId.Value))
                                                  || (x.DestinationSiteId != null && sites.Contains(x.DestinationSiteId.Value)))
            .Select(x => x.MaterielId)
            .Union(db.Materiels.Where(m => (m.PersonneId != null && personnes.Contains(m.PersonneId.Value)) || (m.SiteId != null && sites.Contains(m.SiteId.Value)))
                .Select(m => m.Id))
            .ToListAsync(ct);
        var materiels = await db.Materiels.AsNoTracking()
            .Include(m => m.Mouvements).ThenInclude(x => x.DestinationSite)
            .Include(m => m.Mouvements).ThenInclude(x => x.DestinationPersonne)
            .Include(m => m.Mouvements).ThenInclude(x => x.Fiche)
            .AsSplitQuery().Where(m => ids.Contains(m.Id)).ToListAsync(ct);

        var periodes = new List<PeriodeDetention>();
        foreach (var m in materiels)
        {
            var mvts = m.Mouvements.OrderBy(x => x.Date ?? DateOnly.MinValue).ThenBy(x => x.Numero).ToList();
            var detientAujourdhui = (m.PersonneId is { } pid && personnes.Contains(pid)) || (m.SiteId is { } sid && sites.Contains(sid));
            var trouve = false;
            for (var i = 0; i < mvts.Count; i++)
            {
                if (!VersLui(mvts[i])) continue;
                // Mouvements successifs vers le même détenteur (doublon, correction) : une seule période
                if (i > 0 && VersLui(mvts[i - 1])) continue;
                var depart = mvts.Skip(i + 1).FirstOrDefault(x => !VersLui(x));
                periodes.Add(new PeriodeDetention(m, mvts[i], depart, depart is null && detientAujourdhui));
                trouve = true;
            }
            // Détenu sans mouvement enregistré (inventaire repris de l'Excel)
            if (!trouve && detientAujourdhui) periodes.Add(new PeriodeDetention(m, null, null, true));
        }
        return periodes.OrderByDescending(p => p.EnCours).ThenByDescending(p => p.Du ?? DateOnly.MinValue).ThenBy(p => p.Materiel.NumInterne).ToList();
    }
}
