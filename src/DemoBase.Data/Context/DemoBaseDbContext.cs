using DemoBase.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DemoBase.Data.Context;

public class DemoBaseDbContext : DbContext
{
    public DemoBaseDbContext(DbContextOptions<DemoBaseDbContext> options) : base(options) { }

    // ─── DbSets ───────────────────────────────────────────────────────────────
    public DbSet<Release>              Releases              => Set<Release>();
    public DbSet<ReleaseType>          ReleaseTypes          => Set<ReleaseType>();
    public DbSet<ReleasePlatform>      ReleasePlatforms      => Set<ReleasePlatform>();
    public DbSet<ReleaseAuthor>        ReleaseAuthors        => Set<ReleaseAuthor>();
    public DbSet<ReleaseCredit>        ReleaseCredits        => Set<ReleaseCredit>();
    public DbSet<ReleaseLink>          ReleaseLinks          => Set<ReleaseLink>();
    public DbSet<Releaser>             Releasers             => Set<Releaser>();
    public DbSet<Nick>                 Nicks                 => Set<Nick>();
    public DbSet<ReleaserMembership>   ReleaserMemberships   => Set<ReleaserMembership>();
    public DbSet<Platform>             Platforms             => Set<Platform>();
    public DbSet<PartySeries>          PartySeries           => Set<PartySeries>();
    public DbSet<Party>                Parties               => Set<Party>();
    public DbSet<Competition>          Competitions          => Set<Competition>();
    public DbSet<CompetitionPlacing>   CompetitionPlacings   => Set<CompetitionPlacing>();
    public DbSet<Emulator>             Emulators             => Set<Emulator>();
    public DbSet<EmulatorConfig>       EmulatorConfigs       => Set<EmulatorConfig>();
    public DbSet<MediaFile>            MediaFiles            => Set<MediaFile>();
    public DbSet<ReleaseSoundtrack>    ReleaseSoundtracks    => Set<ReleaseSoundtrack>();
    public DbSet<EmulatorSetting>      EmulatorSettings      => Set<EmulatorSetting>();
    public DbSet<DatEntry>             DatEntries            => Set<DatEntry>();
    public DbSet<DatRom>               DatRoms               => Set<DatRom>();
    public DbSet<DatFileVersion>       DatFileVersions       => Set<DatFileVersion>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        base.OnConfiguring(options);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        // Base unique (demobase.db) depuis la fusion config.db/dats.db/demobase.db —
        // ToTable() ne fait plus que nommer les tables, plus de question de fichier.
        m.Entity<DatEntry>().ToTable("DatEntries");
        m.Entity<DatRom>().ToTable("DatRoms");
        m.Entity<DatFileVersion>().ToTable("DatFileVersions");

        m.Entity<Emulator>(e =>
        {
            e.ToTable("Emulators");
            // Les IDs 0–99 sont réservés aux émulateurs gérés par DemoBase
            // (valeur fixe = (int)EmulatorType). Les émulateurs créés
            // manuellement par l'utilisateur démarrent à 100 (voir DbInitializer).
            // ValueGeneratedNever : EF Core respecte l'Id fourni et ne laisse
            // pas SQLite AUTOINCREMENT l'écraser.
            e.Property(em => em.Id).ValueGeneratedNever();
            e.Property(em => em.Status).HasConversion<string>();
        });
        m.Entity<EmulatorConfig>().ToTable("EmulatorConfigs");
        m.Entity<EmulatorSetting>().ToTable("EmulatorSettings");
        base.OnModelCreating(m);

        m.Entity<DatEntry>(e =>
        {
            e.HasIndex(d => d.DemozooId);
        });
        m.Entity<DatRom>(e =>
        {
            e.HasOne(r => r.DatEntry).WithMany(d => d.Roms)
             .HasForeignKey(r => r.DatEntryId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<DatFileVersion>(e =>
        {
            e.HasIndex(d => d.FileName).IsUnique();
        });

        m.Entity<EmulatorSetting>(e =>
        {
            e.HasOne(es => es.EmulatorConfig)
             .WithMany(ec => ec.Settings)
             .HasForeignKey(es => es.EmulatorConfigId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(es => new { es.EmulatorConfigId, es.Key }).IsUnique();
        });

        m.Entity<ReleaseSoundtrack>(e =>
        {
            e.HasOne(rs => rs.Release)
             .WithMany(r => r.Soundtracks)
             .HasForeignKey(rs => rs.ReleaseId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(rs => rs.Soundtrack)
             .WithMany()
             .HasForeignKey(rs => rs.SoundtrackId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ─── ReleaseType ──────────────────────────────────────────────────────
        m.Entity<ReleaseType>(e =>
        {
            e.HasIndex(rt => rt.Name).IsUnique();
            e.Property(rt => rt.Name).HasMaxLength(100);
            e.HasIndex(rt => rt.DemozooId).IsUnique()
             .HasFilter("[DemozooId] IS NOT NULL");
        });

        // ─── Platform ─────────────────────────────────────────────────────────
        m.Entity<Platform>(e =>
        {
            e.HasIndex(p => p.Name).IsUnique();
            e.HasIndex(p => p.DemozooId).IsUnique()
             .HasFilter("[DemozooId] IS NOT NULL");
        });

        // ─── Release ──────────────────────────────────────────────────────────
        m.Entity<Release>(e =>
        {
            e.HasIndex(r => r.Title);
            e.HasIndex(r => r.ReleaseDate);
            e.HasIndex(r => r.ReleaseTypeId);
            e.HasIndex(r => r.Supertype);
            e.HasIndex(r => r.DemozooId).IsUnique()
             .HasFilter("[DemozooId] IS NOT NULL");
            // Index composite pour les filtres MediaBrowser (Supertype + DemozooId)
            // accélère la sous-requête HasDatEntry : WHERE Supertype=? AND DemozooId IS NOT NULL
            e.HasIndex(r => new { r.Supertype, r.DemozooId })
             .HasFilter("[DemozooId] IS NOT NULL")
             .HasDatabaseName("IX_Releases_Supertype_DemozooId");

            e.HasOne(r => r.ReleaseType)
             .WithMany(rt => rt.Releases)
             .HasForeignKey(r => r.ReleaseTypeId)
             .OnDelete(DeleteBehavior.Restrict)
             .IsRequired(false);
        });

        // ─── ReleasePlatform (M:N composite PK) ───────────────────────────────
        m.Entity<ReleasePlatform>(e =>
        {
            e.HasKey(rp => new { rp.ReleaseId, rp.PlatformId });
            e.HasOne(rp => rp.Release).WithMany(r => r.ReleasePlatforms).HasForeignKey(rp => rp.ReleaseId);
            e.HasOne(rp => rp.Platform).WithMany(p => p.ReleasePlatforms).HasForeignKey(rp => rp.PlatformId);
        });

        // ─── ReleaseAuthor ────────────────────────────────────────────────────
        m.Entity<ReleaseAuthor>(e =>
        {
            e.HasKey(ra => new { ra.ReleaseId, ra.NickId });
            e.HasOne(ra => ra.Release).WithMany(r => r.Authors).HasForeignKey(ra => ra.ReleaseId);
            e.HasOne(ra => ra.Nick).WithMany().HasForeignKey(ra => ra.NickId);
            e.HasOne(ra => ra.AffiliationNick).WithMany()
             .HasForeignKey(ra => ra.AffiliationNickId)
             .IsRequired(false).OnDelete(DeleteBehavior.SetNull);
        });

        // ─── ReleaseCredit ────────────────────────────────────────────────────
        // ReleaserId contient un NickId (structure Demozoo) — pas de FK vers Releasers
        // La résolution Releaser se fait via JOIN manuel dans GetWithFullDetailsAsync
        m.Entity<ReleaseCredit>(e =>
        {
            e.HasKey(c => new { c.ReleaseId, c.ReleaserId, c.Role });
            e.HasOne(c => c.Release).WithMany(r => r.Credits).HasForeignKey(c => c.ReleaseId);
            e.Ignore(c => c.Releaser);  // ignoré par EF — résolu manuellement
        });

        // ─── Releaser ─────────────────────────────────────────────────────────
        m.Entity<Releaser>(e =>
        {
            e.HasIndex(r => r.Name);
            e.HasIndex(r => r.DemozooId).IsUnique()
             .HasFilter("[DemozooId] IS NOT NULL");
            e.Ignore(r => r.Credits);  // Credits résolus manuellement via NickId
        });

        // ─── Nick ─────────────────────────────────────────────────────────────
        m.Entity<Nick>(e =>
        {
            e.HasIndex(n => n.Name);
            e.HasIndex(n => n.DemozooId).IsUnique()
             .HasFilter("[DemozooId] IS NOT NULL");
            e.HasOne(n => n.Releaser).WithMany(r => r.Nicks).HasForeignKey(n => n.ReleaserId);
        });

        // ─── ReleaserMembership (composite PK) ───────────────────────────────
        m.Entity<ReleaserMembership>(e =>
        {
            e.HasKey(rm => new { rm.ScenerId, rm.GroupId });
            e.HasOne(rm => rm.Scener)
             .WithMany(r => r.MembershipsAsScener)
             .HasForeignKey(rm => rm.ScenerId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(rm => rm.Group)
             .WithMany(r => r.MembershipsAsGroup)
             .HasForeignKey(rm => rm.GroupId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ─── Party & PartySeries ──────────────────────────────────────────────
        m.Entity<Party>(e =>
        {
            e.HasIndex(p => p.Name);
            e.HasIndex(p => p.DemozooId).IsUnique()
             .HasFilter("[DemozooId] IS NOT NULL");
            e.HasOne(p => p.PartySeries).WithMany(ps => ps.Parties)
             .HasForeignKey(p => p.PartySeriesId).IsRequired(false);
        });

        m.Entity<PartySeries>(e =>
        {
            e.HasIndex(ps => ps.DemozooId).IsUnique()
             .HasFilter("[DemozooId] IS NOT NULL");
        });

        // ─── Competition & Placings ───────────────────────────────────────────
        m.Entity<Competition>(e =>
        {
            e.HasIndex(c => c.DemozooId).IsUnique()
             .HasFilter("[DemozooId] IS NOT NULL");
            e.HasOne(c => c.Party).WithMany(p => p.Competitions).HasForeignKey(c => c.PartyId);
        });

        m.Entity<CompetitionPlacing>(e =>
        {
            e.HasIndex(cp => cp.DemozooId).IsUnique()
             .HasFilter("[DemozooId] IS NOT NULL");
            e.HasOne(cp => cp.Competition).WithMany(c => c.Placings).HasForeignKey(cp => cp.CompetitionId);
            e.HasOne(cp => cp.Release).WithMany(r => r.CompetitionPlacings).HasForeignKey(cp => cp.ReleaseId);
        });

        // ─── EmulatorConfig ───────────────────────────────────────────────────
        m.Entity<EmulatorConfig>(e =>
        {
            e.HasOne(ec => ec.Emulator).WithMany(em => em.Configurations).HasForeignKey(ec => ec.EmulatorId);
            e.HasOne(ec => ec.Platform).WithMany(p => p.Emulators).HasForeignKey(ec => ec.PlatformId);
        });

        // ─── MediaFile ────────────────────────────────────────────────────────
        m.Entity<MediaFile>(e =>
        {
            e.Property(mf => mf.Type).HasConversion<string>();
            e.HasIndex(mf => mf.ReleaseId);
            e.HasOne(mf => mf.Release).WithMany(r => r.MediaFiles).HasForeignKey(mf => mf.ReleaseId);
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.CreatedAt == null)
                entry.Entity.CreatedAt = now;
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }
        return await base.SaveChangesAsync(cancellationToken);
    }
}
