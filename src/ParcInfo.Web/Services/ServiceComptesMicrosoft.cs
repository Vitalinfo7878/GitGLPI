using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Import;

namespace ParcInfo.Web.Services;

public sealed record RapportSynchro(int Comptes, int ReliesAutomatiquement, int ATrancher, int Departs, DateTime Le);

/// <summary>Compte proposé pour une personne ou un site, avec la raison de la proposition.</summary>
public sealed record Candidat(CompteMicrosoft Compte, string Raison);

/// <summary>Personne ou site sans compte Microsoft relié, avec les comptes possibles.</summary>
public sealed record ATrancher(TypeDetenteur Type, int Id, string Nom, string? Email, int NbMateriels, List<Candidat> Candidats);

/// <summary>
/// Liaison des personnes et des sites avec l'annuaire Microsoft : synchronisation, rapprochement, départs.
/// Seules les correspondances certaines sont reliées automatiquement (même adresse mail, ou nom identique et unique) ;
/// les autres attendent une décision sur la page « Comptes Microsoft ».
/// Une fois reliés, le nom et l'adresse affichés dans l'application sont ceux de l'annuaire.
/// </summary>
public sealed class ServiceComptesMicrosoft(IDbContextFactory<ParcDbContext> dbFactory, IAnnuaireMicrosoft annuaire, ILogger<ServiceComptesMicrosoft> log)
{
    private static readonly SemaphoreSlim EnCours = new(1, 1);

    public bool Disponible => annuaire.Disponible;

    public async Task<RapportSynchro> SynchroniserAsync(string auteur, CancellationToken ct = default)
    {
        if (!await EnCours.WaitAsync(0, ct)) throw new ErreurMetier("Une synchronisation est déjà en cours.");
        try
        {
            var lus = await annuaire.LireComptesAsync(ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var maintenant = DateTime.UtcNow;

            // 1. Copie locale de l'annuaire (les comptes disparus sont marqués supprimés, jamais effacés)
            // Changements notés dans le journal d'inventaire
            var changements = new List<string>();
            var existants = await db.ComptesMicrosoft.ToDictionaryAsync(c => c.Id, ct);
            var nouveaux = 0;
            foreach (var a in lus)
            {
                var nouveau = !existants.TryGetValue(a.Id, out var c);
                if (nouveau) { db.ComptesMicrosoft.Add(existants[a.Id] = c = new CompteMicrosoft { Id = a.Id }); nouveaux++; }
                var (nom, adresse, actif, supprime) = (c!.NomAffichage, c.AdressePrincipale, c.Actif, c.Supprime);
                Copier(a, c, maintenant);
                if (nouveau) continue;
                if (nom != c.NomAffichage) changements.Add($"Nom : {nom} → {c.NomAffichage}");
                if (adresse != c.AdressePrincipale) changements.Add($"{c.NomAffichage} : adresse {adresse ?? "—"} → {c.AdressePrincipale ?? "—"}");
                if (actif && !c.Actif) changements.Add($"{c.NomAffichage} : compte désactivé");
                if (!actif && c.Actif) changements.Add($"{c.NomAffichage} : compte réactivé");
                if (supprime) changements.Add($"{c.NomAffichage} : compte de retour dans l'annuaire");
            }
            var ids = lus.Select(a => a.Id).ToHashSet();
            foreach (var c in existants.Values.Where(c => !ids.Contains(c.Id) && !c.Supprime))
            {
                c.Supprime = true;
                changements.Add($"{c.NomAffichage} : compte supprimé de l'annuaire");
            }
            if (nouveaux > 0) changements.Insert(0, $"{nouveaux} nouveau(x) compte(s)");
            await db.SaveChangesAsync(ct);

            // 2. Rapprochements certains
            var comptes = existants.Values.Where(c => !c.Supprime).ToList();
            var relies = await RelierAutomatiquementAsync(db, comptes, changements, ct);

            // 3. Noms et adresses des personnes et sites reliés = ceux de l'annuaire
            await ActualiserReliesAsync(db, changements, ct);
            if (changements.Count > 0)
                db.Journal.Add(new Journal
                {
                    Date = maintenant, Utilisateur = auteur, Action = JournalInventaire.ComptesMisAJour,
                    Details = JournalInventaire.Tronquer(string.Join(" ; ", changements)),
                });
            await db.SaveChangesAsync(ct);

            var rapport = new RapportSynchro(lus.Count, relies, (await ListerATrancherAsync(ct)).Count, (await ListerDepartsAsync(ct)).Count, maintenant);
            db.Journal.Add(new Journal
            {
                Date = maintenant, Utilisateur = auteur, Action = "Synchronisation annuaire Microsoft",
                Details = $"{rapport.Comptes} comptes, {rapport.ReliesAutomatiquement} rapprochement(s) automatique(s), {rapport.ATrancher} à trancher, {rapport.Departs} départ(s) à traiter",
            });
            await db.SaveChangesAsync(ct);
            log.LogInformation("Annuaire synchronisé : {Comptes} comptes, {Relies} reliés", rapport.Comptes, relies);
            return rapport;
        }
        finally { EnCours.Release(); }
    }

    private static void Copier(CompteAnnuaire a, CompteMicrosoft c, DateTime maintenant)
    {
        c.NomAffichage = a.NomAffichage; c.Prenom = a.Prenom; c.Nom = a.Nom; c.Upn = a.Upn?.ToLowerInvariant(); c.Email = a.Email?.ToLowerInvariant();
        c.AutresAdresses = Tronquer(string.Join(' ', a.AutresAdresses), 4000);
        c.Poste = a.Poste; c.Service = a.Service; c.Bureau = a.Bureau; c.Societe = a.Societe; c.Matricule = a.Matricule;
        c.TelephoneMobile = a.TelephoneMobile; c.TelephonePro = a.TelephonePro; c.Adresse = a.Adresse; c.Ville = a.Ville;
        c.ResponsableId = a.ResponsableId; c.ResponsableNom = a.ResponsableNom;
        c.Groupes = Tronquer(string.Join(" ; ", a.Groupes), 4000);
        c.Licences = Tronquer(string.Join(" ; ", a.Licences), 1000);
        c.Actif = a.Actif; c.CreeDansEntraLe = a.CreeLe; c.Supprime = false; c.SynchroniseLe = maintenant;
    }

    private static async Task<int> RelierAutomatiquementAsync(ParcDbContext db, List<CompteMicrosoft> comptes, List<string> changements, CancellationToken ct)
    {
        var prisParPersonne = (await db.Personnes.Where(p => p.EntraObjectId != null).Select(p => p.EntraObjectId!).ToListAsync(ct)).ToHashSet();
        var dejaPris = prisParPersonne.Concat(await db.Sites.Where(s => s.EntraObjectId != null).Select(s => s.EntraObjectId!).ToListAsync(ct)).ToHashSet();
        var parAdresse = comptes.SelectMany(c => c.Adresses.Select(a => (a, c))).GroupBy(x => x.a).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().c);
        var parNom = comptes.GroupBy(c => CleNom(c.NomAffichage)).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.ToList());
        var relies = 0;

        bool Libre(CompteMicrosoft c) => !dejaPris.Contains(c.Id);

        var personnes = await db.Personnes.Where(p => p.EntraObjectId == null && !p.SansCompteMicrosoft).ToListAsync(ct);
        // Un nom identique n'est certain que s'il est unique des deux côtés (deux « Dominique MARTIN » = doute)
        var nomsPersonnes = personnes.GroupBy(p => CleNom(p.NomComplet)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var p in personnes)
        {
            CompteMicrosoft? c = null;
            if (p.Email is { } e && parAdresse.TryGetValue(e.Trim().ToLowerInvariant(), out var parMail)) c = parMail;
            else if (CleNom(p.NomComplet) is { Length: > 0 } cle && nomsPersonnes[cle] == 1 && parNom.TryGetValue(cle, out var memeNom) && memeNom.Count == 1)
                c = memeNom[0];
            if (c is null || !Libre(c)) continue;
            p.EntraObjectId = c.Id;
            dejaPris.Add(c.Id);
            relies++;
            changements.Add($"{p.NomComplet} relié automatiquement à {c.NomAffichage}");
        }

        // Sites : seulement par adresse mail (le nom d'un site ne suffit pas à être certain).
        // Les sous-sites (440, 440-1…) partagent la boîte du site : un même compte peut être relié à plusieurs sites.
        foreach (var s in await db.Sites.Where(s => s.EntraObjectId == null && !s.SansCompteMicrosoft && s.Email != null).ToListAsync(ct))
        {
            if (!parAdresse.TryGetValue(s.Email!.Trim().ToLowerInvariant(), out var c) || prisParPersonne.Contains(c.Id)) continue;
            s.EntraObjectId = c.Id;
            dejaPris.Add(c.Id);
            relies++;
            changements.Add($"Site {s.Code} relié automatiquement à {c.NomAffichage}");
        }
        await db.SaveChangesAsync(ct);
        return relies;
    }

    private static async Task ActualiserReliesAsync(ParcDbContext db, List<string> changements, CancellationToken ct)
    {
        foreach (var p in await db.Personnes.Include(p => p.CompteMicrosoft).Where(p => p.CompteMicrosoft != null).ToListAsync(ct))
        {
            var (nom, email) = (p.NomComplet, p.Email);
            p.NomComplet = p.CompteMicrosoft!.NomAffichage;
            p.Email = p.CompteMicrosoft.AdressePrincipale ?? p.Email;
            if (nom != p.NomComplet) changements.Add($"Détenteur renommé : {nom} → {p.NomComplet}");
            if (!string.Equals(email, p.Email, StringComparison.OrdinalIgnoreCase)) changements.Add($"{p.NomComplet} : mail {email ?? "—"} → {p.Email ?? "—"}");
        }
        foreach (var s in await db.Sites.Include(s => s.CompteMicrosoft).Where(s => s.CompteMicrosoft != null).ToListAsync(ct))
        {
            var (nom, email) = (s.Libelle, s.Email);
            s.Nom = s.CompteMicrosoft!.NomAffichage;
            s.Email = s.CompteMicrosoft.AdressePrincipale ?? s.Email;
            if (nom != s.Libelle) changements.Add($"Site {s.Code} renommé : {nom} → {s.Libelle}");
            if (!string.Equals(email, s.Email, StringComparison.OrdinalIgnoreCase)) changements.Add($"Site {s.Code} : mail {email ?? "—"} → {s.Email ?? "—"}");
        }
    }

    // --- Décisions ---

    /// <summary>
    /// Relie une personne à un compte. Si une autre personne est déjà reliée à ce compte (doublon de l'Excel),
    /// son matériel et son historique sont rattachés à celle-ci, puis le doublon est supprimé.
    /// </summary>
    public async Task<string> RelierPersonneAsync(int personneId, string compteId, Utilisateur u, CancellationToken ct = default)
    {
        Verifier(u);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var compte = await db.ComptesMicrosoft.FirstOrDefaultAsync(c => c.Id == compteId, ct) ?? throw new ErreurMetier("Compte introuvable.");
        if (await db.Sites.AnyAsync(s => s.EntraObjectId == compteId, ct)) throw new ErreurMetier($"{compte.NomAffichage} est déjà relié à un site.");
        var p = await db.Personnes.FirstOrDefaultAsync(x => x.Id == personneId, ct) ?? throw new ErreurMetier("Personne introuvable.");
        var ancienNom = p.NomComplet;
        var message = $"{ancienNom} relié à {compte.NomAffichage}.";

        var cible = await db.Personnes.FirstOrDefaultAsync(x => x.EntraObjectId == compteId && x.Id != personneId, ct);
        if (cible is not null)
        {
            await db.Materiels.Where(m => m.PersonneId == p.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.PersonneId, cible.Id), ct);
            await db.Mouvements.Where(m => m.DestinationPersonneId == p.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.DestinationPersonneId, cible.Id), ct);
            cible.RegionId ??= p.RegionId;
            db.Personnes.Remove(p);
            p = cible;
            message = $"{ancienNom} fusionné avec {compte.NomAffichage} (déjà relié) : matériel et historique regroupés.";
        }
        p.EntraObjectId = compte.Id;
        p.SansCompteMicrosoft = false;
        p.NomComplet = compte.NomAffichage;
        p.Email = compte.AdressePrincipale ?? p.Email;
        db.Journal.Add(new Journal { Date = DateTime.UtcNow, Utilisateur = u.Signature, Action = "Compte Microsoft relié", Details = message });
        await db.SaveChangesAsync(ct);
        return message;
    }

    public async Task<string> RelierSiteAsync(int siteId, string compteId, Utilisateur u, CancellationToken ct = default)
    {
        Verifier(u);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var compte = await db.ComptesMicrosoft.FirstOrDefaultAsync(c => c.Id == compteId, ct) ?? throw new ErreurMetier("Compte introuvable.");
        // Plusieurs sites peuvent partager un compte (sous-sites 440, 440-1…), mais pas une personne et un site
        if (await db.Personnes.AnyAsync(p => p.EntraObjectId == compteId, ct))
            throw new ErreurMetier($"{compte.NomAffichage} est déjà relié à une personne.");
        var s = await db.Sites.FirstOrDefaultAsync(x => x.Id == siteId, ct) ?? throw new ErreurMetier("Site introuvable.");
        var autres = await db.Sites.Where(x => x.EntraObjectId == compteId && x.Id != siteId).Select(x => x.Code).OrderBy(c => c).ToListAsync(ct);
        var message = $"Site {s.Code} relié à {compte.NomAffichage}" + (autres.Count > 0 ? $" (compte partagé avec le site {string.Join(", ", autres)})." : ".");
        s.EntraObjectId = compte.Id;
        s.SansCompteMicrosoft = false;
        s.Nom = compte.NomAffichage;
        s.Email = compte.AdressePrincipale ?? s.Email;
        db.Journal.Add(new Journal { Date = DateTime.UtcNow, Utilisateur = u.Signature, Action = "Compte Microsoft relié", Details = message });
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>« Aucun compte » (sansCompte = vrai), « Revoir » ou « Délier » (sansCompte = faux) : le détenteur repasse à trancher.</summary>
    public async Task DefinirSansCompteAsync(TypeDetenteur type, int id, bool sansCompte, Utilisateur u, CancellationToken ct = default)
    {
        Verifier(u);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        string nom;
        if (type == TypeDetenteur.Personne)
        {
            var p = await db.Personnes.FirstAsync(x => x.Id == id, ct);
            p.EntraObjectId = null;
            p.SansCompteMicrosoft = sansCompte;
            nom = p.NomComplet;
        }
        else
        {
            var s = await db.Sites.FirstAsync(x => x.Id == id, ct);
            s.EntraObjectId = null;
            s.SansCompteMicrosoft = sansCompte;
            nom = s.Libelle;
        }
        db.Journal.Add(new Journal
        {
            Date = DateTime.UtcNow, Utilisateur = u.Signature, Action = sansCompte ? "Sans compte Microsoft" : "Compte Microsoft à revoir", Details = nom,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Personne correspondant à un compte de l'annuaire (créée si besoin), pour une attribution.</summary>
    public async Task<Personne> PersonnePourCompteAsync(string compteId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existante = await db.Personnes.FirstOrDefaultAsync(p => p.EntraObjectId == compteId, ct);
        if (existante is not null) return existante;
        var c = await db.ComptesMicrosoft.FirstAsync(x => x.Id == compteId, ct);
        if (await db.Sites.AnyAsync(s => s.EntraObjectId == compteId, ct)) throw new ErreurMetier($"{c.NomAffichage} est le compte d'un site.");
        var p = new Personne { NomComplet = c.NomAffichage, Email = c.AdressePrincipale, EntraObjectId = c.Id };
        db.Personnes.Add(p);
        await db.SaveChangesAsync(ct);
        return p;
    }

    // --- Listes ---

    public async Task<List<ATrancher>> ListerATrancherAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var comptes = await db.ComptesMicrosoft.AsNoTracking().Where(c => !c.Supprime).ToListAsync(ct);
        var prisParPersonne = (await db.Personnes.Where(p => p.EntraObjectId != null).Select(p => p.EntraObjectId!).ToListAsync(ct)).ToHashSet();
        var sitesParCompte = (await db.Sites.Where(s => s.EntraObjectId != null).Select(s => new { s.EntraObjectId, s.Code }).ToListAsync(ct))
            .GroupBy(s => s.EntraObjectId!).ToDictionary(g => g.Key, g => g.Select(s => s.Code).Order().ToList());
        var pris = prisParPersonne.Concat(sitesParCompte.Keys).ToHashSet();
        var personnes = await db.Personnes.AsNoTracking().Where(p => p.EntraObjectId == null && !p.SansCompteMicrosoft)
            .Select(p => new { p.Id, p.NomComplet, p.Email, N = p.Materiels.Count }).ToListAsync(ct);
        var sites = await db.Sites.AsNoTracking().Where(s => s.EntraObjectId == null && !s.SansCompteMicrosoft)
            .Select(s => new { s.Id, s.Code, s.Nom, s.Email, N = s.Materiels.Count }).ToListAsync(ct);

        return personnes.Select(p => new ATrancher(TypeDetenteur.Personne, p.Id, p.NomComplet, p.Email, p.N, CandidatsPersonne(p.NomComplet, p.Email, comptes, pris)))
            .Concat(sites.Select(s => new ATrancher(TypeDetenteur.Site, s.Id, Site.LibelleDe(s.Code, s.Nom, false), s.Email, s.N, CandidatsSite(s.Code, s.Nom, s.Email, comptes, prisParPersonne, sitesParCompte))))
            .OrderByDescending(x => x.NbMateriels).ThenBy(x => x.Nom).ToList();
    }

    /// <summary>Personnes reliées à un compte désactivé ou supprimé qui détiennent encore du matériel.</summary>
    public async Task<List<(Personne Personne, List<Materiel> Materiels)>> ListerDepartsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var personnes = await db.Personnes.AsNoTracking().Include(p => p.CompteMicrosoft)
            .Include(p => p.Materiels.Where(m => m.Statut == StatutMateriel.Attribue))
            .Where(p => p.CompteMicrosoft != null && (p.CompteMicrosoft.Supprime || !p.CompteMicrosoft.Actif)
                && p.Materiels.Any(m => m.Statut == StatutMateriel.Attribue))
            .OrderBy(p => p.NomComplet).ToListAsync(ct);
        return personnes.Select(p => (p, p.Materiels.OrderBy(m => m.NumInterne).ToList())).ToList();
    }

    public async Task<List<CompteMicrosoft>> ChercherComptesAsync(string? texte, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.ComptesMicrosoft.AsNoTracking().Where(c => !c.Supprime);
        if (!string.IsNullOrWhiteSpace(texte))
        {
            var t = Recherche.Texte(texte);
            q = q.Where(c => c.NomAffichage.ToLower().Contains(t) || (c.Email != null && c.Email.Contains(t)) || (c.Upn != null && c.Upn.Contains(t)));
        }
        return await q.OrderBy(c => c.NomAffichage).Take(30).ToListAsync(ct);
    }

    // --- Rapprochement ---

    /// <summary>Clé de comparaison des noms : sans accents ni majuscules, mots triés (« DUPONT Camille » = « Camille Dupont »).</summary>
    public static string CleNom(string? nom) => string.Join(' ', Mots(nom).Order(StringComparer.Ordinal));

    private static IEnumerable<string> Mots(string? s) =>
        new string(Normalisation.SansAccents(s ?? "").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static List<Candidat> CandidatsPersonne(string nom, string? email, IEnumerable<CompteMicrosoft> comptes, IReadOnlySet<string> pris)
    {
        var tous = Mots(nom).ToList();
        var mots = tous.Where(m => m.Length >= 3).ToHashSet();
        var initiales = tous.Where(m => m.Length == 1).ToHashSet();
        var cle = CleNom(nom);
        return comptes
            .Select(c =>
            {
                if (email is not null && c.Adresses.Contains(email.Trim().ToLowerInvariant())) return (c, Score: 100, Raison: "même adresse mail");
                if (cle.Length > 0 && CleNom(c.NomAffichage) == cle) return (c, Score: 90, Raison: "même nom");
                var motsCompte = Mots(c.NomAffichage).ToList();
                // Mot identique : 10 ; début commun d'au moins 4 lettres (MARTIN / Martinez) : 5 ; initiale (« D. ») : 2
                var score = motsCompte.Sum(m => mots.Contains(m) ? 10
                    : mots.Any(x => x.Length >= 4 && m.Length >= 4 && (m.StartsWith(x) || x.StartsWith(m))) ? 5 : 0);
                if (score > 0) score += motsCompte.Count(m => initiales.Contains(m[..1])) * 2;
                return (c, Score: score, Raison: score >= 20 ? "nom proche" : "nom de famille ou prénom proche");
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => pris.Contains(x.c.Id)).ThenBy(x => x.c.NomAffichage)
            .Take(6).Select(x => new Candidat(x.c, pris.Contains(x.c.Id) ? $"{x.Raison} – déjà relié (doublon ?)" : x.Raison)).ToList();
    }

    /// <param name="prisParPersonne">Comptes reliés à une personne : exclus.</param>
    /// <param name="sitesParCompte">Codes des sites déjà reliés à chaque compte : proposés quand même (sous-sites qui partagent la boîte du site).</param>
    public static List<Candidat> CandidatsSite(string code, string? nom, string? email, IEnumerable<CompteMicrosoft> comptes, IReadOnlySet<string> prisParPersonne,
        IReadOnlyDictionary<string, List<string>>? sitesParCompte = null)
    {
        var codes = new[] { code.ToLowerInvariant(), code.TrimStart('0').ToLowerInvariant() }.Where(c => c.Length > 0).ToHashSet();
        var motsNom = Mots(nom).Where(m => m.Length >= 4).ToHashSet();
        var principal = code.IndexOf('-') is > 0 and var i ? code[..i].TrimStart('0').ToLowerInvariant() : null;
        return comptes.Where(c => !prisParPersonne.Contains(c.Id))
            .Select(c =>
            {
                if (email is not null && c.Adresses.Contains(email.Trim().ToLowerInvariant())) return (c, Score: 100, Raison: "même adresse mail");
                var motsCompte = Mots($"{c.NomAffichage} {c.Email} {c.Upn}").SelectMany(m => new[] { m, m.TrimStart('0') }).ToHashSet();
                if (codes.Any(motsCompte.Contains)) return (c, Score: 50, Raison: $"code {code} dans le nom ou l'adresse");
                // Sous-site « 440-1 » : compte du site principal « 440 »
                if (principal is not null && motsCompte.Contains(principal)) return (c, Score: 45, Raison: $"site principal {code[..code.IndexOf('-')]}");
                // Code collé à des lettres dans l'adresse : « site087@… »
                if (codes.Any(cd => cd.Any(char.IsDigit) && motsCompte.Any(m => m.EndsWith(cd) && m.Length > cd.Length && char.IsLetter(m[^(cd.Length + 1)]))))
                    return (c, Score: 40, Raison: $"code {code} dans l'adresse");
                var communs = motsCompte.Count(motsNom.Contains);
                return (c, Score: communs * 10, Raison: "nom proche");
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.c.NomAffichage)
            .Take(6).Select(x => new Candidat(x.c, sitesParCompte?.GetValueOrDefault(x.c.Id) is { Count: > 0 } codes
                ? $"{x.Raison} – déjà relié au site {string.Join(", ", codes)} (sous-site ?)" : x.Raison)).ToList();
    }

    private static void Verifier(Utilisateur u)
    {
        if (!u.EstGestionnaire) throw new ErreurMetier("Réservé au service informatique.");
    }

    private static string? Tronquer(string s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length > max ? s[..max] : s;
}

/// <summary>Synchronisation automatique de l'annuaire chaque nuit (vers 2 h 30, heure de Paris).</summary>
public sealed class SynchronisationAnnuaireNocturne(IServiceScopeFactory scopes, ILogger<SynchronisationAnnuaireNocturne> log) : BackgroundService
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var maintenant = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Paris);
            var prochaine = new DateTimeOffset(maintenant.Date.AddHours(2.5), maintenant.Offset);
            if (prochaine <= maintenant) prochaine = prochaine.AddDays(1);
            try { await Task.Delay(prochaine - maintenant, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ServiceComptesMicrosoft>().SynchroniserAsync("Synchronisation automatique", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Synchronisation nocturne de l'annuaire impossible");
            }
        }
    }
}
