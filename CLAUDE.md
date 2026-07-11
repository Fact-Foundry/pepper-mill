# Claude Code Instructions for PepperMill

## Project Overview

PepperMill is the **key-custody service** for the Fact Foundry / TelemetryForge ecosystem. It
generates, stores (encrypted), serves, and monthly-rotates the per-site **peppers** that key
TelemetryForge's visitor-identity hashes — so the secret that could reverse a visitor hash lives
outside the customer's infrastructure. Licensed under AGPL-3.0.

It does one hard thing well: **generate → store → serve → rotate → destroy** peppers. *Who* is
entitled, billing, and audit UI are delegated (to `fact-foundry-platform` in the hosted edition),
not reimplemented here.

## First Steps

**Read `docs/design/peppermill-spec.md`** — the full design. Operational detail is in
`docs/operations.md`.

## Architecture

- .NET 10, ASP.NET Minimal API. No database — peppers are held in an encrypted file store.
- **OpenAPI + Scalar** for the interactive API reference (`/scalar/v1`); no Swashbuckle.
- One codebase, a pluggable `IEntitlementProvider`: `Local` (shared server credential, OSS) vs
  `Platform` (delegates to `fact-foundry-platform`, currently stubbed).

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
| `POST /v1/peppers/current` | Return a site's current pepper (bearer server credential + entitlement) |
| `POST /v1/webhooks/provision` | Ensure a site's pepper exists (platform lifecycle) |
| `POST /v1/webhooks/revoke` | Destroy a site's pepper (platform lifecycle) |
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
