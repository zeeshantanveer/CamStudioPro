using System.Windows;
using System.Windows.Threading;
using CamStudio.Desktop.Core;

namespace CamStudio.Desktop;

public partial class ApprovalWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining = 55;

    public bool Allowed { get; private set; }

    public ApprovalWindow(PhoneInfo phone)
    {
        InitializeComponent();
        PhoneName.Text = phone.Name;
        var via = phone.Transport == "usb" ? "USB cable" : "Wi‑Fi";
        PhoneDetail.Text = string.IsNullOrEmpty(phone.Model)
            ? $"via {via} · {phone.Address}"
            : $"{phone.Model} · via {via} · {phone.Address}";
        UpdateCountdown();
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            UpdateCountdown();
            if (_remaining <= 0)
                Close();
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateCountdown() => Countdown.Text = $"The request expires in {_remaining} s.";

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        Allowed = true;
        Close();
    }

    private void Deny_Click(object sender, RoutedEventArgs e) => Close();
}
