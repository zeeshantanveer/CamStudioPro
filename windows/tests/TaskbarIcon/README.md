# Installed taskbar icon check

The old MSI advertised shortcut exposed an extensionless cached `CamStudioIcon` as its
shell target/icon. The corrected 1.3.2 MSI uses a direct executable shortcut, a cached
`CamStudioPro.ico`, and `System.AppUserModel.ID=ZeeshanTanveer.CamStudioPro.Desktop`.
The application sets the same process identity before creating UI.

Local installation on 2026-11-19 completed with Windows Installer exit code 0. Verified
the installed executable is 1.3.2, the Start menu shortcut targets the executable, its
icon reference has an `.ico` extension, and its shell AppUserModelID matches the app.
The relaunched main window returned a nonzero `WM_GETICON` handle; extracting that
handle produced the expected CamStudio yellow-circle icon in `output/running-icon.png`.
This check used the actual installed window, not a render of the source asset.

The local self-signed installer is `windows/dist/msi/CamStudioPro_1.3.2_x64_TestSigned.msi`.
