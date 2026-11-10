using AccountRecoveryApi.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AccountRecoveryApi.Infrastructure;

public sealed class RecoveryDbContext(DbContextOptions<RecoveryDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<RecoverySession> RecoverySessions => Set<RecoverySession>();
    public DbSet<MockInboxFixture> MockInboxes => Set<MockInboxFixture>();
    public DbSet<SyntheticAccount> SyntheticAccounts => Set<SyntheticAccount>();
    public DbSet<InboxAccount> InboxAccounts => Set<InboxAccount>();
    public DbSet<ProcessedCallEvent> ProcessedCallEvents => Set<ProcessedCallEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<RecoverySession>().HasKey(session => session.SessionId);
        builder.Entity<RecoverySession>()
            .Property(session => session.State)
            .HasConversion<string>();
        builder.Entity<RecoverySession>()
            .Property(session => session.Version)
            .IsConcurrencyToken();
        builder.Entity<RecoverySession>()
            .HasIndex(session => session.CallConnectionId)
            .IsUnique();
        builder.Entity<RecoverySession>()
            .HasIndex(session => session.ResetTokenHash)
            .IsUnique();

        builder.Entity<MockInboxFixture>().HasKey(inbox => inbox.AccountId);
        builder.Entity<MockInboxFixture>()
            .HasOne<SyntheticAccount>()
            .WithMany()
            .HasForeignKey(inbox => inbox.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<SyntheticAccount>().HasKey(account => account.AccountId);
        builder.Entity<InboxAccount>().HasKey(mapping => mapping.UserId);
        builder.Entity<InboxAccount>()
            .HasOne<IdentityUser>()
            .WithMany()
            .HasForeignKey(mapping => mapping.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<InboxAccount>()
            .HasIndex(mapping => mapping.AccountId)
            .IsUnique();
        builder.Entity<InboxAccount>()
            .HasOne<SyntheticAccount>()
            .WithMany()
            .HasForeignKey(mapping => mapping.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ProcessedCallEvent>().HasKey(callEvent => callEvent.EventId);
        builder.Entity<ProcessedCallEvent>()
            .HasOne<RecoverySession>()
            .WithMany()
            .HasForeignKey(callEvent => callEvent.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RecoveryDbContextFactory : IDesignTimeDbContextFactory<RecoveryDbContext>
{
    public RecoveryDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("RecoveryDatabase")
            ?? "Data Source=account-recovery.db";
        var options = new DbContextOptionsBuilder<RecoveryDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new RecoveryDbContext(options);
    }
}

public sealed class SyntheticAccount
{
    public required string AccountId { get; set; }
    public required string DisplayName { get; set; }
    public string? PasswordHash { get; set; }
}

public sealed class InboxAccount
{
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
}

public sealed class ProcessedCallEvent
{
    public required string EventId { get; set; }
    public required string SessionId { get; set; }
}
