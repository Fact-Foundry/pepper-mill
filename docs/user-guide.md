# PepperMill — User Guide

A step-by-step walkthrough for running and operating PepperMill (the OSS / Local edition):
set up the server, create sites, fetch peppers, and keep it healthy.

This guide is task-oriented. For the reference tables (every config key, the client contract, the
security rationale) see [`operations.md`](operations.md); for the architecture see
[`design/peppermill-spec.md`](design/peppermill-spec.md).

> **What PepperMill is, in one line:** a headless key-custody API. It generates, encrypts, serves, and
> monthly-rotates a per-site secret "pepper" so the key that could reverse a visitor hash lives outside
> your analytics box. There is **no web UI** — everything below is the API and a bit of config.

---

## Contents

1. [Set up the server](#1-set-up-the-server)
2. [Set up a new site](#2-set-up-a-new-site)
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
- A place to keep two secrets out of source control: the **storage key** and the **server credential**.

### Option A — run from source (development)

```bash
cd src/FactFoundry.PepperMill
dotnet run
```

In `Development` everything needed is preconfigured:

- the server credential is **`dev-server-credential`**, and
- the storage key is **ephemeral** — a fresh one is generated each start, so **peppers do not survive a
  restart**. That is intentional for dev; a warning is logged at startup.

The console prints the URL (default **`http://localhost:5130`**). Note that `/` returns **404 by design** —
PepperMill is an API, not a site. Confirm it's up with [`/health`](#4-check-server-health).

### Option B — configure for real use (production)

Outside `Development`, PepperMill **refuses to start without a storage key** — a custody service must not
silently lose the key that gives peppers continuity. You must provide two values.

**1. Generate a 32-byte storage key** (base64):

```bash
head -c 32 /dev/urandom | base64
```

**2. Generate a strong server credential** (any long random string):

```bash
head -c 32 /dev/urandom | base64
```

**3. Supply them out-of-band** — environment variables (note the `__` double-underscore that maps to the
`PepperMill:` config section), never committed to source:

```bash
export PepperMill__StorageKeyBase64="<base64-of-32-bytes>"
export PepperMill__LocalServerCredential="<long-random-credential>"
export PepperMill__StorePath="/var/lib/peppermill"   # durable path; default is ./peppers
export ASPNETCORE_ENVIRONMENT="Production"

dotnet FactFoundry.PepperMill.dll
```

| Setting | Meaning | Required |
|---|---|---|
| `PepperMill__StorageKeyBase64` | base64 of a **32-byte** AES-256 key that encrypts peppers at rest | **yes (outside Development)** |
| `PepperMill__LocalServerCredential` | the shared bearer credential your servers present | **yes (Local mode)** |
| `PepperMill__StorePath` | directory for the encrypted pepper files + `audit.log` | no — default `peppers` |
| `PepperMill__EntitlementMode` | `Local` (this guide) or `Platform` (hosted, delegates to fact-foundry-platform) | no — default `Local` |

> **Put TLS in front of it.** The credential is a bearer token; terminate HTTPS at a reverse proxy (or the
> platform ingress) and run PepperMill in a **separate trust domain** from your analytics servers — that
> separation is the whole point.

---

## 2. Set up a new site

**There is no registration step and no admin UI.** A site is identified by a `tenantId` **and** a `siteId`
string you choose (e.g. tenant `acme`, site `acme-blog`). A `siteId` is unique only **within its tenant**, so
the same `siteId` under two different tenants is two isolated peppers — cross-tenant collisions are impossible.
A site comes into existence the first time PepperMill sees that `(tenantId, siteId)` pair — either when a
server **fetches** its pepper, or when you **provision** it explicitly. Each site gets its own isolated,
independently-rotated pepper, stored in its own encrypted file.

To create a site up front (e.g. right after a customer signs up), call **provision**:

```bash
curl -X POST http://localhost:5130/v1/webhooks/provision \
  -H "Authorization: Bearer $CREDENTIAL" \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"acme","siteId":"acme-blog"}'
# → 200 OK   (pepper generated and stored if it didn't exist)
```

That's it — `acme/acme-blog` now has a current-epoch pepper. Use different `(tenantId, siteId)` pairs for
different sites; they never share a pepper. (In this Local edition, a valid credential is entitled to **any**
tenant and site — you operate both PepperMill and the servers, so you trust yourself. The Hosted edition swaps
in per-tenant, per-site subscription checks.)

To **remove** a site — destroy its pepper (e.g. on cancellation):

```bash
curl -X POST http://localhost:5130/v1/webhooks/revoke \
  -H "Authorization: Bearer $CREDENTIAL" \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"acme","siteId":"acme-blog"}'
# → 200 OK   (pepper file deleted)
```

---

## 3. Fetch a pepper

This is the endpoint your TelemetryForge servers call at startup. It returns the site's **current-epoch**
pepper, creating one on the fly if the site is new.

```bash
CREDENTIAL="dev-server-credential"   # or your real one

curl -X POST http://localhost:5130/v1/peppers/current \
  -H "Authorization: Bearer $CREDENTIAL" \
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

**The client contract** (what a well-behaved TelemetryForge server does with this):

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
| `401` | no/!`Bearer` credential |
| `403` | credential not entitled (wrong credential in Local mode) — logged as `pepper.fetch.denied` |

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
2. Find the **Authentication** panel and set the bearer token (in dev: `dev-server-credential`).
3. Open `POST /v1/peppers/current`, set the body to `{ "tenantId": "my-tenant", "siteId": "my-site" }`, and **Send**.

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
#   PEPPERMILL_CREDENTIAL=$(head -c 32 /dev/urandom | base64)

# 2. Build and start
docker compose up -d --build

# 3. Verify
curl http://localhost:5130/health          # → {"status":"ok"}
docker compose ps                           # STATUS shows "healthy" after a few seconds
```

Details worth knowing:

- The container listens on **8080** internally; compose maps it to **5130** on your host (so the URLs above
  are unchanged). Adjust the `ports:` line to serve elsewhere.
- The pepper store persists in a named volume (`peppermill-data` → `/data`). It survives
  `docker compose down` — **back it up** (see below). `down -v` deletes it and all peppers.
- Compose **won't start without both secrets** (the `:?` guards in the compose file), matching the server's
  own fail-fast. Secrets come from `.env` / the environment — never bake them into the image.
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

- Back up `StorePath` — it holds every site's encrypted pepper **and** `audit.log`. It's already encrypted
  at rest, so a backup is safe to store, but is worthless (by design) without the separately-held master key.
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

Every fetch / provision / revoke appends a JSON line to `StorePath/audit.log` with **metadata only**
(timestamp, event, site id, epoch) — never pepper material. Ship or rotate this file with your normal log
tooling; watch for `pepper.fetch.denied` (a credential failing entitlement).

### Deployment checklist

- [ ] `StorageKeyBase64` set (32 bytes) and held in a secret manager, **not** with the store backups.
- [ ] `LocalServerCredential` set to a long random value and distributed only to your servers.
- [ ] `StorePath` on **durable** storage, backed up.
- [ ] TLS terminated in front; PepperMill in a **separate trust domain** from analytics.
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
| **`403 Not entitled for this site`** | Wrong/missing credential. In dev it's `dev-server-credential`; otherwise match `LocalServerCredential`. |
| **`401 Missing bearer server credential`** | Add `-H "Authorization: Bearer <credential>"`. |
| **Peppers vanished after restart** | Development ephemeral key, or (in Docker) the volume was removed (`down -v`). Set a persistent key and keep the volume. |
| **A site's pepper changed unexpectedly mid-month** | Check for a month boundary (UTC) or a `revoke`+re-provision. On the client side, an unexpected change is a deliberate tripwire signal. |
| **`docker compose up` errors that `PEPPERMILL_STORAGE_KEY` is unset** | Create `.env` from `.env.example` and fill in both secrets. |
