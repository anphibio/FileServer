$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot ".." "..")
$scriptPath = Join-Path $repoRoot "src/FileServerMonitor.Agent/scripts/Collect-UsnJournalEvents.ps1"
$fixturePath = Join-Path $PSScriptRoot "usn-readjournal-gap.csv"
$knownPaths = @{
    "00000000000000000002000000000000" = "C:\Corporativo"
} | ConvertTo-Json -Compress

$json = & $scriptPath `
    -Volume "C:" `
    -BasePath "C:\Corporativo" `
    -StartUsn 1000 `
    -MaxEvents 10 `
    -ServerName "FileServer" `
    -DefaultShare "Corporativo" `
    -RawCsvPath $fixturePath `
    -KnownPathByFileIdJson $knownPaths

$events = $json | ConvertFrom-Json
$deletedPaths = @($events | Where-Object action -eq "deleted" | ForEach-Object path)

if ($deletedPaths -notcontains "C:\Corporativo\target-folder\target-file.txt") {
    throw "Expected deleted file event after unhydratable USN gap."
}

if ($deletedPaths -notcontains "C:\Corporativo\target-folder") {
    throw "Expected deleted folder event after unhydratable USN gap."
}

Write-Output "OK USN collector skips unhydratable gap and emits later monitored deletes."
