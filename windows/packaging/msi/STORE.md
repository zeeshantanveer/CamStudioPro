# CamStudio Pro: MSI listed in Microsoft Store

One installer contains the self-contained WPF desktop app and native virtual camera. It installs
to C:\Program Files\CamStudio Pro for all users. Administrator permission is required at setup,
not during normal camera use. No OBS, separate camera installer, Developer Mode, SCCD, or
MSIX custom capability approval is needed for this route. Normal Store certification applies.

## Build and local test

Prerequisites: .NET 10 SDK, VS 2022 C++ Build Tools, Windows 11 SDK, internet for build tools.
The script restores pinned WiX 4.0.6, its UI/firewall extensions, and the native WIL dependency.
End users do not need these tools or an installed .NET runtime. Setup downloads nothing.

From the repository root:

```powershell
.\windows\build-msi.ps1 -Version 1.2.2
```

Output: windows\dist\msi\CamStudioPro_1.2.2_x64.msi (unsigned local candidate).
For a self-signed local installer, add `-TestSign`. This generates/reuses a code-signing test
certificate in CurrentUser\My, exports only the public certificate to
`dist\msi\CamStudioPro-LocalTest.cer`, and produces `_TestSigned.msi`.
It does not grant public trust or qualify the installer for Store submission.
No signing key is exported. Test signing does not contact a timestamp service.
To trust it on a test machine, use Administrator PowerShell:

```powershell
Import-Certificate -FilePath .\windows\dist\msi\CamStudioPro-LocalTest.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

`-TestSign` cannot be combined with `-Release` or a production certificate thumbprint.
Close CamStudio Pro using its tray menu before installing an update or switching editions.
In Administrator PowerShell:

```powershell
msiexec.exe /i ".\windows\dist\msi\CamStudioPro_1.2.2_x64.msi" /qn /norestart /L*v "$env:TEMP\CamStudio-install.log"
```

A silent install shows no completion dialog. Success codes are 0 (complete) or 3010 (reboot required).
For interactive installation, double-click the MSI. It uses a minimal setup UI.
Launch the MSI copy explicitly if an older MSIX remains installed:

```powershell
Start-Process "C:\Program Files\CamStudio Pro\CamStudioPro.exe"
```

Only one Release copy can run per Windows session. Quit the old MSIX or developer tray app
before launching this path. The MSI does not remove old MSIX packages or delete their data.
After migration, the old MSIX can be removed through Windows Settings; retain recordings first.

Connect the phone, wait for preview, then restart Zoom and choose
CamStudio Pro Windows Virtual Camera. Keep CamStudio Pro running during the meeting.
The virtual camera is session scoped and disappears after the app exits. Microphone selection
remains separate; installing this MSI does not create a virtual microphone.

## Installer behavior

- Stable UpgradeCode and stable per-path component identity; newer versions replace older MSI versions.
- Downgrades are blocked. MSI version changes must increase the first three numeric parts.
- Camera registration uses declarative 64-bit HKLM InprocServer32 + ThreadingModel=Both entries.
- Registry changes, files, and private/local-subnet phone firewall rules participate in MSI rollback.
- Repair restores registration; uninstall removes installed files, shortcut, camera registry values and installer-owned firewall rules.
- Recordings and user settings are retained. Optional per-user startup settings are not enabled by setup.
- Restart Manager handles files in use; close the app before updates/uninstall. Do not force a reboot.

Repair and uninstall (Administrator PowerShell):

```powershell
msiexec.exe /fomus ".\windows\dist\msi\CamStudioPro_1.2.2_x64.msi" /qn /norestart /L*v "$env:TEMP\CamStudio-repair.log"
msiexec.exe /x ".\windows\dist\msi\CamStudioPro_1.2.2_x64.msi" /qn /norestart /L*v "$env:TEMP\CamStudio-uninstall.log"
```

## Verification

`windows\Test-NativeCamera.ps1` tests native COM/frame transport without registration.
After MSI installation, `windows\Test-NativeCamera.ps1 -RegisteredCapture` additionally
creates the registered virtual camera and reads three pixel-checked NV12 samples through
Windows Frame Server and Media Foundation Source Reader. It uses synthetic frames; it does
not record the phone or establish Zoom interoperability by itself. Keep other camera output
sessions closed for this test (it uses the camera registration and loopback port 47802).

Local results: MSI installation succeeded; traditional COM activation, virtual camera Start,
Frame Server GetMediaSource and three real camera-pipeline samples passed. Direct native tests
also cover 1080p, 720p and 360p. Actual Zoom UI selection with the phone remains a user check.
MSI repair and uninstall passed (including registry/file/firewall cleanup). Upgrade from 1.2.0 to 1.2.1 passed; downgrade to 1.2.0 was rejected. The self-signed 1.2.2 test candidate was then installed successfully over 1.2.1. A clean Windows VM and actual phone video in Zoom still need release qualification.

## Signing and Store submission

The MSIX self-signed test certificate does not qualify for this route. Obtain a publisher
code-signing certificate from a CA trusted by the Microsoft Trusted Root Program, or use a
supported managed signing service. Do not send private keys or certificate passwords in chat.
The MSI and every embedded PE file (.exe/.dll, including unsigned dependencies) must be signed.
The build preserves existing valid vendor signatures and signs unsigned files before embedding.

For an available signing certificate in CurrentUser\My:

```powershell
.\windows\build-msi.ps1 -Version 1.2.2 -Release -SigningCertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT
```

Release mode rejects self-signed leaf certificates and checks payload/MSI signatures. Verify
that your issuer is publicly trusted: a locally trusted private CA is not sufficient for Store
submission even if Windows reports its signature as Valid. A hardware/managed signing service
may need a provider-specific signing integration instead of the thumbprint command above.

The owner provided deepsoll.com. The following URL is proposed; no file has been uploaded.
Host the final signed installer at an immutable, directly downloadable versioned HTTPS URL,
for example https://deepsoll.com/downloads/camstudiopro/1.2.2/CamStudioPro_1.2.2_x64.msi.
Do not upload a local unsigned MSI or DeveloperTest MSIX as the production installer.

In Partner Center, create/select the Windows MSI/EXE app submission route and supply that URL
and silent install parameters `/qn /norestart`. Do not put an MSI into an MSIX package upload.
If the existing product offers only MSIX/PWA packages, check Partner Center's product-type
options or support before reserving a second listing. Provide Windows 11 x64 requirements,
privacy policy, screenshots, and accurate app details. Store review still applies.

Source: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements

## Current local test artifact

The current candidate is `CamStudioPro_1.2.2_x64_TestSigned.msi`, signed with
CN=CamStudio Pro Local Test. It was installed locally and its application and native camera
signatures validate against the locally trusted test certificate. It is not Store eligible.

For repeatable local installation, run `windows\Install-LocalTestMsi.ps1 -TrustTestCertificate`
in Administrator PowerShell. This helper verifies the MSI signer matches the exported
local test certificate before trusting it in LocalMachine\TrustedPeople. It never trusts an
arbitrary supplied certificate. The public certificate is exported; the private key stays in
the developer's CurrentUser certificate store.

An old MSIX may still be running. Quit its tray process before starting the MSI application
from `C:\Program Files\CamStudio Pro\CamStudioPro.exe`.

## HD/background feature update (1.3.0)

Build with `windows\build-msi.ps1 -Version 1.3.0 -TestSign`. The local-test installation
helper now defaults to `CamStudioPro_1.3.0_x64_TestSigned.msi`. Update the Android app to
1.1.0/versionCode2 using the signed `android/app/build/outputs/apk/release/app-release.apk`
as well. Reconnect after both updates, then use Live > Video & background. Older phone
apps intentionally leave these controls disabled. This update does not change camera COM
registration or require Developer Mode or MSIX custom capabilities.

## Live controls update (1.3.1)

The latest local MSI is `CamStudioPro_1.3.1_x64_TestSigned.msi`; the installation helper
now defaults to it. Pair with Android 1.1.1/versionCode3 for background-only changes
that preserve frame rate and resolution while streaming. End an active broadcast before
installing either app update. Once updated, changing a background does not end the stream.
Stream setup includes a separate viewer watch URL with Copy/Open while live; the application
cannot determine a YouTube broadcast ID from its RTMP key. Touch-up uses the phone's existing
beauty pipeline. Test installer signing and Store production-signing requirements are unchanged.
