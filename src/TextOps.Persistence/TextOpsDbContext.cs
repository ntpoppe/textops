using Microsoft.EntityFrameworkCore;
using TextOps.Persistence.Entities;

namespace TextOps.Persistence;

/// <summary>
/// EF Core DbContext for TextOps persistence.
/// Supports SQLite (dev) and PostgreSQL (prod).
/// </summary>
public sealed class TextOpsDbContext : DbContext
{
    public TextOpsDbContext(DbContextOptions<TextOpsDbContext> options) : base(options)
    {
    }

    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<RunEventEntity> RunEvents => Set<RunEventEntity>();
    public DbSet<InboxEntryEntity> InboxEntries => Set<InboxEntryEntity>();
    public DbSet<ExecutionQueueEntity> ExecutionQueue => Set<ExecutionQueueEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Runs table
        modelBuilder.Entity<RunEntity>(entity =>
        {
            entity.ToTable("Runs");
            entity.HasKey(e => e.RunId);
            entity.Property(e => e.RunId).HasMaxLength(50);
            entity.Property(e => e.JobKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.RequestedByAddress).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ChannelId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ConversationId).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Version).IsConcurrencyToken();

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.ChannelId, e.ConversationId });
        });

        // RunEvents table (append-only)
        modelBuilder.Entity<RunEventEntity>(entity =>
        {
            entity.ToTable("RunEvents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.RunId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Type).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Actor).HasMaxLength(500).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();

            entity.HasIndex(e => e.RunId);

            entity.HasOne(e => e.Run)
                .WithMany(r => r.Events)
                .HasForeignKey(e => e.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // InboxDedup table
        modelBuilder.Entity<InboxEntryEntity>(entity =>
        {
            entity.ToTable("InboxDedup");
            entity.HasKey(e => new { e.ChannelId, e.ProviderMessageId });
            entity.Property(e => e.ChannelId).HasMaxLength(100);
            entity.Property(e => e.ProviderMessageId).HasMaxLength(500);
            entity.Property(e => e.RunId).HasMaxLength(50);
        });

        // ExecutionQueue table
        modelBuilder.Entity<ExecutionQueueEntity>(entity =>
        {
            entity.ToTable("ExecutionQueue");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.RunId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.JobKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
            entity.Property(e => e.LockedBy).HasMaxLength(100);
            entity.Property(e => e.LastError).HasMaxLength(2000);

            // Index for claiming: find pending entries quickly
            entity.HasIndex(e => e.Status);
            // Index for stale lock recovery
            entity.HasIndex(e => new { e.Status, e.LockedAt });
        });
    }
}

