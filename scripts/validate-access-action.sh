#!/usr/bin/env bash
set -euo pipefail

API_BASE_URL="${API_BASE_URL:-http://localhost:8180}"
SSH_TARGET="${SSH_TARGET:-Administrator@192.168.2.170}"
SERVER_NAME="${SERVER_NAME:-FileServer}"
ROOT_NAME="${ROOT_NAME:-codex-access-action-$(date -u +%Y%m%d-%H%M%S)}"
ROOT_PATH="C:\\Corporativo\\${ROOT_NAME}"

echo "access-action: criando e acessando cenarios em ${ROOT_PATH}"

ps_script="$(mktemp)"
cat > "${ps_script}" <<PS
\$ErrorActionPreference = "Stop"
\$root = "${ROOT_PATH}"
Remove-Item \$root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path \$root | Out-Null
New-Item -ItemType Directory -Path (Join-Path \$root "sub") | Out-Null

Set-Content -Path (Join-Path \$root "access-root.txt") -Value "root" -Encoding UTF8
Set-Content -Path (Join-Path \$root "sub\\access-child.md") -Value "child" -Encoding UTF8
Set-Content -Path (Join-Path \$root "access-doc.docx") -Value "doc" -Encoding UTF8

Start-Sleep -Seconds 8

Get-Content -LiteralPath (Join-Path \$root "access-root.txt") -Raw | Out-Null
Get-Content -LiteralPath (Join-Path \$root "sub\\access-child.md") -Raw | Out-Null
Get-Content -LiteralPath (Join-Path \$root "access-doc.docx") -Raw | Out-Null

Write-Output \$root
PS

encoded="$(python3 - <<PY
import base64
from pathlib import Path
print(base64.b64encode(Path("${ps_script}").read_text().encode("utf-16le")).decode())
PY
)"
ssh "${SSH_TARGET}" "powershell -NoProfile -EncodedCommand ${encoded}" >/dev/null
rm -f "${ps_script}"

expected_json="$(mktemp)"
cat > "${expected_json}" <<JSON
[
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\access-root.txt",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\sub\\\\access-child.md",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\access-doc.docx"
]
JSON

echo "access-action: aguardando coleta do agente"
deadline=$((SECONDS + 180))
last_report=""
while (( SECONDS < deadline )); do
  events_json="$(mktemp)"
  curl -fsS "${API_BASE_URL}/api/events?server=${SERVER_NAME}&take=800" > "${events_json}"

  timeline_report="$(node scripts/validate-access-action-timeline.mjs "${events_json}" "${expected_json}" "${ROOT_NAME}" || true)"
  last_report="${timeline_report}"
  missing_count="$(jq '.missing | length' <<<"${timeline_report}")"
  extra_count="$(jq '.extras | length' <<<"${timeline_report}")"
  duplicate_count="$(jq '.duplicates | length' <<<"${timeline_report}")"

  if [[ "${missing_count}" == "0" && "${extra_count}" == "0" && "${duplicate_count}" == "0" ]]; then
    echo "access-action timeline:"
    echo "${timeline_report}" | jq .
    ssh "${SSH_TARGET}" "powershell -NoProfile -Command \"Remove-Item -LiteralPath '${ROOT_PATH}' -Recurse -Force -ErrorAction SilentlyContinue\"" >/dev/null || true
    rm -f "${expected_json}"
    rm -f "${events_json}"
    echo "access-action: OK"
    exit 0
  fi

  rm -f "${events_json}"
  sleep 10
done

echo "access-action: FALHOU, timeline fora do esperado:"
echo "${last_report}" | jq .
rm -f "${expected_json}"
exit 1
