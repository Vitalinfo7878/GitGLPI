namespace ParcInfo.Web.Domain;

/// <summary>
/// Effet de chaque type de mouvement sur le matériel : nouveau statut, détenteur, fiche générée, statuts de départ autorisés.
/// </summary>
public sealed record RegleMouvement(
    TypeMouvement Type,
    StatutMateriel StatutResultant,
    bool DestinationEstDetenteur,
    TypeFiche? Fiche,
    StatutMateriel[] StatutsAutorises,
    string? ExpediteurParDefaut,
    string? DestinationParDefaut)
{
    public const string ServiceInformatique = "SERVICE INFORMATIQUE";

    private static readonly StatutMateriel[] Tous = Enum.GetValues<StatutMateriel>();

    private static readonly Dictionary<TypeMouvement, RegleMouvement> Regles = new[]
    {
        new RegleMouvement(TypeMouvement.SortieInitiale, StatutMateriel.Attribue, true, null, Tous, ServiceInformatique, null),
        new RegleMouvement(TypeMouvement.EntreeInitiale, StatutMateriel.EnStock, false, null, Tous, null, ServiceInformatique),
        new RegleMouvement(TypeMouvement.Achat, StatutMateriel.EnStock, false, TypeFiche.Achat, [StatutMateriel.EnStock], null, ServiceInformatique),
        new RegleMouvement(TypeMouvement.Attribution, StatutMateriel.Attribue, true, TypeFiche.Attribution,
            [StatutMateriel.EnStock, StatutMateriel.Attribue], ServiceInformatique, null),
        new RegleMouvement(TypeMouvement.Restitution, StatutMateriel.EnStock, false, TypeFiche.Restitution,
            [StatutMateriel.Attribue, StatutMateriel.VolePerdu], null, ServiceInformatique),
        new RegleMouvement(TypeMouvement.EnvoiReparation, StatutMateriel.EnReparation, false, null,
            [StatutMateriel.EnStock, StatutMateriel.Attribue], ServiceInformatique, null),
        new RegleMouvement(TypeMouvement.RetourReparation, StatutMateriel.EnStock, false, null,
            [StatutMateriel.EnReparation], null, ServiceInformatique),
        new RegleMouvement(TypeMouvement.RenvoiFournisseur, StatutMateriel.RenvoyeFournisseur, false, null,
            [StatutMateriel.EnStock, StatutMateriel.Attribue, StatutMateriel.EnReparation], ServiceInformatique, null),
        new RegleMouvement(TypeMouvement.VolPerte, StatutMateriel.VolePerdu, false, null,
            [StatutMateriel.EnStock, StatutMateriel.Attribue], null, null),
        new RegleMouvement(TypeMouvement.RebutEnAttente, StatutMateriel.RebutEnAttente, false, null,
            [StatutMateriel.EnStock, StatutMateriel.Attribue, StatutMateriel.EnReparation], null, "REBUT"),
        new RegleMouvement(TypeMouvement.MiseAuRebut, StatutMateriel.Rebute, false, TypeFiche.MiseAuRebut,
            [StatutMateriel.EnStock, StatutMateriel.Attribue, StatutMateriel.EnReparation, StatutMateriel.RebutEnAttente], null, "REBUT"),
    }.ToDictionary(r => r.Type);

    public static RegleMouvement Pour(TypeMouvement type) => Regles[type];

    /// <summary>Types proposés dans l'écran « Nouveau mouvement » (hors types réservés à l'import et à la réception d'achat).</summary>
    public static IReadOnlyList<TypeMouvement> TypesSaisissables { get; } =
    [
        TypeMouvement.Attribution, TypeMouvement.Restitution, TypeMouvement.EnvoiReparation, TypeMouvement.RetourReparation,
        TypeMouvement.RenvoiFournisseur, TypeMouvement.VolPerte, TypeMouvement.RebutEnAttente, TypeMouvement.MiseAuRebut,
    ];

    public bool EstAutorisePour(StatutMateriel statut) => StatutsAutorises.Contains(statut);

    public static string PrefixeFiche(TypeFiche type) => type switch
    {
        TypeFiche.Attribution => "FIA",
        TypeFiche.Restitution => "FIR",
        TypeFiche.Achat => "FIS",
        TypeFiche.MiseAuRebut => "FIRE",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Code de fiche selon la nomenclature historique : FIA2026 1997.</summary>
    public static string CodeFiche(TypeFiche type, int annee, int numero) => $"{PrefixeFiche(type)}{annee} {numero:D4}";
}
