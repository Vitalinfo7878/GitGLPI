using System.ComponentModel.DataAnnotations;

namespace ParcInfo.Web.Domain;

public enum TypeMateriel
{
    [Display(Name = "PC fixe")] PcFixe,
    [Display(Name = "PC portable")] PcPortable,
    [Display(Name = "Écran")] Ecran,
    [Display(Name = "Imprimante")] Imprimante,
}

public enum StatutMateriel
{
    [Display(Name = "En stock")] EnStock,
    [Display(Name = "Attribué")] Attribue,
    [Display(Name = "En réparation")] EnReparation,
    [Display(Name = "Renvoyé fournisseur")] RenvoyeFournisseur,
    [Display(Name = "Volé / perdu")] VolePerdu,
    [Display(Name = "Rebut en attente")] RebutEnAttente,
    [Display(Name = "Rebuté")] Rebute,
}

/// <summary>
/// Types de mouvement. Les libellés « Excel » sont ceux de la colonne « Type de mouvement » du classeur historique.
/// </summary>
public enum TypeMouvement
{
    /// <summary>Matériel déjà en place lors de la création de l'inventaire.</summary>
    [Display(Name = "Sortie initiale")] SortieInitiale,
    /// <summary>Matériel déjà en stock lors de la création de l'inventaire.</summary>
    [Display(Name = "Entrée initiale (SI)")] EntreeInitiale,
    [Display(Name = "Achat / réception fournisseur")] Achat,
    [Display(Name = "Attribution (sortie)")] Attribution,
    [Display(Name = "Restitution (entrée)")] Restitution,
    [Display(Name = "Envoi en réparation")] EnvoiReparation,
    [Display(Name = "Retour de réparation")] RetourReparation,
    [Display(Name = "Renvoi fournisseur")] RenvoiFournisseur,
    [Display(Name = "Vol / perte")] VolPerte,
    [Display(Name = "Mise en attente de rebut")] RebutEnAttente,
    [Display(Name = "Mise au rebut")] MiseAuRebut,
}

public enum TypeFiche
{
    [Display(Name = "Fiche d'attribution")] Attribution,
    [Display(Name = "Fiche de restitution")] Restitution,
    [Display(Name = "Fiche d'achat (stock)")] Achat,
    [Display(Name = "Fiche de mise au rebut")] MiseAuRebut,
}

public enum StatutReception
{
    [Display(Name = "Non demandée")] NonDemandee,
    [Display(Name = "En attente")] EnAttente,
    [Display(Name = "Confirmée")] Confirmee,
}

public enum TypeDetenteur
{
    [Display(Name = "Aucun")] Aucun,
    [Display(Name = "Site")] Site,
    [Display(Name = "Personne")] Personne,
}
