using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

/// <summary>Domaine d'une entrée du journal, pour le « Journal d'inventaire ».</summary>
public enum DomaineJournal
{
    [Display(Name = "Matériel")] Materiel,
    [Display(Name = "Mouvement")] Mouvement,
    [Display(Name = "Compte Microsoft")] CompteMicrosoft,
    [Display(Name = "Autre")] Autre,
}

/// <summary>Classement des entrées du journal et détail des modifications de matériel.</summary>
public static class JournalInventaire
{
    public const string MaterielModifie = "Matériel modifié";
    public const string MaterielSupprime = "Matériel supprimé";
    public const string MaterielRestaure = "Matériel restauré";
    public const string RattacheDemande = "Matériel d'une demande";
    public const string MouvementSupprime = "Mouvement supprimé";
    public const string MouvementRestaure = "Mouvement restauré";
    public const string ComptesMisAJour = "Comptes Microsoft mis à jour";

    private static readonly HashSet<string> ActionsMateriel =
    [
        MaterielModifie, MaterielSupprime, MaterielRestaure, RattacheDemande, "Nouveau matériel (non inventorié)", "Catégorie PC", "Import Excel", "Pas un doublon",
    ];

    private static readonly HashSet<string> ActionsMouvement =
        [.. Enum.GetValues<TypeMouvement>().Select(t => t.Libelle()), "Correction du détenteur", MouvementSupprime, MouvementRestaure];

    /// <summary>Suppressions (matériel ou mouvement) : en rouge et filtre « Suppressions seulement ».</summary>
    public static bool EstSuppression(string action) => action is MaterielSupprime or MouvementSupprime;

    private static readonly HashSet<string> ActionsComptes =
    [
        "Synchronisation annuaire Microsoft", "Compte Microsoft relié", "Sans compte Microsoft", "Compte Microsoft à revoir", ComptesMisAJour,
    ];

    public static DomaineJournal Domaine(string action) =>
        ActionsMateriel.Contains(action) ? DomaineJournal.Materiel
        : ActionsMouvement.Contains(action) ? DomaineJournal.Mouvement
        : ActionsComptes.Contains(action) ? DomaineJournal.CompteMicrosoft
        : DomaineJournal.Autre;

    /// <summary>Actions affichées dans le journal d'inventaire (pour filtrer en base).</summary>
    public static IReadOnlyCollection<string> Actions { get; } = [.. ActionsMateriel, .. ActionsMouvement, .. ActionsComptes];

    /// <summary>
    /// Champs modifiés d'un matériel : « Modèle : X → Y ; … ». Les secrets (mot de passe, clé Office) sont signalés sans leur valeur.
    /// </summary>
    public static List<string> Differences(Materiel avant, Materiel apres, Func<int?, string?> region)
    {
        var l = new List<string>();
        void C(string libelle, object? a, object? b)
        {
            var (x, y) = (Texte(a), Texte(b));
            if (x != y) l.Add($"{libelle} : {x ?? "—"} → {y ?? "—"}");
        }
        void Secret(string libelle, string? a, string? b)
        {
            if (Texte(a) != Texte(b)) l.Add($"{libelle} modifié(e)");
        }
        C("N° interne", avant.NumInterne, apres.NumInterne);
        C("Type", avant.Type, apres.Type);
        C("Modèle", avant.Modele, apres.Modele);
        C("N° de série", avant.NumSerie, apres.NumSerie);
        C("Région / service", region(avant.RegionId), region(apres.RegionId));
        C("Année", avant.AnneeAcquisition, apres.AnneeAcquisition);
        C("Mise en place", avant.DateMiseEnPlace, apres.DateMiseEnPlace);
        C("Catégorie PC", avant.CategoriePc, apres.CategoriePc);
        C("Configuration", avant.Configuration, apres.Configuration);
        C("Profil Microsoft", avant.ProfilMicrosoft, apres.ProfilMicrosoft);
        C("Licence Microsoft", avant.LicenceMicrosoft, apres.LicenceMicrosoft);
        Secret("Mot de passe de session", avant.MotDePasseSession, apres.MotDePasseSession);
        C("Version Windows", avant.VersionWindows, apres.VersionWindows);
        C("Édition Windows", avant.EditionWindows, apres.EditionWindows);
        C("Note", avant.Note, apres.Note);
        Secret("Clé Office", avant.CleOffice, apres.CleOffice);
        C("Office en dur", avant.OfficeEnDur, apres.OfficeEnDur);
        C("Connexion administrateur", avant.ConnexionAdmin, apres.ConnexionAdmin);
        C("Labtech", avant.Labtech, apres.Labtech);
        C("À changer", avant.AChanger, apres.AChanger);
        C("N° immobilisation", avant.NumImmo, apres.NumImmo);
        C("N° de facture", avant.NumFacture, apres.NumFacture);
        C("Commentaire", avant.Commentaire, apres.Commentaire);
        return l;
    }

    private static string? Texte(object? v) => v switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        bool b => b ? "oui" : "non",
        DateOnly d => d.ToString("dd/MM/yyyy"),
        Enum e => e.Libelle(),
        _ => v.ToString(),
    };

    /// <summary>Détail limité à la taille de la colonne.</summary>
    public static string Tronquer(string s, int max = 3900) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Mouvement d'un matériel supprimé, conservé pour sa restauration.</summary>
public sealed record InstantaneMouvement(
    int Numero, string? NumeroOrigine, TypeMouvement Type, DateOnly? Date, string? Expediteur, int? DestinationSiteId, int? DestinationPersonneId,
    string? DestinationLibre, string? Region, string? DocumentReference, DateOnly? DateReception, string? Commentaire, int? FicheId, string? CreePar, DateTime CreeLe);

/// <summary>
/// État d'un matériel enregistré dans le journal avant une modification ou une suppression.
/// Le mot de passe de session y reste chiffré.
/// </summary>
public sealed class InstantaneMateriel
{
    public string NumInterne { get; set; } = "";
    public TypeMateriel Type { get; set; }
    public string? Modele { get; set; }
    public string? NumSerie { get; set; }
    public StatutMateriel Statut { get; set; }
    public int? SiteId { get; set; }
    public int? PersonneId { get; set; }
    public int? RegionId { get; set; }
    public CategoriePc? CategoriePc { get; set; }
    public string? Configuration { get; set; }
    public int? AnneeAcquisition { get; set; }
    public DateOnly? DateMiseEnPlace { get; set; }
    public string? ProfilMicrosoft { get; set; }
    public string? LicenceMicrosoft { get; set; }
    public bool? OfficeEnDur { get; set; }
    public int? Note { get; set; }
    public string? MotDePasseChiffre { get; set; }
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
    /// <summary>Adresse du détenteur au moment de l'instantané (modifiable depuis la fiche matériel).</summary>
    public string? EmailDetenteur { get; set; }
    public List<InstantaneMouvement> Mouvements { get; set; } = [];
    /// <summary>Demandes pour lesquelles le matériel avait été attribué.</summary>
    public List<int> Demandes { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static InstantaneMateriel Depuis(Materiel m, string? emailDetenteur, IEnumerable<Mouvement>? mouvements = null, IEnumerable<int>? demandes = null) => new()
    {
        NumInterne = m.NumInterne, Type = m.Type, Modele = m.Modele, NumSerie = m.NumSerie, Statut = m.Statut, SiteId = m.SiteId, PersonneId = m.PersonneId,
        RegionId = m.RegionId, CategoriePc = m.CategoriePc, Configuration = m.Configuration, AnneeAcquisition = m.AnneeAcquisition,
        DateMiseEnPlace = m.DateMiseEnPlace, ProfilMicrosoft = m.ProfilMicrosoft, LicenceMicrosoft = m.LicenceMicrosoft, OfficeEnDur = m.OfficeEnDur,
        Note = m.Note, MotDePasseChiffre = ChiffrementSecrets.Chiffrer(m.MotDePasseSession), ConnexionAdmin = m.ConnexionAdmin,
        VersionWindows = m.VersionWindows, EditionWindows = m.EditionWindows, AChanger = m.AChanger, Labtech = m.Labtech, Commentaire = m.Commentaire,
        CleOffice = m.CleOffice, NumImmo = m.NumImmo, NumFacture = m.NumFacture, CreeLe = m.CreeLe, EmailDetenteur = emailDetenteur,
        Mouvements = (mouvements ?? []).OrderBy(x => x.Numero).Select(x => new InstantaneMouvement(x.Numero, x.NumeroOrigine, x.Type, x.Date, x.Expediteur,
            x.DestinationSiteId, x.DestinationPersonneId, x.DestinationLibre, x.Region, x.DocumentReference, x.DateReception, x.Commentaire, x.FicheId,
            x.CreePar, x.CreeLe)).ToList(),
        Demandes = demandes?.ToList() ?? [],
    };

    /// <summary>Remet les champs modifiables depuis la fiche matériel (pas le statut ni le détenteur, qui dépendent des mouvements).</summary>
    public void AppliquerChampsA(Materiel m)
    {
        m.NumInterne = NumInterne; m.Type = Type; m.Modele = Modele; m.NumSerie = NumSerie; m.RegionId = RegionId; m.CategoriePc = CategoriePc;
        m.Configuration = Configuration; m.AnneeAcquisition = AnneeAcquisition; m.DateMiseEnPlace = DateMiseEnPlace; m.ProfilMicrosoft = ProfilMicrosoft;
        m.LicenceMicrosoft = LicenceMicrosoft; m.OfficeEnDur = OfficeEnDur; m.Note = Note; m.MotDePasseSession = ChiffrementSecrets.Dechiffrer(MotDePasseChiffre);
        m.ConnexionAdmin = ConnexionAdmin; m.VersionWindows = VersionWindows; m.EditionWindows = EditionWindows; m.AChanger = AChanger; m.Labtech = Labtech;
        m.Commentaire = Commentaire; m.CleOffice = CleOffice; m.NumImmo = NumImmo; m.NumFacture = NumFacture;
    }

    public string Serialiser() => JsonSerializer.Serialize(this, Options);

    public static InstantaneMateriel Lire(string json) =>
        JsonSerializer.Deserialize<InstantaneMateriel>(json, Options) ?? throw new ErreurMetier("État enregistré illisible.");
}

/// <summary>Fiche supprimée avec son dernier mouvement, conservée pour sa restauration.</summary>
public sealed record InstantaneFiche(Guid IdPublic, TypeFiche Type, int Annee, int Numero, string Code, string? DestinataireNom, string? DestinataireEmail,
    byte[]? Pdf, DateTime? PdfGenereLe, string? CreePar, DateTime CreeLe);

/// <summary>Mouvement supprimé (doublon, erreur) : le mouvement, l'état du matériel avant la suppression et la fiche supprimée avec lui.</summary>
public sealed class InstantaneSuppressionMouvement
{
    public int MaterielId { get; set; }
    public InstantaneMouvement Mouvement { get; set; } = null!;
    /// <summary>Vrai si la suppression a remis le matériel dans l'état du mouvement précédent.</summary>
    public bool EtatModifie { get; set; }
    public StatutMateriel Statut { get; set; }
    public int? SiteId { get; set; }
    public int? PersonneId { get; set; }
    public InstantaneFiche? Fiche { get; set; }

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public string Serialiser() => JsonSerializer.Serialize(this, Options);

    public static InstantaneSuppressionMouvement Lire(string json) =>
        JsonSerializer.Deserialize<InstantaneSuppressionMouvement>(json, Options) ?? throw new ErreurMetier("État enregistré illisible.");
}
