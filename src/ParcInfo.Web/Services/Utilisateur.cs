using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;

namespace ParcInfo.Web.Services;

public static class Roles
{
    /// <summary>Rôle d'application Entra ID donnant accès à la gestion du parc (équipe informatique).</summary>
    public const string Gestionnaire = "Gestionnaire";
    public const string PolitiqueGestion = "Gestion";
}

/// <summary>Utilisateur connecté (compte Microsoft 365 / Entra ID).</summary>
public sealed record Utilisateur(string Nom, string Email, bool EstGestionnaire)
{
    public static Utilisateur Depuis(ClaimsPrincipal p)
    {
        var email = p.FindFirst("preferred_username")?.Value
            ?? p.FindFirst(ClaimTypes.Email)?.Value
            ?? p.FindFirst(ClaimTypes.Upn)?.Value
            ?? p.Identity?.Name
            ?? "";
        var nom = p.FindFirst("name")?.Value ?? p.FindFirst(ClaimTypes.Name)?.Value ?? email;
        return new Utilisateur(nom, email.ToLowerInvariant(), p.IsInRole(Roles.Gestionnaire));
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
/// Donne le rôle Gestionnaire aux adresses listées dans « Authentification:Gestionnaires »
/// (pratique pour démarrer avant d'avoir configuré les rôles d'application dans Entra ID).
/// </summary>
public sealed class AjoutRoleGestionnaire(IConfiguration config) : IClaimsTransformation
{
    private readonly HashSet<string> _emails = new(
        config.GetSection("Authentification:Gestionnaires").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is ClaimsIdentity { IsAuthenticated: true } id && !principal.IsInRole(Roles.Gestionnaire))
        {
            var email = Utilisateur.Depuis(principal).Email;
            if (_emails.Contains(email))
                id.AddClaim(new Claim(id.RoleClaimType, Roles.Gestionnaire));
        }
        return Task.FromResult(principal);
    }
}
