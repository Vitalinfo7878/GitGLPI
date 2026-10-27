using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

public sealed class OptionsDemandes
{
    /// <summary>Boîtes qui reçoivent chaque nouvelle demande avec les boutons « Valider » / « Refuser ».</summary>
    public string[] Destinataires { get; set; } = [];
    /// <summary>
    /// Valideurs de la configuration : repris une fois sur la page « Droits », qui fait foi ensuite ;
    /// utilisés pour les mails seulement si aucun valideur n'y est défini.
    /// </summary>
    public string[] Valideurs { get; set; } = [];
    /// <summary>Premier numéro attribué (suite de la feuille ETAT DEMANDES).</summary>
    public int PremierNumero { get; set; } = 1;
    /// <summary>Une demande ni validée ni refusée depuis ce nombre de jours est relancée auprès des valideurs (puis à nouveau à chaque délai).</summary>
    public int DelaiRelanceJours { get; set; } = 7;
}

/// <summary>Réponses au formulaire de demande, avec les mêmes obligations que le formulaire Microsoft Forms.</summary>
public sealed class FormulaireDemande
{
    public const string Autre = "Autre";
    public const string MessageObligatoire = "Cette question est obligatoire.";
    public const int LongueurRaison = 4000;

    public string? DemandeurNom { get; set; }
    public HashSet<string> Objets { get; set; } = [];
    public string? ObjetAutre { get; set; }
    /// <summary>Un des choix de <see cref="Demande.ChoixConcerne"/>, ou <see cref="Autre"/>.</summary>
    public string? Concerne { get; set; }
    public string? ConcerneAutre { get; set; }
    public string? AdresseMailACreer { get; set; }
    public string? Raison { get; set; }
    public string? AdresseLivraison { get; set; }
    public string? DestinataireLivraison { get; set; }
    public string? DemandeurEmail { get; set; }
    public string? DemandeurTelephone { get; set; }
    public DateTime? Echeance { get; set; }

    /// <summary>Erreurs par champ (nom de propriété → message). Vide si le formulaire est complet.</summary>
    public Dictionary<string, string> Valider()
    {
        var e = new Dictionary<string, string>();
        void Obligatoire(string champ, string? valeur)
        {
            if (string.IsNullOrWhiteSpace(valeur)) e[champ] = MessageObligatoire;
        }

        Obligatoire(nameof(DemandeurNom), DemandeurNom);
        if (Objets.Count == 0) e[nameof(Objets)] = MessageObligatoire;
        else if (Objets.Contains(Autre) && string.IsNullOrWhiteSpace(ObjetAutre)) e[nameof(ObjetAutre)] = "Précisez « Autre ».";
        if (string.IsNullOrWhiteSpace(Concerne)) e[nameof(Concerne)] = MessageObligatoire;
        else if (Concerne == Autre && string.IsNullOrWhiteSpace(ConcerneAutre)) e[nameof(ConcerneAutre)] = "Précisez « Autre ».";
        if (!string.IsNullOrWhiteSpace(AdresseMailACreer) && !EmailValide(AdresseMailACreer))
            e[nameof(AdresseMailACreer)] = "Adresse mail invalide.";
        Obligatoire(nameof(Raison), Raison);
        Obligatoire(nameof(AdresseLivraison), AdresseLivraison);
        Obligatoire(nameof(DemandeurEmail), DemandeurEmail);
        if (!e.ContainsKey(nameof(DemandeurEmail)) && !EmailValide(DemandeurEmail!)) e[nameof(DemandeurEmail)] = "Adresse mail invalide.";
        Obligatoire(nameof(DemandeurTelephone), DemandeurTelephone);
        if (!e.ContainsKey(nameof(DemandeurTelephone)) && Regex.Replace(DemandeurTelephone!, @"\D", "").Length < 10)
            e[nameof(DemandeurTelephone)] = "Numéro de téléphone incomplet (10 chiffres minimum).";
        if (Echeance is { } d && d.Date < DateTime.Today) e[nameof(Echeance)] = "L'échéance ne peut pas être dans le passé.";
        foreach (var (champ, valeur, max) in new (string, string?, int)[]
        {
            (nameof(DemandeurNom), DemandeurNom, 200), (nameof(ObjetAutre), ObjetAutre, 200), (nameof(ConcerneAutre), ConcerneAutre, 200),
            (nameof(AdresseMailACreer), AdresseMailACreer, 200), (nameof(Raison), Raison, LongueurRaison),
            (nameof(AdresseLivraison), AdresseLivraison, 400), (nameof(DestinataireLivraison), DestinataireLivraison, 200),
            (nameof(DemandeurEmail), DemandeurEmail, 200), (nameof(DemandeurTelephone), DemandeurTelephone, 50),
        })
            if (valeur?.Trim().Length > max && !e.ContainsKey(champ)) e[champ] = $"Réponse trop longue ({max} caractères maximum).";
        return e;
    }

    internal static bool EmailValide(string s) =>
        MailAddress.TryCreate(s.Trim(), out var a) && a.Address.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase) && a.Host.Contains('.');

    /// <summary>Objets dans l'ordre du formulaire, « Autre » remplacé par la précision saisie.</summary>
    public string ObjetsTexte() => string.Join(" ; ",
        Demande.ChoixObjet.Where(Objets.Contains)
            .Concat(Objets.Contains(Autre) && !string.IsNullOrWhiteSpace(ObjetAutre) ? [$"Autre : {ObjetAutre.Trim()}"] : []));

    public string ConcerneTexte() => Concerne == Autre ? $"Autre : {ConcerneAutre?.Trim()}" : Concerne ?? "";
}

public sealed class ErreurFormulaire(Dictionary<string, string> erreurs) : Exception("Le formulaire est incomplet.")
{
    public Dictionary<string, string> Erreurs { get; } = erreurs;
}

public sealed record ResultatDemande(Demande Demande, string? AvertissementCourriel);

public sealed class ServiceDemandes(
    IDbContextFactory<ParcDbContext> dbFactory, ServiceNotifications notifications, OptionsDemandes options, ILogger<ServiceDemandes> log)
{
    public async Task<ResultatDemande> CreerAsync(FormulaireDemande f, Utilisateur auteur, CancellationToken ct = default)
    {
        if (auteur.EstSite) throw new ErreurMetier("Les comptes de site ne peuvent pas faire de demande informatique : créez un ticket.");
        // Le mail du demandeur est toujours celui du compte connecté (non modifiable dans le formulaire)
        f.DemandeurEmail = auteur.Email;
        var erreurs = f.Valider();
        if (erreurs.Count > 0) throw new ErreurFormulaire(erreurs);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var maintenant = DateTime.UtcNow;
        var d = new Demande
        {
            CreeLe = maintenant,
            ModifieLe = maintenant,
            SaisiePar = auteur.Email,
            DemandeurNom = f.DemandeurNom!.Trim(),
            Objets = f.ObjetsTexte(),
            Concerne = f.ConcerneTexte(),
            AdresseMailACreer = Vide(f.AdresseMailACreer)?.ToLowerInvariant(),
            Raison = f.Raison!.Trim(),
            AdresseLivraison = f.AdresseLivraison!.Trim(),
            DestinataireLivraison = Vide(f.DestinataireLivraison),
            DemandeurEmail = f.DemandeurEmail!.Trim().ToLowerInvariant(),
            DemandeurTelephone = f.DemandeurTelephone!.Trim(),
            Echeance = f.Echeance is { } e ? DateOnly.FromDateTime(e) : null,
            StatutValidation = StatutValidation.EnAttente,
        };

        // Numérotation à la suite (avec une nouvelle tentative si deux demandes arrivent en même temps)
        for (var essai = 0; ; essai++)
        {
            var max = await db.Demandes.MaxAsync(x => (int?)x.Numero, ct) ?? 0;
            d.Numero = Math.Max(max + 1, options.PremierNumero);
            db.Demandes.Add(d);
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException) when (essai < 3)
            {
                db.Entry(d).State = EntityState.Detached;
            }
        }

        db.Journal.Add(new Journal { Date = maintenant, Utilisateur = auteur.Signature, Action = "Nouvelle demande", Details = $"n° {d.Numero} – {d.Objets}" });
        await db.SaveChangesAsync(ct);

        var avertissements = new List<string>();
        try { await notifications.NouvelleDemandeAsync(d, options.Destinataires, await ValideursAsync(db, ct), ct); }
        catch (Exception ex) { log.LogError(ex, "Courriel de nouvelle demande"); avertissements.Add(ex.Message); }
        try { await notifications.AccuseReceptionDemandeAsync(d, ct); }
        catch (Exception ex) { log.LogError(ex, "Accusé de réception de demande"); avertissements.Add(ex.Message); }

        return new ResultatDemande(d, avertissements.Count == 0 ? null : string.Join(" ", avertissements));
    }

    /// <summary>Peut voir la demande : son auteur / demandeur, les valideurs et l'informatique.</summary>
    public static bool PeutVoir(Utilisateur u, Demande d) => u.VoitToutesLesDemandes || u.AEmail(d.SaisiePar) || u.AEmail(d.DemandeurEmail);

    /// <summary>Échanges internes de la demande : réservés au valideur et au service informatique, jamais au demandeur.</summary>
    public async Task<List<MessageDemande>> ListerMessagesAsync(int demandeId, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.VoitToutesLesDemandes) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MessagesDemandes.AsNoTracking().Where(m => m.DemandeId == demandeId).OrderBy(m => m.Le).ThenBy(m => m.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Le valideur peut commenter une demande en attente sans la trancher, tant que personne ne lui a répondu ;
    /// une fois que le service informatique a répondu, il doit valider ou refuser (son commentaire accompagne alors la décision).
    /// </summary>
    public static bool ValideurDoitDecider(Utilisateur u, Demande d, IEnumerable<MessageDemande> messages) =>
        u.EstValideur && !u.EstGestionnaire && d.StatutValidation == StatutValidation.EnAttente && messages.Any(m => !u.AEmail(m.AuteurEmail));

    /// <summary>
    /// Message interne (ex. « Validez-vous ? ») : enregistré sur la demande et envoyé par mail aux autres destinataires
    /// des demandes (service informatique, valideurs), qui répondent depuis la demande.
    /// </summary>
    public async Task<ResultatDemande> AjouterMessageAsync(Guid idPublic, string? texte, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.VoitToutesLesDemandes) throw new ErreurMetier("Réservé au valideur et au service informatique.");
        if (string.IsNullOrWhiteSpace(texte)) throw new ErreurMetier("Écrivez votre message.");
        if (texte.Trim().Length > 4000) throw new ErreurMetier("Message trop long (4000 caractères maximum).");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var d = await db.Demandes.FirstOrDefaultAsync(x => x.IdPublic == idPublic, ct) ?? throw new ErreurMetier("Demande introuvable.");
        if (ValideurDoitDecider(u, d, await db.MessagesDemandes.Where(x => x.DemandeId == d.Id).ToListAsync(ct)))
            throw new ErreurMetier("Le service informatique vous a répondu : validez ou refusez la demande (vous pouvez joindre un commentaire).");
        var m = new MessageDemande { DemandeId = d.Id, Le = DateTime.UtcNow, Auteur = u.Nom, AuteurEmail = u.Email, Texte = texte.Trim() };
        db.MessagesDemandes.Add(m);
        db.Journal.Add(new Journal { Date = m.Le, Utilisateur = u.Signature, Action = "Échange interne sur une demande", Details = $"n° {d.Numero}" });
        await db.SaveChangesAsync(ct);

        string? avertissement = null;
        try { await notifications.MessageDemandeAsync(d, m, options.Destinataires.Concat(await ValideursAsync(db, ct)).Where(x => !u.AEmail(x)), ct); }
        catch (Exception ex) { log.LogError(ex, "Courriel d'échange interne"); avertissement = ex.Message; }
        return new ResultatDemande(d, avertissement);
    }

    /// <summary>
    /// Validation ou refus, avec un commentaire facultatif réservé aux gestionnaires et valideurs.
    /// Le demandeur n'est pas prévenu automatiquement (le service informatique l'informe) ; les autres destinataires des demandes reçoivent la décision.
    /// </summary>
    public async Task<ResultatDemande> DeciderAsync(Guid idPublic, bool valider, string? commentaire, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstValideur) throw new ErreurMetier("Vous n'êtes pas autorisé à valider les demandes.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var d = await db.Demandes.FirstOrDefaultAsync(x => x.IdPublic == idPublic, ct) ?? throw new ErreurMetier("Demande introuvable.");
        if (d.StatutValidation != StatutValidation.EnAttente)
            throw new ErreurMetier($"Cette demande a déjà été {d.StatutValidation.Libelle().ToLowerInvariant()} par {d.DecisionPar} le {d.DecisionLe?.ToLocalTime():dd/MM/yyyy à HH:mm}.");

        var maintenant = DateTime.UtcNow;
        d.StatutValidation = valider ? StatutValidation.Validee : StatutValidation.Refusee;
        d.StatutRealisation = StatutRealisation.AFaire;
        d.DecisionLe = maintenant;
        d.DecisionPar = u.Nom;
        d.DecisionParEmail = u.Email;
        d.CommentaireDecision = Vide(commentaire);
        d.ModifieLe = maintenant;

        // Mise à jour conditionnelle : si deux valideurs cliquent en même temps, seule la première décision compte.
        var modifiees = await db.Demandes
            .Where(x => x.Id == d.Id && x.StatutValidation == StatutValidation.EnAttente)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.StatutValidation, d.StatutValidation)
                .SetProperty(p => p.StatutRealisation, d.StatutRealisation)
                .SetProperty(p => p.DecisionLe, d.DecisionLe)
                .SetProperty(p => p.DecisionPar, d.DecisionPar)
                .SetProperty(p => p.DecisionParEmail, d.DecisionParEmail)
                .SetProperty(p => p.CommentaireDecision, d.CommentaireDecision)
                .SetProperty(p => p.ModifieLe, d.ModifieLe), ct);
        if (modifiees == 0)
            throw new ErreurMetier("Cette demande vient d'être traitée par quelqu'un d'autre : rechargez la page.");
        db.Journal.Add(new Journal { Date = maintenant, Utilisateur = u.Signature, Action = valider ? "Demande validée" : "Demande refusée", Details = $"n° {d.Numero}" });
        await db.SaveChangesAsync(ct);

        string? avertissement = null;
        try { await notifications.DecisionDemandeAsync(d, options.Destinataires.Where(x => !u.AEmail(x)), ct); }
        catch (Exception ex) { log.LogError(ex, "Courriel de décision"); avertissement = ex.Message; }
        return new ResultatDemande(d, avertissement);
    }

    /// <summary>
    /// Bouton « Envoyer » : prévient le demandeur que le matériel est parti (réception sous 2 à 3 jours ouvrés)
    /// et passe la demande « En cours d'envoi ».
    /// </summary>
    public async Task<ResultatDemande> NotifierEnvoiAsync(int id, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstGestionnaire) throw new ErreurMetier("Réservé au service informatique.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var d = await db.Demandes.FirstAsync(x => x.Id == id, ct);
        if (d.StatutValidation != StatutValidation.Validee) throw new ErreurMetier("Seule une demande validée peut être envoyée.");
        if (d.StatutRealisation is StatutRealisation.Realisee or StatutRealisation.Annulee)
            throw new ErreurMetier($"La demande est déjà « {d.StatutRealisation.Libelle()} ».");

        var maintenant = DateTime.UtcNow;
        d.StatutRealisation = StatutRealisation.EnCoursEnvoi;
        d.EnvoiNotifieLe = maintenant;
        d.ModifieLe = maintenant;
        db.Journal.Add(new Journal { Date = maintenant, Utilisateur = u.Signature, Action = "Demande envoyée", Details = $"n° {d.Numero} : demandeur prévenu" });
        await db.SaveChangesAsync(ct);

        string? avertissement = null;
        try { await notifications.MaterielEnvoyeAsync(d, DateOnly.FromDateTime(DateTime.Today), ct); }
        catch (Exception ex) { log.LogError(ex, "Courriel d'envoi du matériel"); avertissement = ex.Message; }
        return new ResultatDemande(d, avertissement);
    }

    /// <summary>
    /// Relance les valideurs pour les demandes ni validées ni refusées depuis <see cref="OptionsDemandes.DelaiRelanceJours"/> jours
    /// (un seul mail récapitulatif ; chaque demande est relancée à nouveau après le même délai). Renvoie le nombre de demandes relancées.
    /// </summary>
    public async Task<int> RelancerAsync(DateTime maintenant, CancellationToken ct = default)
    {
        var limite = maintenant.AddDays(-options.DelaiRelanceJours);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var demandes = await db.Demandes
            .Where(d => d.StatutValidation == StatutValidation.EnAttente && d.CreeLe <= limite && (d.DerniereRelanceLe == null || d.DerniereRelanceLe <= limite))
            .OrderBy(d => d.Numero).ToListAsync(ct);
        if (demandes.Count == 0) return 0;

        var valideurs = await ValideursAsync(db, ct);
        await notifications.RelanceDemandesAsync(demandes, valideurs.Count > 0 ? valideurs : options.Destinataires, maintenant, ct);
        foreach (var d in demandes) d.DerniereRelanceLe = maintenant;
        db.Journal.Add(new Journal
        {
            Date = maintenant, Utilisateur = "Relance automatique", Action = "Relance des demandes en attente",
            Details = string.Join(", ", demandes.Select(d => $"n° {d.Numero}")),
        });
        await db.SaveChangesAsync(ct);
        return demandes.Count;
    }

    /// <summary>Suivi du traitement par le service informatique (priorité, avancement, commentaire).</summary>
    public async Task<ResultatDemande> MettreAJourTraitementAsync(int id, Priorite priorite, StatutRealisation statut, string? commentaire,
        Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstGestionnaire) throw new ErreurMetier("Réservé au service informatique.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var d = await db.Demandes.FirstAsync(x => x.Id == id, ct);
        if (d.StatutValidation != StatutValidation.Validee) throw new ErreurMetier("Seule une demande validée peut être traitée.");

        var devientRealisee = statut == StatutRealisation.Realisee && d.StatutRealisation != StatutRealisation.Realisee;
        d.Priorite = priorite;
        d.StatutRealisation = statut;
        d.CommentaireInformatique = Vide(commentaire);
        d.RealiseeLe = statut is StatutRealisation.Realisee or StatutRealisation.Annulee ? d.RealiseeLe ?? DateTime.UtcNow : null;
        d.ModifieLe = DateTime.UtcNow;
        db.Journal.Add(new Journal { Date = DateTime.UtcNow, Utilisateur = u.Signature, Action = "Demande mise à jour", Details = $"n° {d.Numero} : {statut.Libelle()}" });
        await db.SaveChangesAsync(ct);

        string? avertissement = null;
        if (devientRealisee)
        {
            try { await notifications.DemandeRealiseeAsync(d, ct); }
            catch (Exception ex) { avertissement = ex.Message; }
        }
        return new ResultatDemande(d, avertissement);
    }

    /// <summary>Valideurs de la page « Droits » (à défaut, ceux de la configuration).</summary>
    public async Task<List<string>> ValideursAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ValideursAsync(db, ct);
    }

    private async Task<List<string>> ValideursAsync(ParcDbContext db, CancellationToken ct)
    {
        var valideurs = await ServiceDroits.ValideursAsync(db, ct);
        return valideurs.Count > 0 ? valideurs : [.. options.Valideurs];
    }

    private static string? Vide(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Relance des demandes en attente de validation, chaque jour ouvré vers 8 h 30 (heure de Paris).</summary>
public sealed class RelanceDemandes(IServiceScopeFactory scopes, ILogger<RelanceDemandes> log) : BackgroundService
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var maintenant = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Paris);
            var prochaine = new DateTimeOffset(maintenant.Date.AddHours(8.5), maintenant.Offset);
            if (prochaine <= maintenant) prochaine = prochaine.AddDays(1);
            while (!JoursOuvres.EstOuvre(DateOnly.FromDateTime(prochaine.Date))) prochaine = prochaine.AddDays(1);
            try { await Task.Delay(prochaine - maintenant, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = scopes.CreateScope();
                var n = await scope.ServiceProvider.GetRequiredService<ServiceDemandes>().RelancerAsync(DateTime.UtcNow, ct);
                if (n > 0) log.LogInformation("{Nombre} demande(s) en attente relancée(s)", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Relance des demandes en attente impossible");
            }
        }
    }
}

/// <summary>Jours ouvrés : du lundi au vendredi, hors jours fériés en France métropolitaine.</summary>
public static class JoursOuvres
{
    public static bool EstOuvre(DateOnly j) => j.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !Feries(j.Year).Contains(j);

    /// <summary>Date après <paramref name="n"/> jours ouvrés (ex. envoi le vendredi + 2 jours ouvrés = mardi).</summary>
    public static DateOnly Ajouter(DateOnly depart, int n)
    {
        var j = depart;
        while (n > 0) { j = j.AddDays(1); if (EstOuvre(j)) n--; }
        return j;
    }

    private static HashSet<DateOnly> Feries(int annee)
    {
        // Dimanche de Pâques (algorithme de Meeus)
        int a = annee % 19, b = annee / 100, c = annee % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3,
            h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        var paques = new DateOnly(annee, (h + l - 7 * m + 114) / 31, (h + l - 7 * m + 114) % 31 + 1);
        return
        [
            new(annee, 1, 1), paques.AddDays(1), new(annee, 5, 1), new(annee, 5, 8), paques.AddDays(39), paques.AddDays(50),
            new(annee, 7, 14), new(annee, 8, 15), new(annee, 11, 1), new(annee, 11, 11), new(annee, 12, 25),
        ];
    }
}
