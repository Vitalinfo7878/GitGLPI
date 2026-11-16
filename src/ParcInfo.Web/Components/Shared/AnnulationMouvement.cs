using MudBlazor;
using ParcInfo.Web.Services;

namespace ParcInfo.Web.Components.Shared;

/// <summary>
/// Question posée avant d'annuler un mouvement : le matériel revient par défaut à son ancienne affectation,
/// ou au service informatique (en stock).
/// </summary>
public static class AnnulationMouvement
{
    public enum Choix { Abandon, AncienneAffectation, ServiceInformatique }

    private const string Si = "Service informatique (en stock)";

    public static async Task<Choix> DemanderAsync(IDialogService dialogues, ServiceMouvements.ApercuAnnulation a)
    {
        var titre = $"Annuler le mouvement n° {a.Numero} ?";
        var fiche = " Une fiche qui ne contient que ce mouvement est supprimée avec lui. L'annulation peut être défaite depuis le journal d'inventaire.";
        if (!a.EstDernier)
        {
            var ok = await dialogues.ShowMessageBoxAsync(titre,
                $"{a.Libelle}. Ce n'est pas le dernier mouvement de ce matériel : son affectation actuelle ne change pas.{fiche}",
                yesText: "Annuler le mouvement", cancelText: "Ne rien faire");
            return ok == true ? Choix.AncienneAffectation : Choix.Abandon;
        }
        if (a.AncienneAffectation is null or Si)
        {
            var ok = await dialogues.ShowMessageBoxAsync(titre,
                $"{a.Libelle}. Le matériel revient au service informatique (en stock).{fiche}",
                yesText: "Annuler le mouvement", cancelText: "Ne rien faire");
            return ok == true ? Choix.ServiceInformatique : Choix.Abandon;
        }
        var r = await dialogues.ShowMessageBoxAsync(titre,
            $"{a.Libelle}. Où remettre le matériel ? Par défaut, à son ancienne affectation : {a.AncienneAffectation}.{fiche}",
            yesText: $"Remettre à : {a.AncienneAffectation}", noText: "Remettre au service informatique", cancelText: "Ne rien faire");
        return r switch { true => Choix.AncienneAffectation, false => Choix.ServiceInformatique, _ => Choix.Abandon };
    }
}
