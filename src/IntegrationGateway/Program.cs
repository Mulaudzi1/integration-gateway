using IntegrationGateway;
using System.Text.Json.Serialization;
using IntegrationGateway.Connectors;
using IntegrationGateway.Domain;
using IntegrationGateway.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.AddDbContext<GatewayDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Gateway") ?? "Data Source=gateway.db"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("connectors", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<ConnectorRegistry>();
builder.Services.AddScoped<IngestionService>();
builder.Services.AddScoped<Dispatcher>();
builder.Services.AddHostedService<DispatcherService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// OpenTelemetry: traces and metrics are exported over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set.
var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("integration-gateway"))
    .WithTracing(t =>
    {
        t.AddSource(Telemetry.Name).AddAspNetCoreInstrumentation();
        if (!string.IsNullOrEmpty(otlp)) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddMeter(Telemetry.Name).AddAspNetCoreInstrumentation();
        if (!string.IsNullOrEmpty(otlp)) m.AddOtlpExporter();
    });

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<GatewayDb>().Database.EnsureCreated();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGatewayEndpoints();

app.Run();

public partial class Program;
