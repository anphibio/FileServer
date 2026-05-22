[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Volume,

    [long]$StartUsn = 0,

    [int]$MaxEvents = 200,

    [string]$ServerName = $env:COMPUTERNAME,

    [string]$DefaultShare = "FileServer"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ptBrCulture = [System.Globalization.CultureInfo]::GetCultureInfo("pt-BR")
$enUsCulture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")

function Parse-UsnTimestamp {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return [DateTimeOffset]::UtcNow
    }

    $styles = [System.Globalization.DateTimeStyles]::AssumeLocal
    $timestamp = [DateTimeOffset]::UtcNow

    foreach ($culture in @([System.Globalization.CultureInfo]::InvariantCulture, $ptBrCulture, $enUsCulture, [System.Globalization.CultureInfo]::CurrentCulture)) {
        if ([DateTimeOffset]::TryParse($Value, $culture, $styles, [ref]$timestamp)) {
            return $timestamp.ToUniversalTime()
        }
    }

    return [DateTimeOffset]::UtcNow
}

function Convert-ReasonToAction {
    param([string]$Reason)

    if ([string]::IsNullOrWhiteSpace($Reason)) {
        return "changed"
    }

    if ($Reason -match "FILE_CREATE|NAMED_DATA_EXTEND|Criação de arquivo|Criacao de arquivo") {
        return "created"
    }

    if ($Reason -match "FILE_DELETE|Arquivo morto|Exclusão|Exclusao") {
        return "deleted"
    }

    if ($Reason -match "RENAME_OLD_NAME|RENAME_NEW_NAME|Renomear: nome antigo|Renomear: novo nome") {
        return "renamed"
    }

    if ($Reason -match "SECURITY_CHANGE|Alteração de segurança|Alteracao de seguranca") {
        return "permission_changed"
    }

    if ($Reason -match "DATA_OVERWRITE|DATA_EXTEND|DATA_TRUNCATION|BASIC_INFO_CHANGE|Extensao de dados|Extensão de dados|Fechar|Alteração de ID de objeto|Alteracao de ID de objeto") {
        return "modified"
    }

    return "changed"
}

function Test-RenameOldReason {
    param([string]$Reason)

    if ([string]::IsNullOrWhiteSpace($Reason)) {
        return $false
    }

    return $Reason -match "RENAME_OLD_NAME|Renomear: nome antigo"
}

function Test-RenameNewReason {
    param([string]$Reason)

    if ([string]::IsNullOrWhiteSpace($Reason)) {
        return $false
    }

    return $Reason -match "RENAME_NEW_NAME|Renomear: novo nome"
}

function Get-Extension {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    return [System.IO.Path]::GetExtension($Path)
}

function Normalize-Volume {
    param([string]$Value)

    if ($Value.EndsWith("\")) {
        return $Value.TrimEnd("\")
    }

    return $Value
}

$normalizedVolume = Normalize-Volume -Value $Volume

# fsutil usn readjournal e suportado no Windows Server 2022. A opcao csv existe em builds modernos
# e facilita uma coleta inicial sem P/Invoke. Uma etapa posterior pode substituir isso por leitura nativa.
$arguments = @("usn", "readjournal", $normalizedVolume, "startusn=$StartUsn", "csv")
$raw = & fsutil @arguments 2>&1

if ($LASTEXITCODE -ne 0) {
    throw "fsutil usn readjournal falhou para o volume '$normalizedVolume': $raw"
}

$lines = @($raw) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

if ($lines.Count -eq 0) {
    @() | ConvertTo-Json -Depth 8
    return
}

$headerIndex = -1

for ($index = 0; $index -lt $lines.Count; $index++) {
    $candidate = [string]$lines[$index]
    if ($candidate -match '^\s*Usn,' -or $candidate -match '^\s*USN,') {
        $headerIndex = $index
        break
    }
}

if ($headerIndex -lt 0) {
    @() | ConvertTo-Json -Depth 8
    return
}

$csvLines = $lines[$headerIndex..($lines.Count - 1)]
$records = $csvLines | ConvertFrom-Csv
$parsedRecords = foreach ($record in $records | Select-Object -First ([Math]::Max($MaxEvents * 4, $MaxEvents))) {
    try {
        $usnValue = 0L
        $fileName = $null
        $reason = $null
        $timestampText = $null
        $fileId = $null

        foreach ($property in $record.PSObject.Properties) {
            switch -Regex ($property.Name) {
                "^USN$|^Usn$" { [long]::TryParse([string]$property.Value, [ref]$usnValue) | Out-Null }
                "File.*Name|Name|Nome.*arquivo" { if ($null -eq $fileName) { $fileName = [string]$property.Value } }
                "Reason|Motivo" { $reason = [string]$property.Value }
                "Time.*Stamp|Date.*Time|Carimbo.*data.*hora" { if ($null -eq $timestampText) { $timestampText = [string]$property.Value } }
                "File.*Reference|File.*ID|ID do arquivo" { if ($null -eq $fileId) { $fileId = [string]$property.Value } }
            }
        }

        if ($usnValue -le $StartUsn) {
            continue
        }

        $path = if ([string]::IsNullOrWhiteSpace($fileName)) {
            "$normalizedVolume\"
        } else {
            Join-Path $normalizedVolume $fileName
        }

        [pscustomobject]@{
            usn = $usnValue
            volume = $normalizedVolume
            timestampUtc = Parse-UsnTimestamp -Value $timestampText
            server = $ServerName
            share = $DefaultShare
            path = $path
            previousPath = $null
            objectType = "unknown"
            action = Convert-ReasonToAction -Reason $reason
            reason = $reason
            fileId = $fileId
            user = "UNKNOWN"
            sid = $null
            sourceHost = $null
            sourceIp = $null
            processName = "fsutil.exe"
            fileSizeBytes = $null
            extension = Get-Extension -Path $path
            result = "success"
            severity = "info"
            source = "usn-journal"
        }
    } catch {
        continue
    }
}

$materializedRecords = @(
    $parsedRecords |
        Sort-Object { [long]$_.usn } |
        Select-Object -First $MaxEvents
)

$result = New-Object System.Collections.Generic.List[object]

for ($index = 0; $index -lt $materializedRecords.Count; $index++) {
    $record = $materializedRecords[$index]
    $isRenameOld = Test-RenameOldReason -Reason $record.reason

    if ($record.action -eq "renamed" -and $isRenameOld -and $index + 1 -lt $materializedRecords.Count) {
        $nextRecord = $materializedRecords[$index + 1]
        $isRenameNew = Test-RenameNewReason -Reason $nextRecord.reason

        if ($nextRecord.action -eq "renamed" -and $isRenameNew) {
            $result.Add([pscustomobject]@{
                cursorType = "usn"
                usn = $nextRecord.usn
                volume = $nextRecord.volume
                timestampUtc = $nextRecord.timestampUtc.ToString("o")
                server = $nextRecord.server
                share = $nextRecord.share
                path = $nextRecord.path
                previousPath = $record.path
                objectType = if ([string]::IsNullOrWhiteSpace($nextRecord.extension)) { "unknown" } else { "file" }
                action = "renamed"
                user = $nextRecord.user
                sid = $nextRecord.sid
                sourceHost = $nextRecord.sourceHost
                sourceIp = $nextRecord.sourceIp
                processName = $nextRecord.processName
                fileSizeBytes = $nextRecord.fileSizeBytes
                extension = $nextRecord.extension
                result = $nextRecord.result
                severity = $nextRecord.severity
                source = $nextRecord.source
            })
            $index++
            continue
        }
    }

    $isRenameNewOnly = $record.action -eq "renamed" -and (Test-RenameNewReason -Reason $record.reason)
    if ($isRenameNewOnly) {
        continue
    }

    $result.Add([pscustomobject]@{
        cursorType = "usn"
        usn = $record.usn
        volume = $record.volume
        timestampUtc = $record.timestampUtc.ToString("o")
        server = $record.server
        share = $record.share
        path = $record.path
        previousPath = $record.previousPath
        objectType = if ([string]::IsNullOrWhiteSpace($record.extension)) { "unknown" } else { "file" }
        action = $record.action
        user = $record.user
        sid = $record.sid
        sourceHost = $record.sourceHost
        sourceIp = $record.sourceIp
        processName = $record.processName
        fileSizeBytes = $record.fileSizeBytes
        extension = $record.extension
        result = $record.result
        severity = $record.severity
        source = $record.source
    })
}

if ($result.Count -eq 0) {
    @() | ConvertTo-Json -Depth 8
} else {
    $result | ConvertTo-Json -Depth 8
}
