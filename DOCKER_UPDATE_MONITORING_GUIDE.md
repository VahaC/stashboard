# Docker Update Monitoring — User Guide

Step-by-step instructions for enabling per-service Docker container update
tracking in Stashboard, with all the host-access nuances, registry quirks,
and troubleshooting tips you'll actually hit in practice.

---

## TL;DR

1. Make sure the Stashboard container can reach Docker — either by mounting
   the local socket or by opening a TLS-protected TCP endpoint on the
   remote daemon.
2. Open a service in Stashboard → **Docker** tab → **Enable**.
3. Fill in the image reference, choose Docker host type, type the container
   name, save.
4. Click **Test connection** before relying on the result. Then **Check now**
   to populate the first digest comparison.
5. Wait for the badge **Update** to appear on the dashboard card (background
   scan runs every ~24 hours per watch by default — see §3.6) — or get the
   email notification if you left them on.
6. When the badge appears, either copy the templated upgrade command (§5)
   or — V2.7 — click **Update now** to let Stashboard pull + recreate the
   container in place (see §5.1; needs a writable Docker socket mount).

---

## 1. Decide where the container runs

You can monitor a container on:

- **The same host as Stashboard.** Easiest path. You'll mount the Docker
  socket into the Stashboard container.
- **A remote host.** Either expose the remote Docker daemon over
  `tcp://...:2376` with mutual TLS (§2.2), or — V2.5 — tunnel over
  SSH and skip the public Docker port entirely (§2.3).

---

## 2. Give Stashboard access to Docker

### 2.1 Local socket (recommended for self-hosting)

Add a read-only Docker socket mount to your `docker-compose.yml` under the
`stashboard` (or whatever you named the app) service:

```yaml
services:
  app:
    image: vahac/stashboard:${STASHBOARD_TAG:-latest}
    # ...
    volumes:
      - stashboard-uploads:/app/wwwroot/uploads
      - /var/run/docker.sock:/var/run/docker.sock:ro  # ← add this line
```

Then recreate the container:

```bash
docker compose up -d --force-recreate app
```

**Why read-only:** Stashboard only inspects containers and images — it never
needs to start, stop, or modify them. `:ro` blocks an entire class of
escapes if the app is ever compromised.

> **Note for V2.7 "Update now":** the one-click pull + recreate feature
> (see [§5.1](#51-update-now-v27)) calls `StopContainer` /
> `RemoveContainer` / `CreateContainer` / `StartContainer`, so it needs
> the socket to be **writable**. Drop the `:ro` suffix if you want that
> button to work. The rest of the surface (tracking, notifications,
> webhooks, manual upgrade commands) is fine on `:ro`.

**On Windows hosts** (Docker Desktop): the same mount works with WSL2 backend.
The path inside the container is still `/var/run/docker.sock`.

> **Security caveat:** Anyone with the Docker socket effectively has root on
> the host (they can launch a privileged container). Don't expose the
> Stashboard UI to the public internet without a strong reverse-proxy
> auth layer.

### 2.2 Remote TCP + TLS

If you want Stashboard to monitor a container on a **different** host, expose
the remote daemon over `tcp://...:2376` with mutual TLS. **Never expose the
unencrypted `:2375`** — anyone who finds it gets root on that host.

Quick setup on the remote machine (Linux, adapt for your distro).

> **Do steps 1–2 before touching the daemon config.** Docker won't start
> if the cert files are missing when it reads the override — you'll lose
> your Docker socket and have to undo the config to recover.

**Step 1 — Generate certificates** (run on the remote host as root):

```bash
mkdir -p /etc/docker/certs
cd /etc/docker/certs

# CA
openssl genrsa -out ca-key.pem 4096
openssl req -new -x509 -days 3650 -key ca-key.pem -out ca.pem \
  -subj "/CN=docker-ca"

# Server key + CSR
openssl genrsa -out server-key.pem 4096
openssl req -new -key server-key.pem -out server.csr \
  -subj "/CN=$(hostname)"

# Sign server cert — replace the IP with your actual public/LAN IP
echo "subjectAltName = IP:$(hostname -I | awk '{print $1}'),IP:127.0.0.1" \
  > extfile.cnf
openssl x509 -req -days 3650 -in server.csr -CA ca.pem -CAkey ca-key.pem \
  -CAcreateserial -out server-cert.pem -extfile extfile.cnf

# Client key + CSR
openssl genrsa -out client-key.pem 4096
openssl req -new -key client-key.pem -out client.csr -subj "/CN=client"
echo "extendedKeyUsage = clientAuth" > extfile-client.cnf
openssl x509 -req -days 3650 -in client.csr -CA ca.pem -CAkey ca-key.pem \
  -CAcreateserial -out client-cert.pem -extfile extfile-client.cnf

# Lock down permissions
chmod 0400 ca-key.pem server-key.pem client-key.pem
chmod 0444 ca.pem server-cert.pem client-cert.pem
```

**Step 2 — Configure the daemon to listen on 2376 with TLS:**

```bash
mkdir -p /etc/systemd/system/docker.service.d
tee /etc/systemd/system/docker.service.d/override.conf <<'EOF'
[Service]
ExecStart=
ExecStart=/usr/bin/dockerd \
  -H unix:///var/run/docker.sock \
  -H tcp://0.0.0.0:2376 \
  --tlsverify \
  --tlscacert=/etc/docker/certs/ca.pem \
  --tlscert=/etc/docker/certs/server-cert.pem \
  --tlskey=/etc/docker/certs/server-key.pem
EOF
systemctl daemon-reload
systemctl restart docker
```

**Step 3 — Open port 2376** in your firewall.

This step bites people most often. If the daemon is listening but the
firewall silently **drops** (not rejects) packets from the Stashboard
host, the TCP connection hangs for the full client timeout — and any
reverse-proxy sitting in front of Stashboard returns **504 Gateway
Timeout** on **Test connection** long before Stashboard itself gives
up. The Stashboard logs will show nothing useful; from the daemon
side, the daemon is healthy and `curl https://localhost:2376/_ping`
on the daemon host returns `OK`.

Pick the recipe for your host (replace `192.168.1.197` with the IP
of the Stashboard machine):

```bash
# UFW (Ubuntu, Debian, OpenMediaVault — UFW is the underlying backend
# even when OMV-Extras' "Firewall" plugin manages it through the UI)
sudo ufw allow from 192.168.1.197 to any port 2376 proto tcp \
  comment 'Stashboard Docker TLS'
sudo ufw reload

# firewalld (RHEL/Fedora/Rocky)
sudo firewall-cmd --permanent --add-rich-rule=\
'rule family="ipv4" source address="192.168.1.197/32" port port="2376" protocol="tcp" accept'
sudo firewall-cmd --reload

# Plain iptables
sudo iptables -I INPUT -p tcp -s 192.168.1.197 --dport 2376 -j ACCEPT
# persist with iptables-save / netfilter-persistent

# Cloud security group (AWS / GCP / Hetzner / etc.)
# Add an inbound rule: TCP 2376 from the Stashboard host's IP.
```

> **OpenMediaVault note.** OMV ships with UFW set to
> `default deny incoming` (you'll see `policy DROP` in
> `iptables -L INPUT`). Even though Docker listens on `*:2376`, every
> packet from the LAN gets dropped until you add an explicit allow
> rule. The OMV-Extras "Firewall" plugin in the web UI writes the
> same UFW rules — adding the rule there or via the `ufw` command above
> are equivalent.

**Quick smoke test from the Stashboard host before retrying in the UI:**

```bash
# TCP layer — must say "succeeded" within a second or two
nc -vz -w 3 192.168.1.200 2376

# TLS layer — must print "OK"
curl --cacert ca.pem --cert client-cert.pem --key client-key.pem \
     https://192.168.1.200:2376/_ping
```

If the `nc` hangs and ends with `Connection timed out`, the firewall
is still dropping the packet — the daemon never sees the SYN. If it
prints `Connection refused`, the daemon isn't actually bound to that
interface (re-check `ss -tlnp | grep 2376` on the daemon host).

**Step 4 — Copy client files** to the machine running Stashboard
(or save them to paste into the UI):

```bash
cat /etc/docker/certs/ca.pem
cat /etc/docker/certs/client-cert.pem
cat /etc/docker/certs/client-key.pem
```

You need these three PEM files on the **client** side:

- `ca.pem` — the CA that signed the daemon's server cert
- `client-cert.pem` — your client certificate
- `client-key.pem` — your client private key

Keep them safe — Stashboard encrypts them at rest (AES-256-GCM with the
key in `STASHBOARD_Encryption__Key`), but a leaked client cert lets the
holder talk to your Docker daemon.

> **Note on `sudo`:** some minimal Linux images (e.g. the official Docker
> image used for self-hosted Stashboard) don't ship `sudo`. If you're
> already `root`, just drop the `sudo` prefix — all the commands above
> work without it.

### 2.3 SSH tunnel (V2.5 — easiest for VPS hosts)

If you have an SSH login on the remote host but don't want to expose
`2376/tcp` to the internet, pick **SSH tunnel** as the Docker host type.
Stashboard opens an SSH connection per check, runs
`docker system dial-stdio` on the remote host, and bridges the resulting
stdio stream into the local `Docker.DotNet` HTTP client. No daemon TLS
material, no public port, no Caddy/Traefik wrapper.

**Step 1 — Generate a dedicated SSH key pair** on the machine running
Stashboard (or any machine you control — the private key only needs to be
pasted into the Stashboard UI):

```bash
ssh-keygen -t ed25519 -f ~/stashboard-vps -C "stashboard@vps"
# Press Enter twice for an unencrypted key, or set a passphrase you'll
# paste into Stashboard alongside the private key.
```

You now have:

- `~/stashboard-vps` — the **private** key (paste into Stashboard).
- `~/stashboard-vps.pub` — the **public** key (install on the remote).

**Step 2 — Install the public key on the remote host:**

```bash
ssh-copy-id -i ~/stashboard-vps.pub docker@vps.example.com
# or, manually:
cat ~/stashboard-vps.pub | ssh docker@vps.example.com 'cat >> ~/.ssh/authorized_keys'
```

The SSH user (`docker` in this example) needs permission to talk to the
Docker socket on the remote host. The usual way:

```bash
sudo usermod -aG docker docker
# Then re-login (or restart the SSH session) so the group takes effect.
```

For **rootless Docker**, the socket lives under
`/run/user/<uid>/docker.sock` instead of `/var/run/docker.sock`. Tell
Stashboard about it in the "Remote socket path" field.

**Step 3 — Configure the connection in the Stashboard UI:**

| Field | Example | Notes |
|---|---|---|
| Docker host | **SSH tunnel** | — |
| SSH host | `vps.example.com` | DNS name or IP |
| SSH port | `22` | default 22 |
| SSH username | `docker` | the user you ran `ssh-copy-id` for |
| SSH private key (PEM) | (paste contents of `~/stashboard-vps`) | full PEM including the `-----BEGIN ...` lines |
| Private key passphrase | (only if you set one) | stored encrypted at rest |
| Remote socket path | `/var/run/docker.sock` | change for rootless Docker |

Click **Test connection** — Stashboard opens the SSH session, runs
`docker version` over the tunnel, and reports back.

**Security model:**

- The private key is encrypted at rest with the same AES-256-GCM key
  (`STASHBOARD_Encryption__Key`) as the TLS material.
- No long-lived SSH session is held — a fresh tunnel is opened per check
  and torn down as soon as the call completes.
- The SSH user only needs `docker` socket access — Stashboard does **not**
  require root/sudo or shell login privileges beyond running
  `docker system dial-stdio`.
- A leaked private key gives the attacker the same Docker socket access
  the SSH user has, which is equivalent to root on the remote host —
  treat it accordingly.

**Troubleshooting SSH:**

| Error | Likely cause |
|---|---|
| `SSH connection failed: Permission denied (publickey)` | Public key isn't in `~/.ssh/authorized_keys` for the SSH user, or the key file's permissions on the remote are too loose (`chmod 600 ~/.ssh/authorized_keys`). |
| `SSH connection failed: Connection refused` | Wrong host/port, firewall blocking, or `sshd` not running. |
| `dial unix /var/run/docker.sock: connect: permission denied` | SSH user isn't in the `docker` group on the remote. Run `sudo usermod -aG docker <user>` and reconnect. |
| `docker: command not found` | Stashboard falls back to `socat` automatically; install it with `apt install socat` or set the **Remote socket path** to the correct location and ensure the user has read+write access. |

---

## 3. Configure the watch(es) in the UI

A single Stashboard service can track **multiple containers** — useful when
your service is really a stack. Some common shapes:

| Stashboard service | Containers you'd add |
|---|---|
| WordPress | `app` (wordpress image) + `db` (mariadb/mysql) |
| Nextcloud | `app` (nextcloud) + `db` (postgres) + `cache` (redis) |
| n8n | `app` (n8nio/n8n) + `db` (postgres) |
| Sonarr | just `app` — one container is fine |

Each tracked container shows up as its own card under the **Docker** tab,
identified by a short **Label** (`app`, `db`, `cache`, …). Each one has its
own image reference, container name, status, and notification settings.

1. **Open** any service from the dashboard (click the pencil icon).
2. Switch to the **Docker** tab.
3. Click **+ Add container** to add the first tracked container. For a
   composite service, click **+ Add container** again for each sibling.

### 3.1 Label

A short identifier that distinguishes this container from its siblings on
the **same service**. Common values:

- `app` — the main application container
- `db` — database
- `cache` — Redis/Memcached
- `worker` — background queue worker
- `proxy` — reverse proxy sidecar

**Required.** Must be unique per service (you can't have two `app` watches
on one service), but the same label can appear on different services
(every WordPress service has its own `app`/`db` pair). Max 100 chars.

### 3.2 Image reference

The image **as your container was pulled from**. Examples:

| What you typed in `docker run` / compose | Image reference to enter |
|---|---|
| `nginx` | `nginx` *(expands to `docker.io/library/nginx:latest`)* |
| `nginx:1.27-alpine` | `nginx:1.27-alpine` |
| `linuxserver/sonarr:latest` | `linuxserver/sonarr:latest` |
| `ghcr.io/home-assistant/home-assistant:stable` | `ghcr.io/home-assistant/home-assistant:stable` |
| Self-hosted: `registry.example.com/team/svc:v3` | `registry.example.com/team/svc:v3` |

> **Watch out:** Use the **same** registry/repo/tag string as your container
> actually runs. Stashboard compares the digest the registry reports for
> that tag against the digest your container's local image was pulled from
> — if you typed `nginx:latest` but the container actually runs
> `nginx:stable`, the check will report "no matching RepoDigest".

### 3.3 Docker host

- **Local socket** — leave the host URL empty.
- **Remote TCP+TLS** — fill in `tcp://host.example.com:2376`. Paste your
  CA / client cert / client key into the corresponding fields below; each
  is a PEM string (the full text including the
  `-----BEGIN ...-----` / `-----END ...-----` lines).

### 3.4 Container name

The name `docker ps` shows for the running container — for example
`stashboard-db-dev`, `homeassistant`, `sonarr_1`. Docker Compose typically
names containers `{project}_{service}_{n}` or `{project}-{service}-{n}`
(depending on Compose version) — copy the exact string.

You can also use the container **ID** if the name is awkward
(`docker ps --no-trunc --format '{{.ID}}'`).

### 3.5 Registry credentials (private images only)

If your image is in a **private** Docker Hub repo or a private GHCR
package, supply a username + a token (NOT your account password):

- **Docker Hub:** use a personal access token from your account →
  Security → Access Tokens.
- **GHCR:** create a fine-grained PAT with `read:packages` scope at
  <https://github.com/settings/tokens>.

Leave both fields empty for public images.

### 3.5a Registry type (V2.4)

The watch form now carries a **Registry type** dropdown right under the
registry-credential fields:

| Option | Use it for |
|---|---|
| **Auto** | Docker Hub, GHCR, and any generic OCI registry that follows the standard anonymous → Bearer-token flow. Default. |
| **HTTP Basic** | Nexus Repository, Gitea Packages, and other registries that either don't speak Bearer at all or simply prefer to see Basic on every request. Stashboard skips the token round-trip entirely. |
| **AWS ECR** | Private AWS Elastic Container Registry. Stashboard calls `GetAuthorizationToken` with your IAM keys, decodes the temporary `AWS:<token>` pair, and uses it as Basic for the next ~12 h before refreshing. |

When the image reference is a private ECR hostname
(`<account-id>.dkr.ecr.<region>.amazonaws.com/<repo>:<tag>`) the form
auto-detects it and offers to promote **Auto → AWS ECR**. The mapper
applies the same promotion at save time, so even an "Auto" watch against
an ECR image will end up with the right strategy on the server.

**For AWS ECR you also need to supply:**

- **AWS access key id** — IAM key with permission to call ECR
  `GetAuthorizationToken`. The managed policy
  `AmazonEC2ContainerRegistryReadOnly` is the minimum.
- **AWS secret access key** — paired secret.
- **AWS region** — auto-filled from the hostname (`eu-central-1` in the
  example above) but you can override.

Both AWS secrets are encrypted at rest with the same `IEncryptionService`
the TLS material uses; the API responses surface only a
`hasAwsCredentials: bool` flag.

### 3.5b GitHub PAT for release notes (V2.3, GHCR only)

When the image lives on `ghcr.io`, an extra **GitHub PAT for release
notes** field appears under the registry credentials. Stashboard uses it
to pull the matching GitHub release for the resolved tag and show its
changelog inline in the modal's **What's new** panel (and to include a
`Release notes: {url}` line in the email / Telegram notification).

- **Public repositories** work without a PAT, but unauthenticated GitHub
  calls share a 60 requests / hour quota per IP — fine for one or two
  watches, not enough for a fleet.
- Adding a token (scope: `public_repo`, or `repo` for private repos)
  raises the ceiling to **5 000 requests / hour** for your token.
- Private GHCR repos *require* a PAT — without it, the enrichment
  silently degrades and the panel just doesn't appear.

The token is stored encrypted at rest, never returned by the API, and
the field uses the same tri-state **Keep / Set / Clear** control as the
TLS material.

### 3.6 Check schedule

How often Stashboard checks for a new digest. Default: **every 24 hours**.

The schedule picker has three modes:

| Mode | When to use | What you pick |
|---|---|---|
| **Every N hours** | Rolling cadence. Good for any registry; the default for new watches. | One of `1 h / 2 h / 4 h / 6 h / 12 h / 24 h`. Roll starts from the last successful check. |
| **Daily at HH:MM** | Check once a day at a fixed time (your morning, for example). | A time-of-day in your local timezone. |
| **Weekly on Day at HH:MM** | Low-traffic images — databases, base OS layers — that rarely receive updates mid-week. | A day-of-week and a time-of-day in your local timezone. |

Times are entered in your local timezone and converted to UTC server-side.
A small helper line under the picker shows the projected next check, e.g.
`Next check ~Mon, May 18 08:00`. If the server is down when a Daily or
Weekly window arrives, the watch fires as soon as the loop comes back up —
no missed-window holes.

For Docker Hub rate-limit guidance on choosing the right interval, see
[§10 Rate limits](#10-rate-limits).

### 3.7 Notifications

Two independent channels, each with its own toggle on the watch:

#### Email
Leave **Email me when an update is available** on (default). You'll get
exactly **one** email per unique newer digest — even if the scan re-runs
50 times before you upgrade, no inbox spam.

**Prerequisites:** SMTP must be configured in Stashboard via
`STASHBOARD_Email__*` env vars (see the main README). If
`Email__Provider=LogOnly`, emails just go to the container logs.

#### Telegram
**Send a Telegram message when an update is available** — disabled by
default. The toggle is **greyed out** with a hint pointing at the Account
page if you haven't configured Telegram yet.

**Prerequisites — configure once on the Account page:**
1. Create a Telegram bot via [@BotFather](https://t.me/BotFather) →
   `/newbot` → copy the bot token.
2. Find your chat ID — easiest way is to message your bot once, then
   visit `https://api.telegram.org/bot<TOKEN>/getUpdates` and copy the
   `chat.id` field from the JSON.
3. In Stashboard go to **Account → Notifications**, paste the bot token
   and chat ID, tick **Enable Telegram notifications**, save.

After that the Telegram toggle on each Docker watch becomes available.
The two channels throttle **independently** — a transient Telegram Bot
API outage won't drop the email, and a flaky SMTP server won't suppress
the Telegram message. Each channel has its own per-digest throttle key,
so you still get exactly one notification per channel per unique digest.

> **Both channels off?** That's fine — the dashboard will still show the
> **Update** badge on the card, and the modal's Docker tab will still
> display the digest comparison. You just won't get a push from us.

> **GHCR images (V2.3):** if the upstream repository has a GitHub release
> matching the resolved tag, the notification body picks up a
> `Release notes: {url}` line and the modal renders a collapsible
> **What's new** panel beside the digest comparison. Best-effort — a
> missing GitHub release or a hiccup on the GitHub API never delays the
> notification.

### 3.8 Save + Test connection

Click **Test connection** first. You'll see three independent ticks:

```
✓ Docker host reachable
✓ Container found
✓ Registry reachable
```

If any one fails, you'll see an error message inline — fix it before
saving. A failed registry probe with the host probe passing usually means
the image reference is wrong or you need credentials.

Then **Save changes** (or **Enable tracking** for a new watch).

### 3.9 First check

Click **Check now** in the Docker tab to populate the digests immediately.
The status panel will show:

```
Current digest:   sha256:abc123def456…
Latest digest:    sha256:abc123def456…
```

If they match: ✓ Up to date. If they differ: ⚠ Update — and you'll see
the **Update** pill on the dashboard card right away.

---

## 4. What happens automatically after that

- The **background scan** wakes up every 5 minutes (`STASHBOARD_DockerUpdate__TickIntervalSeconds`).
- For each watch (**each container, not each service**), it checks whether
  the per-watch interval has elapsed since the last check. If yes → it
  runs the comparison and updates the status fields. Sibling watches on
  the same service tick independently.
- When a **newer** `LatestDigest` is observed for the first time:
  - The dashboard card's **Update** badge appears (or stays on if it
    already was). For composite services, the badge appears as soon as
    **any** sibling watch has an update — you'll see the per-container
    breakdown when you open the modal.
  - An email goes out (if notifications are enabled and SMTP works).
  - `LastNotifiedDigest` is stamped so the same digest never re-emails.
- When you upgrade your container (`docker compose pull && docker compose up -d`)
  and the digests realign, the next scan flips the watch back to
  **Up to date** and clears the badge (only once **all** siblings are
  Up to date for that service).

---

## 5. Doing the actual upgrade

Two paths once you see the **Update** badge:

- **Manual upgrade** (always available) — copy the templated command and
  run it yourself. Detailed below in this section.
- **One-click "Update now"** (V2.7) — let Stashboard pull + recreate the
  container in place. See [§5.1](#51-update-now-v27).

### Manual upgrade

The "Copy update command" panel exists for the cases where you'd rather
keep your hands on the wheel (production stacks behind change-management,
deploys that need a downstream test, etc.):

1. Click the service card to open the modal → Docker tab.
2. Click **Copy update command** to get a templated one-liner like:
   ```bash
   docker pull ghcr.io/owner/repo:v1 && docker stop my-service && docker rm my-service
   ```
3. Adjust if needed (e.g. for Docker Compose, swap to
   `docker compose pull && docker compose up -d <service>`).
4. Paste in your shell.
5. **Check now** in the modal to confirm the watch goes green again.

### 5.1 Update now (V2.7)

The **Update now** button next to **Check now** in the watch's status row
does the whole pull + recreate cycle without leaving the modal. Each click
pops a confirmation dialog that names the exact image reference and container,
warns that recreating a running container isn't risk-free, and links back to
this section — the recreate only runs once you confirm there. On
**Update now** (confirm), Stashboard:

1. Pulls the configured tag from the registry (using the watch's stored
   registry credentials / ECR token / anonymous as appropriate).
2. Inspects the running container to capture every config bit — env,
   mounts, ports, network mode, labels, restart policy.
3. Stops + force-removes the old container (`docker stop` + `docker rm -f`).
4. Creates a new container with the **same name** and every captured
   config bit, but pointed at the newly-pulled image. Compose-managed
   containers stay Compose-managed because their
   `com.docker.compose.*` labels ride along untouched.
5. Starts the new container.
6. Runs the orchestrator inline so the watch flips back to **Up to date**
   in the same round-trip — no need to click **Check now** afterwards.

The result is recorded in the **Update history** accordion under the
status panel: outcome badge, digest transition (`sha256:abc…` →
`sha256:def…`), timestamp, and any error string. The history is
append-only and capped at 50 rows per watch; rows go away only when the
parent watch / service is deleted.

#### Requirements

| Need | Why |
|---|---|
| **Writable** Docker socket mount (drop `:ro`) | Stashboard has to call `StopContainer` / `RemoveContainer` / `CreateContainer` / `StartContainer` — all writes. `:ro` is fine for the rest of the surface (tracking, notifications, webhooks). |
| Watch must be **enabled** | Disabled watches refuse to update — toggle Enabled first. |
| Image must come from the registry (i.e. have a RepoDigest) | Locally-built images have nothing to "pull". The same constraint that applies to digest tracking applies here. |

If your `docker-compose.yml` currently mounts the socket as `:ro`, drop
the suffix:

```yaml
services:
  app:
    volumes:
      # - /var/run/docker.sock:/var/run/docker.sock:ro   # before
      - /var/run/docker.sock:/var/run/docker.sock       # after (V2.7)
```

then recreate the container: `docker compose up -d --force-recreate app`.

#### Failure modes and the audit row

Every click writes one row to the history regardless of outcome. The
status badge tells you which leg of the sequence failed:

| Status | Meaning | What survives |
|---|---|---|
| **Success** | New container is running on the new image. Watch flipped to **Up to date**. | The pulled image, the new container. |
| **PullFailed** | `docker pull` failed (rate limit, auth, image not found, network). | The **old** container is untouched and still running — no downtime. |
| **RecreateFailed** | Pull succeeded but stop / remove / create / start hit an error halfway through. | The pulled image is local; the old container may already be gone. The error string points you at `docker ps -a` so you can recover manually. |
| **HostUnreachable** | Docker daemon wasn't reachable at the start of the attempt. | Nothing changed. |
| **ContainerNotFound** | The configured container doesn't exist on the host. | Nothing changed. Edit the watch's container name and try again. |

#### Safety properties

- The button **always** asks for confirmation. There is no
  "update-without-confirming" preference.
- **Per-watch only.** There is no "update all watches" bulk action by
  design — each container gets one explicit click.
- **No state lost on a pull failure.** The old container keeps running
  if `docker pull` doesn't land, so a rate-limit hit is harmless.
- **All credentials stay encrypted at rest.** The pull uses the same
  decrypted-in-memory profile the orchestrator already builds for the
  scheduled check — no new long-lived credential paths.
- **Owner-only.** Like every other watch endpoint, the API checks that
  the calling user owns the parent service before doing anything.

#### Limitations

- **Raw recreate by default.** Out of the box Stashboard does a
  Watchtower-style raw recreate (`stop` → `rm -f` → `create` → `start`)
  rather than shelling out to `docker compose up -d <service>`. The labels
  are preserved, so the container continues to look Compose-managed
  afterwards (`docker compose ps` still lists it), but a downstream
  `docker compose up -d` will treat the recreate as drift. **V5.2 adds an
  opt-in Compose-aware path that removes this limitation — see
  [§5.1a](#51a-compose-aware-recreate-v52).**
- **One primary network at create time.** Multi-network containers get
  their primary network attached at create and the rest re-attached
  after start. Plain `bridge`-only containers — the overwhelming
  majority — go through the fast path with no post-start work.
- **No rollback on a failed recreate.** If `create` or `start` fails
  after the old container is already gone, you'll need to recreate it
  manually from your `docker-compose.yml` / launch command. The
  `RecreateFailed` row records the digest of the image we just pulled
  so you know what to point the manual recreate at.

### 5.1a Compose-aware recreate (V5.2)

The raw recreate above is faithful for the common case but bypasses Docker
Compose entirely — so `env_file` values are frozen at the last `up` time,
`depends_on` ordering is ignored, profiles aren't considered, and Compose's
own subnet allocation isn't used. **V5.2** lets "Update now" delegate the
recreate to the real `docker compose` CLI instead, preserving the full
Compose lifecycle.

When it's used, **Update now** runs:

```bash
docker compose pull <service>
docker compose up -d <service>      # no --no-deps, so depends_on order is honoured
```

against your project directory, then runs the same post-start health
verification (V3.2) and writes the same audit row. The image bundles the
standalone `docker compose` v2 binary, so nothing extra needs installing.

#### The two recreate variants at a glance

"Update now" always does **one** of these two. Stashboard picks automatically
per attempt — there is no global switch:

| | **Raw recreate** (default, V2.7) | **Compose-aware** (opt-in, V5.2) |
|---|---|---|
| Mechanism | `stop` → `rm -f` → `create` → `start` via the Docker API | `docker compose pull <svc>` → `up -d <svc>` |
| `env_file` values | **frozen** — copied from the old container's inspect snapshot | **re-resolved** from the file at `up` time |
| `depends_on` ordering | ignored (only this one container is touched) | honoured — dependencies start in order |
| Compose `profiles` | ignored | honoured |
| Network / subnet | re-attaches the same named networks | Compose's own IP / subnet allocation |
| Registry auth for the pull | the **watch's** stored credentials / ECR token / anonymous | the **host's** `docker login` state |
| Private images | work with the credentials stored on the watch | the host must already be `docker login`'d |
| Remote hosts (TCP+TLS / SSH) | ✅ supported | ❌ not supported — always falls back to raw |
| Non-Compose (`docker run`) containers | ✅ supported | ❌ falls back to raw (no `com.docker.compose.service` label) |
| Drift vs. a later `docker compose up -d` | the manual recreate looks like drift | none — it *is* a `compose up` |
| If the pull fails | old container untouched, still running | old container untouched, still running |
| If create/`up` fails | old container may already be gone → manual recovery (`RecreateFailed`) | Compose manages the partial state; reported as `RecreateFailed` |

#### Which one to pick

- **Use the raw recreate (the default — just don't set a project path) when:**
  - the host is remote (TCP+TLS / SSH) — the Compose path can't run there;
  - the container was started with `docker run` (not Compose);
  - the image is private and **only Stashboard** holds the credentials (the
    host isn't `docker login`'d) — the raw path uses the watch's stored creds;
  - it's a single, self-contained container with no `depends_on` and an
    `env_file` that doesn't change between updates — the raw path is simpler
    and needs no extra mounts.
- **Prefer the Compose-aware path when:**
  - the service has `depends_on` relationships that must come up in order;
  - the `env_file` / `.env` is edited on the host between updates and you want
    the new values picked up;
  - the stack uses `profiles`, or you want to avoid the "drift" a raw recreate
    leaves behind so your own `docker compose up -d` stays a clean no-op.
- **Avoid the Compose-aware path when:**
  - you do **not** want `up -d <service>` to also (re)start stopped
    dependency services as a side effect — `up -d` brings up anything the
    service `depends_on` that isn't already running;
  - the host can't reach the registry with its own `docker login` for a
    private image — fall back to the raw path, which uses the watch's creds.

#### When the Compose path kicks in

All four must be true; otherwise Stashboard transparently falls back to the
raw recreate:

1. The connection is a **Local socket** (the CLI runs against the local
   daemon; remote TCP+TLS / SSH connections stay on the raw recreate).
2. The connection has a **Compose project path** set (see below).
3. The tracked container is **Compose-managed** — it carries the
   `com.docker.compose.service` label (i.e. it was originally started by
   `docker compose`).
4. The `docker compose` CLI is present in the container (it is, in the
   official image).

#### Setting it up

1. **Bind-mount the host's Compose project directory** (the folder
   containing your `docker-compose.yml`, plus any `.env` / `env_file` paths
   it references) read-only into the Stashboard container:

   ```yaml
   services:
     app:
       volumes:
         - /var/run/docker.sock:/var/run/docker.sock   # writable — Update now needs it
         - /srv/my-stack:/compose-projects/home-server:ro
   ```

2. **Set the connection's "Compose project path"** (the field appears on the
   connection form for Local socket hosts) to the **in-container** path —
   `/compose-projects/home-server` in the example above, *not* the host path.

3. Recreate Stashboard (`docker compose up -d --force-recreate app`) and click
   **Update now** on a Compose-managed watch. The Update history row is
   written exactly as for the raw path.

#### What each feature level needs in `docker-compose.yml`

The Docker tab works in stages — mount only as much as the feature you want
requires:

| You want… | Socket mount | Extra mounts |
|---|---|---|
| Digest tracking + email/Telegram notifications (V1–V2.6) | `:ro` is enough | — |
| "Update now" — **raw** recreate (V2.7) | **writable** (drop `:ro`) | — |
| "Update now" — **Compose-aware** recreate (V5.2) | **writable** (drop `:ro`) | bind-mount the project dir `:ro` **+** set the connection's *Compose project path* to the in-container path |

So for the Compose-aware path the only additions over the V2.7 setup are:
**(a)** one read-only volume line pointing at the host's Compose project
directory, and **(b)** the *Compose project path* field in the connection
form. No environment variables, no image change (the `docker compose` binary
is already baked in). `docker-compose.yml` in this repo ships both lines as
commented-out examples — uncomment and adjust the host path.

> **Heads-up on the socket:** the Compose-aware path still needs the **writable**
> socket — `docker compose up -d` issues the same create/start writes the raw
> path does. A `:ro` socket only covers tracking + notifications.

#### Notes & limitations

- **Pull auth.** The compose CLI pulls using the host's Docker login state,
  **not** the watch's stored registry credentials. For private images, make
  sure the host is already `docker login`'d to the registry.
- **Local socket only.** Remote hosts remain on the raw recreate until a
  separate piece of work tackles remote Compose shelling.
- **Misconfiguration is surfaced, not silently downgraded.** If the project
  path is set but the directory isn't reachable inside the container (missing
  bind mount), the attempt is recorded as **RecreateFailed** with a hint —
  it does **not** fall back to a destructive raw recreate. A missing CLI (only
  possible on a custom image) *does* fall back to the raw recreate.

### 5.2 Diagnostics inside the watch modal (V3.1 – V3.4)

The watch modal carries four collapsible panels for diagnosing a
misbehaving container without SSH-ing into the host:

- **Inspect container** (V3.1) — slimmed `docker inspect` snapshot:
  image digest, command, env, mounts, networks, labels, restart
  policy, health state, and ports. Env values whose key matches the
  secret heuristic (`PASSWORD`, `TOKEN`, `SECRET`, `API_KEY`, …)
  arrive as `value: null, masked: true` so credentials baked into the
  container never reach the browser.
- **Async health verification** (V3.2) — after V2.7's recreate
  finishes, Stashboard polls `docker inspect` for up to 30 s until
  the new container reports `healthy` (or has no `HEALTHCHECK`). The
  audit row's `HealthVerified` / `HealthVerifiedUtc` columns
  distinguish "started and is healthy" from "started but never became
  healthy" — the second is downgraded to `RecreateFailed`.
- **Container logs** (V3.3) — live tail with stdout / stderr / timestamp
  toggles, Pause / Resume / Stop, and a Download button that re-fetches
  the snapshot without `follow` and saves it as
  `<container>-<timestamp>.log`. Streams over chunked NDJSON so the
  existing JWT bearer auth works without any query-token tricks.
  Read-only — Stashboard never writes to stdin.
- **Live stats** (V3.4) — per-second CPU% / memory / network / block-I/O
  computed from the daemon's `/containers/{id}/stats` endpoint. Inline
  sparklines for CPU and memory, plus current-value tiles with
  per-second deltas for network and block I/O. Bounded to ~2 min of
  history per panel so a long-running tab can't accumulate memory.

All four panels reuse the watch's existing Docker connection — no extra
configuration on top of what V2.5 already requires for the schedule scan.

### 5.3 Docker instances page (V3.5)

Click **Docker** in the sidebar to open `/docker` — a top-down view of
every container across every Docker connection you own. The page lists
each connection as its own section; inside, a grid of cards shows one
container each:

- **Name / state / status** — green badge for running, red for
  exited / dead, amber for restarting / created. Exited and dead
  containers render with a **disabled card style** (V5.0) — dashed
  border and reduced opacity — so they stand out from running
  containers at a glance. Hovering the card restores readability.
- **Image, created, exposed ports, compose project / service** — pulled
  from the standard `com.docker.compose.*` labels. Click a project
  badge to filter the page to just that compose project.
- **Open in service** — when one of your watches tracks this container,
  the card links back to the watch modal so you can use the V3.1–V3.4
  diagnostics. Cards for containers that aren't tracked still get the
  lifecycle buttons but skip the link.
- **Start / Stop / Restart** — write one row to the same
  `DockerUpdateAttempts` audit table the V2.7 "Update now" flow uses,
  tagged with an `ActionType` discriminator (`Start` / `Stop` /
  `Restart` / `Remove`). If the container is tracked by one of your
  watches the audit row also links to the watch + service, so the
  per-watch **Update history** panel surfaces these actions too.
- **Remove** — destructive. The button only renders when the operator
  has opted in by setting `Stashboard:AllowContainerRemoval=true` (or
  the equivalent `STASHBOARD_Stashboard__AllowContainerRemoval=true`
  environment variable). With the flag off, the matching
  `DELETE /api/docker/connections/{id}/instance/containers/{name}`
  endpoint returns `403` regardless of caller — a stale frontend
  can't bypass it. With the flag on, the UI still asks for a
  second confirmation naming the container before firing the request.
  **For exited / dead containers** (V5.0), the Remove button is
  promoted from the overflow menu to an inline action in the card's
  action row so cleanup is one click away.

Operationally the page reuses the same connection / socket your
schedule-driven scan already uses, so no extra mount is needed.

#### Enabling Remove

Add the flag to your `docker-compose.yml`:

```yaml
services:
  app:
    environment:
      STASHBOARD_Stashboard__AllowContainerRemoval: "true"
```

or to `appsettings.json`:

```json
{
  "Stashboard": {
    "AllowContainerRemoval": true
  }
}
```

then recreate the container: `docker compose up -d --force-recreate app`.
The button shows up automatically — no rebuild required.

### 5.4 Host terminal (V5.3)

The container modal has a **Terminal** tab that opens an interactive shell **on
the Docker host itself** — handy when you need to poke at the box (`df -h`,
`journalctl`, edit a compose file) without leaving Stashboard and `ssh`-ing in
manually. It complements the diagnostics tabs: Logs/Stats/Inspect look *inside*
a container; the terminal drops you onto the *host* running it.

This is the most dangerous feature in Stashboard — it is full, interactive,
host-level shell access. It is therefore **off by default and SSH-only**, and
must be enabled in **two** places before the live terminal appears:

1. **Globally**, by the operator — go to **Settings → Host terminal** in the UI
   and turn on **Enable the host terminal server-wide**. That page also lays out
   every condition and the risks. (No env var / restart needed — the switch is
   stored in the database, like the SMTP settings. The optional
   `Stashboard:AllowHostShell` config flag only *seeds* the toggle on first run.)

2. **Per connection** — edit the SSH connection and tick **Allow host terminal**.
   The option only appears for **SSH tunnel** connections; `LocalSocket` and
   `TCP+TLS` connections show *"Available only for SSH tunnel connections"* on
   the Terminal tab because they have no host shell to offer.

Once both are on, open any container on that host, switch to **Terminal**, and
click **Connect**. The shell uses the same SSH key you configured for the
connection in [§2.3](#23-ssh-tunnel-v25--easiest-for-vps-hosts).

**Guardrails (all enforced server-side):**

- **Audited.** Every session is recorded (who, when, which host, duration, bytes
  in/out, why it ended) and streamed to the application log.
- **Concurrency caps + idle timeout** — tune via
  `STASHBOARD_Stashboard__HostShell__MaxSessionsPerUser` (default 3),
  `__MaxSessionsPerHost` (5), `__IdleTimeoutSeconds` (600; `0` disables) and
  `__TicketTtlSeconds` (30). Idle sessions are closed server-side regardless of
  whether the browser tab is still open.
- **No header-less token leakage** — the WebSocket is authorised by a single-use
  ticket minted by an authenticated request, not a JWT on the query string.

> **Live resize caveat:** the terminal is created at your browser window's size
> on connect. SSH.NET 2024.2.0 can't change the PTY window afterwards, so
> resizing the browser later won't reflow the remote shell — reconnect to pick
> up a new size. Everything else works normally.

**Behind a reverse proxy?** The terminal uses a WebSocket
(`/api/docker/connections/{id}/host-shell/ws`). If the tab stays stuck on
*"connecting…"*, your proxy isn't forwarding the WebSocket upgrade. Pass the
`Upgrade` / `Connection` headers for `/api` — e.g. nginx:

```nginx
location /api/ {
    proxy_pass http://stashboard:8080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
}
```

Traefik forwards WebSockets automatically; Cloudflare proxied (orange-cloud)
hosts do too. The single-container deployment (no external proxy) needs no extra
config.

---

## 6. Status reference

| Status | Meaning |
|---|---|
| **Up to date** | Local container's image digest = registry's digest. Nothing to do. |
| **Update available** | Registry has a newer digest for the configured tag. Time to pull. |
| **Error** | Check couldn't complete. See the last-error message for the cause. |
| **Disabled** | The watch exists but `Enabled` is off. Background scan skips it. |
| **Unknown** | Never checked yet (e.g. just created — wait for first scan or click Check now). |

---

## 7. Troubleshooting

### "Container not found"

- The container name on the configured host doesn't match. Run
  `docker ps --format 'table {{.Names}}\t{{.Image}}'` on the host and
  copy the exact name.
- For Compose-managed containers: the name is `{project}-{service}-{n}`
  (Compose v2) or `{project}_{service}_{n}` (Compose v1). The project
  name defaults to the parent directory.

### "Image not found on the host"

- The container is running, but its image was deleted (e.g. you ran
  `docker image prune` aggressively). Pull the image again before
  Stashboard can compare digests.

### "No RepoDigest matches X/Y"

- Your container was built locally and never pulled from a registry — it
  has no manifest digest to compare. This feature is only useful for
  containers that were `docker pull`ed.
- Or you typed the image reference differently from how the container
  was actually pulled. Check `docker image inspect <image>` →
  `RepoDigests` to see the exact form the daemon recorded.

### "Docker host unreachable"

- **Local socket:** the mount in `docker-compose.yml` is missing or
  the socket path differs (rare). Check
  `docker exec stashboard ls -la /var/run/docker.sock` — it should
  exist and be readable.
- **Remote TCP+TLS:** verify the daemon is listening on 2376 from the
  Stashboard host:
  `curl --cacert ca.pem --cert client-cert.pem --key client-key.pem https://host:2376/_ping`
  should return `OK`.
- Check the CA cert you pasted into the UI actually signed the daemon's
  server cert. A common mistake is pasting the **client** CA where the
  **server's** CA should go.

### Test connection returns 504 Gateway Timeout

The 504 never comes from Stashboard itself — the test endpoint always
returns 200 with a structured `{ dockerHostReachable, containerFound,
registryReachable, error }` body. A 504 means your reverse-proxy
(Nginx Proxy Manager, Caddy, Traefik, Cloudflare, …) gave up waiting
for the upstream because something in the test chain hung past the
proxy's read timeout (commonly 60 s).

The hang is almost always a **firewall silently dropping packets**
between Stashboard and the Docker host — SYNs go out, no SYN-ACK
comes back, the TCP stack waits for its full timeout. Same symptom
applies to all three host types:

- **TCP+TLS** — the daemon host's firewall (UFW, firewalld, cloud SG,
  OPNsense / pfSense / OMV ruleset) doesn't allow Stashboard's IP on
  port 2376. See [§2.2 step 3](#22-remote-tcp--tls) for the exact
  allow rules per firewall.
- **SSH tunnel** — same idea, but the blocked port is 22 (or whatever
  custom SSH port you set). `nc -vz -w 3 <host> 22` must succeed.
- **Local socket** — not a firewall issue; usually the socket mount
  is missing (see entry above).

**One-minute diagnosis from the Stashboard host:**

```bash
# Replace 192.168.1.200/2376 with your daemon host + port (or 22 for SSH)
nc -vz -w 3 192.168.1.200 2376
```

- `succeeded!` → TCP is fine, problem is higher up (TLS material, SSH
  key, container name). Re-run the **Test connection** and read the
  inline error message — it'll tell you which of the three checks
  failed.
- `Connection timed out` (exit code 1, after ~3 s with `-w 3`) → packets
  are being **dropped** by a firewall. Add the allow rule on the daemon
  host (see [§2.2 step 3](#22-remote-tcp--tls)) and retry.
- `Connection refused` → daemon isn't listening on that port from
  Stashboard's perspective. Re-check `ss -tlnp` on the daemon host
  and confirm it binds to `*:2376` (not `127.0.0.1:2376`).

**Want a more useful error instead of 504 while you debug?** Bump the
reverse-proxy upstream timeout to 120 s temporarily:

- **Nginx Proxy Manager:** edit the proxy host → *Advanced* tab →
  `proxy_read_timeout 120s; proxy_send_timeout 120s;`
- **Caddy:** `reverse_proxy ... { transport http { read_timeout 120s } }`
- **Traefik:** set `forwardingTimeouts.responseHeaderTimeout=120s` on
  the service.

That lets Stashboard's own 100 s default `HttpClient` timeout fire
first, so you get the actual `Docker host unreachable: ...` error in
the UI instead of an opaque 504. Revert the bumped timeout once the
firewall rule is in place.

### "Registry rate-limited"

- Anonymous Docker Hub: 100 pulls / 6h. Either:
  - Supply credentials in the watch (raises to 200/6h per account).
  - Switch to a longer schedule (Hourly → 12 h / 24 h, or **Daily**) on the
    watch's Docker tab.
  - Stagger Daily / Weekly schedules so a fleet of watches doesn't all
    fire at the same UTC minute.

### "Registry rejected the credentials"

- Wrong username / wrong token / token without `read:packages` scope
  (GHCR). Regenerate the PAT and update the watch (set the password
  field's action to **Set** and paste the new token).

### "Registry returned 401 even after authentication"

- The image is private AND your account doesn't have access. Verify
  by `docker login <registry>` + `docker pull <ref>` from your laptop
  with the same credentials.

### Dashboard badge doesn't appear after an update lands

- The dashboard polls every 30 s, but the scan only runs every
  ~5 minutes. **Check now** in the modal short-circuits the wait.
- If you just saved a new watch and `Check now` shows
  **Update available** but the dashboard pill doesn't appear: the
  dashboard's `useServices` cache may be stale. Refresh the page;
  background polling will re-sync within 30 s.

### Email never arrives

- Check `STASHBOARD_Email__Provider` — if it's `LogOnly`, emails are
  printed to container logs only.
- For Gmail: `Email__Username` and `Email__FromAddress` must be the
  same address, and the password must be a **Gmail App Password**
  (Google Account → Security → 2-Step Verification → App passwords).
  Regular passwords don't work over SMTP.

### Telegram message never arrives

- The Telegram toggle on the Docker watch is greyed out → you haven't
  finished setup on **Account → Notifications**. Bot token + chat ID +
  the master "Enable Telegram notifications" checkbox all need to be
  set.
- Toggle is on but no message: open the bot in Telegram and send it any
  message at least once. Bots can't initiate conversations — the chat
  has to exist before they can post into it.
- Wrong chat ID: query `https://api.telegram.org/bot<TOKEN>/getUpdates`
  and verify `result[*].message.chat.id` matches what you pasted. For
  group chats the ID is negative (e.g. `-1001234567890`).
- A previous send succeeded but a re-send for the same digest doesn't —
  that's the per-channel throttle working as designed. The Telegram
  message has its own `LastTelegramNotifiedDigest` key, so a single
  unique `LatestDigest` produces exactly one Telegram push.
- Bot is in a channel/group and only has post-permissions? Make sure
  it's added as an admin with "Post Messages" right, otherwise the Bot
  API returns 403.

---

## 8. Editing / removing a watch

- **Edit** anything in the Docker tab → **Save changes**. Each secret
  field has three actions in its dropdown:
  - **Keep** (default for already-saved values) — leaves the stored
    encrypted value untouched.
  - **Set** — pastes a new value (encrypted before save).
  - **Clear** — drops the stored value (encrypted column → NULL).
- **Pause** tracking without losing config: uncheck **Enabled**. Status
  flips to **Disabled** on next scan; resume by re-checking.
- **Remove entirely:** the trash icon next to "Last checked" in the
  Docker tab. Asks for confirmation; deletion is immediate.

---

## 9. What's deliberately out of scope (V1)

- ~~**SSH-based Docker hosts** (`ssh://user@host`).**~~
  ✅ **Shipped (V2.5).** Use TCP+TLS instead,
  or deploy Stashboard on the same host.
- ~~**Automatic upgrades** (one-click pull + restart). Notifications only.~~
  ✅ **Shipped (V2.7) as one-click "Update now".** Per-watch button in the
  modal, per-click confirmation, audit row written for every attempt,
  and the watch flips back to "Up to date" in the same round-trip.
  Requires the Docker socket mount to be writable. See [§5.1](#51-update-now-v27).
- ~~**Quay / Harbor / Nexus / AWS ECR.**~~
  ✅ **Shipped (V2.4).** The watch form has a **Registry type** dropdown
  with three modes: Auto (Docker Hub + GHCR + generic OCI), HTTP Basic
  (Nexus / Gitea Packages / Harbor with basic auth), and AWS ECR
  (Stashboard resolves IAM credentials → temporary Basic token, cached
  for ~12 h). Private ECR hostnames auto-promote on save. See §3.5a.
- ~~**Tag-pattern filtering** (e.g. "ignore -rc / -beta tags").~~
  ✅ **Shipped (V2.1).** A watch can set a `.NET regex` (e.g.
  `^v\d+\.\d+\.\d+$`); when set, Stashboard lists the registry's tags,
  filters by the pattern, picks the highest semver match, and compares
  *that* tag's digest. So a watch pinned to `latest` with the SemVer
  preset no longer flips to "Update available" the moment upstream
  publishes a `-rc1` build. Configurable via the **Tag pattern filter**
  field on each container, with presets for SemVer / stable-only.
- ~~**GitHub Releases enrichment** (inline changelog from GitHub).~~
  ✅ **Shipped (V2.3).** GHCR watches pull the matching release into a
  collapsible "What's new" panel and append a `Release notes:` link to
  email + Telegram messages.
- ~~**Webhook-based push notifications** (instant detection instead of polling).~~
  ✅ **Shipped (V2.6).** Each watch can opt in to a public webhook URL
  (`/api/docker/webhooks/{token}`) that registries POST to on every push;
  Stashboard re-checks within ~5 s instead of waiting for the schedule.
  See §11 below.
- **Kubernetes / Podman / containerd.** Docker daemon only.

Full V2 roadmap: `ROADMAP.md §11`.

---

## 10. Rate limits

> **tl;dr for Docker Hub:** keep the default 6-hour interval, or add
> credentials. Everything else (GHCR, self-hosted) is fine at any interval.

### What counts as a "pull" for rate-limiting?

Stashboard does **not** download the full image (which can be gigabytes).
It makes a single lightweight HTTP request — a `HEAD` call to the
registry's manifest endpoint (~1 KB of headers) — to obtain the current
digest for the configured tag. Docker Hub counts these manifest requests
against the same quota as full image pulls.

### Docker Hub quotas

| Account type | Quota | Window | Resets |
|---|---|---|---|
| **Anonymous** (no credentials) | 100 requests | per 6 hours | per originating IP |
| **Authenticated** (any Docker Hub account) | 200 requests | per 6 hours | per account |
| **Docker Hub Pro / Team / Business** | Unlimited (effectively) | — | — |

The quota is per **IP** for anonymous requests. If Stashboard and other
tools on the same server share one IP, their anonymous calls are pooled.

### How to stay within the limit

**Scenario A — small setup (≤ 24 Docker Hub watches, anonymous):**
The default 24-hour Hourly schedule means each watch uses exactly 1 pull
per *day*. 24 watches × 1 pull = 24 pulls / 24 h, well under 100 pulls / 6 h.
No action needed.

**Scenario B — larger setup (25+ Docker Hub watches, anonymous):**
You may approach 100 pulls / 6 h. Choose one or more:
- Add Docker Hub credentials to the watch (raises limit to 200 / 6 h; each
  authenticated account has its own quota).
- Switch some watches to a **Daily** or **Weekly** schedule (one pull per
  day / per week respectively).
- Stagger Daily / Weekly times so checks don't all fire at the same
  UTC minute.

**Scenario C — private Docker Hub images:**
You already need credentials for registry access, so you're automatically
on the authenticated quota (200/6h). No extra action needed.

**GHCR:** GitHub does not publish a specific manifest-pull rate limit for
authenticated users. In practice, limits are extremely generous (tens of
thousands of requests per hour). You can safely use an Hourly = 1 h
schedule for GHCR watches.

**Self-hosted registries (V2.4):** No external rate limits. Use any
schedule you like.

**AWS ECR (V2.4):** ECR doesn't publish a documented rate limit for the
manifest endpoint, but `GetAuthorizationToken` is rate-limited to
roughly 100 requests / sec per account. Stashboard caches the temporary
ECR token until 30 minutes before its 12-hour expiry, so a single watch
calls `GetAuthorizationToken` no more than twice a day per region — well
under the limit even with many parallel watches sharing the same IAM
key.

### Interpreting rate-limit errors

When Stashboard hits the limit, the watch status becomes `Error` and the
last-error field reads `"Registry rate-limited (HTTP 429)"`. The next
background scan tick (~5 minutes) will retry; if the 6-hour window has
reset by then, the check will succeed automatically. No manual action is
needed unless errors persist beyond 6 hours.

---

## 11. Webhook receiver (V2.6 — instant updates)

For images you publish yourself (or any image whose registry can fire a
webhook), you can skip polling entirely and have Stashboard re-check the
watch within seconds of the push. The watch's status flips to
**Update available** the moment your registry POSTs to its dedicated URL.

This is an **opt-in per-watch** feature — turn it on for the handful of
images that change often and where a few minutes of latency matters; leave
it off for the rest and let the schedule do the work.

### 11.1 Enable the webhook on a watch

1. Open the service modal → **Docker** tab → expand the watch.
2. Below the digest panel, expand the **Webhook receiver** section.
3. Click **Generate webhook URL**. Stashboard creates a 256-bit secret
   token and shows you the full URL:
   ```
   https://stashboard.example.com/api/docker/webhooks/abcdef0123…
   ```
4. Click the eye icon to reveal the token, then the clipboard icon to
   copy the URL.

The URL contains the only authentication. Anyone who has it can trigger
a re-check (no other action — they can't read your data, alter the
watch, or upload anything). If a URL leaks, click **Rotate token** to
invalidate it immediately.

### 11.2 Wire it into your registry

| Registry | Where to paste |
|---|---|
| **Docker Hub** | Repository page → **Webhooks** → **New webhook**. Fires on every tag push automatically. |
| **GitHub Container Registry** | GHCR has no built-in webhooks. Add a `workflow_dispatch` (or `release: published`) step to the GitHub Action that publishes the image, with a `curl -X POST <stashboard-url>` after the `docker push`. |
| **Harbor** | Project → **Webhooks** → policy type `Push`. |
| **Nexus Repository** | Capabilities → **Webhook: Repository** → events: `ASSET / CREATED`. |
| **Gitea Packages / Container registry** | Repository → Settings → Webhooks → custom URL on the `package` event. |
| **Self-hosted OCI distribution** | Stashboard accepts the standard distribution `events[]` shape — point your registry's `notifications` config at the URL. |

You don't need to send any particular payload. Stashboard recognises
Docker Hub, GHCR, and generic OCI shapes for diagnostics, but any POST
(even an empty body) will trigger the re-check.

### 11.3 What happens after the webhook lands

1. Stashboard returns **`202 Accepted`** to your registry within
   milliseconds — no DB writes block on the orchestrator, so retry
   storms are impossible.
2. The watch is queued for an immediate check. The background loop
   drains the queue every ~5 s, so the digest comparison runs within
   that window.
3. If the comparison flips the watch to **Update available**, the
   dashboard badge, email, and Telegram notification all fire on the
   same code path as a scheduled check — the only difference is the
   latency.
4. The watch's **Webhook receiver** panel shows the timestamp of the
   most recent accepted delivery so you can verify your registry is
   actually firing.

### 11.4 Safety net

Webhooks are a **latency optimisation, not a replacement for polling**.
Your scheduled check (24 h by default) continues to run regardless. If a
webhook is dropped — network blip, queue at capacity, registry
misconfigured — the next scheduled tick will pick up the update and
notify you on the usual cadence. You'll never silently miss an update.

### 11.5 Rotating or disabling

- **Rotate token** generates a fresh URL and invalidates the old one
  immediately. Use it whenever you suspect the URL has leaked, or as a
  rolling-credentials hygiene practice.
- **Disable webhook** removes the token entirely. The public endpoint
  starts returning `404` for the old URL on the next request. The watch
  reverts to schedule-only behaviour.

### 11.6 Security model

| Concern | Mitigation |
|---|---|
| Endpoint is public (no JWT) | The 32-byte CSPRNG token in the URL is the auth. 2^256 keyspace — brute force is infeasible. |
| Token leaks via referrer / log | Rotate the token. The old URL becomes 404 on the next request. |
| Inbound flood / DoS | Bounded process-local queue (capacity 1024) with duplicate collapsing — repeat hits for the same watch coalesce into one check. Beyond capacity the endpoint still returns 202 and the scheduled scan picks up the work. |
| Payload-based attack | Body is capped at 64 KB and parsed read-only; malformed JSON is silently tolerated. The orchestrator never sees the payload. |
| Public exposure required | The webhook URL must be reachable from the registry. If Stashboard is behind a VPN, the receiver won't work — use the schedule instead. |
