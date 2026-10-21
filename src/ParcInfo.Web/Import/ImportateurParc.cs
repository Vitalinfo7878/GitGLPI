using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Data;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Import;

public sealed record Anomalie(string Feuille, int Ligne, string Message);

public sealed class RapportImport
{
    public int Materiels { get; set; }
    public int Sites { get; set; }
    public int Personnes { get; set; }
    public int Regions { get; set; }
    public int Mouvements { get; set; }
    public List<Anomalie> Anomalies { get; } = [];
    public bool Enregistre { get; set; }

    public void Signaler(string feuille, int ligne, string message) => Anomalies.Add(new(feuille, ligne, message));
}

/// <summary>
/// Transforme les lignes de l'Excel en matériels, sites, personnes et mouvements.
/// L'import n'est possible que sur une base vide (premier chargement).
/// </summary>
public sealed class ImportateurParc
{
    private const string Inv = LecteurExcel.FeuilleInventaire;
    private const string Mvt = LecteurExcel.FeuilleMouvement;

    private readonly RapportImport _rapport = new();
    private readonly Dictionary<string, Region> _regions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Site> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Personne> _personnes = new();
    private readonly Dictionary<string, Materiel> _materiels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Site, List<(string? Profil, string? Tel, string? Resp)>> _infosSites = [];
    private HashSet<string> _nomsRegions = [];

    public static TypeMouvement? TypeDepuisExcel(string? valeur)
    {
        var v = Normalisation.Texte(valeur);
        if (v is null) return null;
        var k = Normalisation.SansAccents(v).ToUpperInvariant();
        if (k.StartsWith("SORTIE INITIALE")) return TypeMouvement.SortieInitiale;
        if (k.StartsWith("SORTIE")) return TypeMouvement.Attribution;
        if (k.StartsWith("ENTREE (SI)")) return TypeMouvement.EntreeInitiale;
        if (k.StartsWith("ENTRE")) return TypeMouvement.Restitution;
        if (k.StartsWith("ACHAT")) return TypeMouvement.Achat;
        if (k.Contains("REBUT")) return TypeMouvement.MiseAuRebut;
        return null;
    }

    /// <summary>Construit les entités sans rien enregistrer (utile pour l'aperçu et les tests).</summary>
    public (RapportImport Rapport, List<Materiel> Materiels) Construire(
        IReadOnlyList<LigneInventaire> inventaire, IReadOnlyList<LigneMouvement> mouvements, string utilisateur)
    {
        var maintenant = DateTime.UtcNow;
        _nomsRegions = inventaire.Select(l => Normalisation.Region(l.ServiceLieu))
            .Concat(mouvements.Select(m => Normalisation.Region(m.Region)))
            .OfType<string>().ToHashSet();

        var dernierType = mouvements
            .Select(m => (Num: Normalisation.NumInterne(m.NumInterne), Type: TypeDepuisExcel(m.Type), Date: Normalisation.Date(m.Date), Ordre: m.Ligne))
            .Where(x => x.Num != null && x.Type != null)
            .GroupBy(x => x.Num!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Date ?? DateOnly.MinValue).ThenBy(x => x.Ordre).Last().Type!.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var l in inventaire)
            ImporterLigneInventaire(l, dernierType, maintenant);

        ImporterMouvements(mouvements, utilisateur, maintenant);
        CompleterSites();
        SignalerDoublonsProbables();

        _rapport.Materiels = _materiels.Count;
        _rapport.Sites = _sites.Count;
        _rapport.Personnes = _personnes.Count;
        _rapport.Regions = _regions.Count;
        _rapport.Mouvements = _materiels.Values.Sum(m => m.Mouvements.Count);
        return (_rapport, _materiels.Values.ToList());
    }

    public async Task<RapportImport> ImporterAsync(ParcDbContext db, IReadOnlyList<LigneInventaire> inventaire,
        IReadOnlyList<LigneMouvement> mouvements, string utilisateur, CancellationToken ct = default)
    {
        if (await db.Materiels.AnyAsync(ct))
            throw new InvalidOperationException("La base contient déjà du matériel : l'import Excel n'est possible que sur une base vide.");

        var (rapport, materiels) = Construire(inventaire, mouvements, utilisateur);
        db.Materiels.AddRange(materiels);
        // Sites et personnes sans matériel actuel (présents seulement dans l'historique des mouvements)
        db.Sites.AddRange(_sites.Values);
        db.Personnes.AddRange(_personnes.Values);
        db.Journal.Add(new Journal
        {
            Date = DateTime.UtcNow, Utilisateur = utilisateur, Action = "Import Excel",
            Details = $"{rapport.Materiels} matériels, {rapport.Mouvements} mouvements, {rapport.Sites} sites, {rapport.Personnes} personnes, {rapport.Anomalies.Count} anomalies",
        });
        await db.SaveChangesAsync(ct);
        rapport.Enregistre = true;
        return rapport;
    }

    private void ImporterLigneInventaire(LigneInventaire l, Dictionary<string, TypeMouvement> dernierType, DateTime maintenant)
    {
        var num = Normalisation.NumInterne(l.NumInterne);
        if (num is null)
        {
            num = $"SANS-NUM-L{l.Ligne}";
            _rapport.Signaler(Inv, l.Ligne, $"Pas de numéro interne : numéro provisoire {num} attribué.");
        }
        if (_materiels.ContainsKey(num))
        {
            var nouveau = $"{num}-DOUBLON-L{l.Ligne}";
            _rapport.Signaler(Inv, l.Ligne, $"Numéro interne {num} en double : renommé {nouveau}.");
            num = nouveau;
        }

        var type = Normalisation.TypeMateriel(l.Appareil, num);
        if (type is null)
            _rapport.Signaler(Inv, l.Ligne, $"Type d'appareil inconnu (« {l.Appareil} ») : classé « PC fixe » par défaut.");

        var m = new Materiel
        {
            NumInterne = num,
            Type = type ?? TypeMateriel.PcFixe,
            Modele = Normalisation.Texte(l.Modele),
            NumSerie = Normalisation.Texte(l.NumSerie),
            Configuration = Normalisation.Texte(l.Configuration),
            AnneeAcquisition = Normalisation.Annee(l.Annee),
            DateMiseEnPlace = Normalisation.Date(l.DateMiseEnPlace),
            ProfilMicrosoft = Normalisation.Texte(l.ProfilMicrosoft)?.ToLowerInvariant(),
            LicenceMicrosoft = Normalisation.Licence(l.Licence),
            OfficeEnDur = Normalisation.OuiNon(l.OfficeEnDur),
            Note = Normalisation.Note(l.Note),
            MotDePasseSession = Normalisation.Texte(l.MotDePasse),
            ConnexionAdmin = Normalisation.OuiNon(l.ConnexionAdmin),
            VersionWindows = Normalisation.VersionWindows(l.VersionWindows),
            AChanger = Normalisation.OuiNon(l.AChanger) == true,
            EditionWindows = Normalisation.EditionWindows(l.EditionWindows),
            Labtech = Normalisation.OuiNon(l.Labtech),
            Commentaire = Normalisation.Texte(l.Commentaire),
            CleOffice = Normalisation.Texte(l.CleOffice),
            NumImmo = Normalisation.Texte(l.NumImmo),
            NumFacture = Normalisation.Texte(l.NumFacture),
            CreeLe = maintenant,
            ModifieLe = maintenant,
        };
        _materiels[num] = m;

        var lieu = Normalisation.Region(l.ServiceLieu);
        var statutLieu = Normalisation.StatutDepuisLieu(lieu);
        if (Normalisation.Texte(l.Detenteur) is { } d && Normalisation.SansAccents(d).ToUpperInvariant() == "REBUT")
            statutLieu = StatutMateriel.Rebute;
        if (statutLieu is null)
            m.Region = RegionPour(lieu);

        var det = Detenteur.Classer(l.Detenteur, _nomsRegions);
        dernierType.TryGetValue(num, out var dernier);

        m.Statut = statutLieu ?? StatutMateriel.Attribue;
        if (statutLieu is not null and not StatutMateriel.Rebute && dernier == TypeMouvement.MiseAuRebut)
            _rapport.Signaler(Inv, l.Ligne, $"{num} : lieu « {l.ServiceLieu} » mais dernier mouvement « MISE AU REBUT » : classé {m.Statut.Libelle()}, à vérifier.");
        if (statutLieu is null && dernier == TypeMouvement.MiseAuRebut)
        {
            m.Statut = StatutMateriel.Rebute;
            _rapport.Signaler(Inv, l.Ligne, $"{num} : dernier mouvement « MISE AU REBUT » mais lieu « {l.ServiceLieu} » : classé Rebuté.");
        }

        if (m.Statut == StatutMateriel.Attribue)
        {
            AffecterDetenteur(m, det, l, lieu);
            if (m.SiteId is null && m.Site is null && m.Personne is null)
                _rapport.Signaler(Inv, l.Ligne, $"{num} : attribué sans site ni personne (colonne « SITES / UTILISATEURS » = « {l.Detenteur} »).");
        }
        else if (det.Valeur is not null)
        {
            // En stock / au rebut : on ne garde pas de détenteur, mais on note le dernier connu.
            m.Commentaire = Joindre(m.Commentaire, $"Dernier détenteur connu (Excel) : {det.Valeur}");
        }
        if (det.Remarque is not null)
        {
            m.Commentaire = Joindre(m.Commentaire, det.Remarque);
            if (det.Type == TypeDetenteur.Aucun)
                _rapport.Signaler(Inv, l.Ligne, $"{num} : {det.Remarque}.");
        }
    }

    private void AffecterDetenteur(Materiel m, Detenteur det, LigneInventaire l, string? lieu)
    {
        switch (det.Type)
        {
            case TypeDetenteur.Site:
                var site = SitePour(det.Valeur!);
                m.Site = site;
                site.Region ??= m.Region;
                if (m.EstUnPc) m.CategoriePc = CategoriePc.Site;
                if (!_infosSites.TryGetValue(site, out var infos)) _infosSites[site] = infos = [];
                infos.Add((m.EstUnPc ? m.ProfilMicrosoft : null, Normalisation.Texte(l.Telephone), Normalisation.Texte(l.ResponsableSite)));
                break;
            case TypeDetenteur.Personne:
                var p = PersonnePour(det.Valeur!);
                m.Personne = p;
                p.Region ??= m.Region;
                if (m.EstUnPc) m.CategoriePc = CategoriePc.Enrole;
                break;
        }
    }

    private void ImporterMouvements(IReadOnlyList<LigneMouvement> lignes, string utilisateur, DateTime maintenant)
    {
        var sansNumero = new List<Mouvement>();
        var numeros = new HashSet<int>();
        var crees = new HashSet<Materiel>();

        foreach (var l in lignes)
        {
            var num = Normalisation.NumInterne(l.NumInterne)!;
            var type = TypeDepuisExcel(l.Type);
            if (type is null)
            {
                _rapport.Signaler(Mvt, l.Ligne, $"Type de mouvement inconnu (« {l.Type} ») : ligne ignorée.");
                continue;
            }

            if (!_materiels.TryGetValue(num, out var materiel))
            {
                materiel = new Materiel
                {
                    NumInterne = num,
                    Type = Normalisation.TypeMateriel(l.Appareil, num) ?? TypeMateriel.PcFixe,
                    Modele = Normalisation.Texte(l.Modele),
                    NumSerie = Normalisation.Texte(l.NumSerie),
                    CreeLe = maintenant,
                    ModifieLe = maintenant,
                    Commentaire = "Matériel absent de la feuille INVENTAIRE, créé à partir de la feuille MOUVEMENT.",
                };
                _materiels[num] = materiel;
                crees.Add(materiel);
                _rapport.Signaler(Mvt, l.Ligne, $"{num} absent de l'inventaire : matériel créé à partir du mouvement.");
            }

            var mv = new Mouvement
            {
                Materiel = materiel,
                Type = type.Value,
                Date = Normalisation.Date(l.Date),
                Expediteur = Normalisation.Texte(l.Expediteur),
                Region = Normalisation.Region(l.Region),
                DocumentReference = Normalisation.Texte(l.DocumentReference),
                DateReception = Normalisation.Date(l.DateReception),
                CreePar = $"Import Excel ({utilisateur})",
                CreeLe = maintenant,
            };
            if (Normalisation.Texte(l.Type) is { } t && t.Contains('(') && type == TypeMouvement.Attribution)
                mv.Commentaire = $"Type d'origine : {t}";

            var dest = Detenteur.Classer(l.Destination, _nomsRegions);
            switch (dest.Type)
            {
                case TypeDetenteur.Site: mv.DestinationSite = SitePour(dest.Valeur!); break;
                case TypeDetenteur.Personne: mv.DestinationPersonne = PersonnePour(dest.Valeur!); break;
                default: mv.DestinationLibre = Normalisation.Texte(l.Destination); break;
            }
            if (Normalisation.Texte(l.DestinataireEmail) is { } email)
                mv.Commentaire = Joindre(mv.Commentaire, $"Destinataire : {email}");

            switch (l.Numero)
            {
                case double d when d == Math.Floor(d) && numeros.Add((int)d):
                    mv.Numero = (int)d;
                    break;
                default:
                    mv.NumeroOrigine = Normalisation.Texte(Convert.ToString(l.Numero, System.Globalization.CultureInfo.InvariantCulture));
                    sansNumero.Add(mv);
                    break;
            }
            if (mv.Date is null)
                _rapport.Signaler(Mvt, l.Ligne, $"Mouvement {l.Numero} ({num}) sans date.");
            materiel.Mouvements.Add(mv);
        }

        // Les numéros non numériques (« E748 »…) reçoivent un numéro à la suite, l'original est conservé.
        var prochain = numeros.Count == 0 ? 1 : numeros.Max() + 1;
        foreach (var mv in sansNumero)
        {
            mv.Numero = prochain++;
            if (mv.NumeroOrigine is not null)
                _rapport.Signaler(Mvt, 0, $"Mouvement « {mv.NumeroOrigine} » ({mv.Materiel.NumInterne}) renuméroté {mv.Numero}.");
        }

        // Statut des matériels créés depuis la feuille MOUVEMENT : déduit de leur dernier mouvement
        foreach (var m in crees)
        {
            var dernier = m.Mouvements.OrderBy(x => x.Date ?? DateOnly.MinValue).ThenBy(x => x.Numero).Last();
            var regle = RegleMouvement.Pour(dernier.Type);
            m.Statut = Normalisation.StatutDepuisLieu(dernier.Region) ?? regle.StatutResultant;
            if (regle.DestinationEstDetenteur && m.Statut == StatutMateriel.Attribue)
            {
                m.Site = dernier.DestinationSite;
                m.Personne = dernier.DestinationPersonne;
            }
            m.Region = RegionPour(dernier.Region);
        }
    }

    /// <summary>Téléphone, responsable et boîte mail du site : valeur la plus fréquente sur ses lignes d'inventaire.</summary>
    private void CompleterSites()
    {
        foreach (var (site, infos) in _infosSites)
        {
            site.Email ??= PlusFrequent(infos.Select(i => i.Profil));
            site.Telephone ??= PlusFrequent(infos.Select(i => i.Tel));
            site.Responsable ??= PlusFrequent(infos.Select(i => i.Resp));
        }
    }

    /// <summary>Signale les personnes dont les noms sont presque identiques (« Gisela DA SILVA » / « Gisela DASILVA »).</summary>
    private void SignalerDoublonsProbables()
    {
        var noms = _personnes.Values
            .Select(p => (p.NomComplet, Lettres: new string(Normalisation.SansAccents(p.NomComplet).ToLowerInvariant().Where(char.IsLetter).ToArray())))
            .Where(x => x.Lettres.Length >= 8)
            .ToList();
        for (var i = 0; i < noms.Count; i++)
            for (var j = i + 1; j < noms.Count; j++)
                if (Math.Abs(noms[i].Lettres.Length - noms[j].Lettres.Length) <= 2 && Distance(noms[i].Lettres, noms[j].Lettres) <= 2)
                    _rapport.Signaler("PERSONNES", 0, $"Doublon probable : « {noms[i].NomComplet} » et « {noms[j].NomComplet} ».");
    }

    private static int Distance(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) d[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prec = d[0];
            d[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prec + (a[i - 1] == b[j - 1] ? 0 : 1));
                prec = tmp;
            }
        }
        return d[b.Length];
    }

    private static string? PlusFrequent(IEnumerable<string?> valeurs) =>
        valeurs.OfType<string>().GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    private Region? RegionPour(string? nomNormalise)
    {
        if (nomNormalise is null || Normalisation.StatutDepuisLieu(nomNormalise) is not null) return null;
        if (!_regions.TryGetValue(nomNormalise, out var r))
            _regions[nomNormalise] = r = new Region { Nom = nomNormalise };
        return r;
    }

    private Site SitePour(string code)
    {
        if (!_sites.TryGetValue(code, out var s))
            _sites[code] = s = new Site { Code = code };
        return s;
    }

    private Personne PersonnePour(string nom)
    {
        var cle = Normalisation.ClePersonne(nom);
        if (!_personnes.TryGetValue(cle, out var p))
            _personnes[cle] = p = new Personne { NomComplet = nom };
        return p;
    }

    private static string Joindre(string? existant, string ajout) =>
        string.IsNullOrWhiteSpace(existant) ? ajout : $"{existant}\n{ajout}";
}
