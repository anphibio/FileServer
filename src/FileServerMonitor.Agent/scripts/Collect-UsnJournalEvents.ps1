[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Volume,

    [string]$BasePath,

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

    if ($Reason -match "RENAME_OLD_NAME|Renomear: nome antigo") {
        return "renamed_old"
    }

    if ($Reason -match "RENAME_NEW_NAME|Renomear: novo nome") {
        return "renamed_new"
    }

    if ($Reason -match "SECURITY_CHANGE|Alteração de segurança|Alteracao de seguranca") {
        return "permission_changed"
    }

    if ($Reason -match "DATA_OVERWRITE|DATA_EXTEND|DATA_TRUNCATION|BASIC_INFO_CHANGE|Extensao de dados|Extensão de dados|Fechar|Alteração de ID de objeto|Alteracao de ID de objeto") {
        return "modified"
    }

    return "changed"
}

function Get-Extension {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    return [System.IO.Path]::GetExtension($Path)
}

function Normalize-ReferenceId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    return ($Value -replace '\s+', '').Trim()
}

function Join-ResolvedPath {
    param(
        [string]$ParentPath,
        [string]$Name,
        [string]$FallbackBasePath
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return "$FallbackBasePath\"
    }

    if ([System.IO.Path]::IsPathRooted($Name)) {
        return $Name
    }

    if (-not [string]::IsNullOrWhiteSpace($ParentPath)) {
        return Join-Path $ParentPath $Name
    }

    return Join-Path $FallbackBasePath $Name
}

function Is-MoveTransition {
    param(
        [string]$PreviousPath,
        [string]$CurrentPath
    )

    if ([string]::IsNullOrWhiteSpace($PreviousPath) -or [string]::IsNullOrWhiteSpace($CurrentPath)) {
        return $false
    }

    $previousParent = [System.IO.Path]::GetDirectoryName($PreviousPath)
    $currentParent = [System.IO.Path]::GetDirectoryName($CurrentPath)

    return -not [string]::Equals($previousParent, $currentParent, [System.StringComparison]::OrdinalIgnoreCase)
}

function Normalize-Volume {
    param([string]$Value)

    if ($Value.EndsWith("\")) {
        return $Value.TrimEnd("\")
    }

    return $Value
}

$normalizedVolume = Normalize-Volume -Value $Volume
$normalizedBasePath = if ([string]::IsNullOrWhiteSpace($BasePath)) {
    $normalizedVolume
} else {
    Normalize-Volume -Value $BasePath
}

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
$parsedRecords = foreach ($record in $records) {
    try {
        $usnValue = 0L
        $fileName = $null
        $reason = $null
        $timestampText = $null
        $fileId = $null
        $parentFileId = $null

        foreach ($property in $record.PSObject.Properties) {
            switch -Regex ($property.Name) {
                "^USN$|^Usn$" { [long]::TryParse([string]$property.Value, [ref]$usnValue) | Out-Null }
                "File.*Name|Name|Nome.*arquivo" { if ($null -eq $fileName) { $fileName = [string]$property.Value } }
                "Reason|Motivo" { $reason = [string]$property.Value }
                "Time.*Stamp|Date.*Time|Carimbo.*data.*hora" { if ($null -eq $timestampText) { $timestampText = [string]$property.Value } }
                "File.*Reference|File.*ID|ID do arquivo" { if ($null -eq $fileId) { $fileId = [string]$property.Value } }
                "Parent.*Reference|Parent.*ID|ID do arquivo pai" { if ($null -eq $parentFileId) { $parentFileId = [string]$property.Value } }
            }
        }

        if ($usnValue -le $StartUsn) {
            continue
        }

        [pscustomobject]@{
            usn = $usnValue
            volume = $normalizedVolume
            timestampUtc = Parse-UsnTimestamp -Value $timestampText
            server = $ServerName
            share = $DefaultShare
            path = $null
            previousPath = $null
            objectType = "unknown"
            action = Convert-ReasonToAction -Reason $reason
            reason = $reason
            fileId = Normalize-ReferenceId -Value $fileId
            parentFileId = Normalize-ReferenceId -Value $parentFileId
            fileName = $fileName
            user = "UNKNOWN"
            sid = $null
            sourceHost = $null
            sourceIp = $null
            processName = "fsutil.exe"
            fileSizeBytes = $null
            extension = $null
            fileReferenceId = Normalize-ReferenceId -Value $fileId
            result = "success"
            severity = "info"
            source = "usn-journal"
        }
    } catch {
        continue
    }
}

$selectedRecords = @(
    $parsedRecords |
        Sort-Object { [long]$_.usn } -Descending |
        Select-Object -First $MaxEvents |
        Sort-Object { [long]$_.usn }
)

$currentPathByFileId = @{}
$pendingRenameOldPathByFileId = @{}
$hydratedRecords = foreach ($record in $selectedRecords) {
    $knownPath = if (-not [string]::IsNullOrWhiteSpace($record.fileId)) { $currentPathByFileId[$record.fileId] } else { $null }
    $parentPath = if (-not [string]::IsNullOrWhiteSpace($record.parentFileId)) { $currentPathByFileId[$record.parentFileId] } else { $null }
    $resolvedPath = Join-ResolvedPath -ParentPath $parentPath -Name $record.fileName -FallbackBasePath $normalizedBasePath
    $resolvedAction = $record.action
    $previousPath = $null

    if (($record.action -eq "changed" -or $record.action -eq "modified" -or $record.action -eq "created") `
        -and -not [string]::IsNullOrWhiteSpace($knownPath) `
        -and -not [string]::Equals($knownPath, $resolvedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        $previousPath = $knownPath
        $resolvedAction = if (Is-MoveTransition -PreviousPath $knownPath -CurrentPath $resolvedPath) { "moved" } else { $record.action }
    }

    if ($record.action -eq "renamed_old") {
        $pendingRenameOldPathByFileId[$record.fileId] = if (-not [string]::IsNullOrWhiteSpace($knownPath)) { $knownPath } else { $resolvedPath }
    }

    if ($record.action -eq "renamed_new") {
        if ($pendingRenameOldPathByFileId.ContainsKey($record.fileId)) {
            $previousPath = $pendingRenameOldPathByFileId[$record.fileId]
            $pendingRenameOldPathByFileId.Remove($record.fileId)
            $resolvedAction = if (Is-MoveTransition -PreviousPath $previousPath -CurrentPath $resolvedPath) { "moved" } else { "renamed" }
        } elseif (-not [string]::IsNullOrWhiteSpace($knownPath) `
            -and -not [string]::Equals($knownPath, $resolvedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            $previousPath = $knownPath
            $resolvedAction = if (Is-MoveTransition -PreviousPath $knownPath -CurrentPath $resolvedPath) { "moved" } else { "renamed" }
        }
    }

    if ($record.action -eq "deleted" -and -not [string]::IsNullOrWhiteSpace($knownPath)) {
        $resolvedPath = $knownPath
    }

    if (-not [string]::IsNullOrWhiteSpace($record.fileId)) {
        if ($record.action -eq "deleted") {
            $currentPathByFileId.Remove($record.fileId)
            $pendingRenameOldPathByFileId.Remove($record.fileId)
        } else {
            $currentPathByFileId[$record.fileId] = $resolvedPath
        }
    }

    $extension = Get-Extension -Path $resolvedPath
    $objectType = if ([string]::IsNullOrWhiteSpace($extension)) { "unknown" } else { "file" }

    [pscustomobject]@{
        cursorType = "usn"
        usn = $record.usn
        volume = $record.volume
        timestampUtc = $record.timestampUtc.ToString("o")
        server = $record.server
        share = $record.share
        path = $resolvedPath
        previousPath = $previousPath
        objectType = $objectType
        action = $resolvedAction
        user = $record.user
        sid = $record.sid
        sourceHost = $record.sourceHost
        sourceIp = $record.sourceIp
        processName = $record.processName
        fileSizeBytes = $record.fileSizeBytes
        extension = $extension
        fileReferenceId = $record.fileReferenceId
        result = $record.result
        severity = $record.severity
        source = $record.source
    }
}

$result = @($hydratedRecords)

if ($result.Count -eq 0) {
    @() | ConvertTo-Json -Depth 8
} else {
    $result | ConvertTo-Json -Depth 8
}
