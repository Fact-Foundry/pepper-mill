# PepperMill

The key-custody service for the Fact Foundry / TelemetryForge ecosystem. PepperMill holds the
per-site **peppers** that key TelemetryForge's visitor-identity hashes, and serves them to
authenticated TelemetryForge servers — so the secret that could reverse a visitor hash lives
outside the customer's infrastructure.

**New here?** The [**User Guide**](docs/user-guide.md) walks through setup, creating a site, fetching a
pepper, health checks, the Scalar test UI, and running it in a container.

Full design: [`docs/design/peppermill-spec.md`](docs/design/peppermill-spec.md). Operations reference:
[`docs/operations.md`](docs/operations.md).

## What it does

A pepper is the secret in `HMAC-SHA256(pepper, IP)`. Without it, a stolen analytics database can't
be rainbow-tabled back to IP addresses. PepperMill does one hard thing well:

**generate → store (encrypted) → serve → rotate + destroy** peppers, monthly.

Everything else — *who* is entitled, billing, audit UI — is delegated (to `fact-foundry-platform`
in the hosted edition), not reimplemented here.

## Editions (one codebase, a pluggable entitlement provider)

| | OSS (Local) | Hosted (Platform) |
|---|---|---|
| Runs | on the operator's infra | in Fact Foundry's infra |
| Entitlement | a shared server credential in config | delegated to `fact-foundry-platform` *(not yet implemented)* |
| Delivers "key not in your infra" | no (it's your box) — but adds managed rotation | **yes** |

## Quick start (Local / dev)

```bash
cd src/FactFoundry.PepperMill
dotnet run
```

In `Development` a config credential (`dev-server-credential`) is preconfigured and the storage key
is **ephemeral** (peppers won't survive a restart — dev only). Open the interactive API reference:

```
http://localhost:<port>/scalar/v1        # Scalar — try the endpoints in the browser
http://localhost:<port>/openapi/v1.json  # raw OpenAPI document
```

Fetch a pepper:

```bash
curl -X POST http://localhost:<port>/v1/peppers/current \
  -H "Authorization: Bearer dev-server-credential" \
  -H "Content-Type: application/json" \
  -d '{"siteId":"my-site"}'
# → { "pepper": "<base64>", "epoch": "2026-07", "rotatesAtUtc": "2026-08-01T00:00:00+00:00" }
```

For anything beyond dev, set a real 32-byte storage key and credential — see
[`docs/operations.md`](docs/operations.md).

## Run with Docker

```bash
cp .env.example .env          # then set a storage key + credential in .env
docker compose up -d --build
curl http://localhost:5130/health
```

Full container notes (volume/backups, ports, health) are in the
[User Guide](docs/user-guide.md#6-run-it-as-a-container).

## API

| Method & path | Purpose |
|---|---|
| `POST /v1/peppers/current` | Return a site's current pepper (bearer server credential + entitlement) |
| `POST /v1/webhooks/provision` | Ensure a site's pepper exists (platform lifecycle) |
| `POST /v1/webhooks/revoke` | Destroy a site's pepper (platform lifecycle) |
| `GET /health` | Liveness probe |

## Security posture

- Peppers are **encrypted at rest** (AES-256-GCM) under a master key held outside the store; a copy
  of the files alone is useless.
- Peppers are **never logged**; the audit log records only metadata (fetch/provision/revoke).
- Rotation is a **destruction ceremony** — the prior epoch's pepper is overwritten, never archived.
- The store keeps **only the current epoch** per site.

## Build & test

```bash
dotnet build PepperMill.slnx
dotnet test PepperMill.slnx
```

## License

AGPL-3.0-or-later. See [`LICENSE`](LICENSE).
