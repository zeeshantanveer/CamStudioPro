param([switch]$RegisteredCapture)
$ErrorActionPreference = "Stop"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw "Visual C++ x64 Build Tools are required." }
$msbuild = Join-Path $vs "MSBuild\Current\Bin\MSBuild.exe"
& $msbuild (Join-Path $PSScriptRoot "tests\NativeCameraSmoke\NativeCameraSmoke.vcxproj") /p:Platform=x64 /p:Configuration=Release /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Native test build failed." }
$testExe = Join-Path $PSScriptRoot "tests\NativeCameraSmoke\bin\x64\Release\NativeCameraSmoke.exe"
# Debug avoids overwriting an app the developer is running from Release.
$cameraDll = Join-Path $PSScriptRoot "src\CamStudio.VCam.Native\bin\x64\Debug\CamStudio.VCam.Native.dll"
$captureArguments = @($testExe, $cameraDll)
if ($RegisteredCapture) { $captureArguments += '--registered-capture' }
dotnet run --project (Join-Path $PSScriptRoot "tests\VirtualCameraSmoke") -c Debug -p:Platform=x64 -- @captureArguments
if ($LASTEXITCODE -ne 0) { throw "Native camera integration test failed." }
