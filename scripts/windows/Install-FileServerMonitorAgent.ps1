[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$AgentDirectory,

    [string]$ServiceName = "FileServerMonitorAgent",

    [string]$DisplayName = "File Server Monitor Agent",

    [string]$Description = "Coleta eventos de auditoria do servidor de arquivos e envia para a API File Server Monitor."
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$agentExe = Join-Path $AgentDirectory "FileServerMonitor.Agent.exe"
$configPath = Join-Path $AgentDirectory "appsettings.agent.json"

if (-not (Test-Path -LiteralPath $agentExe)) {
    throw "Executavel do agente nao encontrado em '$agentExe'."
}

if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Configuracao do agente nao encontrada em '$configPath'."
}

$binaryPath = "`"$agentExe`" `"$configPath`""

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    if ($PSCmdlet.ShouldProcess($ServiceName, "Atualizar servico existente")) {
        Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
        sc.exe config $ServiceName binPath= $binaryPath start= delayed-auto | Out-Null
        sc.exe description $ServiceName $Description | Out-Null
        Start-Service -Name $ServiceName
    }
} else {
    if ($PSCmdlet.ShouldProcess($ServiceName, "Criar servico Windows")) {
        New-Service `
            -Name $ServiceName `
            -DisplayName $DisplayName `
            -BinaryPathName $binaryPath `
            -StartupType Automatic `
            -Description $Description

        sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
        Start-Service -Name $ServiceName
    }
}
