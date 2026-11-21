param(
    [string]$Version = "1.5.5",
    [string]$SigningCertificateThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$TestSign,
    [switch]$Release
)
$ErrorActionPreference = "Stop"
if ($TestSign -and ($Release -or $SigningCertificateThumbprint)) { throw "TestSign cannot be combined with Release or a supplied signing certificate." }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "MSI version must have three numeric parts, such as 1.2.0." }
$parsedVersion = [version]$Version
if ($parsedVersion.Major -gt 255 -or $parsedVersion.Minor -gt 255 -or $parsedVersion.Build -gt 65535) { throw "MSI version exceeds Windows Installer limits." }
if ($Release -and !$SigningCertificateThumbprint) { throw "Release requires a trusted publisher code-signing certificate thumbprint." }
if ($Release) {
    $signingCertificate = Get-Item "Cert:\CurrentUser\My\$SigningCertificateThumbprint" -ErrorAction Stop
    if ($signingCertificate.Subject -eq $signingCertificate.Issuer) { throw "A self-signed test certificate is not eligible for Store MSI distribution." }
}
$root = $PSScriptRoot
$work = Join-Path $root "dist\msi"
$payload = [IO.Path]::GetFullPath((Join-Path $work "payload"))
$expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "dist\msi\payload"))
if ($payload -ne $expected) { throw "Unexpected payload path." }
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Force $payload | Out-Null
if ($TestSign) {
    $testCertificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=CamStudio Pro Local Test' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if (!$testCertificate) {
        $testCertificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=CamStudio Pro Local Test' -FriendlyName 'CamStudio Pro local installer testing only' -HashAlgorithm SHA256 -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(2)
    }
    $SigningCertificateThumbprint = $testCertificate.Thumbprint
    Export-Certificate -Cert $testCertificate -FilePath (Join-Path $work 'CamStudioPro-LocalTest.cer') | Out-Null
}
$wix = Join-Path $root "tools\wix\wix.exe"
if (!(Test-Path -LiteralPath $wix)) {
    dotnet tool install wix --version 4.0.6 --tool-path (Join-Path $root "tools\wix")
    if ($LASTEXITCODE -ne 0) { throw "WiX tool installation failed." }
}
$installedExtensions = & $wix extension list
if ($LASTEXITCODE -ne 0) { throw "WiX extension listing failed." }
foreach ($extension in @('WixToolset.Firewall.wixext/4.0.6','WixToolset.UI.wixext/4.0.6')) {
    $extensionId, $extensionVersion = $extension.Split('/')
    $installed = $installedExtensions | Where-Object { $_ -match "^$([regex]::Escape($extensionId))\s+$([regex]::Escape($extensionVersion))\s*$" }
    if ($installed) { continue }
    & $wix extension add $extension
    if ($LASTEXITCODE -ne 0) { throw "WiX extension installation failed." }
}
dotnet publish (Join-Path $root "src\CamStudio.Desktop") -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:Version=$Version -o $payload
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }
Get-ChildItem -LiteralPath $payload -Recurse -Filter *.pdb | Remove-Item
# Only the native camera source is shipped; no MSIX capability or registration artifacts.
if (!(Test-Path (Join-Path $payload "VCam\CamStudio.VCam.Native.dll"))) { throw "Native camera payload missing." }
$signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
function Sign-File([string]$path) {
    if (!$signTool) { throw "Windows SDK signtool.exe is required." }
    if ($TestSign) { & $signTool.FullName sign /sha1 $SigningCertificateThumbprint /fd SHA256 $path }
    else { & $signTool.FullName sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $path }
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $path" }
}
$unsigned = @()
foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Where-Object Extension -in '.exe','.dll') {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    # Keep valid vendor signatures. Sign our own files and unsigned third-party binaries.
    if ($signature.Status -ne 'Valid') {
        if ($SigningCertificateThumbprint) { Sign-File $file.FullName }
        else { $unsigned += $file.FullName.Substring($payload.Length+1) }
    }
    if ($Release -and (Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -ne 'Valid') { throw "Untrusted payload signature: $($file.FullName)" }
}
$ns = 'http://wixtoolset.org/schemas/v4/wxs'
[xml]$xml = "<Wix xmlns='$ns' xmlns:fw='$ns/firewall'><Fragment><DirectoryRef Id='INSTALLFOLDER'/><ComponentGroup Id='Payload'/></Fragment></Wix>"
function Element($parent, [string]$name, [hashtable]$attributes, [string]$namespace = $ns) {
    $node = $xml.CreateElement($name, $namespace)
    foreach ($key in $attributes.Keys) { $node.SetAttribute($key, [string]$attributes[$key]) }
    [void]$parent.AppendChild($node)
    return $node
}
function StableId([string]$prefix, [string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant())) } finally { $sha.Dispose() }
    return $prefix + ([BitConverter]::ToString($hash).Replace('-','').Substring(0,32))
}
$directoryNodes = @{}
$directoryNodes[''] = $xml.DocumentElement.Fragment.DirectoryRef
$group = $xml.DocumentElement.Fragment.ComponentGroup
foreach ($dir in Get-ChildItem -LiteralPath $payload -Directory -Recurse | Sort-Object FullName) {
    $rel = $dir.FullName.Substring($payload.Length+1)
    $parent = [IO.Path]::GetDirectoryName($rel)
    $directoryNodes[$rel] = Element $directoryNodes[$parent] 'Directory' @{Id=(StableId 'D' $rel);Name=$dir.Name}
}
foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName) {
    $rel = $file.FullName.Substring($payload.Length+1)
    $componentId = StableId 'C' $rel
    $fileId = StableId 'F' $rel
    $component = Element $directoryNodes[[IO.Path]::GetDirectoryName($rel)] 'Component' @{Id=$componentId;Guid='*';Bitness='always64'}
    $fileNode = Element $component 'File' @{Id=$fileId;Source=$file.FullName;KeyPath='yes'}
    [void](Element $group 'ComponentRef' @{Id=$componentId})
    if ($rel -eq 'VCam\CamStudio.VCam.Native.dll') {
        $reg = 'SOFTWARE\Classes\CLSID\{3c1f6a52-8e0b-4b7d-9f2a-6d4e8c1b7a35}\InprocServer32'
        [void](Element $component 'RegistryValue' @{Root='HKLM';Key=$reg;Type='string';Value="[#$fileId]"})
        [void](Element $component 'RegistryValue' @{Root='HKLM';Key=$reg;Name='ThreadingModel';Type='string';Value='Both'})
    }
    if ($rel -eq 'CamStudioPro.exe') {
        $shortcut = Element $fileNode 'Shortcut' @{Id='StartMenuShortcut';Directory='ProgramMenuFolder';Name='CamStudio Pro';WorkingDirectory='INSTALLFOLDER';Icon='CamStudioPro.ico';IconIndex='0';Advertise='no'}
        [void](Element $shortcut 'ShortcutProperty' @{Id='CamStudioTaskbarId';Key='System.AppUserModel.ID';Value='ZeeshanTanveer.CamStudioPro.Desktop'})
        [void](Element $component 'RegistryValue' @{Root='HKLM';Key='SOFTWARE\Zeeshan Tanveer\CamStudioPro';Name='InstallPath';Type='string';Value='[INSTALLFOLDER]'})
        foreach ($rule in @(@{Id='PhoneTcp';Name='CamStudio Pro phone video';Protocol='tcp';Port='47801'},@{Id='DiscoveryUdp';Name='CamStudio Pro phone discovery';Protocol='udp';Port='47800'})) {
            $rule['Scope']='localSubnet';$rule['Profile']='private';$rule['IgnoreFailure']='no'
            [void](Element $fileNode 'FirewallException' $rule "$ns/firewall")
        }
    }
}
$generated = Join-Path $work "Payload.wxs"
$xml.Save($generated)
$testSuffix = if ($TestSign) { "_TestSigned" } else { "" }
$msi = Join-Path $work "CamStudioPro_${Version}_x64${testSuffix}.msi"
& $wix build (Join-Path $root "packaging\msi\Package.wxs") $generated -arch x64 -ext WixToolset.Firewall.wixext -ext WixToolset.UI.wixext -d "ProductVersion=$Version" -d "AppIcon=$(Join-Path $root 'src\CamStudio.Desktop\Assets\app.ico')" -d "LicenseRtf=$(Join-Path $root 'packaging\msi\License.rtf')" -out $msi
if ($LASTEXITCODE -ne 0) { throw "MSI build failed." }
if ($Release) {
    # WiX embeds native custom-action DLLs as Binary streams as well as the app payload.
    $inspection = Join-Path $work ("signature-inspection-" + [guid]::NewGuid().ToString('N'))
    & $wix msi decompile $msi -x $inspection -o "$inspection.wxs"
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect embedded installer binaries." }
    foreach ($embedded in Get-ChildItem (Join-Path $inspection 'Binary') -File) {
        $bytes = [IO.File]::ReadAllBytes($embedded.FullName)
        if ($bytes.Length -gt 2 -and $bytes[0] -eq 77 -and $bytes[1] -eq 90) {
            if ((Get-AuthenticodeSignature -LiteralPath $embedded.FullName).Status -ne 'Valid') { throw "Untrusted embedded installer binary: $($embedded.Name)" }
        }
    }
}
if ($SigningCertificateThumbprint) { Sign-File $msi }
if ($Release -and (Get-AuthenticodeSignature -LiteralPath $msi).Status -ne 'Valid') { throw "MSI signature is not trusted." }
$digest = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
"$digest  $([IO.Path]::GetFileName($msi))" | Set-Content "$msi.sha256"
@{
    Version=$Version; Architecture='x64'; Artifact=[IO.Path]::GetFileName($msi)
    Sha256=$digest; ReleaseBuild=[bool]$Release; TestSigned=[bool]$TestSign
    ProposedDownloadUrl=$(if ($TestSign) { $null } else { "https://deepsoll.com/downloads/camstudiopro/$Version/$([IO.Path]::GetFileName($msi))" })
    Uploaded=$false
} | ConvertTo-Json | Set-Content (Join-Path $work "release-info.json")
$unsigned | Set-Content (Join-Path $work "unsigned-payload.txt")
Write-Host "Built: $msi"
if ($TestSign) {
    Write-Warning "SELF-SIGNED LOCAL TEST ONLY. Do not submit this installer to Microsoft Store."
    Write-Host "Test certificate: $(Join-Path $work 'CamStudioPro-LocalTest.cer')"
}
if (!$Release) { Write-Warning "LOCAL TEST CANDIDATE. Store release requires trusted signing of the MSI and every PE file, plus a versioned HTTPS download URL." }
Write-Host "Silent install: msiexec.exe /i `"$msi`" /qn /norestart /L*v install.log"
