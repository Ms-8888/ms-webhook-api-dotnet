# ms-webhook-api-dotnet

[![CI](https://github.com/Ms-8888/ms-webhook-api-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/Ms-8888/ms-webhook-api-dotnet/actions/workflows/ci.yml)

A .NET 8 port of [ms-webhook-api](https://github.com/Ms-8888/ms-webhook-api), a webhook delivery API originally written with FastAPI. The HTTP routes, status codes, header names and snake_case JSON field names follow the Python version, so the companion dashboard ([ms-webhook-ui](https://github.com/Ms-8888/ms-webhook-ui)) is meant to work against either backend. I checked this by reading the dashboard's API client (`src/lib/api.ts`), and I ran the dashboard in a browser against this port (see below).

An application sends one event to the API and gets `202 Accepted` back. A background worker then POSTs the event payload to every endpoint the tenant has registered, signs each request, and retries failures on a schedule.

## What is implemented

- ASP.NET Core 8 minimal APIs, EF Core 8 with Npgsql, an `Initial` EF Core migration.
- Redis (StackExchange.Redis) list `deliveries:queue` as the delivery queue.
- Two `BackgroundService` workers in the API process:
  - dispatch worker: pops delivery ids from Redis and attempts delivery
  - retry worker: every 30 seconds, re-queues deliveries whose `next_retry_at` has passed
- Retry schedule: attempt 1 immediately, then +1 minute, +5 minutes, +30 minutes; after the fourth failed attempt the delivery is `failed`. The next retry time is stored in the `deliveries` table, so retries survive a restart (the Redis queue itself is not relied on for them).
- Signing: each request carries `X-Webhook-Signature: sha256=<hex>` where the value is `HMAC-SHA256(endpoint_secret, "<timestamp>." + body)`, plus `X-Webhook-Timestamp` (unix seconds) and `X-Event-Type`. Same scheme as the Python version.
- SSRF guard: endpoint URLs must be http or https and must not be localhost or a private, loopback, link-local, unspecified, multicast or reserved address (IPv4 and IPv6, including IPv4-mapped IPv6). Host names are resolved at registration and every returned address is checked. The outbound HTTP client also resolves and checks the address again when it connects, and does not follow redirects or use a proxy.
- API keys are stored as SHA-256 hashes and looked up by hash. Raw keys are never stored.
- Rate limit: 100 events per hour per tenant, counted in Redis (`SET NX` with a 1 hour TTL plus `INCR` in one transaction). Over the limit returns `429` with `Retry-After`.
- `GET /health` (database and Redis ping) and `GET /metrics`, `GET /metrics/chart`.
- CORS allow-list for the dashboard origins, same as the Python version.

### API

| Method | Path | Notes |
|--------|------|-------|
| `POST` | `/endpoints` | body `{"url", "description"?}`, returns `201` |
| `GET` | `/endpoints` | active endpoints of the tenant, newest first |
| `DELETE` | `/endpoints/{id}` | soft delete, `204`; `404` for another tenant's endpoint |
| `POST` | `/events` | body `{"event_type", "payload"}`, returns `202 {"event_id", "queued_deliveries"}` |
| `GET` | `/events?page=1` | 20 per page, newest first |
| `GET` | `/events/{id}/deliveries` | delivery status per endpoint |
| `GET` | `/health` | no API key needed |
| `GET` | `/metrics`, `/metrics/chart` | per tenant, last 24 hours |

All routes except `/health` need the `X-API-Key` header. A missing header returns `422`, an unknown or inactive key returns `401` with `{"detail": "Invalid API key"}`, as in the Python version.

### Differences from the Python version

- Tables are created by EF Core migrations (applied on startup against PostgreSQL), not `create_all`.
- Registration also rejects host names that cannot be resolved; the Python version only checked literal IPs and `localhost`.
- A delivery handed to the queue by the retry worker gets a 5 minute lease on `next_retry_at`, so a slow dispatch is not queued a second time on the next 30 second scan.
- `page < 1` returns `422` instead of a database error.
- No demo tenant is created automatically. Use the `seed` command below.
- Payload is stored as `jsonb` in the `events` table, but the text sent to receivers (and signed) is the JSON text as the client sent it, so whitespace can differ from what the Python version sends.

## Run with Docker Compose

```bash
docker compose up -d --build
docker compose exec app dotnet WebhookApi.dll seed   # prints a demo API key once

To use a key you already have (for example the shared demo key from the Python version, which is hashed the same way), pass it in `SEED_API_KEY`. Running it twice does nothing the second time:

```bash
docker compose exec -e SEED_API_KEY=<your key> app dotnet WebhookApi.dll seed
```
curl localhost:8000/health
```

```bash
curl -X POST localhost:8000/endpoints \
  -H "X-API-Key: <key>" -H "Content-Type: application/json" \
  -d '{"url": "https://webhook.site/your-id"}'

curl -X POST localhost:8000/events \
  -H "X-API-Key: <key>" -H "Content-Type: application/json" \
  -d '{"event_type": "order.created", "payload": {"order_id": 42}}'
```

The compose file starts PostgreSQL 16, Redis 7 and the app. Database and Redis ports are not published to the host.

## Run with dotnet run

You need the .NET 8 SDK, a PostgreSQL database and a Redis server.

```bash
cp .env.example .env     # then export the variables; the app reads DATABASE_URL and REDIS_URL from the environment
export DATABASE_URL=postgresql://postgres:postgres@localhost:5432/webhooks
export REDIS_URL=redis://localhost:6379
dotnet run --project src/WebhookApi            # listens on http://localhost:8000
dotnet run --project src/WebhookApi -- seed    # creates a demo tenant and prints its API key
```

`DATABASE_URL` accepts a `postgres://` or `postgresql://` URL (a `+asyncpg` suffix and `?sslmode=require` are tolerated, as in the Python version) or a plain Npgsql connection string. Migrations are applied on startup.

## Run the tests

```bash
dotnet test
```

The tests use `WebApplicationFactory` and run the real application. They do not need PostgreSQL, Redis or the network:

- the database is in-memory **SQLite**, not PostgreSQL (the `jsonb` column is `TEXT` there, and the PostgreSQL migration is not exercised by the tests)
- Redis is replaced by an in-memory implementation of the queue and rate limit counter, so the Redis code (`RedisQueueStore`) is not covered by the automated tests
- DNS and the outbound webhook call are replaced by fakes; time is controlled with a fake clock
- the background workers are removed in tests; `DeliveryProcessor` and `RetryScanner`, which they call, are tested directly

What is tested: endpoint create, list, delete and tenant isolation; missing, wrong and inactive API keys; hashed key storage; SSRF rejection for many URL forms, including host names that resolve to private addresses and a connect-time check; event creation and queuing; pagination; delivery listing; the 100 per hour rate limit and its per-tenant scope; the HMAC signature against an independently computed value; the 1, 5 and 30 minute retry schedule, final `failed` state, and re-queuing from stored state; health and metrics; configuration parsing.

What is not tested or not done:

- not load tested; no performance numbers
- not tested against real PostgreSQL or real Redis by the automated tests
- the dashboard was only checked on the Overview and Events pages; the Endpoints page was not exercised, because the container used for the check could not resolve outside host names to register an endpoint
- never deployed
- the dispatch worker handles one delivery at a time, and workers run inside the API process (no separate worker service, no horizontal scaling)
- if the Redis push fails after the database commit, a first-attempt delivery stays `pending` and is not recovered automatically
- no automated test makes a real outbound HTTP request

## Layout

```
src/WebhookApi/        API, EF Core model and migrations, workers
tests/WebhookApi.Tests/  xUnit tests
Dockerfile, docker-compose.yml
.github/workflows/     CI (restore, build, test) and gitleaks
```

## License

MIT, see `LICENSE`.

## What was run by hand

On one Windows machine with Docker Desktop, `docker compose up -d --build` built the image and started all three containers. Against that stack I confirmed `/health` returned `{"status":"ok","db":"ok","redis":"ok"}`, the migration created the four tables with a `jsonb` payload column, `seed` printed a key, `POST /events` with that key returned `202`, an endpoint pointing at `localhost` was rejected with `422`, a bad key returned `401`, and the 101st event in an hour returned `429` with `Retry-After` (so the Redis rate limit works). The sandbox had no outbound network access, so no delivery to a real receiver was made: a delivery to a public IP timed out, was recorded with `attempt_count` 1 and a `next_retry_at` about one minute later, and the retry worker re-queued it after that time. Delivery success against a real HTTP receiver was only exercised through the fake sender in the automated tests.

## Dashboard check

I ran ms-webhook-ui (non-mock mode, `VITE_MOCK=false`) in Chrome against this API started with `docker compose up`, using a key from the `seed` command and three events sent with `curl`. The Overview page showed 3 events today, and the Events page listed the three events. The browser console reported no errors. No delivery receiver was registered, so the delivery views were not checked.

![Dashboard Events page running against this API](docs/dashboard-events.png)
