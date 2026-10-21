using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IntegrationGateway.Infrastructure;

public static class Telemetry
{
    public const string Name = "IntegrationGateway";
    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Received = Meter.CreateCounter<long>("gateway.messages.received");
    public static readonly Counter<long> Delivered = Meter.CreateCounter<long>("gateway.deliveries.delivered");
    public static readonly Counter<long> Failed = Meter.CreateCounter<long>("gateway.deliveries.failed");
    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("gateway.deliveries.deadlettered");
}
