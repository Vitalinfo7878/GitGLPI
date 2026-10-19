using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor.Services;
using ParcInfo.Web.Components;
using ParcInfo.Web.Data;
using ParcInfo.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// --- Base de données : SQLite en local, Azure SQL en production ---
var fournisseur = config["BaseDeDonnees:Fournisseur"] ?? "Sqlite";
var chaine = config.GetConnectionString("Parc") ?? "Data Source=parc.db";
builder.Services.AddDbContextFactory<ParcDbContext>(o =>
{
    if (fournisseur.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        o.UseSqlServer(chaine, s => s.EnableRetryOnFailure());
    else
        o.UseSqlite(chaine);
});

// --- Authentification : Entra ID (comptes Microsoft 365) ou mode démo local ---
var entraConfigure = !string.IsNullOrWhiteSpace(config["AzureAd:ClientId"]);
var modeDemo = !entraConfigure && (builder.Environment.IsDevelopment() || config.GetValue<bool>("Authentification:ModeDemo"));
if (entraConfigure)
{
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(config.GetSection("AzureAd"));
    builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme,
        o => o.TokenValidationParameters.RoleClaimType = "roles");
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
builder.Services.AddTransient<IClaimsTransformation, AjoutRoleGestionnaire>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roles.PolitiqueGestion, p => p.RequireRole(Roles.Gestionnaire))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();

// --- Messagerie ---
var messagerie = config.GetSection("Messagerie").Get<OptionsMessagerie>() ?? new OptionsMessagerie();
builder.Services.AddSingleton(messagerie);
if (messagerie.Mode.Equals("Graph", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IEnvoiCourriel, EnvoiCourrielGraph>();
else
    builder.Services.AddSingleton<IEnvoiCourriel, EnvoiCourrielSimule>();

// --- Services métier ---
builder.Services.AddSingleton<GenerateurFiche>();
builder.Services.AddScoped<ServiceNotifications>();
builder.Services.AddScoped<ServiceMouvements>();
builder.Services.AddScoped<UtilisateurCourant>();
builder.Services.AddMudServices();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024);

var app = builder.Build();

await InitialisationBase.AppliquerAsync(app.Services, fournisseur);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
if (entraConfigure) app.MapControllers();
if (modeDemo) app.MapConnexionDemo();
app.MapPointsDAcces();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
