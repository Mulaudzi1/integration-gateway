using System.Text.Json;
using IntegrationGateway.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IntegrationGateway.Infrastructure;

public enum IngestOutcome { Accepted, Duplicate, NoRoute, InvalidSignature, InvalidPayload }

public sealed record IngestResult(IngestOutcome Outcome, Guid? MessageId = null, int Deliveries = 0, string? Error = null);

public sealed class IngestionService(GatewayDb db, IOptions<GatewayOptions> options, TimeProvider clock)
{
    public async Task<IngestResult> IngestAsync(string source, string type, string externalId, string body, string? signature, CancellationToken ct = default)
    {
        using var activity = Telemetry.Source.StartActivity("ingest");
        activity?.SetTag("gateway.source", source);
        activity?.SetTag("gateway.type", type);

        if (options.Value.SourceSecrets.TryGetValue(source, out var secret) && !string.IsNullOrEmpty(secret)
            && !Signatures.Verify(secret, body, signature))
            return new IngestResult(IngestOutcome.InvalidSignature, Error: "Signature does not match.");

        try { using var _ = JsonDocument.Parse(body); }
        catch (JsonException) { return new IngestResult(IngestOutcome.InvalidPayload, Error: "Body must be valid JSON."); }

        var existing = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Source == source && m.ExternalId == externalId, ct);
        if (existing is not null) return new IngestResult(IngestOutcome.Duplicate, existing.Id);

        var routes = options.Value.Routes
            .Where(r => r.Source.Equals(source, StringComparison.OrdinalIgnoreCase) && (r.Type == "*" || r.Type == type))
            .ToList();
        if (routes.Count == 0) return new IngestResult(IngestOutcome.NoRoute, Error: $"No route for {source}/{type}.");

        var now = clock.GetUtcNow();
        var message = new Message
        {
            Source = source, Type = type, ExternalId = externalId, Payload = body, ReceivedAt = now,
            Deliveries = routes.Select(r => new Delivery { Route = r.Name, Connector = r.Connector, NextAttemptAt = now }).ToList(),
        };
        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent duplicate: the unique index protects us.
            return new IngestResult(IngestOutcome.Duplicate);
        }
        Telemetry.Received.Add(1);
        return new IngestResult(IngestOutcome.Accepted, message.Id, message.Deliveries.Count);
    }
}
