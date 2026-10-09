using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WebhookDemo.Inbox;
using WebhookDemo.Integrations.Simulated;
using WebhookDemo.Pipelines.Leads;
using WebhookDemo.Pipelines.Payments;
using WebhookDemo.Pipelines.WhatsApp;

namespace WebhookDemo.Data;

public sealed class WebhookDbContext(DbContextOptions<WebhookDbContext> options) : DbContext(options)
{
    // The idempotency machinery
    public DbSet<InboxEvent> InboxEvents => Set<InboxEvent>();
    public DbSet<StepCheckpoint> Checkpoints => Set<StepCheckpoint>();
    public DbSet<TimelineEntry> Timeline => Set<TimelineEntry>();

    // What the pipelines produce
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    // The external providers' side of the calls (simulated, demo only)
    public DbSet<ProviderCall> ProviderCalls => Set<ProviderCall>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<Enum>().HaveConversion<string>();

        // SQLite has no time zones: everything is written as UTC, so read it back as UTC.
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<InboxEvent>(e =>
        {
            e.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique(); // ← deduplication happens here
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });

        // A step completes at most once per event.
        model.Entity<StepCheckpoint>().HasIndex(x => new { x.InboxEventId, x.Step }).IsUnique();
        model.Entity<TimelineEntry>().HasIndex(x => x.InboxEventId);

        // Business keys are unique as well: the last line of defense if two workers ever race.
        model.Entity<Payment>().HasIndex(x => x.PaymentId).IsUnique();
        model.Entity<Invoice>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.PaymentId).IsUnique();
        });
        model.Entity<Lead>().HasIndex(x => x.SubmissionId).IsUnique();
        model.Entity<ChatMessage>().HasIndex(x => x.MessageId).IsUnique();
        model.Entity<ProviderCall>().HasIndex(x => new { x.Provider, x.Operation, x.Key });
    }

    internal sealed class UtcDateTimeConverter()
        : ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
