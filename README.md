# Integration Gateway

A small, self-hosted integration hub ("iPaaS-lite") for connecting business systems. Source systems such as an online shop or POS send webhooks to the gateway; it stores each event durably, maps it into each target's shape and delivers it to connectors (accounting, CRM, a legacy ERP...) with retries, exponential backoff, dead-lettering, replay and OpenTelemetry tracing.

Built with ASP.NET Core 8, EF Core + SQLite (a durable outbox) and OpenTelemetry. It ships with **mock connectors**, so the whole flow runs locally with no external accounts.

![flow](https://img.shields.io/badge/flow-webhook%20→%20outbox%20→%20connectors-1d2330)

## Features

- **Inbound webhooks** at `POST /api/inbound/{source}` with `X-Event-Type` / `X-Event-Id` headers
- **Idempotency**: duplicate `(source, event id)` pairs are ignored, enforced by a unique index
- **HMAC-SHA256 signatures** (`X-Signature: sha256=…`) per source, checked in constant time
- **Config-driven routing**: one event can fan out to several connectors, filtered by source and type
- **Payload mapping**: `"contactEmail": "$.customer.email"`-style paths, including array indexes
- **Reliable delivery**: a background dispatcher with exponential backoff (2s, 4s, 8s… capped), max attempts, then **dead-letter**
- **Replay** dead letters from the API or the dashboard
- **Connectors**: `webhook` (HTTP POST, signed) and `mock` (in-memory sandbox that can simulate timeouts or outages)
- **Observability**: OpenTelemetry traces and metrics (`gateway.messages.received`, `gateway.deliveries.*`) exported over OTLP; Jaeger included in docker-compose
- **Dashboard** at `/` with live counts, delivery status and replay buttons, plus Swagger at `/swagger`

## Architecture

```text
 Shop / POS / CRM ──webhook──▶ POST /api/inbound/{source}
                                 │  verify HMAC · check idempotency · match routes
                                 ▼
                         SQLite outbox (Messages + Deliveries)
                                 │
                       DispatcherService (every 2s)
                                 │  map payload · send · retry with backoff
                 ┌───────────────┼────────────────┐
                 ▼               ▼                ▼
           accounting          crm           legacy-erp
         (mock/webhook)   (mock/webhook)    (mock/webhook)
                                 │
                     Dead letters ──▶ replay
```

```text
src/IntegrationGateway
  Domain/          Message & Delivery model, options, RetryPolicy, PayloadMapper, Signatures
  Connectors/      IConnector, MockConnector, WebhookConnector, ConnectorRegistry
  Infrastructure/  IngestionService, Dispatcher (+ hosted service), Telemetry
  Endpoints.cs     Inbound + operations API
  wwwroot/         Dashboard
tests/IntegrationGateway.Tests   Unit + end-to-end tests (WebApplicationFactory, fake clock)
```

## Run it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/IntegrationGateway
```

Open the URL shown in the console and click **Send a sample shop order**. With the default config:

- `crm` receives it straight away
- `accounting` fails the first attempt (simulated timeout) and succeeds on the retry
- `legacy-erp` always fails and ends up dead-lettered after 5 attempts. Replay it from the dashboard

With Docker (gateway + Jaeger for traces):

```bash
docker compose up --build
# dashboard http://localhost:8080 · traces http://localhost:16686
```

Send an event with curl:

```bash
curl -X POST http://localhost:8080/api/inbound/shop \
  -H 'Content-Type: application/json' -H 'X-Event-Type: order.created' -H 'X-Event-Id: ord-1001' \
  -d '{"event":"order.created","orderId":"ord-1001","total":1499,"currency":"ZAR","customer":{"name":"Thandi Nkosi","email":"thandi@example.co.za"}}'
```

## API

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/inbound/{source}` | Receive an event (202 accepted, 200 duplicate, 401 bad signature, 422 no route) |
| GET | `/api/messages/{id}` | A message and its deliveries |
| GET | `/api/deliveries?status=DeadLettered` | List deliveries |
| POST | `/api/deliveries/{id}/replay` | Replay a dead letter |
| GET | `/api/stats` | Counts by status |
| POST | `/api/dispatch` | Run the dispatcher once (handy in tests and demos) |
| GET | `/api/mock/{connector}` | What a mock connector has received |

## Configuration

Everything lives under `Gateway` in `appsettings.json` and can be overridden with environment variables (for example `Gateway__MaxAttempts=8`).

| Setting | Default | Purpose |
|---|---|---|
| `MaxAttempts` | `5` | Attempts before a delivery is dead-lettered |
| `BaseDelaySeconds` / `MaxDelaySeconds` | `2` / `300` | Exponential backoff base and cap |
| `PollIntervalSeconds` | `2` | Dispatcher interval |
| `SourceSecrets:{source}` | none | HMAC secret per source. Set it via environment variables, never in git |
| `Connectors:{name}` | 3 mocks | `Kind` = `mock` or `webhook`; `Url`, `Secret`; mock-only `FailFirstAttempts`, `AlwaysFail` |
| `Routes[]` | 3 demo routes | `Name`, `Source`, `Type` (or `*`), `Connector`, `Map` |
| `ConnectionStrings:Gateway` | `Data Source=gateway.db` | SQLite database |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | Enables the OTLP exporter |

To point a route at a real system, add a connector such as `"xero-bridge": { "Kind": "webhook", "Url": "https://…", "Secret": "…" }` and reference it from a route.

## Tests

```bash
dotnet test
```

13 tests: backoff maths, payload mapping, HMAC signatures, the webhook connector, and end-to-end flows (fan-out, retry then success, dead-letter, replay, idempotency, signature rejection, validation errors) driven by a fake clock.

## License

[MIT](LICENSE) © 2026 Ohula Mulaudzi
