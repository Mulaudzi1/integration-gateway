using IntegrationGateway.Connectors;
using IntegrationGateway.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IntegrationGateway.Infrastructure;

/// <summary>Delivers due messages to connectors with exponential-backoff retries and dead-lettering.</summary>
public sealed class Dispatcher(GatewayDb db, ConnectorRegistry connectors, IOptions<GatewayOptions> options, TimeProvider clock, ILogger<Dispatcher> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var due = await db.Deliveries.Include(d => d.Message)
            .Where(d => (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Retrying)
                        && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var delivery in due)
        {
            using var activity = Telemetry.Source.StartActivity("deliver");
            activity?.SetTag("gateway.route", delivery.Route);
            activity?.SetTag("gateway.connector", delivery.Connector);
            delivery.Attempts++;
            try
            {
                var connector = connectors.Get(delivery.Connector)
                                ?? throw new ConnectorException($"Connector '{delivery.Connector}' is not configured.");
                var route = o.Routes.FirstOrDefault(r => r.Name == delivery.Route);
                var body = PayloadMapper.Map(delivery.Message!.Payload, route?.Map ?? new());
                await connector.SendAsync(new OutboundMessage(delivery.MessageId, delivery.Message.Type, body, delivery.Attempts), ct);
                delivery.Status = DeliveryStatus.Delivered;
                delivery.DeliveredAt = now;
                delivery.LastError = null;
                Telemetry.Delivered.Add(1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                delivery.LastError = ex.Message;
                Telemetry.Failed.Add(1);
                if (delivery.Attempts >= o.MaxAttempts)
                {
                    delivery.Status = DeliveryStatus.DeadLettered;
                    Telemetry.DeadLettered.Add(1);
                    logger.LogWarning("Delivery {Id} to {Connector} dead-lettered after {Attempts} attempts: {Error}",
                        delivery.Id, delivery.Connector, delivery.Attempts, ex.Message);
                }
                else
                {
                    delivery.Status = DeliveryStatus.Retrying;
                    delivery.NextAttemptAt = now + RetryPolicy.Delay(delivery.Attempts, o.BaseDelaySeconds, o.MaxDelaySeconds);
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}

public sealed class DispatcherService(IServiceScopeFactory scopes, IOptions<GatewayOptions> options, ILogger<DispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DispatcherEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<Dispatcher>().RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Dispatcher run failed");
            }
        }
    }
}
