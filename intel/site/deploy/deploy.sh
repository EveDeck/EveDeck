#!/usr/bin/env bash
set -Eeuo pipefail

# Convenience wrapper for the rsync deploy script.
#
# Server-specific settings (host, user, SSH key, remote path) are NOT stored in the repo.
# They load from deploy.local.env next to this script (gitignored) or from the environment:
#   EVEDECK_LOCAL_PATH
#   EVEDECK_KEY_SSH
#   EVEDECK_REMOTE_USER
#   EVEDECK_REMOTE_HOST
#   EVEDECK_REMOTE_PATH
#   EVEDECK_APP_NAME
#   EVEDECK_HEALTHCHECK_URL
#
# See deploy.local.env.example for the expected format.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# This deploy needs rsync (and ssh). Git Bash / MSYS2 ships neither by default, but a
# working WSL distro almost always has both. If we're running under MSYS/Cygwin and
# rsync is missing, transparently re-run the whole script inside WSL instead of failing
# with "rsync: command not found" partway through.
if [[ -z "${EVEDECK_DEPLOY_REEXEC:-}" ]] && ! command -v rsync >/dev/null 2>&1; then
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      if command -v wsl.exe >/dev/null 2>&1; then
        wsl_script="$(printf '%s' "$SCRIPT_DIR/deploy.sh" | sed -E 's#^/([A-Za-z])/#/mnt/\L\1/#')"
        echo "rsync not found under $(uname -s); re-running this deploy inside WSL..." >&2
        # Stop Git Bash/MSYS from rewriting the /mnt/... path argument into a Windows path.
        export MSYS2_ARG_CONV_EXCL='*' MSYS_NO_PATHCONV=1
        exec wsl.exe env EVEDECK_DEPLOY_REEXEC=1 bash "$wsl_script" "$@"
      fi
      echo "rsync not found and wsl.exe is unavailable." >&2
      echo "Install rsync (MSYS2: 'pacman -S rsync') or run this script from WSL." >&2
      exit 1
      ;;
  esac
fi

if [[ -f "$SCRIPT_DIR/deploy.local.env" ]]; then
  # shellcheck source=/dev/null
  source "$SCRIPT_DIR/deploy.local.env"
fi

DEFAULT_LOCAL_PATH="$(cd "$SCRIPT_DIR/.." && pwd)/"
DEFAULT_APP_NAME="evedeck-intel"
DEFAULT_HEALTHCHECK_URL="https://intel.evedeck.space/health"

LOCAL_PATH_VALUE="${EVEDECK_LOCAL_PATH:-$DEFAULT_LOCAL_PATH}"
KEY_SSH_VALUE="${EVEDECK_KEY_SSH:-}"
REMOTE_USER_VALUE="${EVEDECK_REMOTE_USER:-}"
REMOTE_HOST_VALUE="${EVEDECK_REMOTE_HOST:-}"
REMOTE_PATH_VALUE="${EVEDECK_REMOTE_PATH:-}"
APP_NAME_VALUE="${EVEDECK_APP_NAME:-$DEFAULT_APP_NAME}"
HEALTHCHECK_URL_VALUE="${EVEDECK_HEALTHCHECK_URL:-$DEFAULT_HEALTHCHECK_URL}"

if [[ -z "$REMOTE_HOST_VALUE" || -z "$REMOTE_USER_VALUE" || -z "$REMOTE_PATH_VALUE" ]]; then
  echo "Missing deploy target settings (EVEDECK_REMOTE_HOST / EVEDECK_REMOTE_USER / EVEDECK_REMOTE_PATH)." >&2
  echo "Create $SCRIPT_DIR/deploy.local.env (see deploy.local.env.example) or export them." >&2
  exit 2
fi

exec env \
  LOCAL_PATH="$LOCAL_PATH_VALUE" \
  KEY_SSH="$KEY_SSH_VALUE" \
  REMOTE_USER="$REMOTE_USER_VALUE" \
  REMOTE_HOST="$REMOTE_HOST_VALUE" \
  REMOTE_PATH="$REMOTE_PATH_VALUE" \
  APP_NAME="$APP_NAME_VALUE" \
  HEALTHCHECK_URL="$HEALTHCHECK_URL_VALUE" \
  "$SCRIPT_DIR/deploy-rsync.sh" "$@"
