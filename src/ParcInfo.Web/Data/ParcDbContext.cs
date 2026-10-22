using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Domain;
using ParcInfo.Web.Services;

namespace ParcInfo.Web.Data;

public class ParcDbContext(DbContextOptions<ParcDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Personne> Personnes => Set<Personne>();
    public DbSet<Materiel> Materiels => Set<Materiel>();
    public DbSet<Mouvement> Mouvements => Set<Mouvement>();
    public DbSet<Fiche> Fiches => Set<Fiche>();
    public DbSet<Courriel> Courriels => Set<Courriel>();
    public DbSet<Journal> Journal => Set<Journal>();
    public DbSet<Demande> Demandes => Set<Demande>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<CompteMicrosoft> ComptesMicrosoft => Set<CompteMicrosoft>();
    public DbSet<TicketMessage> MessagesTickets => Set<TicketMessage>();
    /// <summary>Clés de chiffrement des cookies de connexion : conservées en base pour survivre aux redémarrages du conteneur.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
        builder.Properties<string>().HaveMaxLength(400);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Region>(e =>
        {
            e.Property(x => x.Nom).HasMaxLength(100);
            e.HasIndex(x => x.Nom).IsUnique();
        });

        b.Entity<Site>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(50);
            e.HasIndex(x => x.Code).IsUnique();
            e.Ignore(x => x.Libelle);
            e.HasOne(x => x.CompteMicrosoft).WithMany().HasForeignKey(x => x.EntraObjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.EntraObjectId).IsUnique();
        });

        b.Entity<Personne>(e =>
        {
            e.Property(x => x.NomComplet).HasMaxLength(200);
            e.HasIndex(x => x.NomComplet);
            e.HasIndex(x => x.Email);
            e.HasOne(x => x.CompteMicrosoft).WithMany().HasForeignKey(x => x.EntraObjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.EntraObjectId).IsUnique();
        });

        b.Entity<CompteMicrosoft>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.AutresAdresses).HasMaxLength(4000);
            e.Property(x => x.Groupes).HasMaxLength(4000);
            e.Property(x => x.Licences).HasMaxLength(1000);
            e.HasIndex(x => x.Email);
            e.HasIndex(x => x.Upn);
            e.Ignore(x => x.EstParti);
            e.Ignore(x => x.ListeGroupes);
            e.Ignore(x => x.ListeLicences);
            e.Ignore(x => x.Adresses);
            e.Ignore(x => x.AdressePrincipale);
        });

        b.Entity<Materiel>(e =>
        {
            e.Property(x => x.NumInterne).HasMaxLength(60);
            e.HasIndex(x => x.NumInterne).IsUnique();
            e.HasIndex(x => x.NumSerie);
            e.HasIndex(x => x.Statut);
            e.Property(x => x.Commentaire).HasMaxLength(2000);
            // Mot de passe de session chiffré en base (AES-GCM, clé hors de la base) : la colonne contient la forme chiffrée
            e.Property(x => x.MotDePasseSession).HasMaxLength(1000)
                .HasConversion(v => ChiffrementSecrets.Chiffrer(v), v => ChiffrementSecrets.Dechiffrer(v));
            e.HasOne(x => x.Site).WithMany(s => s.Materiels).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Personne).WithMany(p => p.Materiels).HasForeignKey(x => x.PersonneId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Region).WithMany().HasForeignKey(x => x.RegionId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.TypeDetenteur);
            e.Ignore(x => x.DetenteurLibelle);
            e.Ignore(x => x.EstUnPc);
        });

        b.Entity<Mouvement>(e =>
        {
            e.HasIndex(x => x.Numero).IsUnique();
            e.Property(x => x.Commentaire).HasMaxLength(2000);
            e.HasOne(x => x.Materiel).WithMany(m => m.Mouvements).HasForeignKey(x => x.MaterielId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.DestinationSite).WithMany().HasForeignKey(x => x.DestinationSiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DestinationPersonne).WithMany().HasForeignKey(x => x.DestinationPersonneId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Fiche).WithMany(f => f.Mouvements).HasForeignKey(x => x.FicheId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.DestinationLibelle);
        });

        b.Entity<Fiche>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.IdPublic).IsUnique();
            e.HasIndex(x => x.DestinataireEmail);
        });

        b.Entity<Demande>(e =>
        {
            e.HasIndex(x => x.Numero).IsUnique();
            e.HasIndex(x => x.IdPublic).IsUnique();
            e.HasIndex(x => x.DemandeurEmail);
            e.HasIndex(x => x.SaisiePar);
            e.HasIndex(x => x.StatutValidation);
            e.Property(x => x.Raison).HasMaxLength(4000);
            e.Property(x => x.CommentaireDecision).HasMaxLength(2000);
            e.Property(x => x.CommentaireInformatique).HasMaxLength(4000);
            e.Ignore(x => x.ListeObjets);
            e.Ignore(x => x.StatutLibelle);
        });

        b.Entity<Ticket>(e =>
        {
            e.HasIndex(x => x.Numero).IsUnique();
            e.HasIndex(x => x.Statut);
            e.HasIndex(x => x.SaisiePar);
            e.HasIndex(x => x.DemandeurEmail);
            e.HasIndex(x => x.CreeLe);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.DejaEssaye).HasMaxLength(2000);
            e.Property(x => x.Diagnostic).HasMaxLength(4000);
            e.Property(x => x.Solution).HasMaxLength(4000);
            e.Property(x => x.SatisfactionCommentaire).HasMaxLength(2000);
            e.HasOne(x => x.Materiel).WithMany().HasForeignKey(x => x.MaterielId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.EstOuvert);
        });

        b.Entity<TicketMessage>(e =>
        {
            e.Property(x => x.Texte).HasMaxLength(4000);
            e.HasOne(x => x.Ticket).WithMany(t => t.Messages).HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Courriel>(e => e.Property(x => x.CorpsHtml).Metadata.SetMaxLength(null));
        // Les clés Data Protection sont des documents XML de taille variable : pas de limite
        b.Entity<DataProtectionKey>(e =>
        {
            e.Property(x => x.FriendlyName).Metadata.SetMaxLength(null);
            e.Property(x => x.Xml).Metadata.SetMaxLength(null);
        });
        b.Entity<Journal>(e =>
        {
            e.Property(x => x.Details).HasMaxLength(2000);
            e.HasIndex(x => x.Date);
        });
    }
}
