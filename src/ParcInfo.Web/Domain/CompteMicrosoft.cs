namespace ParcInfo.Web.Domain;

/// <summary>
/// Copie locale d'un compte de l'annuaire Microsoft (Entra ID), actualisée par la synchronisation.
/// Les personnes et les sites y sont reliés par leur identifiant Entra.
/// </summary>
public class CompteMicrosoft
{
    /// <summary>Identifiant d'objet Entra.</summary>
    public string Id { get; set; } = "";
    public string NomAffichage { get; set; } = "";
    public string? Prenom { get; set; }
    public string? Nom { get; set; }
    /// <summary>Identifiant de connexion (UPN).</summary>
    public string? Upn { get; set; }
    public string? Email { get; set; }
    /// <summary>Autres adresses (alias Exchange, adresses secondaires), en minuscules, séparées par des espaces.</summary>
    public string? AutresAdresses { get; set; }
    public string? Poste { get; set; }
    public string? Service { get; set; }
    public string? Bureau { get; set; }
    public string? Societe { get; set; }
    public string? Matricule { get; set; }
    public string? TelephoneMobile { get; set; }
    public string? TelephonePro { get; set; }
    public string? Adresse { get; set; }
    public string? Ville { get; set; }
    public string? ResponsableId { get; set; }
    public string? ResponsableNom { get; set; }
    /// <summary>Groupes dont le compte est membre direct, séparés par « ; ».</summary>
    public string? Groupes { get; set; }
    /// <summary>Licences Microsoft 365 attribuées, séparées par « ; ».</summary>
    public string? Licences { get; set; }
    /// <summary>Connexion autorisée (compte activé).</summary>
    public bool Actif { get; set; }
    public DateTime? CreeDansEntraLe { get; set; }
    /// <summary>Compte absent de l'annuaire lors de la dernière synchronisation (supprimé).</summary>
    public bool Supprime { get; set; }
    public DateTime SynchroniseLe { get; set; }

    /// <summary>Le compte ne permet plus de se connecter : départ à traiter si du matériel est encore attribué.</summary>
    public bool EstParti => Supprime || !Actif;

    public IEnumerable<string> ListeGroupes => Liste(Groupes);
    public IEnumerable<string> ListeLicences => Liste(Licences);

    /// <summary>Toutes les adresses connues du compte, en minuscules.</summary>
    public IEnumerable<string> Adresses => new[] { Email, Upn }
        .Concat((AutresAdresses ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .OfType<string>().Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Contains('@')).Distinct();

    /// <summary>Adresse de contact : la boîte mail, à défaut l'identifiant de connexion.</summary>
    public string? AdressePrincipale => (Email ?? Upn)?.ToLowerInvariant();

    private static IEnumerable<string> Liste(string? s) => (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
