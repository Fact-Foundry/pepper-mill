# PepperMill — Operations

How to configure, run, and operate PepperMill. See the design rationale in
[`design/decisions/`](design/decisions).

## Configuration

All settings live under the `PepperMill` configuration section (env vars use `PepperMill__Key`).

| Key | Meaning | Required |
|---|---|---|
| `EntitlementMode` | `Local` (resolve against enrolled tenant credentials) or `Platform` (external delegation, not implemented) | default `Local` |
| `StorageKeyBase64` | base64 of a **32-byte** AES-256 master key that encrypts peppers at rest | **yes, outside Development** |
| `CallbackAllowedHosts` | hostnames PepperMill may call back to during enrollment (SSRF guard); indexed env keys `__0`, `__1`, … | yes, to enroll |
| `StorePath` | directory holding encrypted pepper files, credential records + the audit log | default `peppers` |

### Generating the storage key

```bash
head -c 32 /dev/urandom | base64
```

Provide it out-of-band — an environment variable or secret store, **never** committed:

```bash
export PepperMill__StorageKeyBase64="<base64-32-bytes>"
export PepperMill__CallbackAllowedHosts__0="tf-server-1.internal"
```

If `StorageKeyBase64` is unset, PepperMill runs only in `Development` (with an **ephemeral** key —
peppers are lost on restart) and **refuses to start** in any other environment. This is deliberate:
a custody service must not silently lose the key that gives peppers continuity.

## The master key vs. the peppers

- The **peppers** are the per-site secrets PepperMill serves. They live encrypted in `StorePath`.
- The **master key** (`StorageKeyBase64`) encrypts those files at rest. It is *not* in the store.

A backup of `StorePath` alone cannot be decrypted without the master key. Losing the master key makes
existing peppers unrecoverable (which, for identity data, only means visitor-return detection resets —
no analytics or behavioral data is affected).

## Rotation & destruction

- Peppers rotate to a new value each **calendar month** (epoch `yyyy-MM`, UTC).
- Rotation is inherent in a fetch — asking for the current pepper generates a fresh one when the
  stored one has aged out, overwriting (destroying) the prior value.
- A background worker also sweeps hourly and rotates any stale pepper, so destruction happens on
  schedule even for sites that aren't fetching.
- "Returning visitor" on the TelemetryForge side therefore means *within the current month*; the
  metric visibly resets at each boundary (expected, not a bug).

## Client contract (TelemetryForge servers)

- A server fetches `POST /v1/peppers/current` at startup with its `key2` bearer credential, holds the
  pepper **in memory only**, and re-fetches after `rotatesAtUtc` (and periodically as a tripwire — an
  unexpected pepper change is an incident signal).
- **Fail-open for identity only:** if PepperMill is unreachable, the server keeps ingesting with
  `IsFirstVisit = null` for the outage; it must never block analytics or retain raw IPs to backfill.

## Auditing

Every fetch / enroll / revoke / rotate appends a JSON line to `StorePath/audit.log`. Entries carry
metadata only (timestamp, event, tenant/site id, epoch) — **never** pepper or credential material.

## Deployment notes

- Run PepperMill on a private network in a **separate trust domain** from the servers it serves — that
  separation is the point of external custody.
- Persist `StorePath` on durable storage; back it up encrypted (it already is, at rest).
- HTTPS is strongly recommended (and essential if the service is ever exposed beyond its private
  network), but not forced — an internal segment may legitimately run plain HTTP.
- Health probe: `GET /health`.

## Interactive API (Scalar)

`GET /scalar/v1` serves an interactive API reference (the modern replacement for Swagger UI); the raw
OpenAPI document is at `GET /openapi/v1.json`. Use it to exercise the endpoints during development.
