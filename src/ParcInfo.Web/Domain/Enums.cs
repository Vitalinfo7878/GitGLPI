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
/// Types de mouvement. Les libellés reprennent ceux de la colonne « Type de mouvement » du classeur historique.
/// Seuls Achat, Entrée, Sortie et Mise au rebut sont saisis (voir RegleMouvement.TypesSaisissables) ;
/// les types initiaux viennent de l'import, les autres sont conservés pour la compatibilité des données.
/// </summary>
public enum TypeMouvement
{
    /// <summary>Matériel déjà en place lors de la création de l'inventaire.</summary>
    [Display(Name = "Sortie initiale (inventaire)")] SortieInitiale,
    /// <summary>Matériel déjà en stock lors de la création de l'inventaire.</summary>
    [Display(Name = "Entrée initiale (inventaire)")] EntreeInitiale,
    [Display(Name = "Achat")] Achat,
    [Display(Name = "Sortie (attribution)")] Attribution,
    [Display(Name = "Entrée (dans le SI)")] Restitution,
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

/// <summary>Distinction des PC dans l'inventaire : PC de site (cuisine) ou PC enrôlé (géré dans Intune).</summary>
public enum CategoriePc
{
    [Display(Name = "PC Site")] Site,
    [Display(Name = "PC Enrôlé")] Enrole,
}

public enum TypeDetenteur
{
    [Display(Name = "Aucun")] Aucun,
    [Display(Name = "Site")] Site,
    [Display(Name = "Personne")] Personne,
}
