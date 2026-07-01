#!/usr/bin/env bash
set -euo pipefail

API_BASE_URL="${API_BASE_URL:-http://localhost:8180}"
SSH_TARGET="${SSH_TARGET:-Administrator@192.168.2.170}"
SERVER_NAME="${SERVER_NAME:-FileServer}"
ROOT_NAME="${ROOT_NAME:-codex-move-action-$(date -u +%Y%m%d-%H%M%S)}"
ROOT_PATH="C:\\Corporativo\\${ROOT_NAME}"

echo "move-action: criando e movendo cenarios em ${ROOT_PATH}"

ps_script="$(mktemp)"
cat > "${ps_script}" <<PS
\$ErrorActionPreference = "Stop"
\$root = "${ROOT_PATH}"
Remove-Item \$root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path \$root | Out-Null

New-Item -ItemType Directory -Path (Join-Path \$root "destino-a") | Out-Null
New-Item -ItemType Directory -Path (Join-Path \$root "destino-b") | Out-Null
New-Item -ItemType Directory -Path (Join-Path \$root "destino-c") | Out-Null
New-Item -ItemType Directory -Path (Join-Path \$root "destino-d") | Out-Null

Set-Content -Path (Join-Path \$root "01-single-file.txt") -Value "single" -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path \$root "02-many-files") | Out-Null
Set-Content -Path (Join-Path \$root "02-many-files\\a.txt") -Value "a" -Encoding UTF8
Set-Content -Path (Join-Path \$root "02-many-files\\b.md") -Value "b" -Encoding UTF8
Set-Content -Path (Join-Path \$root "02-many-files\\c.yml") -Value "c" -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path \$root "03-empty-folder") | Out-Null

New-Item -ItemType Directory -Path (Join-Path \$root "04-folder-with-file") | Out-Null
Set-Content -Path (Join-Path \$root "04-folder-with-file\\inside.txt") -Value "inside" -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path \$root "05-tree\\sub-a") -Force | Out-Null
Set-Content -Path (Join-Path \$root "05-tree\\root.docx") -Value "doc" -Encoding UTF8
Set-Content -Path (Join-Path \$root "05-tree\\sub-a\\child.txt") -Value "child" -Encoding UTF8

Start-Sleep -Seconds 3

Move-Item -LiteralPath (Join-Path \$root "01-single-file.txt") -Destination (Join-Path \$root "destino-a\\01-single-file.txt")
Move-Item -LiteralPath (Join-Path \$root "02-many-files\\a.txt") -Destination (Join-Path \$root "destino-b\\a.txt")
Move-Item -LiteralPath (Join-Path \$root "02-many-files\\b.md") -Destination (Join-Path \$root "destino-b\\b.md")
Move-Item -LiteralPath (Join-Path \$root "02-many-files\\c.yml") -Destination (Join-Path \$root "destino-b\\c.yml")
Move-Item -LiteralPath (Join-Path \$root "03-empty-folder") -Destination (Join-Path \$root "destino-c\\03-empty-folder")
Move-Item -LiteralPath (Join-Path \$root "04-folder-with-file") -Destination (Join-Path \$root "destino-c\\04-folder-with-file")
Move-Item -LiteralPath (Join-Path \$root "05-tree\\root.docx") -Destination (Join-Path \$root "destino-d\\root.docx")
Move-Item -LiteralPath (Join-Path \$root "05-tree\\sub-a") -Destination (Join-Path \$root "destino-d\\sub-a")

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
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\01-single-file.txt",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\destino-a\\\\01-single-file.txt"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\a.txt",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\destino-b\\\\a.txt"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\b.md",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\destino-b\\\\b.md"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\c.yml",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\destino-b\\\\c.yml"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\03-empty-folder",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\destino-c\\\\03-empty-folder"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\04-folder-with-file",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\destino-c\\\\04-folder-with-file"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\root.docx",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\destino-d\\\\root.docx"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\sub-a",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\destino-d\\\\sub-a"
  }
]
JSON

echo "move-action: aguardando coleta do agente"
deadline=$((SECONDS + 180))
last_report=""
while (( SECONDS < deadline )); do
  events_json="$(mktemp)"
  curl -fsS "${API_BASE_URL}/api/events?server=${SERVER_NAME}&take=1000" > "${events_json}"

  timeline_report="$(node scripts/validate-move-action-timeline.mjs "${events_json}" "${expected_json}" "${ROOT_NAME}" || true)"
  last_report="${timeline_report}"
  missing_count="$(jq '.missing | length' <<<"${timeline_report}")"
  extra_count="$(jq '.extras | length' <<<"${timeline_report}")"
  duplicate_count="$(jq '.duplicates | length' <<<"${timeline_report}")"

  if [[ "${missing_count}" == "0" && "${extra_count}" == "0" && "${duplicate_count}" == "0" ]]; then
    echo "move-action timeline:"
    echo "${timeline_report}" | jq .
    ssh "${SSH_TARGET}" "powershell -NoProfile -Command \"Remove-Item -LiteralPath '${ROOT_PATH}' -Recurse -Force -ErrorAction SilentlyContinue\"" >/dev/null || true
    rm -f "${expected_json}"
    rm -f "${events_json}"
    echo "move-action: OK"
    exit 0
  fi

  rm -f "${events_json}"
  sleep 10
done

echo "move-action: FALHOU, timeline fora do esperado:"
echo "${last_report}" | jq .
rm -f "${expected_json}"
exit 1
