# Changelog

## [Unreleased]

### Features

- Initial PepperMill service — the key-custody server from the spec: generate → store → serve → rotate → destroy per-site peppers
- `POST /v1/peppers/current` — returns a site's current-epoch pepper, gated by a bearer server credential and an entitlement check
- Encrypted-at-rest pepper store (`EncryptedFilePepperStore`, AES-256-GCM, one file per site named by a hash of the site id; master key held outside the store)
- Monthly epoch rotation — inherent in a fetch (a stale pepper is regenerated, destroying the prior value) plus an hourly background sweep so destruction happens on schedule even without a fetch
- Pluggable `IEntitlementProvider` — `Local` (shared server credential, OSS edition) and `Platform` (delegates to `fact-foundry-platform`, stubbed pending integration)
- Audit log of fetch / provision / revoke events (metadata only — never pepper material)
- Platform lifecycle webhooks `POST /v1/webhooks/{provision,revoke}` — stubs pending platform integration
- Fail-fast on a missing storage key outside Development; ephemeral key in Development (peppers do not survive restart)
- OpenAPI document (`/openapi/v1.json`) + Scalar interactive API reference (`/scalar/v1`)
- Pinned `Microsoft.OpenApi` to 2.10.0 to clear advisory GHSA-v5pm-xwqc-g5wc carried by the transitive 2.0.0
- Container support — multi-stage `Dockerfile` (non-root, `/health` HEALTHCHECK) and `docker-compose.yml` with a durable pepper-store volume and required-secret guards; adds `.env.example` and `.dockerignore`

### Docs

- `README.md`, operations guide (`docs/operations.md`), and the design spec (`docs/design/peppermill-spec.md`)
- User Guide (`docs/user-guide.md`) — task-oriented walkthrough: server setup, creating a site, fetching a pepper, health, Scalar UI, containerized run, operate/maintain, and troubleshooting
