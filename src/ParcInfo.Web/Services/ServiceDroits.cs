using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

/// <summary>
/// Droits des comptes (page « Droits »), réservés aux administrateurs principaux de la configuration.
/// Un changement s'applique à la page suivante que la personne ouvre (ou après actualisation).
/// </summary>
public sealed record GroupeAnnuaire(string Nom, int Membres);

public sealed class ServiceDroits(
    IDbContextFactory<ParcDbContext> dbFactory, IMemoryCache cache, IConfiguration config, IAnnuaireMicrosoft annuaire, ILogger<ServiceDroits> log)
{
    private const string CleCache = "droits-acces";
    private const string ActionInitialisation = "Droits initialisés";
    private const string ActionInitialisationNotifications = "Notifications initialisées";
    private const string ActionInitialisationGroupe = "Groupe initialisé";

    public static IReadOnlyList<string> AdministrateursDe(IConfiguration config) =>
        (config.GetSection("Authentification:Administrateurs").Get<string[]>() ?? []).Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).ToList();

    public IReadOnlyList<string> Administrateurs => AdministrateursDe(config);

    /// <summary>
    /// Droits effectifs du compte (toutes ses adresses) : sa propre ligne et celles des groupes dont il est membre,
    /// lus dans un cache vidé à chaque modification et après chaque synchronisation de l'annuaire.
    /// </summary>
    public static async Task<DroitAcces?> DroitDeAsync(IDbContextFactory<ParcDbContext> dbFactory, IMemoryCache? cache, IEnumerable<string> adresses)
    {
        if (cache is null || !cache.TryGetValue(CleCache, out Dictionary<string, List<DroitAcces>>? parAdresse) || parAdresse is null)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var lignes = await db.DroitsAcces.AsNoTracking().ToListAsync();
            parAdresse = new Dictionary<string, List<DroitAcces>>(StringComparer.OrdinalIgnoreCase);
            void Noter(string adresse, DroitAcces d)
            {
                if (!parAdresse.TryGetValue(adresse, out var l)) parAdresse[adresse] = l = [];
                l.Add(d);
            }
            foreach (var d in lignes.Where(d => !d.EstGroupe)) Noter(d.Email, d);
            foreach (var (groupe, membres) in await MembresAsync(db, lignes))
                foreach (var a in membres.SelectMany(c => c.Adresses)) Noter(a, groupe);
            cache?.Set(CleCache, parAdresse, TimeSpan.FromMinutes(5));
        }
        var trouves = adresses.SelectMany(a => parAdresse.GetValueOrDefault(a) ?? []).Distinct().ToList();
        if (trouves.Count == 0) return null;
        var propre = trouves.FirstOrDefault(d => !d.EstGroupe);
        var effectif = new DroitAcces { Id = propre?.Id ?? 0, Email = propre?.Email ?? adresses.First(), Nom = propre?.Nom };
        foreach (var d in trouves) effectif.Cumuler(d);
        return effectif;
    }

    public static void ViderCache(IMemoryCache? cache) => cache?.Remove(CleCache);

    /// <summary>Membres directs (comptes actifs) de chaque ligne « groupe », d'après la dernière synchronisation de l'annuaire.</summary>
    public static async Task<Dictionary<DroitAcces, List<CompteMicrosoft>>> MembresAsync(ParcDbContext db, IEnumerable<DroitAcces> lignes, CancellationToken ct = default)
    {
        var groupes = lignes.Where(d => d.EstGroupe).ToList();
        if (groupes.Count == 0) return [];
        var comptes = await db.ComptesMicrosoft.AsNoTracking().Where(c => !c.Supprime && c.Actif && c.Groupes != null).ToListAsync(ct);
        return groupes.ToDictionary(g => g, g => comptes
            .Where(c => c.ListeGroupes.Contains(g.Groupe!, StringComparer.OrdinalIgnoreCase))
            .OrderBy(c => c.NomAffichage, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    /// <summary>Adresses des comptes retenus par le filtre, groupes développés en leurs membres (une adresse par personne).</summary>
    private static async Task<List<string>> AdressesAsync(ParcDbContext db, Func<DroitAcces, bool> filtre, CancellationToken ct)
    {
        var lignes = (await db.DroitsAcces.AsNoTracking().ToListAsync(ct)).Where(filtre).ToList();
        var adresses = lignes.Where(d => !d.EstGroupe).Select(d => d.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in (await MembresAsync(db, lignes, ct)).Values.SelectMany(m => m))
            if (c.AdressePrincipale is { } a && !c.Adresses.Any(adresses.Contains)) adresses.Add(a);
        return adresses.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adresses des valideurs (destinataires des boutons Valider / Refuser et des relances).</summary>
    public static Task<List<string>> ValideursAsync(ParcDbContext db, CancellationToken ct = default) => AdressesAsync(db, d => d.Valideur, ct);

    /// <summary>Adresses abonnées à une notification par mail.</summary>
    public static Task<List<string>> AbonnesAsync(ParcDbContext db, Notification n, CancellationToken ct = default) => AdressesAsync(db, d => d.A(n), ct);

    public async Task<List<DroitAcces>> ListerAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DroitsAcces.AsNoTracking().OrderBy(d => d.Groupe == null).ThenBy(d => d.Nom ?? d.Email).ToListAsync(ct);
    }

    /// <summary>Membres de chaque groupe de la liste, par identifiant de ligne.</summary>
    public async Task<Dictionary<int, List<CompteMicrosoft>>> MembresDesGroupesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await MembresAsync(db, await db.DroitsAcces.AsNoTracking().Where(d => d.Groupe != null).ToListAsync(ct), ct))
            .ToDictionary(x => x.Key.Id, x => x.Value);
    }

    /// <summary>Groupes de l'annuaire Microsoft (dernière synchronisation) contenant le texte, avec leur nombre de membres actifs.</summary>
    public async Task<List<GroupeAnnuaire>> ChercherGroupesAsync(string? texte, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var groupes = (await db.ComptesMicrosoft.AsNoTracking().Where(c => !c.Supprime && c.Actif && c.Groupes != null).Select(c => c.Groupes).ToListAsync(ct))
            .SelectMany(g => g!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase).Select(g => new GroupeAnnuaire(g.Key, g.Count()));
        var t = texte?.Trim();
        return groupes.Where(g => string.IsNullOrEmpty(t) || g.Nom.Contains(t, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(g => g.Nom, StringComparer.CurrentCultureIgnoreCase).Take(30).ToList();
    }

    public async Task<DroitAcces> AjouterGroupeAsync(string? groupe, string? nom, Utilisateur u, CancellationToken ct = default)
    {
        Verifier(u);
        var g = groupe?.Trim();
        if (string.IsNullOrEmpty(g)) throw new ErreurMetier("Choisissez un groupe de l'annuaire Microsoft.");
        var connu = (await ChercherGroupesAsync(g, ct)).FirstOrDefault(x => string.Equals(x.Nom, g, StringComparison.OrdinalIgnoreCase))
            ?? throw new ErreurMetier($"Groupe « {g} » inconnu de l'annuaire (ou sans membre actif) : synchronisez l'annuaire Microsoft s'il vient d'être créé.");
        var cle = DroitAcces.CleGroupe(connu.Nom);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.DroitsAcces.AnyAsync(d => d.Email == cle, ct)) throw new ErreurMetier($"Le groupe {connu.Nom} est déjà dans la liste.");
        var d = new DroitAcces
        {
            Email = cle, Groupe = connu.Nom, Nom = string.IsNullOrWhiteSpace(nom) ? connu.Nom : nom.Trim(), ModifiePar = u.Signature, ModifieLe = DateTime.UtcNow,
        };
        db.DroitsAcces.Add(d);
        db.Journal.Add(new Journal { Date = d.ModifieLe, Utilisateur = u.Signature, Action = "Droits", Details = $"Groupe {connu.Nom} ajouté à la liste des droits ({connu.Membres} membre(s))" });
        await db.SaveChangesAsync(ct);
        cache.Remove(CleCache);
        return d;
    }

    public async Task RenommerAsync(int id, string? nom, Utilisateur u, CancellationToken ct = default)
    {
        Verifier(u);
        if (string.IsNullOrWhiteSpace(nom)) throw new ErreurMetier("Indiquez un nom.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var d = await db.DroitsAcces.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ErreurMetier("Ligne introuvable.");
        var ancien = d.Nom ?? d.Email;
        d.Nom = nom.Trim();
        d.ModifiePar = u.Signature;
        d.ModifieLe = DateTime.UtcNow;
        db.Journal.Add(new Journal { Date = d.ModifieLe, Utilisateur = u.Signature, Action = "Droits", Details = $"« {ancien} » renommé « {d.Nom} »" });
        await db.SaveChangesAsync(ct);
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

    /// <summary>
    /// Une seule fois par groupe : crée les lignes « groupe » de la configuration (« Droits:Groupes »), avec leurs droits et notifications
    /// de départ. Elles se modifient ensuite sur la page « Droits » (et une ligne retirée n'est pas recréée).
    /// </summary>
    public async Task<int> InitialiserGroupesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var crees = 0;
        foreach (var section in config.GetSection("Droits:Groupes").GetChildren())
        {
            var groupe = section["Groupe"]?.Trim();
            if (string.IsNullOrEmpty(groupe)) continue;
            var cle = DroitAcces.CleGroupe(groupe);
            var trace = $"(groupe {groupe})";
            if (await db.DroitsAcces.AnyAsync(d => d.Email == cle, ct)
                || await db.Journal.AnyAsync(j => j.Action == ActionInitialisationGroupe && j.Details != null && j.Details.Contains(trace), ct)) continue;
            var d = new DroitAcces { Email = cle, Groupe = groupe, Nom = section["Nom"]?.Trim() ?? groupe, ModifiePar = ActionInitialisationGroupe, ModifieLe = DateTime.UtcNow };
            foreach (var x in section.GetSection("Droits").Get<string[]>() ?? []) if (Enum.TryParse<Droit>(x, out var droit)) d.Definir(droit, true);
            foreach (var x in section.GetSection("Notifications").Get<string[]>() ?? []) if (Enum.TryParse<Notification>(x, out var n)) d.Definir(n, true);
            db.DroitsAcces.Add(d);
            db.Journal.Add(new Journal
            {
                Date = DateTime.UtcNow, Utilisateur = "Application", Action = ActionInitialisationGroupe,
                Details = $"{d.Nom} {trace} : {string.Join(", ", d.Droits.Select(x => x.Libelle()).Concat(d.Notifications.Select(n => $"notification {n.Libelle()}")).DefaultIfEmpty("aucun droit"))}",
            });
            crees++;
        }
        await db.SaveChangesAsync(ct);
        if (crees > 0) cache.Remove(CleCache);
        return crees;
    }

    private static void Verifier(Utilisateur u)
    {
        if (!u.EstAdministrateur) throw new ErreurMetier("Réservé aux administrateurs principaux.");
    }
}
