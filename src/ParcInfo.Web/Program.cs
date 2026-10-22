using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor.Services;
using ParcInfo.Web.Components;
using ParcInfo.Web.Data;
using ParcInfo.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
// HTTPS imposé au navigateur pendant un an (recommandation des outils d'audit, dont Mozilla Observatory)
builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
});
// Ne pas annoncer le serveur web utilisé
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// Dates, nombres et calendriers en français, quelle que soit la langue du serveur (conteneur Linux en anglais)
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

// --- Base de données : SQLite en local, PostgreSQL en production ---
var fournisseur = config["BaseDeDonnees:Fournisseur"] ?? "Sqlite";
var chaine = config.GetConnectionString("Parc") ?? "Data Source=parc.db";
builder.Services.AddDbContextFactory<ParcDbContext>(o =>
{
    if (fournisseur.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        // Tables dans le schéma de l'application (voir InitialisationBase.SchemaPostgres).
        // Pas de chiffrement Kerberos (GSS) : connexion par mot de passe + TLS, la bibliothèque Kerberos n'est pas dans l'image.
        o.UseNpgsql(new Npgsql.NpgsqlConnectionStringBuilder(chaine)
            {
                SearchPath = InitialisationBase.SchemaPostgres,
                GssEncryptionMode = Npgsql.GssEncryptionMode.Disable,
            }.ConnectionString,
            n => n.EnableRetryOnFailure());
    else
        o.UseSqlite(chaine);
});
// Clés de chiffrement des cookies (session SSO, formulaires) conservées en base :
// sans cela, chaque redémarrage du conteneur déconnecterait tout le monde.
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ParcDbContext>>().CreateDbContext());
builder.Services.AddDataProtection()
    .SetApplicationName("ParcInfo")
    .PersistKeysToDbContext<ParcDbContext>();

// Derrière l'entrée HTTPS d'Azure Container Apps, l'application reçoit du HTTP : on fait confiance aux en-têtes
// X-Forwarded-* pour connaître le schéma réel (https), indispensable à l'adresse de retour de la connexion Microsoft.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// --- Authentification : SSO Entra ID (comptes Microsoft 365) ou mode démo local ---
// Le mode démo n'est jamais actif quand Entra ID est configuré.
var entraConfigure = !string.IsNullOrWhiteSpace(config["AzureAd:ClientId"]);
var modeDemo = !entraConfigure && (builder.Environment.IsDevelopment() || config.GetValue<bool>("Authentification:ModeDemo"));
if (entraConfigure)
{
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(config.GetSection("AzureAd"));
    builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
    {
        o.TokenValidationParameters.RoleClaimType = "roles";
        // Demande l'adresse mail en plus de l'identifiant de connexion (voir Utilisateur.Adresses)
        o.Scope.Add("email");
    });
    builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme,
        o => o.AccessDeniedPath = "/acces-refuse");
    builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
}
else if (modeDemo)
{
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.LoginPath = "/demo/connexion";
            o.AccessDeniedPath = "/acces-refuse";
        });
}
else
{
    throw new InvalidOperationException(
        "Authentification non configurée : renseignez la section AzureAd (TenantId, ClientId, ClientSecret) ou activez Authentification:ModeDemo.");
}
builder.Services.AddTransient<IClaimsTransformation, AjoutRolesConfigures>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roles.PolitiqueGestion, p => p.RequireRole(Roles.Gestionnaire))
    .AddPolicy(Roles.PolitiqueValidation, p => p.RequireRole(Roles.Gestionnaire, Roles.Valideur))
    .AddPolicy(Roles.PolitiqueTickets, p => p.RequireRole(Roles.Gestionnaire, Roles.AdminTickets))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();

// --- Messagerie ---
var messagerie = config.GetSection("Messagerie").Get<OptionsMessagerie>() ?? new OptionsMessagerie();
builder.Services.AddSingleton(messagerie);
if (messagerie.Mode.Equals("Graph", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IEnvoiCourriel, EnvoiCourrielGraph>();
else
    builder.Services.AddSingleton<IEnvoiCourriel, EnvoiCourrielSimule>();

// --- Demandes ---
builder.Services.AddSingleton(config.GetSection("Demandes").Get<OptionsDemandes>() ?? new OptionsDemandes());

// --- Services métier ---
builder.Services.AddSingleton<GenerateurFiche>();
builder.Services.AddScoped<ServiceNotifications>();
builder.Services.AddScoped<ServiceMouvements>();
builder.Services.AddScoped<ServiceDemandes>();
builder.Services.AddScoped<ServiceTickets>();
// Annuaire Microsoft : lecture via Graph quand Entra ID est configuré, annuaire fictif en mode démo
if (entraConfigure)
{
    builder.Services.AddSingleton<IAnnuaireMicrosoft, AnnuaireGraph>();
    builder.Services.AddHostedService<SynchronisationAnnuaireNocturne>();
}
else builder.Services.AddSingleton<IAnnuaireMicrosoft, AnnuaireDemo>();
builder.Services.AddScoped<ServiceComptesMicrosoft>();
builder.Services.AddScoped<UtilisateurCourant>();
builder.Services.AddMudServices();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024);

var app = builder.Build();

if (modeDemo && !app.Environment.IsDevelopment())
    app.Logger.LogWarning("MODE DÉMO ACTIF hors développement : n'importe qui peut se connecter. Configurez AzureAd pour activer le SSO.");

// Mots de passe de session chiffrés en base avec une clé conservée hors de la base (secret de l'application)
ChiffrementSecrets.Configurer(config["Chiffrement:Cle"]);
if (!ChiffrementSecrets.Actif && !app.Environment.IsDevelopment())
    app.Logger.LogWarning("Chiffrement:Cle non configurée : les mots de passe de session sont stockés en clair.");
await InitialisationBase.AppliquerAsync(app.Services, fournisseur);

app.UseForwardedHeaders();

// Adresse unique : l'ancienne adresse Azure (*.azurecontainerapps.io) renvoie vers l'adresse publique
// (Messagerie:UrlApplication, ex. https://informatique.vitalrest.com). La sonde /sante reste joignable sur les deux.
if (Uri.TryCreate(messagerie.UrlApplication, UriKind.Absolute, out var adressePublique))
{
    app.Use(async (ctx, next) =>
    {
        var hote = ctx.Request.Host.Host;
        if (hote.EndsWith(".azurecontainerapps.io", StringComparison.OrdinalIgnoreCase)
            && !hote.Equals(adressePublique.Host, StringComparison.OrdinalIgnoreCase)
            && !ctx.Request.Path.StartsWithSegments("/sante"))
        {
            ctx.Response.Redirect($"{adressePublique.GetLeftPart(UriPartial.Authority)}{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}",
                permanent: true, preserveMethod: true);
            return;
        }
        await next();
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseEnTetesSecurite();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
// Sonde de santé pour Azure Container Apps (vérifie aussi l'accès à la base)
app.MapGet("/sante", async (IDbContextFactory<ParcDbContext> f) =>
{
    await using var db = await f.CreateDbContextAsync();
    return await db.Database.CanConnectAsync() ? Results.Ok("ok") : Results.StatusCode(503);
}).AllowAnonymous();
if (entraConfigure) app.MapControllers();
if (modeDemo) app.MapConnexionDemo();
app.MapPointsDAcces();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
