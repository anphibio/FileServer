[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$ServerName,

    [string]$ApiKey,

    [string]$Identity = "Everyone",

    [string]$ConfigureScriptPath = ".\Configure-FileServerAudit.ps1"
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

function Get-AgentConfig {
    param(
        [string]$BaseUrl,
        [string]$Server,
        [hashtable]$Headers
    )

    $encodedServer = [System.Uri]::EscapeDataString($Server)
    $uri = $BaseUrl.TrimEnd("/") + "/api/agents/config?server=$encodedServer"

    return Invoke-RestMethod -Uri $uri -Headers $Headers -Method Get -TimeoutSec 30
}

if (-not (Test-Path -LiteralPath $ConfigureScriptPath)) {
    throw "Script de configuracao de auditoria nao encontrado: $ConfigureScriptPath"
}

$headers = New-ApiHeaders -Key $ApiKey
$config = Get-AgentConfig -BaseUrl $ApiBaseUrl -Server $ServerName -Headers $headers
$paths = @($config.monitoredPaths |
    Where-Object { $_.status -eq "active" } |
    Select-Object -ExpandProperty path -Unique)

if ($paths.Count -eq 0) {
    Write-Host "Nenhum caminho ativo retornado para o servidor $ServerName."
    return
}

Write-Host "Caminhos ativos retornados para ${ServerName}: $($paths.Count)"
$paths | ForEach-Object { Write-Host " - $_" }

if ($PSCmdlet.ShouldProcess($ServerName, "Sincronizar auditoria NTFS em $($paths.Count) caminho(s)")) {
    & $ConfigureScriptPath -Paths $paths -Identity $Identity -WhatIf:$WhatIfPreference
}
