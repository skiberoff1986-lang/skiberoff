using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CraneJournal.Web.Data;

public sealed class AppDb(DbContextOptions<AppDb> options) : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;
    public DbSet<RepairRecord> Records => Set<RepairRecord>();
    public DbSet<JournalEvent> Events => Set<JournalEvent>();
    public DbSet<CommandReceipt> Commands => Set<CommandReceipt>();
    public DbSet<UserShop> UserShops => Set<UserShop>();
    public DbSet<MediaFile> Media => Set<MediaFile>();
    public DbSet<MediaChunk> Chunks => Set<MediaChunk>();
    public DbSet<OutboxItem> Outbox => Set<OutboxItem>();
    public DbSet<SchemaState> SchemaStates => Set<SchemaState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<AppUser>().Property(x => x.FullName).HasMaxLength(120);
        b.Entity<UserShop>().HasKey(x => new { x.UserId, x.Shop });
        b.Entity<UserShop>().Property(x => x.Shop).HasMaxLength(40);
        b.Entity<UserShop>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RepairRecord>(r =>
        {
            r.Property(x => x.Revision).IsConcurrencyToken();
            r.Property(x => x.Shop).HasMaxLength(40); r.Property(x => x.Crane).HasMaxLength(40);
            r.Property(x => x.DataJson).HasColumnType("jsonb");
            r.Property(x => x.RegisteredLocal).HasColumnType("timestamp without time zone");
            r.HasIndex(x => new { x.Shop, x.RegisteredLocal }); r.HasIndex(x => x.IsClosed);
        });
        b.Entity<JournalEvent>(e =>
        {
            e.HasKey(x => x.Id); e.Property(x => x.Sequence).UseIdentityAlwaysColumn();
            e.HasIndex(x => x.Sequence).IsUnique(); e.HasIndex(x => new { x.RecordId, x.Sequence });
            e.Property(x => x.SnapshotJson).HasColumnType("jsonb");
            e.HasOne<RepairRecord>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<CommandReceipt>().Property(x => x.ResultJson).HasColumnType("jsonb");
        b.Entity<MediaFile>(m =>
        {
            m.Property(x => x.OriginalName).HasMaxLength(255); m.Property(x => x.Sha256).HasMaxLength(64);
            m.HasIndex(x => new { x.RecordId, x.Sha256 }).IsUnique().HasFilter("\"State\" = 1");
            m.HasOne<RepairRecord>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<MediaChunk>(c =>
        {
            c.HasKey(x => new { x.MediaId, x.Index });
            c.HasOne<MediaFile>().WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OutboxItem>(o =>
        {
            o.Property(x => x.Sequence).UseIdentityAlwaysColumn(); o.HasIndex(x => x.Sequence).IsUnique();
            o.HasIndex(x => new { x.State, x.NextAttemptAtUtc }); o.HasIndex(x => x.MediaId).IsUnique();
            o.HasIndex(x => new { x.RecordId, x.Stage }).IsUnique().HasFilter("\"Stage\" <= 3");
            o.HasOne<RepairRecord>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
