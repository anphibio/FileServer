param(
    [string]$RootPath = 'C:\Corporativo',
    [ValidateRange(1, 100000)]
    [int]$FileCount = 2000,
    [string]$ScenarioName = '',
    [ValidateSet('Create', 'Cleanup')]
    [string]$Mode = 'Create',
    [string]$ScenarioPath = ''
)

$ErrorActionPreference = 'Stop'
$prefix = 'codex-agent-pipeline-'
$root = [IO.Path]::GetFullPath($RootPath).TrimEnd('\')

if ($Mode -eq 'Cleanup') {
    if ([string]::IsNullOrWhiteSpace($ScenarioPath)) {
        throw 'Informe -ScenarioPath para limpar o cenario.'
    }

    $target = [IO.Path]::GetFullPath($ScenarioPath).TrimEnd('\')
    if (-not $target.StartsWith("$root\$prefix", [StringComparison]::OrdinalIgnoreCase)) {
        throw "A limpeza so aceita pastas '$prefix*' diretamente abaixo de '$root'."
    }

    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }

    [pscustomobject]@{
        mode = 'cleanup'
        scenarioPath = $target
        exists = Test-Path -LiteralPath $target
    } | ConvertTo-Json -Compress
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ScenarioName)) {
    $ScenarioName = "$prefix$(Get-Date -Format 'yyyyMMdd-HHmmss')"
}

$hasInvalidName = $ScenarioName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0
if (-not $ScenarioName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $hasInvalidName) {
    throw "ScenarioName deve iniciar com '$prefix' e conter apenas um nome de pasta valido."
}

$target = Join-Path $root $ScenarioName
if (Test-Path -LiteralPath $target) {
    throw "O cenario '$target' ja existe."
}

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
New-Item -ItemType Directory -Path $target | Out-Null
for ($index = 1; $index -le $FileCount; $index++) {
    $fileName = 'pipeline-{0:D6}.txt' -f $index
    [IO.File]::WriteAllText((Join-Path $target $fileName), "pipeline load $index")
}
$stopwatch.Stop()

[pscustomobject]@{
    mode = 'create'
    scenarioPath = $target
    files = $FileCount
    durationMs = $stopwatch.ElapsedMilliseconds
    createdUtc = [DateTimeOffset]::UtcNow
} | ConvertTo-Json -Compress
