# Changelog

## [Unreleased]

### Features

- Callback-based tenant enrollment — `POST /v1/webhooks/provision` establishes a tenant's bearer credential (`key2`) via a callback handshake (PepperMill calls the client's `callbackUrl` with `key1`; the client returns `key2`), stored one-shot/locked; the outbound callback is SSRF-guarded by a `CallbackAllowedHosts` allowlist and refuses non-allowlisted or malformed URLs before any request is made
- Credential-derived tenant auth — `POST /v1/peppers/current` resolves the presented bearer against the enrolled tenant's stored hash; a credential is entitled only to its own tenant's sites (the body `tenantId` is a cross-check, never the authority). Replaces the shared `LocalServerCredential`, which is removed from config
- `POST /v1/webhooks/revoke` — destroys all of a tenant's peppers and removes its credential (tenant-level un-enroll, so the tenant can re-enroll), authorized by the tenant's current credential
- Per-tenant credential store (`ICredentialStore` / `FileCredentialStore`) — one JSON file per tenant (named by a hash of the tenant id) holding only a hash of the tenant's bearer credential plus non-secret provisioning metadata (callback URL, rotation cadence, lock); first slice of the ADR-001 tenant-auth work
- Multi-tenant peppers — a pepper is now keyed by the composite `(tenantId, siteId)`; a `siteId` is unique only within its tenant, so same-named sites across tenants are fully isolated. `tenantId` is now required on `POST /v1/peppers/current` and the provision/revoke webhooks, carried through the store (composite file-name hash), entitlement checks, and audit records
- Initial PepperMill service — the key-custody server from the spec: generate → store → serve → rotate → destroy per-site peppers
- `POST /v1/peppers/current` — returns a site's current-epoch pepper, gated by a bearer server credential and an entitlement check
- Encrypted-at-rest pepper store (`EncryptedFilePepperStore`, AES-256-GCM, one file per site named by a hash of the `(tenantId, siteId)` pair; master key held outside the store)
- Monthly epoch rotation — inherent in a fetch (a stale pepper is regenerated, destroying the prior value) plus an hourly background sweep so destruction happens on schedule even without a fetch
- Pluggable `IEntitlementProvider` — `Local` (resolves against enrolled per-tenant credentials) and `Platform` (external delegation, stubbed pending integration)
- Audit log of fetch / enroll / revoke events (metadata only — never pepper material)
- Fail-fast on a missing storage key outside Development; ephemeral key in Development (peppers do not survive restart)
- OpenAPI document (`/openapi/v1.json`) + Scalar interactive API reference (`/scalar/v1`)
- Pinned `Microsoft.OpenApi` to 2.10.0 to clear advisory GHSA-v5pm-xwqc-g5wc carried by the transitive 2.0.0
- Container support — multi-stage `Dockerfile` (non-root, `/health` HEALTHCHECK) and `docker-compose.yml` with a durable pepper-store volume and required-secret guards; adds `.env.example` and `.dockerignore`

### Docs

- ADR-001 (`docs/design/decisions/001-tenant-auth-and-provisioning.md`) — tenant authentication & pepper provisioning: the credential (not the client) decides the tenant, callback enrollment handshake with a one-shot lock and pinned callback URL, per-site peppers, file-based persistence (no database), and HTTPS recommended (not forced) for internal deployments
- `README.md`, operations guide (`docs/operations.md`), and the design spec (`docs/design/peppermill-spec.md`)
- User Guide (`docs/user-guide.md`) — task-oriented walkthrough: server setup, creating a site, fetching a pepper, health, Scalar UI, containerized run, operate/maintain, and troubleshooting
