param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$Tray
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "AzureGameAssistant.sln"

if (-not $NoBuild) {
    dotnet build $solution -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$exe = Join-Path $root "src\AzureGameAssistant.UI\bin\$Configuration\net8.0-windows\AzureGameAssistant.exe"
if (-not (Test-Path $exe)) {
    Write-Error "UI executable not found: $exe"
    exit 1
}

$collector = Join-Path $root "src\AzureGameAssistant.Collector\bin\$Configuration\net8.0\AzureGameAssistant.Collector.exe"
if (-not (Test-Path $collector)) {
    Write-Warning "Collector executable not found: $collector (build the solution first)"
}

$arguments = @()
if ($Tray) {
    $arguments += "--tray"
}

Start-Process -FilePath $exe -ArgumentList $arguments
Write-Host "Started $exe"
