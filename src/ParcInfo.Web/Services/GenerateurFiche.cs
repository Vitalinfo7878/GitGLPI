using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using ParcInfo.Web.Domain;
using PdfSharp.Fonts;

namespace ParcInfo.Web.Services;

/// <summary>
/// Génère le PDF d'une fiche (attribution, restitution, achat, mise au rebut).
/// Le mot de passe n'est volontairement pas imprimé : il s'affiche dans l'application après confirmation de réception.
/// </summary>
public sealed class GenerateurFiche
{
    private const string Police = "DejaVu Sans";
    private static readonly Color Bleu = new(0x23, 0x3d, 0x8e);
    private static readonly Color Gris = new(0xf1, 0xf3, 0xf8);
    private static readonly Lazy<string> Logo = new(() => "base64:" + Convert.ToBase64String(Ressources.Lire("logo-vitalrest.jpg")));

    static GenerateurFiche()
    {
        GlobalFontSettings.FontResolver ??= new ResolveurPolices();
    }

    public static string Titre(TypeFiche type) => type switch
    {
        TypeFiche.Attribution => "FICHE D'ATTRIBUTION DU MATÉRIEL INFORMATIQUE",
        TypeFiche.Restitution => "FICHE DE RESTITUTION DU MATÉRIEL INFORMATIQUE",
        TypeFiche.Achat => "FICHE D'ACHAT DU MATÉRIEL INFORMATIQUE",
        TypeFiche.MiseAuRebut => "FICHE DE MISE AU REBUT DU MATÉRIEL INFORMATIQUE",
        _ => "FICHE MATÉRIEL INFORMATIQUE",
    };

    /// <param name="fiche">Fiche avec ses mouvements et leurs matériels chargés.</param>
    public byte[] Generer(Fiche fiche)
    {
        var doc = new Document();
        doc.Info.Title = fiche.Code;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Police;
        normal.Font.Size = 10;

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        var logo = section.AddImage(Logo.Value);
        logo.Width = Unit.FromCentimeter(6);
        logo.LockAspectRatio = true;

        var titre = section.AddParagraph(Titre(fiche.Type));
        titre.Format.Font.Size = 15;
        titre.Format.Font.Bold = true;
        titre.Format.Font.Color = Bleu;
        titre.Format.SpaceBefore = Unit.FromCentimeter(0.6);
        titre.Format.SpaceAfter = Unit.FromCentimeter(0.6);

        var premier = fiche.Mouvements.OrderBy(m => m.Numero).First();
        var infos = section.AddTable();
        infos.Borders.Width = 0;
        infos.AddColumn(Unit.FromCentimeter(5.5));
        infos.AddColumn(Unit.FromCentimeter(11.5));
        void Info(string libelle, string? valeur)
        {
            var r = infos.AddRow();
            r.TopPadding = r.BottomPadding = Unit.FromPoint(3);
            r.Cells[0].AddParagraph(libelle).Format.Font.Bold = true;
            r.Cells[1].AddParagraph(valeur ?? "");
        }
        Info("N° de fiche", fiche.Code);
        Info("Date", premier.Date?.ToString("dd/MM/yyyy"));
        Info(fiche.Type == TypeFiche.MiseAuRebut ? "Document adressé à" : "Destinataire", fiche.DestinataireNom ?? premier.DestinationLibelle);
        if (!string.IsNullOrEmpty(fiche.DestinataireEmail)) Info("Adresse mail", fiche.DestinataireEmail);
        Info("Envoyé par", premier.Expediteur);
        Info("Région / service", premier.Region);
        if (!string.IsNullOrEmpty(premier.DocumentReference)) Info("Document de référence", premier.DocumentReference);
        if (!string.IsNullOrEmpty(premier.Commentaire)) Info(fiche.Type == TypeFiche.MiseAuRebut ? "Raison" : "Commentaire", premier.Commentaire);

        var st = section.AddParagraph("Matériel");
        st.Format.Font.Bold = true;
        st.Format.Font.Size = 12;
        st.Format.Font.Color = Bleu;
        st.Format.SpaceBefore = Unit.FromCentimeter(0.6);
        st.Format.SpaceAfter = Unit.FromCentimeter(0.2);

        var t = section.AddTable();
        t.Borders.Width = 0.5;
        t.Borders.Color = new Color(0xc8, 0xcd, 0xd8);
        foreach (var largeur in new[] { 1.6, 2.7, 2.7, 5.0, 3.5, 1.5 })
            t.AddColumn(Unit.FromCentimeter(largeur));
        var entete = t.AddRow();
        entete.HeadingFormat = true;
        entete.Shading.Color = Gris;
        entete.Format.Font.Bold = true;
        string[] colonnes = ["Mvt", "N° interne", "Type", "Modèle", "N° de série", "Année"];
        for (var i = 0; i < colonnes.Length; i++) entete.Cells[i].AddParagraph(colonnes[i]);
        foreach (var m in fiche.Mouvements.OrderBy(m => m.Numero))
        {
            var r = t.AddRow();
            r.TopPadding = r.BottomPadding = Unit.FromPoint(2);
            r.Cells[0].AddParagraph(m.Numero.ToString());
            r.Cells[1].AddParagraph(m.Materiel.NumInterne);
            r.Cells[2].AddParagraph(m.Materiel.Type.Libelle());
            r.Cells[3].AddParagraph(m.Materiel.Modele ?? "");
            r.Cells[4].AddParagraph(m.Materiel.NumSerie ?? "");
            r.Cells[5].AddParagraph(m.Materiel.AnneeAcquisition?.ToString() ?? "");
        }

        if (fiche.Type == TypeFiche.Attribution)
        {
            var p = section.AddParagraph();
            p.Format.SpaceBefore = Unit.FromCentimeter(0.6);
            p.AddFormattedText("Merci de bien vouloir nous renvoyer l'ancien matériel.", TextFormat.Bold);
        }

        AjouterReception(section, fiche);
        AjouterSignatures(section, fiche);

        var pied = section.Footers.Primary.AddParagraph($"{fiche.Code} – généré le {DateTime.Now:dd/MM/yyyy HH:mm}");
        pied.Format.Font.Size = 7;
        pied.Format.Font.Color = Colors.Gray;

        var rendu = new PdfDocumentRenderer { Document = doc };
        rendu.RenderDocument();
        using var ms = new MemoryStream();
        rendu.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static void AjouterReception(Section section, Fiche fiche)
    {
        if (fiche.StatutReception == StatutReception.NonDemandee) return;
        var p = section.AddParagraph();
        p.Format.SpaceBefore = Unit.FromCentimeter(0.6);
        p.Format.Shading.Color = fiche.StatutReception == StatutReception.Confirmee ? new Color(0xe8, 0xf5, 0xe9) : new Color(0xff, 0xf4, 0xe5);
        p.Format.Borders.Distance = Unit.FromPoint(5);
        if (fiche.StatutReception == StatutReception.Confirmee)
        {
            p.AddFormattedText("Réception confirmée électroniquement", TextFormat.Bold);
            p.AddText($" le {fiche.ReceptionConfirmeeLe?.ToLocalTime():dd/MM/yyyy à HH:mm} par {fiche.ReceptionConfirmeePar} ({fiche.ReceptionConfirmeeParEmail}).");
        }
        else
        {
            p.AddFormattedText("Réception en attente de confirmation", TextFormat.Bold);
            p.AddText($" par {fiche.DestinataireNom} ({fiche.DestinataireEmail}).");
        }
    }

    private static void AjouterSignatures(Section section, Fiche fiche)
    {
        // Une attribution confirmée électroniquement n'a plus besoin de signature manuscrite.
        if (fiche.StatutReception == StatutReception.Confirmee) return;
        var t = section.AddTable();
        t.Format.SpaceBefore = Unit.FromCentimeter(1);
        t.Borders.Width = 0.5;
        t.Borders.Color = new Color(0xc8, 0xcd, 0xd8);
        t.AddColumn(Unit.FromCentimeter(8.5));
        t.AddColumn(Unit.FromCentimeter(8.5));
        var titres = t.AddRow();
        titres.Shading.Color = Gris;
        titres.Format.Font.Bold = true;
        titres.Cells[0].AddParagraph("Signature service informatique");
        titres.Cells[1].AddParagraph(fiche.Type == TypeFiche.MiseAuRebut ? "Signature DAF" : "Signature destinataire");
        var zone = t.AddRow();
        zone.Height = Unit.FromCentimeter(3);
    }
}

/// <summary>Polices embarquées dans l'application (indispensable sous Linux / Azure App Service).</summary>
internal sealed class ResolveurPolices : IFontResolver
{
    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? "DejaVuSans-Bold" : "DejaVuSans", false, italic);

    public byte[] GetFont(string faceName) => Ressources.Lire($"Fonts.{faceName}.ttf");
}

internal static class Ressources
{
    public static byte[] Lire(string nom)
    {
        var asm = typeof(Ressources).Assembly;
        using var s = asm.GetManifestResourceStream($"ParcInfo.Web.Resources.{nom}")
            ?? throw new FileNotFoundException($"Ressource introuvable : {nom}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
