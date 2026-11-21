using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace CamStudio.Desktop;

public partial class App : Application
{
    private Mutex? _single;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _main;
    private bool _trayHintShown;
    private bool _quitting;
    private static EventWaitHandle? _activate;

    protected override void OnStartup(StartupEventArgs e)
    {
        Platform.TaskbarIdentity.Initialize();
        base.OnStartup(e);
#if DEBUG
        UiPreview = e.Args.Contains("--ui-preview");
#endif

#if DEBUG
        const string instance = "CamStudioPro.Debug"; // a debug build can run next to the installed app
#else
        const string instance = "CamStudioPro";
#endif
        _single = new Mutex(true, $@"Local\{instance}.Desktop", out var first);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{instance}.Activate");
        if (!first)
        {
            _activate.Set(); // ask the running instance to show itself
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            MessageBox.Show(args.Exception.Message, "CamStudio Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);

        // --theme light|dark|system and --page live|editor|settings override for this run only (testing, screenshots).
        string? Arg(string name) { var i = Array.IndexOf(e.Args, name); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
        Platform.ThemeManager.Initialize(Platform.ThemeManager.Parse(Arg("--theme") ?? Core.AppSettings.Load().Theme));
        _main = new MainWindow();
        if (Arg("--page") is string page)
            _main.ShowPage(page);
        SetupTray();

        var thread = new Thread(() =>
        {
            while (_activate.WaitOne())
                Dispatcher.BeginInvoke(ShowMain);
        }) { IsBackground = true };
        thread.Start();

        var minimized = !UiPreview && (e.Args.Contains("--minimized") || Platform.PackageInfo.LaunchedAtStartup() || _main.StartMinimized);
        if (minimized)
            _trayHintShown = true;
        else
            ShowMain();
    }

    private void SetupTray()
    {
        Icon icon;
        try
        {
            var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
            icon = new Icon(stream);
        }
        catch
        {
            icon = SystemIcons.Application;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open CamStudio Pro", null, (_, _) => ShowMain());
        menu.Items.Add("Disconnect phone", null, (_, _) => _main?.DisconnectPhone());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "CamStudio Pro",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    public void UpdateTray(string status)
    {
        if (_tray != null)
            _tray.Text = ("CamStudio Pro – " + status).Length > 63 ? "CamStudio Pro" : "CamStudio Pro – " + status;
    }

    public void ShowMain()
    {
        if (_main == null)
            return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
            _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void HiddenToTray()
    {
        if (_trayHintShown || _tray == null)
            return;
        _trayHintShown = true;
        _tray.ShowBalloonTip(4000, "CamStudio Pro is still running",
            "Phones can still connect and recordings keep going. Right-click the tray icon to quit.", Forms.ToolTipIcon.None);
    }

    public async void Quit()
    {
        if (_quitting) return;
        if (_main != null && !_main.ConfirmQuit()) return;
        _quitting = true;
        if (_main != null) await _main.ShutdownAsync();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Debug builds only: --ui-preview opens the window without camera/network services (screenshots, UI work).</summary>
    public static bool UiPreview { get; private set; }

    public static void Log(Exception? e)
    {
        if (e == null)
            return;
        try
        {
            Directory.CreateDirectory(Core.AppSettings.Folder);
            File.AppendAllText(Path.Combine(Core.AppSettings.Folder, "error.log"), $"{DateTime.Now:u} {e}\r\n\r\n");
        }
        catch
        {
        }
    }
}
