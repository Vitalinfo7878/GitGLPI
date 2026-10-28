using System.ComponentModel.DataAnnotations;

namespace ParcInfo.Web.Domain;

public enum StatutValidation
{
    [Display(Name = "En attente de validation")] EnAttente,
    [Display(Name = "Validée")] Validee,
    [Display(Name = "Refusée")] Refusee,
}

public enum StatutRealisation
{
    [Display(Name = "À faire")] AFaire,
    [Display(Name = "En cours")] EnCours,
    [Display(Name = "En attente de matériel")] AttenteMateriel,
    [Display(Name = "En cours d'envoi")] EnCoursEnvoi,
    [Display(Name = "Réalisée")] Realisee,
    [Display(Name = "Annulée")] Annulee,
}

public enum Priorite
{
    [Display(Name = "Basse")] Basse,
    [Display(Name = "Normale")] Normale,
    [Display(Name = "Haute")] Haute,
}

/// <summary>
/// Demande informatique (matériel ou compte), reprise du formulaire Microsoft Forms
/// « Nouveau formulaire de Demandes Informatiques ».
/// </summary>
public class Demande
{
    /// <summary>Choix de la question « Quel est l'objet de votre demande ? » (plusieurs réponses possibles + « Autre »).</summary>
    public static readonly string[] ChoixObjet =
        ["PC fixe (site)", "PC fixe (Siège / DR)", "PC portable", "Ecran", "Imprimante", "Adresse email", "Compte Vital Cloud"];

    /// <summary>Choix de la question « Votre demande concerne : » (une seule réponse + « Autre »).</summary>
    public static readonly string[] ChoixConcerne = [Remplacement, "Une ouverture de site", "Une ouverture de poste"];

    /// <summary>Remplacement : l'ancien matériel est à rapatrier, suivi par un ticket créé automatiquement.</summary>
    public const string Remplacement = "Un remplacement";

    public int Id { get; set; }
    /// <summary>Identifiant non devinable utilisé dans les liens des courriels.</summary>
    public Guid IdPublic { get; set; } = Guid.NewGuid();
    /// <summary>Numéro de demande (suite de la numérotation de la feuille ETAT DEMANDES).</summary>
    public int Numero { get; set; }
    public DateTime CreeLe { get; set; }
    /// <summary>Compte Microsoft avec lequel la demande a été saisie.</summary>
    public string SaisiePar { get; set; } = "";

    // Réponses au formulaire
    public string DemandeurNom { get; set; } = "";
    /// <summary>Objets demandés, séparés par « ; » (ex. « PC portable ; Ecran »).</summary>
    public string Objets { get; set; } = "";
    public string Concerne { get; set; } = "";
    public string? AdresseMailACreer { get; set; }
    public string Raison { get; set; } = "";
    public string AdresseLivraison { get; set; } = "";
    public string? DestinataireLivraison { get; set; }
    public string DemandeurEmail { get; set; } = "";
    public string DemandeurTelephone { get; set; } = "";
    public DateOnly? Echeance { get; set; }
    /// <summary>
    /// Remplacement : ancien matériel trouvé dans l'inventaire (matériel du demandeur du type demandé),
    /// ex. « PC0450 – PC portable Dell Latitude ». Vide si rien n'a été trouvé.
    /// </summary>
    public string? MaterielRemplace { get; set; }
    /// <summary>Premier ancien matériel trouvé (chacun a son ticket de rapatriement).</summary>
    public int? MaterielRemplaceId { get; set; }

    public bool EstRemplacement => Concerne == Remplacement;

    // Validation
    public StatutValidation StatutValidation { get; set; }
    public DateTime? DecisionLe { get; set; }
    public string? DecisionPar { get; set; }
    public string? DecisionParEmail { get; set; }
    /// <summary>Commentaire facultatif du valideur sur sa décision : réservé au service informatique et aux valideurs, jamais montré au demandeur.</summary>
    public string? CommentaireDecision { get; set; }
    /// <summary>Dernière relance du valideur pour une demande restée en attente.</summary>
    public DateTime? DerniereRelanceLe { get; set; }

    // Traitement par le service informatique
    public Priorite Priorite { get; set; } = Priorite.Normale;
    public StatutRealisation StatutRealisation { get; set; }
    public string? CommentaireInformatique { get; set; }
    public DateTime? RealiseeLe { get; set; }
    /// <summary>Le demandeur a été prévenu que le matériel est envoyé (bouton « Envoyer »).</summary>
    public DateTime? EnvoiNotifieLe { get; set; }
    public DateTime ModifieLe { get; set; }

    /// <summary>Échanges internes entre le valideur et le service informatique (jamais visibles du demandeur).</summary>
    public List<MessageDemande> Messages { get; set; } = [];

    public IEnumerable<string> ListeObjets => Objets.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Statut lisible combinant validation et traitement.</summary>
    public string StatutLibelle => StatutValidation == StatutValidation.Validee
        ? $"Validée – {StatutRealisation.Libelle().ToLowerInvariant()}"
        : StatutValidation.Libelle();
}

/// <summary>Message interne sur une demande (ex. « Validez-vous ? ») : visible du valideur et du service informatique uniquement.</summary>
public class MessageDemande
{
    public int Id { get; set; }
    public int DemandeId { get; set; }
    public Demande Demande { get; set; } = null!;
    public DateTime Le { get; set; }
    public string Auteur { get; set; } = "";
    public string AuteurEmail { get; set; } = "";
    public string Texte { get; set; } = "";
}
