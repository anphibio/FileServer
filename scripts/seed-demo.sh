#!/usr/bin/env bash
set -euo pipefail

API_BASE_URL="${API_BASE_URL:-http://localhost:8080}"
API_KEY="${API_KEY:-}"
RUN_ID="${RUN_ID:-$(date +%s)}"

headers=(-H "Content-Type: application/json")

if [ -n "$API_KEY" ]; then
  headers+=(-H "X-Api-Key: $API_KEY")
fi

post_json() {
  local path="$1"
  local payload="$2"
  local response_file
  response_file="$(mktemp)"
  local status_code

  status_code="$(
    curl -sS -o "$response_file" -w "%{http_code}" -X POST "${API_BASE_URL}${path}" "${headers[@]}" -d "$payload"
  )"

  if [ "$status_code" -lt 200 ] || [ "$status_code" -ge 300 ]; then
    printf 'Falha em %s (HTTP %s)\n' "$path" "$status_code" >&2
    cat "$response_file" >&2
    rm -f "$response_file"
    return 1
  fi

  rm -f "$response_file"
}

json_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

event_json() {
  local action="$1"
  local user="$2"
  local path="$3"
  local process="$4"
  local extension="$5"
  local source="${6:-manual-demo}"
  local timestamp
  timestamp="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

  cat <<JSON
{
  "timestampUtc": "$timestamp",
  "server": "FS01",
  "share": "Departamentos",
  "path": "$(json_escape "$path")",
  "objectType": "file",
  "action": "$action",
  "user": "$(json_escape "$user")",
  "sourceHost": "WKS-DEMO-01",
  "sourceIp": "192.168.10.50",
  "processName": "$process",
  "fileSizeBytes": 20480,
  "extension": "$extension",
  "result": "success",
  "severity": "info",
  "source": "$source"
}
JSON
}

batch_start() {
  printf '['
}

batch_item() {
  local is_first="$1"
  shift

  if [ "$is_first" != "true" ]; then
    printf ','
  fi

  event_json "$@"
}

batch_end() {
  printf ']'
}

printf 'Seed demo em %s\n' "$API_BASE_URL"
printf 'Run ID: %s\n' "$RUN_ID"

printf '1/6 heartbeat do agente...\n'
post_json "/api/agents/heartbeat" '{
  "agentId": "fs01-agent",
  "server": "FS01",
  "status": "running",
  "version": "demo",
  "lastRecordId": 145230,
  "lastUsnByVolume": {
    "D:": 84520122
  },
  "message": "Agente demo online"
}'

printf '2/6 caminhos monitorados...\n'
post_json "/api/monitored-paths" "{
  \"server\": \"FS01\",
  \"share\": \"Departamentos\",
  \"path\": \"D:\\\\Shares\\\\Departamentos\\\\Financeiro-Demo-${RUN_ID}\",
  \"status\": \"active\",
  \"priority\": \"critical\",
  \"owner\": \"Infra / Financeiro\",
  \"notes\": \"Pasta piloto com auditoria NTFS habilitada\"
}"

post_json "/api/monitored-paths" "{
  \"server\": \"FS01\",
  \"share\": \"Departamentos\",
  \"path\": \"D:\\\\Shares\\\\Departamentos\\\\RH-Demo-${RUN_ID}\",
  \"status\": \"planned\",
  \"priority\": \"high\",
  \"owner\": \"Infra / RH\",
  \"notes\": \"Entrar na segunda onda do piloto\"
}"

printf '3/6 eventos unitários...\n'
post_json "/api/events" "$(event_json "modified" "EMPRESA\\maria.silva" "\\\\FS01\\Departamentos\\Financeiro\\relatorio-mensal-${RUN_ID}.xlsx" "EXCEL.EXE" ".xlsx")"
post_json "/api/events" "$(event_json "permission_changed" "EMPRESA\\admin.arquivos" "\\\\FS01\\Departamentos\\RH\\Folha-${RUN_ID}" "explorer.exe" "")"
post_json "/api/events" "$(event_json "renamed" "EMPRESA\\joao.santos" "\\\\FS01\\Departamentos\\Projetos\\contrato-v2-${RUN_ID}.docx" "WINWORD.EXE" ".docx" "usn-journal+security-log")"

printf '4/6 lote de exclusões...\n'
delete_batch="$(
  batch_start
  first=true
  for index in $(seq 1 55); do
    batch_item "$first" "deleted" "EMPRESA\\usuario.teste" "\\\\FS01\\Departamentos\\Temp\\arquivo-${RUN_ID}-${index}.tmp" "explorer.exe" ".tmp"
    first=false
  done
  batch_end
)"
post_json "/api/events/batch" "$delete_batch"

printf '5/6 lote de ransomware simulado...\n'
ransom_batch="$(
  batch_start
  first=true
  for index in $(seq 1 12); do
    batch_item "$first" "modified" "EMPRESA\\usuario.suspeito" "\\\\FS01\\Departamentos\\Dados\\cliente-${RUN_ID}-${index}.locked" "unknown.exe" ".locked"
    first=false
  done
  batch_end
)"
post_json "/api/events/batch" "$ransom_batch"

printf '6/6 seed concluído.\n'
printf 'Seed concluido.\n'
printf 'Eventos: %s/api/events?take=20\n' "$API_BASE_URL"
printf 'Alertas: %s/api/alerts?status=open&take=20\n' "$API_BASE_URL"
printf 'Agentes: %s/api/agents/health\n' "$API_BASE_URL"
printf 'Caminhos: %s/api/monitored-paths\n' "$API_BASE_URL"
