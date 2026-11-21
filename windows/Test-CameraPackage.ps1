param([Parameter(Mandatory=$true)][string]$PackagePath)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
try {
    $entry = $archive.GetEntry("AppxManifest.xml")
    $reader = New-Object IO.StreamReader($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $ns = New-Object Xml.XmlNamespaceManager($manifest.NameTable)
    $ns.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $ns.AddNamespace("com", "http://schemas.microsoft.com/appx/manifest/com/windows10/4")
    $ns.AddNamespace("d7", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/7")
    $ns.AddNamespace("u4", "http://schemas.microsoft.com/appx/manifest/uap/windows10/4")
    $extension = $manifest.SelectSingleNode("//com:Extension[@Category='windows.comServer']", $ns)
    if (!$extension -or $extension.GetAttribute("Scope", $ns.LookupNamespace("d7")) -ne "machine" -or $extension.GetAttribute("CompatMode", $ns.LookupNamespace("d7")) -ne "classic") { throw "Missing machine/classic COM registration." }
    $server = $extension.SelectSingleNode("com:ComServer/com:InProcessServer", $ns)
    if ($server.Path -ne "VCam\CamStudio.VCam.Native.dll") { throw "Camera must use native DLL." }
    $class = $server.SelectSingleNode("com:Class", $ns)
    if ($class.Id -ne "3c1f6a52-8e0b-4b7d-9f2a-6d4e8c1b7a35" -or $class.ThreadingModel -ne "Both") { throw "Wrong camera CLSID or threading model." }
    $cameraFiles = @($archive.Entries | Where-Object FullName -like 'VCam/*')
    if ($cameraFiles.Count -ne 1 -or $cameraFiles[0].FullName -ne 'VCam/CamStudio.VCam.Native.dll') { throw "Unexpected camera payload: managed dependencies must not be packaged." }
    foreach ($capability in @('Microsoft.classicAppCompat_8wekyb3d8bbwe', 'Microsoft.classicAppCompatElevated_8wekyb3d8bbwe')) {
        if (!$manifest.SelectSingleNode("//u4:CustomCapability[@Name='$capability']", $ns)) { throw "Missing $capability" }
    }
    if (!$manifest.SelectSingleNode("//m:DeviceCapability[@Name='webcam']", $ns)) { throw "Missing webcam capability." }
    $identity = Get-Content (Join-Path $PSScriptRoot "packaging\store-identity.json") -Raw | ConvertFrom-Json
    if ($manifest.Package.Identity.Name -ne $identity.IdentityName -or $manifest.Package.Identity.Publisher -ne $identity.Publisher) { throw "Wrong Store identity." }
    foreach ($license in @('THIRD-PARTY-VCamSample-LICENSE.txt','THIRD-PARTY-WIL-LICENSE.txt')) { if (!$archive.GetEntry($license)) { throw "Missing $license" } }
    $testDescriptor = $archive.GetEntry('CamStudioPro.DeveloperTest.sccd')
    if ($testDescriptor) {
        if ([IO.Path]::GetFileName($PackagePath) -notlike '*_DeveloperTest.msix') { throw "Developer descriptor in a production-named artifact." }
        $descriptorReader = New-Object IO.StreamReader($testDescriptor.Open())
        try { [xml]$descriptorXml = $descriptorReader.ReadToEnd() } finally { $descriptorReader.Dispose() }
        $descriptorRoot = $descriptorXml.DocumentElement
        if ($descriptorRoot.Catalog -ne 'FFFF' -or $descriptorRoot.DeveloperModeOnly.Value -ne 'true') { throw "Invalid developer SCCD." }
        $entity = $descriptorRoot.AuthorizedEntities.AuthorizedEntity
        if ($entity.AppPackageFamilyName -ne $identity.PackageFamilyName -or $entity.CertificateSignatureHash.Length -ne 64) { throw "Wrong developer SCCD identity or signature hash length." }
        $declared = @($manifest.SelectNodes('//u4:CustomCapability', $ns) | ForEach-Object { $_.Name })
        $described = @($descriptorRoot.CustomCapabilities.CustomCapability | ForEach-Object { $_.Name })
        if (Compare-Object $declared $described) { throw "SCCD capabilities differ from manifest." }
        Write-Host "PASS: local SCCD covers both capabilities, matches PFN, and is explicitly Developer Mode only."
    }
    Write-Host "PASS: Store identity, native-only camera payload, machine/classic COM, CLSID, capabilities, and licenses."
} finally { $archive.Dispose() }
