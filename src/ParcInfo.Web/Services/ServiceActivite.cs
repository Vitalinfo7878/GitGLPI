using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Services;

/// <summary>Indicateurs d'activité du service informatique pour un mois (heure de Paris).</summary>
public sealed record IndicateursMois(
    DateOnly Mois,
    int TicketsCrees, int TicketsResolus, int TicketsOuvertsFin, double? PriseEnChargeHeures, double? ResolutionJours, double? Satisfaction,
    int DemandesRecues, int DemandesValidees, int DemandesRefusees, int DemandesRealisees, int DemandesEnAttenteFin, double? ValidationJours, double? RealisationJours,
    int ParcFin, int Achats, int Attributions, int Restitutions, int Rebuts, int Mouvements);

/// <summary>Détail du mois choisi : répartitions (graphiques en barres et rapport).</summary>
public sealed record DetailMois(List<Barre> TicketsParCategorie, List<Barre> DemandesParObjet, List<Barre> MouvementsParType, List<Barre> ParcParType);

/// <summary>Données brutes nécessaires au calcul (lues une fois, puis découpées par mois).</summary>
public sealed record DonneesActivite(
    List<DonneesActivite.T> Tickets, List<DonneesActivite.D> Demandes, List<DonneesActivite.M> Mouvements, List<DonneesActivite.P> Parc)
{
    public sealed record T(DateTime CreeLe, DateTime? PrisEnChargeLe, DateTime? ResoluLe, int? Note, string Categorie);
    public sealed record D(DateTime CreeLe, DateTime? DecisionLe, StatutValidation Validation, StatutRealisation Realisation, DateTime? RealiseeLe, string Objets);
    public sealed record M(DateOnly Date, TypeMouvement Type);
    /// <param name="SortiLe">Mise au rebut ou vol / perte : le matériel quitte le parc.</param>
    public sealed record P(DateTime CreeLe, TypeMateriel Type, DateOnly? SortiLe);
}

/// <summary>
/// Onglet « Indicateurs » et rapport d'activité mensuel : évolution du parc, tickets (hors tickets internes de rapatriement),
/// demandes informatiques et mouvements, mois par mois.
/// </summary>
public sealed class ServiceActivite(IDbContextFactory<ParcDbContext> dbFactory)
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Premier jour du mois (heure de Paris) d'un instant UTC.</summary>
    public static DateOnly MoisDe(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Paris);
        return new DateOnly(local.Year, local.Month, 1);
    }

    public static DateOnly MoisDe(DateOnly jour) => new(jour.Year, jour.Month, 1);

    public static DateOnly MoisCourant() => MoisDe(DateTime.UtcNow);

    public static string NomMois(DateOnly mois) => Fr.TextInfo.ToTitleCase(mois.ToString("MMMM yyyy", Fr));

    /// <summary>Fin du mois (exclue), en UTC.</summary>
    private static DateTime FinUtc(DateOnly mois) =>
        TimeZoneInfo.ConvertTimeToUtc(mois.AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Paris);

    public async Task<DonneesActivite> LireAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tickets = (await db.Tickets.AsNoTracking().Where(t => !t.Interne)
                .Select(t => new { t.CreeLe, t.PrisEnChargeLe, t.ResoluLe, t.SatisfactionNote, t.Categorie, t.CategorieAutre }).ToListAsync(ct))
            .Select(t => new DonneesActivite.T(t.CreeLe, t.PrisEnChargeLe, t.ResoluLe, t.SatisfactionNote,
                new Ticket { Categorie = t.Categorie, CategorieAutre = t.CategorieAutre }.LibelleCategorie)).ToList();
        var demandes = (await db.Demandes.AsNoTracking()
                .Select(d => new { d.CreeLe, d.DecisionLe, d.StatutValidation, d.StatutRealisation, d.RealiseeLe, d.Objets }).ToListAsync(ct))
            .Select(d => new DonneesActivite.D(d.CreeLe, d.DecisionLe, d.StatutValidation, d.StatutRealisation, d.RealiseeLe, d.Objets)).ToList();
        var mouvements = (await db.Mouvements.AsNoTracking().Select(m => new { m.Date, m.CreeLe, m.Type, m.MaterielId }).ToListAsync(ct));
        var sorties = mouvements.Where(m => m.Type is TypeMouvement.MiseAuRebut or TypeMouvement.VolPerte)
            .GroupBy(m => m.MaterielId).ToDictionary(g => g.Key, g => g.Min(m => m.Date ?? DateOnly.FromDateTime(m.CreeLe)));
        var parc = (await db.Materiels.AsNoTracking().Select(m => new { m.Id, m.CreeLe, m.Type, m.Statut }).ToListAsync(ct))
            .Select(m => new DonneesActivite.P(m.CreeLe, m.Type,
                // Rebuté ou perdu sans mouvement daté (import) : sorti dès son entrée
                sorties.TryGetValue(m.Id, out var s) ? s
                : m.Statut is StatutMateriel.Rebute or StatutMateriel.VolePerdu ? DateOnly.FromDateTime(m.CreeLe) : null)).ToList();
        return new DonneesActivite(tickets, demandes,
            mouvements.Select(m => new DonneesActivite.M(m.Date ?? DateOnly.FromDateTime(m.CreeLe), m.Type)).ToList(), parc);
    }

    // --- Calcul ---

    public static IndicateursMois Calculer(DonneesActivite d, DateOnly mois)
    {
        mois = MoisDe(mois);
        var fin = FinUtc(mois);
        var finJour = mois.AddMonths(1);
        bool Dans(DateTime? utc) => utc is { } u && MoisDe(u) == mois;
        static double? Moyenne(IEnumerable<double> v, int decimales = 1) => v.ToList() is { Count: > 0 } l ? Math.Round(l.Average(), decimales) : null;

        var resolus = d.Tickets.Where(t => Dans(t.ResoluLe)).ToList();
        var decisions = d.Demandes.Where(x => Dans(x.DecisionLe)).ToList();
        var realisees = d.Demandes.Where(x => x.Realisation == StatutRealisation.Realisee && Dans(x.RealiseeLe)).ToList();
        var mouvements = d.Mouvements.Where(m => MoisDe(m.Date) == mois).ToList();
        int Nombre(TypeMouvement t) => mouvements.Count(m => m.Type == t);

        return new IndicateursMois(
            mois,
            TicketsCrees: d.Tickets.Count(t => Dans(t.CreeLe)),
            TicketsResolus: resolus.Count,
            TicketsOuvertsFin: d.Tickets.Count(t => t.CreeLe < fin && (t.ResoluLe is null || t.ResoluLe >= fin)),
            PriseEnChargeHeures: Moyenne(d.Tickets.Where(t => Dans(t.CreeLe) && t.PrisEnChargeLe is not null).Select(t => (t.PrisEnChargeLe!.Value - t.CreeLe).TotalHours), 2),
            ResolutionJours: Moyenne(resolus.Select(t => (t.ResoluLe!.Value - t.CreeLe).TotalDays), 2),
            Satisfaction: Moyenne(resolus.Where(t => t.Note is not null).Select(t => (double)t.Note!.Value)),
            DemandesRecues: d.Demandes.Count(x => Dans(x.CreeLe)),
            DemandesValidees: decisions.Count(x => x.Validation == StatutValidation.Validee),
            DemandesRefusees: decisions.Count(x => x.Validation == StatutValidation.Refusee),
            DemandesRealisees: realisees.Count,
            DemandesEnAttenteFin: d.Demandes.Count(x => x.CreeLe < fin && (x.DecisionLe is null || x.DecisionLe >= fin)),
            ValidationJours: Moyenne(decisions.Select(x => (x.DecisionLe!.Value - x.CreeLe).TotalDays), 2),
            RealisationJours: Moyenne(realisees.Select(x => (x.RealiseeLe!.Value - x.CreeLe).TotalDays), 2),
            ParcFin: d.Parc.Count(p => p.CreeLe < fin && (p.SortiLe is null || p.SortiLe >= finJour)),
            Achats: Nombre(TypeMouvement.Achat),
            Attributions: Nombre(TypeMouvement.Attribution),
            Restitutions: Nombre(TypeMouvement.Restitution),
            Rebuts: Nombre(TypeMouvement.MiseAuRebut),
            Mouvements: mouvements.Count);
    }

    /// <summary>Les <paramref name="nombre"/> mois qui se terminent par <paramref name="dernier"/>, du plus ancien au plus récent.</summary>
    public static List<IndicateursMois> Evolution(DonneesActivite d, DateOnly dernier, int nombre = 12) =>
        Enumerable.Range(0, nombre).Select(i => Calculer(d, MoisDe(dernier).AddMonths(i - nombre + 1))).ToList();

    public static DetailMois Detail(DonneesActivite d, DateOnly mois)
    {
        mois = MoisDe(mois);
        var fin = FinUtc(mois);
        static List<Barre> Barres(IEnumerable<string> valeurs) =>
            valeurs.GroupBy(v => v).Select(g => new Barre(g.Key, g.Count())).OrderByDescending(b => b.Nombre).ThenBy(b => b.Libelle).ToList();
        return new DetailMois(
            Barres(d.Tickets.Where(t => MoisDe(t.CreeLe) == mois).Select(t => t.Categorie)),
            Barres(d.Demandes.Where(x => MoisDe(x.CreeLe) == mois)
                .SelectMany(x => x.Objets.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))),
            Barres(d.Mouvements.Where(m => MoisDe(m.Date) == mois).Select(m => m.Type.Libelle())),
            Barres(d.Parc.Where(p => p.CreeLe < fin && (p.SortiLe is null || p.SortiLe >= mois.AddMonths(1))).Select(p => p.Type.Libelle())));
    }

    // --- Rapport mensuel ---

    /// <summary>Rapport d'activité du mois au format Excel : synthèse (avec le mois précédent et les 12 derniers mois) et listes du mois.</summary>
    public async Task<byte[]> RapportAsync(DateOnly mois, Utilisateur u, CancellationToken ct = default)
    {
        if (!u.EstAdministrateur) throw new ErreurMetier("Réservé aux administrateurs principaux.");
        mois = MoisDe(mois);
        var donnees = await LireAsync(ct);
        var courant = Calculer(donnees, mois);
        var precedent = Calculer(donnees, mois.AddMonths(-1));
        var evolution = Evolution(donnees, mois);
        var detail = Detail(donnees, mois);
        var (debutUtc, finUtc) = (FinUtc(mois.AddMonths(-1)), FinUtc(mois));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tickets = await db.Tickets.AsNoTracking().Where(t => !t.Interne && t.CreeLe >= debutUtc && t.CreeLe < finUtc).OrderBy(t => t.Numero).ToListAsync(ct);
        var demandes = await db.Demandes.AsNoTracking().Where(x => x.CreeLe >= debutUtc && x.CreeLe < finUtc).OrderBy(x => x.Numero).ToListAsync(ct);
        var (debutJour, finJour) = (mois, mois.AddMonths(1));
        var mouvements = await db.Mouvements.AsNoTracking().Include(m => m.Materiel).Include(m => m.Fiche)
            .Include(m => m.DestinationSite).Include(m => m.DestinationPersonne)
            .Where(m => (m.Date ?? DateOnly.FromDateTime(m.CreeLe)) >= debutJour && (m.Date ?? DateOnly.FromDateTime(m.CreeLe)) < finJour)
            .OrderBy(m => m.Numero).ToListAsync(ct);

        using var wb = new XLWorkbook();
        var nom = NomMois(mois);

        // Synthèse
        var ws = wb.AddWorksheet("Synthèse");
        ws.Cell(1, 1).Value = $"Rapport d'activité du service informatique – {nom}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"Généré le {TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Paris):dd/MM/yyyy à HH:mm} par {u.Nom}. Tickets : hors tickets internes de rapatriement.";
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;
        var ligne = 4;
        void Entete(params string[] titres)
        {
            for (var i = 0; i < titres.Length; i++) ws.Cell(ligne, i + 1).Value = titres[i];
            var plage = ws.Range(ligne, 1, ligne, titres.Length);
            plage.Style.Font.Bold = true;
            plage.Style.Fill.BackgroundColor = XLColor.FromHtml("#233D8E");
            plage.Style.Font.FontColor = XLColor.White;
            ligne++;
        }
        void Section(string titre)
        {
            ws.Cell(ligne, 1).Value = titre;
            ws.Cell(ligne, 1).Style.Font.Bold = true;
            ws.Cell(ligne, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2FA");
            ws.Range(ligne, 1, ligne, 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2FA");
            ligne++;
        }
        void Valeur(string libelle, double? actuel, double? avant)
        {
            ws.Cell(ligne, 1).Value = libelle;
            if (actuel is { } a) ws.Cell(ligne, 2).Value = a;
            if (avant is { } b) ws.Cell(ligne, 3).Value = b;
            if (actuel is { } x && avant is { } y) ws.Cell(ligne, 4).Value = Math.Round(x - y, 1);
            ligne++;
        }
        Entete("Indicateur", nom, NomMois(mois.AddMonths(-1)), "Évolution");
        Section("Parc");
        Valeur("Matériel en service (fin de mois)", courant.ParcFin, precedent.ParcFin);
        Valeur("Achats (matériels entrés en stock)", courant.Achats, precedent.Achats);
        Valeur("Attributions", courant.Attributions, precedent.Attributions);
        Valeur("Entrées dans le SI (restitutions)", courant.Restitutions, precedent.Restitutions);
        Valeur("Mises au rebut", courant.Rebuts, precedent.Rebuts);
        Valeur("Mouvements (tous types)", courant.Mouvements, precedent.Mouvements);
        Section("Tickets");
        Valeur("Tickets créés", courant.TicketsCrees, precedent.TicketsCrees);
        Valeur("Tickets résolus", courant.TicketsResolus, precedent.TicketsResolus);
        Valeur("Tickets ouverts (fin de mois)", courant.TicketsOuvertsFin, precedent.TicketsOuvertsFin);
        Valeur("Délai moyen de prise en charge (heures)", courant.PriseEnChargeHeures, precedent.PriseEnChargeHeures);
        Valeur("Délai moyen de résolution (jours)", courant.ResolutionJours, precedent.ResolutionJours);
        Valeur("Satisfaction moyenne (sur 5)", courant.Satisfaction, precedent.Satisfaction);
        Section("Demandes informatiques");
        Valeur("Demandes reçues", courant.DemandesRecues, precedent.DemandesRecues);
        Valeur("Demandes validées", courant.DemandesValidees, precedent.DemandesValidees);
        Valeur("Demandes refusées", courant.DemandesRefusees, precedent.DemandesRefusees);
        Valeur("Demandes réalisées", courant.DemandesRealisees, precedent.DemandesRealisees);
        Valeur("En attente de validation (fin de mois)", courant.DemandesEnAttenteFin, precedent.DemandesEnAttenteFin);
        Valeur("Délai moyen de validation (jours)", courant.ValidationJours, precedent.ValidationJours);
        Valeur("Délai moyen de réalisation (jours)", courant.RealisationJours, precedent.RealisationJours);

        // Douze derniers mois
        ligne += 2;
        ws.Cell(ligne++, 1).Value = "Évolution sur 12 mois";
        ws.Cell(ligne - 1, 1).Style.Font.Bold = true;
        Entete("Mois", "Parc en service", "Tickets créés", "Tickets résolus", "Demandes reçues", "Demandes réalisées", "Achats", "Attributions", "Rebuts");
        foreach (var m in evolution)
        {
            object[] v = [NomMois(m.Mois), m.ParcFin, m.TicketsCrees, m.TicketsResolus, m.DemandesRecues, m.DemandesRealisees, m.Achats, m.Attributions, m.Rebuts];
            for (var i = 0; i < v.Length; i++) ws.Cell(ligne, i + 1).Value = XLCellValue.FromObject(v[i]);
            ligne++;
        }
        ws.Columns().AdjustToContents();
        ws.Column(1).Width = 42;

        // Répartitions du mois
        var wr = wb.AddWorksheet("Répartitions");
        var col = 1;
        foreach (var (titre, barres) in new[] { ("Tickets par type", detail.TicketsParCategorie), ("Demandes par objet", detail.DemandesParObjet),
                     ("Mouvements par type", detail.MouvementsParType), ("Parc en service par type", detail.ParcParType) })
        {
            wr.Cell(1, col).Value = titre;
            wr.Cell(1, col).Style.Font.Bold = true;
            for (var i = 0; i < barres.Count; i++) { wr.Cell(i + 2, col).Value = barres[i].Libelle; wr.Cell(i + 2, col + 1).Value = barres[i].Nombre; }
            col += 3;
        }
        wr.Columns().AdjustToContents();

        static string? Local(DateTime? utc) => utc is { } u ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(u, DateTimeKind.Utc), Paris).ToString("dd/MM/yyyy HH:mm") : null;
        void Liste<T>(string feuille, string[] titres, IEnumerable<T> lignes, Func<T, object?[]> valeurs)
        {
            var f = wb.AddWorksheet(feuille);
            for (var i = 0; i < titres.Length; i++) f.Cell(1, i + 1).Value = titres[i];
            var r = 2;
            foreach (var l in lignes)
            {
                var v = valeurs(l);
                for (var i = 0; i < v.Length; i++) f.Cell(r, i + 1).Value = XLCellValue.FromObject(v[i]);
                r++;
            }
            var t = f.Range(1, 1, Math.Max(r - 1, 1), titres.Length).CreateTable(feuille.Replace(" ", ""));
            t.Theme = XLTableTheme.TableStyleLight9;
            f.Columns().AdjustToContents();
            foreach (var c in f.ColumnsUsed()) if (c.Width > 60) c.Width = 60;
        }
        Liste("Tickets", ["N°", "Ouvert le", "Type de problème", "Problème", "Lieu", "Demandeur", "Priorité", "Statut", "Pris en charge le", "Résolu le", "Note"],
            tickets, t => [t.Numero, Local(t.CreeLe), t.LibelleCategorie, t.Titre, t.Lieu, t.DemandeurNom, t.Priorite.Libelle(), t.Statut.Libelle(),
                Local(t.PrisEnChargeLe), Local(t.ResoluLe), t.SatisfactionNote]);
        Liste("Demandes", ["N°", "Reçue le", "Demandeur", "Objet", "Concerne", "Validation", "Décision le", "Avancement", "Réalisée le"],
            demandes, x => [x.Numero, Local(x.CreeLe), x.DemandeurNom, x.Objets, x.Concerne, x.StatutValidation.Libelle(), Local(x.DecisionLe),
                x.StatutRealisation.Libelle(), Local(x.RealiseeLe)]);
        Liste("Mouvements", ["N°", "Date", "Type", "N° interne", "Matériel", "Destination", "Fiche"],
            mouvements, m => [m.Numero, m.Date?.ToString("dd/MM/yyyy") ?? Local(m.CreeLe), m.Type.Libelle(), m.Materiel.NumInterne,
                $"{m.Materiel.Type.Libelle()} {m.Materiel.Modele}".Trim(), m.DestinationLibelle, m.Fiche?.Code]);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        db.Journal.Add(new Journal { Date = DateTime.UtcNow, Utilisateur = u.Signature, Action = "Rapport d'activité exporté", Details = nom });
        await db.SaveChangesAsync(ct);
        return ms.ToArray();
    }
}
