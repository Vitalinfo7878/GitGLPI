using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

/// <summary>
/// Suivi du rapatriement de l'ancien matériel d'une demande de remplacement (ticket interne) : relances datées,
/// identification du matériel, puis « Matériel rapatrié » (entrée dans le SI et clôture) ou clôture sans rapatriement.
/// </summary>
public sealed class ServiceRapatriements(IDbContextFactory<ParcDbContext> dbFactory, ServiceMouvements mouvements)
{
    /// <summary>Actions proposées dans le suivi (les deux dernières sont posées par la clôture).</summary>
    public static IReadOnlyList<SuiviRapatriement> SuivisSaisissables { get; } =
        [SuiviRapatriement.RelanceTelephone, SuiviRapatriement.RelanceMail, SuiviRapatriement.EtiquetteEnvoyee, SuiviRapatriement.Note];

    public static bool EstRelance(SuiviRapatriement? s) => s is SuiviRapatriement.RelanceTelephone or SuiviRapatriement.RelanceMail;

    /// <summary>État affiché : en cours, rapatrié, ou clos (sans rapatriement, demande refusée…).</summary>
    public static string Etat(Ticket t) => t.EstOuvert ? "En cours"
        : t.Messages.Any(m => m.Suivi == SuiviRapatriement.Rapatrie) ? "Rapatrié" : "Clos";

    public async Task<Ticket?> ChargerAsync(int id, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstAdminTickets) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tickets.AsNoTracking()
            .Include(t => t.Messages)
            .Include(t => t.Materiel).ThenInclude(m => m!.Site)
            .Include(t => t.Materiel).ThenInclude(m => m!.Personne)
            .Include(t => t.Demande)
            .FirstOrDefaultAsync(t => t.Id == id && t.Interne, ct);
    }

    /// <summary>Date d'une action saisie (jour choisi) : l'heure actuelle si c'est aujourd'hui, midi (heure de Paris) sinon.</summary>
    public static DateTime DateAction(DateOnly jour, DateTime maintenantUtc)
    {
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        if (jour == DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(maintenantUtc, paris))) return maintenantUtc;
        return TimeZoneInfo.ConvertTimeToUtc(jour.ToDateTime(new TimeOnly(12, 0)), paris);
    }

    private static async Task<Ticket> RapatriementAsync(ParcDbContext db, int id, Utilisateur u, CancellationToken ct)
    {
        if (!u.EstAdminTickets) throw new ErreurMetier("Réservé au service informatique.");
        var t = await ServiceTickets.ChargerAsync(db, id, u, ct);
        if (!t.Interne) throw new ErreurMetier("Ce n'est pas un rapatriement.");
        return t;
    }

    private static void VerifierJour(DateOnly jour, DateTime maintenant)
    {
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        if (jour > DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(maintenant, paris))) throw new ErreurMetier("La date ne peut pas être dans le futur.");
    }

    private static string? Vide(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Relance téléphonique, relance mail, étiquette envoyée ou note, à la date choisie.</summary>
    public async Task NoterSuiviAsync(int id, SuiviRapatriement type, DateOnly jour, string? commentaire, Utilisateur u, CancellationToken ct = default)
    {
        if (!SuivisSaisissables.Contains(type)) throw new ErreurMetier("Action de suivi inconnue.");
        if (type == SuiviRapatriement.Note && Vide(commentaire) is null) throw new ErreurMetier("Écrivez la note.");
        if (commentaire?.Trim().Length > 2000) throw new ErreurMetier("Commentaire trop long (2000 caractères maximum).");
        var maintenant = DateTime.UtcNow;
        VerifierJour(jour, maintenant);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await RapatriementAsync(db, id, u, ct);
        if (!t.EstOuvert) throw new ErreurMetier("Ce rapatriement est terminé : rouvrez-le pour ajouter un suivi.");
        t.Messages.Add(new TicketMessage
        {
            Date = DateAction(jour, maintenant), Auteur = u.Nom, AuteurEmail = u.Email, Interne = true, Suivi = type,
            Texte = Vide(commentaire) ?? type.Libelle(),
        });
        if (t.Statut == StatutTicket.Nouveau) ServiceTickets.ChangerStatut(t, StatutTicket.EnCours, u, maintenant);
        t.AssigneA ??= u.Nom;
        t.ModifieLe = maintenant;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Identifie (ou corrige) l'ancien matériel à rapatrier : n° interne de l'inventaire.</summary>
    public async Task DefinirMaterielAsync(int id, string? numInterne, Utilisateur u, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await RapatriementAsync(db, id, u, ct);
        var avant = t.MaterielReference;
        if (string.Equals(Vide(numInterne), avant, StringComparison.OrdinalIgnoreCase)) return;
        await ServiceTickets.LierMaterielAsync(db, t, numInterne, ct);
        if (t.MaterielReference is not null && t.MaterielId is null) throw new ErreurMetier($"{t.MaterielReference} est introuvable dans l'inventaire.");
        var maintenant = DateTime.UtcNow;
        if (t.DemandeId is { } demandeId)
        {
            try { await ServiceTickets.CorrigerRapatriementAsync(db, t, demandeId, avant, u, maintenant, ct); }
            catch (ErreurFormulaire ex) { throw new ErreurMetier(string.Join(" ", ex.Erreurs.Values)); }
        }
        else t.Messages.Add(ServiceTickets.Trace(u, $"Ancien matériel : {avant ?? "à identifier"} → {t.MaterielReference ?? "à identifier"}", maintenant));
        t.ModifieLe = maintenant;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Matériel rapatrié : enregistre au besoin son entrée dans le SI (mouvement « Entrée (dans le SI) » à la date de réception),
    /// puis clôt le rapatriement. Renvoie le message à afficher.
    /// </summary>
    public async Task<string> MarquerRapatrieAsync(int id, DateOnly jour, bool enregistrerEntree, string? commentaire, Utilisateur u, CancellationToken ct = default)
    {
        if (commentaire?.Trim().Length > 2000) throw new ErreurMetier("Commentaire trop long (2000 caractères maximum).");
        var maintenant = DateTime.UtcNow;
        VerifierJour(jour, maintenant);
        int numero; int? materielId; string? reference;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var t = await RapatriementAsync(db, id, u, ct);
            if (!t.EstOuvert) throw new ErreurMetier("Ce rapatriement est déjà terminé.");
            (numero, materielId, reference) = (t.Numero, t.MaterielId, t.MaterielReference);
        }
        if (enregistrerEntree && materielId is null) throw new ErreurMetier("Identifiez d'abord l'ancien matériel pour enregistrer son entrée dans le SI.");

        // Entrée dans le SI (le mouvement est aussi noté sur le rapatriement, encore ouvert)
        string? entree = null;
        if (enregistrerEntree && materielId is { } mid)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var statut = await db.Materiels.Where(m => m.Id == mid).Select(m => m.Statut).FirstAsync(ct);
            if (statut != StatutMateriel.EnStock)
            {
                var r = await mouvements.CreerMouvementsAsync(new DemandeMouvement
                {
                    Type = TypeMouvement.Restitution, MaterielIds = [mid], Date = jour,
                    DocumentReference = $"Rapatriement n° {numero}", Commentaire = Vide(commentaire),
                }, u, ct);
                entree = $"entrée dans le SI enregistrée (mouvement n° {r.Numeros[0]}{(r.Fiche is null ? "" : $", fiche {r.Fiche.Code}")})";
            }
            else entree = "déjà en stock : pas de nouveau mouvement";
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var t = await RapatriementAsync(db, id, u, ct);
            var texte = $"Matériel {reference ?? ""} rapatrié le {jour:dd/MM/yyyy}".Replace("  ", " ")
                + (Vide(commentaire) is { } c ? $" : {c}" : "");
            t.Messages.Add(new TicketMessage
            {
                Date = DateAction(jour, maintenant), Auteur = u.Nom, AuteurEmail = u.Email, Interne = true, Suivi = SuiviRapatriement.Rapatrie,
                Texte = texte + (entree is null ? "" : $" ({entree})"),
            });
            t.Solution = texte;
            t.ModeResolution ??= ModeResolution.Autre;
            t.AssigneA ??= u.Nom;
            ServiceTickets.ChangerStatut(t, StatutTicket.Clos, u, maintenant, "Matériel rapatrié");
            t.ModifieLe = maintenant;
            db.Journal.Add(new Journal
            {
                Date = maintenant, Utilisateur = u.Signature, Action = "Rapatriement terminé", MaterielId = t.MaterielId,
                Details = JournalInventaire.Tronquer($"n° {t.Numero} : {texte}{(entree is null ? "" : $" ; {entree}")}"),
            });
            await db.SaveChangesAsync(ct);
        }
        return $"Rapatriement n° {numero} terminé" + (entree is null ? "." : $" : {entree}.");
    }

    /// <summary>Clôture sans rapatriement (matériel perdu, laissé sur place…), avec le motif.</summary>
    public async Task CloreSansRapatriementAsync(int id, string? motif, Utilisateur u, CancellationToken ct = default)
    {
        var m = Vide(motif) ?? throw new ErreurMetier("Indiquez le motif.");
        if (m.Length > 2000) throw new ErreurMetier("Motif trop long (2000 caractères maximum).");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await RapatriementAsync(db, id, u, ct);
        if (!t.EstOuvert) throw new ErreurMetier("Ce rapatriement est déjà terminé.");
        var maintenant = DateTime.UtcNow;
        t.Messages.Add(new TicketMessage
        {
            Date = maintenant, Auteur = u.Nom, AuteurEmail = u.Email, Interne = true, Suivi = SuiviRapatriement.SansRapatriement, Texte = m,
        });
        t.Solution = $"Clos sans rapatriement : {m}";
        t.ModeResolution ??= ModeResolution.Autre;
        ServiceTickets.ChangerStatut(t, StatutTicket.Clos, u, maintenant, "Clos sans rapatriement");
        t.ModifieLe = maintenant;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Reprend le suivi d'un rapatriement terminé par erreur (le mouvement d'entrée éventuel reste : il s'annule depuis Mouvements).</summary>
    public async Task RouvrirAsync(int id, Utilisateur u, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await RapatriementAsync(db, id, u, ct);
        if (t.EstOuvert) return;
        var maintenant = DateTime.UtcNow;
        // Les marques de fin ne comptent plus
        foreach (var x in t.Messages.Where(x => x.Suivi is SuiviRapatriement.Rapatrie or SuiviRapatriement.SansRapatriement)) x.Suivi = SuiviRapatriement.Note;
        t.Solution = null;
        ServiceTickets.ChangerStatut(t, StatutTicket.EnCours, u, maintenant, "Rapatriement rouvert");
        t.ModifieLe = maintenant;
        await db.SaveChangesAsync(ct);
    }
}
