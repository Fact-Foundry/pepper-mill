# ADR-001: Per-Site Credentials & Pepper Provisioning

**Date:** 2026-07-11
**Status:** Accepted

> **Scope.** This ADR is the source of truth for PepperMill's custody contract — how peppers and
> credentials are keyed, the registration/authentication protocol, and each side's responsibilities.
> It is written so a developer implementing the client (the calling TelemetryForge server) side can
> build their half against it directly. It covers *direction* only, not which side mints a given
> secret — the client generates `key2` (see decision 3).

## Decision

1. **A fetch is scoped by `(clusterId, tenantId, siteId)`, and the credential — not the client — decides which site it may read.** Every pepper fetch carries `tenantId`, `siteId`, an optional `clusterId` (see below), plus a bearer credential (`key2`). PepperMill looks up the credential record for that `(clusterId, tenantId, siteId)` and requires the presented `key2` to hash to the stored hash; otherwise `403`. So the body's ids are a *claim* that only succeeds when backed by the matching credential — a caller cannot reach another site's (or tenant's, or cluster's) pepper by changing a field in the body.

2. **Both the pepper and the credential are per *site*, keyed by `(clusterId, tenantId, siteId)`.** A `siteId` is unique only within its tenant.
   - **`clusterId`** is an **optional top-level namespace**, defaulting to `"default"` when omitted. It exists so one PepperMill instance can serve **multiple independent clusters** without their same-named tenants/sites colliding onto the same pepper or credential. It is **not a security boundary** — it is client-supplied and spoofable — but because it is part of the *key*, a credential registered under one cluster simply does not resolve under another (different record), so it segregates correctly without being relied on for auth.
   - **Pepper** — created when the site is registered (and lazily on fetch as a fallback), rotated and destroyed per site. A stolen current pepper is worth at most one month of one site, and a tenant cannot silently correlate a visitor across its own sites.
   - **Credential (`key2`)** — one per site, established at registration. **A leaked `key2` exposes exactly one site** — not the whole tenant. This is the reason for per-site (rather than per-tenant) credentials: `key2` is the durable secret (it lives in the client's config/env), so it is the most leak-prone artifact, and scoping it to one site bounds the blast radius of a leak to a single site's rotation.

3. **A site's credential is established by a callback handshake initiated by the client; the client generates `key2`, and PepperMill creates the pepper.** PepperMill defines the protocol, verifies + stores the credential, and provisions the pepper; the client mints and owns the credential value. The flow:
   1. Client → PepperMill `POST /v1/webhooks/provision`: `{ tenantId, siteId, callbackUrl, key1, rotationIntervalDays? }`.
   2. PepperMill → `callbackUrl`: presents `{ tenantId, key1 }`.
   3. Client verifies `key1` matches the request it just made and responds `{ key2 }` (the auth credential it generated).
   4. PepperMill stores `(clusterId, tenantId, siteId) → { hash(key2), callbackUrl, rotationPolicy, locked: true }`, **creates the site's pepper**, and returns success to the step-1 caller.
   5. Client retains `key2` (its own environment) for all subsequent fetches of that site.

   The `key1` echo proves to the client that the service calling its callback is the same PepperMill it just contacted; the round trip is fast, and should run over HTTPS (strongly recommended — see decision 7). This matches the client's site-setup flow: registering a site *is* the act of creating its pepper.

   **Client side (what the calling server must implement):** per site, generate a fresh `key1` per registration request and a strong `key2` (≥ 256-bit CSPRNG); expose the `callbackUrl` endpoint that checks the inbound `key1` against the pending request and responds with `{ key2 }`; persist `key2` for that site and send it as the bearer on every subsequent fetch of that site.

4. **Registration is one-shot and locked, per site.** Once a site's credential is stored, further `provision` attempts for that site are rejected (`409`). Re-establishing a credential is an *update* (decision 5), not a re-registration — so a later caller cannot overwrite a registered site. A consequence: **sites are not implicit.** A fetch for a site that never registered returns `403`; the pepper is created at registration, not conjured on first fetch by an unauthenticated caller.

5. **Updates reuse the pinned callback URL and are authorized by the site's current credential.** The `callbackUrl` captured at registration is authoritative; any URL supplied on a later request is ignored, so a caller cannot inject a rogue callback. Updates fall into three kinds, only one of which re-runs the handshake:

   | Update | Callback? | Authorized by |
   |---|---|---|
   | Change rotation schedule | No | the site's current `key2` — metadata write |
   | Force-rotate the pepper now | No | the site's current `key2` — PepperMill owns pepper generation |
   | Rotate `key2` (new auth binding) | **Yes** — handshake to the *stored* `callbackUrl`; client returns a new `key2` | the site's current `key2` to authorize; stored URL to complete |

   Requiring the *current* `key2` to trigger any update means only the already-registered site can rotate — which also gives operators a safe reset path for rebuilds, rotation, or a lost key without reopening the URL-injection surface. (A full un-register + destroy-pepper is `POST /v1/webhooks/revoke`, also authorized by the site's `key2`.)

6. **Rotation cadence is part of the registration contract now, but only monthly is implemented initially.** `rotationIntervalDays` (or an equivalent enum) is accepted at registration so the wire format is future-proof, and defaults to monthly. Non-monthly cadences are **deferred** because they require generalizing the epoch from a stateless calendar month (`yyyy-MM`) to a stored **interval + anchor** (`intervalDays`, `nextRotatesAtUtc`) evaluated per site.

7. **PepperMill should be deployed internal / inbound-only; the handshake is the defense if it isn't.** The intended posture is a private network with no public IP, reachable only by trusted client servers. The callback handshake + one-shot lock exist precisely so that a wider-than-intended exposure is not immediately catastrophic — they raise the bar well above a static shared bearer (an attacker must complete a round trip, and cannot overwrite an already-registered site). They do **not** make public exposure safe (see Accepted risks); network isolation remains the primary control. The callback is also the one place PepperMill makes an *outbound* call, so it must be restricted to expected hosts (an allowlist / CIDR guard) so it cannot be turned into an SSRF primitive. **HTTPS is not forced** — an internal deployment may legitimately run plain HTTP on a trusted segment — but it is **strongly recommended**, and effectively required if the service is ever publicly exposed, since `key1`/`key2` would otherwise cross the wire in clear text.

## Context

- The service previously authenticated with a single shared bearer credential entitled to **every** tenant and site, accepting `tenantId` from the request body. A single compromised (or shared) credential could read any pepper. Closing that is the core motivation.
- **Storage is file-based by default.** Peppers and credential records are held as files in the store directory — peppers encrypted per site; credential records holding only `hash(key2)` plus non-secret metadata — git-ignored like everything else there. Files keep the default deployment small and auditable and preserve the atomic-overwrite that makes "rotation destroys the prior pepper" a real guarantee. A pluggable Postgres backend is available for shared/HA storage — see [ADR-002](002-storage-backends-and-ha.md).
- The threat model is **"a trusted client server must never over-reach to another site or tenant, and a leaked credential must not cascade."** PepperMill is meant to sit behind the network boundary; the per-site credential contains lateral movement *within* that boundary, and the handshake/lock add graceful degradation if the boundary is weaker than intended.
- This ADR describes the **Local** realization of `IEntitlementProvider` — the credential model shipped in this repo. A separate **Platform** entitlement provider (external delegation) is out of scope here.

## Accepted risks

- **Race-to-register.** Because tenant/site IDs are not secret and the `provision` endpoint is gated by the network boundary plus the one-shot lock (not a pre-shared secret), an attacker who can *reach the endpoint*, *knows the target `(tenantId, siteId)`*, and *acts before the legitimate server registers* could claim a site and lock the real server out. This is the residual risk the handshake does not close; it is accepted rather than defended with an operator-issued registration token. If the deployment assumption weakens materially (e.g. PepperMill routinely reachable from untrusted networks), revisit with a single-use registration token (see Open / deferred).
- **Callback availability coupling.** Registration (and `key2` rotation) require the client to be reachable from PepperMill at that moment. This affects provisioning only, never steady-state fetches.

## Why this shape (and not the alternatives considered)

- **Per-tenant credential (one `key2` for all a tenant's sites)** — rejected. It's operationally lighter (one registration per tenant, sites implicit on first fetch), but a *leaked* `key2` — the common failure, since the token lives in the client's env/config and ends up in logs/backups/git — would expose **every** site under the tenant at once and force a tenant-wide rotation. Per-site credentials scope a leak to one site. The cost is per-site registration (sites are no longer implicit); accepted as worth it.
- **mTLS / client-cert binding as the leak mitigation instead** — would let a leaked `key2` be useless without the cert, keeping credentials coarse. Deferred: it's real operational weight (cert issuance/rotation) we chose not to take on now. Left as the upgrade path (see Open / deferred).
- **Static shared bearer (env var) + network isolation only** — rejected: isolation stops outsiders but one shared credential lets a single leak read everything. Per-site credentials contain the blast radius; isolation is layered on as defense-in-depth.
- **PepperMill mints `key2` and delivers it via the callback** — rejected: the client already holds `key2` to use it as a bearer, so having the client generate it keeps the secret on the side that owns it. PepperMill guarantees integrity by storing only `hash(key2)` and enforcing the `(tenant, site)` binding.
- **HMAC-derived credentials (`key2 = HMAC(master, tenant, site)`)** — rejected: reproducible and non-revocable per site; one leaked master exposes everything (the same reason peppers are random, not derived).
- **Operator-issued single-use registration token → pull** — a stronger bootstrap (only the holder of an issued token can register), but judged heavier than the internal-first threat model warrants today. Documented as the upgrade path if the threat model widens.

## Scope of change

- **Credential store** — a per-site record `{ clusterId, tenantId, siteId, key2Hash, callbackUrl, rotationPolicy, locked }` keyed by `(clusterId, tenantId, siteId)`, persisted via the configured backend (File or Postgres; hashes only, no raw credentials). SHA-256 of the high-entropy credential; no slow KDF; compared constant-time (`CryptographicOperations.FixedTimeEquals`).
- **`IEntitlementProvider`** — `LocalEntitlementProvider` resolves a presented credential against the `(clusterId, tenantId, siteId)` record.
- **Provision endpoint** — the registration handshake (outbound callback, lock) that also creates the site's pepper. Body: `{ tenantId, siteId, callbackUrl, key1, rotationIntervalDays? }`.
- **Revoke / update / rotate operations** — all per site (decision 5).
- **Outbound HTTP client** for the callback, with a host allowlist (required); HTTPS supported and recommended, not enforced.
- **Config** — the callback allowlist / CIDR.
- **Epoch** stays `yyyy-MM` for now; the interval+anchor generalization is deferred with the non-monthly cadence.

## Consequences

- Cross-site and cross-tenant reads become **structurally impossible** in steady state: a credential is bound to one `(tenant, site)`; the body's ids are only a claim that must match.
- **A leaked `key2` is contained to a single site** — the incident response is one site's rotation, not a tenant-wide one.
- **Sites are explicit.** Each site registers before it can be fetched (a fetch for an unregistered site is `403`), and registration is where its pepper is created. This is a behavior change from the earlier "sites appear on first fetch."
- The one-shot lock plus pinned-URL updates give controlled rotation and a safe operator reset, without a standing "switch the key" path an attacker could abuse.
- PepperMill gains an outbound network dependency (the callback) — the single genuinely new attack surface, mitigated by the host allowlist and the inbound-only default (and HTTPS where configured).

## Testing

- Per-site isolation: a credential for `(A, blog)` fetching `(A, shop)` returns `403`; a credential for tenant A can't claim tenant B; the same `siteId` under two tenants yields distinct peppers and credentials.
- Registration: a successful handshake stores `hash(key2)`, locks, and creates the pepper; a second provision for the same site returns `409`; a fetch for an unregistered site returns `403`; a request-supplied callback URL is ignored on update.
- Update taxonomy: schedule change and force-rotate require the site's current `key2` and do **not** call back; `key2` rotation calls the *stored* URL only and retires the old key.
- Revoke: destroys the site's pepper and credential; a subsequent fetch is `403`.
- Callback guard: a callback URL outside the allowlist is refused (no outbound call made); a plain-HTTP callback is allowed but surfaces a warning.

## Open / deferred

- Non-monthly rotation cadences (interval + anchor epoch model) — deferred; contract field reserved now.
- Operator-issued single-use registration token — deferred; documented upgrade path if PepperMill's network trust boundary widens.
- Platform entitlement mode (external delegation) — separate work, out of scope here.
- mTLS / client-cert binding — deferred; would let credentials be coarser again by making a leaked `key2` insufficient on its own. Compatible with this model if adopted later.
