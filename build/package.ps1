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

$running = Get-Process -Name "AzureGameAssistant", "AzureGameAssistant.Collector" -ErrorAction SilentlyContinue
if ($running) {
    $ids = ($running | ForEach-Object { $_.Id }) -join ", "
    Write-Error "程序正在运行（PID: $ids）。重复打包会清空正在使用的目录并删除 PresentMon，请先从托盘退出后再执行。"
    exit 1
}

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

Write-Host "Publishing sensors sidecar ($Configuration, self-contained)..."
$sensorsOutput = Join-Path $artifacts "sensors"
dotnet publish (Join-Path $root "src\AzureGameAssistant.Sensors\AzureGameAssistant.Sensors.csproj") `
    -c $Configuration -r $runtime --self-contained true `
    -p:PublishReadyToRun=true `
    -o $sensorsOutput
if ($LASTEXITCODE -ne 0) {
    Write-Error "Sensors sidecar publish failed."
    exit $LASTEXITCODE
}

$sensorsExe = Join-Path $sensorsOutput "AzureGameAssistant.Sensors.exe"
if (-not (Test-Path $sensorsExe)) {
    Write-Error "Sensors executable missing from $sensorsOutput"
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

$sensorsStage = Join-Path $collectorStage "sensors"
New-Item -ItemType Directory -Path $sensorsStage -Force | Out-Null
Get-ChildItem -Path $sensorsOutput -File | Where-Object { $_.Extension -ne ".pdb" } | ForEach-Object {
    Copy-Item $_.FullName -Destination $sensorsStage -Force
}

$sensorsLicenses = Join-Path $sensorsOutput "LICENSES"
if (Test-Path $sensorsLicenses) {
    Copy-Item $sensorsLicenses -Destination (Join-Path $sensorsStage "LICENSES") -Recurse -Force
}

$presentMon = Join-Path $root "third_party\presentmon"
$presentMonExe = Join-Path $presentMon "PresentMon-2.6.0-x64.exe"
$presentMonLicense = Join-Path $presentMon "LICENSE.txt"
$expectedHash = "B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF"
if (-not (Test-Path $presentMonExe)) {
    Write-Error "PresentMon binary missing: $presentMonExe"
    exit 1
}

if (-not (Test-Path $presentMonLicense)) {
    Write-Error "PresentMon license missing: $presentMonLicense"
    exit 1
}

$actualHash = (Get-FileHash -Path $presentMonExe -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) {
    Write-Error "PresentMon SHA256 mismatch: $actualHash"
    exit 1
}

Copy-Item $presentMon -Destination $collectorStage -Recurse -Force

$licenseFiles = @("LICENSE", "LICENSE.md", "LICENSE.txt")
$licenseCopied = $false
foreach ($license in $licenseFiles) {
    $candidate = Join-Path $root $license
    if (Test-Path $candidate) {
        Copy-Item $candidate -Destination $stage -Force
        $licenseCopied = $true
        break
    }
}

if (-not $licenseCopied) {
    Write-Warning "包内未包含 LICENSE：项目自身许可证尚未确定（K-05），首次公开发布前需补齐。"
}

$readme = Join-Path $root "README.md"
if (-not (Test-Path $readme)) {
    Write-Error "README missing: $readme"
    exit 1
}

Copy-Item $readme -Destination $stage -Force

$notices = Join-Path $root "THIRD-PARTY-NOTICES.md"
if (-not (Test-Path $notices)) {
    Write-Error "Third-party notices missing: $notices"
    exit 1
}

Copy-Item $notices -Destination $stage -Force

$thirdPartyLicenses = Join-Path $root "third_party\licenses"
if (Test-Path $thirdPartyLicenses) {
    Copy-Item $thirdPartyLicenses -Destination (Join-Path $stage "LICENSES") -Recurse -Force
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
