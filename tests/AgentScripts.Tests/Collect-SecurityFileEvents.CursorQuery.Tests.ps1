$ErrorActionPreference = "Stop"

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$collectorPath = Join-Path $repositoryRoot "src\FileServerMonitor.Agent\scripts\Collect-SecurityFileEvents.ps1"

$query = & $collectorPath `
    -LastRecordId 123456 `
    -MaxEvents 250 `
    -EventIds 4663,4660,4670 `
    -BuildQueryOnly |
    ConvertFrom-Json

if ($query.logName -ne "Security") {
    throw "Security collector should query the Security log."
}

if (-not $query.oldest) {
    throw "Security collector must read matching records from oldest to newest."
}

if ($query.maxEvents -ne 250) {
    throw "Security collector must honor MaxEvents as the page size."
}

if ($query.filterXPath -notmatch "EventRecordID > 123456") {
    throw "Security collector query must advance strictly after LastRecordId."
}

foreach ($eventId in 4663,4660,4670) {
    if ($query.filterXPath -notmatch "EventID=$eventId") {
        throw "Security collector query is missing EventID $eventId."
    }
}

Write-Output "OK Security collector pages forward from LastRecordId without skipping older unread events."
