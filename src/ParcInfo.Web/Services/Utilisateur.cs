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
    /// <summary>Administrateur principal (« Authentification:Administrateurs ») : tous les droits du service informatique et la page « Droits ».</summary>
    public const string Administrateur = "Administrateur";
    public const string PolitiqueAdministration = "Administration";
    /// <summary>Service informatique : gestion du parc (droit « Gestionnaire » de la page « Droits »).</summary>
    public const string Gestionnaire = "Gestionnaire";
    /// <summary>Consultation de l'inventaire, des fiches matériel et du journal d'inventaire, sans modification.</summary>
    public const string ConsultationInventaire = "ConsultationInventaire";
    public const string PolitiqueInventaire = "Inventaire";
    /// <summary>Seul rôle autorisé à valider ou refuser les demandes.</summary>
    public const string Valideur = "Valideur";
    public const string PolitiqueGestion = "Gestion";
    /// <summary>Accès à la liste des demandes et aux échanges internes : gestionnaires et valideurs (seuls les valideurs décident).</summary>
    public const string PolitiqueValidation = "Validation";
    /// <summary>Administration des tickets : gestionnaires et comptes ayant le droit « Admin tickets ».</summary>
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
        .AddPolicy(PolitiqueAdministration, p => p.RequireAuthenticatedUser().RequireRole(Administrateur))
        .AddPolicy(PolitiqueGestion, p => p.RequireAuthenticatedUser().RequireRole(Gestionnaire))
        .AddPolicy(PolitiqueInventaire, p => p.RequireAuthenticatedUser().RequireRole(Gestionnaire, ConsultationInventaire))
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

    /// <summary>Administrateur principal : gère les droits.</summary>
    public bool EstAdministrateur { get; init; }

    /// <summary>Voit l'inventaire et le journal d'inventaire (service informatique ou droit « Consultation inventaire »).</summary>
    public bool VoitInventaire { get; init; }

    /// <summary>Voit toutes les demandes et leurs échanges internes : service informatique et valideurs.</summary>
    public bool VoitToutesLesDemandes => EstGestionnaire || EstValideur;

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
        return new Utilisateur(nom, email, gestionnaire, p.IsInRole(Roles.Valideur))
        {
            Adresses = adresses,
            EstAdminTickets = gestionnaire || p.IsInRole(Roles.AdminTickets),
            EstSite = !gestionnaire && p.IsInRole(Roles.Site),
            EstAdministrateur = p.IsInRole(Roles.Administrateur),
            VoitInventaire = gestionnaire || p.IsInRole(Roles.ConsultationInventaire),
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
/// Donne à l'utilisateur connecté ses rôles :
/// administrateurs principaux de la configuration (« Authentification:Administrateurs ») et droits de la page « Droits » (en base).
/// Avec Entra ID, les rôles portés par le jeton sont ignorés : la page « Droits » est la seule source
/// (les anciens rôles Entra ID et listes de configuration ont été repris une fois, à la création de la page).
/// En mode démo (sans Entra ID), les rôles choisis à la connexion sont conservés.
/// </summary>
public sealed class AjoutRolesConfigures(IConfiguration config, IDbContextFactory<ParcDbContext>? dbFactory = null, IMemoryCache? cache = null)
    : IClaimsTransformation
{
    private readonly HashSet<string> _administrateurs = new(ServiceDroits.AdministrateursDe(config), StringComparer.OrdinalIgnoreCase);
    private readonly bool _rolesDuJeton = string.IsNullOrWhiteSpace(config["AzureAd:ClientId"]);

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is ClaimsIdentity { IsAuthenticated: true } id)
        {
            var duJeton = TypesRole.SelectMany(t => id.FindAll(t)).ToList();
            foreach (var c in duJeton) id.TryRemoveClaim(c);
            if (_rolesDuJeton)
                foreach (var role in duJeton.Select(c => c.Value).Distinct()) Ajouter(id, principal, role, true);

            var u = Utilisateur.Depuis(principal);
            var admin = u.ToutesAdresses.Any(_administrateurs.Contains);
            var droit = dbFactory is null ? null : await ServiceDroits.DroitDeAsync(dbFactory, cache, u.ToutesAdresses);
            Ajouter(id, principal, Roles.Administrateur, admin);
            Ajouter(id, principal, Roles.Gestionnaire, admin || droit?.Gestionnaire == true);
            Ajouter(id, principal, Roles.Valideur, droit?.Valideur == true);
            Ajouter(id, principal, Roles.AdminTickets, droit?.AdminTickets == true);
            Ajouter(id, principal, Roles.ConsultationInventaire, droit?.ConsultationInventaire == true);
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
