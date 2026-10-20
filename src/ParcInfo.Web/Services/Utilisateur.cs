using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;

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
}

/// <summary>Utilisateur connecté (compte Microsoft 365 / Entra ID).</summary>
public sealed record Utilisateur(string Nom, string Email, bool EstGestionnaire, bool EstValideur = false)
{
    public static Utilisateur Depuis(ClaimsPrincipal p)
    {
        var email = p.FindFirst("preferred_username")?.Value
            ?? p.FindFirst(ClaimTypes.Email)?.Value
            ?? p.FindFirst(ClaimTypes.Upn)?.Value
            ?? p.Identity?.Name
            ?? "";
        var nom = p.FindFirst("name")?.Value ?? p.FindFirst(ClaimTypes.Name)?.Value ?? email;
        var gestionnaire = p.IsInRole(Roles.Gestionnaire);
        return new Utilisateur(nom, email.ToLowerInvariant(), gestionnaire, gestionnaire || p.IsInRole(Roles.Valideur));
    }

    public string Signature => $"{Nom} <{Email}>";

    public bool AEmail(string? email) => !string.IsNullOrWhiteSpace(email) && string.Equals(email.Trim(), Email, StringComparison.OrdinalIgnoreCase);
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
/// Donne les rôles aux adresses listées dans la configuration :
/// « Authentification:Gestionnaires » → Gestionnaire (pratique avant d'avoir configuré les rôles d'application Entra ID),
/// « Demandes:Valideurs » → Valideur.
/// </summary>
public sealed class AjoutRolesConfigures(IConfiguration config) : IClaimsTransformation
{
    private readonly HashSet<string> _gestionnaires = new(
        config.GetSection("Authentification:Gestionnaires").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _valideurs = new(
        config.GetSection("Demandes:Valideurs").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is ClaimsIdentity { IsAuthenticated: true } id)
        {
            var email = Utilisateur.Depuis(principal).Email;
            Ajouter(id, principal, Roles.Gestionnaire, _gestionnaires.Contains(email));
            Ajouter(id, principal, Roles.Valideur, _valideurs.Contains(email));
        }
        return Task.FromResult(principal);
    }

    private static void Ajouter(ClaimsIdentity id, ClaimsPrincipal p, string role, bool condition)
    {
        if (condition && !p.IsInRole(role)) id.AddClaim(new Claim(id.RoleClaimType, role));
    }
}
