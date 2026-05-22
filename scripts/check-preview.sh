#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="${ENV_FILE:-.env.preview.example}"
SEED_DEMO="${SEED_DEMO:-true}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-30}"
SLEEP_SECONDS="${SLEEP_SECONDS:-2}"

if [ ! -f "$ENV_FILE" ]; then
  printf 'Arquivo de ambiente não encontrado: %s\n' "$ENV_FILE" >&2
  exit 1
fi

read_env_value() {
  local key="$1"
  awk -F= -v key="$key" '$1 == key {print $2}' "$ENV_FILE" | tail -n 1
}

PREVIEW_WEB_PORT="$(read_env_value PREVIEW_WEB_PORT)"
PREVIEW_API_PORT="$(read_env_value PREVIEW_API_PORT)"
PREVIEW_WEB_PORT="${PREVIEW_WEB_PORT:-3001}"
PREVIEW_API_PORT="${PREVIEW_API_PORT:-8081}"

WEB_URL="http://localhost:${PREVIEW_WEB_PORT}"
API_URL="http://localhost:${PREVIEW_API_PORT}"

wait_for_url() {
  local name="$1"
  local url="$2"
  local attempt=1

  while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
    if curl -fsS "$url" >/dev/null 2>&1; then
      printf 'OK %s disponível em %s\n' "$name" "$url"
      return 0
    fi

    printf 'Aguardando %s (%s/%s)...\n' "$name" "$attempt" "$MAX_ATTEMPTS"
    sleep "$SLEEP_SECONDS"
    attempt=$((attempt + 1))
  done

  printf 'Falha ao alcançar %s em %s\n' "$name" "$url" >&2
  return 1
}

printf 'Verificando preview local...\n'
wait_for_url "API" "${API_URL}/health"
wait_for_url "Web" "$WEB_URL"

if [ "$SEED_DEMO" = "true" ]; then
  printf 'Populando dados de demonstração...\n'
  API_BASE_URL="$API_URL" ./scripts/seed-demo.sh >/dev/null
  printf 'OK dados de demonstração carregados\n'
fi

check_json() {
  local name="$1"
  local path="$2"
  curl -fsS "${API_URL}${path}" >/dev/null
  printf 'OK %s\n' "$name"
}

check_json "eventos" "/api/events?take=5"
check_json "alertas" "/api/alerts?status=open&take=5"
check_json "agentes" "/api/agents/health"
check_json "caminhos" "/api/monitored-paths"
check_json "auditoria" "/api/admin-audit?take=5"

printf '\nPreview pronto para revisão visual.\n'
printf 'Web: %s\n' "$WEB_URL"
printf 'API: %s\n' "$API_URL"
