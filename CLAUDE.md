# Claude Code Instructions for PepperMill

## Project Overview

PepperMill is the **key-custody service** for the Fact Foundry / TelemetryForge ecosystem. It
generates, stores (encrypted), serves, and monthly-rotates the per-site **peppers** that key
TelemetryForge's visitor-identity hashes — so the secret that could reverse a visitor hash lives
outside the customer's infrastructure. Licensed under AGPL-3.0.

It does one hard thing well: **generate → store → serve → rotate → destroy** peppers. *Who* is
entitled, and any UI around it, is delegated to a pluggable entitlement provider, not reimplemented
here.

## First Steps

**Read `docs/design/decisions/`** (start with ADR-001) and `docs/user-guide.md` for the current
auth/provisioning model. Operational detail is in `docs/operations.md`.

## Architecture

- .NET 10, ASP.NET Minimal API. No database — peppers and per-tenant credential records are held as
  files in an encrypted store (peppers encrypted at rest; credential records hold hashes only).
- **OpenAPI + Scalar** for the interactive API reference (`/scalar/v1`); no Swashbuckle.
- A tenant enrolls via a callback handshake to establish its bearer credential (`key2`); a fetch
  resolves that credential to a tenant, so the request body's `tenantId` is a cross-check, never the
  authority. Peppers are per `(tenantId, siteId)`; the credential is per tenant.
- One codebase, a pluggable `IEntitlementProvider`: `Local` (resolves against enrolled tenant
  credentials) vs `Platform` (external delegation, currently stubbed).

## Project Structure

| Project | Purpose |
|---|---|
| `src/FactFoundry.PepperMill` | The custody service (Minimal API) |
| `tests/FactFoundry.PepperMill.Tests` | xUnit unit + endpoint tests |

## Build Commands

- **Build:** `dotnet build PepperMill.slnx`
- **Tests:** `dotnet test PepperMill.slnx`
- **Run (dev):** `dotnet run --project src/FactFoundry.PepperMill`

## API Endpoints

| Endpoint | Purpose |
|---|---|
| `POST /v1/peppers/current` | Return a site's current pepper (bearer `key2` → tenant) |
| `POST /v1/peppers/rotate` | Force-rotate a site's pepper now (bearer `key2`) |
| `POST /v1/webhooks/provision` | Enroll a tenant — establish `key2` via the callback handshake |
| `POST /v1/webhooks/revoke` | Revoke a tenant — destroy its peppers and un-enroll (bearer `key2`) |
| `POST /v1/webhooks/rotate-credential` | Rotate a tenant's `key2` via the pinned callback (bearer `key2`) |
| `POST /v1/tenants/schedule` | Update a tenant's rotation cadence (bearer `key2`) |
| `GET /health` | Liveness probe |

## Coding Standards

- **XML comments required on all public APIs** — public classes, methods, and properties need `/// <summary>`.
- Secrets (peppers, master key) are generated with `RandomNumberGenerator` — never `Random`, never `Guid`.
- **Peppers are never logged** and never appear in audit records — audit metadata only.
- Peppers are **encrypted at rest** (AES-256-GCM); the master key lives outside the store.
- The store holds **only the current epoch** per site — rotation overwrites (destroys) the prior value; never archive it.
- All catch blocks log meaningful error context — never swallow exceptions silently.

## Workflow Rules

- **Do not commit, push, or tag** unless explicitly asked.
- **Never commit peppers, keys, or the store** — `peppers/`, `*.pepper`, `audit.log`, and key material are git-ignored; keep it that way.
- **Log all changes in `CHANGELOG.md`** under the current unreleased version (Features / Fixes / Docs), one concise line each.

## Testing Rules

- **Run tests after every change** — `dotnet test` before reporting a change complete.
- **Never silently fix a failing test** — if a change invalidates a test, STOP and flag it; the test encodes intended behavior.
- **Add tests for new logic with branches** — rotation, entitlement, encryption, and error paths need coverage.
