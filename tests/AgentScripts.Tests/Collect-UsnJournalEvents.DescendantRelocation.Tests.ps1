$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot ".." "..")
$scriptPath = Join-Path $repoRoot "src/FileServerMonitor.Agent/scripts/Collect-UsnJournalEvents.ps1"
$fixturePath = Join-Path $PSScriptRoot "usn-readjournal-descendant-relocation.csv"
$knownPaths = @{
    "00000000000000000004000000000000" = "C:\Corporativo\batch"
    "00000000000000000004000000000004" = "C:\Corporativo\batch\Arquivo"
    "00000000000000000004000000000001" = "C:\Corporativo\batch\Projeto"
    "00000000000000000004000000000002" = "C:\Corporativo\batch\Projeto\Nova pasta"
    "00000000000000000004000000000003" = "C:\Corporativo\batch\Projeto\Nova pasta\file.txt"
} | ConvertTo-Json -Compress

$json = & $scriptPath `
    -Volume "C:" `
    -BasePath "C:\Corporativo\batch" `
    -StartUsn 1000 `
    -MaxEvents 20 `
    -ServerName "FileServer" `
    -DefaultShare "Corporativo" `
    -RawCsvPath $fixturePath `
    -KnownPathByFileIdJson $knownPaths

$events = $json | ConvertFrom-Json
$usnEvents = @($events | Where-Object cursorType -eq "usn")
$folderRename = @($usnEvents | Where-Object action -eq "renamed" | Where-Object path -eq "C:\Corporativo\batch\Projeto\Financeiro 2026")
$projectTransition = @($usnEvents | Where-Object previousPath -eq "C:\Corporativo\batch\Projeto" | Where-Object path -eq "C:\Corporativo\batch\Arquivo\Projeto")
$modified = @($usnEvents | Where-Object action -eq "modified" | Where-Object fileReferenceId -eq "00000000000000000004000000000003")

if ($folderRename.Count -ne 1) {
    throw "Expected one renamed folder event for Financeiro 2026."
}

if ($projectTransition.Count -ne 1) {
    throw "Expected one explicit transition relocating Projeto into Arquivo."
}

if ($modified.Count -ne 1) {
    throw "Expected one modified child file event after folder rename and move."
}

if ($modified[0].path -ne "C:\Corporativo\batch\Arquivo\Projeto\Financeiro 2026\file.txt") {
    throw "Expected modified descendant path to follow the renamed and moved folder chain."
}

Write-Output "OK USN collector relocates known descendants after folder rename and move within the same batch."
