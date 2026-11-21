# Publishing CamStudio Pro (Windows) to the Microsoft Store

The Store build is an MSIX package (`.msix`), not a loose `.exe`. The Store installs, updates and
signs it. Build it with `windows\build-msix.ps1`.

## 1. One-time Partner Center setup

1. Create a developer account at <https://partner.microsoft.com/dashboard> (individual or company).
2. **Apps and games › New product › MSIX or PWA app**, then reserve the name (e.g. "CamStudio Pro").
3. Open **Product management › Product identity** and copy `windows/packaging/store-identity.example.json` to
   `windows/packaging/store-identity.json` (git-ignored) and fill in these values:

   | Partner Center field          | store-identity.json    |
   |-------------------------------|------------------------|
   | Package/Identity/Name         | `IdentityName`         |
   | Package/Identity/Publisher    | `Publisher` (`CN=…`)   |
   | Package/Properties/PublisherDisplayName | `PublisherDisplayName` |
   | Reserved app name             | `DisplayName`          |

## 2. Build

```powershell
cd windows
.\build-msix.ps1 -Version 1.1.0.0
# → windows\dist\msix\CamStudioPro_1.1.0.0_x64.msix
```

- Every upload needs a higher version. The 4th number must stay `0`.
- The app is self-contained, so users don't install .NET for the app itself.
- Optional: `-FfmpegDir <folder>` bundles `ffmpeg.exe` and `ffprobe.exe` for the editor (see §5).

### Test locally before uploading

```powershell
.\build-msix.ps1 -Version 1.1.3.0 -Sign -DeveloperTest  # local test SCCD; Developer Mode required
# as administrator, once:
Import-Certificate -FilePath dist\msix\CamStudioPro-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage dist\msix\CamStudioPro_1.1.3.0_x64_DeveloperTest.msix
```

Quit any unpackaged copy first (tray › Quit), because both use the same single-instance lock.
Check these: the app starts, phones connect over Wi-Fi with no firewall prompt, **Settings ›
Start with Windows** works (it appears in Windows Settings › Apps › Startup), recording works,
and the editor exports video and audio.

Run the Windows App Certification Kit as administrator. These are the same checks the Store runs:

```powershell
& "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe" test `
   -appxpackagepath dist\msix\CamStudioPro_1.1.0.0_x64.msix -reportoutputpath dist\msix\wack.xml
```

Remove the test install with `Get-AppxPackage *CamStudioPro* | Remove-AppxPackage`.

## 3. Submission

- **Packages:** upload the unsigned `.msix` (the build without `-Sign`).
- **Restricted capability `runFullTrust`:** in the submission's *Submission options* explain:
  > CamStudio Pro is a WPF desktop application. It needs full trust to run as a desktop app,
  > to receive the phone's video stream over the local network, and to run FFmpeg for
  > recording, editing and export.
- **Notes for certification:** testers need an Android phone running the CamStudio Pro app.
  Without one, they can check the editor, settings and theme.
- **Privacy policy URL:** required, because the app receives camera and microphone data over the
  network. State that video and audio stay on the local network and nothing is uploaded.
- **Store listing:** description, at least 1 screenshot (1366×768 or larger, both themes look
  good), and a 300×300 logo (`packaging/Assets/Square310x310Logo.scale-100.png` works as a base).
- **Age rating questionnaire**, **pricing**, and **markets**.
- **System requirements:** Windows 11 (the package's MinVersion is 10.0.22000).

## 4. FFmpeg (video and audio editor)

The editor runs `ffmpeg.exe`/`ffprobe.exe`. It finds them in the package's `ffmpeg\` folder, next
to the exe, at a path the user picks, or on `PATH`.

- If you bundle FFmpeg (`-FfmpegDir`), use an **LGPL** build (no `--enable-gpl`,
  `--enable-nonfree`, x264 or x265). Ship its license, and provide the matching source, or a
  written offer for it, as the LGPL requires.
- With an LGPL build the editor's **Auto** encoder uses Windows' built-in Media Foundation H.264
  encoder (`h264_mf`) or NVIDIA/Intel/AMD hardware encoders. The x264 encoder is only available
  with GPL builds.
- Patent licensing for H.264/HEVC encoding is the publisher's responsibility. Media Foundation
  and hardware encoders are covered by Windows and the GPU drivers.

## Store-only camera deployment candidate

The package declares classic, machine-scoped `com4:InProcessServer` registration for
`VCam/CamStudio.VCam.Native.dll` and these custom capabilities:

- `Microsoft.classicAppCompat_8wekyb3d8bbwe`
- `Microsoft.classicAppCompatElevated_8wekyb3d8bbwe`

Windows package deployment owns registration, replacement and removal. The app starts
the session camera as the current user on a background thread. The packaged app never
runs regsvr32. Camera permission is declared through the webcam device capability.

This is a qualification candidate, not a validated Store camera release. Machine scope
is necessary for the Local Service Frame Server; Microsoft documents restrictions on
loading in-process servers outside ordinary MSIX packages. Approval alone does not prove
activation works. Test activation under the actual Frame Server service before release.

### Approval and identity

Fill `store-identity.json` from Partner Center, then use CAMERA-APPROVAL.md as the request
text for both custom capabilities. Request guidance specifically on machine-wide in-process
camera activation from an ordinary Store MSIX. Do not send the placeholder development
identity as the production identity. No approval request is sent automatically.

If Microsoft issues signed custom capability descriptors for this identity, include the
original files without modification:

```powershell
.\build-msix.ps1 -Version 1.1.1.0 -Sign -CapabilityDescriptorDir C:\approved-descriptors
```

The script copies `.sccd` files to the package root. Supplying this folder does not
verify signatures or grant approval; deployment and Microsoft approval remain separate.
Without it the script builds a candidate and prints a warning. Development-mode testing
of custom capabilities does not establish that Store installation is approved.

### Native camera runtime

The camera source now uses native C++ with `/MT` (statically linked C++ runtime).
The package includes only `VCam/CamStudio.VCam.Native.dll`, not the old managed COM host,
its runtimeconfig, or DirectN assemblies. No machine-installed .NET or Visual C++ runtime
is required for this source. The desktop executable remains self-contained.

Building requires Visual Studio 2022 Build Tools with Desktop development with C++,
a Windows 11 SDK, and the .NET 10 SDK. `build-native-camera.ps1` finds MSBuild with
vswhere and restores the pinned WIL header dependency. Plain `dotnet run` also builds
and copies the native source; it still does not install MSIX registrations.

### Validation

Run `./Test-NativeCamera.ps1` from `windows` for repeatable native COM and desktop transport
checks (no administrator rights or registration required). It covers all supported resolutions,
black fallback, pause, source stop/shutdown, COM object lifetime and absence of CLR loading.


`dotnet run --project src\CamStudio.Desktop -c Release -p:Platform=x64` runs an unpackaged
app. It does not test MSIX install or camera registration. After approval/development-mode
setup and local signing, install the MSIX, quit loose developer copies, then launch the
installed app from Start. Do not call regsvr32 manually during package qualification.

1. Check the registered source points inside the installed package and is readable by Frame Server.
2. Run `./Test-CameraOutput.ps1` while the packaged app is open. Confirm registration and port 47802.
3. Restart Zoom, verify CamStudio Pro Windows Virtual Camera is enumerated and delivers moving phone video.
4. Check permission denied, pause, disconnect, reconnect, and exiting the app.
5. Install a higher-version package, repeat camera capture, then uninstall and verify COM cleanup.
6. Repeat on a clean Windows 11 VM without SDK or globally installed .NET; confirm the camera works without installing any runtime.

References:
- https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations
- https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-com4-extension
- https://learn.microsoft.com/en-us/dotnet/core/native-interop/expose-components-to-com

### Missing windows.capability descriptor (0x80070002)

The deployment log may show `CC file pattern ...\*.sccd` followed by error 0x2.
The package is missing its custom capability descriptor. Trusting the signing certificate
alone does not fix this. Build with `-Sign -DeveloperTest` for local development, enable
Developer Mode in Windows Settings, and install the resulting `_DeveloperTest.msix`.
The test descriptor uses Microsoft's documented Catalog FFFF and placeholder signature hash.
It is not signed by Microsoft, does not grant production approval, and must not be submitted
to the Store. DeveloperTest and CapabilityDescriptorDir are mutually exclusive. A signed
build without either descriptor source now fails early with an actionable error.

Reference: https://learn.microsoft.com/en-us/windows-hardware/drivers/devapps/hardware-support-app--hsa--steps-for-app-developers

### Installed package activation result (1.1.3.0)

Successful installation and presence in PackagedCom do not imply COM activation works.
On build 26300.9974, a native probe under the installed package identity returned
0x80040154 from both CoCreateInstance(CLSCTX_INPROC_SERVER) and IMFVirtualCamera::Start.
The registered in-process class remains unusable through this package deployment route.
See CAMERA-APPROVAL.md for exact observations to include in Microsoft's support request.
The app no longer rejects a packaged build solely because the classic CLSID hive is absent;
it lets Media Foundation return the actual activation result. Do not claim this change fixes
Zoom until installed-package activation and real capture pass.
