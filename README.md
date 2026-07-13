# PepperMill

The key-custody service for the Fact Foundry / TelemetryForge ecosystem. PepperMill holds the
per-site **peppers** that key TelemetryForge's visitor-identity hashes, and serves them to
authenticated servers — so the secret that could reverse a visitor hash lives outside the
customer's infrastructure.

**New here?** The [**User Guide**](docs/user-guide.md) walks through setup, registering a site,
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
| `Local` (ships here) | resolves the presented credential against the registered site records |
| `Platform` | delegates to an external provider *(not yet implemented)* |

## Storage (pluggable backend)

Peppers and credentials live behind a pluggable store, chosen by `StorageProvider`:

| Backend | For whom |
|---|---|
| `File` (default) | self-hosters — encrypted files, zero dependencies |
| `Postgres` | shared/HA storage — both nodes point at one database ([ADR-002](docs/design/decisions/002-storage-backends-and-ha.md)) |

Peppers are AES-256-GCM encrypted **app-side before storage on either backend**, so the store only ever
holds ciphertext — the master key never reaches it.

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

Each **site** first **registers** — which creates its pepper and establishes its own bearer credential
(`key2`) — then fetches peppers with that credential:

```bash
# 1. Register a site. PepperMill calls your callbackUrl with key1; your endpoint replies { "key2": "<secret>" }.
curl -X POST http://localhost:<port>/v1/webhooks/provision \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"my-tenant","siteId":"my-site","callbackUrl":"http://localhost:9000/pepper-callback","key1":"<nonce>"}'

# 2. Fetch that site's pepper with the key2 its callback issued.
curl -X POST http://localhost:<port>/v1/peppers/current \
  -H "Authorization: Bearer <key2>" \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"my-tenant","siteId":"my-site"}'
# → { "pepper": "<base64>", "epoch": "2026-07", "rotatesAtUtc": "2026-08-01T00:00:00+00:00" }
```

Registration is a server-to-server handshake — your server exposes the callback that issues `key2`.
Each site has its own `key2`, so a leaked credential is scoped to one site. Every request also takes
an optional `clusterId` (defaults to `default`) — set it to segregate multiple independent clusters
served by one instance. The [User Guide](docs/user-guide.md) walks through it. For production, set a
real 32-byte storage key and configure `CallbackAllowedHosts` — see [`docs/operations.md`](docs/operations.md).

## Deploy to a server (no Docker)

`deploy/publish.sh` builds a **self-contained single-file binary** (bundles the runtime — nothing to
install on the server) plus a hardened `systemd` unit, env template, and one-shot installer:

```bash
bash deploy/publish.sh                          # → deploy/peppermill-linux-x64.tar.gz
# on the server:  tar xzf … && sudo ./install.sh && systemctl start peppermill
```

Or grab a package from a **[GitHub Release](../../releases)** — tagged versions ship `.rpm` / `.deb` /
`.pkg.tar.zst` (x64 + arm64), so a server install is just `sudo dnf install ./peppermill-….rpm` (or
`pacman -U` / `apt install`; installs the service, user, and hardening; you set the master key and
`systemctl start`). Full walkthrough in
[`docs/operations.md`](docs/operations.md#deploy-to-a-server-systemd-no-docker).

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
| `POST /v1/webhooks/provision` | Register a site — create its pepper + establish its credential via the callback handshake |
| `POST /v1/webhooks/revoke` | Revoke a site — destroy its pepper and un-register |
| `POST /v1/peppers/rotate` | Force-rotate a site's pepper now |
| `POST /v1/webhooks/rotate-credential` | Rotate a site's `key2` via the pinned callback |
| `POST /v1/tenants/schedule` | Update a site's rotation cadence |
| `GET /health` | Liveness probe |

## Security posture

- Peppers are **encrypted at rest** (AES-256-GCM) under a master key held outside the store; a copy
  of the files alone is useless.
- Peppers are **never logged**; the audit log records only metadata (fetch / register / revoke / rotate).
- Each credential is **scoped to one site** `(tenantId, siteId)` — the request body's ids are a claim
  that must match the credential, so a caller can't reach another site's pepper, and a leaked `key2`
  exposes a single site.
- The registration callback is **SSRF-guarded** by a host allowlist; credential rotation reuses the
  callback URL pinned at registration, never a request-supplied one.
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
