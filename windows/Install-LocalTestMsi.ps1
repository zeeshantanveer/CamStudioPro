param(
    [string]$PackagePath = (Join-Path $PSScriptRoot 'dist\msi\CamStudioPro_1.3.1_x64_TestSigned.msi'),
    [switch]$TrustTestCertificate
)
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script in Administrator PowerShell.' }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
if ([IO.Path]::GetFileName($package) -notlike '*_TestSigned.msi') { throw 'This helper is only for self-signed local MSI candidates.' }
$certificatePath = Join-Path ([IO.Path]::GetDirectoryName($package)) 'CamStudioPro-LocalTest.cer'
$certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($certificatePath)
$signature = Get-AuthenticodeSignature -LiteralPath $package
if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or $certificate.Subject -ne 'CN=CamStudio Pro Local Test') { throw 'Installer signer does not match the expected local test certificate.' }
if ($TrustTestCertificate) {
    Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
}
if ((Get-AuthenticodeSignature -LiteralPath $package).Status -ne 'Valid') { throw 'Test certificate is not trusted. Rerun with -TrustTestCertificate to explicitly trust the exported local test certificate.' }
$log = Join-Path ([IO.Path]::GetDirectoryName($package)) 'install-testsigned.log'
$process = Start-Process msiexec.exe -ArgumentList @('/i',('"'+$package+'"'),'/qn','/norestart','/L*v',('"'+$log+'"')) -WindowStyle Hidden -Wait -PassThru
Write-Host "MSI installation exit code: $($process.ExitCode)"
if ($process.ExitCode -notin 0,3010) { throw "MSI installation failed. See $log" }
if ($process.ExitCode -eq 3010) { Write-Host 'Windows reports that a reboot is required. No reboot was started.' }
Write-Host 'Quit the old CamStudio tray application, then open C:\Program Files\CamStudio Pro\CamStudioPro.exe.'
