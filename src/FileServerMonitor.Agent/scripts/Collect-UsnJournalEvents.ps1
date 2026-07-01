[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Volume,

    [string]$BasePath,

    [long]$StartUsn = 0,

    [int]$MaxEvents = 200,

    [string]$ServerName = $env:COMPUTERNAME,

    [string]$DefaultShare = "FileServer",

    [string]$RawCsvPath,

    [string]$KnownPathByFileIdJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

try {
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = $utf8NoBom
    [Console]::OutputEncoding = $utf8NoBom
    $OutputEncoding = $utf8NoBom
} catch {
    # Older hosts can reject console encoding changes; collection still works for ASCII paths.
}

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

    if ($Reason -match "FILE_CREATE|NAMED_DATA_EXTEND|File create|Criação de arquivo|Criacao de arquivo") {
        return "created"
    }

    if ($Reason -match "FILE_DELETE|File delete|Arquivo morto|Exclusão|Exclusao") {
        return "deleted"
    }

    if ($Reason -match "RENAME_OLD_NAME|Rename: old name|Renomear: nome antigo") {
        return "renamed_old"
    }

    if ($Reason -match "RENAME_NEW_NAME|Rename: new name|Renomear: novo nome") {
        return "renamed_new"
    }

    if ($Reason -match "SECURITY_CHANGE|Security change|Alteração de segurança|Alteracao de seguranca") {
        return "permission_changed"
    }

    if ($Reason -match "DATA_OVERWRITE|DATA_EXTEND|DATA_TRUNCATION|BASIC_INFO_CHANGE|Data overwrite|Data extend|Data truncation|Basic info change|Close|Extensao de dados|Extensão de dados|Fechar|Alteração de ID de objeto|Alteracao de ID de objeto") {
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
        [string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return $null
    }

    if ([System.IO.Path]::IsPathRooted($Name)) {
        return $Name
    }

    if (-not [string]::IsNullOrWhiteSpace($ParentPath)) {
        return "$($ParentPath.TrimEnd('\', '/'))\$($Name.TrimStart('\', '/'))"
    }

    return $null
}

function Normalize-ResolvedPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $normalized = $Path.Trim()

    if ($normalized.StartsWith("\\?\")) {
        $normalized = $normalized.Substring(4)
    }

    return $normalized.TrimEnd('\', '/')
}

function Test-PathUnderBase {
    param(
        [string]$Path,
        [string]$BasePath
    )

    $normalizedPath = Normalize-ResolvedPath -Path $Path
    $normalizedBase = Normalize-ResolvedPath -Path $BasePath

    if ([string]::IsNullOrWhiteSpace($normalizedPath) -or [string]::IsNullOrWhiteSpace($normalizedBase)) {
        return $false
    }

    return [string]::Equals($normalizedPath, $normalizedBase, [System.StringComparison]::OrdinalIgnoreCase) `
        -or $normalizedPath.StartsWith("$normalizedBase\", [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-PathByFileId {
    param(
        [string]$Volume,
        [string]$FileId
    )

    if ([string]::IsNullOrWhiteSpace($FileId)) {
        return $null
    }

    try {
        $queryId = $FileId.Trim()

        if (-not $queryId.StartsWith("0x", [System.StringComparison]::OrdinalIgnoreCase)) {
            $queryId = "0x$queryId"
        }

        $output = & fsutil file queryFileNameById $Volume $queryId 2>$null

        foreach ($line in @($output)) {
            $text = [string]$line
            $match = [regex]::Match($text, '(\\\\\?\\[A-Za-z]:\\.*)$')

            if ($match.Success) {
                return Normalize-ResolvedPath -Path $match.Groups[1].Value
            }
        }
    } catch {
        return $null
    }

    return $null
}

function Resolve-FileIdByPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    try {
        $output = & fsutil file queryfileid $Path 2>$null

        foreach ($line in @($output)) {
            $text = [string]$line
            $match = [regex]::Match($text, '0x([0-9A-Fa-f]+)')

            if ($match.Success) {
                return Normalize-ReferenceId -Value $match.Groups[1].Value
            }
        }
    } catch {
        return $null
    }

    return $null
}

function Add-ResolvedPath {
    param(
        [hashtable]$Map,
        [string]$FileId,
        [string]$Path,
        [string]$BasePath
    )

    $normalizedPath = Normalize-ResolvedPath -Path $Path

    if ([string]::IsNullOrWhiteSpace($FileId) -or [string]::IsNullOrWhiteSpace($normalizedPath)) {
        return
    }

    if (Test-PathUnderBase -Path $normalizedPath -BasePath $BasePath) {
        $Map[$FileId] = $normalizedPath
    }
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

$raw = if ([string]::IsNullOrWhiteSpace($RawCsvPath)) {
    # fsutil usn readjournal e suportado no Windows Server 2022. A opcao csv existe em builds modernos
    # e facilita uma coleta inicial sem P/Invoke. Uma etapa posterior pode substituir isso por leitura nativa.
    $arguments = @("usn", "readjournal", $normalizedVolume, "startusn=$StartUsn", "csv")
    & fsutil @arguments 2>&1
} else {
    Get-Content -Path $RawCsvPath
}

if ([string]::IsNullOrWhiteSpace($RawCsvPath) -and $LASTEXITCODE -ne 0) {
    throw "fsutil usn readjournal falhou para o volume '$normalizedVolume': $raw"
}

$lines = @($raw) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

if ($lines.Count -eq 0) {
    Write-Output "[]"
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
    Write-Output "[]"
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
        $fileAttributes = $null

        foreach ($property in $record.PSObject.Properties) {
            switch -Regex ($property.Name) {
                "^USN$|^Usn$" { [long]::TryParse([string]$property.Value, [ref]$usnValue) | Out-Null }
                "File.*Name|Name|Nome.*arquivo" { if ($null -eq $fileName) { $fileName = [string]$property.Value } }
                "Reason|Motivo" { $reason = [string]$property.Value }
                "Time.*Stamp|Date.*Time|Carimbo.*data.*hora" { if ($null -eq $timestampText) { $timestampText = [string]$property.Value } }
                "File.*Reference|File.*ID|ID do arquivo" { if ($null -eq $fileId) { $fileId = [string]$property.Value } }
                "Parent.*Reference|Parent.*ID|ID do arquivo pai" { if ($null -eq $parentFileId) { $parentFileId = [string]$property.Value } }
                "File.*Attributes$|Atributos.*arquivo" { if ($null -eq $fileAttributes) { $fileAttributes = [string]$property.Value } }
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
            fileAttributes = $fileAttributes
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

$scanLimit = [Math]::Max($MaxEvents * 250, 5000)
$selectedRecords = @(
    $parsedRecords |
        Sort-Object { [long]$_.usn } |
        Select-Object -First $scanLimit
)

$currentPathByFileId = @{}
$pendingRenameOldPathByFileId = @{}

if (-not [string]::IsNullOrWhiteSpace($KnownPathByFileIdJson)) {
    try {
        $knownPaths = $KnownPathByFileIdJson | ConvertFrom-Json
        foreach ($property in $knownPaths.PSObject.Properties) {
            Add-ResolvedPath -Map $currentPathByFileId -FileId $property.Name -Path ([string]$property.Value) -BasePath $normalizedBasePath
        }
    } catch {
        throw "KnownPathByFileIdJson invalido: $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($RawCsvPath)) {
    $baseFileId = Resolve-FileIdByPath -Path $normalizedBasePath
    Add-ResolvedPath -Map $currentPathByFileId -FileId $baseFileId -Path $normalizedBasePath -BasePath $normalizedBasePath
}

$hydratedRecords = foreach ($record in $selectedRecords) {
    $knownPath = if (-not [string]::IsNullOrWhiteSpace($record.fileId)) { $currentPathByFileId[$record.fileId] } else { $null }
    $parentPath = if (-not [string]::IsNullOrWhiteSpace($record.parentFileId)) { $currentPathByFileId[$record.parentFileId] } else { $null }
    $resolvedPath = Join-ResolvedPath -ParentPath $parentPath -Name $record.fileName

    if ([string]::IsNullOrWhiteSpace($resolvedPath) -and $record.action -ne "renamed_old") {
        $resolvedPath = $knownPath
    }

    $resolvedPath = Normalize-ResolvedPath -Path $resolvedPath

    if (-not (Test-PathUnderBase -Path $resolvedPath -BasePath $normalizedBasePath)) {
        continue
    }

    $resolvedAction = $record.action
    $previousPath = $null

    if ($record.action -eq "renamed_old") {
        $pendingRenameOldPathByFileId[$record.fileId] = $resolvedPath
        continue
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

        if ([string]::IsNullOrWhiteSpace($previousPath)) {
            continue
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
    $objectType = if ($record.fileAttributes -match "Directory|Diretório|Diretorio") {
        "folder"
    } elseif ([string]::IsNullOrWhiteSpace($extension)) {
        "unknown"
    } else {
        "file"
    }

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

$result = @($hydratedRecords | Select-Object -First $MaxEvents)

if ($result.Count -eq 0) {
    Write-Output "[]"
} else {
    $result | ConvertTo-Json -Depth 8
}
