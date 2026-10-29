using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

/// <summary>Réponses au formulaire « Créer un ticket ».</summary>
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

/// <summary>Photo envoyée depuis le navigateur (déjà réduite).</summary>
public sealed record PhotoEnvoyee(string Nom, string TypeContenu, byte[] Contenu);

/// <summary>Contrôle des photos jointes aux tickets : vraies images (JPEG, PNG, WebP, GIF), taille et nombre limités.</summary>
public static class Photos
{
    public const int MaxParEnvoi = 6;
    public const int TailleMax = 5 * 1024 * 1024;

    /// <summary>Type réel d'après le contenu du fichier (et non son nom ou le type annoncé).</summary>
    public static string? TypeReel(ReadOnlySpan<byte> o) =>
        o.Length >= 3 && o[0] == 0xFF && o[1] == 0xD8 && o[2] == 0xFF ? "image/jpeg"
        : o.Length >= 8 && o[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : o.Length >= 6 && (o[..6].SequenceEqual("GIF87a"u8) || o[..6].SequenceEqual("GIF89a"u8)) ? "image/gif"
        : o.Length >= 12 && o[..4].SequenceEqual("RIFF"u8) && o[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    public static List<PhotoTicket> Verifier(IReadOnlyCollection<PhotoEnvoyee>? photos, Utilisateur u, DateTime maintenant)
    {
        if (photos is null || photos.Count == 0) return [];
        if (photos.Count > MaxParEnvoi) throw new ErreurMetier($"{MaxParEnvoi} photos au maximum par envoi.");
        return photos.Select(p =>
        {
            var type = TypeReel(p.Contenu) ?? throw new ErreurMetier($"« {p.Nom} » n'est pas une photo (JPEG, PNG, WebP ou GIF).");
            if (p.Contenu.Length > TailleMax) throw new ErreurMetier($"« {p.Nom} » est trop lourde (5 Mo au maximum).");
            var nom = Path.GetFileName(p.Nom.Replace('\\', '/')); // chemin Windows éventuel
            return new PhotoTicket
            {
                Nom = string.IsNullOrWhiteSpace(nom) ? "photo" : nom.Length > 200 ? nom[..200] : nom,
                TypeContenu = type, Taille = p.Contenu.Length, Contenu = p.Contenu, AjouteeLe = maintenant, AjouteePar = u.Signature,
            };
        }).ToList();
    }
}

/// <summary>Photo d'un ticket sans son contenu (affichage de la page).</summary>
public sealed record ApercuPhoto(int Id, int? MessageId, string Nom);

public sealed class ServiceTickets(IDbContextFactory<ParcDbContext> dbFactory)
{
    /// <summary>Peut voir le ticket : son auteur / demandeur et les administrateurs des tickets.</summary>
    public static bool PeutVoir(Utilisateur u, Ticket t) => u.EstAdminTickets || EstDemandeur(u, t);

    public static bool EstDemandeur(Utilisateur u, Ticket t) => !t.Interne && (u.AEmail(t.SaisiePar) || u.AEmail(t.DemandeurEmail));

    public async Task<Ticket> CreerAsync(FormulaireTicket f, Utilisateur auteur, CancellationToken ct = default, IReadOnlyCollection<PhotoEnvoyee>? photos = null)
    {
        // Le mail du demandeur est toujours celui du compte connecté (non modifiable dans le formulaire)
        f.DemandeurEmail = auteur.Email;
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
        var jointes = Photos.Verifier(photos, auteur, maintenant);

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
        foreach (var p in jointes) p.TicketId = t.Id;
        db.PhotosTickets.AddRange(jointes);
        await db.SaveChangesAsync(ct);
        return t;
    }

    /// <summary>Photos visibles par l'utilisateur (sans leur contenu) : celles des notes internes seulement pour l'informatique.</summary>
    public async Task<List<ApercuPhoto>> ListerPhotosAsync(int ticketId, Utilisateur u, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PhotosTickets.AsNoTracking()
            .Where(p => p.TicketId == ticketId && (u.EstAdminTickets || p.MessageId == null || !p.Message!.Interne))
            .OrderBy(p => p.Id).Select(p => new ApercuPhoto(p.Id, p.MessageId, p.Nom)).ToListAsync(ct);
    }

    /// <summary>Contenu d'une photo, si l'utilisateur peut voir le ticket (et la note interne à laquelle elle est jointe).</summary>
    public async Task<PhotoTicket?> LirePhotoAsync(int id, Utilisateur u, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.PhotosTickets.AsNoTracking().Include(x => x.Ticket).Include(x => x.Message).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null || !PeutVoir(u, p.Ticket) || (p.Message?.Interne == true && !u.EstAdminTickets)) return null;
        return p;
    }

    /// <summary>Message du demandeur ou du service informatique ; une note interne n'est visible que des administrateurs.</summary>
    public async Task AjouterMessageAsync(int id, string? texte, bool interne, Utilisateur u, CancellationToken ct = default, IReadOnlyCollection<PhotoEnvoyee>? photos = null)
    {
        if (string.IsNullOrWhiteSpace(texte) && (photos is null || photos.Count == 0)) throw new ErreurMetier("Le message est vide.");
        if (texte?.Trim().Length > 4000) throw new ErreurMetier("Message trop long (4000 caractères maximum).");
        var jointes = Photos.Verifier(photos, u, DateTime.UtcNow);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await ChargerAsync(db, id, u, ct);
        if (interne && !u.EstAdminTickets) throw new ErreurMetier("Réservé au service informatique.");
        if (t.Statut == StatutTicket.Clos) throw new ErreurMetier("Ce ticket est clos : ouvrez un nouveau ticket si besoin.");

        var maintenant = DateTime.UtcNow;
        var message = Message(u, string.IsNullOrWhiteSpace(texte) ? (jointes.Count == 1 ? "Photo jointe" : $"{jointes.Count} photos jointes") : texte.Trim(), interne, maintenant);
        t.Messages.Add(message);
        foreach (var p in jointes)
        {
            p.Ticket = t;
            p.Message = message;
        }
        db.PhotosTickets.AddRange(jointes);
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
        if (!string.Equals(Vide(m.MaterielReference), t.MaterielReference, StringComparison.OrdinalIgnoreCase))
        {
            var materielAvant = t.MaterielReference;
            await LierMaterielAsync(db, t, m.MaterielReference, ct);
            if (t.DemandeId is { } demandeId)
                await CorrigerRapatriementAsync(db, t, demandeId, materielAvant, u, maintenant, ct);
        }
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

    // --- Rapatriement de l'ancien matériel (demande de remplacement) ---

    /// <summary>
    /// Correction de l'ancien matériel d'un ticket de rapatriement : il doit exister dans l'inventaire ; le titre du ticket et
    /// la demande (« Ancien matériel ») suivent, et la correction est notée sur le ticket.
    /// </summary>
    private static async Task CorrigerRapatriementAsync(ParcDbContext db, Ticket t, int demandeId, string? ancien, Utilisateur u, DateTime maintenant, CancellationToken ct)
    {
        if (t.MaterielReference is not null && t.MaterielId is null)
            throw new ErreurFormulaire(new() { [nameof(MiseAJourTicket.MaterielReference)] = $"{t.MaterielReference} est introuvable dans l'inventaire." });
        var d = await db.Demandes.FirstAsync(x => x.Id == demandeId, ct);
        var materiel = t.MaterielId is { } id ? await db.Materiels.FirstAsync(x => x.Id == id, ct) : null;
        t.Titre = Tronquer($"Rapatriement de l'ancien matériel {materiel?.NumInterne ?? "(à identifier)"} – demande n° {d.Numero}", 150)!;
        var nouveau = materiel is null ? "à identifier" : $"{materiel.NumInterne} – {materiel.Type.Libelle()} {materiel.Modele}".Trim();
        t.Messages.Add(Trace(u, $"Ancien matériel corrigé : {ancien ?? "à identifier"} → {nouveau}", maintenant));

        // Ancien matériel de la demande : celui de tous ses tickets de rapatriement
        var autres = await db.Tickets.Where(x => x.DemandeId == demandeId && x.Id != t.Id && x.MaterielId != null).Select(x => x.MaterielId!.Value).ToListAsync(ct);
        var ids = autres.Concat(materiel is null ? [] : [materiel.Id]).Distinct().ToList();
        var retenus = await db.Materiels.Where(x => ids.Contains(x.Id)).OrderBy(x => x.NumInterne).ToListAsync(ct);
        d.MaterielRemplace = retenus.Count == 0 ? null
            : Tronquer(string.Join(" ; ", retenus.Select(x => $"{x.NumInterne} – {x.Type.Libelle()} {x.Modele}".Trim())), 200);
        d.MaterielRemplaceId = materiel?.Id ?? retenus.FirstOrDefault()?.Id;
    }

    public const string AuteurApplication = "Application";

    /// <summary>
    /// Ancien matériel à chercher pour chaque objet demandé : type, et s'il ne peut être que sur le site de livraison
    /// (imprimantes et « PC fixe (site) » : seuls les sites en ont). Les comptes et adresses mail n'ont pas d'ancien matériel.
    /// </summary>
    public static IReadOnlyList<(TypeMateriel Type, bool SiteSeulement)> Recherches(Demande d) => d.ListeObjets
        .Select(o => o switch
        {
            "PC fixe (site)" => ((TypeMateriel, bool)?)(TypeMateriel.PcFixe, true),
            "PC fixe (Siège / DR)" => (TypeMateriel.PcFixe, false),
            "PC portable" => (TypeMateriel.PcPortable, false),
            "Ecran" => (TypeMateriel.Ecran, false),
            "Imprimante" => (TypeMateriel.Imprimante, true),
            _ => null,
        })
        .OfType<(TypeMateriel, bool)>().Distinct().ToList();

    /// <summary>Types de matériel demandés (une demande sans matériel n'a ni ancien matériel ni n° interne à saisir).</summary>
    public static IReadOnlyList<TypeMateriel> TypesDemandes(Demande d) => Recherches(d).Select(r => r.Type).Distinct().ToList();

    /// <summary>
    /// Ancien matériel d'un remplacement, par objet demandé : celui du demandeur ou du site de l'adresse de livraison
    /// (seulement le site pour une imprimante ou un PC fixe de site). Un seul candidat : il est retenu ; plusieurs : à identifier parmi eux.
    /// Pour un site, le demandeur est son responsable, jamais le compte du site lui-même.
    /// </summary>
    public static async Task<List<AncienMateriel>> AnciensMaterielsAsync(ParcDbContext db, Demande d, IReadOnlyCollection<string> adresses, CancellationToken ct = default)
    {
        var recherches = Recherches(d);
        if (recherches.Count == 0) return [];
        var sites = SitesDeLAdresse(d.AdresseLivraison, await db.Sites.AsNoTracking().Select(s => new SiteConnu(s.Id, s.Code, s.Nom)).ToListAsync(ct));
        var idsSites = sites.Select(s => s.Id).ToList();
        var types = recherches.Select(r => r.Type).Distinct().ToList();
        var candidats = await db.Materiels.Include(m => m.Personne).Include(m => m.Site)
            .Where(m => m.Statut == StatutMateriel.Attribue && types.Contains(m.Type)
                && (adresses.Contains(m.Personne!.Email!) || adresses.Contains(m.Site!.Email!) || (m.SiteId != null && idsSites.Contains(m.SiteId.Value))))
            .OrderBy(m => m.NumInterne).ToListAsync(ct);
        var codes = sites.Select(s => s.Code).Distinct().Order().ToList();
        return recherches.Select(r =>
        {
            var possibles = candidats.Where(m => m.Type == r.Type && (!r.SiteSeulement || (m.SiteId is { } sid && idsSites.Contains(sid)))).ToList();
            var retenu = possibles.Count == 1 ? possibles[0] : null;
            return new AncienMateriel(r.Type, r.SiteSeulement, retenu, retenu is null ? possibles : [], codes);
        }).ToList();
    }

    public sealed record SiteConnu(int Id, string Code, string? Nom);

    private static readonly HashSet<string> MotsVoie =
    [
        "rue", "avenue", "av", "ave", "bd", "bld", "boulevard", "allee", "allees", "chemin", "che", "chem", "place", "pl", "route", "rte", "impasse", "imp",
        "cours", "quai", "square", "sq", "rond", "lieu", "lieudit", "voie", "passage", "parvis", "esplanade", "promenade", "sentier", "faubourg", "fbg",
        "hameau", "lotissement", "lot", "zi", "za", "zac", "bis", "ter", "quater",
    ];

    private static readonly HashSet<string> MotsCommuns =
    [
        "ehpad", "residence", "vitalrest", "site", "maison", "foyer", "centre", "restaurant", "cuisine", "college", "lycee", "ecole", "clinique",
        "hopital", "retraite", "sante", "accueil", "saint", "sainte", "pour", "avec", "sous", "dans",
    ];

    /// <summary>
    /// Sites cités dans une adresse de livraison : par leur numéro (« Vitalrest 055 », « Site 087 », « 440-1 » ; sous-sites compris),
    /// en ignorant les numéros de rue et les codes postaux ; à défaut, par leur nom (« EHPAD Julie Chauchard »).
    /// </summary>
    public static List<SiteConnu> SitesDeLAdresse(string? adresse, IReadOnlyList<SiteConnu> sites)
    {
        if (string.IsNullOrWhiteSpace(adresse)) return [];
        var texte = ParcInfo.Web.Import.Normalisation.SansAccents(adresse).ToLowerInvariant();
        var codes = new List<string>();
        foreach (Match m in Regex.Matches(texte, @"(?<![\w-])([a-z]?\d+(?:-\d+)?)(?![\w-])"))
        {
            var c = m.Groups[1].Value;
            if (Regex.IsMatch(c, @"^\d{5}$")) continue; // code postal
            var suivant = Regex.Match(texte[(m.Index + m.Length)..], @"^[\s,]*([a-z]+)").Groups[1].Value;
            if (MotsVoie.Contains(suivant)) continue; // numéro de rue
            codes.Add(c);
        }
        var parCode = sites.Where(s => codes.Any(c => MemeCode(s.Code, c)
            || (!c.Contains('-') && s.Code.Contains('-') && MemeCode(s.Code[..s.Code.IndexOf('-')], c)))).ToList();
        if (parCode.Count > 0) return parCode;

        var mots = MotsDe(texte).ToHashSet();
        return sites.Where(s => MotsDe(s.Nom).Where(x => x.Length >= 4 && !MotsCommuns.Contains(x) && !x.All(char.IsDigit)).ToList() is { Count: > 0 } distinctifs
            && distinctifs.All(mots.Contains) && distinctifs.Sum(x => x.Length) >= 6).ToList();
    }

    private static IEnumerable<string> MotsDe(string? s) =>
        Regex.Split(ParcInfo.Web.Import.Normalisation.SansAccents(s ?? "").ToLowerInvariant(), @"[^a-z0-9]+").Where(x => x.Length > 0);

    private static bool MemeCode(string code, string cite) =>
        string.Equals(code, cite, StringComparison.OrdinalIgnoreCase) || string.Equals(code.TrimStart('0'), cite.TrimStart('0'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ticket interne « Rapatriement de l'ancien matériel », créé à la réception d'une demande de remplacement :
    /// les mouvements de l'ancien matériel et les étapes de la demande y sont notés jusqu'à sa clôture.
    /// </summary>
    public static async Task<Ticket> CreerTicketRapatriementAsync(ParcDbContext db, Demande d, AncienMateriel a, CancellationToken ct = default)
    {
        var maintenant = DateTime.UtcNow;
        var ancien = a.Materiel;
        const string identifier = "identifiez l'ancien matériel et renseignez son n° interne dans le traitement du ticket";
        var identifie = ancien is null
            ? a.Possibles.Count > 0
                ? $"{a.Type.Libelle()} à identifier parmi : {string.Join(", ", a.Possibles.Select(m => $"{m.NumInterne} {m.Modele} ({m.DetenteurLibelle})".Replace("  ", " ")))} – {identifier}"
                : a.SiteSeulement
                    ? a.CodesSite.Count == 0
                        ? $"aucun site reconnu dans l'adresse de livraison pour trouver l'ancien matériel ({a.Type.Libelle()}) – {identifier}"
                        : $"aucun matériel « {a.Type.Libelle()} » sur le site {string.Join(", ", a.CodesSite)} dans l'inventaire – {identifier}"
                    : $"aucun {a.Type.Libelle()} attribué à {d.DemandeurEmail}{(a.CodesSite.Count == 0 ? "" : $" ni au site {string.Join(", ", a.CodesSite)}")} dans l'inventaire – {identifier}"
            : $"{ancien.NumInterne} – {ancien.Type.Libelle()} {ancien.Modele}, n° de série {ancien.NumSerie ?? "?"}, statut « {ancien.Statut.Libelle()} »"
              + (string.IsNullOrEmpty(ancien.DetenteurLibelle) ? "" : $", détenu par {ancien.DetenteurLibelle}");
        var description = $"""
            Ticket créé automatiquement pour la demande n° {d.Numero} ({d.Objets}), qui concerne un remplacement.
            Matériel à remplacer : {identifie}
            Demandeur : {d.DemandeurNom} – {d.DemandeurEmail} – {d.DemandeurTelephone}
            Adresse : {d.AdresseLivraison}
            Raison de la demande : {d.Raison}

            À suivre jusqu'au bout :
            1. Récupérer l'ancien matériel.
            2. Enregistrer son retour : mouvement « Entrée (dans le SI) ».
            3. Décider de son devenir (remis en stock, réattribué, mis au rebut…) et l'indiquer dans la solution du ticket.
            """;
        var t = new Ticket
        {
            CreeLe = maintenant, ModifieLe = maintenant, SaisiePar = AuteurApplication, Interne = true, DemandeId = d.Id,
            DemandeurNom = d.DemandeurNom, DemandeurEmail = d.DemandeurEmail, DemandeurTelephone = d.DemandeurTelephone,
            Lieu = Tronquer(d.AdresseLivraison, 400)!,
            Categorie = a.Type switch
            {
                TypeMateriel.PcFixe or TypeMateriel.PcPortable => CategorieTicket.Poste,
                TypeMateriel.Ecran => CategorieTicket.Ecran,
                TypeMateriel.Imprimante => CategorieTicket.Imprimante,
                _ => CategorieTicket.Autre,
            },
            Impact = ImpactTicket.Mineur, Portee = PorteeTicket.Personne, Priorite = PrioriteTicket.Normale, Statut = StatutTicket.Nouveau,
            Titre = Tronquer($"Rapatriement de l'ancien matériel {(ancien is null ? $"({a.Type.Libelle()} à identifier)" : ancien.NumInterne)} – demande n° {d.Numero}", 150)!,
            Description = Tronquer(description, FormulaireTicket.LongueurDescription)!,
            MaterielReference = ancien?.NumInterne, MaterielId = ancien?.Id,
        };
        t.Messages.Add(new TicketMessage { Date = maintenant, Auteur = AuteurApplication, Texte = $"Ticket de rapatriement créé pour la demande n° {d.Numero}", Systeme = true });
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
        db.Journal.Add(new Journal { Date = maintenant, Utilisateur = AuteurApplication, Action = "Nouveau ticket", Details = $"n° {t.Numero} – {t.Titre}", MaterielId = t.MaterielId });
        await db.SaveChangesAsync(ct);
        return t;
    }

    /// <summary>Trace sur les tickets ouverts d'une demande (validation, envoi…) ; un refus clôt le rapatriement.</summary>
    public static async Task NoterSurTicketsDeLaDemandeAsync(ParcDbContext db, int demandeId, Utilisateur u, DateTime maintenant, string texte, bool fermer,
        CancellationToken ct = default)
    {
        foreach (var t in await db.Tickets.Include(x => x.Messages).Where(x => x.DemandeId == demandeId && x.Statut != StatutTicket.Clos).ToListAsync(ct))
        {
            t.Messages.Add(Trace(u, texte, maintenant));
            if (fermer)
            {
                t.Solution ??= texte;
                t.ModeResolution ??= ModeResolution.Autre;
                ChangerStatut(t, StatutTicket.Clos, u, maintenant, "Clôture automatique");
            }
            t.ModifieLe = maintenant;
        }
    }

    /// <summary>
    /// Mouvements notés sur les tickets ouverts du matériel concerné (retour, rebut, réattribution…) :
    /// on suit ainsi le rapatriement jusqu'au devenir du matériel.
    /// </summary>
    public static async Task NoterMouvementsAsync(ParcDbContext db, IEnumerable<Mouvement> mouvements, Utilisateur u, DateTime maintenant, CancellationToken ct = default)
    {
        var parMateriel = mouvements.Where(m => m.Materiel is not null).ToDictionary(m => m.Materiel.Id);
        if (parMateriel.Count == 0) return;
        var ids = parMateriel.Keys.ToList();
        foreach (var t in await db.Tickets.Include(x => x.Messages)
                     .Where(x => x.MaterielId != null && ids.Contains(x.MaterielId.Value) && x.Statut != StatutTicket.Resolu && x.Statut != StatutTicket.Clos).ToListAsync(ct))
        {
            var mv = parMateriel[t.MaterielId!.Value];
            var trajet = string.Join(" → ", new[] { mv.Expediteur, mv.DestinationSite?.Libelle ?? mv.DestinationPersonne?.NomComplet ?? mv.DestinationLibre }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            t.Messages.Add(Trace(u, $"Mouvement n° {mv.Numero} sur {mv.Materiel.NumInterne} : {mv.Type.Libelle()}{(trajet.Length > 0 ? $" ({trajet})" : "")}"
                + $" – statut du matériel : {mv.Materiel.Statut.Libelle()}", maintenant));
            if (t.Statut == StatutTicket.Nouveau) ChangerStatut(t, StatutTicket.EnCours, u, maintenant);
            t.ModifieLe = maintenant;
        }
    }

    // --- Analyse ---

    public async Task<AnalyseTickets> AnalyserAsync(DateTime? depuis, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Les tickets internes (rapatriement…) ne sont pas des demandes d'assistance : hors statistiques
        var q = db.Tickets.AsNoTracking().Where(t => !t.Interne);
        if (depuis is { } d) q = q.Where(t => t.CreeLe >= d);
        var tickets = await q.Select(t => new LigneAnalyse(t.CreeLe, t.PrisEnChargeLe, t.ResoluLe, t.Statut, t.Categorie, t.Cause, t.ModeResolution,
            t.Lieu, t.MaterielId == null ? null : t.Materiel!.NumInterne, t.TempsPasseMinutes, t.SatisfactionNote, t.NbReouvertures)).ToListAsync(ct);
        var ouverts = await db.Tickets.CountAsync(t => !t.Interne && t.Statut != StatutTicket.Resolu && t.Statut != StatutTicket.Clos, ct);
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

/// <summary>Ancien matériel d'un type demandé en remplacement : retenu s'il est unique, sinon les matériels possibles.</summary>
public sealed record AncienMateriel(TypeMateriel Type, bool SiteSeulement, Materiel? Materiel, List<Materiel> Possibles, List<string> CodesSite);
