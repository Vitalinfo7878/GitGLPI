using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

/// <summary>Réponses au formulaire « Signaler un problème ».</summary>
public sealed class FormulaireTicket
{
    public const int LongueurDescription = 4000;

    public string? DemandeurNom { get; set; }
    public string? DemandeurEmail { get; set; }
    public string? DemandeurTelephone { get; set; }
    public string? Lieu { get; set; }
    public CategorieTicket? Categorie { get; set; }
    public ImpactTicket? Impact { get; set; }
    public PorteeTicket? Portee { get; set; }
    public string? Titre { get; set; }
    public string? Description { get; set; }
    public string? DejaEssaye { get; set; }
    public string? MaterielReference { get; set; }
    public string? Disponibilites { get; set; }

    /// <summary>Erreurs par champ (nom de propriété → message). Vide si le formulaire est complet.</summary>
    public Dictionary<string, string> Valider()
    {
        var e = new Dictionary<string, string>();
        void Obligatoire(string champ, object? valeur)
        {
            if (valeur is null || valeur is string s && string.IsNullOrWhiteSpace(s)) e[champ] = FormulaireDemande.MessageObligatoire;
        }

        Obligatoire(nameof(DemandeurNom), DemandeurNom);
        Obligatoire(nameof(DemandeurEmail), DemandeurEmail);
        if (!e.ContainsKey(nameof(DemandeurEmail)) && !FormulaireDemande.EmailValide(DemandeurEmail!)) e[nameof(DemandeurEmail)] = "Adresse mail invalide.";
        Obligatoire(nameof(DemandeurTelephone), DemandeurTelephone);
        if (!e.ContainsKey(nameof(DemandeurTelephone)) && Regex.Replace(DemandeurTelephone!, @"\D", "").Length < 10)
            e[nameof(DemandeurTelephone)] = "Numéro de téléphone incomplet (10 chiffres minimum).";
        Obligatoire(nameof(Lieu), Lieu);
        Obligatoire(nameof(Categorie), Categorie);
        Obligatoire(nameof(Impact), Impact);
        Obligatoire(nameof(Portee), Portee);
        Obligatoire(nameof(Titre), Titre);
        Obligatoire(nameof(Description), Description);
        foreach (var (champ, valeur, max) in new (string, string?, int)[]
        {
            (nameof(DemandeurNom), DemandeurNom, 200), (nameof(DemandeurEmail), DemandeurEmail, 200),
            (nameof(DemandeurTelephone), DemandeurTelephone, 50), (nameof(Lieu), Lieu, 400), (nameof(Titre), Titre, 150),
            (nameof(Description), Description, LongueurDescription), (nameof(DejaEssaye), DejaEssaye, 2000),
            (nameof(MaterielReference), MaterielReference, 60), (nameof(Disponibilites), Disponibilites, 400),
        })
            if (valeur?.Trim().Length > max && !e.ContainsKey(champ)) e[champ] = $"Réponse trop longue ({max} caractères maximum).";
        return e;
    }
}

/// <summary>Traitement et analyse d'un ticket par le service informatique.</summary>
public sealed class MiseAJourTicket
{
    public StatutTicket Statut { get; set; }
    public PrioriteTicket Priorite { get; set; }
    public CategorieTicket Categorie { get; set; }
    public string? AssigneA { get; set; }
    public string? MaterielReference { get; set; }
    public string? Diagnostic { get; set; }
    public CauseTicket? Cause { get; set; }
    public string? Solution { get; set; }
    public ModeResolution? ModeResolution { get; set; }
    public int? TempsPasseMinutes { get; set; }

    public static MiseAJourTicket Depuis(Ticket t) => new()
    {
        Statut = t.Statut, Priorite = t.Priorite, Categorie = t.Categorie, AssigneA = t.AssigneA, MaterielReference = t.MaterielReference,
        Diagnostic = t.Diagnostic, Cause = t.Cause, Solution = t.Solution, ModeResolution = t.ModeResolution, TempsPasseMinutes = t.TempsPasseMinutes,
    };

    /// <summary>Un ticket ne peut être résolu ou clos qu'avec son analyse (cause, solution, mode de résolution).</summary>
    public Dictionary<string, string> Valider()
    {
        var e = new Dictionary<string, string>();
        if (Statut is StatutTicket.Resolu or StatutTicket.Clos)
        {
            const string requis = "Obligatoire pour résoudre le ticket.";
            if (Cause is null) e[nameof(Cause)] = requis;
            if (string.IsNullOrWhiteSpace(Solution)) e[nameof(Solution)] = requis;
            if (ModeResolution is null) e[nameof(ModeResolution)] = requis;
        }
        if (TempsPasseMinutes is < 0 or > 100_000) e[nameof(TempsPasseMinutes)] = "Durée invalide.";
        foreach (var (champ, valeur, max) in new (string, string?, int)[]
        {
            (nameof(AssigneA), AssigneA, 200), (nameof(MaterielReference), MaterielReference, 60),
            (nameof(Diagnostic), Diagnostic, 4000), (nameof(Solution), Solution, 4000),
        })
            if (valeur?.Trim().Length > max && !e.ContainsKey(champ)) e[champ] = $"Texte trop long ({max} caractères maximum).";
        return e;
    }
}

public sealed class ServiceTickets(IDbContextFactory<ParcDbContext> dbFactory)
{
    /// <summary>Peut voir le ticket : son auteur / demandeur et les administrateurs des tickets.</summary>
    public static bool PeutVoir(Utilisateur u, Ticket t) => u.EstAdminTickets || EstDemandeur(u, t);

    public static bool EstDemandeur(Utilisateur u, Ticket t) => u.AEmail(t.SaisiePar) || u.AEmail(t.DemandeurEmail);

    public async Task<Ticket> CreerAsync(FormulaireTicket f, Utilisateur auteur, CancellationToken ct = default)
    {
        var erreurs = f.Valider();
        if (erreurs.Count > 0) throw new ErreurFormulaire(erreurs);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var maintenant = DateTime.UtcNow;
        var t = new Ticket
        {
            CreeLe = maintenant,
            ModifieLe = maintenant,
            SaisiePar = auteur.Email,
            DemandeurNom = f.DemandeurNom!.Trim(),
            DemandeurEmail = f.DemandeurEmail!.Trim().ToLowerInvariant(),
            DemandeurTelephone = f.DemandeurTelephone!.Trim(),
            Lieu = f.Lieu!.Trim(),
            Categorie = f.Categorie!.Value,
            Impact = f.Impact!.Value,
            Portee = f.Portee!.Value,
            Titre = f.Titre!.Trim(),
            Description = f.Description!.Trim(),
            DejaEssaye = Vide(f.DejaEssaye),
            Disponibilites = Vide(f.Disponibilites),
            Priorite = Ticket.PrioriteProposee(f.Impact.Value, f.Portee.Value),
            Statut = StatutTicket.Nouveau,
        };
        await LierMaterielAsync(db, t, f.MaterielReference, ct);

        // Numérotation à la suite (avec une nouvelle tentative si deux tickets arrivent en même temps)
        for (var essai = 0; ; essai++)
        {
            t.Numero = (await db.Tickets.MaxAsync(x => (int?)x.Numero, ct) ?? 0) + 1;
            db.Tickets.Add(t);
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException) when (essai < 3)
            {
                db.Entry(t).State = EntityState.Detached;
            }
        }
        db.Journal.Add(new Journal { Date = maintenant, Utilisateur = auteur.Signature, Action = "Nouveau ticket", Details = $"n° {t.Numero} – {t.Titre}", MaterielId = t.MaterielId });
        await db.SaveChangesAsync(ct);
        return t;
    }

    /// <summary>Message du demandeur ou du service informatique ; une note interne n'est visible que des administrateurs.</summary>
    public async Task AjouterMessageAsync(int id, string? texte, bool interne, Utilisateur u, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(texte)) throw new ErreurMetier("Le message est vide.");
        if (texte.Trim().Length > 4000) throw new ErreurMetier("Message trop long (4000 caractères maximum).");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await ChargerAsync(db, id, u, ct);
        if (interne && !u.EstAdminTickets) throw new ErreurMetier("Réservé au service informatique.");
        if (t.Statut == StatutTicket.Clos) throw new ErreurMetier("Ce ticket est clos : ouvrez un nouveau ticket si besoin.");

        var maintenant = DateTime.UtcNow;
        t.Messages.Add(Message(u, texte.Trim(), interne, maintenant));
        // La réponse du demandeur relance le traitement
        if (!u.EstAdminTickets && t.Statut == StatutTicket.AttenteDemandeur) ChangerStatut(t, StatutTicket.EnCours, u, maintenant);
        t.ModifieLe = maintenant;
        await db.SaveChangesAsync(ct);
    }

    public async Task MettreAJourAsync(int id, MiseAJourTicket m, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstAdminTickets) throw new ErreurMetier("Réservé au service informatique.");
        var erreurs = m.Valider();
        if (erreurs.Count > 0) throw new ErreurFormulaire(erreurs);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await ChargerAsync(db, id, u, ct);
        var maintenant = DateTime.UtcNow;
        var ancien = t.Statut;

        if (t.Priorite != m.Priorite) t.Messages.Add(Trace(u, $"Priorité : {t.Priorite.Libelle()} → {m.Priorite.Libelle()}", maintenant));
        t.Priorite = m.Priorite;
        t.Categorie = m.Categorie;
        var assigne = Vide(m.AssigneA);
        if (assigne != t.AssigneA && assigne is not null) t.Messages.Add(Trace(u, $"Pris en charge par {assigne}", maintenant));
        t.AssigneA = assigne;
        if (Vide(m.MaterielReference) != t.MaterielReference) await LierMaterielAsync(db, t, m.MaterielReference, ct);
        t.Diagnostic = Vide(m.Diagnostic);
        t.Cause = m.Cause;
        t.Solution = Vide(m.Solution);
        t.ModeResolution = m.ModeResolution;
        t.TempsPasseMinutes = m.TempsPasseMinutes;
        if (m.Statut != ancien) ChangerStatut(t, m.Statut, u, maintenant);
        if (t.AssigneA is not null) t.PrisEnChargeLe ??= maintenant;
        t.ModifieLe = maintenant;

        db.Journal.Add(new Journal { Date = maintenant, Utilisateur = u.Signature, Action = "Ticket mis à jour", Details = $"n° {t.Numero} : {t.Statut.Libelle()}", MaterielId = t.MaterielId });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Le demandeur confirme que le problème est réglé et note la réponse apportée (1 à 5).</summary>
    public async Task ConfirmerResolutionAsync(int id, int note, string? commentaire, Utilisateur u, CancellationToken ct = default)
    {
        if (note is < 1 or > 5) throw new ErreurMetier("Choisissez une note de 1 à 5.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await ChargerAsync(db, id, u, ct);
        if (!EstDemandeur(u, t)) throw new ErreurMetier("Seul le demandeur peut confirmer la résolution.");
        if (t.Statut != StatutTicket.Resolu) throw new ErreurMetier("Ce ticket n'est pas en attente de confirmation.");

        var maintenant = DateTime.UtcNow;
        t.SatisfactionNote = note;
        t.SatisfactionCommentaire = Tronquer(commentaire, 2000);
        ChangerStatut(t, StatutTicket.Clos, u, maintenant, "Résolution confirmée par le demandeur");
        t.ModifieLe = maintenant;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Le demandeur signale que le problème persiste : le ticket repart en traitement.</summary>
    public async Task SignalerNonResoluAsync(int id, string? message, Utilisateur u, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ErreurMetier("Expliquez ce qui ne fonctionne toujours pas.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await ChargerAsync(db, id, u, ct);
        if (!EstDemandeur(u, t)) throw new ErreurMetier("Seul le demandeur peut rouvrir le ticket.");
        if (t.Statut != StatutTicket.Resolu) throw new ErreurMetier("Ce ticket n'est pas en attente de confirmation.");

        var maintenant = DateTime.UtcNow;
        t.Messages.Add(Message(u, Tronquer(message, 4000)!, false, maintenant));
        t.NbReouvertures++;
        ChangerStatut(t, StatutTicket.EnCours, u, maintenant, "Le demandeur indique que le problème persiste");
        t.ModifieLe = maintenant;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Ticket> ChargerAsync(ParcDbContext db, int id, Utilisateur u, CancellationToken ct)
    {
        var t = await db.Tickets.Include(x => x.Messages).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null || !PeutVoir(u, t)) throw new ErreurMetier("Ticket introuvable.");
        return t;
    }

    private static void ChangerStatut(Ticket t, StatutTicket nouveau, Utilisateur u, DateTime maintenant, string? motif = null)
    {
        t.Messages.Add(Trace(u, motif is null ? $"Statut : {t.Statut.Libelle()} → {nouveau.Libelle()}" : $"{motif} ({nouveau.Libelle().ToLowerInvariant()})", maintenant));
        t.Statut = nouveau;
        if (nouveau != StatutTicket.Nouveau) t.PrisEnChargeLe ??= maintenant;
        t.ResoluLe = nouveau is StatutTicket.Resolu or StatutTicket.Clos ? t.ResoluLe ?? maintenant : null;
        t.ClosLe = nouveau == StatutTicket.Clos ? maintenant : null;
    }

    private static async Task LierMaterielAsync(ParcDbContext db, Ticket t, string? reference, CancellationToken ct)
    {
        t.MaterielReference = Vide(reference)?.ToUpperInvariant();
        var cle = t.MaterielReference?.ToLower();
        t.MaterielId = cle is null ? null
            : await db.Materiels.Where(x => x.NumInterne.ToLower() == cle).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
    }

    private static TicketMessage Message(Utilisateur u, string texte, bool interne, DateTime date) =>
        new() { Date = date, Auteur = u.Nom, AuteurEmail = u.Email, Texte = texte, Interne = interne };

    private static TicketMessage Trace(Utilisateur u, string texte, DateTime date) =>
        new() { Date = date, Auteur = u.Nom, AuteurEmail = u.Email, Texte = texte, Systeme = true };

    private static string? Vide(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Tronquer(string? s, int max) => Vide(s) is { } v && v.Length > max ? v[..max] : Vide(s);

    // --- Analyse ---

    public async Task<AnalyseTickets> AnalyserAsync(DateTime? depuis, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Tickets.AsNoTracking();
        if (depuis is { } d) q = q.Where(t => t.CreeLe >= d);
        var tickets = await q.Select(t => new LigneAnalyse(t.CreeLe, t.PrisEnChargeLe, t.ResoluLe, t.Statut, t.Categorie, t.Cause, t.ModeResolution,
            t.Lieu, t.MaterielId == null ? null : t.Materiel!.NumInterne, t.TempsPasseMinutes, t.SatisfactionNote, t.NbReouvertures)).ToListAsync(ct);
        var ouverts = await db.Tickets.CountAsync(t => t.Statut != StatutTicket.Resolu && t.Statut != StatutTicket.Clos, ct);
        return AnalyseTickets.Calculer(tickets, ouverts, DateTime.UtcNow);
    }
}

public sealed record LigneAnalyse(DateTime CreeLe, DateTime? PrisEnChargeLe, DateTime? ResoluLe, StatutTicket Statut,
    CategorieTicket Categorie, CauseTicket? Cause, ModeResolution? Mode, string Lieu, string? Materiel,
    int? TempsPasseMinutes, int? Note, int NbReouvertures);

/// <summary>Une barre d'un graphique : libellé, nombre de tickets, et une précision facultative (délai…).</summary>
public sealed record Barre(string Libelle, int Nombre, string? Precision = null);

public sealed record Mois(DateOnly Debut, int Crees, int Resolus);

public sealed record AnalyseTickets(
    int Crees, int Ouverts, int Resolus,
    TimeSpan? PriseEnChargeMoyenne, TimeSpan? ResolutionMoyenne, TimeSpan? ResolutionMediane,
    int TempsPasseTotalMinutes, double? SatisfactionMoyenne, int NbNotes, double? TauxReouverture, double? TauxADistance,
    List<Barre> ParCategorie, List<Barre> ParCause, List<Barre> ParMode, List<Barre> ParLieu, List<Barre> MaterielsRecurrents, List<Mois> ParMois)
{
    public static AnalyseTickets Calculer(List<LigneAnalyse> t, int ouverts, DateTime maintenant)
    {
        var resolus = t.Where(x => x.ResoluLe is not null).ToList();
        var durees = resolus.Select(x => x.ResoluLe!.Value - x.CreeLe).Order().ToList();
        var priseEnCharge = t.Where(x => x.PrisEnChargeLe is not null).Select(x => x.PrisEnChargeLe!.Value - x.CreeLe).ToList();
        var notes = t.Where(x => x.Note is not null).Select(x => x.Note!.Value).ToList();
        var avecMode = resolus.Where(x => x.Mode is not null).ToList();

        static List<Barre> Compter<TCle>(IEnumerable<LigneAnalyse> lignes, Func<LigneAnalyse, TCle> cle, Func<TCle, string> libelle) =>
            lignes.GroupBy(cle).Select(g => new Barre(libelle(g.Key), g.Count())).OrderByDescending(b => b.Nombre).ThenBy(b => b.Libelle).ToList();

        var debutMois = new DateOnly(maintenant.Year, maintenant.Month, 1);
        var mois = Enumerable.Range(0, 12).Select(i => debutMois.AddMonths(i - 11))
            .Select(m => new Mois(m,
                t.Count(x => DateOnly.FromDateTime(x.CreeLe) is var c && c >= m && c < m.AddMonths(1)),
                resolus.Count(x => DateOnly.FromDateTime(x.ResoluLe!.Value) is var r && r >= m && r < m.AddMonths(1))))
            .ToList();

        return new AnalyseTickets(
            t.Count, ouverts, resolus.Count,
            priseEnCharge.Count == 0 ? null : TimeSpan.FromTicks((long)priseEnCharge.Average(d => d.Ticks)),
            durees.Count == 0 ? null : TimeSpan.FromTicks((long)durees.Average(d => d.Ticks)),
            durees.Count == 0 ? null : durees[durees.Count / 2],
            t.Sum(x => x.TempsPasseMinutes ?? 0),
            notes.Count == 0 ? null : notes.Average(), notes.Count,
            resolus.Count == 0 ? null : (double)resolus.Count(x => x.NbReouvertures > 0) / resolus.Count,
            avecMode.Count == 0 ? null : (double)avecMode.Count(x => x.Mode is ModeResolution.Distance or ModeResolution.Telephone) / avecMode.Count,
            t.GroupBy(x => x.Categorie)
                .Select(g => new Barre(g.Key.Libelle(), g.Count(), Duree.Moyenne(g.Where(x => x.ResoluLe is not null).Select(x => x.ResoluLe!.Value - x.CreeLe))))
                .OrderByDescending(b => b.Nombre).ToList(),
            Compter(resolus.Where(x => x.Cause is not null), x => x.Cause!.Value, c => c.Libelle()),
            Compter(avecMode, x => x.Mode!.Value, m => m.Libelle()),
            t.GroupBy(x => x.Lieu.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => new Barre(g.Key, g.Count()))
                .OrderByDescending(b => b.Nombre).ThenBy(b => b.Libelle).Take(10).ToList(),
            Compter(t.Where(x => x.Materiel is not null), x => x.Materiel!, m => m).Where(b => b.Nombre >= 2).Take(10).ToList(),
            mois);
    }
}

public static class Duree
{
    /// <summary>Durée lisible : « 45 min », « 3 h 20 », « 2,5 j ».</summary>
    public static string Texte(TimeSpan? d) => d switch
    {
        null => "–",
        { TotalMinutes: < 60 } x => $"{Math.Max(0, (int)Math.Round(x.TotalMinutes))} min",
        { TotalHours: < 24 } x => x.Minutes == 0 ? $"{(int)x.TotalHours} h" : $"{(int)x.TotalHours} h {x.Minutes:D2}",
        { } x => $"{x.TotalDays:0.#} j",
    };

    public static string Minutes(int minutes) => Texte(TimeSpan.FromMinutes(minutes));

    public static string? Moyenne(IEnumerable<TimeSpan> durees)
    {
        var l = durees.ToList();
        return l.Count == 0 ? null : $"résolu en {Texte(TimeSpan.FromTicks((long)l.Average(d => d.Ticks)))} en moyenne";
    }
}
