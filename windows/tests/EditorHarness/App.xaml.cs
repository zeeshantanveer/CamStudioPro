using System.IO;
using System.Windows;

namespace EditorHarness;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "harness-error.log"), $"{DateTime.Now:u} {e.Exception}\r\n");
            MessageBox.Show(e.Exception.Message, "Editor harness");
            e.Handled = true;
        };
    }

    public static void SetTheme(bool dark)
    {
        Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };
    }
}
