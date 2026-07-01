[CmdletBinding()]
param(
    [string]$RootPath = "E:\Corporativo",

    [int]$DelaySeconds = 1
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

function Invoke-StepDelay {
    param([int]$Seconds)

    if ($Seconds -gt 0) {
        Start-Sleep -Seconds $Seconds
    }
}

function Get-NextAvailablePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,

        [Parameter(Mandatory = $true)]
        [string]$BaseName,

        [Parameter(Mandatory = $true)]
        [string]$Extension
    )

    $candidate = Join-Path $Directory ($BaseName + $Extension)

    if (-not (Test-Path -LiteralPath $candidate)) {
        return $candidate
    }

    $index = 2

    while ($true) {
        $candidate = Join-Path $Directory ("{0} ({1}){2}" -f $BaseName, $index, $Extension)

        if (-not (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }

        $index++
    }
}

function New-EmptyFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $directory = Split-Path -Parent $Path

    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    New-Item -ItemType File -Path $Path -Force | Out-Null
    return $Path
}

function New-LocalizedDefaultFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,

        [Parameter(Mandatory = $true)]
        [string[]]$BaseNames,

        [Parameter(Mandatory = $true)]
        [string]$Extension
    )

    foreach ($baseName in $BaseNames) {
        $candidate = Get-NextAvailablePath -Directory $Directory -BaseName $baseName -Extension $Extension

        if (-not (Test-Path -LiteralPath $candidate)) {
            return (New-EmptyFile -Path $candidate)
        }
    }

    throw "Nao foi possivel gerar um nome padrao para extensao '$Extension'."
}

if (-not (Test-Path -LiteralPath $RootPath)) {
    throw "Caminho raiz nao encontrado: '$RootPath'."
}

$rootItemsToDelete = [System.Collections.Generic.List[string]]::new()
$step = 1
$rhPath = Join-Path $RootPath "RH"

$defaultNames = @{
    ".txt"  = @("Novo Documento de Texto", "New Text Document")
    ".bmp"  = @("Nova Imagem de Bitmap", "New Bitmap Image")
    ".pptx" = @("Novo(a) Apresentacao do Microsoft PowerPoint", "Novo(a) Apresentação do Microsoft PowerPoint", "New Microsoft PowerPoint Presentation")
    ".xlsx" = @("Novo(a) Planilha do Microsoft Excel", "New Microsoft Excel Worksheet")
}

Write-Host "Executando roteiro de teste File Server Monitor em '$RootPath'." -ForegroundColor Green
Write-Host ""

Write-Step -Number $step -Description "Criar .txt sem renomear (deixando o Windows gerar o nome padrao)"
$defaultTxt = New-LocalizedDefaultFile -Directory $RootPath -BaseNames $defaultNames[".txt"] -Extension ".txt"
$rootItemsToDelete.Add($defaultTxt)
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar Novo Documento.txt"
$novoDocumento = Join-Path $RootPath "Novo Documento.txt"
New-EmptyFile -Path $novoDocumento | Out-Null
Set-Content -LiteralPath $novoDocumento -Value "conteudo para teste de acesso" -Encoding UTF8
$rootItemsToDelete.Add($novoDocumento)
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Abrir e fechar Novo Documento.txt"
$novoDocumentoStream = [System.IO.File]::OpenRead($novoDocumento)
$novoDocumentoBuffer = [byte[]]::new(16)
[void]$novoDocumentoStream.Read($novoDocumentoBuffer, 0, $novoDocumentoBuffer.Length)
$novoDocumentoStream.Dispose()
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar teste-txt-01.txt"
$namedTxt = Join-Path $RootPath "teste-txt-01.txt"
New-EmptyFile -Path $namedTxt | Out-Null
Set-Content -LiteralPath $namedTxt -Value "conteudo para teste de acesso" -Encoding UTF8
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Acessar teste-txt-01.txt"
$namedTxtStream = [System.IO.File]::OpenRead($namedTxt)
$namedTxtBuffer = [byte[]]::new(16)
[void]$namedTxtStream.Read($namedTxtBuffer, 0, $namedTxtBuffer.Length)
$namedTxtStream.Dispose()
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar teste-rename-origem.txt"
$renameSource = Join-Path $RootPath "teste-rename-origem.txt"
New-EmptyFile -Path $renameSource | Out-Null
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Renomear teste-rename-origem.txt para teste-renomeado-final.txt"
$renameTarget = Join-Path $RootPath "teste-renomeado-final.txt"
Rename-Item -LiteralPath $renameSource -NewName (Split-Path -Leaf $renameTarget)
$rootItemsToDelete.Add($renameTarget)
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar .bmp sem renomear (deixando o Windows gerar o nome padrao)"
$defaultBmp = New-LocalizedDefaultFile -Directory $RootPath -BaseNames $defaultNames[".bmp"] -Extension ".bmp"
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Renomear o .bmp gerado para teste-bmp-01.bmp"
$namedBmp = Join-Path $RootPath "teste-bmp-01.bmp"
Rename-Item -LiteralPath $defaultBmp -NewName (Split-Path -Leaf $namedBmp)
$rootItemsToDelete.Add($namedBmp)
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar teste-bmp-rename-origem.bmp"
$bmpRenameSource = Join-Path $RootPath "teste-bmp-rename-origem.bmp"
New-EmptyFile -Path $bmpRenameSource | Out-Null
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Renomear teste-bmp-rename-origem.bmp para teste-bmp-renomeado-final.bmp"
$bmpRenameTarget = Join-Path $RootPath "teste-bmp-renomeado-final.bmp"
Rename-Item -LiteralPath $bmpRenameSource -NewName (Split-Path -Leaf $bmpRenameTarget)
$rootItemsToDelete.Add($bmpRenameTarget)
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar teste-bmp-02.bmp"
$bmp02 = Join-Path $RootPath "teste-bmp-02.bmp"
New-EmptyFile -Path $bmp02 | Out-Null
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar teste-pptx-01.pptx"
$pptx01 = Join-Path $RootPath "teste-pptx-01.pptx"
New-EmptyFile -Path $pptx01 | Out-Null
$rootItemsToDelete.Add($pptx01)
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar .pptx sem renomear (deixando o Windows gerar o nome padrao)"
$tempPptx = New-LocalizedDefaultFile -Directory $RootPath -BaseNames $defaultNames[".pptx"] -Extension ".pptx"
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Renomear o .pptx gerado para teste-pptx-02.pptx"
$pptx02 = Join-Path $RootPath "teste-pptx-02.pptx"
Rename-Item -LiteralPath $tempPptx -NewName (Split-Path -Leaf $pptx02)
$rootItemsToDelete.Add($pptx02)
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar teste-pptx-rename-origem.pptx"
$pptxRenameSource = Join-Path $RootPath "teste-pptx-rename-origem.pptx"
New-EmptyFile -Path $pptxRenameSource | Out-Null
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Renomear teste-pptx-rename-origem.pptx para teste-pptx-renomeado-final.pptx"
$pptxRenameTarget = Join-Path $RootPath "teste-pptx-renomeado-final.pptx"
Rename-Item -LiteralPath $pptxRenameSource -NewName (Split-Path -Leaf $pptxRenameTarget)
$rootItemsToDelete.Add($pptxRenameTarget)
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar teste-xlsx-01.xlsx"
$xlsx01 = Join-Path $RootPath "teste-xlsx-01.xlsx"
New-EmptyFile -Path $xlsx01 | Out-Null
$rootItemsToDelete.Add($xlsx01)
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar pasta RH"
New-Item -ItemType Directory -Path $rhPath -Force | Out-Null
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Mover teste-txt-01.txt para RH"
$namedTxtInRh = Join-Path $rhPath "teste-txt-01.txt"
Move-Item -LiteralPath $namedTxt -Destination $namedTxtInRh -Force
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Mover teste-bmp-02.bmp para RH"
$bmp02InRh = Join-Path $rhPath "teste-bmp-02.bmp"
Move-Item -LiteralPath $bmp02 -Destination $bmp02InRh -Force
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Criar .xlsx sem renomear (deixando o Windows gerar o nome padrao)"
$tempXlsx = New-LocalizedDefaultFile -Directory $RootPath -BaseNames $defaultNames[".xlsx"] -Extension ".xlsx"
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Renomear o .xlsx gerado para teste-xlsx-02.xlsx"
$xlsx02 = Join-Path $RootPath "teste-xlsx-02.xlsx"
Rename-Item -LiteralPath $tempXlsx -NewName (Split-Path -Leaf $xlsx02)
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Mover teste-xlsx-02.xlsx para RH"
$xlsx02InRh = Join-Path $rhPath "teste-xlsx-02.xlsx"
Move-Item -LiteralPath $xlsx02 -Destination $xlsx02InRh -Force
Invoke-StepDelay -Seconds $DelaySeconds
$step++

Write-Step -Number $step -Description "Apagar tudo o que foi criado no roteiro"

foreach ($path in $rootItemsToDelete) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force -Recurse
    }
}

if (Test-Path -LiteralPath $rhPath) {
    Remove-Item -LiteralPath $rhPath -Force -Recurse
}

Invoke-StepDelay -Seconds $DelaySeconds
Write-Host ""
Write-Host "Roteiro concluido." -ForegroundColor Green
