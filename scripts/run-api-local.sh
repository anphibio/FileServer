#!/usr/bin/env bash
set -euo pipefail

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/private/tmp}"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:8080}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export Monitor__StorageProvider="${Monitor__StorageProvider:-InMemory}"
export Auth__Enabled="${Auth__Enabled:-false}"

dotnet restore src/FileServerMonitor.Api/FileServerMonitor.Api.csproj --ignore-failed-sources /p:EnableSqlServer=false
dotnet build src/FileServerMonitor.Api/FileServerMonitor.Api.csproj --no-restore /p:EnableSqlServer=false
dotnet src/FileServerMonitor.Api/bin/Debug/net10.0/FileServerMonitor.Api.dll
