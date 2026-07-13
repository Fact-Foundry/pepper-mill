# PepperMill — Operations

How to configure, run, and operate PepperMill. See the design rationale in
[`design/decisions/`](design/decisions).

## Configuration

All settings live under the `PepperMill` configuration section (env vars use `PepperMill__Key`).

| Key | Meaning | Required |
|---|---|---|
| `EntitlementMode` | `Local` (resolve against registered site credentials) or `Platform` (external delegation, not implemented) | default `Local` |
| `StorageKeyBase64` | base64 of a **32-byte** AES-256 master key that encrypts peppers at rest | **yes, outside Development** |
| `CallbackAllowedHosts` | hostnames PepperMill may call back to during registration (SSRF guard); indexed env keys `__0`, `__1`, … | yes, to register |
| `StorageProvider` | storage backend: `File` (encrypted files, zero deps) or `Postgres` (shared/HA) | default `File` |
| `PostgresConnectionString` | Postgres connection string; the schema is created idempotently at startup | **yes, if `StorageProvider = Postgres`** |
| `StorePath` | directory holding the encrypted pepper files, credential records, and the audit log (File backend; the audit log lives here on any backend) | default `peppers` |

### Storage backends

`File` (default) is a zero-dependency encrypted file store — ideal for self-hosting. `Postgres` keys two
tables (`peppers`, `credentials`) by `(cluster_id, tenant_id, site_id)` and is the choice for shared/HA
storage (both nodes point at the same database — see [ADR-002](design/decisions/002-storage-backends-and-ha.md)).
**Peppers are AES-256-GCM encrypted app-side before storage on either backend**, so the store only ever
holds ciphertext; the master key (`StorageKeyBase64`) is never given to the database.

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

Every fetch / register / revoke / rotate appends a JSON line to `StorePath/audit.log`. Entries carry
metadata only (timestamp, event, tenant/site id, epoch) — **never** pepper or credential material.

## Deployment notes

- Run PepperMill on a private network in a **separate trust domain** from the servers it serves — that
  separation is the point of external custody.
- Persist `StorePath` on durable storage; back it up encrypted (it already is, at rest).
- HTTPS is strongly recommended (and essential if the service is ever exposed beyond its private
  network), but not forced — an internal segment may legitimately run plain HTTP.
- Health probe: `GET /health`.

## Deploy to a server (systemd, no Docker)

`deploy/publish.sh` builds a **self-contained single-file binary** (bundles the .NET runtime — the
server needs nothing installed) and generates a ready-to-ship bundle: the binary, a hardened
`peppermill.service` systemd unit, an env template, and a one-shot `install.sh`.

```bash
bash deploy/publish.sh                 # → deploy/peppermill-linux-x64.tar.gz
#   RID=linux-arm64 bash deploy/publish.sh   # for ARM servers
```

On the target server:

```bash
tar xzf peppermill-linux-x64.tar.gz
sudo ./install.sh                      # installs to /opt/peppermill, adds the service (disabled-until-configured)
sudoedit /etc/peppermill/peppermill.env   # set PepperMill__StorageKeyBase64 (head -c 32 /dev/urandom | base64)
systemctl start peppermill
curl http://127.0.0.1:5130/health      # → {"status":"ok"}
journalctl -u peppermill -f            # logs
```

The unit runs as a non-root `peppermill` user with systemd hardening (`ProtectSystem=strict`,
`NoNewPrivileges`, private tmp/devices, …). Config lives in `/etc/peppermill/peppermill.env`
(`chmod 600`, root-owned — systemd reads it before dropping privileges, so the master key never needs
to be readable by the service account). The pepper store lives in the systemd `StateDirectory`
(`/var/lib/peppermill`); back that up. Put your reverse proxy (TLS) in front, keeping the app bound to
`127.0.0.1` per the internal-only posture.

### Install from a release package (rpm / deb)

Pushing a version tag (`git tag v1.2.3 && git push origin v1.2.3`) runs the `release` GitHub Actions
workflow, which builds `.rpm` + `.deb` + `.pkg.tar.zst` packages (x64 and arm64) and the tarball and
**attaches them to the GitHub Release**. Downloading and installing is then a one-liner — no build, no
runtime:

```bash
# RHEL / Fedora / Rocky / Alma
sudo dnf install ./peppermill-1.2.3-1.x86_64.rpm
# Debian / Ubuntu
sudo apt install ./peppermill_1.2.3_amd64.deb
# Arch / Manjaro
sudo pacman -U ./peppermill-1.2.3-1-x86_64.pkg.tar.zst
```

The package installs the binary to `/opt/peppermill`, the hardened unit to
`/usr/lib/systemd/system/peppermill.service`, and a config template to `/etc/peppermill/peppermill.env`
(preserved across upgrades), creates the `peppermill` user, and enables the service. Then the same two
steps: set `PepperMill__StorageKeyBase64` in the env file, and `systemctl start peppermill`.

## Interactive API (Scalar)

`GET /scalar/v1` serves an interactive API reference (the modern replacement for Swagger UI); the raw
OpenAPI document is at `GET /openapi/v1.json`. Use it to exercise the endpoints during development.
