# PepperMill

The key-custody service for the Fact Foundry / TelemetryForge ecosystem. PepperMill holds the
per-site **peppers** that key TelemetryForge's visitor-identity hashes, and serves them to
authenticated servers — so the secret that could reverse a visitor hash lives outside the
customer's infrastructure.

**New here?** The [**User Guide**](docs/user-guide.md) walks through setup, enrolling a tenant,
fetching a pepper, health checks, the Scalar test UI, and running it in a container.

Design decisions live in [`docs/design/decisions/`](docs/design/decisions); operations reference:
[`docs/operations.md`](docs/operations.md).

## What it does

A pepper is the secret in `HMAC-SHA256(pepper, IP)`. Without it, a stolen analytics database can't
be rainbow-tabled back to IP addresses. PepperMill does one hard thing well:

**generate → store (encrypted) → serve → rotate + destroy** peppers, monthly.

Everything else — *who* is entitled, and any UI around it — is delegated to a pluggable entitlement
provider, not reimplemented here.

## Why a separate service

- **Trust domain.** "The key never lives in your infrastructure" only holds if the pepper lives
  somewhere your breach can't reach — a separate service in a distinct trust domain from the analytics
  server it serves.
- **Blast radius.** A custody service should be small, auditable, and boring; keeping it apart from the
  analytics server keeps its attack surface minimal.
- **Independent lifecycle.** Peppers rotate monthly on their own schedule, unentangled from analytics
  deploys.

## Entitlement (pluggable provider)

*Who* may fetch a pepper is decided by a pluggable `IEntitlementProvider`:

| Provider | Entitlement |
|---|---|
| `Local` (ships here) | resolves the presented credential against the enrolled tenant records |
| `Platform` | delegates to an external provider *(not yet implemented)* |

## Quick start (dev)

```bash
cd src/FactFoundry.PepperMill
dotnet run
```

In `Development` the storage key is **ephemeral** (peppers won't survive a restart — dev only) and
`localhost` / `127.0.0.1` are allowlisted as callback hosts. Open the interactive API reference:

```
http://localhost:<port>/scalar/v1        # Scalar — try the endpoints in the browser
http://localhost:<port>/openapi/v1.json  # raw OpenAPI document
```

A tenant first **enrolls** to establish its bearer credential (`key2`), then fetches peppers with it:

```bash
# 1. Enroll. PepperMill calls your callbackUrl with key1; your endpoint replies { "key2": "<secret>" }.
curl -X POST http://localhost:<port>/v1/webhooks/provision \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"my-tenant","callbackUrl":"http://localhost:9000/pepper-callback","key1":"<nonce>"}'

# 2. Fetch a pepper with the key2 your callback issued.
curl -X POST http://localhost:<port>/v1/peppers/current \
  -H "Authorization: Bearer <key2>" \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"my-tenant","siteId":"my-site"}'
# → { "pepper": "<base64>", "epoch": "2026-07", "rotatesAtUtc": "2026-08-01T00:00:00+00:00" }
```

Enrollment is a server-to-server handshake — your server exposes the callback that issues `key2`.
The [User Guide](docs/user-guide.md) walks through it. For production, set a real 32-byte storage key
and configure `CallbackAllowedHosts` — see [`docs/operations.md`](docs/operations.md).

## Run with Docker

```bash
cp .env.example .env          # then set a storage key in .env
docker compose up -d --build
curl http://localhost:5130/health
```

Full container notes (volume/backups, ports, health) are in the
[User Guide](docs/user-guide.md#6-run-it-as-a-container).

## API

| Method & path | Purpose |
|---|---|
| `POST /v1/peppers/current` | Return a site's current pepper (bearer `key2`) |
| `POST /v1/webhooks/provision` | Enroll a tenant — establish its credential via the callback handshake |
| `POST /v1/webhooks/revoke` | Revoke a tenant — destroy its peppers and un-enroll |
| `POST /v1/peppers/rotate` | Force-rotate a site's pepper now |
| `POST /v1/webhooks/rotate-credential` | Rotate a tenant's `key2` via the pinned callback |
| `POST /v1/tenants/schedule` | Update a tenant's rotation cadence |
| `GET /health` | Liveness probe |

## Security posture

- Peppers are **encrypted at rest** (AES-256-GCM) under a master key held outside the store; a copy
  of the files alone is useless.
- Peppers are **never logged**; the audit log records only metadata (fetch / enroll / revoke / rotate).
- A credential decides its tenant — the request body's `tenantId` is a cross-check, never the
  authority, so a caller can't reach another tenant's pepper.
- The enrollment callback is **SSRF-guarded** by a host allowlist; credential rotation reuses the
  callback URL pinned at enrollment, never a request-supplied one.
- Rotation is a **destruction ceremony** — the prior epoch's pepper is overwritten, never archived.
- The store keeps **only the current epoch** per site.

## Out of scope (for now)

- Bring-your-own-KMS (HSM / cloud KMS) backends for the store
- Multi-region custody / HA
- Non-monthly rotation cadences — the contract field is reserved; see [ADR-001](docs/design/decisions/001-tenant-auth-and-provisioning.md)
- The `Platform` entitlement provider (external delegation)
- Any analytics or bot/security logic — that lives in the client

## Build & test

```bash
dotnet build PepperMill.slnx
dotnet test PepperMill.slnx
```

## License

AGPL-3.0-or-later. See [`LICENSE`](LICENSE).
