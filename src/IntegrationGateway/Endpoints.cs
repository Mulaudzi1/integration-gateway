using IntegrationGateway.Connectors;
using IntegrationGateway.Domain;
using IntegrationGateway.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IntegrationGateway;

public static class Endpoints
{
    public static void MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        // Inbound webhook from a source system.
        app.MapPost("/api/inbound/{source}", async (string source, HttpRequest request, IngestionService ingestion) =>
        {
            var type = request.Headers["X-Event-Type"].ToString();
            var id = request.Headers["X-Event-Id"].ToString();
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { message = "X-Event-Type and X-Event-Id headers are required." });
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var result = await ingestion.IngestAsync(source, type, id, body, request.Headers["X-Signature"].FirstOrDefault());
            return result.Outcome switch
            {
                IngestOutcome.Accepted => Results.Accepted($"/api/messages/{result.MessageId}", result),
                IngestOutcome.Duplicate => Results.Ok(result),
                IngestOutcome.InvalidSignature => Results.Unauthorized(),
                IngestOutcome.NoRoute => Results.UnprocessableEntity(result),
                _ => Results.BadRequest(result),
            };
        }).WithTags("Inbound");

        var api = app.MapGroup("/api").WithTags("Operations");
        api.MapGet("/messages/{id:guid}", async (Guid id, GatewayDb db) =>
            await db.Messages.Include(m => m.Deliveries).FirstOrDefaultAsync(m => m.Id == id) is { } m ? Results.Ok(m) : Results.NotFound());

        api.MapGet("/deliveries", async (GatewayDb db, DeliveryStatus? status, int take = 100) =>
            (await db.Deliveries.Include(d => d.Message)
                .Where(d => status == null || d.Status == status)
                .OrderByDescending(d => d.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync())
            .Select(d => new
            {
                d.Id, d.MessageId, d.Route, d.Connector, d.Status, d.Attempts, d.NextAttemptAt, d.DeliveredAt, d.LastError,
                Source = d.Message!.Source, Type = d.Message.Type, d.Message.ExternalId,
            }));

        api.MapGet("/stats", async (GatewayDb db) =>
        {
            var counts = await db.Deliveries.GroupBy(d => d.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync();
            return Results.Ok(new
            {
                messages = await db.Messages.CountAsync(),
                deliveries = Enum.GetValues<DeliveryStatus>().ToDictionary(s => s.ToString(), s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0),
            });
        });

        // Dead-letter replay: reset attempts and schedule immediately.
        api.MapPost("/deliveries/{id:int}/replay", async (int id, GatewayDb db, TimeProvider clock) =>
        {
            var delivery = await db.Deliveries.FindAsync(id);
            if (delivery is null) return Results.NotFound();
            if (delivery.Status != DeliveryStatus.DeadLettered)
                return Results.Conflict(new { message = "Only dead-lettered deliveries can be replayed." });
            delivery.Status = DeliveryStatus.Pending;
            delivery.Attempts = 0;
            delivery.NextAttemptAt = clock.GetUtcNow();
            await db.SaveChangesAsync();
            return Results.Ok(new { delivery.Id, delivery.Status });
        });

        api.MapPost("/dispatch", async (Dispatcher dispatcher) => Results.Ok(new { processed = await dispatcher.RunOnceAsync() }));

        // Sandbox: see what the mock connectors received.
        api.MapGet("/mock/{connector}", (string connector, ConnectorRegistry registry) =>
            registry.Get(connector) is MockConnector mock ? Results.Ok(mock.Received.ToArray()) : Results.NotFound());
    }
}
