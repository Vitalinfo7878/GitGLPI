using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ParcInfo.Web.Services;

namespace ParcInfo.Tests;

public class UtilisateurTests
{
    [Fact]
    public void Identifiant_de_connexion_et_adresse_mail_differents()
    {
        // Compte dont l'identifiant Entra (UPN) est sur un domaine et la boîte mail sur l'autre
        var p = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("name", "Thomas Verchère"),
            new Claim("preferred_username", "Thomas.Verchere@groupe-vitalrest.com"),
            new Claim("email", "thomas.verchere@vitalrest.com"),
        ], "test"));
        var u = Utilisateur.Depuis(p);
        Assert.Equal("thomas.verchere@vitalrest.com", u.Email);
        Assert.True(u.AEmail("thomas.verchere@groupe-vitalrest.com"));
        Assert.True(u.AEmail("THOMAS.VERCHERE@vitalrest.com"));
        Assert.False(u.AEmail("autre@vitalrest.com"));
    }

    [Fact]
    public async Task Roles_configures_reconnus_sur_n_importe_quelle_adresse()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Demandes:Valideurs:0"] = "thomas.verchere@groupe-vitalrest.com" })
            .Build();
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", "thomas.verchere@vitalrest.com"), new Claim("preferred_username", "thomas.verchere@groupe-vitalrest.com")], "test"));
        var t = await new AjoutRolesConfigures(config).TransformAsync(p);
        Assert.True(Utilisateur.Depuis(t).EstValideur);
        Assert.False(Utilisateur.Depuis(t).EstGestionnaire);
    }

    [Theory]
    [InlineData("roles")]
    [InlineData(ClaimTypes.Role)]
    public async Task Role_Entra_reconnu_quel_que_soit_le_nom_de_revendication(string type)
    {
        // Identité construite comme à la connexion Entra ID : rôles attendus sous « roles »
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", "informatique@vitalrest.com"), new Claim(type, "Gestionnaire")],
            "test", "name", "roles"));
        var t = await new AjoutRolesConfigures(new ConfigurationBuilder().Build()).TransformAsync(p);
        Assert.True(Utilisateur.Depuis(t).EstGestionnaire);
    }

    [Fact]
    public async Task Compte_de_site_reconnu_par_sa_boite_mail()
    {
        using var fabrique = new BaseDeTest();
        await using (var db = fabrique.CreateDbContext())
        {
            db.Sites.Add(new ParcInfo.Web.Domain.Site { Code = "087", Email = "site087@vitalrest.com" });
            await db.SaveChangesAsync();
        }
        var transformation = new AjoutRolesConfigures(new ConfigurationBuilder().Build(), fabrique);
        var site = await transformation.TransformAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "Site087@vitalrest.com")], "test")));
        var salarie = await transformation.TransformAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "camille@vitalrest.com")], "test")));
        Assert.True(Utilisateur.Depuis(site).EstSite);
        Assert.False(Utilisateur.Depuis(salarie).EstSite);
    }

    [Theory]
    [InlineData(Roles.PolitiqueSalarie)]
    [InlineData(Roles.PolitiqueGestion)]
    [InlineData(Roles.PolitiqueValidation)]
    [InlineData(Roles.PolitiqueTickets)]
    public async Task Politiques_refusees_sans_connexion(string politique)
    {
        var services = new ServiceCollection().AddLogging();
        Roles.DefinirPolitiques(services.AddAuthorizationBuilder());
        var autorisation = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();

        var anonyme = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False((await autorisation.AuthorizeAsync(anonyme, politique)).Succeeded);

        var salarie = new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "Camille Test")], "test"));
        Assert.Equal(politique == Roles.PolitiqueSalarie, (await autorisation.AuthorizeAsync(salarie, politique)).Succeeded);
        var site = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.Site)], "test"));
        Assert.False((await autorisation.AuthorizeAsync(site, Roles.PolitiqueSalarie)).Succeeded);
    }
}
