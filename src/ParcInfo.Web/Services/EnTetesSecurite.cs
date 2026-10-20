using System.Security.Cryptography;

namespace ParcInfo.Web.Services;

/// <summary>
/// En-têtes de sécurité envoyés au navigateur sur toutes les réponses : politique de contenu (CSP), interdiction d'afficher
/// le site dans un cadre, pas de devinette du type de fichier, référent limité, capteurs désactivés.
/// </summary>
public static class EnTetesSecurite
{
    private const string CleNonce = "csp-nonce";

    /// <summary>Jeton aléatoire de la requête autorisant le seul script en ligne de la page (la carte d'import de Blazor).</summary>
    public static string Nonce(HttpContext ctx) => ctx.Items[CleNonce] as string ?? "";

    public static IApplicationBuilder UseEnTetesSecurite(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        ctx.Items[CleNonce] = nonce;
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h.ContentSecurityPolicy = string.Join("; ",
                "default-src 'self'",
                $"script-src 'self' 'nonce-{nonce}'",
                // MudBlazor positionne ses composants avec des styles en ligne
                "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
                "font-src 'self' data: https://fonts.gstatic.com",
                "img-src 'self' data: blob:",
                // Connexion temps réel de Blazor (WebSocket vers le même site)
                "connect-src 'self'",
                "form-action 'self' https://login.microsoftonline.com",
                "frame-ancestors 'none'",
                "base-uri 'self'",
                "object-src 'none'");
            h.XFrameOptions = "DENY";
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            return Task.CompletedTask;
        });
        await next();
    });
}
