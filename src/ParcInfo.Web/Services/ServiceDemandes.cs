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
    /// <summary>Comptes autorisés à valider ou refuser (en plus des gestionnaires du parc).</summary>
    public string[] Valideurs { get; set; } = [];
    /// <summary>Premier numéro attribué (suite de la feuille ETAT DEMANDES).</summary>
    public int PremierNumero { get; set; } = 1;
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

    private static bool EmailValide(string s) =>
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
        try { await notifications.NouvelleDemandeAsync(d, options.Destinataires, ct); }
        catch (Exception ex) { log.LogError(ex, "Courriel de nouvelle demande"); avertissements.Add(ex.Message); }
        try { await notifications.AccuseReceptionDemandeAsync(d, ct); }
        catch (Exception ex) { log.LogError(ex, "Accusé de réception de demande"); avertissements.Add(ex.Message); }

        return new ResultatDemande(d, avertissements.Count == 0 ? null : string.Join(" ", avertissements));
    }

    /// <summary>Peut voir la demande : son auteur / demandeur, les valideurs et l'informatique.</summary>
    public static bool PeutVoir(Utilisateur u, Demande d) => u.EstValideur || u.AEmail(d.SaisiePar) || u.AEmail(d.DemandeurEmail);

    public async Task<ResultatDemande> DeciderAsync(Guid idPublic, bool valider, string? commentaire, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstValideur) throw new ErreurMetier("Vous n'êtes pas autorisé à valider les demandes.");
        if (!valider && string.IsNullOrWhiteSpace(commentaire)) throw new ErreurMetier("Indiquez le motif du refus.");

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

    private static string? Vide(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
