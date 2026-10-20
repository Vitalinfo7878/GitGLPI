using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

public sealed class DemandeMouvement
{
    public TypeMouvement Type { get; set; } = TypeMouvement.Attribution;
    public List<int> MaterielIds { get; set; } = [];
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string? Expediteur { get; set; }
    public int? DestinationSiteId { get; set; }
    public int? DestinationPersonneId { get; set; }
    public string? DestinationLibre { get; set; }
    public string? DocumentReference { get; set; }
    public string? Commentaire { get; set; }
    /// <summary>Pour une attribution : envoyer un mail au destinataire pour qu'il confirme la réception.</summary>
    public bool DemanderConfirmation { get; set; } = true;
    /// <summary>Adresse du destinataire si elle diffère de celle de la personne / du site.</summary>
    public string? EmailDestinataire { get; set; }
}

public sealed class NouveauMateriel
{
    public TypeMateriel Type { get; set; } = TypeMateriel.PcPortable;
    public string? NumInterne { get; set; }
    public string? Modele { get; set; }
    public string? NumSerie { get; set; }
    public string? Configuration { get; set; }
    public string? NumFacture { get; set; }
}

public sealed class DemandeAchat
{
    public List<NouveauMateriel> Materiels { get; set; } = [new()];
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string? Fournisseur { get; set; }
    public string? DocumentReference { get; set; }
    public string? Commentaire { get; set; }
}

public sealed class ErreurMetier(string message) : Exception(message);

public sealed record ResultatMouvement(List<int> Numeros, Fiche? Fiche, string? AvertissementCourriel);

public sealed class ServiceMouvements(
    IDbContextFactory<ParcDbContext> dbFactory, GenerateurFiche generateur, ServiceNotifications notifications, ILogger<ServiceMouvements> log)
{
    /// <summary>Prochain numéro interne libre pour la nomenclature actuelle (PC, PO, E, I + 4 chiffres).</summary>
    public static string Prefixe(TypeMateriel type) => type switch
    {
        TypeMateriel.PcFixe => "PC",
        TypeMateriel.PcPortable => "PO",
        TypeMateriel.Ecran => "E",
        TypeMateriel.Imprimante => "I",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public async Task<string> ProchainNumInterneAsync(TypeMateriel type, IEnumerable<string>? dejaReserves = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var prefixe = Prefixe(type);
        var existants = await db.Materiels.Where(m => m.NumInterne.StartsWith(prefixe)).Select(m => m.NumInterne).ToListAsync();
        return ProchainNumero(prefixe, existants.Concat(dejaReserves ?? []));
    }

    public static string ProchainNumero(string prefixe, IEnumerable<string> existants)
    {
        var max = existants
            .Where(n => n.Length > prefixe.Length && n.StartsWith(prefixe, StringComparison.OrdinalIgnoreCase))
            .Select(n => int.TryParse(n[prefixe.Length..], out var i) ? i : 0)
            .DefaultIfEmpty(0).Max();
        return $"{prefixe}{max + 1:D4}";
    }

    /// <summary>Réception d'un achat : crée les matériels (en stock) et la fiche d'achat FIS.</summary>
    public async Task<ResultatMouvement> ReceptionnerAchatAsync(DemandeAchat demande, Utilisateur auteur, CancellationToken ct = default)
    {
        if (demande.Materiels.Count == 0) throw new ErreurMetier("Ajoutez au moins un matériel.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var maintenant = DateTime.UtcNow;
        var reserves = new List<string>();
        var crees = new List<Materiel>();
        foreach (var n in demande.Materiels)
        {
            var num = string.IsNullOrWhiteSpace(n.NumInterne)
                ? await ProchainNumInterneAsync(n.Type, reserves)
                : n.NumInterne.Trim().ToUpperInvariant();
            if (reserves.Contains(num) || await db.Materiels.AnyAsync(m => m.NumInterne == num, ct))
                throw new ErreurMetier($"Le numéro interne {num} existe déjà.");
            reserves.Add(num);
            var m = new Materiel
            {
                NumInterne = num, Type = n.Type, Modele = Vide(n.Modele), NumSerie = Vide(n.NumSerie),
                Configuration = Vide(n.Configuration), NumFacture = Vide(n.NumFacture), Statut = StatutMateriel.EnStock,
                AnneeAcquisition = demande.Date.Year, CreeLe = maintenant, ModifieLe = maintenant,
            };
            db.Materiels.Add(m);
            crees.Add(m);
        }
        await db.SaveChangesAsync(ct);

        return await CreerMouvementsAsync(new DemandeMouvement
        {
            Type = TypeMouvement.Achat, MaterielIds = crees.Select(m => m.Id).ToList(), Date = demande.Date,
            Expediteur = demande.Fournisseur, DocumentReference = demande.DocumentReference, Commentaire = demande.Commentaire,
            DemanderConfirmation = false,
        }, auteur, ct);
    }

    public async Task<ResultatMouvement> CreerMouvementsAsync(DemandeMouvement d, Utilisateur auteur, CancellationToken ct = default)
    {
        if (d.MaterielIds.Count == 0) throw new ErreurMetier("Sélectionnez au moins un matériel.");
        var regle = RegleMouvement.Pour(d.Type);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var materiels = await db.Materiels
            .Include(m => m.Site).Include(m => m.Personne)
            .Where(m => d.MaterielIds.Contains(m.Id)).ToListAsync(ct);
        if (materiels.Count != d.MaterielIds.Distinct().Count()) throw new ErreurMetier("Un des matériels sélectionnés est introuvable.");

        var refuses = materiels.Where(m => !regle.EstAutorisePour(m.Statut)).ToList();
        if (refuses.Count > 0)
            throw new ErreurMetier($"« {d.Type.Libelle()} » impossible pour : " +
                string.Join(", ", refuses.Select(m => $"{m.NumInterne} ({m.Statut.Libelle()})")));

        var site = d.DestinationSiteId is { } sid ? await db.Sites.Include(s => s.Region).FirstAsync(s => s.Id == sid, ct) : null;
        var personne = d.DestinationPersonneId is { } pid ? await db.Personnes.Include(p => p.Region).FirstAsync(p => p.Id == pid, ct) : null;
        if (site != null && personne != null) throw new ErreurMetier("Choisissez un site OU une personne, pas les deux.");
        if (regle.DestinationEstDetenteur && site == null && personne == null)
            throw new ErreurMetier("Choisissez le site ou la personne destinataire.");

        // Une attribution confirmée électroniquement n'est possible que sur une destination unique avec une adresse mail.
        var emailDestinataire = (Vide(d.EmailDestinataire) ?? personne?.Email ?? site?.Email)?.ToLowerInvariant();
        var demanderConfirmation = d.Type == TypeMouvement.Attribution && d.DemanderConfirmation;
        if (demanderConfirmation && emailDestinataire is null)
            throw new ErreurMetier("Aucune adresse mail pour ce destinataire : renseignez-la ou décochez la confirmation de réception.");

        var maintenant = DateTime.UtcNow;
        var prochain = (await db.Mouvements.MaxAsync(m => (int?)m.Numero, ct) ?? 0) + 1;
        var mouvements = new List<Mouvement>();

        foreach (var m in materiels.OrderBy(m => m.NumInterne))
        {
            var mv = new Mouvement
            {
                Numero = prochain++,
                Materiel = m,
                Type = d.Type,
                Date = d.Date,
                Expediteur = Vide(d.Expediteur) ?? regle.ExpediteurParDefaut ?? (m.DetenteurLibelle is { Length: > 0 } det ? det : null),
                DestinationSite = site,
                DestinationPersonne = personne,
                DestinationLibre = site == null && personne == null ? Vide(d.DestinationLibre) ?? regle.DestinationParDefaut : null,
                Region = (site?.Region ?? personne?.Region)?.Nom ?? (await RegionDe(db, m, ct)),
                DocumentReference = Vide(d.DocumentReference),
                Commentaire = Vide(d.Commentaire),
                CreePar = auteur.Signature,
                CreeLe = maintenant,
            };
            mouvements.Add(mv);
            db.Mouvements.Add(mv);

            m.Statut = regle.StatutResultant;
            if (regle.DestinationEstDetenteur)
            {
                m.SiteId = site?.Id;
                m.PersonneId = personne?.Id;
                m.RegionId = site?.RegionId ?? personne?.RegionId ?? m.RegionId;
            }
            else
            {
                m.SiteId = null;
                m.PersonneId = null;
            }
            m.ModifieLe = maintenant;
        }

        Fiche? fiche = null;
        if (regle.Fiche is { } typeFiche)
        {
            var numero = mouvements[0].Numero;
            fiche = new Fiche
            {
                Type = typeFiche,
                Annee = d.Date.Year,
                Numero = numero,
                Code = RegleMouvement.CodeFiche(typeFiche, d.Date.Year, numero),
                DestinataireNom = site?.Libelle ?? personne?.NomComplet ?? Vide(d.DestinationLibre) ?? regle.DestinationParDefaut,
                DestinataireEmail = d.Type == TypeMouvement.Attribution ? emailDestinataire : null,
                StatutReception = demanderConfirmation ? StatutReception.EnAttente : StatutReception.NonDemandee,
                CreePar = auteur.Signature,
                CreeLe = maintenant,
                Mouvements = mouvements,
            };
            db.Fiches.Add(fiche);
        }

        db.Journal.Add(new Journal
        {
            Date = maintenant, Utilisateur = auteur.Signature, Action = d.Type.Libelle(),
            Details = $"Mouvements {mouvements[0].Numero}–{mouvements[^1].Numero} : {string.Join(", ", materiels.Select(m => m.NumInterne))}" +
                      (fiche is null ? "" : $" — fiche {fiche.Code}"),
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ErreurMetier("Un autre mouvement vient d'être enregistré en même temps : réessayez.");
        }

        string? avertissement = null;
        if (fiche != null)
        {
            // Le mouvement est déjà enregistré : un problème de PDF ou de courriel ne doit pas le faire échouer.
            try
            {
                fiche.Pdf = generateur.Generer(fiche);
                fiche.PdfGenereLe = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Génération du PDF {Code} impossible", fiche.Code);
                avertissement = $"Mouvement enregistré, mais le PDF de la fiche n'a pas pu être généré ({ex.Message}). Utilisez « Régénérer » dans Fiches.";
            }
            if (fiche.StatutReception == StatutReception.EnAttente)
            {
                try { await notifications.DemanderConfirmationReceptionAsync(fiche, ct); }
                catch (Exception ex) { avertissement = string.Join(" ", avertissement, ex.Message).Trim(); }
            }
        }
        return new ResultatMouvement(mouvements.Select(m => m.Numero).ToList(), fiche, avertissement);
    }

    private static async Task<string?> RegionDe(ParcDbContext db, Materiel m, CancellationToken ct) =>
        m.RegionId is { } id ? await db.Regions.Where(r => r.Id == id).Select(r => r.Nom).FirstOrDefaultAsync(ct) : null;

    /// <summary>Le destinataire (ou l'informatique, pour son compte) confirme la réception d'une fiche d'attribution.</summary>
    public async Task<Fiche> ConfirmerReceptionAsync(Guid idPublic, Utilisateur u, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var fiche = await ChargerFiche(db).FirstOrDefaultAsync(f => f.IdPublic == idPublic, ct)
            ?? throw new ErreurMetier("Fiche introuvable.");
        if (fiche.StatutReception == StatutReception.Confirmee) return fiche;
        if (fiche.StatutReception != StatutReception.EnAttente) throw new ErreurMetier("Cette fiche n'attend pas de confirmation.");
        if (!u.AEmail(fiche.DestinataireEmail) && !u.EstGestionnaire)
            throw new ErreurMetier($"Cette confirmation est réservée à {fiche.DestinataireEmail}. Connectez-vous avec ce compte.");

        var maintenant = DateTime.UtcNow;
        fiche.StatutReception = StatutReception.Confirmee;
        fiche.ReceptionConfirmeeLe = maintenant;
        fiche.ReceptionConfirmeePar = u.AEmail(fiche.DestinataireEmail) ? u.Nom : $"{u.Nom} (service informatique, pour le compte du destinataire)";
        fiche.ReceptionConfirmeeParEmail = u.Email;
        foreach (var m in fiche.Mouvements) m.DateReception = DateOnly.FromDateTime(DateTime.Today);
        fiche.Pdf = generateur.Generer(fiche);
        fiche.PdfGenereLe = maintenant;
        db.Journal.Add(new Journal { Date = maintenant, Utilisateur = u.Signature, Action = "Réception confirmée", Details = fiche.Code });
        await db.SaveChangesAsync(ct);

        try { await notifications.SignalerReceptionConfirmeeAsync(fiche, ct); }
        catch { /* déjà tracé dans les courriels */ }
        return fiche;
    }

    public async Task RelancerAsync(int ficheId, Utilisateur u, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var fiche = await ChargerFiche(db).FirstAsync(f => f.Id == ficheId, ct);
        if (fiche.StatutReception != StatutReception.EnAttente) throw new ErreurMetier("Cette fiche n'attend pas de confirmation.");
        await notifications.DemanderConfirmationReceptionAsync(fiche, ct);
        fiche.DerniereRelanceLe = DateTime.UtcNow;
        db.Journal.Add(new Journal { Date = DateTime.UtcNow, Utilisateur = u.Signature, Action = "Relance réception", Details = fiche.Code });
        await db.SaveChangesAsync(ct);
    }

    public async Task RegenererPdfAsync(int ficheId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var fiche = await ChargerFiche(db).FirstAsync(f => f.Id == ficheId, ct);
        fiche.Pdf = generateur.Generer(fiche);
        fiche.PdfGenereLe = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Mot de passe visible par l'informatique, ou par le détenteur actuel (même adresse mail)
    /// à condition qu'aucune réception ne soit en attente pour ce matériel.
    /// </summary>
    public static bool PeutVoirMotDePasse(Utilisateur u, Materiel m, IEnumerable<Fiche> fichesEnAttente)
    {
        if (u.EstGestionnaire) return true;
        var estDetenteur = u.AEmail(m.Personne?.Email) || u.AEmail(m.Site?.Email);
        return estDetenteur && m.Statut == StatutMateriel.Attribue && !fichesEnAttente.Any();
    }

    public static IQueryable<Fiche> ChargerFiche(ParcDbContext db) => db.Fiches
        .Include(f => f.Mouvements).ThenInclude(m => m.Materiel)
        .Include(f => f.Mouvements).ThenInclude(m => m.DestinationSite)
        .Include(f => f.Mouvements).ThenInclude(m => m.DestinationPersonne)
        .AsSplitQuery();

    private static string? Vide(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
