param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts\mcp"

if (Test-Path $output) {
    Remove-Item -Path $output -Recurse -Force
}

New-Item -ItemType Directory -Path $output -Force | Out-Null

Write-Host "Publishing MCP server ($Configuration, single file)..."
if ($SelfContained) {
    dotnet publish (Join-Path $root "src\AzureGameAssistant.Mcp\AzureGameAssistant.Mcp.csproj") `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -o $output
}
else {
    dotnet publish (Join-Path $root "src\AzureGameAssistant.Mcp\AzureGameAssistant.Mcp.csproj") `
        -c $Configuration -r win-x64 --self-contained false `
        -p:PublishSingleFile=true `
        -o $output
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "MCP publish failed."
    exit $LASTEXITCODE
}

$exe = Join-Path $output "AzureGameAssistant.Mcp.exe"
if (-not (Test-Path $exe)) {
    Write-Error "MCP executable missing: $exe"
    exit 1
}

Write-Host "MCP server: $exe"
