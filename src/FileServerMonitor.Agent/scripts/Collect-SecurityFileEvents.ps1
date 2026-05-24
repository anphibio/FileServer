[CmdletBinding()]
param(
    [long]$LastRecordId = 0,
    [int]$MaxEvents = 200,
    [string[]]$EventIds = @("4663", "4660", "4670"),
    [string]$ServerName = $env:COMPUTERNAME,
    [string]$DefaultShare = "FileServer"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$normalizedEventIds = @(
    $EventIds |
        ForEach-Object { [string]$_ -split "," } |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [int]$_ }
)

function Get-EventDataMap {
    param([xml]$EventXml)

    $map = @{}
    $dataNodes = $EventXml.Event.EventData.Data

    if ($null -eq $dataNodes) {
        return $map
    }

    foreach ($item in $dataNodes) {
        if ($null -eq $item) {
            continue
        }

        $name = $item.Name
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $value = $null

            if ($item.PSObject.Properties.Name -contains '#text') {
                $value = [string]$item.'#text'
            } else {
                $value = [string]$item.InnerText
            }

            $map[$name] = $value
        }
    }

    return $map
}

function Get-ActionName {
    param(
        [int]$EventId,
        [hashtable]$Data
    )

    if ($EventId -eq 4660) {
        return "deleted"
    }

    if ($EventId -eq 4670) {
        return "permission_changed"
    }

    $accessMask = Get-AccessMaskValue -Value $Data["AccessMask"]
    $accessCodes = Get-AccessCodes -Value $Data["AccessList"]
    $objectType = Get-ObjectType -Data $Data

    if (($accessMask -band 0x00040000) -ne 0 -or $accessCodes -contains 1539) {
        return "permission_changed"
    }

    if (($accessMask -band 0x00080000) -ne 0 -or $accessCodes -contains 1540) {
        return "owner_changed"
    }

    if (($accessMask -band 0x00010000) -ne 0 -or ($accessMask -band 0x00000040) -ne 0 -or $accessCodes -contains 1537 -or $accessCodes -contains 4422) {
        return "deleted"
    }

    if (($accessMask -band 0x00000002) -ne 0 -or ($accessMask -band 0x00000004) -ne 0 -or $accessCodes -contains 4417 -or $accessCodes -contains 4418) {
        if ($objectType -eq "directory") {
            return "created"
        }

        return "created_or_appended"
    }

    if (($accessMask -band 0x00000010) -ne 0 -or ($accessMask -band 0x00000100) -ne 0 -or $accessCodes -contains 4420 -or $accessCodes -contains 4424) {
        return "modified"
    }

    if (($accessMask -band 0x00000001) -ne 0 -or ($accessMask -band 0x00000008) -ne 0 -or ($accessMask -band 0x00000020) -ne 0 -or ($accessMask -band 0x00000080) -ne 0) {
        return "accessed"
    }

    return "accessed"
}

function Get-ObjectType {
    param([hashtable]$Data)

    $objectType = $Data["ObjectType"]

    if ([string]::IsNullOrWhiteSpace($objectType)) {
        return "unknown"
    }

    if ($objectType -match "Directory|File") {
        return $objectType.ToLowerInvariant()
    }

    return "file"
}

function Get-AccessMaskValue {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return 0
    }

    try {
        if ($Value.StartsWith("0x")) {
            return [Convert]::ToInt32($Value.Substring(2), 16)
        }

        return [int]$Value
    } catch {
        return 0
    }
}

function Get-AccessCodes {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return @()
    }

    $matches = [regex]::Matches($Value, "%%(\d+)")
    $codes = @()

    foreach ($match in $matches) {
        $codes += [int]$match.Groups[1].Value
    }

    return $codes
}

function Get-Extension {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    return [System.IO.Path]::GetExtension($Path)
}

function Get-TimestampUtc {
    param($Event)

    if ($null -ne $Event.TimeCreated) {
        return $Event.TimeCreated.ToUniversalTime().ToString("o")
    }

    return [DateTimeOffset]::UtcNow.ToString("o")
}

function Resolve-EventPath {
    param([hashtable]$Data)

    $basePath = $Data["ObjectName"]
    $relativeName = $null

    foreach ($field in @("RelativeTargetName", "TargetFilename", "FileName", "FilePath", "Name")) {
        if (-not [string]::IsNullOrWhiteSpace($Data[$field])) {
            $relativeName = $Data[$field]
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($relativeName)) {
        return $basePath
    }

    if ([string]::IsNullOrWhiteSpace($basePath)) {
        return $relativeName
    }

    $normalizedBase = $basePath.TrimEnd('\', '/')
    $normalizedRelative = $relativeName.TrimStart('\', '/')

    if ($normalizedBase.EndsWith($normalizedRelative, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $normalizedBase
    }

    return "$normalizedBase\$normalizedRelative"
}

$filter = @{
    LogName = "Security"
    Id = $normalizedEventIds
}

try {
    $events = Get-WinEvent -FilterHashtable $filter -MaxEvents ([Math]::Max($MaxEvents * 4, $MaxEvents)) |
        Where-Object { $_.RecordId -gt $LastRecordId } |
        Sort-Object RecordId -Descending |
        Select-Object -First $MaxEvents
} catch [System.Diagnostics.Eventing.Reader.EventLogNotFoundException] {
    $events = @()
} catch {
    if ($_.FullyQualifiedErrorId -eq "NoMatchingEventsFound,Microsoft.PowerShell.Commands.GetWinEventCommand") {
        $events = @()
    } else {
        throw
    }
}

$events = @($events) | Sort-Object RecordId

$result = foreach ($event in $events) {
    try {
        if ($null -eq $event) {
            continue
        }

        [xml]$xml = $event.ToXml()
        $data = Get-EventDataMap -EventXml $xml
        $path = Resolve-EventPath -Data $data
        $domain = $data["SubjectDomainName"]
        $userName = $data["SubjectUserName"]
        $user = if ([string]::IsNullOrWhiteSpace($domain)) { $userName } else { "$domain\$userName" }

        [pscustomobject]@{
            cursorType = "security"
            recordId = [long]$event.RecordId
            usn = $null
            volume = $null
            timestampUtc = Get-TimestampUtc -Event $event
            server = $ServerName
            share = $DefaultShare
            path = $path
            previousPath = $null
            objectType = Get-ObjectType -Data $data
            action = Get-ActionName -EventId $event.Id -Data $data
            user = $user
            sid = $data["SubjectUserSid"]
            sourceHost = $null
            sourceIp = $null
            processName = $data["ProcessName"]
            fileSizeBytes = $null
            extension = Get-Extension -Path $path
            result = "success"
            severity = "info"
            source = "windows-security-log"
        }
    } catch {
        continue
    }
}

if ($null -eq $result) {
    @() | ConvertTo-Json -Depth 8
} else {
    @($result) | ConvertTo-Json -Depth 8
}
