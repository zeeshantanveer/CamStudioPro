<#
.SYNOPSIS
  Builds the Microsoft Store package: windows\dist\msix\CamStudioPro_<version>_x64.msix

.DESCRIPTION
  1. Publishes the app self-contained (Store users need no separate .NET install for the app).
  2. Lays out the package with the logo assets and AppxManifest.xml (identity from store-identity.json).
  3. Generates resources.pri (scaled icons) and packs the .msix with makeappx (Windows SDK).

  Upload the unsigned .msix to Partner Center; the Store signs it. For a local test install, pass
  -Sign: the package is signed with a self-signed certificate whose subject equals the Publisher,
  and the .cer is written next to the .msix (trust it once, see the printed instructions).

.EXAMPLE
  .\build-msix.ps1 -Version 1.1.0.0
  .\build-msix.ps1 -Version 1.1.0.0 -Sign
  .\build-msix.ps1 -Version 1.1.0.0 -FfmpegDir C:\tools\ffmpeg-lgpl\bin
#>
param(
    # Store rule: the 4th part must be 0, and each upload must increase the version.
    [string]$Version = "1.1.0.0",
    [ValidateSet("x64")] [string]$Arch = "x64",
    [string]$IdentityFile = (Join-Path $PSScriptRoot "packaging\store-identity.json"),
    # Optional folder with ffmpeg.exe + ffprobe.exe (+ their LICENSE) to bundle for the editor.
    # Prefer an LGPL build for Store distribution; see packaging\STORE.md.
    [string]$FfmpegDir = "",
    # Microsoft-issued custom capability descriptors, if supplied for this identity.
    [string]$CapabilityDescriptorDir = "",
    [switch]$DeveloperTest,
    [switch]$Sign
)
$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "Version must look like 1.2.3.0 (the Store requires the last part to be 0)." }
if (-not (Test-Path $IdentityFile)) {
    $example = Join-Path $PSScriptRoot "packaging\store-identity.example.json"
    Write-Warning "$IdentityFile not found; using placeholder identity from $example. Copy it to store-identity.json and fill it from Partner Center for Store builds."
    $IdentityFile = $example
}
$id = Get-Content $IdentityFile -Raw | ConvertFrom-Json
foreach ($k in "IdentityName", "Publisher", "PublisherDisplayName", "DisplayName") {
    if (-not $id.$k) { throw "$k is missing in $IdentityFile" }
}

# --- Windows SDK tools ---------------------------------------------------------------------------
$kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$sdk = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^10\.' -and (Test-Path (Join-Path $_.FullName "x64\makeappx.exe")) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw "Windows 10/11 SDK not found (need makeappx.exe). Install it with Visual Studio Installer or winget install Microsoft.WindowsSDK." }
$tools = Join-Path $sdk.FullName "x64"
$makeappx = Join-Path $tools "makeappx.exe"
$makepri = Join-Path $tools "makepri.exe"
$signtool = Join-Path $tools "signtool.exe"

if ($DeveloperTest -and (!$Sign -or $CapabilityDescriptorDir)) {
    throw "DeveloperTest requires -Sign and cannot be combined with CapabilityDescriptorDir."
}
if ($DeveloperTest -and !$id.PackageFamilyName) { throw "PackageFamilyName is required in the identity file for DeveloperTest." }
if ($Sign -and !$DeveloperTest -and !$CapabilityDescriptorDir) {
    throw "Local installation needs an SCCD: use -Sign -DeveloperTest for Developer Mode testing, or supply Microsoft-issued files with -CapabilityDescriptorDir."
}
if ($CapabilityDescriptorDir) {
    $descriptorFiles = @(Get-ChildItem -LiteralPath $CapabilityDescriptorDir -Filter *.sccd -File)
    if ($descriptorFiles.Count -eq 0) { throw "No Microsoft-issued .sccd files in $CapabilityDescriptorDir" }
} else {
    Write-Warning "Camera MSIX requires approval for classicAppCompat and classicAppCompatElevated. This build is a qualification candidate, not a verified Store release."
}

# --- publish -------------------------------------------------------------------------------------
$root = $PSScriptRoot
$work = Join-Path $root "dist\msix"
$layout = Join-Path $work "layout"
$layout = [IO.Path]::GetFullPath($layout)
$expectedLayout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "dist\msix\layout"))
if ($layout -ne $expectedLayout) { throw "Unexpected MSIX layout path: $layout" }
if (Test-Path -LiteralPath $layout) { Remove-Item -LiteralPath $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null

$appVersion = ($Version -split '\.')[0..2] -join '.'
dotnet publish (Join-Path $root "src\CamStudio.Desktop\CamStudio.Desktop.csproj") -c Release -p:Platform=$Arch -r "win-$Arch" `
    --self-contained true -p:Version=$appVersion -p:FileVersion=$Version -o $layout
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-ChildItem $layout -Recurse -Filter *.pdb | Remove-Item

if ($FfmpegDir) {
    $ff = Join-Path $layout "ffmpeg"
    New-Item -ItemType Directory -Force $ff | Out-Null
    foreach ($exe in "ffmpeg.exe", "ffprobe.exe") {
        $src = Join-Path $FfmpegDir $exe
        if (-not (Test-Path $src)) { throw "$exe not found in $FfmpegDir" }
        Copy-Item $src $ff
    }
    Get-ChildItem $FfmpegDir -File | Where-Object { $_.Name -match 'LICENSE|COPYING|README' } | Copy-Item -Destination $ff
    Write-Host "Bundled ffmpeg from $FfmpegDir"
}

# --- manifest + assets ---------------------------------------------------------------------------
$assetDestination = Join-Path $layout "Assets"
New-Item -ItemType Directory -Force $assetDestination | Out-Null
Get-ChildItem (Join-Path $root "packaging\Assets") | Copy-Item -Destination $assetDestination -Recurse -Force
if ($CapabilityDescriptorDir) { $descriptorFiles | Copy-Item -Destination $layout }
if ($DeveloperTest) {
    # Microsoft documents Catalog=FFFF and a 64-character placeholder certificate hash
    # for Developer Mode testing. This is NOT Microsoft approval or a production SCCD.
    $family = [Security.SecurityElement]::Escape($id.PackageFamilyName)
    $descriptor = @"
<?xml version="1.0" encoding="utf-8"?>
<CustomCapabilityDescriptor xmlns="http://schemas.microsoft.com/appx/2016/sccd">
  <CustomCapabilities>
    <CustomCapability Name="Microsoft.classicAppCompat_8wekyb3d8bbwe"/>
    <CustomCapability Name="Microsoft.classicAppCompatElevated_8wekyb3d8bbwe"/>
  </CustomCapabilities>
  <AuthorizedEntities>
    <AuthorizedEntity AppPackageFamilyName="$family" CertificateSignatureHash="0000000000000000000000000000000000000000000000000000000000000000"/>
  </AuthorizedEntities>
  <Catalog>FFFF</Catalog>
  <DeveloperModeOnly Value="true"/>
</CustomCapabilityDescriptor>
"@
    [IO.File]::WriteAllText((Join-Path $layout "CamStudioPro.DeveloperTest.sccd"), $descriptor, (New-Object Text.UTF8Encoding $false))
    Write-Warning "DEVELOPER TEST ONLY. Enable Windows Developer Mode before installing. Do not upload this package to the Store."
}

$manifest = Get-Content (Join-Path $root "packaging\Package.appxmanifest") -Raw
$manifest = $manifest.Replace('$IDENTITY_NAME$', $id.IdentityName).Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($id.Publisher)).
    Replace('$PUBLISHER_DISPLAY_NAME$', [Security.SecurityElement]::Escape($id.PublisherDisplayName)).
    Replace('$DISPLAY_NAME$', [Security.SecurityElement]::Escape($id.DisplayName)).Replace('$VERSION$', $Version).Replace('$ARCH$', $Arch)
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object Text.UTF8Encoding $false))

# --- resources.pri (lets Windows pick scale-200 / targetsize icons) ------------------------------
$priConfig = Join-Path $work "priconfig.xml"
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed" }
# One resources.pri: no per-language/per-scale split packs (those are for .msixbundle resource packages).
[xml]$cfg = Get-Content $priConfig
$cfg.resources.SelectNodes("packaging") | ForEach-Object { [void]$cfg.resources.RemoveChild($_) }
$cfg.Save($priConfig)
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout "AppxManifest.xml") /of (Join-Path $layout "resources.pri") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri new failed" }

# --- pack ----------------------------------------------------------------------------------------
$testSuffix = if ($DeveloperTest) { "_DeveloperTest" } else { "" }
$msix = Join-Path $work "CamStudioPro_${Version}_${Arch}${testSuffix}.msix"
& $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

if ($Sign) {
    $subject = $id.Publisher
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -FriendlyName "CamStudio Pro test signing" `
            -CertStoreLocation Cert:\CurrentUser\My -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
    $cer = Join-Path $work "CamStudioPro-test.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Write-Host ""
    Write-Host "Test install (once, as administrator):  Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "Then:                                   Add-AppxPackage `"$msix`""
}

Write-Host ""
Write-Host "Built: $msix"
if ($id.IdentityName -eq "CamStudioPro.Dev") {
    Write-Warning "store-identity.json still has placeholder values. Fill it from Partner Center before uploading to the Store."
}
