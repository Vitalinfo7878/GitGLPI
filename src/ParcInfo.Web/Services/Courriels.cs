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
            ? new DefaultAzureCredential() // identité managée (si aucun secret client n'est configuré)
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
    public string Url(string chemin) => $"{options.UrlApplication.TrimEnd('/')}/{chemin.TrimStart('/')}";

    public string LienReception(Fiche f) => Url($"reception/{f.IdPublic}");

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

    // --- Demandes informatiques ---

    /// <summary>Courriel aux valideurs : récapitulatif de la demande + boutons « Valider » / « Refuser ».</summary>
    public async Task NouvelleDemandeAsync(Demande d, IEnumerable<string> destinataires, CancellationToken ct = default)
    {
        var lien = Url($"demandes/{d.IdPublic}");
        var corps = Gabarit($"""
            <p style="margin:0 0 12px">Une nouvelle demande informatique (<b>n° {d.Numero}</b>) attend votre validation.</p>
            {Recapitulatif(d)}
            <p style="margin:20px 0 8px">
              {Bouton($"{lien}?choix=valider", "✔ Valider", "#2e7d32")}
              &nbsp;
              {Bouton($"{lien}?choix=refuser", "✖ Refuser", "#c62828")}
            </p>
            <p style="margin:0;color:#5b6478;font-size:12px">Le bouton ouvre la demande dans l'application (connexion avec votre compte Microsoft) pour confirmer votre décision.</p>
            """);
        await EnvoyerATousAsync(destinataires, $"Nouvelle demande informatique n° {d.Numero} – {d.Objets} – {d.DemandeurNom}", corps, ct);
    }

    public async Task AccuseReceptionDemandeAsync(Demande d, CancellationToken ct = default)
    {
        var corps = Gabarit($"""
            <p style="margin:0 0 12px">Bonjour {E(d.DemandeurNom)},</p>
            <p style="margin:0 0 12px">Votre demande informatique a bien été enregistrée sous le <b>n° {d.Numero}</b>. Elle va être examinée pour validation ;
            vous serez informé(e) par mail de la décision.</p>
            {Recapitulatif(d)}
            <p style="margin:20px 0 0">{Bouton(Url($"demandes/{d.IdPublic}"), "Suivre ma demande", "#233d8e")}</p>
            """);
        await EnvoyerEtTracerAsync(d.DemandeurEmail, $"Votre demande informatique n° {d.Numero} a bien été reçue", corps, ct);
    }

    /// <summary>Décision : au demandeur, et aux autres destinataires pour qu'ils sachent que la demande est traitée.</summary>
    public async Task DecisionDemandeAsync(Demande d, IEnumerable<string> autresDestinataires, CancellationToken ct = default)
    {
        var validee = d.StatutValidation == StatutValidation.Validee;
        var motif = string.IsNullOrWhiteSpace(d.CommentaireDecision) ? "" : $"""<p style="margin:0 0 12px"><b>{(validee ? "Commentaire" : "Motif")} :</b> {E(d.CommentaireDecision)}</p>""";
        var demandeur = Gabarit($"""
            <p style="margin:0 0 12px">Bonjour {E(d.DemandeurNom)},</p>
            <p style="margin:0 0 12px">Votre demande informatique <b>n° {d.Numero}</b> ({E(d.Objets)}) a été
            <b style="color:{(validee ? "#2e7d32" : "#c62828")}">{(validee ? "validée" : "refusée")}</b>.</p>
            {motif}
            <p style="margin:0 0 12px">{(validee ? "Le service informatique va la prendre en charge ; vous serez informé(e) de sa réalisation." : "Pour toute question, contactez le service informatique.")}</p>
            <p style="margin:16px 0 0">{Bouton(Url($"demandes/{d.IdPublic}"), "Voir ma demande", "#233d8e")}</p>
            """);
        await EnvoyerEtTracerAsync(d.DemandeurEmail, $"Demande informatique n° {d.Numero} {(validee ? "validée" : "refusée")}", demandeur, ct);

        var info = Gabarit($"""
            <p style="margin:0 0 12px">La demande <b>n° {d.Numero}</b> de {E(d.DemandeurNom)} a été
            <b>{(validee ? "validée" : "refusée")}</b> par {E(d.DecisionPar)} le {d.DecisionLe?.ToLocalTime():dd/MM/yyyy à HH:mm}.</p>
            {motif}
            {Recapitulatif(d)}
            <p style="margin:16px 0 0">{Bouton(Url($"demandes/{d.IdPublic}"), "Ouvrir la demande", "#233d8e")}</p>
            """);
        await EnvoyerATousAsync(autresDestinataires, $"Demande n° {d.Numero} {(validee ? "validée" : "refusée")} par {d.DecisionPar}", info, ct);
    }

    public async Task DemandeRealiseeAsync(Demande d, CancellationToken ct = default)
    {
        var corps = Gabarit($"""
            <p style="margin:0 0 12px">Bonjour {E(d.DemandeurNom)},</p>
            <p style="margin:0 0 12px">Votre demande informatique <b>n° {d.Numero}</b> ({E(d.Objets)}) a été <b>réalisée</b>.</p>
            {(string.IsNullOrWhiteSpace(d.CommentaireInformatique) ? "" : $"<p style=\"margin:0 0 12px\">{E(d.CommentaireInformatique)}</p>")}
            <p style="margin:0">Le service informatique</p>
            """);
        await EnvoyerEtTracerAsync(d.DemandeurEmail, $"Demande informatique n° {d.Numero} réalisée", corps, ct);
    }

    private static string Recapitulatif(Demande d)
    {
        string Ligne(string libelle, string? valeur) => string.IsNullOrWhiteSpace(valeur) ? "" :
            $"""<tr><td style="padding:4px 12px 4px 0;color:#5b6478;vertical-align:top;white-space:nowrap">{E(libelle)}</td><td style="padding:4px 0;white-space:pre-line">{E(valeur)}</td></tr>""";
        return $"""
            <table style="border-collapse:collapse;font-size:14px">
            {Ligne("Demandeur", d.DemandeurNom)}
            {Ligne("Objet", d.Objets)}
            {Ligne("Concerne", d.Concerne)}
            {Ligne("Adresse mail à créer", d.AdresseMailACreer)}
            {Ligne("Raison", d.Raison)}
            {Ligne("Adresse de livraison", d.AdresseLivraison)}
            {Ligne("Livraison à l'attention de", d.DestinataireLivraison)}
            {Ligne("Mail du demandeur", d.DemandeurEmail)}
            {Ligne("Téléphone", d.DemandeurTelephone)}
            {Ligne("Échéance souhaitée", d.Echeance?.ToString("dd/MM/yyyy"))}
            </table>
            """;
    }

    private static string Gabarit(string contenu) =>
        $"""<div style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2a44;line-height:1.5">{contenu}</div>""";

    private static string Bouton(string url, string texte, string couleur) =>
        $"""<a href="{E(url)}" style="display:inline-block;background:{couleur};color:#ffffff;padding:10px 20px;border-radius:4px;text-decoration:none;font-weight:600">{E(texte)}</a>""";

    /// <summary>Envoie à chaque destinataire ; un échec n'empêche pas les envois suivants.</summary>
    private async Task EnvoyerATousAsync(IEnumerable<string> destinataires, string sujet, string corps, CancellationToken ct)
    {
        var erreurs = new List<string>();
        foreach (var dest in destinataires.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { await EnvoyerEtTracerAsync(dest, sujet, corps, ct); }
            catch (InvalidOperationException ex) { erreurs.Add(ex.Message); }
        }
        if (erreurs.Count > 0) throw new InvalidOperationException(string.Join(" ", erreurs));
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
