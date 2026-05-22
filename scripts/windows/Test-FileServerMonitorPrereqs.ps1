[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,

    [string]$ApiKey,

    [string[]]$Volumes = @("D:")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

function Write-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail = ""
    )

    $status = if ($Passed) { "OK" } else { "FALHA" }
    $color = if ($Passed) { "Green" } else { "Red" }

    Write-Host "[$status] $Name" -ForegroundColor $color

    if (-not [string]::IsNullOrWhiteSpace($Detail)) {
        Write-Host "      $Detail"
    }
}

function Invoke-HealthCheck {
    param(
        [string]$BaseUrl,
        [string]$Key
    )

    try {
        $headers = @{}

        if (-not [string]::IsNullOrWhiteSpace($Key)) {
            $headers["X-Api-Key"] = $Key
        }

        $uri = $BaseUrl.TrimEnd("/") + "/health"
        $result = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -TimeoutSec 10
        Write-Check -Name "API /health" -Passed $true -Detail "Status: $($result.status); Storage: $($result.storageProvider)"
    } catch {
        Write-Check -Name "API /health" -Passed $false -Detail $_.Exception.Message
    }
}

function Test-SecurityLog {
    try {
        $event = Get-WinEvent -LogName Security -MaxEvents 1 -ErrorAction Stop
        Write-Check -Name "Acesso ao Security Log" -Passed ($null -ne $event) -Detail "Ultimo RecordId: $($event.RecordId)"
    } catch {
        Write-Check -Name "Acesso ao Security Log" -Passed $false -Detail $_.Exception.Message
    }
}

function Test-AuditPolicy {
    try {
        $result = auditpol.exe /get /subcategory:"File System" 2>&1
        $text = ($result | Out-String).Trim()
        $passed = $text -match "Success|Failure|Sucesso|Falha"
        Write-Check -Name "Politica de auditoria File System" -Passed $passed -Detail $text
    } catch {
        Write-Check -Name "Politica de auditoria File System" -Passed $false -Detail $_.Exception.Message
    }
}

function Test-Fsutil {
    try {
        $fsutil = Get-Command fsutil.exe -ErrorAction Stop
        Write-Check -Name "fsutil disponivel" -Passed $true -Detail $fsutil.Source
    } catch {
        Write-Check -Name "fsutil disponivel" -Passed $false -Detail $_.Exception.Message
    }
}

function Test-Volumes {
    param([string[]]$VolumeList)

    foreach ($volume in $VolumeList) {
        try {
            $driveName = $volume.TrimEnd(":")
            $drive = Get-Volume -DriveLetter $driveName -ErrorAction Stop
            $passed = $drive.FileSystem -eq "NTFS"
            Write-Check -Name "Volume $volume NTFS" -Passed $passed -Detail "FileSystem: $($drive.FileSystem); Size: $([Math]::Round($drive.Size / 1GB, 2)) GB"
        } catch {
            Write-Check -Name "Volume $volume NTFS" -Passed $false -Detail $_.Exception.Message
        }
    }
}

function Test-UsnJournal {
    param([string[]]$VolumeList)

    foreach ($volume in $VolumeList) {
        try {
            $result = fsutil usn queryjournal $volume 2>&1
            $passed = $LASTEXITCODE -eq 0
            Write-Check -Name "USN Journal $volume" -Passed $passed -Detail (($result | Select-Object -First 5) -join " ")
        } catch {
            Write-Check -Name "USN Journal $volume" -Passed $false -Detail $_.Exception.Message
        }
    }
}

Write-Host "File Server Monitor - Diagnostico de Pre-Requisitos" -ForegroundColor Cyan
Write-Host ""

Invoke-HealthCheck -BaseUrl $ApiBaseUrl -Key $ApiKey
Test-SecurityLog
Test-AuditPolicy
Test-Fsutil
Test-Volumes -VolumeList $Volumes
Test-UsnJournal -VolumeList $Volumes
