param(
    [string]$PackagePath = 'C:\Windows\Temp\FileServerMonitor.Agent-win-x64.zip',
    [string]$InstallRoot = 'C:\Program Files\FileServerMonitor\agent-dotnet',
    [string]$ServiceName = 'FileServerMonitorAgent'
)

$ErrorActionPreference = 'Stop'

$publishPath = Join-Path $InstallRoot 'publish'
$backupPath = Join-Path $InstallRoot 'publish.prev'

Write-Host "Stopping service $ServiceName"
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (Test-Path $backupPath) {
    Remove-Item $backupPath -Recurse -Force
}

if (Test-Path $publishPath) {
    Rename-Item $publishPath (Split-Path $backupPath -Leaf)
}

New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
Expand-Archive -Path $PackagePath -DestinationPath $publishPath -Force

$preserveNames = @(
    'appsettings.agent.json',
    'appsettings.json',
    'appsettings.Development.json',
    'state',
    'collectors'
)

foreach ($name in $preserveNames) {
    $source = Join-Path $backupPath $name
    $target = Join-Path $publishPath $name
    if (-not (Test-Path $source)) {
        continue
    }

    if (Test-Path $target) {
        Remove-Item $target -Recurse -Force
    }

    Copy-Item $source $target -Recurse -Force
}

Write-Host "Starting service $ServiceName"
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2

Get-Service -Name $ServiceName |
    Select-Object Name, Status
