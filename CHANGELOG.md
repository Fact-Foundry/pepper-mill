# Changelog

## [Unreleased]

### Features

- Pluggable storage backend (`StorageProvider`) — `File` (default, encrypted files, zero deps) or `Postgres` (two tables keyed by `(cluster_id, tenant_id, site_id)`, with an idempotent `IF NOT EXISTS` startup schema); one setting drives both the pepper and credential stores. Peppers are AES-256-GCM encrypted **app-side** via a shared `PepperCipher` before storage on either backend, so the store only ever holds ciphertext and the master key never reaches the database
- Optional `clusterId` namespace — peppers and credentials are now keyed by `(clusterId, tenantId, siteId)`; `clusterId` defaults to `"default"` and segregates otherwise same-named tenants/sites so one instance can serve multiple independent clusters. It is not a security boundary (client-supplied/spoofable), but being part of the key it segregates correctly — a credential registered under one cluster never resolves under another
- Callback-based site registration — `POST /v1/webhooks/provision` creates a site's pepper and establishes its per-site bearer credential (`key2`) via a callback handshake (PepperMill posts `{ clusterId, tenantId, siteId, key1 }` to the client's `callbackUrl`; the client returns `key2`), stored one-shot/locked; the outbound callback is SSRF-guarded by a `CallbackAllowedHosts` allowlist and refuses non-allowlisted or malformed URLs before any request is made. Sites are no longer implicit — a fetch for an unregistered site is `403`
- Per-site credential auth — `POST /v1/peppers/current` resolves the presented bearer against the registered site's stored hash; a credential is scoped to exactly one `(tenantId, siteId)`, so a **leaked `key2` exposes a single site** and the body ids are a claim that must match. Replaces the shared `LocalServerCredential`, which is removed from config
- `POST /v1/webhooks/revoke` — destroys a site's pepper and removes its credential (per-site un-register, so the site can re-register), authorized by the site's current credential
- Update/rotate operations — `POST /v1/peppers/rotate` (force-rotate a site's pepper now, returning the fresh one), `POST /v1/webhooks/rotate-credential` (issue a new `key2` via the callback URL *pinned at registration* — never a request-supplied one), and `POST /v1/tenants/schedule` (change a site's rotation cadence; stored, monthly-only honored); all authorized by the site's current credential
- Per-site credential store (`ICredentialStore` / `FileCredentialStore`) — one JSON file per site (named by a hash of the `(tenantId, siteId)` pair) holding only a hash of the site's bearer credential plus non-secret provisioning metadata (callback URL, rotation cadence, lock)
- Multi-tenant peppers — a pepper is now keyed by the composite `(tenantId, siteId)`; a `siteId` is unique only within its tenant, so same-named sites across tenants are fully isolated. `tenantId` is now required on `POST /v1/peppers/current` and the provision/revoke webhooks, carried through the store (composite file-name hash), entitlement checks, and audit records
- Initial PepperMill service — the key-custody server from the spec: generate → store → serve → rotate → destroy per-site peppers
- `POST /v1/peppers/current` — returns a site's current-epoch pepper, gated by the site's bearer credential and an entitlement check
- Encrypted-at-rest pepper store (`EncryptedFilePepperStore`, AES-256-GCM, one file per site named by a hash of the `(tenantId, siteId)` pair; master key held outside the store)
- Monthly epoch rotation — inherent in a fetch (a stale pepper is regenerated, destroying the prior value) plus an hourly background sweep so destruction happens on schedule even without a fetch
- Pluggable `IEntitlementProvider` — `Local` (resolves against registered per-site credentials) and `Platform` (external delegation, stubbed pending integration)
- Audit log of fetch / register / revoke / rotate events (metadata only — never pepper material)
- Fail-fast on a missing storage key outside Development; ephemeral key in Development (peppers do not survive restart)
- OpenAPI document (`/openapi/v1.json`) + Scalar interactive API reference (`/scalar/v1`)
- Pinned `Microsoft.OpenApi` to 2.10.0 to clear advisory GHSA-v5pm-xwqc-g5wc carried by the transitive 2.0.0
- Container support — multi-stage `Dockerfile` (non-root, `/health` HEALTHCHECK) and `docker-compose.yml` with a durable pepper-store volume and required-secret guards; adds `.env.example` and `.dockerignore`

### Docs

- ADR-001 (`docs/design/decisions/001-tenant-auth-and-provisioning.md`) — per-site credentials & pepper provisioning: the credential (not the client) decides the site, callback registration handshake with a one-shot lock and pinned callback URL, per-site peppers/credentials keyed by `(clusterId, tenantId, siteId)`, and HTTPS recommended (not forced) for internal deployments
- ADR-002 (`docs/design/decisions/002-storage-backends-and-ha.md`) — pluggable storage backends (File/Postgres, S3 planned) and active/passive HA: the app-side pepper-encryption invariant for every backend, and the Linode no-shared-drive constraint that motivates Postgres for shared/HA storage
- `README.md`, operations guide (`docs/operations.md`), and design decisions (`docs/design/decisions/`)
- User Guide (`docs/user-guide.md`) — task-oriented walkthrough: server setup, creating a site, fetching a pepper, health, Scalar UI, containerized run, operate/maintain, and troubleshooting
