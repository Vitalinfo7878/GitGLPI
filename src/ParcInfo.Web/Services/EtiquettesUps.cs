using System.Globalization;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

/// <summary>
/// Bouton « Prêt » d'une demande : message pour faire préparer les étiquettes UPS, un colis par matériel
/// (le matériel rattaché à la demande ; à défaut, un par matériel demandé).
/// </summary>
public static class EtiquettesUps
{
    public static string Colis(TypeMateriel t) => t switch
    {
        TypeMateriel.PcFixe => "Pc Fixe -5kg : Longueur 50 * largeur 30 * hauteur 20 cm",
        TypeMateriel.PcPortable => "Pc portable -5kg : Longueur 50 * largeur 30 * hauteur 10 cm",
        TypeMateriel.Ecran => "Ecran -10kg",
        TypeMateriel.Imprimante => "Imprimante -10kg : Longueur 50 * largeur 45 * hauteur 30 cm",
        _ => t.Libelle(),
    };

    /// <summary>Types des colis à expédier, dans l'ordre PC fixe, PC portable, écran, imprimante.</summary>
    public static List<TypeMateriel> Liste(Demande d, IEnumerable<Materiel> attribues)
    {
        var types = attribues.Select(m => m.Type).ToList();
        if (types.Count == 0)
            types = d.ListeObjets.Select(o => o switch
            {
                "PC fixe (site)" or "PC fixe (Siège / DR)" => (TypeMateriel?)TypeMateriel.PcFixe,
                "PC portable" => TypeMateriel.PcPortable,
                "Ecran" => TypeMateriel.Ecran,
                "Imprimante" => TypeMateriel.Imprimante,
                _ => null,
            }).OfType<TypeMateriel>().ToList();
        return types.Order().ToList();
    }

    /// <summary>« demain », ou le jour ouvré suivant s'il n'est pas le lendemain (« lundi » pour un vendredi, après un jour férié…).</summary>
    public static string Jour(DateOnly aujourdhui)
    {
        var j = JoursOuvres.Ajouter(aujourdhui, 1);
        return j == aujourdhui.AddDays(1) ? "demain" : j.ToString("dddd", CultureInfo.GetCultureInfo("fr-FR"));
    }

    public static string Texte(Demande d, IEnumerable<Materiel> attribues, DateOnly aujourdhui)
    {
        var colis = Liste(d, attribues);
        var lignes = new List<string>
        {
            "Bonjour Laetitia,",
            "",
            $"Peux tu stp préparer {colis.Count} étiquette{(colis.Count > 1 ? "s" : "")} UPS pour {Jour(aujourdhui)} 14h :",
            "",
        };
        lignes.AddRange(colis.Select(Colis));
        lignes.Add("");
        lignes.Add(string.IsNullOrWhiteSpace(d.DestinataireLivraison) ? d.DemandeurNom : d.DestinataireLivraison.Trim());
        lignes.Add(d.AdresseLivraison.Trim());
        lignes.Add("");
        lignes.Add("Merci");
        return string.Join("\r\n", lignes) + "\r\n";
    }
}
