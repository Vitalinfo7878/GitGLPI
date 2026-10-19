using Microsoft.EntityFrameworkCore;
using ParcInfo.Web.Domain;

namespace ParcInfo.Web.Data;

public class ParcDbContext(DbContextOptions<ParcDbContext> options) : DbContext(options)
{
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Personne> Personnes => Set<Personne>();
    public DbSet<Materiel> Materiels => Set<Materiel>();
    public DbSet<Mouvement> Mouvements => Set<Mouvement>();
    public DbSet<Fiche> Fiches => Set<Fiche>();
    public DbSet<Courriel> Courriels => Set<Courriel>();
    public DbSet<Journal> Journal => Set<Journal>();

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
        });

        b.Entity<Personne>(e =>
        {
            e.Property(x => x.NomComplet).HasMaxLength(200);
            e.HasIndex(x => x.NomComplet);
            e.HasIndex(x => x.Email);
        });

        b.Entity<Materiel>(e =>
        {
            e.Property(x => x.NumInterne).HasMaxLength(60);
            e.HasIndex(x => x.NumInterne).IsUnique();
            e.HasIndex(x => x.NumSerie);
            e.HasIndex(x => x.Statut);
            e.Property(x => x.Commentaire).HasMaxLength(2000);
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

        b.Entity<Courriel>(e => e.Property(x => x.CorpsHtml).HasMaxLength(int.MaxValue));
        b.Entity<Journal>(e =>
        {
            e.Property(x => x.Details).HasMaxLength(2000);
            e.HasIndex(x => x.Date);
        });
    }
}
