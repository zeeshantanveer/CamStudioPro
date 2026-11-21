using System.Runtime.InteropServices;

namespace CamStudio.Desktop.Platform;

/// <summary>Whether the app runs from an MSIX package (Microsoft Store / sideloaded) or as a plain exe.</summary>
public static class PackageInfo
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when Windows launched the packaged app from its startup task at sign-in.</summary>
    public static bool LaunchedAtStartup()
    {
        if (!IsPackaged)
            return false;
        try
        {
            var args = Windows.ApplicationModel.AppInstance.GetActivatedEventArgs();
            return args?.Kind == Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch
        {
            return false;
        }
    }
}
