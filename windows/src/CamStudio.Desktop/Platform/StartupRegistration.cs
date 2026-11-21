using Microsoft.Win32;
using Windows.ApplicationModel;

namespace CamStudio.Desktop.Platform;

/// <summary>
/// "Start with Windows". Packaged (Microsoft Store) builds use the manifest's StartupTask, which the
/// user controls in Settings › Apps › Startup. Unpackaged builds use a per-user HKCU\...\Run entry.
/// Either way it launches the app hidden in the notification area. No administrator rights needed.
/// </summary>
public static class StartupRegistration
{
    /// <summary>Must match the TaskId in windows/packaging/Package.appxmanifest.</summary>
    public const string TaskId = "CamStudioProStartup";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CamStudioPro";
    // Task Manager › Startup apps writes here when the user disables an entry.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public sealed record State(bool Enabled, bool CanChange, string? Note);

    public static async Task<State> GetAsync()
    {
        if (PackageInfo.IsPackaged)
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                return task.State switch
                {
                    StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => new(true, task.State == StartupTaskState.Enabled, null),
                    StartupTaskState.DisabledByUser => new(false, false, "Turned off in Windows Settings › Apps › Startup."),
                    StartupTaskState.DisabledByPolicy => new(false, false, "Turned off by your organization."),
                    _ => new(false, true, null),
                };
            }
            catch (Exception e)
            {
                App.Log(e);
                return new(false, false, "Startup isn't available for this installation.");
            }
        }
        return new(IsRunKeyEnabled(), true, null);
    }

    public static async Task<State> SetAsync(bool enabled)
    {
        if (PackageInfo.IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
                await task.RequestEnableAsync();
            else
                task.Disable();
            return await GetAsync();
        }

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled)
            {
                run.SetValue(ValueName, Command);
                // Clear a "disabled" mark left by Task Manager so the entry actually runs.
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        return await GetAsync();
    }

    private static bool IsRunKeyEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string)
                return false;
            // StartupApproved: first byte 0x02/0x06 = enabled, 0x03/0x07 = disabled by the user.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Points an existing Run entry at this exe, e.g. after the folder moved. Unpackaged only.</summary>
    public static void RefreshPath()
    {
        if (PackageInfo.IsPackaged)
            return;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                run.SetValue(ValueName, Command);
        }
        catch
        {
        }
    }
}
