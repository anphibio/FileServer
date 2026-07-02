#!/usr/bin/env bash
set -euo pipefail

API_BASE_URL="${API_BASE_URL:-http://localhost:8180}"
SSH_TARGET="${SSH_TARGET:-Administrator@192.168.2.170}"
SERVER_NAME="${SERVER_NAME:-FileServer}"
ROOT_NAME="${ROOT_NAME:-codex-load-action-$(date -u +%Y%m%d-%H%M%S)}"
ROOT_PATH="C:\\Corporativo\\${ROOT_NAME}"
WORKERS="${WORKERS:-6}"
ITERATIONS="${ITERATIONS:-8}"
TAKE="${TAKE:-5000}"

echo "load-action: cenário ${ROOT_PATH}, workers=${WORKERS}, iterations=${ITERATIONS}"

ps_script="$(mktemp)"
cat > "${ps_script}" <<PS
\$ErrorActionPreference = "Stop"
\$root = "${ROOT_PATH}"
\$workers = ${WORKERS}
\$iterations = ${ITERATIONS}
Remove-Item \$root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path \$root | Out-Null

for (\$worker = 1; \$worker -le \$workers; \$worker++) {
  \$workerRoot = Join-Path \$root ("worker-" + ("{0:D2}" -f \$worker))
  New-Item -ItemType Directory -Path \$workerRoot | Out-Null
  New-Item -ItemType Directory -Path (Join-Path \$workerRoot "move-dest") | Out-Null
  New-Item -ItemType Directory -Path (Join-Path \$workerRoot "delete-tree\\sub") -Force | Out-Null
  Set-Content -Path (Join-Path \$workerRoot "delete-tree\\delete-root.txt") -Value "delete root" -Encoding UTF8
  Set-Content -Path (Join-Path \$workerRoot "delete-tree\\sub\\delete-child.txt") -Value "delete child" -Encoding UTF8
  for (\$i = 1; \$i -le \$iterations; \$i++) {
    \$suffix = \$i.ToString("00")
    Set-Content -Path (Join-Path \$workerRoot ("access-" + \$suffix + ".txt")) -Value "access" -Encoding UTF8
  }
}

Start-Sleep -Seconds 8

\$jobs = for (\$worker = 1; \$worker -le \$workers; \$worker++) {
  Start-Job -ScriptBlock {
    param(\$root, \$worker, \$iterations)
    \$workerRoot = Join-Path \$root ("worker-" + ("{0:D2}" -f \$worker))
    for (\$i = 1; \$i -le \$iterations; \$i++) {
      \$suffix = \$i.ToString("00")
      \$createdPath = Join-Path \$workerRoot ("created-" + \$suffix + ".txt")
      Set-Content -Path \$createdPath -Value ("created " + \$worker + "/" + \$i) -Encoding UTF8

      \$renameOld = Join-Path \$workerRoot ("rename-" + \$suffix + "-old.txt")
      \$renameNew = Join-Path \$workerRoot ("rename-" + \$suffix + "-new.txt")
      Set-Content -Path \$renameOld -Value "rename" -Encoding UTF8
      Start-Sleep -Milliseconds 250
      Rename-Item -LiteralPath \$renameOld -NewName ("rename-" + \$suffix + "-new.txt")
      Get-Item -LiteralPath \$renameNew | Out-Null

      \$moveSource = Join-Path \$workerRoot ("move-" + \$suffix + ".txt")
      \$moveTarget = Join-Path \$workerRoot ("move-dest\\move-" + \$suffix + ".txt")
      Set-Content -Path \$moveSource -Value "move" -Encoding UTF8
      Start-Sleep -Milliseconds 250
      Move-Item -LiteralPath \$moveSource -Destination \$moveTarget
      Get-Item -LiteralPath \$moveTarget | Out-Null
    }

    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath (Join-Path \$workerRoot "delete-tree") -Recurse -Force
  } -ArgumentList \$root, \$worker, \$iterations
}

Receive-Job -Job \$jobs -Wait -AutoRemoveJob

for (\$worker = 1; \$worker -le \$workers; \$worker++) {
  \$workerRoot = Join-Path \$root ("worker-" + ("{0:D2}" -f \$worker))
  for (\$i = 1; \$i -le \$iterations; \$i++) {
    \$suffix = \$i.ToString("00")
    \$accessPath = Join-Path \$workerRoot ("access-" + \$suffix + ".txt")
    \$stream = [System.IO.File]::Open(\$accessPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
      \$buffer = New-Object byte[] 64
      [void]\$stream.Read(\$buffer, 0, \$buffer.Length)
    } finally {
      \$stream.Dispose()
    }
    Start-Sleep -Milliseconds 150
  }
}

Write-Output \$root
PS

ssh "${SSH_TARGET}" "powershell -NoProfile -ExecutionPolicy Bypass -Command -" < "${ps_script}" >/dev/null
rm -f "${ps_script}"

expected_json="$(mktemp)"
python3 - <<PY > "${expected_json}"
import json
root = r"C:\\Corporativo\\${ROOT_NAME}"
workers = int("${WORKERS}")
iterations = int("${ITERATIONS}")
expected = {
    "created": [root],
    "deleted": [],
    "accessed": [],
    "renamed": [],
    "moved": [],
}
for worker in range(1, workers + 1):
    worker_root = f"{root}\\\\worker-{worker:02d}"
    expected["created"].extend([
        worker_root,
        f"{worker_root}\\\\move-dest",
        f"{worker_root}\\\\delete-tree",
        f"{worker_root}\\\\delete-tree\\\\sub",
        f"{worker_root}\\\\delete-tree\\\\delete-root.txt",
        f"{worker_root}\\\\delete-tree\\\\sub\\\\delete-child.txt",
    ])
    expected["deleted"].extend([
        f"{worker_root}\\\\delete-tree",
        f"{worker_root}\\\\delete-tree\\\\sub",
        f"{worker_root}\\\\delete-tree\\\\delete-root.txt",
        f"{worker_root}\\\\delete-tree\\\\sub\\\\delete-child.txt",
    ])
    for i in range(1, iterations + 1):
        suffix = f"{i:02d}"
        expected["created"].extend([
            f"{worker_root}\\\\created-{suffix}.txt",
            f"{worker_root}\\\\access-{suffix}.txt",
            f"{worker_root}\\\\rename-{suffix}-old.txt",
            f"{worker_root}\\\\move-{suffix}.txt",
        ])
        expected["accessed"].append(f"{worker_root}\\\\access-{suffix}.txt")
        expected["renamed"].append({
            "from": f"{worker_root}\\\\rename-{suffix}-old.txt",
            "to": f"{worker_root}\\\\rename-{suffix}-new.txt",
        })
        expected["moved"].append({
            "from": f"{worker_root}\\\\move-{suffix}.txt",
            "to": f"{worker_root}\\\\move-dest\\\\move-{suffix}.txt",
        })
print(json.dumps(expected, indent=2))
PY

echo "load-action: aguardando coleta do agente"
deadline=$((SECONDS + 300))
last_report=""
while (( SECONDS < deadline )); do
  events_json="$(mktemp)"
  curl -fsS "${API_BASE_URL}/api/events?server=${SERVER_NAME}&take=${TAKE}" > "${events_json}"
  report="$(node scripts/validate-load-action-timeline.mjs "${events_json}" "${expected_json}" "${ROOT_NAME}")"
  last_report="${report}"
  missing_count="$(jq '[.summary[] | .missing] | add' <<<"${report}")"

  if [[ "${missing_count}" == "0" ]]; then
    echo "load-action timeline:"
    echo "${report}" | jq .
    ssh "${SSH_TARGET}" "powershell -NoProfile -Command \"Remove-Item -LiteralPath '${ROOT_PATH}' -Recurse -Force -ErrorAction SilentlyContinue\"" >/dev/null || true
    rm -f "${expected_json}"
    rm -f "${events_json}"
    echo "load-action: OK"
    exit 0
  fi

  rm -f "${events_json}"
  sleep 10
done

echo "load-action: relatório final com pendências:"
echo "${last_report}" | jq .
rm -f "${expected_json}"
exit 1
