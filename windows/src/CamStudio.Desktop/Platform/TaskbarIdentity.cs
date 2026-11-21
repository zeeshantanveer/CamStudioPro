using System.Runtime.InteropServices;

namespace CamStudio.Desktop.Platform;

internal static class TaskbarIdentity
{
    // Match the MSI shortcut; keep this stable across application upgrades.
    private const string AppId = "ZeeshanTanveer.CamStudioPro.Desktop";

    internal static void Initialize()
    {
        var result = SetCurrentProcessExplicitAppUserModelID(AppId);
        if (result < 0)
            App.Log(Marshal.GetExceptionForHR(result));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
