#!/usr/bin/env bash
set -euo pipefail

# ── colours ──────────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()    { echo -e "${GREEN}[deploy]${NC} $*"; }
warn()    { echo -e "${YELLOW}[deploy]${NC} $*"; }
error()   { echo -e "${RED}[deploy]${NC} $*" >&2; exit 1; }

# ── config ────────────────────────────────────────────────────────────────────
HEALTH_TIMEOUT=60   # seconds to wait for the app to become healthy
COMPOSE="docker compose"

# ── pre-flight ────────────────────────────────────────────────────────────────
[[ -f docker-compose.yml ]] || error "Run this script from the directory holding docker-compose.yml."
# .env is optional — the app auto-generates secrets and the compose file has
# defaults for everything else. Warn (don't fail) so first-time deploys just work.
[[ -f .env ]] || warn "No .env file found — using built-in defaults (auto-generated keys, port 8080, latest tag)."

# ── 1. pull the published image ───────────────────────────────────────────────
# Pulls vahac/stashboard at the tag set by STASHBOARD_TAG in .env (default
# `latest`). No source checkout or local build is required to update.
info "Pulling latest image..."
$COMPOSE pull app

# ── 2. (re)start app ──────────────────────────────────────────────────────────
# Single container: the app applies pending SQLite migrations on startup, so
# there is no separate database or migrator step. The database is a file on the
# `stashboard-data` volume and is preserved across image updates.
info "Starting app..."
$COMPOSE up -d app

# ── 3. wait for app to be healthy ─────────────────────────────────────────────
info "Waiting for app to become healthy (up to ${HEALTH_TIMEOUT}s)..."
SECONDS=0
until $COMPOSE ps app | grep -q "healthy\|running"; do
    if (( SECONDS >= HEALTH_TIMEOUT )); then
        warn "App did not report healthy in ${HEALTH_TIMEOUT}s. Showing logs:"
        $COMPOSE logs --tail=50 app
        error "Deployment failed. Check logs above."
    fi
    sleep 3
done

# ── 4. show result ────────────────────────────────────────────────────────────
info "Deployment complete."
info "App logs (last 20 lines):"
$COMPOSE logs --tail=20 app
