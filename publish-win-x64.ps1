param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "BroadcastControl.App\BroadcastControl.App.csproj"

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishDir = Join-Path $PSScriptRoot "BroadcastControl.App\bin\$Configuration\net10.0-windows\$Runtime\publish"
Write-Host "Published GUI package:"
Write-Host $publishDir
Write-Host "Edit LigDnaGui.config.json next to the exe for each PC/network."
