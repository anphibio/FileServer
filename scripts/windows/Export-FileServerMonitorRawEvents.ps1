[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,

    [string]$ApiKey,

    [int]$Take = 200,

    [string]$OutputPath = ".\\logs\\raw-events-{timestamp}.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-ApiHeaders {
    param([string]$Key)

    $headers = @{}

    if (-not [string]::IsNullOrWhiteSpace($Key)) {
        $headers["X-Api-Key"] = $Key
    }

    return $headers
}

function Get-RawEvents {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [int]$Limit
    )

    $safeLimit = [Math]::Max(1, [Math]::Min($Limit, 500))
    $uri = $BaseUrl.TrimEnd("/") + "/api/events?take=$safeLimit"

    return Invoke-RestMethod -Uri $uri -Headers $Headers -Method Get -TimeoutSec 30
}

function Expand-EventResponse {
    param([object]$Response)

    if ($null -eq $Response) {
        return @()
    }

    if ($Response.PSObject.Properties.Name -contains "value" -and $Response.value -is [System.Collections.IEnumerable]) {
        return @($Response.value)
    }

    if ($Response -is [System.Collections.IEnumerable] -and -not ($Response -is [string])) {
        $expanded = @()

        foreach ($item in $Response) {
            if ($null -eq $item) {
                continue
            }

            if ($item.PSObject.Properties.Name -contains "value" -and $item.value -is [System.Collections.IEnumerable]) {
                $expanded += @($item.value)
                continue
            }

            $expanded += $item
        }

        return $expanded
    }

    return @($Response)
}

function Ensure-ParentDirectory {
    param([string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $parent = [System.IO.Path]::GetDirectoryName($fullPath)

    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    return $fullPath
}

function Resolve-OutputPathTemplate {
    param([string]$PathTemplate)

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    return $PathTemplate.Replace("{timestamp}", $timestamp)
}

function Write-Utf8JsonFile {
    param(
        [string]$Path,
        [object]$Data
    )

    $json = $Data | ConvertTo-Json -Depth 12
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json, $utf8NoBom)
}

$headers = New-ApiHeaders -Key $ApiKey
$rawResponse = Get-RawEvents -BaseUrl $ApiBaseUrl -Headers $headers -Limit $Take
$rawEvents = @(Expand-EventResponse -Response $rawResponse)
$resolvedOutputPath = Resolve-OutputPathTemplate -PathTemplate $OutputPath
$fullOutputPath = Ensure-ParentDirectory -Path $resolvedOutputPath

$payload = [ordered]@{
    exportedAtLocal = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    apiBaseUrl = $ApiBaseUrl.TrimEnd("/")
    takeRequested = $Take
    count = $rawEvents.Count
    rawResponse = $rawResponse
    events = $rawEvents
}

Write-Utf8JsonFile -Path $fullOutputPath -Data $payload

Write-Host "Eventos exportados: $($rawEvents.Count)"
Write-Host "Arquivo salvo em: $fullOutputPath"
