using System.Net;
using System.Security.Claims;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

public static class PointsDAcces
{
    public static void MapPointsDAcces(this WebApplication app)
    {
        // Téléchargement du PDF d'une fiche : informatique ou destinataire de la fiche
        app.MapGet("/fiches/{idPublic:guid}/pdf", async (Guid idPublic, ClaimsPrincipal user, IDbContextFactory<ParcDbContext> f) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var fiche = await db.Fiches.AsNoTracking()
                .Where(x => x.IdPublic == idPublic)
                .Select(x => new { x.Code, x.Pdf, x.DestinataireEmail })
                .FirstOrDefaultAsync();
            if (fiche?.Pdf is null) return Results.NotFound();
            var u = Utilisateur.Depuis(user);
            if (!u.EstGestionnaire && !u.AEmail(fiche.DestinataireEmail)) return Results.Forbid();
            return Results.File(fiche.Pdf, "application/pdf", $"{fiche.Code}.pdf");
        });

        app.MapGet("/export/inventaire.xlsx", async (IDbContextFactory<ParcDbContext> f) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var materiels = await db.Materiels.AsNoTracking()
                .Include(m => m.Site).ThenInclude(s => s!.Region).Include(m => m.Personne).Include(m => m.Region)
                .OrderBy(m => m.NumInterne).ToListAsync();
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Inventaire");
            string[] entetes = ["N° interne", "Type", "Statut", "Parc", "Site / personne", "Région / service", "Modèle", "N° série",
                "Configuration", "Année", "Mise en place", "Profil Microsoft", "Licence", "Office en dur", "Note /5", "Admin local",
                "Windows", "Édition", "À changer", "Labtech", "N° immo", "N° facture", "Clé Office", "Commentaire"];
            for (var i = 0; i < entetes.Length; i++) ws.Cell(1, i + 1).Value = entetes[i];
            var r = 2;
            foreach (var m in materiels)
            {
                object?[] v = [m.NumInterne, m.Type.Libelle(), m.Statut.Libelle(), m.TypeDetenteur.Libelle(), m.DetenteurLibelle,
                    m.Region?.Nom ?? m.Site?.Region?.Nom, m.Modele, m.NumSerie, m.Configuration, m.AnneeAcquisition,
                    m.DateMiseEnPlace?.ToDateTime(TimeOnly.MinValue), m.ProfilMicrosoft, m.LicenceMicrosoft, OuiNon(m.OfficeEnDur), m.Note,
                    OuiNon(m.ConnexionAdmin), m.VersionWindows, m.EditionWindows, m.AChanger ? "oui" : "non", OuiNon(m.Labtech),
                    m.NumImmo, m.NumFacture, m.CleOffice, m.Commentaire];
                for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = XLCellValue.FromObject(v[i]);
                r++;
            }
            return Classeur(wb, ws, "inventaire");
        }).RequireAuthorization(Roles.PolitiqueGestion);

        app.MapGet("/export/mouvements.xlsx", async (IDbContextFactory<ParcDbContext> f) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var mvts = await db.Mouvements.AsNoTracking()
                .Include(m => m.Materiel).Include(m => m.DestinationSite).Include(m => m.DestinationPersonne).Include(m => m.Fiche)
                .OrderBy(m => m.Numero).ToListAsync();
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Mouvements");
            string[] entetes = ["N° mouvement", "N° interne", "Appareil", "Modèle", "N° série", "Type de mouvement", "Date",
                "Expéditeur", "Destination", "Région", "Document de référence", "Date de réception", "Fiche", "Commentaire", "Saisi par"];
            for (var i = 0; i < entetes.Length; i++) ws.Cell(1, i + 1).Value = entetes[i];
            var r = 2;
            foreach (var m in mvts)
            {
                object?[] v = [m.Numero, m.Materiel.NumInterne, m.Materiel.Type.Libelle(), m.Materiel.Modele, m.Materiel.NumSerie,
                    m.Type.Libelle(), m.Date?.ToDateTime(TimeOnly.MinValue), m.Expediteur, m.DestinationLibelle, m.Region,
                    m.DocumentReference, m.DateReception?.ToDateTime(TimeOnly.MinValue), m.Fiche?.Code, m.Commentaire, m.CreePar];
                for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = XLCellValue.FromObject(v[i]);
                r++;
            }
            return Classeur(wb, ws, "mouvements");
        }).RequireAuthorization(Roles.PolitiqueGestion);
    }

    private static string? OuiNon(bool? b) => b switch { true => "oui", false => "non", null => null };

    private static IResult Classeur(XLWorkbook wb, IXLWorksheet ws, string nom)
    {
        var plage = ws.RangeUsed()!;
        plage.CreateTable();
        ws.Columns().AdjustToContents(1, 200, 8, 50);
        foreach (var c in ws.ColumnsUsed())
            if (c.FirstCellUsed()?.Value.ToString().StartsWith("Date") == true || c.FirstCellUsed()?.Value.ToString() == "Mise en place")
                c.Style.DateFormat.Format = "dd/MM/yyyy";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"{nom}-{DateTime.Today:yyyy-MM-dd}.xlsx");
    }

    /// <summary>
    /// Connexion de démonstration (sans Entra ID) : permet de tester l'application en tant que service informatique
    /// ou en tant que n'importe quel destinataire (pour la confirmation de réception).
    /// </summary>
    public static void MapConnexionDemo(this WebApplication app)
    {
        app.MapGet("/demo/connexion", (HttpContext ctx, IAntiforgery af, string? ReturnUrl) =>
        {
            var jeton = af.GetAndStoreTokens(ctx);
            var retour = WebUtility.HtmlEncode(ReturnUrl ?? "/");
            var html = $$"""
                <!doctype html><html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>Connexion (démo)</title>
                <style>body{font-family:system-ui,sans-serif;background:#f4f6fb;display:flex;justify-content:center;padding:40px 16px;margin:0}
                main{background:#fff;max-width:440px;width:100%;padding:28px;border-radius:10px;box-shadow:0 2px 12px #0001}
                h1{font-size:20px;color:#233d8e;margin:0 0 6px}p{color:#555;font-size:14px}label{display:block;margin:14px 0 4px;font-size:14px}
                input,select{width:100%;padding:9px;border:1px solid #ccd;border-radius:6px;box-sizing:border-box;font-size:14px}
                button{margin-top:20px;width:100%;padding:11px;background:#233d8e;color:#fff;border:0;border-radius:6px;font-size:15px;cursor:pointer}</style></head>
                <body><main><img src="/img/logo-vitalrest.jpg" alt="Vitalrest" style="width:180px">
                <h1>Connexion de démonstration</h1>
                <p>Entra ID n'est pas encore configuré. Choisissez une identité de test.</p>
                <form method="post" action="/demo/connexion">
                <input type="hidden" name="{{jeton.FormFieldName}}" value="{{jeton.RequestToken}}">
                <input type="hidden" name="retour" value="{{retour}}">
                <label>Profil</label><select name="role"><option value="gestionnaire">Service informatique (gestion)</option><option value="valideur">Valideur des demandes</option><option value="utilisateur">Utilisateur (salarié ou site)</option></select>
                <label>Nom</label><input name="nom" value="Service Informatique (démo)" required>
                <label>Adresse mail</label><input name="email" type="email" value="informatique.demo@vitalrest.com" required>
                <button type="submit">Se connecter</button></form></main></body></html>
                """;
            return Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous();

        app.MapPost("/demo/connexion", async (HttpContext ctx, [FromForm] string nom, [FromForm] string email, [FromForm] string role, [FromForm] string? retour) =>
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, nom), new("name", nom), new("preferred_username", email.Trim().ToLowerInvariant()) };
            if (role == "gestionnaire") claims.Add(new Claim(ClaimTypes.Role, Roles.Gestionnaire));
            if (role == "valideur") claims.Add(new Claim(ClaimTypes.Role, Roles.Valideur));
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.LocalRedirect(string.IsNullOrEmpty(retour) || !retour.StartsWith('/') ? "/" : retour);
        }).AllowAnonymous();

        app.MapGet("/demo/deconnexion", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/demo/connexion");
        }).AllowAnonymous();
    }
}
