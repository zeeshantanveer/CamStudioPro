param([ValidateSet("Debug", "Release")][string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (!(Test-Path -LiteralPath $vswhere)) { throw "Install Visual Studio Build Tools with Desktop development with C++." }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw "The x64 Visual C++ build tools are required for the native camera." }
$msbuild = Join-Path $vs "MSBuild\Current\Bin\MSBuild.exe"
$project = Join-Path $PSScriptRoot "src\CamStudio.VCam.Native"
dotnet restore (Join-Path $project "NativeDependencies.csproj") --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Native camera dependency restore failed." }
& $msbuild (Join-Path $project "CamStudio.VCam.Native.vcxproj") /p:Platform=x64 "/p:Configuration=$Configuration" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Native camera build failed." }
