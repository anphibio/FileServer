#!/usr/bin/env bash
set -euo pipefail

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/private/tmp}"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

dotnet run --project tests/FileServerMonitor.Core.Tests/FileServerMonitor.Core.Tests.csproj
