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

        ConfigureRunEntity(modelBuilder);
        ConfigureRunEventEntity(modelBuilder);
        ConfigureInboxEntryEntity(modelBuilder);
        ConfigureExecutionQueueEntity(modelBuilder);
    }

    private static void ConfigureRunEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunEntity>(runEntity =>
        {
            runEntity.ToTable("Runs");
            runEntity.HasKey(run => run.RunId);
            runEntity.Property(run => run.RunId).HasMaxLength(50);
            runEntity.Property(run => run.JobKey).HasMaxLength(200).IsRequired();
            runEntity.Property(run => run.Status).IsRequired();
            runEntity.Property(run => run.RequestedByAddress).HasMaxLength(500).IsRequired();
            runEntity.Property(run => run.ChannelId).HasMaxLength(100).IsRequired();
            runEntity.Property(run => run.ConversationId).HasMaxLength(500).IsRequired();
            runEntity.Property(run => run.Version).IsConcurrencyToken();

            runEntity.HasIndex(run => run.Status);
            runEntity.HasIndex(run => new { run.ChannelId, run.ConversationId });
        });
    }

    private static void ConfigureRunEventEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunEventEntity>(runEventEntity =>
        {
            runEventEntity.ToTable("RunEvents");
            runEventEntity.HasKey(runEvent => runEvent.Id);
            runEventEntity.Property(runEvent => runEvent.Id).ValueGeneratedOnAdd();
            runEventEntity.Property(runEvent => runEvent.RunId).HasMaxLength(50).IsRequired();
            runEventEntity.Property(runEvent => runEvent.Type).HasMaxLength(100).IsRequired();
            runEventEntity.Property(runEvent => runEvent.Actor).HasMaxLength(500).IsRequired();
            runEventEntity.Property(runEvent => runEvent.PayloadJson).IsRequired();

            runEventEntity.HasIndex(runEvent => runEvent.RunId);

            runEventEntity.HasOne(runEvent => runEvent.Run)
                .WithMany(run => run.Events)
                .HasForeignKey(runEvent => runEvent.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureInboxEntryEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxEntryEntity>(inboxEntryEntity =>
        {
            inboxEntryEntity.ToTable("InboxDedup");
            inboxEntryEntity.HasKey(inboxEntry => new { inboxEntry.ChannelId, inboxEntry.ProviderMessageId });
            inboxEntryEntity.Property(inboxEntry => inboxEntry.ChannelId).HasMaxLength(100);
            inboxEntryEntity.Property(inboxEntry => inboxEntry.ProviderMessageId).HasMaxLength(500);
            inboxEntryEntity.Property(inboxEntry => inboxEntry.RunId).HasMaxLength(50);
        });
    }

    private static void ConfigureExecutionQueueEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExecutionQueueEntity>(queueEntity =>
        {
            queueEntity.ToTable("ExecutionQueue");
            queueEntity.HasKey(queueEntry => queueEntry.Id);
            queueEntity.Property(queueEntry => queueEntry.Id).ValueGeneratedOnAdd();
            queueEntity.Property(queueEntry => queueEntry.RunId).HasMaxLength(50).IsRequired();
            queueEntity.Property(queueEntry => queueEntry.JobKey).HasMaxLength(200).IsRequired();
            queueEntity.Property(queueEntry => queueEntry.Status).HasMaxLength(20).IsRequired();
            queueEntity.Property(queueEntry => queueEntry.LockedBy).HasMaxLength(100);
            queueEntity.Property(queueEntry => queueEntry.LastError).HasMaxLength(2000);

            queueEntity.HasIndex(queueEntry => queueEntry.Status);
            queueEntity.HasIndex(queueEntry => new { queueEntry.Status, queueEntry.LockedAt });
        });
    }
}

