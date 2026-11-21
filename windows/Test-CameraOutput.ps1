# Read-only checks for camera output. Run while CamStudio Pro is open.
$ErrorActionPreference = "Stop"
$cameraClsid = "{3c1f6a52-8e0b-4b7d-9f2a-6d4e8c1b7a35}"
$cameraKey = "HKLM:\SOFTWARE\Classes\CLSID\$cameraClsid\InprocServer32"
$processes = @(Get-Process CamStudioPro -ErrorAction SilentlyContinue)
$packages = @(Get-AppxPackage *CamStudio* -ErrorAction SilentlyContinue)
$registration = Get-ItemProperty -LiteralPath $cameraKey -ErrorAction SilentlyContinue
$packagedRegistration = Get-Item -LiteralPath "HKLM:\SOFTWARE\Classes\PackagedCom\ClassIndex\$cameraClsid" -ErrorAction SilentlyContinue
$listeners = @(Get-NetTCPConnection -LocalPort 47802 -State Listen -ErrorAction SilentlyContinue)
$devices = @(Get-PnpDevice -PresentOnly -Class Camera -ErrorAction SilentlyContinue)

Write-Host "Installed CamStudio packages:"
if ($packages.Count) { $packages | Select-Object Name,Version,InstallLocation | Format-List } else { Write-Host "None for this Windows user." }
Write-Host "Running CamStudio processes:"
if ($processes.Count) { $processes | Select-Object Id,Path | Format-List } else { Write-Host "None. Open CamStudio Pro before checking camera output." }
Write-Host "Camera COM registration:"
if ($registration) {
    $server = $registration.'(default)'
    Write-Host $server
    Write-Host "Registered DLL exists: $(Test-Path -LiteralPath $server)"
} elseif ($packagedRegistration) { Write-Host "Packaged COM registration exists. Media Foundation activation determines whether it is usable." } else { Write-Host "No classic or packaged COM registration found." }
Write-Host "Camera frame listener (loopback TCP 47802):"
if ($listeners.Count) { $listeners | Select-Object LocalAddress,LocalPort,OwningProcess | Format-Table } else { Write-Host "Not listening." }
Write-Host "Present Windows camera devices:"
$devices | Select-Object Status,FriendlyName,InstanceId | Format-Table -AutoSize
Write-Host "This checks deployment and startup only. Confirm camera enumeration and live video in Zoom after installation."
