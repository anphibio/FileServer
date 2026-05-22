#!/usr/bin/env bash
set -euo pipefail

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/private/tmp}"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

. "$ROOT_DIR/scripts/lib-compose.sh"

warn_count=0

section() {
  printf '\n== %s ==\n' "$1"
}

warn() {
  warn_count=$((warn_count + 1))
  printf 'WARN %s\n' "$1"
}

section "Docker Compose"
compose_cmd config >/tmp/fileserver-monitor-compose-dev.txt
compose_cmd --env-file .env.preview.example -f docker-compose.preview.yml config >/tmp/fileserver-monitor-compose-preview.txt
compose_cmd --env-file .env.production.example -f docker-compose.prod.yml config >/tmp/fileserver-monitor-compose-prod.txt
printf 'OK compose de desenvolvimento\n'
printf 'OK compose de preview\n'
printf 'OK compose de producao\n'

section "Core Tests"
./scripts/run-tests.sh

section "Agent Build"
dotnet build src/FileServerMonitor.Agent/FileServerMonitor.Agent.csproj --no-restore

section "PowerShell Syntax"
if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -Command '
    $files = Get-ChildItem scripts/windows/*.ps1
    foreach ($file in $files) {
      $errors = $null
      [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$null, [ref]$errors) | Out-Null
      if ($errors) {
        Write-Error $file.FullName
        $errors | ForEach-Object { Write-Error $_.Message }
        exit 1
      }
    }
  '
  printf 'OK scripts PowerShell\n'
else
  warn "pwsh nao encontrado; sintaxe PowerShell nao validada."
fi

section "API Build"
dotnet restore src/FileServerMonitor.Api/FileServerMonitor.Api.csproj --ignore-failed-sources /p:EnableSqlServer=false
dotnet build src/FileServerMonitor.Api/FileServerMonitor.Api.csproj --no-restore /p:EnableSqlServer=false
printf 'OK API sem SQL Server\n'

if [ "${VALIDATE_SQLSERVER:-false}" = "true" ]; then
  section "API Build SQL Server"
  dotnet restore src/FileServerMonitor.Api/FileServerMonitor.Api.csproj /p:EnableSqlServer=true
  dotnet build src/FileServerMonitor.Api/FileServerMonitor.Api.csproj --no-restore /p:EnableSqlServer=true
  printf 'OK API com SQL Server\n'
else
  warn "Build SQL Server nao executado. Use VALIDATE_SQLSERVER=true em ambiente com acesso ao NuGet."
fi

section "Resumo"
if [ "$warn_count" -eq 0 ]; then
  printf 'Validacao concluida sem avisos.\n'
else
  printf 'Validacao concluida com %s aviso(s).\n' "$warn_count"
fi
