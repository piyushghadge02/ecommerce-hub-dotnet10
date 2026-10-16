# E-Commerce Order Management — .NET 10 / ASP.NET Core Port

A behavioral port of the Python/FastAPI backend to **.NET 10 + ASP.NET Core (C#)**.
It implements the exact API contract in `PLAN.md` §4, so the existing Vue 3
frontend works unchanged — only the backend language differs.

Reference implementation: `ecommerce-polyglot-order-management/backend/`
(Python/FastAPI). Database schemas (§5–§7), sync design (§8) and seed rules (§9)
are ported 1:1.

## Architecture

```
src/ECommerce.Api/
  Config/          AppOptions (UPPER_SNAKE env overrides) + DSN translator
  Controllers/     /api/users /api/products /api/orders /api/search/orders
                   /api/health /api/admin/*
  Data/            PostgreSQL DDL (CREATE TABLE IF NOT EXISTS)
  Infrastructure/  snake_case JSON, money-as-string converter, error envelope
  Models/          DTOs mirroring the Pydantic schemas
  Repositories/    UsersRepository, ProductsRepository (MongoDB.Driver),
                   OrdersRepository (raw Npgsql ADO.NET, explicit transactions),
                   SearchRepository (Elasticsearch 8.x via raw transport JSON)
  Services/        OrderService (checkout), SyncService (dual-write/outbox/polling),
                   SyncWorker (BackgroundService)
  Tools/           SeedTool — deterministic seed (seed=42), reindex, verify
  Program.cs        `dotnet run` (API) | `dotnet run -- seed [--reset]` |
                   `dotnet run -- reindex`
tests/ECommerce.Tests/
  Unit/            money/rounding, snake_case, DSN, seed-plan rules,
                   ES query body shape, outbox backoff (xUnit)
  Integration/     WebApplicationFactory API tests against isolated
                   PG schema + Mongo db + ES index
```

Key implementation choices:

- **Checkout transaction** — `OrdersRepository.CreateOrderAsync` opens one
  `NpgsqlTransaction`; order + items (+ optional outbox row) commit atomically,
  rollback on any failure.
- **Money** — `decimal` / `NUMERIC(12,2)`; serialized as JSON strings
  (`"50.16"`) exactly like the Python API. `Money.FromDouble` replicates
  Python's `Decimal(str(v)).quantize(0.01, HALF_UP)`.
- **Elasticsearch** — `Elastic.Clients.Elasticsearch` **8.18.0** (the 9.x client
  is rejected by the ES 8.17.4 server; Python pins `elasticsearch>=8.17,<9`
  for the same reason). Queries go through the client's raw transport as JSON
  so the ES query body is byte-identical to the Python implementation.
- **Sync strategies** — `dual_write` (immediate index + outbox fallback +
  10 s repair loop), `outbox` (transactional outbox + worker with exponential
  backoff, 10 attempts → `failed`), `polling` (15 s watermark via `sync_state`).
  ES doc id is the deterministic `order-<id>`.
- **Snapshots** — order items store title/unit_price at checkout; catalog edits
  never rewrite history (PG) or ES docs (until reindex).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` → 10.x)
- PostgreSQL 16, MongoDB 8, Elasticsearch 8.17 (single-node, no auth) —
  or `docker compose up -d postgres mongodb elasticsearch`

## Quick start

```bash
# 1. Configure (optional — defaults match local dev DBs)
cp .env.example .env

# 2. Seed the demo data (deterministic, seed=42)
dotnet run --project src/ECommerce.Api -- seed
# reset everything first:  dotnet run --project src/ECommerce.Api -- seed --reset
# rebuild the ES index:    dotnet run --project src/ECommerce.Api -- reindex

# 3. Run the API (http://localhost:8000)
dotnet run --project src/ECommerce.Api

# 4. Run the tests
dotnet test
```

With Docker:

```bash
docker compose up -d --build
docker compose exec api dotnet ECommerce.Api.dll seed   # seed via the container
```

## Configuration

All settings come from `appsettings.json` and can be overridden by
environment variables of the same UPPER_SNAKE name:

| Variable | Default | Purpose |
|---|---|---|
| `POSTGRES_DSN` | `postgresql+asyncpg://ecom:ecom@localhost:5432/ecommerce` | PG (SQLAlchemy-style DSN accepted) |
| `MONGO_URI` | `mongodb://localhost:27017` | MongoDB |
| `MONGO_DB` | `ecommerce` | Mongo database |
| `ELASTICSEARCH_URL` | `http://localhost:9200` | Elasticsearch |
| `ES_INDEX` | `orders` | ES index name |
| `SYNC_STRATEGY` | `dual_write` | `dual_write` \| `outbox` \| `polling` |
| `OUTBOX_POLL_SECONDS` | `2` | outbox worker interval |
| `POLLING_INTERVAL_SECONDS` | `15` | polling worker interval |
| `OUTBOX_MAX_ATTEMPTS` | `10` | attempts before `failed` |
| `START_WORKERS` | `true` | set `false` to disable background workers |
| `API_HOST` / `API_PORT` | `0.0.0.0` / `8000` | Kestrel bind |

## API

Base URL `http://localhost:8000`. Full contract: `PLAN.md` §4.

| Method | Route | Notes |
|---|---|---|
| GET | `/api/health` | `{status, postgres, mongodb, elasticsearch, sync_strategy}` |
| GET | `/api/users` | list users |
| GET/POST | `/api/products`, `/api/products/{id}` | `?q=&category=&active=`; 409 on duplicate SKU |
| PUT | `/api/products/{id}` | partial update; never touches order history |
| POST | `/api/orders` | checkout → 201 (200 on idempotent replay) |
| GET | `/api/orders/{id}` | PG-canonical + `sync.es_in_sync` badge |
| PATCH | `/api/orders/{id}/status` | `{"status": "PROCESSING"}` |
| POST | `/api/search/orders` | ES-only search: `q`, filters, sort, pagination, aggs |
| POST | `/api/admin/reindex` | full ES rebuild |
| GET | `/api/admin/sync-status` | outbox pending/failed, ES doc count, watermark |
| POST | `/api/admin/reset-demo` | wipe demo data |

Money fields serialize as strings (`"total_amount": "119.98"`).
Errors use the FastAPI envelope: `{"detail": "..."}` with 400/404/409/422/503.

## Sync-strategy demo

```bash
# Terminal 1: run with the outbox strategy
SYNC_STRATEGY=outbox dotnet run --project src/ECommerce.Api

# Terminal 2: place an order -> es_sync.status == "queued",
# then watch the worker index it (or GET /api/admin/sync-status)
```

Kill Elasticsearch mid-checkout under `dual_write`: the order still commits to
Postgres (`es_sync.status == "pending_retry"`), an outbox row is enqueued, and
the 10 s repair loop indexes it once ES is back.

## Seed data (§9)

`dotnet run -- seed` creates (deterministically, seed=42):

- 8 users, 25 active + 1 inactive products (26 total)
- 44 orders / ≥80 items; status 15 PENDING / 15 PROCESSING / 14 SHIPPED
- Fixtures: ≥8 orders with “Wireless Mouse”, ≥3 with “Mechanical Keyboard”,
  ≥6 wireless-tagged products, price bands, 90-day date spread
- Then renames Mongo “Wireless Mouse” → “Wireless Mouse Pro” (new price) —
  PG/ES history keeps the original snapshot — and runs a full reindex.
- `seed` ends with a verification pass (counts, snapshot intact, ES == PG);
  it exits non-zero if anything is off.

## Troubleshooting

- **`dotnet test` hangs / testhost never connects** — some SDK installs don't
  ship `testhost.runtimeconfig.json` next to `testhost.dll`. The test project
  generates it at build time (`EnsureTestHostRuntimeConfig` target), so a
  plain `dotnet test` works.
- **Elasticsearch client version** — use 8.x (`Elastic.Clients.Elasticsearch`
  8.18.0). The 9.x client is refused by an 8.17 server.
- **ES connection refused in tests** — integration tests need the live DBs;
  they use isolated resources (`ecommerce_test`, `orders_test`, a temp PG
  schema) and never touch demo data.
- **Port 8000 in use** — set `API_PORT`.
- **Reindex shows fewer docs than PG rows** — `ReindexAllAsync` refreshes the
  index when done; give ES a second if you query mid-reindex.

## Deviations from PLAN.md

- Search and reindex return **503** (not 500) when Elasticsearch is unreachable
  — deliberate improvement; the frontend treats both as “ES down”.
- ES accessed via the 8.x client's raw transport with hand-built JSON, so the
  query/mapping is byte-identical to the Python implementation.
- Seed produces 90 items (Python produces 93); §9 requires ≥80 — both satisfy.
