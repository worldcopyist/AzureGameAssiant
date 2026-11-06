param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [ValidateSet("Aot", "SelfContained")]
    [string]$CollectorMode = "Aot",
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.VersionPrefix
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = "0.0.0"
}

$runtime = "win-x64"
$packageName = "AzureGameAssistant-$version-$runtime"
$artifacts = Join-Path $root "artifacts"
$stage = Join-Path $artifacts $packageName
$collectorOutput = Join-Path $artifacts "collector"

if (Test-Path $stage) {
    Remove-Item -Path $stage -Recurse -Force
}

New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host "Publishing UI ($Configuration, self-contained, ReadyToRun)..."
dotnet publish (Join-Path $root "src\AzureGameAssistant.UI\AzureGameAssistant.UI.csproj") `
    -c $Configuration -r $runtime --self-contained true `
    -p:PublishReadyToRun=true `
    -o $stage
if ($LASTEXITCODE -ne 0) {
    Write-Error "UI publish failed."
    exit $LASTEXITCODE
}

Write-Host "Publishing collector ($Configuration, $CollectorMode)..."
if ($CollectorMode -eq "Aot") {
    dotnet publish (Join-Path $root "src\AzureGameAssistant.Collector\AzureGameAssistant.Collector.csproj") `
        -c $Configuration -r $runtime -p:PublishAot=true `
        -o $collectorOutput
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Collector NativeAOT publish failed. Install the Desktop development with C++ workload, or use -CollectorMode SelfContained for a non-AOT test build."
        exit $LASTEXITCODE
    }
}
else {
    dotnet publish (Join-Path $root "src\AzureGameAssistant.Collector\AzureGameAssistant.Collector.csproj") `
        -c $Configuration -r $runtime --self-contained true `
        -p:PublishAot=false `
        -o $collectorOutput
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Collector publish failed."
        exit $LASTEXITCODE
    }

    Write-Warning "Self-contained JIT collector is for testing only; it does not meet the 30 MB budget."
}

$collectorExe = Join-Path $collectorOutput "AzureGameAssistant.Collector.exe"
if (-not (Test-Path $collectorExe)) {
    Write-Error "Collector executable missing from $collectorOutput"
    exit 1
}

$collectorStage = Join-Path $stage "collector"
if (Test-Path $collectorStage) {
    Remove-Item -Path $collectorStage -Recurse -Force
}

New-Item -ItemType Directory -Path $collectorStage -Force | Out-Null
Get-ChildItem -Path $collectorOutput -File | Where-Object { $_.Extension -ne ".pdb" } | ForEach-Object {
    Copy-Item $_.FullName -Destination $collectorStage -Force
}

$presentMon = Join-Path $root "third_party\presentmon"
if (Test-Path $presentMon) {
    Copy-Item $presentMon -Destination $stage -Recurse -Force
}
else {
    Write-Warning "third_party\presentmon not found; frame capture is unavailable in this package."
}

$licenseFiles = @("LICENSE", "LICENSE.md", "LICENSE.txt")
foreach ($license in $licenseFiles) {
    $candidate = Join-Path $root $license
    if (Test-Path $candidate) {
        Copy-Item $candidate -Destination $stage -Force
        break
    }
}

$zipPath = Join-Path $artifacts "$packageName.zip"
if (-not $SkipZip) {
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath
    Write-Host "Created $zipPath"
}

Write-Host "Package staging directory: $stage"
