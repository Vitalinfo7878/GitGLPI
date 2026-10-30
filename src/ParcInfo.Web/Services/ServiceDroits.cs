using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

/// <summary>
/// Droits des comptes (page « Droits »), réservés aux administrateurs principaux de la configuration.
/// Un changement s'applique à la page suivante que la personne ouvre (ou après actualisation).
/// </summary>
public sealed class ServiceDroits(
    IDbContextFactory<ParcDbContext> dbFactory, IMemoryCache cache, IConfiguration config, IAnnuaireMicrosoft annuaire, ILogger<ServiceDroits> log)
{
    private const string CleCache = "droits-acces";
    private const string ActionInitialisation = "Droits initialisés";
    private const string ActionInitialisationNotifications = "Notifications initialisées";

    public static IReadOnlyList<string> AdministrateursDe(IConfiguration config) =>
        (config.GetSection("Authentification:Administrateurs").Get<string[]>() ?? []).Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).ToList();

    public IReadOnlyList<string> Administrateurs => AdministrateursDe(config);

    /// <summary>Droits du compte (une de ses adresses), lus dans un cache vidé à chaque modification.</summary>
    public static async Task<DroitAcces?> DroitDeAsync(IDbContextFactory<ParcDbContext> dbFactory, IMemoryCache? cache, IEnumerable<string> adresses)
    {
        if (cache is null || !cache.TryGetValue(CleCache, out Dictionary<string, DroitAcces>? parAdresse) || parAdresse is null)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            parAdresse = (await db.DroitsAcces.AsNoTracking().ToListAsync()).ToDictionary(d => d.Email, StringComparer.OrdinalIgnoreCase);
            cache?.Set(CleCache, parAdresse, TimeSpan.FromMinutes(5));
        }
        return adresses.Select(a => parAdresse.GetValueOrDefault(a)).FirstOrDefault(d => d is not null);
    }

    /// <summary>Adresses des valideurs (destinataires des boutons Valider / Refuser et des relances).</summary>
    public static async Task<List<string>> ValideursAsync(ParcDbContext db, CancellationToken ct = default) =>
        await db.DroitsAcces.Where(d => d.Valideur).OrderBy(d => d.Email).Select(d => d.Email).ToListAsync(ct);

    /// <summary>Adresses abonnées à une notification par mail.</summary>
    public static async Task<List<string>> AbonnesAsync(ParcDbContext db, Notification n, CancellationToken ct = default) => n switch
    {
        Notification.ReceptionAchat => await db.DroitsAcces.Where(d => d.NotifReceptionAchat).OrderBy(d => d.Email).Select(d => d.Email).ToListAsync(ct),
        Notification.NouveauMateriel => await db.DroitsAcces.Where(d => d.NotifNouveauMateriel).OrderBy(d => d.Email).Select(d => d.Email).ToListAsync(ct),
        _ => [],
    };

    public async Task<List<DroitAcces>> ListerAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DroitsAcces.AsNoTracking().OrderBy(d => d.Nom ?? d.Email).ToListAsync(ct);
    }

    public async Task<DroitAcces> AjouterAsync(string? email, string? nom, Utilisateur u, CancellationToken ct = default)
    {
        Verifier(u);
        var e = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e) || !FormulaireDemande.EmailValide(e)) throw new ErreurMetier("Adresse mail invalide.");
        if (Administrateurs.Contains(e)) throw new ErreurMetier($"{e} est administrateur principal : il a déjà tous les droits.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.DroitsAcces.AnyAsync(d => d.Email == e, ct)) throw new ErreurMetier($"{e} est déjà dans la liste.");
        var d = new DroitAcces { Email = e, Nom = string.IsNullOrWhiteSpace(nom) ? null : nom.Trim(), ModifiePar = u.Signature, ModifieLe = DateTime.UtcNow };
        db.DroitsAcces.Add(d);
        db.Journal.Add(new Journal { Date = d.ModifieLe, Utilisateur = u.Signature, Action = "Droits", Details = $"{d.Nom ?? e} ajouté à la liste des droits" });
        await db.SaveChangesAsync(ct);
        cache.Remove(CleCache);
        return d;
    }

    public async Task ModifierAsync(int id, Droit droit, bool valeur, Utilisateur u, CancellationToken ct = default)
    {
        Verifier(u);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var d = await db.DroitsAcces.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ErreurMetier("Compte introuvable.");
        if (d.A(droit) == valeur) return;
        d.Definir(droit, valeur);
        d.ModifiePar = u.Signature;
        d.ModifieLe = DateTime.UtcNow;
        db.Journal.Add(new Journal
        {
            Date = d.ModifieLe, Utilisateur = u.Signature, Action = "Droits",
            Details = $"{d.Nom ?? d.Email} : « {droit.Libelle()} » {(valeur ? "accordé" : "retiré")}",
        });
        await db.SaveChangesAsync(ct);
        cache.Remove(CleCache);
    }

    public async Task ModifierAsync(int id, Notification notification, bool valeur, Utilisateur u, CancellationToken ct = default)
    {
        Verifier(u);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var d = await db.DroitsAcces.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ErreurMetier("Compte introuvable.");
        if (d.A(notification) == valeur) return;
        d.Definir(notification, valeur);
        d.ModifiePar = u.Signature;
        d.ModifieLe = DateTime.UtcNow;
        db.Journal.Add(new Journal
        {
            Date = d.ModifieLe, Utilisateur = u.Signature, Action = "Droits",
            Details = $"{d.Nom ?? d.Email} : notification « {notification.Libelle()} » {(valeur ? "activée" : "désactivée")}",
        });
        await db.SaveChangesAsync(ct);
        cache.Remove(CleCache);
    }

    public async Task SupprimerAsync(int id, Utilisateur u, CancellationToken ct = default)
    {
        Verifier(u);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var d = await db.DroitsAcces.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ErreurMetier("Compte introuvable.");
        db.DroitsAcces.Remove(d);
        db.Journal.Add(new Journal { Date = DateTime.UtcNow, Utilisateur = u.Signature, Action = "Droits", Details = $"{d.Nom ?? d.Email} retiré de la liste (plus aucun droit)" });
        await db.SaveChangesAsync(ct);
        cache.Remove(CleCache);
    }

    /// <summary>
    /// Une seule fois, à la mise en service de la page : reprend les droits existants, c'est-à-dire les rôles d'application
    /// Entra ID (Gestionnaire, Valideur) et les listes de la configuration (« Authentification:Gestionnaires », « Demandes:Valideurs »,
    /// « Tickets:Administrateurs »). Les valideurs reçoivent aussi la consultation de l'inventaire.
    /// </summary>
    public async Task<int> InitialiserAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.DroitsAcces.AnyAsync(ct) || await db.Journal.AnyAsync(j => j.Action == ActionInitialisation, ct)) return 0;

        var droits = new Dictionary<string, DroitAcces>(StringComparer.OrdinalIgnoreCase);
        void Donner(string? email, string? nom, params Droit[] liste)
        {
            var e = email?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(e) || Administrateurs.Contains(e)) return;
            if (!droits.TryGetValue(e, out var d)) droits[e] = d = new DroitAcces { Email = e, ModifiePar = ActionInitialisation, ModifieLe = DateTime.UtcNow };
            d.Nom ??= string.IsNullOrWhiteSpace(nom) ? null : nom.Trim();
            foreach (var x in liste) d.Definir(x, true);
        }

        foreach (var e in config.GetSection("Authentification:Gestionnaires").Get<string[]>() ?? []) Donner(e, null, Droit.Gestionnaire);
        foreach (var e in config.GetSection("Demandes:Valideurs").Get<string[]>() ?? []) Donner(e, null, Droit.Valideur, Droit.ConsultationInventaire);
        foreach (var e in config.GetSection("Tickets:Administrateurs").Get<string[]>() ?? []) Donner(e, null, Droit.AdminTickets);
        try
        {
            foreach (var r in await annuaire.LireRolesApplicationAsync(ct))
            {
                if (r.Role == Roles.Gestionnaire) Donner(r.Email, r.Nom, Droit.Gestionnaire);
                else if (r.Role == Roles.Valideur) Donner(r.Email, r.Nom, Droit.Valideur, Droit.ConsultationInventaire);
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Rôles d'application Entra ID illisibles : seuls les droits de la configuration sont repris"); }

        // Noms d'affichage connus de l'annuaire
        var comptes = await db.ComptesMicrosoft.AsNoTracking().Where(c => !c.Supprime).ToListAsync(ct);
        foreach (var d in droits.Values.Where(d => d.Nom is null))
            d.Nom = comptes.FirstOrDefault(c => c.Adresses.Contains(d.Email))?.NomAffichage;

        db.DroitsAcces.AddRange(droits.Values);
        db.Journal.Add(new Journal
        {
            Date = DateTime.UtcNow, Utilisateur = "Application", Action = ActionInitialisation,
            Details = droits.Count == 0 ? "aucun droit à reprendre"
                : JournalInventaire.Tronquer(string.Join(" ; ", droits.Values.Select(d => $"{d.Nom ?? d.Email} : {string.Join(", ", d.Droits.Select(x => x.Libelle()))}"))),
        });
        await db.SaveChangesAsync(ct);
        cache.Remove(CleCache);
        return droits.Count;
    }

    /// <summary>
    /// Une seule fois : abonne les adresses de la configuration (« Notifications:ReceptionAchat », « Notifications:NouveauMateriel »),
    /// en les ajoutant à la liste si besoin. Les abonnements se gèrent ensuite sur la page « Droits ».
    /// </summary>
    public async Task<int> InitialiserNotificationsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Journal.AnyAsync(j => j.Action == ActionInitialisationNotifications, ct)) return 0;
        var existants = await db.DroitsAcces.ToListAsync(ct);
        var comptes = await db.ComptesMicrosoft.AsNoTracking().Where(c => !c.Supprime).ToListAsync(ct);
        var abonnes = new List<string>();
        foreach (var n in Enum.GetValues<Notification>())
        {
            foreach (var adresse in config.GetSection($"Notifications:{n}").Get<string[]>() ?? [])
            {
                var e = adresse.Trim().ToLowerInvariant();
                if (e.Length == 0) continue;
                var d = existants.FirstOrDefault(x => x.Email == e);
                if (d is null)
                {
                    d = new DroitAcces { Email = e, Nom = comptes.FirstOrDefault(c => c.Adresses.Contains(e))?.NomAffichage, ModifiePar = ActionInitialisationNotifications, ModifieLe = DateTime.UtcNow };
                    db.DroitsAcces.Add(d);
                    existants.Add(d);
                }
                d.Definir(n, true);
                abonnes.Add($"{d.Nom ?? e} : {n.Libelle()}");
            }
        }
        db.Journal.Add(new Journal
        {
            Date = DateTime.UtcNow, Utilisateur = "Application", Action = ActionInitialisationNotifications,
            Details = abonnes.Count == 0 ? "aucun abonnement à reprendre" : JournalInventaire.Tronquer(string.Join(" ; ", abonnes)),
        });
        await db.SaveChangesAsync(ct);
        cache.Remove(CleCache);
        return abonnes.Count;
    }

    private static void Verifier(Utilisateur u)
    {
        if (!u.EstAdministrateur) throw new ErreurMetier("Réservé aux administrateurs principaux.");
    }
}
