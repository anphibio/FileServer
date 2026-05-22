#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

. "$ROOT_DIR/scripts/lib-compose.sh"

ENV_FILE="${ENV_FILE:-.env}"
compose_cmd --env-file "$ENV_FILE" down
