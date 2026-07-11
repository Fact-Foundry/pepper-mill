# ADR-001: Tenant Authentication & Pepper Provisioning

**Date:** 2026-07-11
**Status:** Accepted

> **Scope.** This ADR is the source of truth for PepperMill's custody contract — how peppers are keyed, the provisioning/authentication protocol, and each side's responsibilities. It is written so a developer implementing the client (the calling TelemetryForge server) side can build their half against it directly. It covers *direction* only, not which side mints a given secret — the client generates `key2` (see decision 3).

## Decision

1. **A fetch is scoped by `(tenantId, siteId)`, and the credential — not the client — decides the tenant.** Because peppers are per site (decision 2), every pepper fetch carries **both** `tenantId` and `siteId`. The bearer credential (`key2`) resolves *server-side* to a tenant; the `tenantId` in the request body must equal the credential's tenant, or the request is rejected (`403`). `siteId` then selects which of that tenant's per-site peppers to return. The tenant is always derived from trusted server-side context — the credential — never accepted from the client, so a caller cannot read another tenant's pepper by changing a field in the body. (`siteId` is *not* a boundary between tenants — it only names a site *within* the already-authorized tenant; the tenant binding is what isolates.)

2. **Peppers are per *site*; the auth credential is per *tenant*.** Two different granularities that must not be conflated:
   - **Pepper** — identified by the composite `(tenantId, siteId)`. Created on demand (first fetch or explicit provision), rotated and destroyed per site. This is why a stolen current pepper is worth at most one month of one site, and why a tenant cannot silently correlate a visitor across its own sites.
   - **Credential (`key2`)** — one per tenant (in practice, per calling server). Established once, then used to fetch peppers for *any* site under that tenant.

3. **A tenant's credential is established by a callback handshake initiated by the client; the client generates `key2`.** PepperMill defines the protocol and verifies + stores the credential; the client mints and owns the credential value. The flow:
   1. Client → PepperMill `POST /v1/webhooks/provision`: `{ tenantId, callbackUrl, key1, rotationIntervalDays? }`.
   2. PepperMill → `callbackUrl`: presents `{ tenantId, key1 }`.
   3. Client verifies `key1` matches the request it just made and responds `{ key2 }` (the auth credential it generated).
   4. PepperMill stores `tenantId → { hash(key2), callbackUrl, rotationPolicy, locked: true }` and returns success to the step-1 caller.
   5. Client retains `key2` (its own environment) for all subsequent fetches.

   The `key1` echo proves to the client that the service calling its callback is the same PepperMill it just contacted; the round trip is fast, and should run over HTTPS (strongly recommended — see decision 7).

   **Client side (what the calling server must implement):** generate a fresh `key1` per provision request and a strong `key2` (≥ 256-bit CSPRNG) to return; expose the `callbackUrl` endpoint that checks the inbound `key1` against the pending request and responds with `{ key2 }`; persist `key2` for the tenant and send it as the bearer on every subsequent pepper fetch.

4. **Enrollment is one-shot and locked.** Once a tenant's credential is stored, further `provision` attempts for that tenant are rejected. Re-establishing a credential is an *update* (decision 5), not a re-provision — so a later caller cannot overwrite an enrolled tenant.

5. **Updates reuse the pinned callback URL and are authorized by the current credential.** The `callbackUrl` captured at enrollment is authoritative; any URL supplied on a later request is ignored, so a caller cannot inject a rogue callback. Updates fall into three kinds, only one of which re-runs the handshake:

   | Update | Callback? | Authorized by |
   |---|---|---|
   | Change rotation schedule | No | current `key2` — metadata write |
   | Force-rotate the pepper now | No | current `key2` — PepperMill owns pepper generation |
   | Rotate `key2` (new auth binding) | **Yes** — handshake to the *stored* `callbackUrl`; client returns a new `key2` | current `key2` to authorize; stored URL to complete |

   Requiring the *current* `key2` to trigger any update means only the already-enrolled server can rotate — which also gives operators a safe reset path for rebuilds, rotation, or a lost key without reopening the URL-injection surface.

6. **Rotation cadence is part of the provision contract now, but only monthly is implemented initially.** `rotationIntervalDays` (or an equivalent enum) is accepted at provision time so the wire format is future-proof, and defaults to monthly. Non-monthly cadences are **deferred** because they require generalizing the epoch from a stateless calendar month (`yyyy-MM`) to a stored **interval + anchor** (`intervalDays`, `nextRotatesAtUtc`) evaluated per site. Cadence follows the pepper's granularity — per site, with a tenant-level default permitted.

7. **PepperMill should be deployed internal / inbound-only; the handshake is the defense if it isn't.** The intended posture is a private network with no public IP, reachable only by trusted client servers. The callback handshake + one-shot lock exist precisely so that a wider-than-intended exposure is not immediately catastrophic — they raise the bar well above a static shared bearer (an attacker must complete a round trip, and cannot overwrite an already-enrolled tenant). They do **not** make public exposure safe (see Accepted risks); network isolation remains the primary control. The callback is also the one place PepperMill makes an *outbound* call, so it must be restricted to expected hosts (an allowlist / CIDR guard) so it cannot be turned into an SSRF primitive. **HTTPS is not forced** — an internal deployment may legitimately run plain HTTP on a trusted segment — but it is **strongly recommended**, and effectively required if the service is ever publicly exposed, since `key1`/`key2` would otherwise cross the wire in clear text.

## Context

- The service previously authenticated with a single shared bearer credential that was entitled to **every** tenant, and it accepted `tenantId` from the request body. A single compromised (or shared) credential could therefore read any tenant's pepper. Closing that is the core motivation.
- **PepperMill has no database.** Both peppers and credential records are held as files in the store directory — peppers encrypted per site; credential records holding only `hash(key2)` plus non-secret metadata — git-ignored like everything else there. Files (not SQLite/a DB) keep the service small and auditable, and preserve the atomic-overwrite that makes "rotation destroys the prior pepper" a real guarantee.
- The threat model is **"a trusted client server must never over-reach to another tenant."** PepperMill is meant to sit behind the network boundary; the credential model contains lateral movement *within* that boundary, and the handshake/lock add graceful degradation if the boundary is weaker than intended.
- This ADR describes the **Local** realization of `IEntitlementProvider` — the credential model shipped in this repo. A separate **Platform** entitlement provider (external delegation) is out of scope here.

## Accepted risks

- **Race-to-enroll.** Because tenant IDs are not secret and the `provision` endpoint is gated by the network boundary plus the one-shot lock (not a pre-shared secret), an attacker who can *reach the endpoint*, *knows the target `tenantId`*, and *acts before the legitimate server enrolls* could claim a tenant and lock the real server out. This is the residual risk the handshake does not close; it is accepted rather than defended with an operator-issued enrollment token. If the deployment assumption weakens materially (e.g. PepperMill routinely reachable from untrusted networks), revisit with a single-use enrollment token (see Open / deferred).
- **Callback availability coupling.** Enrollment (and `key2` rotation) require the client to be reachable from PepperMill at that moment. This affects provisioning only, never steady-state fetches.

## Why this shape (and not the alternatives considered)

- **Static shared bearer (env var) + network isolation only** — rejected: network isolation stops outsiders but a single shared credential still lets one compromised insider read every tenant's pepper, and a leak is total. Per-tenant credentials contain the blast radius; isolation is layered on top as defense-in-depth, not as a substitute.
- **Config-only credentials, no callback** — considered as the simpler shape (credentials as operator config), but rejected: the callback handshake + one-shot lock degrade far more gracefully if the service is ever more exposed than intended, and the extra enrollment step is small. This is a deliberate trade of a little setup friction for meaningfully better failure behavior.
- **PepperMill mints `key2` and delivers it via the callback** — rejected: the client already has to hold `key2` to use it as a bearer, so having the client generate it keeps the secret on the side that owns it and avoids PepperMill handing a credential outward. PepperMill still guarantees integrity by storing only `hash(key2)` and enforcing the tenant binding.
- **HMAC-derived credentials (`key2 = HMAC(master, tenantId)`)** — rejected: reproducible and non-revocable per tenant; one leaked master exposes all tenants (the same reason peppers are random, not derived).
- **Operator-issued single-use enrollment token → pull** — a stronger bootstrap (only the holder of an issued token can enroll), but judged heavier than the internal-first threat model warrants today. Documented as the upgrade path if the threat model widens.

## Scope of change

- **Credential store** — a per-tenant record `{ key2Hash, callbackUrl, rotationPolicy, locked }` persisted as files in the store directory (alongside the peppers; hashes only, no raw credentials). SHA-256 of the high-entropy credential; no slow KDF needed; compared constant-time (`CryptographicOperations.FixedTimeEquals`).
- **`IEntitlementProvider`** — `LocalEntitlementProvider` resolves a presented credential to its tenant and enforces `requestedTenantId == credentialTenantId`.
- **Provision endpoint** — the enrollment handshake (outbound callback, lock). New `callbackUrl`, `key1`, `rotationIntervalDays` fields.
- **New update/rotate operations** per decision 5.
- **Outbound HTTP client** for the callback, with a host allowlist (required); HTTPS supported and recommended, not enforced.
- **Config** — the callback allowlist / CIDR.
- **Epoch** stays `yyyy-MM` for now; the interval+anchor generalization is deferred with the non-monthly cadence.

## Consequences

- Cross-tenant reads become **structurally impossible** in steady state: a credential is bound to one tenant and the body's `tenantId` is only a cross-check, never the authority.
- The one-shot lock plus pinned-URL updates give controlled rotation and a safe operator reset, without a standing "switch the key" path an attacker could abuse.
- Every deployment enrolls the same way — a single code path.
- PepperMill gains an outbound network dependency (the callback) it did not have before — the single genuinely new attack surface, mitigated by the host allowlist and the inbound-only default (and HTTPS where configured).

## Testing

- Cross-tenant isolation: a credential for tenant A fetching `tenantId = B` returns `403`; the same `siteId` under two tenants yields distinct peppers (already covered at the store/service layer).
- Enrollment: a successful handshake stores `hash(key2)` and locks; a second provision for the same tenant is rejected; a request-supplied callback URL is ignored on update.
- Update taxonomy: schedule change and force-rotate require the current `key2` and do **not** call back; `key2` rotation calls the *stored* URL only.
- Callback guard: a callback URL outside the allowlist is refused (no outbound call made); a plain-HTTP callback is allowed but surfaces a warning.

## Open / deferred

- Non-monthly rotation cadences (interval + anchor epoch model) — deferred; contract field reserved now.
- Operator-issued single-use enrollment token — deferred; documented upgrade path if PepperMill's network trust boundary widens.
- Platform entitlement mode (external delegation) — separate work, out of scope here.
- mTLS as an alternative to bearer `key2` for per-server identity — deferred; compatible with this model if adopted later.
