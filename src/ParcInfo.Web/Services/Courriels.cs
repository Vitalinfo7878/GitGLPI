using System.Globalization;
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

    // --- Demandes informatiques ---

    /// <summary>
    /// Nouvelle demande : les valideurs reçoivent les boutons « Valider » / « Refuser » ;
    /// les autres destinataires (service informatique) une simple information, avec le lien vers la demande.
    /// </summary>
    public async Task NouvelleDemandeAsync(Demande d, IEnumerable<string> destinataires, IEnumerable<string> valideurs, CancellationToken ct = default)
    {
        var lien = Url($"demandes/{d.IdPublic}");
        var tous = destinataires.Concat(valideurs).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var aValider = tous.Where(x => valideurs.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
        var sujet = $"Nouvelle demande informatique n° {d.Numero} – {d.Objets} – {d.DemandeurNom}";
        var erreurs = new List<string>();

        var information = Gabarit($"""
            <p style="margin:0 0 12px">Une nouvelle demande informatique (<b>n° {d.Numero}</b>) a été reçue. Elle est en attente de validation.</p>
            {Recapitulatif(d)}
            <p style="margin:20px 0 8px">{Bouton(lien, "Ouvrir la demande", "#233d8e")}</p>
            <p style="margin:0;color:#5b6478;font-size:12px">Pour échanger avec le valideur, utilisez « Échanges internes » sur la demande (non visibles du demandeur).</p>
            """);
        try { await EnvoyerATousAsync(tous.Except(aValider, StringComparer.OrdinalIgnoreCase), sujet, information, ct); }
        catch (InvalidOperationException ex) { erreurs.Add(ex.Message); }

        var corps = Gabarit($"""
            <p style="margin:0 0 12px">Une nouvelle demande informatique (<b>n° {d.Numero}</b>) attend votre validation.</p>
            {Recapitulatif(d)}
            <p style="margin:20px 0 8px">
              {Bouton($"{lien}?choix=valider", "✔ Valider", "#2e7d32")}
              &nbsp;
              {Bouton($"{lien}?choix=refuser", "✖ Refuser", "#c62828")}
            </p>
            <p style="margin:0;color:#5b6478;font-size:12px">Le bouton ouvre la demande dans l'application (connexion avec votre compte Microsoft) pour confirmer votre décision.
            Pour poser une question au service informatique avant de décider, utilisez « Échanges internes » sur la demande.</p>
            """);
        try { await EnvoyerATousAsync(aValider, sujet, corps, ct); }
        catch (InvalidOperationException ex) { erreurs.Add(ex.Message); }
        if (erreurs.Count > 0) throw new InvalidOperationException(string.Join(" ", erreurs));
    }

    /// <summary>Message interne sur une demande, envoyé au valideur et au service informatique (jamais au demandeur).</summary>
    public async Task MessageDemandeAsync(Demande d, MessageDemande m, IEnumerable<string> destinataires, CancellationToken ct = default)
    {
        var corps = Gabarit($"""
            <p style="margin:0 0 12px"><b>{E(m.Auteur)}</b> a écrit au sujet de la demande <b>n° {d.Numero}</b> de {E(d.DemandeurNom)} ({E(d.Objets)}) :</p>
            <blockquote style="margin:0 0 16px;padding:8px 14px;border-left:4px solid #233d8e;background:#f3f5fa;white-space:pre-line">{E(m.Texte)}</blockquote>
            <p style="margin:0 0 8px">{Bouton(Url($"demandes/{d.IdPublic}#echanges"), "Répondre", "#233d8e")}</p>
            <p style="margin:0;color:#5b6478;font-size:12px">Échange interne : il n'est pas visible du demandeur. Répondez depuis la demande (une réponse à ce mail n'est pas enregistrée dans l'application).</p>
            """);
        await EnvoyerATousAsync(destinataires, $"Demande n° {d.Numero} – message de {m.Auteur}", corps, ct);
    }

    public async Task AccuseReceptionDemandeAsync(Demande d, CancellationToken ct = default)
    {
        var corps = Gabarit($"""
            <p style="margin:0 0 12px">Bonjour {E(d.DemandeurNom)},</p>
            <p style="margin:0 0 12px">Votre demande informatique a bien été enregistrée sous le <b>n° {d.Numero}</b>. Elle va être examinée pour validation ;
            vous serez informé(e) de la décision.</p>
            {Recapitulatif(d)}
            <p style="margin:20px 0 0">{Bouton(Url($"demandes/{d.IdPublic}"), "Suivre ma demande", "#233d8e")}</p>
            """);
        await EnvoyerEtTracerAsync(d.DemandeurEmail, $"Votre demande informatique n° {d.Numero} a bien été reçue", corps, ct);
    }

    /// <summary>
    /// Décision : aux autres destinataires des demandes (service informatique), qui informent eux-mêmes le demandeur.
    /// Le demandeur ne reçoit pas de mail.
    /// </summary>
    public async Task DecisionDemandeAsync(Demande d, IEnumerable<string> autresDestinataires, CancellationToken ct = default)
    {
        var validee = d.StatutValidation == StatutValidation.Validee;
        var info = Gabarit($"""
            <p style="margin:0 0 12px">La demande <b>n° {d.Numero}</b> de {E(d.DemandeurNom)} a été
            <b style="color:{(validee ? "#2e7d32" : "#c62828")}">{(validee ? "validée" : "refusée")}</b> par {E(d.DecisionPar)} le {d.DecisionLe?.ToLocalTime():dd/MM/yyyy à HH:mm}.</p>
            {(string.IsNullOrWhiteSpace(d.CommentaireDecision) ? "" : $"""<p style="margin:0 0 12px"><b>Commentaire</b> (non transmis au demandeur) : <span style="white-space:pre-line">{E(d.CommentaireDecision)}</span></p>""")}
            <p style="margin:0 0 12px">Le demandeur n'est pas prévenu automatiquement.</p>
            {Recapitulatif(d)}
            <p style="margin:16px 0 0">{Bouton(Url($"demandes/{d.IdPublic}"), "Ouvrir la demande", "#233d8e")}</p>
            """);
        await EnvoyerATousAsync(autresDestinataires, $"Demande n° {d.Numero} {(validee ? "validée" : "refusée")} par {d.DecisionPar}", info, ct);
    }

    /// <summary>Relance des valideurs : demandes ni validées ni refusées depuis plus d'une semaine, avec les boutons de décision.</summary>
    public async Task RelanceDemandesAsync(IReadOnlyList<Demande> demandes, IEnumerable<string> valideurs, DateTime maintenant, CancellationToken ct = default)
    {
        var blocs = string.Concat(demandes.Select(d =>
        {
            var lien = Url($"demandes/{d.IdPublic}");
            var jours = (int)(maintenant - d.CreeLe).TotalDays;
            return $"""
                <div style="margin:0 0 14px;padding:10px 14px;border:1px solid #dde2ee;border-radius:4px">
                  <p style="margin:0 0 4px"><b>N° {d.Numero}</b> – {E(d.Objets)} – {E(d.DemandeurNom)}</p>
                  <p style="margin:0 0 8px;color:#5b6478;font-size:13px">Reçue le {d.CreeLe.ToLocalTime():dd/MM/yyyy}, en attente depuis {jours} jours. {E(d.Raison.Length > 200 ? d.Raison[..200] + "…" : d.Raison)}</p>
                  {Bouton($"{lien}?choix=valider", "✔ Valider", "#2e7d32")} &nbsp; {Bouton($"{lien}?choix=refuser", "✖ Refuser", "#c62828")}
                </div>
                """;
        }));
        var corps = Gabarit($"""
            <p style="margin:0 0 12px">{(demandes.Count == 1 ? "Une demande informatique attend" : $"{demandes.Count} demandes informatiques attendent")} votre décision depuis plus d'une semaine :</p>
            {blocs}
            <p style="margin:0;color:#5b6478;font-size:12px">Sans décision, une nouvelle relance sera envoyée dans une semaine.</p>
            """);
        await EnvoyerATousAsync(valideurs, demandes.Count == 1
            ? $"Relance : demande informatique n° {demandes[0].Numero} en attente de validation"
            : $"Relance : {demandes.Count} demandes informatiques en attente de validation", corps, ct);
    }

    /// <summary>Bouton « Envoyer » : le matériel est parti, réception prévue sous 2 à 3 jours ouvrés.</summary>
    public async Task MaterielEnvoyeAsync(Demande d, DateOnly envoyeLe, CancellationToken ct = default)
    {
        var (du, au) = (JoursOuvres.Ajouter(envoyeLe, 2), JoursOuvres.Ajouter(envoyeLe, 3));
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var corps = Gabarit($"""
            <p style="margin:0 0 12px">Bonjour {E(d.DemandeurNom)},</p>
            <p style="margin:0 0 12px">Le matériel de votre demande informatique <b>n° {d.Numero}</b> ({E(d.Objets)}) a été <b>envoyé</b>.</p>
            <p style="margin:0 0 12px">Vous devriez le recevoir d'ici <b>2 à 3 jours ouvrés</b>, soit entre le {du.ToString("dddd d MMMM", fr)} et le {au.ToString("dddd d MMMM", fr)},
            à l'adresse indiquée : {E(d.AdresseLivraison)}{(string.IsNullOrWhiteSpace(d.DestinataireLivraison) ? "" : $" (à l'attention de {E(d.DestinataireLivraison)})")}.</p>
            {(d.EstRemplacement ? $"""<p style="margin:0 0 12px"><b>Merci de nous renvoyer l'ancien matériel</b>{(d.MaterielRemplace is null ? "" : $" ({E(d.MaterielRemplace)})")} dès que vous aurez reçu le nouveau. Pour organiser son retour, contactez le service informatique : informatique@vitalrest.com.</p>""" : "")}
            <p style="margin:0">Le service informatique</p>
            """);
        await EnvoyerEtTracerAsync(d.DemandeurEmail, $"Demande informatique n° {d.Numero} : votre matériel a été envoyé", corps, ct);
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

    // --- Inventaire ---

    /// <summary>Matériel entré au parc (réception d'achat ou ajout hors achat), pour les abonnés de la notification.</summary>
    public async Task MaterielRecuAsync(IEnumerable<string> destinataires, string sujet, string introduction, IReadOnlyList<Materiel> materiels,
        string? fournisseur, string? documentReference, Fiche? fiche, string? auteur, CancellationToken ct = default)
    {
        string Cellule(string? v) => $"""<td style="padding:4px 10px;border-bottom:1px solid #e3e7ef">{E(v)}</td>""";
        var lignes = string.Join("", materiels.Select(m =>
            $"<tr>{Cellule(m.NumInterne)}{Cellule(m.Type.Libelle())}{Cellule(m.Modele)}{Cellule(m.NumSerie)}{Cellule(m.NumFacture)}</tr>"));
        string Info(string libelle, string? valeur) => string.IsNullOrWhiteSpace(valeur) ? "" :
            $"""<tr><td style="padding:2px 12px 2px 0;color:#5b6478">{E(libelle)}</td><td style="padding:2px 0">{E(valeur)}</td></tr>""";
        var corps = Gabarit($"""
            <p style="margin:0 0 12px">{introduction}</p>
            <table style="border-collapse:collapse;font-size:14px;margin:0 0 12px">
            {Info("Fournisseur", fournisseur)}
            {Info("Référence", documentReference)}
            {Info("Fiche", fiche?.Code)}
            {Info("Enregistré par", auteur)}
            </table>
            <table style="border-collapse:collapse;font-size:13px;margin:0 0 16px">
            <tr style="background:#f3f5fa;text-align:left"><th style="padding:6px 10px">N° interne</th><th style="padding:6px 10px">Type</th><th style="padding:6px 10px">Modèle</th><th style="padding:6px 10px">N° de série</th><th style="padding:6px 10px">N° de facture</th></tr>
            {lignes}
            </table>
            <p style="margin:0 0 8px">{Bouton(Url("inventaire?statut=EnStock"), "Voir le stock", "#233d8e")}
            {(fiche is null ? "" : $"&nbsp; {Bouton(Url($"fiches/{fiche.IdPublic}/pdf"), $"Fiche {fiche.Code}", "#5b6478")}")}</p>
            <p style="margin:0;color:#5b6478;font-size:12px">Mail d'information automatique. Pour ne plus le recevoir, demandez au service informatique (informatique@vitalrest.com).</p>
            """);
        await EnvoyerATousAsync(destinataires, sujet, corps, ct);
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
            // L'échec d'envoi ne doit pas annuler l'opération en cours : il est tracé dans les courriels envoyés.
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
