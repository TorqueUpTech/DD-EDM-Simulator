using System.Windows;
using System.Windows.Threading;
using DynoCommandPC.Server;
using Microsoft.Win32;

namespace DynoCommandPC;

/// <summary>
/// UI for the EDM Simulator server — pick a captured log, start a Bluetooth-SPP
/// listener, and replay the dyno-&gt;handheld stream to a connecting handheld
/// (e.g. the Android app pointed at this PC's Bluetooth address).
/// </summary>
public partial class EdmServerWindow : Window
{
    private readonly EdmSimServer _server = new();
    private readonly DispatcherTimer _timer;
    private string? _logPath;

    public EdmServerWindow()
    {
        InitializeComponent();
        TxtAddr.Text = EdmSimServer.LocalRadioAddress();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); _server.Stop(); };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a captured dyno log",
            Filter = "Dyno capture (*.txt;*.log;*.csv)|*.txt;*.log;*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _logPath = dlg.FileName;
            TxtLog.Text = _logPath;
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_logPath))
        {
            MessageBox.Show("Pick a datalog to replay first.", "EDM Simulator");
            return;
        }
        try
        {
            var frames = EdmReplaySource.Load(_logPath);
            if (frames.Count == 0)
            {
                MessageBox.Show("No RX (dyno->handheld) frames found in that log.", "EDM Simulator");
                return;
            }
            _server.Start(frames);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to start: " + ex.Message, "EDM Simulator");
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _server.Stop();

    private void Refresh()
    {
        if (_server.LocalAddress != "—") TxtAddr.Text = _server.LocalAddress;
        TxtStatus.Text = _server.Status + (_server.LastError is { } err ? "   —   " + err : "");
        TxtStats.Text =
            $"listening: {_server.Listening}     handheld: {(_server.ClientConnected ? "CONNECTED" : "—")}\n" +
            $"frames sent: {_server.FramesSent}     bytes from handheld: {_server.BytesFromClient}";
    }
}
