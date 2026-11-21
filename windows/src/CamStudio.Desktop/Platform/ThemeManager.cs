using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace CamStudio.Desktop.Platform;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Light/Dark theme. "System" (the default) follows Windows Settings › Personalization › Colors ›
/// "Choose your app mode" and updates live when it changes. Swaps the colour dictionary at
/// Application.Resources.MergedDictionaries[0]; every style uses DynamicResource, so open windows
/// restyle immediately. Also switches the native title bar between light and dark.
/// </summary>
public static class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;
    private static bool _hooked;

    public static AppTheme Mode { get; private set; } = AppTheme.System;
    public static bool IsDark { get; private set; } = true;
    public static event Action? Changed;

    public static AppTheme Parse(string? value) =>
        Enum.TryParse<AppTheme>(value, ignoreCase: true, out var mode) ? mode : AppTheme.System;

    public static void Initialize(AppTheme mode)
    {
        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color && Mode == AppTheme.System)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
            };
            // Every window (main, approval prompts, editor dialogs) gets a matching title bar.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => ApplyTitleBar((Window)s)));
        }
        Apply(mode);
    }

    public static void Apply(AppTheme mode)
    {
        Mode = mode;
        var dark = mode switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => SystemUsesDark(),
        };
        var app = Application.Current;
        if (app == null)
            return;
        IsDark = dark;
        var wanted = dark ? "Dark.xaml" : "Light.xaml";
        var current = app.Resources.MergedDictionaries.FirstOrDefault()?.Source?.OriginalString ?? "";
        if (!current.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
        {
            var colors = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/" + wanted) };
            if (app.Resources.MergedDictionaries.Count > 0)
                app.Resources.MergedDictionaries[0] = colors;
            else
                app.Resources.MergedDictionaries.Add(colors);
        }
        foreach (Window w in app.Windows)
            ApplyTitleBar(w);
        Changed?.Invoke();
    }

    public static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var value = IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
