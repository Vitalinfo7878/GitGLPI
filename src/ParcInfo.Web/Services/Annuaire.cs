using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace ParcInfo.Web.Services;

/// <summary>Compte tel que lu dans l'annuaire Microsoft (Entra ID).</summary>
public sealed record CompteAnnuaire(
    string Id, string NomAffichage, string? Prenom, string? Nom, string? Upn, string? Email, IReadOnlyList<string> AutresAdresses,
    string? Poste, string? Service, string? Bureau, string? Societe, string? Matricule, string? TelephoneMobile, string? TelephonePro,
    string? Adresse, string? Ville, string? ResponsableId, string? ResponsableNom, IReadOnlyList<string> Groupes, IReadOnlyList<string> Licences,
    bool Actif, DateTime? CreeLe);

/// <summary>Rôle d'application Entra ID (« Gestionnaire », « Valideur ») attribué à un compte.</summary>
public sealed record RoleApplication(string Email, string? Nom, string Role);

/// <summary>Lecture de l'annuaire Microsoft (lecture seule).</summary>
public interface IAnnuaireMicrosoft
{
    /// <summary>Faux si l'application n'est pas reliée à Entra ID (poste de développement).</summary>
    bool Disponible { get; }
    Task<IReadOnlyList<CompteAnnuaire>> LireComptesAsync(CancellationToken ct = default);

    /// <summary>Rôles d'application de « Parc informatique Vitalrest » attribués dans Entra ID (reprise des droits).</summary>
    Task<IReadOnlyList<RoleApplication>> LireRolesApplicationAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RoleApplication>>([]);
}

public static class ClientGraph
{
    /// <summary>Connexion à Microsoft Graph avec le secret de l'application (ou son identité managée).</summary>
    public static GraphServiceClient Creer(IConfiguration config)
    {
        var az = config.GetSection("AzureAd");
        TokenCredential identifiant = string.IsNullOrEmpty(az["ClientSecret"])
            ? new DefaultAzureCredential()
            : new ClientSecretCredential(az["TenantId"], az["ClientId"], az["ClientSecret"]);
        return new GraphServiceClient(identifiant, ["https://graph.microsoft.com/.default"]);
    }
}

/// <summary>
/// Annuaire lu via Microsoft Graph. Nécessite l'autorisation d'application « Directory.Read.All » (lecture seule) :
/// comptes, responsable, groupes et licences.
/// </summary>
public sealed class AnnuaireGraph(IConfiguration config) : IAnnuaireMicrosoft
{
    private readonly GraphServiceClient _graph = ClientGraph.Creer(config);

    public bool Disponible => true;

    public async Task<IReadOnlyList<CompteAnnuaire>> LireComptesAsync(CancellationToken ct = default)
    {
        // Noms des licences de l'organisation
        var skus = await _graph.SubscribedSkus.GetAsync(cancellationToken: ct);
        var licences = (skus?.Value ?? []).Where(s => s.SkuId is not null)
            .ToDictionary(s => s.SkuId!.Value, s => NomsLicences.Libelle(s.SkuPartNumber));

        // Groupes : membres directs de chaque groupe
        var groupesParCompte = new Dictionary<string, List<string>>();
        var groupes = await ToutLire<Group, GroupCollectionResponse>(
            await _graph.Groups.GetAsync(r => { r.QueryParameters.Select = ["id", "displayName"]; r.QueryParameters.Top = 999; }, ct), ct);
        foreach (var g in groupes.Where(g => g.Id is not null && !string.IsNullOrWhiteSpace(g.DisplayName)))
        {
            var membres = await ToutLire<User, UserCollectionResponse>(
                await _graph.Groups[g.Id].Members.GraphUser.GetAsync(r => { r.QueryParameters.Select = ["id"]; r.QueryParameters.Top = 999; }, ct), ct);
            foreach (var m in membres.Where(m => m.Id is not null))
            {
                if (!groupesParCompte.TryGetValue(m.Id!, out var liste)) groupesParCompte[m.Id!] = liste = [];
                liste.Add(g.DisplayName!.Trim());
            }
        }

        // Comptes (membres de l'organisation, sans les invités) avec leur responsable
        var comptes = await ToutLire<User, UserCollectionResponse>(await _graph.Users.GetAsync(r =>
        {
            r.QueryParameters.Select =
            [
                "id", "displayName", "givenName", "surname", "userPrincipalName", "mail", "otherMails", "proxyAddresses", "jobTitle",
                "department", "officeLocation", "companyName", "employeeId", "mobilePhone", "businessPhones", "streetAddress", "postalCode",
                "city", "accountEnabled", "createdDateTime", "assignedLicenses", "userType",
            ];
            r.QueryParameters.Expand = ["manager($select=id,displayName)"];
            r.QueryParameters.Top = 999;
        }, ct), ct);

        return comptes
            .Where(u => u.Id is not null && !string.Equals(u.UserType, "Guest", StringComparison.OrdinalIgnoreCase))
            .Select(u => new CompteAnnuaire(
                u.Id!, u.DisplayName?.Trim() ?? u.UserPrincipalName ?? u.Id!, Vide(u.GivenName), Vide(u.Surname), Vide(u.UserPrincipalName), Vide(u.Mail),
                (u.ProxyAddresses ?? []).Where(a => a.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase)).Select(a => a[5..])
                    .Concat(u.OtherMails ?? []).Select(a => a.Trim().ToLowerInvariant()).Distinct().ToList(),
                Vide(u.JobTitle), Vide(u.Department), Vide(u.OfficeLocation), Vide(u.CompanyName), Vide(u.EmployeeId), Vide(u.MobilePhone),
                Vide(string.Join(" / ", u.BusinessPhones ?? [])),
                Vide(string.Join(", ", new[] { u.StreetAddress, string.Join(" ", new[] { u.PostalCode, u.City }.Where(x => !string.IsNullOrWhiteSpace(x))) }
                    .Where(x => !string.IsNullOrWhiteSpace(x)))),
                Vide(u.City),
                u.Manager?.Id, Vide((u.Manager as User)?.DisplayName),
                groupesParCompte.TryGetValue(u.Id!, out var gs) ? gs.Order(StringComparer.CurrentCultureIgnoreCase).ToList() : [],
                (u.AssignedLicenses ?? []).Where(l => l.SkuId is not null).Select(l => licences.GetValueOrDefault(l.SkuId!.Value, l.SkuId.Value.ToString())).Order().ToList(),
                u.AccountEnabled ?? false, u.CreatedDateTime?.UtcDateTime))
            .ToList();
    }

    public async Task<IReadOnlyList<RoleApplication>> LireRolesApplicationAsync(CancellationToken ct = default)
    {
        var appli = config["AzureAd:ClientId"];
        var sps = await _graph.ServicePrincipals.GetAsync(r =>
        {
            r.QueryParameters.Filter = $"appId eq '{appli}'";
            r.QueryParameters.Select = ["id", "appRoles"];
        }, ct);
        var sp = sps?.Value?.FirstOrDefault();
        if (sp?.Id is null) return [];
        var roles = (sp.AppRoles ?? []).Where(r => r.Id is not null && r.Value is not null).ToDictionary(r => r.Id!.Value, r => r.Value!);
        var affectations = await ToutLire<AppRoleAssignment, AppRoleAssignmentCollectionResponse>(
            await _graph.ServicePrincipals[sp.Id].AppRoleAssignedTo.GetAsync(r => r.QueryParameters.Top = 999, ct), ct);

        var resultat = new List<RoleApplication>();
        foreach (var a in affectations.Where(a => a.PrincipalType == "User" && a.PrincipalId is not null && a.AppRoleId is { } id && roles.ContainsKey(id)))
        {
            var u = await _graph.Users[a.PrincipalId!.Value.ToString()].GetAsync(r => r.QueryParameters.Select = ["mail", "userPrincipalName", "displayName"], ct);
            var email = Vide(u?.Mail) ?? Vide(u?.UserPrincipalName);
            if (email is not null) resultat.Add(new RoleApplication(email.ToLowerInvariant(), Vide(u?.DisplayName), roles[a.AppRoleId!.Value]));
        }
        return resultat;
    }

    private async Task<List<T>> ToutLire<T, TPage>(TPage? premiere, CancellationToken ct) where TPage : BaseCollectionPaginationCountResponse, new()
    {
        var tout = new List<T>();
        if (premiere is null) return tout;
        var iterateur = PageIterator<T, TPage>.CreatePageIterator(_graph, premiere, e => { tout.Add(e); return true; });
        await iterateur.IterateAsync(ct);
        return tout;
    }

    private static string? Vide(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Annuaire fictif du mode démonstration (sans Entra ID) : permet d'essayer les écrans sur un poste de développement.</summary>
public sealed class AnnuaireDemo : IAnnuaireMicrosoft
{
    public bool Disponible => true;

    public Task<IReadOnlyList<CompteAnnuaire>> LireComptesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CompteAnnuaire>>(
    [
        Compte("d1", "Camille Test", "Camille", "Test", "camille.test@vitalrest.com", "Chef gérant", "Exploitation", "Thomas Verchère", true),
        Compte("d2", "Dominique Martin", "Dominique", "Martin", "dominique.martin@vitalrest.com", "Comptable", "Comptabilité", "Thomas Verchère", true),
        Compte("d3", "Dominique Martinez", "Dominique", "Martinez", "dominique.martinez@vitalrest.com", "Cuisinier", "Exploitation", "Camille Test", false),
        Compte("d4", "Site 087 - EHPAD Les Tilleuls", null, null, "site087@vitalrest.com", null, "Sites", null, true),
        Compte("d5", "Thomas Verchère", "Thomas", "Verchère", "thomas.verchere@groupe-vitalrest.com", "Directeur des opérations", "Direction", null, true),
    ]);

    private static CompteAnnuaire Compte(string id, string nom, string? prenom, string? famille, string mail, string? poste, string? service, string? responsable, bool actif) =>
        new(id, nom, prenom, famille, mail, mail, [], poste, service, "Siège", "Vitalrest", null, "06 12 34 56 78", null, null, "Paris",
            responsable switch { null => null, "Thomas Verchère" => "d5", _ => "d1" }, responsable, service == "Comptabilité" ? ["Tous les salariés", service, "SERV_COMPTA"] : ["Tous les salariés", service ?? "Divers"], ["Microsoft 365 Business Standard"], actif,
            new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
}

/// <summary>Noms lisibles des licences Microsoft les plus courantes (sinon le code technique est affiché).</summary>
public static class NomsLicences
{
    private static readonly Dictionary<string, string> Noms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["O365_BUSINESS_ESSENTIALS"] = "Microsoft 365 Business Basic",
        ["O365_BUSINESS_PREMIUM"] = "Microsoft 365 Business Standard",
        ["SPB"] = "Microsoft 365 Business Premium",
        ["O365_BUSINESS"] = "Microsoft 365 Apps for business",
        ["OFFICESUBSCRIPTION"] = "Microsoft 365 Apps for enterprise",
        ["EXCHANGESTANDARD"] = "Exchange Online (Plan 1)",
        ["EXCHANGEENTERPRISE"] = "Exchange Online (Plan 2)",
        ["EXCHANGEDESKLESS"] = "Exchange Online Kiosk",
        ["STANDARDPACK"] = "Office 365 E1",
        ["ENTERPRISEPACK"] = "Office 365 E3",
        ["SPE_E3"] = "Microsoft 365 E3",
        ["SPE_E5"] = "Microsoft 365 E5",
        ["SPE_F1"] = "Microsoft 365 F3",
        ["DESKLESSPACK"] = "Office 365 F3",
        ["FLOW_FREE"] = "Power Automate Free",
        ["POWER_BI_STANDARD"] = "Power BI (gratuit)",
        ["POWER_BI_PRO"] = "Power BI Pro",
        ["TEAMS_EXPLORATORY"] = "Teams Exploratory",
        ["AAD_PREMIUM"] = "Entra ID P1",
        ["INTUNE_A"] = "Intune",
        ["EMS"] = "Enterprise Mobility + Security E3",
    };

    public static string Libelle(string? code) => code is null ? "?" : Noms.GetValueOrDefault(code, code);
}
