using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

public enum TypeDoublon
{
    /// <summary>Même numéro interne (nom du PC) dans l'Excel : le second a été renommé « …-DOUBLON-L… » à l'import.</summary>
    NumInterne,
    /// <summary>Même numéro de série sur plusieurs matériels.</summary>
    NumSerie,
    /// <summary>Une personne ou un site détient plusieurs appareils du même type (2 imprimantes, 2 PC fixes…).</summary>
    MemeDetenteur,
}

/// <summary>Matériel examiné par le contrôle (Id = 0 avant enregistrement, lors de l'aperçu d'import).</summary>
public sealed record LigneControle(int Id, string NumInterne, TypeMateriel Type, string? Modele, string? NumSerie, StatutMateriel Statut,
    string? Detenteur, string? CleDetenteur);

/// <summary>Groupe de matériels à trancher. La clé change si la composition du groupe change (le doublon réapparaît alors).</summary>
public sealed record GroupeDoublon(TypeDoublon Type, string Cle, string Titre, List<LigneControle> Materiels);

/// <summary>Doublons signalés à l'import et sur la page « Doublons » : n° interne, n° de série, même type chez un même détenteur.</summary>
public sealed class ControleDoublons(IDbContextFactory<ParcDbContext> dbFactory)
{
    public const string MarqueDoublonImport = "-DOUBLON-";

    // Valeurs de remplissage qui ne sont pas de vrais numéros de série
    private static readonly HashSet<string> SeriesFactices = new(StringComparer.OrdinalIgnoreCase)
        { "INCONNU", "AUCUN", "SANS", "NEANT", "ABSENT", "NONRENSEIGNE", "AREMPLIR", "ACOMPLETER", "ARRACHE", "ARRACHEE", "ILLISIBLE", "EFFACE" };

    public static List<GroupeDoublon> Analyser(IEnumerable<LigneControle> lignes)
    {
        var liste = lignes.ToList();
        var groupes = new List<GroupeDoublon>();

        foreach (var g in liste.GroupBy(l => NumBase(l.NumInterne), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            groupes.Add(Groupe(TypeDoublon.NumInterne, $"interne:{g.Key.ToUpperInvariant()}", $"N° interne {g.Key} présent {g.Count()} fois dans l'Excel", g));

        foreach (var g in liste.Where(l => SerieNormalisee(l.NumSerie) is not null).GroupBy(l => SerieNormalisee(l.NumSerie)!).Where(g => g.Count() > 1))
            groupes.Add(Groupe(TypeDoublon.NumSerie, $"serie:{g.Key}", $"N° de série {g.First().NumSerie} sur {g.Count()} matériels", g));

        foreach (var g in liste.Where(l => l.Statut == StatutMateriel.Attribue && l.CleDetenteur is not null)
                     .GroupBy(l => (l.CleDetenteur!, l.Type)).Where(g => g.Count() > 1))
            groupes.Add(Groupe(TypeDoublon.MemeDetenteur, $"detenteur:{g.Key.Item1}:{g.Key.Type}",
                $"{g.First().Detenteur} : {g.Count()} × {g.Key.Type.Libelle()}", g));

        return groupes;
    }

    private static GroupeDoublon Groupe(TypeDoublon type, string cle, string titre, IEnumerable<LigneControle> materiels)
    {
        var tries = materiels.OrderBy(m => m.NumInterne).ToList();
        // Composition du groupe dans la clé : un « pas un doublon » ne vaut que pour ces matériels-là
        return new GroupeDoublon(type, $"{cle}|{string.Join(',', tries.Select(m => m.Id > 0 ? m.Id.ToString() : m.NumInterne))}", titre, tries);
    }

    /// <summary>N° interne sans le suffixe ajouté à l'import pour les doublons de l'Excel.</summary>
    public static string NumBase(string numInterne)
    {
        var i = numInterne.IndexOf(MarqueDoublonImport, StringComparison.OrdinalIgnoreCase);
        return i > 0 ? numInterne[..i] : numInterne;
    }

    public static string? SerieNormalisee(string? serie)
    {
        if (string.IsNullOrWhiteSpace(serie)) return null;
        var s = new string(serie.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (s.Length < 4 || SeriesFactices.Contains(s) || s.Distinct().Count() == 1) return null;
        return s;
    }

    public static LigneControle Ligne(Materiel m) => new(m.Id, m.NumInterne, m.Type, m.Modele, m.NumSerie, m.Statut, m.DetenteurLibelle,
        m.Personne is not null ? $"P{(m.PersonneId ?? 0)}:{m.Personne.NomComplet}" : m.Site is not null ? $"S{(m.SiteId ?? 0)}:{m.Site.Code}" : null);

    /// <summary>Doublons présents en base, hors ceux déclarés « pas un doublon ».</summary>
    public async Task<List<GroupeDoublon>> ListerAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lignes = (await db.Materiels.AsNoTracking()
            .Select(m => new
            {
                m.Id, m.NumInterne, m.Type, m.Modele, m.NumSerie, m.Statut, m.PersonneId, m.SiteId, Personne = m.Personne!.NomComplet,
                SiteCode = m.Site!.Code, SiteNom = m.Site.Nom, SiteRelie = m.Site.EntraObjectId != null,
            })
            .ToListAsync(ct))
            .Select(m => new LigneControle(m.Id, m.NumInterne, m.Type, m.Modele, m.NumSerie, m.Statut,
                m.PersonneId != null ? m.Personne : m.SiteId != null ? Site.LibelleDe(m.SiteCode, m.SiteNom, m.SiteRelie) : null,
                m.PersonneId != null ? $"P{m.PersonneId}" : m.SiteId != null ? $"S{m.SiteId}" : null))
            .ToList();
        var ignores = (await db.DoublonsIgnores.Select(d => d.Cle).ToListAsync(ct)).ToHashSet();
        return Analyser(lignes).Where(g => !ignores.Contains(g.Cle)).ToList();
    }

    public async Task IgnorerAsync(GroupeDoublon g, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstGestionnaire) throw new ErreurMetier("Réservé au service informatique.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.DoublonsIgnores.AnyAsync(d => d.Cle == g.Cle, ct))
            db.DoublonsIgnores.Add(new DoublonIgnore { Cle = g.Cle, Par = u.Signature, Le = DateTime.UtcNow });
        db.Journal.Add(new Journal { Date = DateTime.UtcNow, Utilisateur = u.Signature, Action = "Pas un doublon", Details = g.Titre });
        await db.SaveChangesAsync(ct);
    }
}
