#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/lib-api-auth.sh"

API_BASE_URL="${API_BASE_URL:-http://localhost:8180}"
API_KEY="$(require_human_api_key)"
SSH_TARGET="${SSH_TARGET:-Administrator@192.168.2.170}"
SERVER_NAME="${SERVER_NAME:-FileServer}"
ROOT_NAME="${ROOT_NAME:-codex-rename-action-$(date -u +%Y%m%d-%H%M%S)}"
ROOT_PATH="C:\\Corporativo\\${ROOT_NAME}"

echo "rename-action: criando e renomeando cenarios em ${ROOT_PATH}"

ps_script="$(mktemp)"
cat > "${ps_script}" <<PS
\$ErrorActionPreference = "Stop"
\$root = "${ROOT_PATH}"
Remove-Item \$root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path \$root | Out-Null

Set-Content -Path (Join-Path \$root "01-single-file-old.txt") -Value "single" -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path \$root "02-many-files") | Out-Null
Set-Content -Path (Join-Path \$root "02-many-files\\a-old.txt") -Value "a" -Encoding UTF8
Set-Content -Path (Join-Path \$root "02-many-files\\b-old.md") -Value "b" -Encoding UTF8
Set-Content -Path (Join-Path \$root "02-many-files\\c-old.yml") -Value "c" -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path \$root "03-empty-folder-old") | Out-Null

New-Item -ItemType Directory -Path (Join-Path \$root "04-folder-with-file-old") | Out-Null
Set-Content -Path (Join-Path \$root "04-folder-with-file-old\\inside.txt") -Value "inside" -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path \$root "05-tree\\sub-a-old") -Force | Out-Null
Set-Content -Path (Join-Path \$root "05-tree\\root-old.docx") -Value "doc" -Encoding UTF8
Set-Content -Path (Join-Path \$root "05-tree\\sub-a-old\\child.txt") -Value "child" -Encoding UTF8

Start-Sleep -Seconds 3

Rename-Item -LiteralPath (Join-Path \$root "01-single-file-old.txt") -NewName "01-single-file-renamed.txt"
Rename-Item -LiteralPath (Join-Path \$root "02-many-files\\a-old.txt") -NewName "a-renamed.txt"
Rename-Item -LiteralPath (Join-Path \$root "02-many-files\\b-old.md") -NewName "b-renamed.md"
Rename-Item -LiteralPath (Join-Path \$root "02-many-files\\c-old.yml") -NewName "c-renamed.yml"
Rename-Item -LiteralPath (Join-Path \$root "03-empty-folder-old") -NewName "03-empty-folder-renamed"
Rename-Item -LiteralPath (Join-Path \$root "04-folder-with-file-old") -NewName "04-folder-with-file-renamed"
Rename-Item -LiteralPath (Join-Path \$root "05-tree\\root-old.docx") -NewName "root-renamed.docx"
Rename-Item -LiteralPath (Join-Path \$root "05-tree\\sub-a-old") -NewName "sub-a-renamed"

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
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\01-single-file-old.txt",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\01-single-file-renamed.txt"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\a-old.txt",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\a-renamed.txt"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\b-old.md",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\b-renamed.md"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\c-old.yml",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\c-renamed.yml"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\03-empty-folder-old",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\03-empty-folder-renamed"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\04-folder-with-file-old",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\04-folder-with-file-renamed"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\root-old.docx",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\root-renamed.docx"
  },
  {
    "from": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\sub-a-old",
    "to": "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\sub-a-renamed"
  }
]
JSON

echo "rename-action: aguardando coleta do agente"
deadline=$((SECONDS + 180))
last_report=""
while (( SECONDS < deadline )); do
  events_json="$(mktemp)"
  curl -fsS -H "X-Api-Key: ${API_KEY}" "${API_BASE_URL}/api/events?server=${SERVER_NAME}&take=800" > "${events_json}"

  timeline_report="$(node scripts/validate-rename-action-timeline.mjs "${events_json}" "${expected_json}" "${ROOT_NAME}" || true)"
  last_report="${timeline_report}"
  missing_count="$(jq '.missing | length' <<<"${timeline_report}")"
  extra_count="$(jq '.extras | length' <<<"${timeline_report}")"
  duplicate_count="$(jq '.duplicates | length' <<<"${timeline_report}")"

  if [[ "${missing_count}" == "0" && "${extra_count}" == "0" && "${duplicate_count}" == "0" ]]; then
    echo "rename-action timeline:"
    echo "${timeline_report}" | jq .
    ssh "${SSH_TARGET}" "powershell -NoProfile -Command \"Remove-Item -LiteralPath '${ROOT_PATH}' -Recurse -Force -ErrorAction SilentlyContinue\"" >/dev/null || true
    rm -f "${expected_json}"
    rm -f "${events_json}"
    echo "rename-action: OK"
    exit 0
  fi

  rm -f "${events_json}"
  sleep 10
done

echo "rename-action: FALHOU, timeline fora do esperado:"
echo "${last_report}" | jq .
rm -f "${expected_json}"
exit 1
