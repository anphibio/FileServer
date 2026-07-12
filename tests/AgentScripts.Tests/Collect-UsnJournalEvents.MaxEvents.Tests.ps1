$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot ".." "..")
$scriptPath = Join-Path $repoRoot "src/FileServerMonitor.Agent/scripts/Collect-UsnJournalEvents.ps1"
$fixturePath = Join-Path $PSScriptRoot "usn-readjournal-maxevents.csv"
$knownPaths = @{
    "00000000000000000003000000000000" = "C:\Corporativo\batch"
} | ConvertTo-Json -Compress

$json = & $scriptPath `
    -Volume "C:" `
    -BasePath "C:\Corporativo\batch" `
    -StartUsn 1000 `
    -MaxEvents 10 `
    -ServerName "FileServer" `
    -DefaultShare "Corporativo" `
    -RawCsvPath $fixturePath `
    -KnownPathByFileIdJson $knownPaths

$events = $json | ConvertFrom-Json
$visibleEvents = @($events | Where-Object cursorType -eq "usn")
$checkpoint = @($events | Where-Object cursorType -eq "usn_checkpoint")

if ($visibleEvents.Count -ne 10) {
    throw "Expected script to emit exactly MaxEvents visible records."
}

if ($visibleEvents[-1].path -ne "C:\Corporativo\batch\file-10.txt") {
    throw "Expected the tenth emitted event to be file-10.txt."
}

if ($checkpoint.Count -ne 1 -or [long]$checkpoint[0].usn -ne 1080) {
    throw "Expected checkpoint to stop at the last emitted USN when MaxEvents truncates the cycle."
}

Write-Output "OK USN collector keeps checkpoint at the last emitted record when MaxEvents truncates the batch."
