#!/usr/bin/env bash
set -Eeuo pipefail

# Deploy helper for rsync + optional remote build/restart.
# Server-specific values come from the environment (usually via deploy.sh + deploy.local.env);
# no host/user/key defaults are stored in the repo.
#
# Example:
#   LOCAL_PATH="/path/to/site/" REMOTE_USER=me REMOTE_HOST=host REMOTE_PATH=/srv/app/ \
#   ./deploy/deploy-rsync.sh --dry-run

LOCAL_PATH="${LOCAL_PATH:-}"
REMOTE_USER="${REMOTE_USER:-}"
REMOTE_HOST="${REMOTE_HOST:-}"
SSH_PORT="${SSH_PORT:-22}"
KEY_SSH="${KEY_SSH:-}"
REMOTE_PATH="${REMOTE_PATH:-}"

if [[ -z "$LOCAL_PATH" || -z "$REMOTE_USER" || -z "$REMOTE_HOST" || -z "$REMOTE_PATH" ]]; then
  echo "LOCAL_PATH, REMOTE_USER, REMOTE_HOST and REMOTE_PATH must be set (use deploy.sh + deploy.local.env)." >&2
  exit 2
fi
APP_NAME="${APP_NAME:-evedeck}"
NODE_VERSION="${NODE_VERSION:-22}"

DO_DELETE="${DO_DELETE:-yes}"
PRESERVE_REMOTE_ENV="${PRESERVE_REMOTE_ENV:-yes}"
PRESERVE_TINYMCE="${PRESERVE_TINYMCE:-yes}"
DO_REMOTE_BUILD="${DO_REMOTE_BUILD:-yes}"
DRY_RUN="${DRY_RUN:-no}"
RSYNC_INFO="${RSYNC_INFO:-progress2,stats2}"
PM2_CLEAR_LOGS="${PM2_CLEAR_LOGS:-no}"
HEALTHCHECK_ENABLED="${HEALTHCHECK_ENABLED:-yes}"
HEALTHCHECK_URL="${HEALTHCHECK_URL:-https://evedeck.space/health}"
HEALTHCHECK_RETRIES="${HEALTHCHECK_RETRIES:-12}"
HEALTHCHECK_DELAY="${HEALTHCHECK_DELAY:-5}"
HEALTHCHECK_TIMEOUT="${HEALTHCHECK_TIMEOUT:-10}"

usage() {
  cat <<'EOF'
Usage: deploy/deploy-rsync.sh [options]

Options:
  --dry-run           rsync dry run only (no write, no remote build)
  --no-build          skip remote npm/build/pm2 restart
  --no-delete         disable rsync --delete
  --no-preserve-env   allow syncing/deleting .env* files
  --no-preserve-tinymce do not protect remote public/tinymce/
  --pm2-clear-logs    clear PM2 logs before tailing app logs
  --no-health-check   skip post-deploy GET /health verification
  --health-url URL    health endpoint URL (default: https://evedeck.space/health)
  --health-retries N  retry count for health check (default: 12)
  --health-delay SEC  delay between health retries (default: 5)
  --health-timeout SEC curl timeout per health request (default: 10)
  -h, --help          show this help

Env:
  KEY_SSH             optional SSH key path (default: use ssh-agent/config)
  RSYNC_INFO          rsync --info value (default: progress2,stats2)
  PM2_CLEAR_LOGS      yes/no (default: no)
  HEALTHCHECK_ENABLED yes/no (default: yes)
  HEALTHCHECK_URL     URL to validate after deploy
  HEALTHCHECK_RETRIES positive integer retry count
  HEALTHCHECK_DELAY   positive integer retry delay in seconds
  HEALTHCHECK_TIMEOUT positive integer curl timeout in seconds
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run) DRY_RUN="yes"; DO_REMOTE_BUILD="no"; shift ;;
    --no-build) DO_REMOTE_BUILD="no"; shift ;;
    --no-delete) DO_DELETE="no"; shift ;;
    --no-preserve-env) PRESERVE_REMOTE_ENV="no"; shift ;;
    --no-preserve-tinymce) PRESERVE_TINYMCE="no"; shift ;;
    --pm2-clear-logs) PM2_CLEAR_LOGS="yes"; shift ;;
    --no-health-check) HEALTHCHECK_ENABLED="no"; shift ;;
    --health-url)
      if [[ $# -lt 2 ]]; then
        echo "--health-url requires a value" >&2
        exit 2
      fi
      HEALTHCHECK_URL="$2"
      shift 2
      ;;
    --health-retries)
      if [[ $# -lt 2 ]]; then
        echo "--health-retries requires a value" >&2
        exit 2
      fi
      HEALTHCHECK_RETRIES="$2"
      shift 2
      ;;
    --health-delay)
      if [[ $# -lt 2 ]]; then
        echo "--health-delay requires a value" >&2
        exit 2
      fi
      HEALTHCHECK_DELAY="$2"
      shift 2
      ;;
    --health-timeout)
      if [[ $# -lt 2 ]]; then
        echo "--health-timeout requires a value" >&2
        exit 2
      fi
      HEALTHCHECK_TIMEOUT="$2"
      shift 2
      ;;
    -h|--help) usage; exit 0 ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      exit 2
      ;;
  esac
done

if [[ ! -d "$LOCAL_PATH" ]]; then
  echo "LOCAL_PATH not found: $LOCAL_PATH" >&2
  exit 1
fi
if [[ ! -f "${LOCAL_PATH%/}/package.json" ]]; then
  echo "package.json missing under LOCAL_PATH: $LOCAL_PATH" >&2
  exit 1
fi
if [[ -n "$KEY_SSH" && ! -f "$KEY_SSH" ]]; then
  echo "SSH key file not found: $KEY_SSH" >&2
  exit 1
fi

is_positive_int() {
  [[ "$1" =~ ^[0-9]+$ ]] && [[ "$1" != "0" ]]
}

if [[ "$HEALTHCHECK_ENABLED" == "yes" ]]; then
  if ! command -v curl >/dev/null 2>&1; then
    echo "curl is required for post-deploy health checks" >&2
    exit 1
  fi
  if ! is_positive_int "$HEALTHCHECK_RETRIES"; then
    echo "HEALTHCHECK_RETRIES must be a positive integer: $HEALTHCHECK_RETRIES" >&2
    exit 1
  fi
  if ! is_positive_int "$HEALTHCHECK_DELAY"; then
    echo "HEALTHCHECK_DELAY must be a positive integer: $HEALTHCHECK_DELAY" >&2
    exit 1
  fi
  if ! is_positive_int "$HEALTHCHECK_TIMEOUT"; then
    echo "HEALTHCHECK_TIMEOUT must be a positive integer: $HEALTHCHECK_TIMEOUT" >&2
    exit 1
  fi
fi

RSYNC_ARGS=(-azvh "--info=$RSYNC_INFO")
if [[ "$DO_DELETE" == "yes" ]]; then
  RSYNC_ARGS+=(--delete)
fi
if [[ "$DRY_RUN" == "yes" ]]; then
  RSYNC_ARGS+=(--dry-run)
fi

RSYNC_ARGS+=(--exclude 'node_modules/' --exclude '.next/' --exclude '.git/')
if [[ "$PRESERVE_REMOTE_ENV" == "yes" ]]; then
  RSYNC_ARGS+=(--exclude '.env*')
fi
if [[ "$PRESERVE_TINYMCE" == "yes" ]]; then
  RSYNC_ARGS+=(--filter='P public/tinymce/***')
fi

SSH_OPTS=(-p "$SSH_PORT" -o StrictHostKeyChecking=accept-new)
if [[ -n "$KEY_SSH" ]]; then
  SSH_OPTS=(-i "$KEY_SSH" -o IdentitiesOnly=yes "${SSH_OPTS[@]}")
fi

SSH_TRANSPORT_CMD=(ssh "${SSH_OPTS[@]}")
printf -v SSH_TRANSPORT '%q ' "${SSH_TRANSPORT_CMD[@]}"
SSH_TRANSPORT="${SSH_TRANSPORT% }"

step() {
  printf "\n==> %s\n" "$1"
}

run_health_check() {
  local url="$1"
  local retries="$2"
  local delay="$3"
  local timeout="$4"
  local body_file
  body_file="$(mktemp)"

  local attempt=1
  while [[ "$attempt" -le "$retries" ]]; do
    local http_code
    http_code="$(curl -sS -L --max-time "$timeout" -o "$body_file" -w '%{http_code}' "$url" || true)"

    if [[ "$http_code" == "200" ]] && grep -Eq '"status"[[:space:]]*:[[:space:]]*"ok"' "$body_file"; then
      printf "   health check passed on attempt %s/%s\n" "$attempt" "$retries"
      rm -f "$body_file"
      return 0
    fi

    if [[ "$attempt" -lt "$retries" ]]; then
      printf "   health check attempt %s/%s failed (http %s), retrying in %ss\n" "$attempt" "$retries" "$http_code" "$delay"
      sleep "$delay"
    else
      printf "   health check failed after %s attempts (http %s)\n" "$retries" "$http_code" >&2
      if [[ -s "$body_file" ]]; then
        echo "   ----- /health response -----" >&2
        sed -n '1,20p' "$body_file" >&2
      fi
      rm -f "$body_file"
      return 1
    fi

    attempt=$((attempt + 1))
  done
}

step "Syncing files to $REMOTE_USER@$REMOTE_HOST:$REMOTE_PATH"
rsync "${RSYNC_ARGS[@]}" -e "$SSH_TRANSPORT" \
  "$LOCAL_PATH" "$REMOTE_USER@$REMOTE_HOST:$REMOTE_PATH"

if [[ "$DO_REMOTE_BUILD" == "yes" ]]; then
  step "Running remote install/build/restart for $APP_NAME"
  ssh "${SSH_OPTS[@]}" \
    "$REMOTE_USER@$REMOTE_HOST" \
    "bash -se -- $(printf '%q ' "$REMOTE_PATH" "$NODE_VERSION" "$APP_NAME" "$PM2_CLEAR_LOGS")" <<'REMOTE_SCRIPT'
set -Eeuo pipefail

REMOTE_PATH="$1"
NODE_VERSION="$2"
APP_NAME="$3"
PM2_CLEAR_LOGS="${4:-no}"

phase() {
  printf "\n---- %s ----\n" "$1"
}

run_with_spinner() {
  local label="$1"
  shift

  local log_file
  log_file="$(mktemp)"
  local start_ts
  start_ts="$(date +%s)"

  "$@" >"$log_file" 2>&1 &
  local cmd_pid=$!

  if [[ -t 1 ]]; then
    local frames='-\|/'
    local i=0
    while kill -0 "$cmd_pid" 2>/dev/null; do
      local frame="${frames:i%4:1}"
      printf "\r   %s... %s" "$label" "$frame"
      i=$((i + 1))
      sleep 0.12
    done
  fi

  local cmd_status=0
  if wait "$cmd_pid"; then
    cmd_status=0
  else
    cmd_status=$?
  fi

  local end_ts
  end_ts="$(date +%s)"
  local elapsed
  elapsed=$((end_ts - start_ts))

  if [[ "$cmd_status" -eq 0 ]]; then
    if [[ -t 1 ]]; then
      printf "\r   %s... done (%ss)\n" "$label" "$elapsed"
    else
      printf "   %s... done (%ss)\n" "$label" "$elapsed"
    fi
    rm -f "$log_file"
    return 0
  fi

  if [[ -t 1 ]]; then
    printf "\r   %s... failed (exit %d)\n" "$label" "$cmd_status" >&2
  else
    printf "   %s... failed (exit %d)\n" "$label" "$cmd_status" >&2
  fi
  echo "   ----- command output -----" >&2
  cat "$log_file" >&2
  rm -f "$log_file"
  return "$cmd_status"
}

export NVM_DIR="$HOME/.nvm"
if [[ -s "$NVM_DIR/nvm.sh" ]]; then
  # shellcheck source=/dev/null
  . "$NVM_DIR/nvm.sh"
  nvm use "$NODE_VERSION" >/dev/null || true
fi

NODE_BIN="$(command -v node || true)"
if [[ -z "$NODE_BIN" ]]; then
  echo "Node.js binary not found on remote host" >&2
  exit 1
fi

cd "$REMOTE_PATH"

phase "1/4 Install dependencies"
run_with_spinner "npm ci --no-audit --fund=false" npm ci --no-audit --fund=false

phase "2/4 Build app"
run_with_spinner "npx next build" npx next build

if [[ "$PM2_CLEAR_LOGS" == "yes" ]]; then
  phase "3/5 Restart PM2 app"
else
  phase "3/4 Restart PM2 app"
fi
pm2 delete "$APP_NAME" >/dev/null 2>&1 || true
pm2 start deploy/ecosystem.config.cjs --only "$APP_NAME" --interpreter "$NODE_BIN" --update-env

if [[ "$PM2_CLEAR_LOGS" == "yes" ]]; then
  phase "4/5 Clear PM2 logs"
  pm2 flush "$APP_NAME" >/dev/null 2>&1 || pm2 flush >/dev/null 2>&1 || true
  phase "5/5 Save PM2 and tail recent logs"
else
  phase "4/4 Save PM2 and tail recent logs"
fi
pm2 save
pm2 logs "$APP_NAME" --lines 60 --nostream || true
REMOTE_SCRIPT
else
  step "Remote build/restart skipped"
fi

if [[ "$HEALTHCHECK_ENABLED" == "yes" && "$DRY_RUN" != "yes" ]]; then
  step "Running post-deploy health check: $HEALTHCHECK_URL"
  run_health_check "$HEALTHCHECK_URL" "$HEALTHCHECK_RETRIES" "$HEALTHCHECK_DELAY" "$HEALTHCHECK_TIMEOUT"
fi

step "Deploy complete"
