# Installing Stashboard (step by step)

This guide walks you through bringing up Stashboard as a single Docker container,
from an empty host to a running dashboard. No source code, .NET, or Node.js is
required — you run a prebuilt image from Docker Hub
([`vahac/stashboard`](https://hub.docker.com/r/vahac/stashboard)).

If you just want the short version, see the [README quick start](./README.md#quick-start-docker-compose).
For the maintainer/release side, see [PUBLISHING.md](./PUBLISHING.md).

---

## 1. Prerequisites

You need a host (Linux server, VM, Raspberry Pi, NAS, or your laptop) with:

- **Docker Engine 20.10+** and the **Docker Compose v2 plugin**.
  Check what you have:

  ```bash
  docker --version
  docker compose version
  ```

  If either command is missing, install Docker from
  <https://docs.docker.com/engine/install/> (the "Docker Engine" install includes
  the Compose plugin). On Windows/macOS, install **Docker Desktop**.

- Outbound internet access (to pull the image from Docker Hub).
- A free TCP port on the host. The default is **8080**.

> You do **not** need to install a database. Stashboard stores everything in a
> SQLite file inside a Docker volume.

---

## 2. Get the compose file

Stashboard needs just **one** file in a directory of your choice — the
`docker-compose.yml`. Create a folder and download it:

```bash
mkdir -p /opt/stashboard && cd /opt/stashboard

curl -O https://raw.githubusercontent.com/VahaC/stashboard/main/docker-compose.yml
```

(`/opt/stashboard` is just a suggestion — any directory works. On Windows, use a
folder like `C:\stashboard` and download the file with a browser or
`Invoke-WebRequest`.)

That's everything you need to start — skip straight to [§4](#4-start-the-container).
There is **no `.env` to create** for a standard install; the compose file has a
working default for every value and the app generates its own keys on first run.

---

## 3. (Optional) override defaults with a `.env`

You only need a `.env` file if you want to **change a default** — use a different
port, pin a version, configure SMTP, or supply your own secrets. If none of that
applies, skip this step entirely.

To add overrides, grab the template into the same folder and edit it:

```bash
curl -O https://raw.githubusercontent.com/VahaC/stashboard/main/.env.example
mv .env.example .env
```

```dotenv
# Encryption key + JWT secret — leave BLANK to auto-generate (recommended).
STASHBOARD_ENCRYPTION_KEY=
STASHBOARD_JWT_SECRET=

# Host port to expose the app on (default 8080).
STASHBOARD_PORT=8080

# Image version: `latest`, or pin one, e.g. 5.8.0.
STASHBOARD_TAG=latest
```

What each setting does:

| Setting | Need to set it? | Notes |
|---|---|---|
| `STASHBOARD_ENCRYPTION_KEY` | ❌ No | Leave blank. On first start the app generates a strong AES‑256 key and saves it on the data volume. See [§7](#7-how-secrets-are-handled). |
| `STASHBOARD_JWT_SECRET` | ❌ No | Same — auto-generated and persisted if blank. |
| `STASHBOARD_PORT` | only to change it | Change if `8080` is taken, e.g. `9090`. |
| `STASHBOARD_TAG` | only to pin | `latest` tracks the newest build. Pin (e.g. `5.8.0`) for a reproducible deploy. |

> **Feature toggles live in the app, not in `.env`.** Destructive / high-risk features are off by default and turned on inside Stashboard: the **Remove container** action is gated by `Stashboard:AllowContainerRemoval`, and the **host terminal** (V5.3 — an interactive SSH shell on the Docker host) is enabled at **Settings → Host terminal**, which spells out the conditions and risks. You don't set these in `.env`.

> **When would you set the keys yourself?** Only if you manage secrets in an
> external system, or you are **migrating an existing deployment** and must reuse
> its original encryption key (otherwise previously encrypted data can't be
> decrypted). An explicitly set value always wins and disables auto-generation
> for that secret.

---

## 4. Start the container

From the directory holding `docker-compose.yml`:

```bash
docker compose up -d
```

This pulls `vahac/stashboard:${STASHBOARD_TAG}` from Docker Hub and starts one
container named `stashboard-app`. The first run also:

- creates the SQLite database on the `stashboard-data` volume and applies all
  schema migrations,
- generates and persists the encryption key + JWT secret (if you left them
  blank).

Check it's healthy:

```bash
docker compose ps
docker compose logs -f app   # Ctrl+C to stop following
```

You're looking for a `running`/`healthy` status and, on first run, log lines like:

```
Generated a new encryption key and stored it under '/app/Data/.secrets'. ...
Generated a new JWT signing secret and persisted it.
```

---

## 5. Open the app and create your account

1. Browse to **`http://<host-ip>:8080`** (or `http://localhost:8080` if local).
2. Register the first account — this becomes your login.
3. Log in, then click **+ Add service** to start tracking your services.

If the page doesn't load, see [§9 Troubleshooting](#9-troubleshooting).

---

## 6. Updating to a newer version

Updating is just pulling a newer image and recreating the container. Your data —
**and your auto-generated keys** — live on Docker volumes and are preserved.

```bash
cd /opt/stashboard
docker compose pull         # fetch the image at STASHBOARD_TAG
docker compose up -d        # recreate the container
```

Or use the helper script (waits for health and prints logs on failure):

```bash
chmod +x deploy.sh   # first time only
./deploy.sh
```

To move to a specific version, set `STASHBOARD_TAG=5.8.0` in `.env` first, then
run the commands above. The new image applies any pending schema migrations on
startup — there is no separate migration step.

---

## 7. How secrets are handled

On first start, if `STASHBOARD_ENCRYPTION_KEY` / `STASHBOARD_JWT_SECRET` are
blank, Stashboard generates strong random values and writes them to
`/app/Data/.secrets/` (owner-only permissions), which lives on the
`stashboard-data` volume:

- **First deploy** → fresh keys generated and saved.
- **Every later start / update** → the same keys are read back, never overwritten,
  so encrypted credentials stay decryptable.

> ⚠️ **Back up the `stashboard-data` volume.** Losing the encryption key means
> losing every stored credential — there is no recovery.

---

## 8. Backups

Everything that matters is on two named volumes: `stashboard-data` (database +
secrets) and `stashboard-uploads` (logos). Back them up while the app is stopped
(or accept a hot copy):

```bash
# Find where Docker stores them
docker volume inspect stashboard_stashboard-data

# Simple tar backup of the data volume (run from anywhere)
docker run --rm \
  -v stashboard_stashboard-data:/data \
  -v "$PWD":/backup \
  busybox tar czf /backup/stashboard-data-backup.tgz -C /data .
```

> The volume names are prefixed with the Compose project name (the directory
> name), e.g. `stashboard_stashboard-data`. Run `docker volume ls` to see the
> exact names on your host.

---

## 9. Troubleshooting

**The app didn't become healthy / won't start**

```bash
docker compose logs --tail=100 app
```

Look for the first error. Common causes:

- **Port already in use** — change `STASHBOARD_PORT` in `.env` and re-run
  `docker compose up -d`.
- **Permission denied writing the database/secrets** — ensure the
  `stashboard-data` volume isn't bind-mounted to a host path the container user
  can't write. The default named volume "just works".

**I need to start completely fresh (destroys all data)**

```bash
docker compose down -v   # -v also deletes the volumes — irreversible
```

**Enable Docker container update tracking** (optional) — to let Stashboard watch
other containers on the host for image updates, see the dedicated
[DOCKER_UPDATE_MONITORING_GUIDE.md](./DOCKER_UPDATE_MONITORING_GUIDE.md).

---

## 10. Building from source (advanced)

The published image is recommended. If you want to build locally instead:

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
```

See [PUBLISHING.md §5](./PUBLISHING.md#5-building-from-source-development) for
details.
