# ADR-002: Pluggable Storage Backends & High Availability

**Date:** 2026-07-11
**Status:** Accepted
**Relates to:** [ADR-001](001-tenant-auth-and-provisioning.md) (per-site credentials & provisioning)

> **Scope.** How PepperMill persists peppers and credentials, and how it runs redundantly. The store
> is chosen by config behind the existing `IPepperStore` / `ICredentialStore` interfaces; nothing
> above the store layer changes between backends.

## Decision

1. **Storage is a pluggable backend, selected by a `StorageProvider` config value** — the same
   "one codebase, pick an implementation" pattern as `IEntitlementProvider`. One setting drives *both*
   stores (peppers and credentials); mixing backends is not supported.

   | `StorageProvider` | Backing | For whom |
   |---|---|---|
   | **`File`** (default) | encrypted files in `StorePath` | self-hosters — zero dependencies, works out of the box |
   | **`Postgres`** | two tables in a Postgres database | anyone already running Postgres — reuse it, inherit its HA, no new bill; also removes the O(N) rotation scan |
   | **`S3`** | objects in an S3-compatible bucket | anyone with object storage but no Postgres |

2. **The master-key invariant holds for every backend: peppers are AES-256-GCM encrypted *app-side*, before they reach the backend.** Postgres and S3 therefore store only **ciphertext**; the master key never enters the store's trust domain. This is non-negotiable — it is the property customers pay for ("a copy of the store, without the master key, is useless"). We do **not** substitute Postgres TDE or S3 SSE for it, because those keep a plaintext-recoverable key inside the store's blast radius. Credential records remain hash-only (no encryption) on every backend.

3. **The pepper cipher is a shared component.** AES-256-GCM encrypt/decrypt of a `StoredPepper` ↔ bytes is extracted out of the file store into a shared helper so all backends encrypt identically and differ only in *where the encrypted blob lands*.

4. **HA is active/passive, and its feasibility depends on the backend.** Two nodes must serve the *same* pepper for a site (a visitor's hash must match across them), so the state must be shared or replicated — you cannot run two independent stores.

   - **Serving failover is an infrastructure concern, not PepperMill's.** A load balancer / orchestrator health-checks `GET /health` and routes traffic to a healthy node. PepperMill does not detect peer failure or elect a leader.
   - **The only shared-state hazard is the rotation *writer*** (the timer-driven sweep). With a backend that has real transactions/locking (**Postgres**), both nodes may run safely — the DB serializes writes. With **File** or **S3** (no cross-node write coordination), the rotation worker must run on **one** node only — gated by an `EnableRotationWorker` flag (default `true`; the standby sets `false`) or a leader lease.

5. **Backend guidance follows the deployment, and fail-open lowers the bar.** The client contract fails open — if PepperMill is unreachable the caller keeps ingesting with identity unknown for the outage — so PepperMill downtime degrades one signal rather than causing an outage. That means seamless HA is a *nice-to-have*, not a hard requirement, and a manual-promote window is acceptable.
   - **Self-host / single node:** `File`. Redundancy, if wanted, is a warm standby restored from backup.
   - **Have Postgres:** `Postgres` — cleanest HA (both nodes hit the same DB, no shared drive, no replication), and it also turns the rotation "sweep stale" into an indexed `WHERE epoch <> current` query instead of the O(N) full-store scan the file backend does hourly.
   - **Have object storage but no Postgres:** `S3` — genuinely shared, durable state without a shared drive; still needs single-writer rotation (flag/lease).

## Context

- **The file store cannot be shared across nodes on common hosting.** Block volumes (e.g. Linode/AWS) attach to one node at a time, and there is no managed shared filesystem (no EFS equivalent on Linode). So file-based HA requires replication (async lag + failback complexity), which we would rather avoid — pushing HA-minded operators toward Postgres or S3.
- **The interfaces already abstract storage.** `IPepperStore` / `ICredentialStore` predate this ADR; adding backends is additive and does not touch the service, endpoints, or entitlement layers.
- **Scale.** At a pepper-per-site, record counts are realistically thousands to tens of thousands, and traffic is a fetch-at-startup plus occasional tripwire re-fetch (far less chatty than API servers). Files are fast for point access at this scale; the only O(N) cost is the hourly rotation scan, which is the first thing to pinch as records climb — and which Postgres removes.

## Consequences

- The original "**no database**" stance becomes "**file by default; optional Postgres/S3 backends**." The *core* stays small and file-based; a database is opt-in behind the interface, never required.
- Postgres answers the HA **and** scale questions together for operators who have it, without compromising the self-hoster's zero-dependency default.
- Each backend pulls its dependency (Npgsql / an S3 SDK) only when selected.
- A new backend must uphold decision 2 (app-side pepper encryption) and the atomic-overwrite semantics that make "rotation destroys the prior pepper" real (Postgres: an `UPDATE`/upsert in a transaction; S3: an atomic `PUT` overwrite).

## Testing

- The `File` backend is covered by the existing suite (temp-dir round-trips).
- Backend implementations share a **contract test** (round-trip, overwrite-destroys-prior, delete, isolation by `(clusterId, tenantId, siteId)`) run against each available backend; the Postgres/S3 runs are **integration tests gated on a connection string / bucket env var**, skipped when absent (they need a real PG / bucket to verify).

## Open / deferred

- Automatic rotation-writer failover via a leader lease (File/S3) — deferred; the `EnableRotationWorker` flag covers the manual-promote case now.
- S3 backend — planned after Postgres.
- Read-replica / active-active serving — out of scope; active/passive with fail-open is sufficient at this scale.
