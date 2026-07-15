using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DynoCommandPC.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;

namespace DynoCommandPC.Server;

/// <summary>
/// Acts as the dyno's EDM over a <b>Bluetooth SPP server</b> using the native
/// WinRT RFCOMM stack (<see cref="RfcommServiceProvider"/> + StreamSocketListener,
/// PlainSocket = no pairing). Accepts a handheld connection — the Android app
/// pointed at this PC's Bluetooth address — and replays a captured dyno-&gt;handheld
/// stream (each frame wrapped with PacketFramer) in a loop, so the handheld lights
/// up without the physical dyno. The handheld's TX is drained and counted.
/// </summary>
public sealed class EdmSimServer
{
    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;
    private StreamSocket? _socket;
    private Thread? _replayThread;
    private volatile bool _running;
    private IReadOnlyList<EdmReplaySource.Frame> _frames = Array.Empty<EdmReplaySource.Frame>();

    public bool Listening { get; private set; }
    public bool ClientConnected { get; private set; }
    public long FramesSent { get; private set; }
    public long BytesFromClient { get; private set; }
    public string LocalAddress { get; private set; } = "—";
    public string Status { get; private set; } = "stopped";
    public string? LastError { get; private set; }

    public event Action? Changed;

    /// <summary>The local Bluetooth adapter address (what the handheld targets), or "—".</summary>
    public static string LocalRadioAddress()
    {
        try
        {
            var adapter = Task.Run(async () => await BluetoothAdapter.GetDefaultAsync()).GetAwaiter().GetResult();
            return adapter == null ? "—" : FormatAddr(adapter.BluetoothAddress);
        }
        catch { return "—"; }
    }

    public async void Start(IReadOnlyList<EdmReplaySource.Frame> frames)
    {
        Stop();
        _frames = frames;
        LastError = null;
        FramesSent = 0;
        BytesFromClient = 0;
        try
        {
            LocalAddress = LocalRadioAddress();
            _provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.SerialPort);

            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnection;
            // PlainSocket: unauthenticated/unencrypted — matches the EDM (no pairing).
            await _listener.BindServiceNameAsync(_provider.ServiceId.AsString(), SocketProtectionLevel.PlainSocket);

            _running = true;
            _provider.StartAdvertising(_listener, true); // radioDiscoverable
            Listening = true;
            Status = "listening — waiting for handheld";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "error";
            Stop();
        }
        Raise();
    }

    private void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        _socket = args.Socket;
        ClientConnected = true;
        Status = "handheld connected — replaying";
        Raise();
        _replayThread = new Thread(() => ReplayLoop(args.Socket)) { IsBackground = true, Name = "edm-sim" };
        _replayThread.Start();
    }

    private void ReplayLoop(StreamSocket socket)
    {
        try
        {
            using var outStream = socket.OutputStream.AsStreamForWrite();
            using var inStream = socket.InputStream.AsStreamForRead();
            var drain = new Thread(() => Drain(inStream)) { IsBackground = true };
            drain.Start();

            while (_running)
            {
                if (_frames.Count == 0) { Thread.Sleep(100); continue; }
                foreach (var f in _frames)
                {
                    if (!_running) break;
                    if (f.DelayMs > 0) Thread.Sleep(f.DelayMs);
                    byte[] wire = PacketFramer.Wrap(f.Packet);
                    outStream.Write(wire, 0, wire.Length);
                    outStream.Flush();
                    FramesSent++;
                    if ((FramesSent & 0x1F) == 0) Raise();
                }
            }
        }
        catch (Exception ex)
        {
            if (_running) LastError = ex.Message; // a handheld disconnect lands here too
        }
        finally
        {
            ClientConnected = false;
            try { _socket?.Dispose(); } catch { }
            _socket = null;
            if (_running) { Status = "listening — waiting for handheld"; Raise(); }
        }
    }

    private void Drain(Stream s)
    {
        var buf = new byte[512];
        try
        {
            int n;
            while (_running && (n = s.Read(buf, 0, buf.Length)) > 0)
                BytesFromClient += n;
        }
        catch { /* connection closed */ }
    }

    public void Stop()
    {
        _running = false;
        try { _provider?.StopAdvertising(); } catch { }
        try { _listener?.Dispose(); } catch { }
        try { _socket?.Dispose(); } catch { }
        _provider = null;
        _listener = null;
        _socket = null;
        Listening = false;
        ClientConnected = false;
        if (Status != "error") Status = "stopped";
        Raise();
    }

    private void Raise() => Changed?.Invoke();

    // 48-bit BluetoothAddress -> "XX:XX:XX:XX:XX:XX".
    private static string FormatAddr(ulong addr)
    {
        var b = BitConverter.GetBytes(addr); // little-endian
        return string.Join(":", new[] { b[5], b[4], b[3], b[2], b[1], b[0] }.Select(x => x.ToString("X2")));
    }
}
