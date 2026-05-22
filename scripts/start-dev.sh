#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

. "$ROOT_DIR/scripts/lib-compose.sh"

ENV_FILE="${ENV_FILE:-.env}"

if [ ! -f "$ENV_FILE" ]; then
  printf 'Arquivo de ambiente não encontrado: %s\n' "$ENV_FILE" >&2
  exit 1
fi

compose_cmd --env-file "$ENV_FILE" up --build -d

APP_WEB_PORT="$(awk -F= '/^APP_WEB_PORT=/{print $2}' "$ENV_FILE" | tail -n 1)"
APP_API_PORT="$(awk -F= '/^APP_API_PORT=/{print $2}' "$ENV_FILE" | tail -n 1)"
APP_WEB_PORT="${APP_WEB_PORT:-3300}"
APP_API_PORT="${APP_API_PORT:-8180}"

printf 'Stack de desenvolvimento iniciada.\n'
printf 'Web: http://localhost:%s\n' "$APP_WEB_PORT"
printf 'API: http://localhost:%s\n' "$APP_API_PORT"
printf 'Saúde: http://localhost:%s/health\n' "$APP_API_PORT"
