using System.Collections.Concurrent;
using System.Text;
using IntegrationGateway.Domain;
using Microsoft.Extensions.Options;

namespace IntegrationGateway.Connectors;

public sealed record OutboundMessage(Guid MessageId, string Type, string Body, int Attempt);

public interface IConnector
{
    string Name { get; }
    Task SendAsync(OutboundMessage message, CancellationToken ct);
}

/// <summary>In-memory sandbox connector. Records what it receives and can simulate failures.</summary>
public sealed class MockConnector(string name, ConnectorOptions options) : IConnector
{
    public string Name => name;
    public ConcurrentQueue<OutboundMessage> Received { get; } = new();

    public Task SendAsync(OutboundMessage message, CancellationToken ct)
    {
        if (options.AlwaysFail) throw new ConnectorException($"{name} is unavailable (simulated).");
        if (message.Attempt <= options.FailFirstAttempts)
            throw new ConnectorException($"{name} timed out on attempt {message.Attempt} (simulated).");
        Received.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>POSTs the mapped JSON to a URL, signed with HMAC-SHA256 when a secret is configured.</summary>
public sealed class WebhookConnector(string name, ConnectorOptions options, HttpClient http) : IConnector
{
    public string Name => name;

    public async Task SendAsync(OutboundMessage message, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Url)
        {
            Content = new StringContent(message.Body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Event-Id", message.MessageId.ToString());
        request.Headers.Add("X-Event-Type", message.Type);
        if (!string.IsNullOrEmpty(options.Secret))
            request.Headers.Add("X-Signature", Signatures.Sign(options.Secret, message.Body));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ConnectorException($"{name} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
    }
}

public sealed class ConnectorException(string message) : Exception(message);

public sealed class ConnectorRegistry
{
    private readonly Dictionary<string, IConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);

    public ConnectorRegistry(IOptions<GatewayOptions> options, IHttpClientFactory http)
    {
        foreach (var (name, c) in options.Value.Connectors)
        {
            _connectors[name] = c.Kind.ToLowerInvariant() switch
            {
                "mock" => new MockConnector(name, c),
                "webhook" when !string.IsNullOrWhiteSpace(c.Url) => new WebhookConnector(name, c, http.CreateClient("connectors")),
                _ => throw new InvalidOperationException($"Connector '{name}' has an unknown kind '{c.Kind}' or no Url."),
            };
        }
    }

    public IConnector? Get(string name) => _connectors.GetValueOrDefault(name);
    public IEnumerable<IConnector> All => _connectors.Values;
}
