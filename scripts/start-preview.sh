#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

. "$ROOT_DIR/scripts/lib-compose.sh"

ENV_FILE="${ENV_FILE:-.env.preview.example}"

if [ ! -f "$ENV_FILE" ]; then
  printf 'Arquivo de ambiente não encontrado: %s\n' "$ENV_FILE" >&2
  exit 1
fi

compose_cmd --env-file "$ENV_FILE" -f docker-compose.preview.yml up --build -d

PREVIEW_WEB_PORT="$(awk -F= '/^PREVIEW_WEB_PORT=/{print $2}' "$ENV_FILE" | tail -n 1)"
PREVIEW_API_PORT="$(awk -F= '/^PREVIEW_API_PORT=/{print $2}' "$ENV_FILE" | tail -n 1)"
PREVIEW_WEB_PORT="${PREVIEW_WEB_PORT:-3001}"
PREVIEW_API_PORT="${PREVIEW_API_PORT:-8081}"

printf 'Preview iniciado.\n'
printf 'Web: http://localhost:%s\n' "$PREVIEW_WEB_PORT"
printf 'API: http://localhost:%s\n' "$PREVIEW_API_PORT"
printf 'Para popular a demo: API_BASE_URL=http://localhost:%s ./scripts/seed-demo.sh\n' "$PREVIEW_API_PORT"
printf 'Para validar o preview: ENV_FILE=%s ./scripts/check-preview.sh\n' "$ENV_FILE"
