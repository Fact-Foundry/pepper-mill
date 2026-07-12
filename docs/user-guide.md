# PepperMill — User Guide

A step-by-step walkthrough for running and operating PepperMill (with the Local entitlement mode):
set up the server, register sites, fetch peppers, and keep it healthy.

This guide is task-oriented. For the reference tables (every config key, the client contract, the
security rationale) see [`operations.md`](operations.md); for the design rationale see
[`design/decisions/`](design/decisions).

> **What PepperMill is, in one line:** a headless key-custody API. It generates, encrypts, serves, and
> monthly-rotates a per-site secret "pepper" so the key that could reverse a visitor hash lives outside
> your analytics box. There is **no web UI** — everything below is the API and a bit of config.

---

## Contents

1. [Set up the server](#1-set-up-the-server)
2. [Register a site](#2-register-a-site)
3. [Fetch a pepper](#3-fetch-a-pepper)
4. [Check server health](#4-check-server-health)
5. [Run the Scalar UI for testing](#5-run-the-scalar-ui-for-testing)
6. [Run it as a container](#6-run-it-as-a-container)
7. [Operate & maintain](#7-operate--maintain)
8. [Troubleshooting](#8-troubleshooting)

---

## 1. Set up the server

### Prerequisites

- **.NET 10 SDK** (to build/run from source) — or **Docker** (see [§6](#6-run-it-as-a-container), the easiest path).
- One secret to keep out of source control: the **storage key** (the master key that encrypts peppers at rest).
  Site credentials are **not** configured here — each site establishes its own during [registration](#2-register-a-site).

### Option A — run from source (development)

```bash
cd src/FactFoundry.PepperMill
dotnet run
```

In `Development` everything needed is preconfigured:

- the storage key is **ephemeral** — a fresh one is generated each start, so **peppers do not survive a
  restart**. That is intentional for dev; a warning is logged at startup.
- `localhost` and `127.0.0.1` are **allowlisted as callback hosts**, so you can run a local registration
  callback without extra config.

The console prints the URL (default **`http://localhost:5130`**). Note that `/` returns **404 by design** —
PepperMill is an API, not a site. Confirm it's up with [`/health`](#4-check-server-health).

### Option B — configure for real use (production)

Outside `Development`, PepperMill **refuses to start without a storage key** — a custody service must not
silently lose the key that gives peppers continuity.

**1. Generate a 32-byte storage key** (base64):

```bash
head -c 32 /dev/urandom | base64
```

**2. Supply configuration out-of-band** — environment variables (note the `__` double-underscore that maps to
the `PepperMill:` config section), never committed to source:

```bash
export PepperMill__StorageKeyBase64="<base64-of-32-bytes>"
export PepperMill__CallbackAllowedHosts__0="tf-server-1.internal"   # hosts PepperMill may call back
export PepperMill__StorePath="/var/lib/peppermill"                  # durable path; default is ./peppers
export ASPNETCORE_ENVIRONMENT="Production"

dotnet FactFoundry.PepperMill.dll
```

| Setting | Meaning | Required |
|---|---|---|
| `PepperMill__StorageKeyBase64` | base64 of a **32-byte** AES-256 key that encrypts peppers at rest | **yes (outside Development)** |
| `PepperMill__CallbackAllowedHosts__N` | hostnames PepperMill may call back to during registration (SSRF guard); empty ⇒ registration refused | **yes (to register)** |
| `PepperMill__StorageProvider` | storage backend: `File` (encrypted files, zero deps) or `Postgres` (shared/HA — see [operations.md](operations.md)) | no — default `File` |
| `PepperMill__PostgresConnectionString` | Postgres connection string (schema created idempotently at startup) | **yes, if `StorageProvider = Postgres`** |
| `PepperMill__StorePath` | directory for the encrypted pepper files, credential records, and `audit.log` (File backend; audit log lives here on any backend) | no — default `peppers` |
| `PepperMill__EntitlementMode` | `Local` (this guide) or `Platform` (external delegation, not implemented) | no — default `Local` |

> **Keep it internal.** PepperMill is meant to run on a private network, reachable only by your servers.
> HTTPS is strongly recommended (and essential if it's ever more exposed) but not forced — an internal
> segment may legitimately run plain HTTP. Run PepperMill in a **separate trust domain** from your analytics
> servers — that separation is the whole point.

---

## 2. Register a site

Before a server can fetch a site's pepper, that **site** is registered once — which creates its pepper and
establishes its own bearer credential (`key2`). Registration is a **server-to-server handshake**, not a manual
step — but here's exactly what happens so you can implement the client side. (In TelemetryForge this is the
"create pepper" option at site setup.)

**The handshake:**

1. Your server exposes a **callback endpoint** that, given `{ clusterId, tenantId, siteId, key1 }`, verifies
   `key1` against the request it just initiated and responds `{ "key2": "<a strong random secret it generates and stores>" }`.
2. Your server triggers registration for the site:

   ```bash
   curl -X POST http://localhost:5130/v1/webhooks/provision \
     -H "Content-Type: application/json" \
     -d '{"tenantId":"acme","siteId":"acme-blog","callbackUrl":"https://tf-server-1.internal/pepper-callback","key1":"<fresh-nonce>"}'
   # → 200 OK   (PepperMill called your callbackUrl, got key2, stored only its hash, and created the pepper)
   ```

3. PepperMill calls your `callbackUrl` with `{ clusterId, tenantId, siteId, key1 }`, receives `key2`, stores
   **only a hash** of it **locked** to that site, and creates the site's pepper. Your server keeps `key2` for
   every future fetch of that site — hold it wherever your servers read secrets (an env var, a secret store,
   or an encrypted per-site record so any node can read it). PepperMill only ever stores its hash, and doesn't
   care where you keep your copy.

Notes:

- **Each site has its own `key2`.** Register each site separately; a leaked credential is then scoped to a
  single site, not the whole tenant.
- **`callbackUrl`'s host must be allowlisted** (`CallbackAllowedHosts`), or registration is refused with `403`
  before any outbound call — this is the SSRF guard.
- **Registration is one-shot.** A second `provision` for an already-registered site returns `409`. To
  re-register (rebuild, lost key), [revoke](#removing-a-site) the site first, or rotate its credential (below).
- **The client, not PepperMill, generates `key2`** — so its strength is on you; use a 256-bit CSPRNG value.

### Rotating a site's credential

To issue a fresh `key2` without un-registering — PepperMill calls back to the **URL pinned at registration**
(never a request-supplied one), authorized by the site's current `key2`:

```bash
curl -X POST http://localhost:5130/v1/webhooks/rotate-credential \
  -H "Authorization: Bearer $CURRENT_KEY2" \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"acme","siteId":"acme-blog","key1":"<fresh-nonce>"}'
# → 200 OK   (your callback returned a new key2; the old one stops working)
```

### Removing a site

Destroy a site's pepper and un-register it (e.g. on cancellation), authorized by its current `key2`:

```bash
curl -X POST http://localhost:5130/v1/webhooks/revoke \
  -H "Authorization: Bearer $CURRENT_KEY2" \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"acme","siteId":"acme-blog"}'
# → 200 OK   (pepper deleted; site can register again)
```

---

## 3. Fetch a pepper

This is the endpoint your servers call at startup, with the site's `key2` from registration. A site is
identified by a `tenantId` **and** a `siteId`; a `siteId` is unique only **within its tenant**, so the same
`siteId` under two tenants is two isolated peppers. The site must be **registered first**
([§2](#2-register-a-site)) — a fetch for an unregistered site returns `403`.

```bash
curl -X POST http://localhost:5130/v1/peppers/current \
  -H "Authorization: Bearer $KEY2" \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"acme","siteId":"acme-blog"}'
```

Response:

```json
{
  "pepper": "<base64-encoded 256-bit secret>",
  "epoch": "2026-07",
  "rotatesAtUtc": "2026-08-01T00:00:00+00:00"
}
```

The credential is scoped to one site: `key2` is valid only for the exact `(tenantId, siteId)` it was registered
for — changing either to something it wasn't registered for returns `403`. So a leaked `key2` exposes a single
site's pepper, nothing more.

> **Multiple clusters?** Every request (register, fetch, revoke, rotate) also accepts an optional `clusterId`,
> defaulting to `"default"`. Set it if one PepperMill instance serves several **independent** clusters — the
> same `tenantId`/`siteId` under different `clusterId`s are fully segregated (distinct peppers and credentials).
> It's a namespace, not a security control, so you can leave it unset for a single-cluster deployment.

To force a fresh pepper for a site immediately (destroying the current one), call `POST /v1/peppers/rotate`
with the same body and bearer; it returns the new pepper.

**The client contract** (what a well-behaved server does with a fetched pepper):

- Hold the pepper **in memory only** — never write it to disk, logs, or config.
- **Re-fetch after `rotatesAtUtc`** to pick up the next epoch's pepper.
- Re-fetch periodically as a **tripwire** — an *unexpected* pepper change is an incident signal.
- **Fail open for identity only:** if PepperMill is unreachable, keep ingesting analytics with
  "first visit" marked unknown for the outage. Never block analytics; never retain raw IPs to backfill.

**Responses you may get:**

| Status | Meaning |
|---|---|
| `200` | pepper returned |
| `400` | `tenantId` or `siteId` missing from the body |
| `401` | no/malformed `Bearer` credential |
| `403` | credential not valid for this tenant — logged as `pepper.fetch.denied` |

---

## 4. Check server health

A liveness probe — no auth, safe to hit from a load balancer or `docker` healthcheck:

```bash
curl http://localhost:5130/health
# → {"status":"ok"}
```

Use this (not `/`, which is a deliberate 404) to confirm the server is up.

---

## 5. Run the Scalar UI for testing

PepperMill ships **Scalar**, the modern replacement for Swagger UI — an interactive API reference you can
drive from the browser. It's intended for development/testing.

- Interactive reference: **`http://localhost:5130/scalar/v1`**
- Raw OpenAPI document: **`http://localhost:5130/openapi/v1.json`**

**To call the authenticated endpoints from Scalar:**

1. Open `/scalar/v1`.
2. Register a site first (a fetch needs a `key2`), then find the **Authentication** panel and set the bearer
   token to that site's `key2`.
3. Open `POST /v1/peppers/current`, set the body to `{ "tenantId": "acme", "siteId": "acme-blog" }`, and **Send**.

> Scalar is a dev convenience. In production, keep it behind your proxy/auth or disable public access — it
> exposes your API shape, though never any pepper material.

---

## 6. Run it as a container

The repo ships a `Dockerfile` and `docker-compose.yml`. This is the simplest way to run a durable instance.

```bash
# 1. Create your secrets file (gitignored) and fill in real values
cp .env.example .env
# edit .env:
#   PEPPERMILL_STORAGE_KEY=$(head -c 32 /dev/urandom | base64)

# 2. Build and start
docker compose up -d --build

# 3. Verify
curl http://localhost:5130/health          # → {"status":"ok"}
docker compose ps                           # STATUS shows "healthy" after a few seconds
```

Details worth knowing:

- The container listens on **8080** internally; compose maps it to **5130** on your host (so the URLs above
  are unchanged). Adjust the `ports:` line to serve elsewhere.
- The store persists in a named volume (`peppermill-data` → `/data`) — encrypted peppers, credential records,
  and `audit.log`. It survives `docker compose down` — **back it up** (see below). `down -v` deletes it.
- Compose **won't start without the storage key** (the `:?` guard in the compose file), matching the server's
  own fail-fast. The secret comes from `.env` / the environment — never bake it into the image.
- The image runs as a **non-root** user and includes a `HEALTHCHECK` that polls `/health`.

To view logs / stop:

```bash
docker compose logs -f peppermill
docker compose down          # stop (keeps the volume/peppers)
```

---

## 7. Operate & maintain

### The master key vs. the peppers — keep them straight

- The **peppers** are the per-site secrets PepperMill serves. They live **encrypted** in `StorePath`.
- The **master key** (`StorageKeyBase64`) encrypts those files. It is **not** in the store.

A backup of `StorePath` alone is useless without the master key — that's the design. Store the master key
in a secret manager, separate from the store backups.

- **Rotating the master key** re-keys the encryption, not the peppers themselves; there is no built-in
  re-encrypt command, so treat the master key as long-lived. **Losing it** makes existing peppers
  unrecoverable — which for identity data only means visitor-return detection resets (no analytics or
  behavioral data is affected).

### Backups

- Back up `StorePath` — it holds every site's encrypted pepper, the per-site credential records, **and**
  `audit.log`. Peppers are encrypted at rest, so a backup is safe to store but is worthless (by design)
  without the separately-held master key.
- In Docker, back up the volume, e.g.:
  ```bash
  docker run --rm -v peppermill-data:/data -v "$PWD":/backup alpine \
    tar czf /backup/peppermill-data.tgz -C /data .
  ```

### Rotation & destruction (automatic — nothing to schedule)

- Peppers rotate every **calendar month** (epoch `yyyy-MM`, UTC); the next one takes over at `00:00 UTC on
  the 1st`.
- Rotation happens two ways that agree: **lazily** on any fetch (a stale pepper is regenerated, overwriting
  the old value), and via an **hourly background sweep** so the prior pepper is destroyed on schedule even
  for sites that aren't fetching.
- The prior epoch is **destroyed, not archived** — a stolen current pepper is worth at most one month of one
  site. On the TelemetryForge side, "returning visitor" therefore means *within the current month*; the
  metric visibly resets at each boundary (expected, not a bug).

### Auditing

Every fetch / register / revoke / rotate appends a JSON line to `StorePath/audit.log` with **metadata only**
(timestamp, event, tenant/site id, epoch) — never pepper or credential material. Ship or rotate this file with
your normal log tooling; watch for `pepper.fetch.denied` (a credential failing entitlement).

### Deployment checklist

- [ ] `StorageKeyBase64` set (32 bytes) and held in a secret manager, **not** with the store backups.
- [ ] `CallbackAllowedHosts` set to the exact hosts of your servers' registration callbacks.
- [ ] `StorePath` on **durable** storage, backed up.
- [ ] PepperMill on a private network in a **separate trust domain** from analytics; HTTPS in front (recommended).
- [ ] `/health` wired to your liveness probe.
- [ ] Scalar (`/scalar/v1`) not publicly exposed in production.

---

## 8. Troubleshooting

| Symptom | Cause & fix |
|---|---|
| **`http://localhost:5130/` returns 404** | Expected — there's no page at `/`. Use `/health`, `/scalar/v1`, or the `/v1/...` endpoints. |
| **Server won't start; "StorageKeyBase64 … is required outside Development"** | You're in `Production` (or non-Development) with no key. Set `PepperMill__StorageKeyBase64` to base64 of 32 bytes. |
| **"must decode to exactly 32 bytes"** | Your key isn't 32 bytes. Regenerate: `head -c 32 /dev/urandom \| base64`. |
| **Startup warns "EPHEMERAL storage key (Development only)"** | You're in Development with no key — peppers won't survive a restart. Fine for dev; set a real key otherwise. |
| **Registration returns `403 callbackUrl is not permitted`** | The callback host isn't allowlisted. Add it to `PepperMill__CallbackAllowedHosts`. |
| **Registration returns `409`** | The site is already registered. Revoke it first, or use `rotate-credential`. |
| **Registration returns `502`** | PepperMill couldn't reach your callback, or it didn't return `{ "key2": ... }`. Check the callback is up, reachable, and responds correctly. |
| **`403 Not entitled for this site`** | The `key2` isn't valid for this `(tenantId, siteId)` (wrong credential, or the site isn't registered). |
| **`401 Missing bearer credential`** | Add `-H "Authorization: Bearer <key2>"`. |
| **Peppers vanished after restart** | Development ephemeral key, or (in Docker) the volume was removed (`down -v`). Set a persistent key and keep the volume. |
| **A site's pepper changed unexpectedly mid-month** | Check for a month boundary (UTC), a `revoke`, or a `peppers/rotate`. On the client side, an unexpected change is a deliberate tripwire signal. |
| **`docker compose up` errors that `PEPPERMILL_STORAGE_KEY` is unset** | Create `.env` from `.env.example` and set the storage key. |
