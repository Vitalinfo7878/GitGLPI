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
    /// <summary>Attribution : adresse mail du destinataire (obligatoire), enregistrée sur la personne ou le site.</summary>
    public string? EmailDetenteur { get; set; }
    /// <summary>Correction du détenteur d'un matériel déjà attribué (erreur de saisie ou d'import) : mouvement tracé, sans fiche.</summary>
    public bool Correction { get; set; }
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
    /// <summary>Catégorie appliquée aux PC reçus (facultatif).</summary>
    public CategoriePc? CategoriePc { get; set; }
    public string? DocumentReference { get; set; }
    public string? Commentaire { get; set; }
}

public sealed class ErreurMetier(string message) : Exception(message);

/// <summary>Matériel arrivé hors achat et jamais inventorié (retour, matériel retrouvé…) : entre directement en stock.</summary>
public sealed class MaterielNonInventorie
{
    public TypeMateriel Type { get; set; } = TypeMateriel.PcPortable;
    /// <summary>Nom du PC (obligatoire pour les PC) ; vide pour un écran ou une imprimante = numéro suivant de la nomenclature.</summary>
    public string? NumInterne { get; set; }
    public string? Modele { get; set; }
    public string? NumSerie { get; set; }
    public CategoriePc? CategoriePc { get; set; }
    /// <summary>D'où vient le matériel (rendu par…, retrouvé sur le site…).</summary>
    public string? Provenance { get; set; }
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string? Commentaire { get; set; }
}

public sealed record ResultatMouvement(List<int> Numeros, Fiche? Fiche, string? Avertissement);

public sealed class ServiceMouvements(
    IDbContextFactory<ParcDbContext> dbFactory, GenerateurFiche generateur, ILogger<ServiceMouvements> log)
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
        // Champs obligatoires : fournisseur ; pour chaque ligne n° de facture et n° de série, et nom (n° interne) des PC
        var manquants = new List<string>();
        if (string.IsNullOrWhiteSpace(demande.Fournisseur)) manquants.Add("le fournisseur");
        void Lignes(string libelle, Func<NouveauMateriel, bool> manque)
        {
            var lignes = demande.Materiels.Select((n, i) => (n, i)).Where(x => manque(x.n)).Select(x => x.i + 1).ToList();
            if (lignes.Count > 0) manquants.Add($"{libelle} (ligne {string.Join(", ", lignes)})");
        }
        Lignes("le nom du PC", n => n.Type is TypeMateriel.PcFixe or TypeMateriel.PcPortable && string.IsNullOrWhiteSpace(n.NumInterne));
        Lignes("le n° de série", n => string.IsNullOrWhiteSpace(n.NumSerie));
        Lignes("le n° de facture", n => string.IsNullOrWhiteSpace(n.NumFacture));
        if (manquants.Count > 0) throw new ErreurMetier($"À renseigner : {string.Join(" ; ", manquants)}.");
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
                CategoriePc = n.Type is TypeMateriel.PcFixe or TypeMateriel.PcPortable ? demande.CategoriePc : null,
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
        }, auteur, ct);
    }

    /// <summary>
    /// Ajoute au parc un matériel qui n'avait pas été inventorié : il entre en stock avec un mouvement
    /// « Entrée initiale (inventaire) », sans fiche ni facture. Il peut ensuite être attribué par une sortie.
    /// </summary>
    public async Task<Materiel> AjouterMaterielNonInventorieAsync(MaterielNonInventorie d, Utilisateur auteur, CancellationToken ct = default)
    {
        if (!auteur.EstGestionnaire) throw new ErreurMetier("Réservé au service informatique.");
        var estPc = d.Type is TypeMateriel.PcFixe or TypeMateriel.PcPortable;
        var manquants = new List<string>();
        if (estPc && string.IsNullOrWhiteSpace(d.NumInterne)) manquants.Add("le nom du PC");
        if (string.IsNullOrWhiteSpace(d.NumSerie)) manquants.Add("le n° de série");
        if (manquants.Count > 0) throw new ErreurMetier($"À renseigner : {string.Join(" ; ", manquants)}.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var num = string.IsNullOrWhiteSpace(d.NumInterne) ? await ProchainNumInterneAsync(d.Type) : d.NumInterne.Trim().ToUpperInvariant();
        if (await db.Materiels.AnyAsync(m => m.NumInterne == num, ct)) throw new ErreurMetier($"Le numéro interne {num} existe déjà.");
        var maintenant = DateTime.UtcNow;
        var m = new Materiel
        {
            NumInterne = num, Type = d.Type, Modele = Vide(d.Modele), NumSerie = Vide(d.NumSerie), Statut = StatutMateriel.EnStock,
            CategoriePc = estPc ? d.CategoriePc : null, Commentaire = Vide(d.Commentaire), CreeLe = maintenant, ModifieLe = maintenant,
        };
        m.Mouvements.Add(new Mouvement
        {
            Numero = (await db.Mouvements.MaxAsync(x => (int?)x.Numero, ct) ?? 0) + 1,
            Type = TypeMouvement.EntreeInitiale, Date = d.Date, Expediteur = Vide(d.Provenance),
            DestinationLibre = RegleMouvement.ServiceInformatique, Commentaire = "Matériel non inventorié ajouté au parc",
            CreePar = auteur.Signature, CreeLe = maintenant,
        });
        db.Materiels.Add(m);
        db.Journal.Add(new Journal { Date = maintenant, Utilisateur = auteur.Signature, Action = "Nouveau matériel (non inventorié)",
            Details = $"{num} – {d.Type.Libelle()} {m.Modele} (n° série {m.NumSerie})" });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ErreurMetier("Un autre enregistrement vient d'avoir lieu en même temps : réessayez."); }
        return m;
    }

    public const string MentionCorrection = "Correction du détenteur (sans fiche)";

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

        if (d.Correction && (d.Type != TypeMouvement.Attribution || materiels.Any(m => m.Statut != StatutMateriel.Attribue)))
            throw new ErreurMetier("La correction sans fiche ne s'applique qu'au changement de détenteur d'un matériel déjà attribué.");

        var site = d.DestinationSiteId is { } sid ? await db.Sites.Include(s => s.Region).FirstAsync(s => s.Id == sid, ct) : null;
        var personne = d.DestinationPersonneId is { } pid ? await db.Personnes.Include(p => p.Region).FirstAsync(p => p.Id == pid, ct) : null;
        if (site != null && personne != null) throw new ErreurMetier("Choisissez un site OU une personne, pas les deux.");
        if (regle.DestinationEstDetenteur && site == null && personne == null)
            throw new ErreurMetier("Choisissez le site ou la personne destinataire.");

        // Adresse du destinataire d'une attribution, obligatoire : enregistrée sur la personne ou le site,
        // elle donne accès à « Mon matériel » et au téléchargement de la fiche.
        string? emailDestinataire = null;
        if (regle.DestinationEstDetenteur)
        {
            emailDestinataire = (Vide(d.EmailDetenteur) ?? personne?.Email ?? site?.Email)?.ToLowerInvariant();
            if (emailDestinataire is null) throw new ErreurMetier("L'adresse mail du destinataire est obligatoire.");
            if (!FormulaireDemande.EmailValide(emailDestinataire)) throw new ErreurMetier($"Adresse mail invalide : {emailDestinataire}.");
            if (personne != null) personne.Email = emailDestinataire;
            if (site != null) site.Email = emailDestinataire;
        }

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
                Commentaire = d.Correction ? string.Join(" — ", new[] { MentionCorrection, Vide(d.Commentaire) }.OfType<string>()) : Vide(d.Commentaire),
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
        if (regle.Fiche is { } typeFiche && !d.Correction)
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
                CreePar = auteur.Signature,
                CreeLe = maintenant,
                Mouvements = mouvements,
            };
            db.Fiches.Add(fiche);
        }

        await ServiceTickets.NoterMouvementsAsync(db, mouvements, auteur, maintenant, ct);
        db.Journal.Add(new Journal
        {
            Date = maintenant, Utilisateur = auteur.Signature, Action = d.Correction ? "Correction du détenteur" : d.Type.Libelle(),
            MaterielId = materiels.Count == 1 ? materiels[0].Id : null,
            Details = $"{((site?.Libelle ?? personne?.NomComplet ?? mouvements[0].DestinationLibre) is { } vers ? $"Vers {vers} – " : "")}Mouvements {mouvements[0].Numero}–{mouvements[^1].Numero} : {string.Join(", ", materiels.Select(m => m.NumInterne))}" +
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
            // Le mouvement est déjà enregistré : un problème de PDF ne doit pas le faire échouer.
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
        }
        return new ResultatMouvement(mouvements.Select(m => m.Numero).ToList(), fiche, avertissement);
    }

    private static async Task<string?> RegionDe(ParcDbContext db, Materiel m, CancellationToken ct) =>
        m.RegionId is { } id ? await db.Regions.Where(r => r.Id == id).Select(r => r.Nom).FirstOrDefaultAsync(ct) : null;

    /// <summary>
    /// Supprime définitivement un matériel et son historique de mouvements. Les fiches PDF déjà émises sont conservées,
    /// les tickets restent avec le n° interne saisi ; la suppression est tracée dans le journal.
    /// </summary>
    public async Task SupprimerMaterielAsync(int id, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstGestionnaire) throw new ErreurMetier("Réservé au service informatique.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var m = await db.Materiels.Include(x => x.Mouvements).Include(x => x.Personne).Include(x => x.Site).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ErreurMetier("Matériel introuvable.");
        db.Materiels.Remove(m);
        db.Journal.Add(new Journal
        {
            Date = DateTime.UtcNow, Utilisateur = u.Signature, Action = JournalInventaire.MaterielSupprime, MaterielId = m.Id,
            Instantane = InstantaneMateriel.Depuis(m, m.Personne?.Email ?? m.Site?.Email, m.Mouvements).Serialiser(),
            Details = $"{m.NumInterne} – {m.Type.Libelle()} {m.Modele} (n° série {m.NumSerie ?? "?"}, statut {m.Statut.Libelle()}, " +
                      $"{m.Mouvements.Count} mouvement(s) supprimé(s))",
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Restaure depuis le journal d'inventaire : annule une suppression (matériel et mouvements recréés), ou remet les valeurs
    /// d'avant une modification. La restauration est elle-même tracée, et une restauration de modification peut être annulée à son tour.
    /// </summary>
    public async Task<string> RestaurerAsync(int journalId, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstGestionnaire) throw new ErreurMetier("Réservé au service informatique.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var j = await db.Journal.FirstOrDefaultAsync(x => x.Id == journalId, ct) ?? throw new ErreurMetier("Entrée du journal introuvable.");
        if (j.Instantane is null) throw new ErreurMetier("Cette entrée ne peut pas être restaurée.");
        if (j.RestaureLe is not null) throw new ErreurMetier($"Déjà restauré par {j.RestaurePar}.");
        var inst = InstantaneMateriel.Lire(j.Instantane);
        var maintenant = DateTime.UtcNow;
        var trace = new Journal { Date = maintenant, Utilisateur = u.Signature, Action = JournalInventaire.MaterielRestaure };
        string message;

        if (j.Action == JournalInventaire.MaterielSupprime)
        {
            if (await db.Materiels.AnyAsync(m => m.NumInterne == inst.NumInterne, ct))
                throw new ErreurMetier($"Le n° interne {inst.NumInterne} est repris par un autre matériel : renommez-le d'abord.");
            // Même identifiant qu'avant la suppression (jamais réattribué) : l'historique du journal et les liens restent rattachés
            var ancienId = j.MaterielId is { } a && !await db.Materiels.AnyAsync(x => x.Id == a, ct) ? a : 0;
            var m = new Materiel { Id = ancienId, Statut = inst.Statut, CreeLe = inst.CreeLe, ModifieLe = maintenant };
            inst.AppliquerChampsA(m);
            // Détenteur, région ou fiche supprimés depuis : laissés vides
            m.SiteId = inst.SiteId is { } sid && await db.Sites.AnyAsync(x => x.Id == sid, ct) ? sid : null;
            m.PersonneId = inst.PersonneId is { } pid && await db.Personnes.AnyAsync(x => x.Id == pid, ct) ? pid : null;
            if (m.RegionId is { } rid && !await db.Regions.AnyAsync(x => x.Id == rid, ct)) m.RegionId = null;
            var sites = await db.Sites.Select(x => x.Id).ToListAsync(ct);
            var personnes = await db.Personnes.Select(x => x.Id).ToListAsync(ct);
            var fiches = await db.Fiches.Select(x => x.Id).ToListAsync(ct);
            var numeros = (await db.Mouvements.Select(x => x.Numero).ToListAsync(ct)).ToHashSet();
            var prochain = numeros.DefaultIfEmpty(0).Max() + 1;
            foreach (var x in inst.Mouvements)
                m.Mouvements.Add(new Mouvement
                {
                    Numero = numeros.Contains(x.Numero) ? prochain++ : x.Numero, NumeroOrigine = x.NumeroOrigine, Type = x.Type, Date = x.Date,
                    Expediteur = x.Expediteur, DestinationLibre = x.DestinationLibre, Region = x.Region, DocumentReference = x.DocumentReference,
                    DestinationSiteId = x.DestinationSiteId is { } ds && sites.Contains(ds) ? ds : null,
                    DestinationPersonneId = x.DestinationPersonneId is { } dp && personnes.Contains(dp) ? dp : null,
                    FicheId = x.FicheId is { } f && fiches.Contains(f) ? f : null,
                    DateReception = x.DateReception, Commentaire = x.Commentaire, CreePar = x.CreePar, CreeLe = x.CreeLe,
                });
            db.Materiels.Add(m);
            await db.SaveChangesAsync(ct);
            trace.MaterielId = m.Id;
            trace.Details = $"{m.NumInterne} : suppression annulée ({m.Mouvements.Count} mouvement(s) restauré(s))";
            message = $"{m.NumInterne} restauré avec ses {m.Mouvements.Count} mouvement(s).";
        }
        else if (j.Action is JournalInventaire.MaterielModifie or JournalInventaire.MaterielRestaure)
        {
            var m = await db.Materiels.Include(x => x.Personne).Include(x => x.Site).FirstOrDefaultAsync(x => x.Id == j.MaterielId, ct)
                ?? throw new ErreurMetier("Ce matériel a été supprimé depuis : restaurez d'abord sa suppression.");
            if (inst.NumInterne != m.NumInterne && await db.Materiels.AnyAsync(x => x.NumInterne == inst.NumInterne && x.Id != m.Id, ct))
                throw new ErreurMetier($"Le n° interne {inst.NumInterne} est repris par un autre matériel : renommez-le d'abord.");
            var regions = await db.Regions.ToDictionaryAsync(r => r.Id, r => r.Nom, ct);
            var emailActuel = m.Personne?.Email ?? m.Site?.Email;
            var avant = (Materiel)db.Entry(m).CurrentValues.ToObject();
            trace.Instantane = InstantaneMateriel.Depuis(m, emailActuel).Serialiser();
            inst.AppliquerChampsA(m);
            var changements = JournalInventaire.Differences(avant, m, id => id is { } r ? regions.GetValueOrDefault(r) : null);
            // Adresse du détenteur : seulement s'il s'agit toujours du même détenteur
            if (inst.EmailDetenteur is { } e && m.SiteId == inst.SiteId && m.PersonneId == inst.PersonneId
                && !string.Equals(e, emailActuel, StringComparison.OrdinalIgnoreCase))
            {
                if (m.Personne is not null) m.Personne.Email = e;
                else if (m.Site is not null) m.Site.Email = e;
                changements.Add($"Mail de {m.DetenteurLibelle} : {emailActuel ?? "—"} → {e}");
            }
            if (changements.Count == 0) throw new ErreurMetier("Le matériel a déjà ces valeurs : rien à restaurer.");
            m.ModifieLe = maintenant;
            trace.MaterielId = m.Id;
            trace.Details = JournalInventaire.Tronquer($"{m.NumInterne} : valeurs d'avant la modification de {j.Utilisateur.Split(" <")[0]} du {j.Date:dd/MM/yyyy} remises ; {string.Join(" ; ", changements)}");
            message = $"{m.NumInterne} : {changements.Count} valeur(s) restaurée(s).";
        }
        else throw new ErreurMetier("Cette entrée ne peut pas être restaurée.");

        j.RestaureLe = maintenant;
        j.RestaurePar = u.Signature;
        db.Journal.Add(trace);
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>Range des PC dans une catégorie (PC Site / PC Enrôlé), ou l'efface ; les autres matériels sont ignorés.</summary>
    public async Task<int> DefinirCategoriePcAsync(IReadOnlyCollection<int> ids, CategoriePc? categorie, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstGestionnaire) throw new ErreurMetier("Réservé au service informatique.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pcs = await db.Materiels
            .Where(m => ids.Contains(m.Id) && (m.Type == TypeMateriel.PcFixe || m.Type == TypeMateriel.PcPortable))
            .ToListAsync(ct);
        foreach (var m in pcs)
        {
            m.CategoriePc = categorie;
            m.ModifieLe = DateTime.UtcNow;
        }
        db.Journal.Add(new Journal
        {
            Date = DateTime.UtcNow, Utilisateur = u.Signature, Action = "Catégorie PC",
            Details = $"{categorie?.Libelle() ?? "non renseignée"} : {string.Join(", ", pcs.Select(m => m.NumInterne))}",
        });
        await db.SaveChangesAsync(ct);
        return pcs.Count;
    }

    public async Task RegenererPdfAsync(int ficheId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var fiche = await ChargerFiche(db).FirstAsync(f => f.Id == ficheId, ct);
        fiche.Pdf = generateur.Generer(fiche);
        fiche.PdfGenereLe = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public static IQueryable<Fiche> ChargerFiche(ParcDbContext db) => db.Fiches
        .Include(f => f.Mouvements).ThenInclude(m => m.Materiel)
        .Include(f => f.Mouvements).ThenInclude(m => m.DestinationSite)
        .Include(f => f.Mouvements).ThenInclude(m => m.DestinationPersonne)
        .AsSplitQuery();

    private static string? Vide(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
