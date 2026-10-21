namespace IntegrationGateway.Domain;

public sealed class GatewayOptions
{
    public int MaxAttempts { get; set; } = 5;
    public int BaseDelaySeconds { get; set; } = 2;
    public int MaxDelaySeconds { get; set; } = 300;
    public int PollIntervalSeconds { get; set; } = 2;
    public bool DispatcherEnabled { get; set; } = true;
    /// <summary>Shared secrets per source for HMAC-SHA256 signature checks. A source with no secret is accepted unsigned.</summary>
    public Dictionary<string, string> SourceSecrets { get; set; } = new();
    public List<RouteOptions> Routes { get; set; } = new();
    public Dictionary<string, ConnectorOptions> Connectors { get; set; } = new();
}

public sealed class RouteOptions
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>Event type to match, or "*" for all types from the source.</summary>
    public string Type { get; set; } = "*";
    public string Connector { get; set; } = "";
    /// <summary>Output field name → JSON path in the inbound payload (e.g. "$.customer.email"). Empty = pass through.</summary>
    public Dictionary<string, string> Map { get; set; } = new();
}

public sealed class ConnectorOptions
{
    /// <summary>"mock" (in-memory sandbox) or "webhook" (HTTP POST).</summary>
    public string Kind { get; set; } = "mock";
    public string? Url { get; set; }
    public string? Secret { get; set; }
    /// <summary>Mock only: fail the first N attempts of every message (to demo retries).</summary>
    public int FailFirstAttempts { get; set; }
    /// <summary>Mock only: always fail (to demo dead-lettering).</summary>
    public bool AlwaysFail { get; set; }
}
