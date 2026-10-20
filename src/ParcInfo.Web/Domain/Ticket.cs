using System.ComponentModel.DataAnnotations;

namespace ParcInfo.Web.Domain;

public enum CategorieTicket
{
    [Display(Name = "PC / poste de travail")] Poste,
    [Display(Name = "Écran")] Ecran,
    [Display(Name = "Imprimante / scanner")] Imprimante,
    [Display(Name = "Réseau / Internet / Wi-Fi")] Reseau,
    [Display(Name = "Messagerie / Microsoft 365")] Messagerie,
    [Display(Name = "Logiciel / application métier")] Logiciel,
    [Display(Name = "Compte / mot de passe / accès")] Compte,
    [Display(Name = "Téléphonie")] Telephonie,
    [Display(Name = "Autre")] Autre,
}

/// <summary>Question « Quel est l'impact sur votre travail ? ».</summary>
public enum ImpactTicket
{
    [Display(Name = "Je ne peux plus travailler")] Bloquant,
    [Display(Name = "Je peux travailler mais je suis gêné")] Genant,
    [Display(Name = "Pas urgent / simple question")] Mineur,
}

/// <summary>Question « Qui est concerné ? ».</summary>
public enum PorteeTicket
{
    [Display(Name = "Moi uniquement")] Personne,
    [Display(Name = "Plusieurs personnes")] Plusieurs,
    [Display(Name = "Tout le site / service")] Site,
}

public enum PrioriteTicket
{
    [Display(Name = "Basse")] Basse,
    [Display(Name = "Normale")] Normale,
    [Display(Name = "Haute")] Haute,
    [Display(Name = "Critique")] Critique,
}

public enum StatutTicket
{
    [Display(Name = "Nouveau")] Nouveau,
    [Display(Name = "En cours")] EnCours,
    [Display(Name = "En attente du demandeur")] AttenteDemandeur,
    [Display(Name = "En attente fournisseur / matériel")] AttenteTiers,
    [Display(Name = "Résolu")] Resolu,
    [Display(Name = "Clos")] Clos,
}

/// <summary>Analyse : origine du problème.</summary>
public enum CauseTicket
{
    [Display(Name = "Panne matérielle")] Materiel,
    [Display(Name = "Bug / configuration logicielle")] Logiciel,
    [Display(Name = "Réseau / connexion")] Reseau,
    [Display(Name = "Compte / droits / mot de passe")] Compte,
    [Display(Name = "Manipulation / besoin de formation")] Utilisation,
    [Display(Name = "Service externe / fournisseur")] Fournisseur,
    [Display(Name = "Cause non identifiée")] Inconnue,
}

/// <summary>Analyse : comment le problème a été résolu.</summary>
public enum ModeResolution
{
    [Display(Name = "À distance (prise en main)")] Distance,
    [Display(Name = "Par téléphone / conseil")] Telephone,
    [Display(Name = "Intervention sur site")] SurSite,
    [Display(Name = "Remplacement du matériel")] Remplacement,
    [Display(Name = "Via fournisseur / prestataire")] Fournisseur,
    [Display(Name = "Autre")] Autre,
}

/// <summary>Ticket d'incident ou d'assistance, suivi par le service informatique.</summary>
public class Ticket
{
    public int Id { get; set; }
    public int Numero { get; set; }
    public DateTime CreeLe { get; set; }
    public DateTime ModifieLe { get; set; }
    /// <summary>Compte Microsoft avec lequel le ticket a été saisi.</summary>
    public string SaisiePar { get; set; } = "";

    // Déclaration
    public string DemandeurNom { get; set; } = "";
    public string DemandeurEmail { get; set; } = "";
    public string DemandeurTelephone { get; set; } = "";
    /// <summary>Site ou service où se trouve le problème.</summary>
    public string Lieu { get; set; } = "";
    public CategorieTicket Categorie { get; set; }
    public ImpactTicket Impact { get; set; }
    public PorteeTicket Portee { get; set; }
    public string Titre { get; set; } = "";
    public string Description { get; set; } = "";
    public string? DejaEssaye { get; set; }
    public string? Disponibilites { get; set; }
    /// <summary>N° interne saisi ; <see cref="MaterielId"/> est renseigné s'il correspond à un matériel de l'inventaire.</summary>
    public string? MaterielReference { get; set; }
    public int? MaterielId { get; set; }
    public Materiel? Materiel { get; set; }

    // Traitement
    public PrioriteTicket Priorite { get; set; }
    public StatutTicket Statut { get; set; }
    public string? AssigneA { get; set; }
    public DateTime? PrisEnChargeLe { get; set; }
    public DateTime? ResoluLe { get; set; }
    public DateTime? ClosLe { get; set; }
    public int NbReouvertures { get; set; }

    // Analyse
    public string? Diagnostic { get; set; }
    public CauseTicket? Cause { get; set; }
    public string? Solution { get; set; }
    public ModeResolution? ModeResolution { get; set; }
    public int? TempsPasseMinutes { get; set; }

    // Retour du demandeur
    public int? SatisfactionNote { get; set; }
    public string? SatisfactionCommentaire { get; set; }

    public List<TicketMessage> Messages { get; set; } = [];

    public bool EstOuvert => Statut is not (StatutTicket.Resolu or StatutTicket.Clos);

    /// <summary>Priorité proposée à la création, d'après l'impact et le nombre de personnes concernées.</summary>
    public static PrioriteTicket PrioriteProposee(ImpactTicket impact, PorteeTicket portee) => (impact, portee) switch
    {
        (ImpactTicket.Bloquant, PorteeTicket.Personne) => PrioriteTicket.Haute,
        (ImpactTicket.Bloquant, _) => PrioriteTicket.Critique,
        (ImpactTicket.Genant, PorteeTicket.Personne) => PrioriteTicket.Normale,
        (ImpactTicket.Genant, _) => PrioriteTicket.Haute,
        _ => PrioriteTicket.Basse,
    };
}

/// <summary>Échange sur un ticket : message, note interne (invisible du demandeur) ou trace automatique.</summary>
public class TicketMessage
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public DateTime Date { get; set; }
    public string Auteur { get; set; } = "";
    public string AuteurEmail { get; set; } = "";
    public string Texte { get; set; } = "";
    /// <summary>Note réservée au service informatique.</summary>
    public bool Interne { get; set; }
    /// <summary>Trace générée par l'application (changement de statut…).</summary>
    public bool Systeme { get; set; }
}
