using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ParcInfo.Web.Data;

namespace ParcInfo.Web.Services;

public static class Roles
{
    /// <summary>Rôle d'application Entra ID donnant accès à la gestion du parc (équipe informatique).</summary>
    public const string Gestionnaire = "Gestionnaire";
    /// <summary>Rôle donnant le droit de valider ou refuser les demandes (en plus des gestionnaires).</summary>
    public const string Valideur = "Valideur";
    public const string PolitiqueGestion = "Gestion";
    /// <summary>Accès à la liste des demandes et à leur validation : gestionnaires et valideurs.</summary>
    public const string PolitiqueValidation = "Validation";
    /// <summary>Administration des tickets : gestionnaires et comptes listés dans « Tickets:Administrateurs ».</summary>
    public const string AdminTickets = "AdminTickets";
    public const string PolitiqueTickets = "Tickets";
    /// <summary>Compte Microsoft d'un site (cuisine) : uniquement les tickets, ni demandes ni « Mon matériel ».</summary>
    public const string Site = "Site";
    /// <summary>Espace salarié (« Mon matériel », demandes) : tout le monde sauf les comptes de site.</summary>
    public const string PolitiqueSalarie = "Salarie";

    /// <summary>
    /// Politiques d'accès. Chacune exige un utilisateur connecté : une page portant [Authorize(Policy = …)]
    /// n'applique pas la politique par défaut, la politique doit donc l'exiger elle-même.
    /// </summary>
    public static AuthorizationBuilder DefinirPolitiques(AuthorizationBuilder b) => b
        .AddPolicy(PolitiqueGestion, p => p.RequireAuthenticatedUser().RequireRole(Gestionnaire))
        .AddPolicy(PolitiqueValidation, p => p.RequireAuthenticatedUser().RequireRole(Gestionnaire, Valideur))
        .AddPolicy(PolitiqueTickets, p => p.RequireAuthenticatedUser().RequireRole(Gestionnaire, AdminTickets))
        .AddPolicy(PolitiqueSalarie, p => p.RequireAuthenticatedUser().RequireAssertion(c => !c.User.IsInRole(Site) || c.User.IsInRole(Gestionnaire)))
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
}

/// <summary>Utilisateur connecté (compte Microsoft 365 / Entra ID, en SSO).</summary>
/// <param name="Email">Adresse principale : la boîte mail si Entra la fournit, sinon l'identifiant de connexion.</param>
public sealed record Utilisateur(string Nom, string Email, bool EstGestionnaire, bool EstValideur = false)
{
    /// <summary>
    /// Toutes les adresses connues de la personne (boîte mail, identifiant de connexion UPN…).
    /// Avec plusieurs domaines (vitalrest.com, groupe-vitalrest.com), l'identifiant de connexion peut différer
    /// de l'adresse saisie dans l'inventaire : on accepte l'une ou l'autre.
    /// </summary>
    public IReadOnlyList<string> Adresses { get; init; } = [];

    /// <summary>Accès à l'administration des tickets (traitement, notes internes, analyse).</summary>
    public bool EstAdminTickets { get; init; }

    /// <summary>Compte d'un site : n'a accès qu'aux tickets (l'informatique garde tous ses droits).</summary>
    public bool EstSite { get; init; }

    public static Utilisateur Depuis(ClaimsPrincipal p)
    {
        var adresses = new[] { "email", ClaimTypes.Email, "preferred_username", ClaimTypes.Upn, "upn" }
            .Select(t => p.FindFirst(t)?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v) && v.Contains('@'))
            .Select(v => v!.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        var email = adresses.FirstOrDefault() ?? p.Identity?.Name?.ToLowerInvariant() ?? "";
        var nom = p.FindFirst("name")?.Value ?? p.FindFirst(ClaimTypes.Name)?.Value ?? email;
        var gestionnaire = p.IsInRole(Roles.Gestionnaire);
        return new Utilisateur(nom, email, gestionnaire, gestionnaire || p.IsInRole(Roles.Valideur))
        {
            Adresses = adresses,
            EstAdminTickets = gestionnaire || p.IsInRole(Roles.AdminTickets),
            EstSite = !gestionnaire && p.IsInRole(Roles.Site),
        };
    }

    public string Signature => $"{Nom} <{Email}>";

    /// <summary>Adresses à comparer aux adresses enregistrées (toujours en minuscules).</summary>
    public string[] ToutesAdresses => Adresses.Append(Email).Where(a => a.Length > 0).Distinct().ToArray();

    public bool AEmail(string? email) => !string.IsNullOrWhiteSpace(email)
        && ToutesAdresses.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Accès à l'utilisateur courant depuis les composants Blazor.</summary>
public sealed class UtilisateurCourant(AuthenticationStateProvider auth)
{
    public async Task<Utilisateur> ObtenirAsync()
    {
        var etat = await auth.GetAuthenticationStateAsync();
        return Utilisateur.Depuis(etat.User);
    }
}

/// <summary>
/// Reconnaît les rôles d'application Entra ID quel que soit le nom de revendication reçu (« roles », « role »
/// ou le nom long de .NET, selon la correspondance appliquée à la connexion), puis
/// donne les rôles aux adresses listées dans la configuration :
/// « Authentification:Gestionnaires » → Gestionnaire (pratique avant d'avoir configuré les rôles d'application Entra ID),
/// « Demandes:Valideurs » → Valideur, « Tickets:Administrateurs » → AdminTickets.
/// </summary>
public sealed class AjoutRolesConfigures(IConfiguration config, IDbContextFactory<ParcDbContext>? dbFactory = null, IMemoryCache? cache = null)
    : IClaimsTransformation
{
    private readonly HashSet<string> _gestionnaires = new(
        config.GetSection("Authentification:Gestionnaires").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _valideurs = new(
        config.GetSection("Demandes:Valideurs").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _adminTickets = new(
        config.GetSection("Tickets:Administrateurs").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is ClaimsIdentity { IsAuthenticated: true } id)
        {
            foreach (var role in TypesRole.SelectMany(t => principal.FindAll(t)).Select(c => c.Value).ToList())
                Ajouter(id, principal, role, true);
            var u = Utilisateur.Depuis(principal);
            Ajouter(id, principal, Roles.Gestionnaire, u.ToutesAdresses.Any(_gestionnaires.Contains));
            Ajouter(id, principal, Roles.Valideur, u.ToutesAdresses.Any(_valideurs.Contains));
            Ajouter(id, principal, Roles.AdminTickets, u.ToutesAdresses.Any(_adminTickets.Contains));
            Ajouter(id, principal, Roles.Site, await EstCompteDeSiteAsync(principal, u));
        }
        return principal;
    }

    /// <summary>Compte relié à un site (identifiant Microsoft) ou portant la boîte mail d'un site. Résultat gardé 10 minutes.</summary>
    private async Task<bool> EstCompteDeSiteAsync(ClaimsPrincipal p, Utilisateur u)
    {
        if (dbFactory is null) return false;
        var oid = p.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value ?? p.FindFirst("oid")?.Value;
        var adresses = u.ToutesAdresses;
        var cle = $"compte-site:{oid}:{string.Join(',', adresses)}";
        if (cache?.TryGetValue(cle, out bool connu) == true) return connu;
        await using var db = await dbFactory.CreateDbContextAsync();
        var estSite = await db.Sites.AnyAsync(s => (oid != null && s.EntraObjectId == oid) || (s.Email != null && adresses.Contains(s.Email.ToLower())));
        cache?.Set(cle, estSite, TimeSpan.FromMinutes(10));
        return estSite;
    }

    private static readonly string[] TypesRole = ["roles", "role", ClaimTypes.Role];

    private static void Ajouter(ClaimsIdentity id, ClaimsPrincipal p, string role, bool condition)
    {
        if (condition && !p.IsInRole(role)) id.AddClaim(new Claim(id.RoleClaimType, role));
    }
}
