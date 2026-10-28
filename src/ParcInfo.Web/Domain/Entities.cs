using System.Text.RegularExpressions;

namespace ParcInfo.Web.Domain;

/// <summary>Région ou service (« SITE IDF », « DR IDF », « SIEGE »…), colonne « SERVICE / LIEU » de l'Excel.</summary>
public class Region
{
    public int Id { get; set; }
    public string Nom { get; set; } = "";
}

/// <summary>Site (cuisine). Les sites ne sont pas dans Entra ID.</summary>
public class Site
{
    public int Id { get; set; }
    /// <summary>Code du site : « 586 », « S012 », « G124 », « 440-1 »…</summary>
    public string Code { get; set; } = "";
    public string? Nom { get; set; }
    public int? RegionId { get; set; }
    public Region? Region { get; set; }
    public string? Responsable { get; set; }
    public string? Telephone { get; set; }
    /// <summary>Boîte mail du site : son compte voit le matériel du site dans « Mon matériel ».</summary>
    public string? Email { get; set; }
    public string? Adresse { get; set; }
    public bool Actif { get; set; } = true;

    public List<Materiel> Materiels { get; set; } = [];

    /// <summary>Compte Microsoft du site (boîte Exchange), relié par l'annuaire.</summary>
    public string? EntraObjectId { get; set; }
    public CompteMicrosoft? CompteMicrosoft { get; set; }
    /// <summary>Décision prise sur la page « Comptes Microsoft » : ce site n'a pas de compte.</summary>
    public bool SansCompteMicrosoft { get; set; }

    public string Libelle => LibelleDe(Code, Nom, EntraObjectId != null);

    /// <summary>
    /// Site relié à son compte Microsoft : son nom d'affichage, suivi du code s'il n'y figure pas
    /// (sous-sites qui partagent la boîte du site : « 440 - EHPAD SAINTE MONIQUE (440-1) ») ; sinon « code – nom ».
    /// </summary>
    public static string LibelleDe(string code, string? nom, bool relie) =>
        string.IsNullOrWhiteSpace(nom) ? $"Site {code}" : !relie ? $"{code} – {nom}" : ContientCode(nom, code) ? nom : $"{nom} ({code})";

    private static bool ContientCode(string nom, string code) =>
        new[] { code, code.TrimStart('0') }.Where(c => c.Length > 0).Any(c =>
            Regex.IsMatch(nom, $@"(?<![\w-]){Regex.Escape(c)}(?![\w-])", RegexOptions.IgnoreCase));
}

/// <summary>Personne physique (salarié), normalement présente dans Entra ID.</summary>
public class Personne
{
    public int Id { get; set; }
    public string NomComplet { get; set; } = "";
    public string? Email { get; set; }
    /// <summary>Compte Microsoft relié : le nom et l'adresse sont alors ceux de l'annuaire, actualisés chaque nuit.</summary>
    public string? EntraObjectId { get; set; }
    public CompteMicrosoft? CompteMicrosoft { get; set; }
    /// <summary>Décision prise sur la page « Comptes Microsoft » : cette personne n'a pas de compte.</summary>
    public bool SansCompteMicrosoft { get; set; }
    public int? RegionId { get; set; }
    public Region? Region { get; set; }
    public bool Actif { get; set; } = true;

    public List<Materiel> Materiels { get; set; } = [];
}

public class Materiel
{
    public int Id { get; set; }
    /// <summary>Numéro interne (PC0443, PO0080, E0088, I0074, ou anciens noms LPW…/DTW…).</summary>
    public string NumInterne { get; set; } = "";
    public TypeMateriel Type { get; set; }
    public string? Modele { get; set; }
    public string? NumSerie { get; set; }
    public StatutMateriel Statut { get; set; }

    // Détenteur actuel : un site OU une personne (ou aucun si en stock / rebut…)
    public int? SiteId { get; set; }
    public Site? Site { get; set; }
    public int? PersonneId { get; set; }
    public Personne? Personne { get; set; }
    /// <summary>Région / service de rattachement (repris de l'Excel, ou du détenteur).</summary>
    public int? RegionId { get; set; }
    public Region? Region { get; set; }

    // Informations PC
    public CategoriePc? CategoriePc { get; set; }
    public string? Configuration { get; set; }
    public int? AnneeAcquisition { get; set; }
    public DateOnly? DateMiseEnPlace { get; set; }
    public string? ProfilMicrosoft { get; set; }
    public string? LicenceMicrosoft { get; set; }
    public bool? OfficeEnDur { get; set; }
    public int? Note { get; set; }
    /// <summary>Mot de passe de session, chiffré en base. Visible uniquement par l'informatique.</summary>
    public string? MotDePasseSession { get; set; }
    public bool? ConnexionAdmin { get; set; }
    public string? VersionWindows { get; set; }
    public string? EditionWindows { get; set; }
    public bool AChanger { get; set; }
    public bool? Labtech { get; set; }
    public string? Commentaire { get; set; }
    public string? CleOffice { get; set; }
    public string? NumImmo { get; set; }
    public string? NumFacture { get; set; }

    public DateTime CreeLe { get; set; }
    public DateTime ModifieLe { get; set; }

    public List<Mouvement> Mouvements { get; set; } = [];
    /// <summary>Demandes informatiques pour lesquelles ce matériel a été attribué.</summary>
    public List<DemandeMateriel> Demandes { get; set; } = [];

    public TypeDetenteur TypeDetenteur => SiteId != null ? TypeDetenteur.Site : PersonneId != null ? TypeDetenteur.Personne : TypeDetenteur.Aucun;

    public string DetenteurLibelle => Site != null ? Site.Libelle : Personne?.NomComplet ?? "";

    public bool EstUnPc => Type is TypeMateriel.PcFixe or TypeMateriel.PcPortable;
}

public class Mouvement
{
    public int Id { get; set; }
    /// <summary>Numéro séquentiel du mouvement (suite de la numérotation Excel).</summary>
    public int Numero { get; set; }
    /// <summary>Numéro d'origine dans l'Excel lorsqu'il n'était pas numérique (ex. « E748 »).</summary>
    public string? NumeroOrigine { get; set; }
    public int MaterielId { get; set; }
    public Materiel Materiel { get; set; } = null!;
    public TypeMouvement Type { get; set; }
    public DateOnly? Date { get; set; }
    /// <summary>Expéditeur : SERVICE INFORMATIQUE, fournisseur (AMAZON, CELERIS…), site ou personne qui renvoie.</summary>
    public string? Expediteur { get; set; }

    // Destination
    public int? DestinationSiteId { get; set; }
    public Site? DestinationSite { get; set; }
    public int? DestinationPersonneId { get; set; }
    public Personne? DestinationPersonne { get; set; }
    /// <summary>Destination libre quand ce n'est ni un site ni une personne (SERVICE INFORMATIQUE, REBUT, fournisseur…).</summary>
    public string? DestinationLibre { get; set; }
    public string? Region { get; set; }

    public string? DocumentReference { get; set; }
    public DateOnly? DateReception { get; set; }
    public string? Commentaire { get; set; }

    public int? FicheId { get; set; }
    public Fiche? Fiche { get; set; }

    public string? CreePar { get; set; }
    public DateTime CreeLe { get; set; }

    public string DestinationLibelle => DestinationSite?.Libelle ?? DestinationPersonne?.NomComplet ?? DestinationLibre ?? "";
}

/// <summary>Fiche (attribution FIA, restitution FIR, achat FIS, mise au rebut FIRE) regroupant un ou plusieurs mouvements.</summary>
public class Fiche
{
    public int Id { get; set; }
    /// <summary>Identifiant non devinable utilisé dans l'adresse de téléchargement du PDF.</summary>
    public Guid IdPublic { get; set; } = Guid.NewGuid();
    public TypeFiche Type { get; set; }
    public int Annee { get; set; }
    /// <summary>Numéro = numéro du premier mouvement de la fiche (comme les macros Excel).</summary>
    public int Numero { get; set; }
    public string Code { get; set; } = "";

    public string? DestinataireNom { get; set; }
    /// <summary>Adresse du destinataire d'une attribution : il peut télécharger la fiche.</summary>
    public string? DestinataireEmail { get; set; }

    public byte[]? Pdf { get; set; }
    public DateTime? PdfGenereLe { get; set; }

    public string? CreePar { get; set; }
    public DateTime CreeLe { get; set; }

    public List<Mouvement> Mouvements { get; set; } = [];
}

/// <summary>Trace des courriels envoyés (ou simulés en développement).</summary>
public class Courriel
{
    public int Id { get; set; }
    public string Destinataire { get; set; } = "";
    public string Sujet { get; set; } = "";
    public string CorpsHtml { get; set; } = "";
    public DateTime EnvoyeLe { get; set; }
    public bool Simule { get; set; }
    public string? Erreur { get; set; }
}

/// <summary>Journal des actions sensibles (affichage d'un mot de passe, modification, import…).</summary>
public class Journal
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public string Utilisateur { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    public int? MaterielId { get; set; }
    /// <summary>
    /// État du matériel avant une modification ou une suppression (JSON, mot de passe chiffré) : permet de le restaurer
    /// depuis le journal d'inventaire.
    /// </summary>
    public string? Instantane { get; set; }
    public DateTime? RestaureLe { get; set; }
    public string? RestaurePar { get; set; }
}

/// <summary>Groupe de matériels déclaré « pas un doublon » sur la page « Doublons » (clé = type, valeur et matériels concernés).</summary>
public class DoublonIgnore
{
    public string Cle { get; set; } = "";
    public string Par { get; set; } = "";
    public DateTime Le { get; set; }
}
