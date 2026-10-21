using Microsoft.EntityFrameworkCore;

namespace IntegrationGateway.Domain;

public enum DeliveryStatus { Pending, Retrying, Delivered, DeadLettered }

/// <summary>An inbound event received from a source system (shop, CRM, POS...).</summary>
public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Source { get; set; }
    public required string Type { get; set; }
    /// <summary>The source's own event id, used for idempotency.</summary>
    public required string ExternalId { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public List<Delivery> Deliveries { get; set; } = new();
}

/// <summary>One message going to one connector via one route.</summary>
public class Delivery
{
    public int Id { get; set; }
    public Guid MessageId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Message? Message { get; set; }
    public required string Route { get; set; }
    public required string Connector { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? LastError { get; set; }
}

public class GatewayDb(DbContextOptions<GatewayDb> options) : DbContext(options)
{
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Message>().HasIndex(m => new { m.Source, m.ExternalId }).IsUnique();
        b.Entity<Delivery>().Property(d => d.Status).HasConversion<string>();
        b.Entity<Delivery>().HasIndex(d => new { d.Status, d.NextAttemptAt });
        // SQLite cannot order by DateTimeOffset natively; store as UTC ticks.
        b.Entity<Delivery>().Property(d => d.NextAttemptAt)
            .HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
    }
}
