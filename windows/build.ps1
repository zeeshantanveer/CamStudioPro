# Builds the CamStudio Pro Windows client into windows\dist\CamStudioPro (x64).
# Requires the .NET 10 SDK. End users need the .NET 10 Desktop Runtime (x64); the Store build
# (build-msix.ps1) is self-contained instead.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "dist\CamStudioPro"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish (Join-Path $root "src\CamStudio.Desktop\CamStudio.Desktop.csproj") -c $Configuration -p:Platform=x64 -r win-x64 --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built: $out\CamStudioPro.exe"
