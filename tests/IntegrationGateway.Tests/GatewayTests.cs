using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntegrationGateway.Connectors;
using IntegrationGateway.Domain;
using IntegrationGateway.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationGateway.Tests;

public sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"gateway-test-{Guid.NewGuid():N}.db");
    public FixedClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Gateway", $"Data Source={_db}");
        builder.UseSetting("Gateway:DispatcherEnabled", "false");
        builder.UseSetting("Gateway:MaxAttempts", "3");
        builder.UseSetting("Gateway:SourceSecrets:pos", "pos-secret");
        builder.UseSetting("Gateway:Routes:3:Name", "pos-sales");
        builder.UseSetting("Gateway:Routes:3:Source", "pos");
        builder.UseSetting("Gateway:Routes:3:Type", "sale");
        builder.UseSetting("Gateway:Routes:3:Connector", "crm");
        builder.ConfigureServices(s => s.AddSingleton<TimeProvider>(Clock));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }
}

public class GatewayTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<HttpResponseMessage> Send(string source, string type, string id, object body, string? signature = null)
    {
        var json = JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/inbound/{source}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Event-Type", type);
        request.Headers.Add("X-Event-Id", id);
        if (signature is not null) request.Headers.Add("X-Signature", signature);
        return await _client.SendAsync(request);
    }

    private static object Order(string id) => new { @event = "order.created", orderId = id, total = 1499.0, currency = "ZAR", customer = new { name = "Thandi", email = "thandi@example.co.za" } };

    private async Task<int> Dispatch()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Dispatcher>().RunOnceAsync();
    }

    private async Task<Message> GetMessage(Guid id) => (await _client.GetFromJsonAsync<Message>($"/api/messages/{id}", Json))!;

    [Fact]
    public async Task Order_fans_out_retries_and_dead_letters()
    {
        var id = "ord-" + Guid.NewGuid().ToString("N")[..8];
        var response = await Send("shop", "order.created", id, Order(id));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<IngestResult>(Json);
        Assert.Equal(3, accepted!.Deliveries);

        await Dispatch();
        var message = await GetMessage(accepted.MessageId!.Value);
        Assert.Equal(DeliveryStatus.Delivered, message.Deliveries.Single(d => d.Connector == "crm").Status);
        var accounting = message.Deliveries.Single(d => d.Connector == "accounting");
        Assert.Equal(DeliveryStatus.Retrying, accounting.Status); // mock fails the first attempt
        Assert.Equal(factory.Clock.Now.AddSeconds(2), accounting.NextAttemptAt);

        // Not due yet: nothing changes.
        await Dispatch();
        Assert.Equal(1, (await GetMessage(message.Id)).Deliveries.Single(d => d.Connector == "accounting").Attempts);

        factory.Clock.Now = factory.Clock.Now.AddMinutes(10);
        await Dispatch(); // accounting succeeds on attempt 2, legacy-erp fails again
        factory.Clock.Now = factory.Clock.Now.AddMinutes(10);
        await Dispatch(); // legacy-erp fails a third time and is dead-lettered
        message = await GetMessage(message.Id);
        Assert.Equal(DeliveryStatus.Delivered, message.Deliveries.Single(d => d.Connector == "accounting").Status);
        var erp = message.Deliveries.Single(d => d.Connector == "legacy-erp");
        Assert.Equal(DeliveryStatus.DeadLettered, erp.Status);
        Assert.Equal(3, erp.Attempts);
        Assert.Contains("unavailable", erp.LastError);

        // Mapped payload reached the accounting mock.
        var received = await _client.GetFromJsonAsync<List<OutboundMessage>>("/api/mock/accounting", Json);
        var body = received!.Single(r => r.MessageId == message.Id).Body;
        Assert.Contains($"\"reference\":\"{id}\"", body);
        Assert.Contains("\"contactEmail\":\"thandi@example.co.za\"", body);

        // Replay resets the dead letter.
        var replay = await _client.PostAsync($"/api/deliveries/{erp.Id}/replay", null);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(DeliveryStatus.Pending, (await GetMessage(message.Id)).Deliveries.Single(d => d.Id == erp.Id).Status);
    }

    [Fact]
    public async Task Duplicate_event_ids_are_ignored()
    {
        var id = "dup-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(HttpStatusCode.Accepted, (await Send("shop", "order.created", id, Order(id))).StatusCode);
        var second = await Send("shop", "order.created", id, Order(id));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(IngestOutcome.Duplicate, (await second.Content.ReadFromJsonAsync<IngestResult>(Json))!.Outcome);
    }

    [Fact]
    public async Task Signed_sources_reject_bad_signatures()
    {
        var body = new { sale = 1 };
        var json = JsonSerializer.Serialize(body);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("pos", "sale", "s1", body, "sha256=bad")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("pos", "sale", "s2", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Send("pos", "sale", "s3", body, Signatures.Sign("pos-secret", json))).StatusCode);
    }

    [Fact]
    public async Task Unknown_route_and_bad_input_are_rejected()
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send("unknown", "x", "1", new { a = 1 })).StatusCode);
        var noHeaders = await _client.PostAsync("/api/inbound/shop", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, noHeaders.StatusCode);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inbound/shop") { Content = new StringContent("not json") };
        request.Headers.Add("X-Event-Type", "order.created");
        request.Headers.Add("X-Event-Id", "bad-json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Stats_and_dashboard_are_served()
    {
        var stats = await _client.GetFromJsonAsync<JsonElement>("/api/stats");
        Assert.True(stats.TryGetProperty("deliveries", out _));
        Assert.Contains("Integration Gateway", await _client.GetStringAsync("/"));
    }
}

public class WebhookConnectorTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status);
        }
    }

    [Fact]
    public async Task Webhook_posts_signed_json_and_raises_on_failure()
    {
        var handler = new CaptureHandler();
        var connector = new WebhookConnector("crm", new ConnectorOptions { Kind = "webhook", Url = "https://crm.test/hook", Secret = "s" }, new HttpClient(handler));
        await connector.SendAsync(new OutboundMessage(Guid.NewGuid(), "order.created", "{\"a\":1}", 1), default);
        Assert.Equal(Signatures.Sign("s", "{\"a\":1}"), handler.Request!.Headers.GetValues("X-Signature").Single());
        Assert.Equal("{\"a\":1}", handler.Body);

        handler.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<ConnectorException>(() => connector.SendAsync(new OutboundMessage(Guid.NewGuid(), "t", "{}", 1), default));
    }
}
