using System.ComponentModel.DataAnnotations;

namespace ParcInfo.Web.Domain;

/// <summary>Droit attribuable sur la page « Droits ».</summary>
public enum Droit
{
    [Display(Name = "Gestionnaire", Description = "Service informatique : inventaire, mouvements, demandes, tickets, administration.")]
    Gestionnaire,
    [Display(Name = "Valideur", Description = "Valide ou refuse les demandes informatiques, reçoit les relances.")]
    Valideur,
    [Display(Name = "Admin tickets", Description = "Traite les tickets et consulte leur analyse.")]
    AdminTickets,
    [Display(Name = "Consultation inventaire", Description = "Consulte l'inventaire, les fiches matériel et le journal d'inventaire, sans rien modifier.")]
    ConsultationInventaire,
}

/// <summary>
/// Droits d'un compte Microsoft, gérés par les administrateurs principaux sur la page « Droits ».
/// Les administrateurs principaux eux-mêmes sont fixés dans la configuration (« Authentification:Administrateurs »).
/// </summary>
public class DroitAcces
{
    public int Id { get; set; }
    /// <summary>Adresse du compte (en minuscules) : boîte mail ou identifiant de connexion.</summary>
    public string Email { get; set; } = "";
    public string? Nom { get; set; }
    public bool Gestionnaire { get; set; }
    public bool Valideur { get; set; }
    public bool AdminTickets { get; set; }
    public bool ConsultationInventaire { get; set; }
    public string? ModifiePar { get; set; }
    public DateTime ModifieLe { get; set; }

    public bool A(Droit d) => d switch
    {
        Droit.Gestionnaire => Gestionnaire,
        Droit.Valideur => Valideur,
        Droit.AdminTickets => AdminTickets,
        Droit.ConsultationInventaire => ConsultationInventaire,
        _ => false,
    };

    public void Definir(Droit d, bool valeur)
    {
        switch (d)
        {
            case Droit.Gestionnaire: Gestionnaire = valeur; break;
            case Droit.Valideur: Valideur = valeur; break;
            case Droit.AdminTickets: AdminTickets = valeur; break;
            case Droit.ConsultationInventaire: ConsultationInventaire = valeur; break;
        }
    }

    public IEnumerable<Droit> Droits => Enum.GetValues<Droit>().Where(A);
}
