#!/usr/bin/env bash
set -euo pipefail

API_BASE_URL="${API_BASE_URL:-http://localhost:8180}"
SSH_TARGET="${SSH_TARGET:-Administrator@192.168.2.170}"
SERVER_NAME="${SERVER_NAME:-FileServer}"
ROOT_NAME="${ROOT_NAME:-codex-delete-action-$(date -u +%Y%m%d-%H%M%S)}"
ROOT_PATH="C:\\Corporativo\\${ROOT_NAME}"

echo "delete-action: criando e excluindo cenarios em ${ROOT_PATH}"

ps_script="$(mktemp)"
cat > "${ps_script}" <<PS
\$ErrorActionPreference = "Stop"
\$root = "${ROOT_PATH}"
Remove-Item \$root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path \$root | Out-Null

Set-Content -Path (Join-Path \$root "01-single-file.txt") -Value "single" -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path \$root "02-many-files") | Out-Null
Set-Content -Path (Join-Path \$root "02-many-files\\a.txt") -Value "a" -Encoding UTF8
Set-Content -Path (Join-Path \$root "02-many-files\\b.md") -Value "b" -Encoding UTF8
Set-Content -Path (Join-Path \$root "02-many-files\\c.yml") -Value "c" -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path \$root "03-empty-folder") | Out-Null

New-Item -ItemType Directory -Path (Join-Path \$root "04-folder-with-file") | Out-Null
Set-Content -Path (Join-Path \$root "04-folder-with-file\\inside.txt") -Value "inside" -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path \$root "05-tree\\sub-a") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path \$root "05-tree\\sub-b") -Force | Out-Null
Set-Content -Path (Join-Path \$root "05-tree\\root.docx") -Value "doc" -Encoding UTF8
Set-Content -Path (Join-Path \$root "05-tree\\sub-a\\child-a.txt") -Value "child a" -Encoding UTF8
Set-Content -Path (Join-Path \$root "05-tree\\sub-b\\child-b.xlsx") -Value "child b" -Encoding UTF8

Start-Sleep -Seconds 3

Remove-Item \$root -Recurse -Force

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
  "C:\\\\Corporativo\\\\${ROOT_NAME}",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\01-single-file.txt",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\a.txt",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\b.md",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files\\\\c.yml",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\02-many-files",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\03-empty-folder",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\04-folder-with-file",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\04-folder-with-file\\\\inside.txt",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\root.docx",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\sub-a",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\sub-a\\\\child-a.txt",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\sub-b",
  "C:\\\\Corporativo\\\\${ROOT_NAME}\\\\05-tree\\\\sub-b\\\\child-b.xlsx"
]
JSON

echo "delete-action: aguardando coleta do agente"
deadline=$((SECONDS + 180))
last_report=""
while (( SECONDS < deadline )); do
  events_json="$(mktemp)"
  curl -fsS "${API_BASE_URL}/api/events?server=${SERVER_NAME}&take=500" > "${events_json}"

  report="$(jq -n --arg root "${ROOT_NAME}" --slurpfile events "${events_json}" --slurpfile expected "${expected_json}" '
    def norm: ascii_downcase;
    ($events[0] // []) as $events
    | ($expected[0] // []) as $expected
    | [ $events[]
        | select((.path // "") | contains($root))
        | select(.action == "deleted")
        | .path
      ] | unique as $deleted
    | {
        deletedCount: ($deleted | length),
        expectedCount: ($expected | length),
        missing: ($expected | map(select((. as $p | $deleted | index($p)) | not))),
        deleted: $deleted
      }
  ')" 
  last_report="${report}"
  missing_count="$(jq '.missing | length' <<<"${report}")"
  if [[ "${missing_count}" == "0" ]]; then
    timeline_report="$(node scripts/validate-delete-action-timeline.mjs "${events_json}" "${expected_json}" "${ROOT_NAME}")"
    timeline_missing_count="$(jq '.missing | length' <<<"${timeline_report}")"
    timeline_duplicate_count="$(jq '.duplicates | length' <<<"${timeline_report}")"
    if [[ "${timeline_missing_count}" != "0" || "${timeline_duplicate_count}" != "0" ]]; then
      last_report="$(jq -n --argjson api "${report}" --argjson timeline "${timeline_report}" '{api:$api,timeline:$timeline}')"
      rm -f "${events_json}"
      sleep 10
      continue
    fi
    echo "${report}" | jq .
    echo "delete-action timeline:"
    echo "${timeline_report}" | jq .
    rm -f "${expected_json}"
    rm -f "${events_json}"
    echo "delete-action: OK"
    exit 0
  fi

  rm -f "${events_json}"
  sleep 10
done

echo "delete-action: FALHOU, eventos faltando:"
echo "${last_report}" | jq .
rm -f "${expected_json}"
exit 1
