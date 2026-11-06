using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph.Models.ODataErrors;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Import;
using System.Text.RegularExpressions;

namespace ParcInfo.Web.Services;

/// <summary>Ligne utile du fichier « Organisation » : un site, son état, sa région et son responsable de secteur.</summary>
public sealed record LigneOrganisation(string Code, string? NomCommercial, bool Ouvert, string? Region, string? EmailResponsable);

/// <summary>Groupe de sites d'une région dans l'annuaire, avec ses membres (identifiants Entra) au moment de l'analyse.</summary>
/// <param name="Exchange">Liste de diffusion (ou groupe à extension messagerie) : membres modifiés via Exchange Online.</param>
public sealed record GroupeRegion(string Id, string Nom, bool Modifiable, string? Raison, IReadOnlySet<string> Membres,
    string? Alias = null, string? Adresse = null, bool Exchange = false);

/// <summary>Région du fichier Organisation → groupe de sites de l'annuaire ; groupes à vider des sites.</summary>
public sealed class CorrespondanceGroupes
{
    public Dictionary<string, string> Regions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Anciens groupes de sites : les comptes de sites qui y sont en sont retirés.</summary>
    public List<string> AVider { get; init; } = [];

    public static CorrespondanceGroupes ParDefaut() => new()
    {
        Regions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CENTRE EST"] = "REGION_CE_SITES",
            ["CENTRE OUEST"] = "REGION_CENTRE_OUEST_SITES",
            ["IDF"] = "REGION_IDF_SITES",
            ["IDF AL"] = "REGION_IDF_SITES",
            ["NORD EST"] = "REGION_NE_SITES",
            ["SUD EST"] = "REGION_SUD_SITES",
            ["SUD OUEST"] = "REGION_SUD_OUEST_SITES",
        },
        AVider = ["REGION_OUEST_SITES"],
    };

    public string? GroupeDe(string? region) => region is null ? null : Regions.GetValueOrDefault(region.Trim());

    /// <summary>Seuls ces groupes sont touchés ; les autres (REGION_SITES_AGENCES_…) jamais.</summary>
    public bool Concerne(GroupeRegion g) => Regions.Values.Concat(AVider).Any(n => Est(g, n));

    public static bool Est(GroupeRegion g, string nom) =>
        string.Equals(g.Nom, nom, StringComparison.OrdinalIgnoreCase) || string.Equals(g.Alias, nom, StringComparison.OrdinalIgnoreCase);
}

public enum TypeChangementOrganisation
{
    /// <summary>Responsable (manager) du compte Microsoft du site.</summary>
    Responsable,
    AjoutGroupe,
    RetraitGroupe,
    /// <summary>Champ « Responsable » de la fiche du site dans l'inventaire.</summary>
    ResponsableInventaire,
}

public sealed record ChangementOrganisation(
    TypeChangementOrganisation Type, string Sites, string? CompteId, string? CompteNom, string? Avant, string? Apres,
    string? GroupeId = null, string? ResponsableId = null, int? SiteId = null, string? Blocage = null,
    string? GroupeAdresse = null, bool ParExchange = false, string? CompteAdresse = null);

public sealed record PlanOrganisation(
    int Lignes, int Ouverts, int Fermes, int ComptesConcernes, List<ChangementOrganisation> Changements,
    List<(string Region, string? Groupe)> Correspondances, List<string> Problemes, List<string> RapprochementsParNom, List<string>? GroupesLus = null);

public sealed record ResultatChangement(ChangementOrganisation Changement, string? Erreur);

/// <summary>
/// Écriture dans l'annuaire Microsoft : responsable via Graph (User.ReadWrite.All) ; membres des groupes via Graph
/// (GroupMember.ReadWrite.All) ou, pour les listes de diffusion, via Exchange Online (Exchange.ManageAsApp + rôle Exchange).
/// </summary>
public interface IEcritureAnnuaire
{
    Task DefinirResponsableAsync(string compteId, string responsableId, CancellationToken ct = default);
    Task AjouterMembreAsync(ChangementOrganisation c, CancellationToken ct = default);
    Task RetirerMembreAsync(ChangementOrganisation c, CancellationToken ct = default);
    /// <summary>Lecture sans modification d'une liste de diffusion via Exchange Online : vérifie les droits de l'application.</summary>
    Task<string> TesterExchangeAsync(string groupe, CancellationToken ct = default);
}

public sealed class EcritureAnnuaireGraph(IConfiguration config, OptionsMessagerie messagerie) : IEcritureAnnuaire
{
    private readonly Microsoft.Graph.GraphServiceClient _graph = ClientGraph.Creer(config);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private string? _domaineInitial;

    public Task DefinirResponsableAsync(string compteId, string responsableId, CancellationToken ct = default) =>
        _graph.Users[compteId].Manager.Ref.PutAsync(
            new Microsoft.Graph.Models.ReferenceUpdate { OdataId = $"https://graph.microsoft.com/v1.0/users/{responsableId}" }, cancellationToken: ct);

    public Task AjouterMembreAsync(ChangementOrganisation c, CancellationToken ct = default) => c.ParExchange
        ? ExchangeAsync("Add-DistributionGroupMember", new() { ["Identity"] = c.GroupeAdresse ?? c.GroupeId, ["Member"] = c.CompteAdresse ?? c.CompteId, ["BypassSecurityGroupManagerCheck"] = true }, ct)
        : _graph.Groups[c.GroupeId].Members.Ref.PostAsync(
            new Microsoft.Graph.Models.ReferenceCreate { OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{c.CompteId}" }, cancellationToken: ct);

    public Task RetirerMembreAsync(ChangementOrganisation c, CancellationToken ct = default) => c.ParExchange
        ? ExchangeAsync("Remove-DistributionGroupMember", new() { ["Identity"] = c.GroupeAdresse ?? c.GroupeId, ["Member"] = c.CompteAdresse ?? c.CompteId, ["BypassSecurityGroupManagerCheck"] = true, ["Confirm"] = false }, ct)
        : _graph.Groups[c.GroupeId].Members[c.CompteId].Ref.DeleteAsync(cancellationToken: ct);

    public async Task<string> TesterExchangeAsync(string groupe, CancellationToken ct = default)
    {
        var reponse = await ExchangeAsync("Get-DistributionGroupMember", new() { ["Identity"] = groupe, ["ResultSize"] = 5 }, ct);
        return $"Accès Exchange Online correct : lecture de la liste {groupe} réussie.{(reponse.Length > 0 ? "" : "")}";
    }

    /// <summary>
    /// Commande Exchange Online par son API d'administration (celle du module PowerShell ExchangeOnlineManagement v3),
    /// avec le jeton de l'application.
    /// </summary>
    private async Task<string> ExchangeAsync(string commande, Dictionary<string, object?> parametres, CancellationToken ct)
    {
        var tenant = config["AzureAd:TenantId"];
        // Jeton neuf (identité recréée) : des droits accordés depuis le dernier jeton sont pris en compte
        var jeton = await ClientGraph.Identifiant(config).GetTokenAsync(new Azure.Core.TokenRequestContext(["https://outlook.office365.com/.default"]), ct);
        using var requete = new HttpRequestMessage(HttpMethod.Post, $"https://outlook.office365.com/adminapi/beta/{tenant}/InvokeCommand")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { CmdletInput = new { CmdletName = commande, Parameters = parametres } }),
        };
        requete.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jeton.Token);
        // Comme le module ExchangeOnlineManagement en connexion d'application : boîte système de l'organisation (domaine initial)
        _domaineInitial ??= await DomaineInitialAsync(ct);
        requete.Headers.TryAddWithoutValidation("X-AnchorMailbox", $"APP:SystemMailbox{{bb558c35-97f1-4cb9-8ff7-d53741dc928c}}@{_domaineInitial}");
        using var reponse = await Http.SendAsync(requete, ct);
        var corps = await reponse.Content.ReadAsStringAsync(ct);
        if (reponse.IsSuccessStatusCode) return corps;
        if (reponse.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            var roles = RolesDuJeton(jeton.Token);
            var raison = string.Join(" ", reponse.Headers.WwwAuthenticate.Select(h => h.ToString())) is { Length: > 0 } w ? w : MessageExchange(corps);
            throw new InvalidOperationException($"Exchange Online refuse l'accès ({(int)reponse.StatusCode}). "
                + (roles.Contains("Exchange.ManageAsApp")
                    ? "Le jeton contient bien le droit Exchange.ManageAsApp : il manque sans doute le rôle « Exchange Recipient Administrator » (ou il n'est pas encore pris en compte, jusqu'à 1 h)."
                    : $"Le jeton ne contient pas le droit Exchange.ManageAsApp (droits présents : {(roles.Count == 0 ? "aucun" : string.Join(", ", roles))}) : consentement non accordé ou pas encore pris en compte.")
                + $" Détail : {raison}");
        }
        throw new InvalidOperationException($"Exchange Online ({(int)reponse.StatusCode}) : {MessageExchange(corps)}");
    }

    /// <summary>Domaine initial du tenant (xxx.onmicrosoft.com).</summary>
    private async Task<string> DomaineInitialAsync(CancellationToken ct)
    {
        try
        {
            var orgs = await _graph.Organization.GetAsync(r => r.QueryParameters.Select = ["verifiedDomains"], ct);
            return orgs?.Value?.SelectMany(o => o.VerifiedDomains ?? []).FirstOrDefault(d => d.IsInitial == true)?.Name
                ?? messagerie.Expediteur?.Split('@').LastOrDefault() ?? "vitalrest.com";
        }
        catch (Exception) { return messagerie.Expediteur?.Split('@').LastOrDefault() ?? "vitalrest.com"; }
    }

    /// <summary>Droits d'application (revendication « roles ») contenus dans un jeton JWT.</summary>
    public static List<string> RolesDuJeton(string jeton)
    {
        try
        {
            var charge = jeton.Split('.')[1].Replace('-', '+').Replace('_', '/');
            charge = charge.PadRight(charge.Length + (4 - charge.Length % 4) % 4, '=');
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(charge));
            return doc.RootElement.TryGetProperty("roles", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [];
        }
        catch (Exception) { return []; }
    }

    private static string MessageExchange(string corps)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(corps);
            var erreur = doc.RootElement.GetProperty("error");
            if (erreur.TryGetProperty("details", out var details) && details.ValueKind == System.Text.Json.JsonValueKind.Array && details.GetArrayLength() > 0
                && details[0].TryGetProperty("message", out var m)) return m.GetString() ?? corps;
            return erreur.GetProperty("message").GetString() ?? corps;
        }
        catch (Exception) { return corps.Length > 300 ? corps[..300] : corps; }
    }
}

/// <summary>Mode démonstration : aucune écriture réelle.</summary>
public sealed class EcritureAnnuaireSimulee(ILogger<EcritureAnnuaireSimulee> log) : IEcritureAnnuaire
{
    public Task DefinirResponsableAsync(string compteId, string responsableId, CancellationToken ct = default)
    {
        log.LogInformation("Simulation : responsable de {Compte} = {Responsable}", compteId, responsableId);
        return Task.CompletedTask;
    }

    public Task AjouterMembreAsync(ChangementOrganisation c, CancellationToken ct = default)
    {
        log.LogInformation("Simulation : {Compte} ajouté au groupe {Groupe}{Exchange}", c.CompteAdresse ?? c.CompteId, c.Apres, c.ParExchange ? " (Exchange)" : "");
        return Task.CompletedTask;
    }

    public Task RetirerMembreAsync(ChangementOrganisation c, CancellationToken ct = default)
    {
        log.LogInformation("Simulation : {Compte} retiré du groupe {Groupe}{Exchange}", c.CompteAdresse ?? c.CompteId, c.Avant, c.ParExchange ? " (Exchange)" : "");
        return Task.CompletedTask;
    }

    public Task<string> TesterExchangeAsync(string groupe, CancellationToken ct = default) => Task.FromResult($"Mode démonstration : pas d'accès réel à Exchange Online ({groupe}).");
}

/// <summary>
/// Page « Synchronisation organisation » : à partir du fichier Organisation, met à jour pour chaque site
/// le responsable (responsable de secteur, « Email RS ») de son compte Microsoft et de sa fiche, et son groupe de sites de région
/// (REGION_IDF_SITES… : un site ouvert n'est que dans le groupe de sa région ; un site fermé sort de tous ces groupes).
/// </summary>
public sealed class ServiceSynchronisationOrganisation(
    IDbContextFactory<ParcDbContext> dbFactory, IAnnuaireMicrosoft annuaire, IEcritureAnnuaire ecriture, ILogger<ServiceSynchronisationOrganisation> log)
{
    /// <summary>Début du nom des groupes lus dans l'annuaire (REGION_IDF_SITES…) ; seuls ceux de la correspondance sont touchés.</summary>
    public const string PrefixeGroupes = "REGION_";

    // --- Lecture du fichier ---

    /// <summary>Lignes du fichier ; un site « Open » dont la date de fermeture est passée est compté fermé.</summary>
    public static List<LigneOrganisation> Lire(Stream fichier, DateOnly? aujourdhui = null)
    {
        var jour = aujourdhui ?? DateOnly.FromDateTime(DateTime.Today);
        using var wb = new XLWorkbook(fichier);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Organisation", StringComparison.OrdinalIgnoreCase)) ?? wb.Worksheets.First();
        var entete = ws.FirstRowUsed() ?? throw new ErreurMetier("Le fichier est vide.");
        var colonnes = entete.CellsUsed().ToDictionary(c => Normalisation.SansAccents(c.GetString().Trim()).ToUpperInvariant(), c => c.Address.ColumnNumber);
        int Colonne(string nom) => colonnes.TryGetValue(nom, out var n) ? n
            : throw new ErreurMetier($"Colonne « {nom} » introuvable : est-ce bien l'export Organisation (colonnes Nom, Etat, Région, Email RS) ?");
        var (cCode, cNom, cEtat, cRegion, cRs) = (Colonne("NOM"), colonnes.GetValueOrDefault("NOM COMMERCIAL"), Colonne("ETAT"), Colonne("REGION"), Colonne("EMAIL RS"));
        var cFermeture = colonnes.GetValueOrDefault("DATE FERMETURE");

        string? Valeur(IXLRow r, int c) => c == 0 ? null : r.Cell(c).GetString().Trim() is { Length: > 0 } v && v != "None" ? v : null;
        var lignes = new List<LigneOrganisation>();
        foreach (var r in ws.RowsUsed().Where(r => r.RowNumber() > entete.RowNumber()))
        {
            if (Valeur(r, cCode) is not { } code) continue;
            var ferme = cFermeture > 0 && DateDe(r.Cell(cFermeture)) is { } fermeture && fermeture < jour;
            lignes.Add(new LigneOrganisation(code, Valeur(r, cNom), !ferme && string.Equals(Valeur(r, cEtat), "Open", StringComparison.OrdinalIgnoreCase),
                Valeur(r, cRegion), Valeur(r, cRs)?.ToLowerInvariant()));
        }
        if (lignes.Count == 0) throw new ErreurMetier("Aucun site dans le fichier.");
        return lignes;
    }

    /// <summary>Date d'une cellule : vraie date Excel ou texte « jj/mm/aaaa ».</summary>
    private static DateOnly? DateDe(IXLCell c) =>
        c.DataType == XLDataType.DateTime ? DateOnly.FromDateTime(c.GetDateTime())
        : DateOnly.TryParseExact(c.GetString().Trim(), "dd/MM/yyyy", out var d) ? d : null;

    /// <summary>Code en tête du nom d'affichage d'un compte de site : « 455 - FOYER HORIZON » → 455, « A010-40 - … » → A010-40.</summary>
    public static string? CodeDuNom(string nom) =>
        Regex.Match(nom, @"^\s*([A-Za-z]?\d+(?:-\d+)?)\s+-\s") is { Success: true } m ? m.Groups[1].Value : null;

    private static bool MemeCode(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(a.TrimStart('0'), b.TrimStart('0'), StringComparison.OrdinalIgnoreCase);

    // --- Calcul des changements ---

    /// <summary>Changements à faire, sans rien modifier (comptes : copie de la dernière synchronisation ; groupes : lus à l'instant).</summary>
    public static PlanOrganisation Calculer(IReadOnlyList<LigneOrganisation> lignes, IReadOnlyList<Site> sites, IReadOnlyList<CompteMicrosoft> comptes,
        IReadOnlyList<GroupeRegion> groupes, CorrespondanceGroupes? correspondance = null)
    {
        var cfg = correspondance ?? CorrespondanceGroupes.ParDefaut();
        var changements = new List<ChangementOrganisation>();
        var problemes = new List<string>();
        groupes = groupes.Where(cfg.Concerne).ToList();
        GroupeRegion? GroupeDe(string? region) => cfg.GroupeDe(region) is { } nom ? groupes.FirstOrDefault(g => CorrespondanceGroupes.Est(g, nom)) : null;
        var parId = comptes.Where(c => !c.Supprime).ToDictionary(c => c.Id);
        var parAdresse = new Dictionary<string, CompteMicrosoft>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in comptes.Where(c => !c.Supprime))
            foreach (var a in c.Adresses) parAdresse.TryAdd(a, c);

        // Comptes des sites par le code en tête de leur nom d'affichage (« 455 - FOYER HORIZON »)
        var parCodeDuNom = parId.Values.Select(c => (Code: CodeDuNom(c.NomAffichage), Compte: c)).Where(x => x.Code is not null)
            .GroupBy(x => x.Code!.TrimStart('0').ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Select(x => x.Compte).ToList());
        List<CompteMicrosoft> ParNom(string code) => parCodeDuNom.GetValueOrDefault(code.TrimStart('0').ToUpperInvariant()) ?? [];

        // Rapprochement fichier → compte Microsoft du site : celui relié à la fiche du site, sinon celui qui porte son code
        // (un sous-site sans compte à son code : le compte du site principal)
        var sansCompte = new List<string>();
        var ambigus = new List<string>();
        var parNom = new List<string>();
        var parCompte = new Dictionary<string, List<(LigneOrganisation Ligne, Site? Site)>>();
        foreach (var l in lignes)
        {
            var site = sites.FirstOrDefault(s => MemeCode(s.Code, l.Code));

            // Fiche du site : responsable de secteur (nom de l'annuaire, à défaut son adresse)
            if (site is not null && l.Ouvert && l.EmailResponsable is { } rs)
            {
                var nom = parAdresse.GetValueOrDefault(rs)?.NomAffichage ?? rs;
                if (!string.Equals(site.Responsable, nom, StringComparison.Ordinal))
                    changements.Add(new ChangementOrganisation(TypeChangementOrganisation.ResponsableInventaire, site.Code, null, site.Libelle, site.Responsable, nom, SiteId: site.Id));
            }

            string? compteId = site?.EntraObjectId is { } relie && parId.ContainsKey(relie) ? relie : null;
            if (compteId is null)
            {
                var candidats = ParNom(l.Code);
                if (candidats.Count == 0 && l.Code.Contains('-'))
                {
                    var codePrincipal = l.Code[..l.Code.IndexOf('-')];
                    candidats = sites.FirstOrDefault(s => MemeCode(s.Code, codePrincipal))?.EntraObjectId is { } principal && parId.TryGetValue(principal, out var comptePrincipal)
                        ? [comptePrincipal] : ParNom(codePrincipal);
                }
                if (candidats.Count > 1)
                {
                    if (l.Ouvert) ambigus.Add($"{l.Code} ({string.Join(" / ", candidats.Select(c => c.NomAffichage))})");
                    continue;
                }
                if (candidats.Count == 1)
                {
                    compteId = candidats[0].Id;
                    if (l.Ouvert) parNom.Add($"{l.Code} → {candidats[0].NomAffichage}");
                }
            }
            if (compteId is null)
            {
                if (l.Ouvert) sansCompte.Add(l.Code);
                continue;
            }
            if (!parCompte.TryGetValue(compteId, out var liste)) parCompte[compteId] = liste = [];
            liste.Add((l, site));
        }
        if (sansCompte.Count > 0) problemes.Add($"{sansCompte.Count} site(s) ouvert(s) sans compte Microsoft trouvé (ni relié à la fiche du site, ni à son nom « code - … ») : {string.Join(", ", sansCompte.Order())}.");
        if (ambigus.Count > 0) problemes.Add($"{ambigus.Count} site(s) ouvert(s) avec plusieurs comptes à leur code, laissés de côté : {string.Join(" ; ", ambigus.Order())}.");

        var regionsSansGroupe = new SortedSet<string>();
        var rsInconnus = new SortedSet<string>();
        foreach (var (compteId, rattaches) in parCompte)
        {
            var compte = parId[compteId];
            var codes = string.Join(", ", rattaches.Select(x => x.Ligne.Code).Distinct().Order());
            var ouverts = rattaches.Where(x => x.Ligne.Ouvert).ToList();
            // Sous-sites qui partagent le compte du site : la ligne du site principal (code sans tiret) fait foi
            var principal = ouverts.OrderBy(x => x.Ligne.Code.Contains('-')).ThenBy(x => x.Ligne.Code).Select(x => x.Ligne).FirstOrDefault();
            if (ouverts.Select(x => cfg.GroupeDe(x.Ligne.Region) ?? x.Ligne.Region).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                problemes.Add($"Compte {compte.NomAffichage} ({codes}) : régions différentes dans le fichier, celle du site {principal!.Code} ({principal.Region}) est retenue.");

            var cible = principal is null ? null : GroupeDe(principal.Region);
            if (principal is not null && cible is null && principal.Region is { } r) regionsSansGroupe.Add(r);
            foreach (var g in groupes)
            {
                var membre = g.Membres.Contains(compteId);
                var blocage = g.Modifiable ? null : g.Raison;
                var aVider = cfg.AVider.Any(n => CorrespondanceGroupes.Est(g, n));
                if (g == cible && !membre)
                    changements.Add(new ChangementOrganisation(TypeChangementOrganisation.AjoutGroupe, codes, compteId, compte.NomAffichage, null, g.Nom, g.Id,
                        Blocage: blocage, GroupeAdresse: g.Adresse, ParExchange: g.Exchange, CompteAdresse: compte.AdressePrincipale));
                // Site fermé : retiré de tous les groupes de sites ; site ouvert : seulement du groupe de sa région ; anciens groupes vidés
                else if (g != cible && membre && (principal is null || cible is not null || aVider))
                    changements.Add(new ChangementOrganisation(TypeChangementOrganisation.RetraitGroupe, codes, compteId, compte.NomAffichage, g.Nom, null, g.Id,
                        Blocage: blocage, GroupeAdresse: g.Adresse, ParExchange: g.Exchange, CompteAdresse: compte.AdressePrincipale));
            }

            if (principal?.EmailResponsable is not { } email) continue;
            if (parAdresse.GetValueOrDefault(email) is not { } responsable) { rsInconnus.Add(email); continue; }
            if (compte.ResponsableId != responsable.Id)
                changements.Add(new ChangementOrganisation(TypeChangementOrganisation.Responsable, codes, compteId, compte.NomAffichage,
                    compte.ResponsableNom, responsable.NomAffichage, ResponsableId: responsable.Id));
        }
        if (regionsSansGroupe.Count > 0) problemes.Add($"Groupe introuvable dans l'annuaire pour la région : {string.Join(", ", regionsSansGroupe.Select(r => $"{r} ({cfg.GroupeDe(r) ?? "pas de correspondance"})"))} (sites laissés dans leurs groupes actuels).");
        if (rsInconnus.Count > 0) problemes.Add($"{rsInconnus.Count} responsable(s) de secteur introuvable(s) dans l'annuaire Microsoft : {string.Join(", ", rsInconnus)}.");

        var correspondances = lignes.Where(l => l.Ouvert && l.Region is not null).Select(l => l.Region!).Distinct(StringComparer.OrdinalIgnoreCase).Order()
            .Select(r => (r, GroupeDe(r)?.Nom)).ToList();
        return new PlanOrganisation(lignes.Count, lignes.Count(l => l.Ouvert), lignes.Count(l => !l.Ouvert), parCompte.Count,
            changements.OrderBy(c => c.Type).ThenBy(c => c.Sites, StringComparer.OrdinalIgnoreCase).ToList(), correspondances, problemes,
            parNom.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<PlanOrganisation> AnalyserAsync(IReadOnlyList<LigneOrganisation> lignes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sites = await db.Sites.AsNoTracking().ToListAsync(ct);
        var comptes = await db.ComptesMicrosoft.AsNoTracking().ToListAsync(ct);
        var groupes = await annuaire.LireGroupesAsync(PrefixeGroupes, ct);
        var cfg = CorrespondanceGroupes.ParDefaut();
        return Calculer(lignes, sites, comptes, groupes, cfg) with
        {
            GroupesLus = groupes.Where(cfg.Concerne).OrderBy(g => g.Nom)
                .Select(g => g.Nom + (g.Exchange ? " (liste de diffusion)" : "") + (cfg.AVider.Any(n => CorrespondanceGroupes.Est(g, n)) ? " — à vider" : "")).ToList(),
        };
    }

    /// <summary>Vérifie, sans rien modifier, que l'application peut lire les listes de diffusion via Exchange Online.</summary>
    public Task<string> TesterExchangeAsync(CancellationToken ct = default) => ecriture.TesterExchangeAsync("REGION_IDF_SITES", ct);

    // --- Application ---

    public async Task<List<ResultatChangement>> AppliquerAsync(PlanOrganisation plan, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstAdministrateur) throw new ErreurMetier("Réservé aux administrateurs principaux.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var resultats = new List<ResultatChangement>();
        var maintenant = DateTime.UtcNow;
        foreach (var c in plan.Changements.Where(c => c.Blocage is null))
        {
            string? erreur = null;
            try
            {
                switch (c.Type)
                {
                    case TypeChangementOrganisation.Responsable:
                        await ecriture.DefinirResponsableAsync(c.CompteId!, c.ResponsableId!, ct);
                        if (await db.ComptesMicrosoft.FindAsync([c.CompteId!], ct) is { } compte)
                            (compte.ResponsableId, compte.ResponsableNom) = (c.ResponsableId, c.Apres);
                        break;
                    case TypeChangementOrganisation.AjoutGroupe:
                        await ecriture.AjouterMembreAsync(c, ct);
                        break;
                    case TypeChangementOrganisation.RetraitGroupe:
                        await ecriture.RetirerMembreAsync(c, ct);
                        break;
                    case TypeChangementOrganisation.ResponsableInventaire:
                        if (await db.Sites.FindAsync([c.SiteId!.Value], ct) is { } site) site.Responsable = c.Apres;
                        break;
                }
            }
            catch (ODataError ex) { erreur = Message(ex); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Synchronisation organisation : {Type} {Sites}", c.Type, c.Sites);
                erreur = ex.Message;
            }
            resultats.Add(new ResultatChangement(c, erreur));
            if (erreur is null && c.Type != TypeChangementOrganisation.ResponsableInventaire)
                db.Journal.Add(new Journal { Date = maintenant, Utilisateur = u.Signature, Action = "Synchronisation organisation", Details = JournalInventaire.Tronquer(Texte(c)) });
        }
        var faits = resultats.Count(r => r.Erreur is null);
        db.Journal.Add(new Journal
        {
            Date = maintenant, Utilisateur = u.Signature, Action = "Synchronisation organisation",
            Details = $"Fichier Organisation appliqué : {faits} changement(s) fait(s), {resultats.Count - faits} en échec, {plan.Changements.Count(c => c.Blocage is not null)} non modifiable(s) par l'application",
        });
        await db.SaveChangesAsync(ct);
        return resultats;
    }

    public static string Texte(ChangementOrganisation c) => c.Type switch
    {
        TypeChangementOrganisation.Responsable => $"{c.CompteNom} ({c.Sites}) : responsable {c.Avant ?? "aucun"} → {c.Apres}",
        TypeChangementOrganisation.AjoutGroupe => $"{c.CompteNom} ({c.Sites}) ajouté au groupe {c.Apres}",
        TypeChangementOrganisation.RetraitGroupe => $"{c.CompteNom} ({c.Sites}) retiré du groupe {c.Avant}",
        _ => $"Fiche du site {c.CompteNom} : responsable {c.Avant ?? "aucun"} → {c.Apres}",
    };

    private static string Message(ODataError ex) => ex.Error?.Code switch
    {
        "Authorization_RequestDenied" => "Droits d'écriture non accordés à l'application (script 4-droits-synchronisation-organisation.sh).",
        _ => ex.Error?.Message ?? ex.Message,
    };
}
