using System.Security.Claims;
using Microsoft.Extensions.Configuration;
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
    public void Roles_configures_reconnus_sur_n_importe_quelle_adresse()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Demandes:Valideurs:0"] = "thomas.verchere@groupe-vitalrest.com" })
            .Build();
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", "thomas.verchere@vitalrest.com"), new Claim("preferred_username", "thomas.verchere@groupe-vitalrest.com")], "test"));
        var t = new AjoutRolesConfigures(config).TransformAsync(p).Result;
        Assert.True(Utilisateur.Depuis(t).EstValideur);
        Assert.False(Utilisateur.Depuis(t).EstGestionnaire);
    }
}
