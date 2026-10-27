using System.ComponentModel.DataAnnotations;
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
    public const string ComptesMisAJour = "Comptes Microsoft mis à jour";

    private static readonly HashSet<string> ActionsMateriel =
    [
        MaterielModifie, "Matériel supprimé", "Nouveau matériel (non inventorié)", "Catégorie PC", "Mot de passe affiché", "Import Excel", "Pas un doublon",
    ];

    private static readonly HashSet<string> ActionsMouvement =
        [.. Enum.GetValues<TypeMouvement>().Select(t => t.Libelle()), "Correction du détenteur"];

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
