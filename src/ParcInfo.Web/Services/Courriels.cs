using System.Net;
using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

public sealed class OptionsMessagerie
{
    /// <summary>« Simulation » (les courriels sont seulement enregistrés) ou « Graph » (envoi réel via Microsoft Graph).</summary>
    public string Mode { get; set; } = "Simulation";
    /// <summary>Boîte aux lettres d'envoi (ex. informatique@vitalrest.com).</summary>
    public string? Expediteur { get; set; }
    /// <summary>Boîte de l'équipe informatique, prévenue quand une réception est confirmée.</summary>
    public string? BoiteInformatique { get; set; }
    /// <summary>Adresse publique de l'application, utilisée dans les liens des courriels.</summary>
    public string UrlApplication { get; set; } = "https://localhost:5001";
}

public interface IEnvoiCourriel
{
    Task EnvoyerAsync(string destinataire, string sujet, string corpsHtml, CancellationToken ct = default);
}

/// <summary>Envoi via Microsoft Graph (permission d'application Mail.Send, à restreindre à la boîte d'envoi).</summary>
public sealed class EnvoiCourrielGraph(IConfiguration config, OptionsMessagerie options) : IEnvoiCourriel
{
    private readonly GraphServiceClient _graph = new(Identifiant(config), ["https://graph.microsoft.com/.default"]);

    private static TokenCredential Identifiant(IConfiguration config)
    {
        var az = config.GetSection("AzureAd");
        return string.IsNullOrEmpty(az["ClientSecret"])
            ? new DefaultAzureCredential() // identité managée de l'App Service
            : new ClientSecretCredential(az["TenantId"], az["ClientId"], az["ClientSecret"]);
    }

    public async Task EnvoyerAsync(string destinataire, string sujet, string corpsHtml, CancellationToken ct = default)
    {
        var expediteur = options.Expediteur ?? throw new InvalidOperationException("Messagerie:Expediteur n'est pas configuré.");
        await _graph.Users[expediteur].SendMail.PostAsync(new SendMailPostRequestBody
        {
            Message = new Message
            {
                Subject = sujet,
                Body = new ItemBody { ContentType = BodyType.Html, Content = corpsHtml },
                ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = destinataire } }],
            },
            SaveToSentItems = true,
        }, cancellationToken: ct);
    }
}

/// <summary>Aucun envoi réel : le courriel est seulement enregistré (consultable dans « Courriels »).</summary>
public sealed class EnvoiCourrielSimule(ILogger<EnvoiCourrielSimule> log) : IEnvoiCourriel
{
    public Task EnvoyerAsync(string destinataire, string sujet, string corpsHtml, CancellationToken ct = default)
    {
        log.LogInformation("Courriel simulé à {Destinataire} : {Sujet}", destinataire, sujet);
        return Task.CompletedTask;
    }
}

/// <summary>Rédige les courriels de l'application, les envoie et les trace.</summary>
public sealed class ServiceNotifications(
    IEnvoiCourriel envoi, IDbContextFactory<ParcDbContext> dbFactory, OptionsMessagerie options, ILogger<ServiceNotifications> log)
{
    public string LienReception(Fiche f) => $"{options.UrlApplication.TrimEnd('/')}/reception/{f.IdPublic}";

    public async Task DemanderConfirmationReceptionAsync(Fiche fiche, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fiche.DestinataireEmail)) return;
        var lignes = string.Join("", fiche.Mouvements.Select(m =>
            $"<li><b>{E(m.Materiel.NumInterne)}</b> – {E(m.Materiel.Type.Libelle())} {E(m.Materiel.Modele)}{(m.Materiel.NumSerie is null ? "" : $" (n° série {E(m.Materiel.NumSerie)})")}</li>"));
        var corps = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2a44;line-height:1.5">
            <p style="margin:0 0 12px">Bonjour {E(fiche.DestinataireNom)},</p>
            <p style="margin:0 0 8px">Le service informatique vous a envoyé le matériel suivant (fiche <b>{E(fiche.Code)}</b>) :</p>
            <ul style="margin:0 0 12px;padding-left:20px">{lignes}</ul>
            <p style="margin:0 0 16px">Dès que vous l'avez reçu, merci de <b>confirmer la réception</b> en cliquant sur le bouton ci-dessous
            (connexion avec votre compte Microsoft Vitalrest). Le mot de passe de session du PC s'affichera après confirmation.</p>
            <p style="margin:0 0 20px"><a href="{LienReception(fiche)}" style="display:inline-block;background:#233d8e;color:#ffffff;padding:10px 18px;border-radius:4px;text-decoration:none;font-weight:600">Confirmer la réception</a></p>
            <p style="margin:0">Merci,<br/>Le service informatique</p>
            </div>
            """;
        await EnvoyerEtTracerAsync(fiche.DestinataireEmail, $"Matériel informatique envoyé – confirmez la réception ({fiche.Code})", corps, ct);
    }

    public async Task SignalerReceptionConfirmeeAsync(Fiche fiche, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.BoiteInformatique)) return;
        var corps = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2a44;line-height:1.5">
            <p style="margin:0 0 8px">La réception de la fiche <b>{E(fiche.Code)}</b> a été confirmée par {E(fiche.ReceptionConfirmeePar)}
            ({E(fiche.ReceptionConfirmeeParEmail)}) le {fiche.ReceptionConfirmeeLe?.ToLocalTime():dd/MM/yyyy à HH:mm}.</p>
            <p style="margin:0">Matériel : {E(string.Join(", ", fiche.Mouvements.Select(m => m.Materiel.NumInterne)))}</p>
            </div>
            """;
        await EnvoyerEtTracerAsync(options.BoiteInformatique, $"Réception confirmée – {fiche.Code}", corps, ct);
    }

    private async Task EnvoyerEtTracerAsync(string destinataire, string sujet, string corps, CancellationToken ct)
    {
        var trace = new Courriel
        {
            Destinataire = destinataire, Sujet = sujet, CorpsHtml = corps, EnvoyeLe = DateTime.UtcNow,
            Simule = envoi is EnvoiCourrielSimule,
        };
        try
        {
            await envoi.EnvoyerAsync(destinataire, sujet, corps, ct);
        }
        catch (Exception ex)
        {
            // L'échec d'envoi ne doit pas annuler le mouvement : il est tracé et la relance reste possible.
            log.LogError(ex, "Échec d'envoi du courriel à {Destinataire}", destinataire);
            trace.Erreur = ex.Message;
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Courriels.Add(trace);
        await db.SaveChangesAsync(ct);
        if (trace.Erreur is not null)
            throw new InvalidOperationException($"Le courriel à {destinataire} n'a pas pu être envoyé : {trace.Erreur}");
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
}
