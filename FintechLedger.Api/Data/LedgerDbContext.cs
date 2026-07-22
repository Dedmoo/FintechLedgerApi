using FintechLedger.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FintechLedger.Api.Data;

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<LedgerLine> LedgerLines => Set<LedgerLine>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AuditChainState> AuditChainStates => Set<AuditChainState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(a => a.AccountId);
            entity.Property(a => a.OwnerName).IsRequired().HasMaxLength(120);
            entity.Property(a => a.Currency).IsRequired().HasMaxLength(3);
        });

        modelBuilder.Entity<JournalEntry>(entity =>
        {
            entity.HasKey(e => e.EntryId);
            entity.Property(e => e.Description).IsRequired();
            entity.HasMany(e => e.Lines)
                .WithOne()
                .HasForeignKey(l => l.JournalEntryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LedgerLine>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.AccountId).IsRequired();
            entity.Property(l => l.Debit).HasPrecision(18, 2);
            entity.Property(l => l.Credit).HasPrecision(18, 2);
            entity.HasIndex(l => l.AccountId);
            entity.HasIndex(l => l.JournalEntryId);
        });

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasKey(r => r.MapKey);
            entity.Property(r => r.EntryId).IsRequired();
            entity.Property(r => r.Fingerprint).IsRequired();
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(a => a.EventId);
            entity.Property(a => a.Action).IsRequired();
            entity.Property(a => a.Details).IsRequired();
            entity.Property(a => a.PreviousHash).IsRequired();
            entity.Property(a => a.EventHash).IsRequired();
            entity.HasIndex(a => a.OccurredAt);
        });

        modelBuilder.Entity<AuditChainState>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.LastHash).IsRequired();
        });
    }
}
