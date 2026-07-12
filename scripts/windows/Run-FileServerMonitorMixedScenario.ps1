[CmdletBinding()]
param(
    [string]$RootPath = "C:\Corporativo",
    [int]$DelaySeconds = 2,
    [string]$ScenarioId = ("codex-mixed-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param(
        [int]$Number,
        [string]$Description
    )

    Write-Host ("[{0:00}] {1}" -f $Number, $Description) -ForegroundColor Cyan
}

function Wait-Step {
    param([int]$Seconds)

    if ($Seconds -gt 0) {
        Start-Sleep -Seconds $Seconds
    }
}

function New-TextFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$Content = "conteudo inicial"
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    Set-Content -LiteralPath $Path -Value $Content -Encoding UTF8
}

function Read-FileBytes {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $buffer = [byte[]]::new(64)
        [void]$stream.Read($buffer, 0, $buffer.Length)
    }
    finally {
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $RootPath)) {
    throw "Caminho raiz nao encontrado: '$RootPath'."
}

$scenarioRoot = Join-Path $RootPath $ScenarioId
$origem = Join-Path $scenarioRoot "Origem"
$destino = Join-Path $scenarioRoot "Destino"
$nested = Join-Path $origem "Subpasta"
$aclTarget = Join-Path $scenarioRoot "Acl"
$step = 1

Write-Host "Executando bateria mista em '$scenarioRoot'." -ForegroundColor Green
Write-Host ""

if (Test-Path -LiteralPath $scenarioRoot) {
    Remove-Item -LiteralPath $scenarioRoot -Recurse -Force
    Wait-Step -Seconds 2
}

Write-Step -Number $step -Description "Criar raiz do cenario"
New-Item -ItemType Directory -Path $scenarioRoot -Force | Out-Null
Wait-Step -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar estrutura de pastas"
New-Item -ItemType Directory -Path $origem -Force | Out-Null
New-Item -ItemType Directory -Path $destino -Force | Out-Null
New-Item -ItemType Directory -Path $nested -Force | Out-Null
New-Item -ItemType Directory -Path $aclTarget -Force | Out-Null
Wait-Step -Seconds $DelaySeconds
$step++

$fileA = Join-Path $origem "arquivo-a.txt"
$fileB = Join-Path $nested "arquivo-b.txt"
$fileC = Join-Path $scenarioRoot "arquivo-c.txt"

Write-Step -Number $step -Description "Criar arquivos iniciais"
New-TextFile -Path $fileA -Content "arquivo A"
New-TextFile -Path $fileB -Content "arquivo B"
New-TextFile -Path $fileC -Content "arquivo C"
Wait-Step -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Modificar arquivo-c.txt"
Add-Content -LiteralPath $fileC -Value "linha alterada"
Wait-Step -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Ler arquivo-a.txt"
Read-FileBytes -Path $fileA
Wait-Step -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Renomear arquivo-b.txt"
$fileBRenamed = Join-Path $nested "arquivo-b-renomeado.txt"
Rename-Item -LiteralPath $fileB -NewName (Split-Path -Leaf $fileBRenamed)
Wait-Step -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Mover arquivo-a.txt para Destino"
$fileAMoved = Join-Path $destino "arquivo-a.txt"
Move-Item -LiteralPath $fileA -Destination $fileAMoved -Force
Wait-Step -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Alterar permissao da pasta Acl"
icacls $aclTarget /grant "BUILTIN\Users:(OI)(CI)RX" | Out-Null
Wait-Step -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Renomear pasta Origem"
$origemRenamed = Join-Path $scenarioRoot "Origem-Renomeada"
Rename-Item -LiteralPath $origem -NewName (Split-Path -Leaf $origemRenamed)
$nestedRenamed = Join-Path $origemRenamed "Subpasta"
$fileBRenamed = Join-Path $nestedRenamed "arquivo-b-renomeado.txt"
Wait-Step -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Mover pasta Origem-Renomeada para Destino"
$origemMoved = Join-Path $destino "Origem-Renomeada"
Move-Item -LiteralPath $origemRenamed -Destination $origemMoved -Force
$nestedMoved = Join-Path $origemMoved "Subpasta"
$fileBRenamed = Join-Path $nestedMoved "arquivo-b-renomeado.txt"
Wait-Step -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Excluir arquivo-c.txt"
Remove-Item -LiteralPath $fileC -Force
Wait-Step -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Excluir arvore do cenario"
Remove-Item -LiteralPath $scenarioRoot -Recurse -Force
Wait-Step -Seconds $DelaySeconds
$step++

Write-Host ""
Write-Host "Bateria concluida. ScenarioId: $ScenarioId" -ForegroundColor Green
