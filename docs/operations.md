# PepperMill — Operations

How to configure, run, and operate PepperMill. See the architecture in
[`design/peppermill-spec.md`](design/peppermill-spec.md).

## Configuration

All settings live under the `PepperMill` configuration section (env vars use `PepperMill__Key`).

| Key | Meaning | Required |
|---|---|---|
| `EntitlementMode` | `Local` (shared credential) or `Platform` (delegate to fact-foundry-platform) | default `Local` |
| `StorageKeyBase64` | base64 of a **32-byte** AES-256 master key that encrypts peppers at rest | **yes, outside Development** |
| `LocalServerCredential` | the shared server credential accepted in `Local` mode | yes in Local mode |
| `StorePath` | directory holding encrypted pepper files + the audit log | default `peppers` |

### Generating the storage key

```bash
head -c 32 /dev/urandom | base64
```

Provide it out-of-band — an environment variable or secret store, **never** committed:

```bash
export PepperMill__StorageKeyBase64="<base64-32-bytes>"
export PepperMill__LocalServerCredential="<long-random-credential>"
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

- A server fetches `POST /v1/peppers/current` at startup with its bearer credential, holds the pepper
  **in memory only**, and re-fetches after `rotatesAtUtc` (and periodically as a tripwire — an
  unexpected pepper change is an incident signal).
- **Fail-open for identity only:** if PepperMill is unreachable, the server keeps ingesting with
  `IsFirstVisit = null` for the outage; it must never block analytics or retain raw IPs to backfill.

## Auditing

Every fetch / provision / revoke appends a JSON line to `StorePath/audit.log`. Entries carry
metadata only (timestamp, event, site id, epoch) — **never** pepper material. In the hosted edition
this is delegated to the platform's audit log with exportable access records.

## Deployment notes

- Run PepperMill in a **separate trust domain** from customer infrastructure — that separation is the
  entire value of hosted custody.
- Persist `StorePath` on durable storage; back it up encrypted (it already is, at rest).
- Terminate TLS in front of it; the credential is a bearer token.
- Health probe: `GET /health`.

## Interactive API (Scalar)

`GET /scalar/v1` serves an interactive API reference (the modern replacement for Swagger UI); the raw
OpenAPI document is at `GET /openapi/v1.json`. Use it to exercise the endpoints during development.
