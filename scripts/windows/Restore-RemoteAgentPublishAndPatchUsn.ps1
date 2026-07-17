param(
    [string]$InstallRoot = 'C:\Program Files\FileServerMonitor\agent-dotnet',
    [string]$PatchedUsnScriptPath = 'C:\Windows\Temp\Collect-UsnJournalEvents.ps1',
    [string]$ServiceName = 'FileServerMonitorAgent'
)

$ErrorActionPreference = 'Stop'

$publishPath = Join-Path $InstallRoot 'publish'
$backupPath = Join-Path $InstallRoot 'publish.prev'
$targetScriptPath = Join-Path $publishPath 'scripts\Collect-UsnJournalEvents.ps1'

Write-Host "Stopping service $ServiceName"
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (Test-Path $backupPath) {
    if (Test-Path $publishPath) {
        Remove-Item $publishPath -Recurse -Force
    }

    Rename-Item $backupPath (Split-Path $publishPath -Leaf)
}

if (-not (Test-Path $publishPath)) {
    throw "Publish path not found after restore: $publishPath"
}

if (-not (Test-Path $PatchedUsnScriptPath)) {
    throw "Patched USN script not found: $PatchedUsnScriptPath"
}

Copy-Item $PatchedUsnScriptPath $targetScriptPath -Force

Write-Host "Starting service $ServiceName"
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2

Get-Service -Name $ServiceName |
    Select-Object Name, Status
