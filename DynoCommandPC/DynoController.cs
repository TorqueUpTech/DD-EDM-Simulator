using System.Collections.Concurrent;
using System.IO;
using DynoCommandPC.Logging;
using DynoCommandPC.Protocol;
using DynoCommandPC.Transport;

namespace DynoCommandPC;

/// <summary>
/// Owns the active transport plus the TX loop (~20 Hz, mirroring the original
/// COM_Comms.txThread cadence) and the RX framer/parser. Plain fields are read
/// by the UI on a refresh timer — no per-packet cross-thread marshalling.
/// </summary>
public sealed class DynoController
{
    public CommandState Command { get; } = new();
    public DynoState State { get; } = new();

    private ITransport? _transport;
    private readonly FrameParser _rx = new();
    private System.Threading.Timer? _txTimer;
    private int _tick5Hz;

    // Handshake/identity replies the parser queues on the RX thread; flushed on
    // the TX thread so all writes to the transport stay serialized.
    private readonly ConcurrentQueue<CanPacket> _responses = new();

    // Loss-of-comms tally (dropouts after a successful connect) + optional file log.
    public long CommsLossEvents { get; private set; }
    public bool CommsLogEnabled;
    private readonly CommsLogger _commsLog = new();
    private bool _commsActive;
    private bool _commsEverUp;
    private static readonly TimeSpan CommsTimeout = TimeSpan.FromMilliseconds(1500);
    public string? CommsLogPath => _commsLog.FilePath;

    public long TxPackets;
    public long RxPackets;
    public long RxErrors => _rx.Errors;
    public bool IsConnected => _transport?.IsOpen ?? false;
    public string TransportName => _transport?.Description ?? "—";

    public string LastTxHex = "—";
    public string LastRxHex = "—";

    public bool LogEnabled;
    private readonly ConcurrentQueue<string> _log = new();

    // Raw inbound-byte capture (BEFORE framing) — for confirming/decoding an unknown
    // wire format (e.g. the DynoServer TCP stream). Rolling buffer + optional file.
    public bool RawCaptureEnabled { get; private set; }
    private readonly ConcurrentQueue<string> _rawLog = new();
    private StreamWriter? _rawFile;

    // Live inventory of every distinct packet type seen (ID/CMD/Payload0), incl.
    // ones we don't decode — used to discover extra data (air temp, power, …).
    public sealed class PacketStat
    {
        public long Count;
        public int Last;
        public int Min = int.MaxValue;
        public int Max = int.MinValue;
        public string LastHex = "";
    }
    private readonly ConcurrentDictionary<int, PacketStat> _stats = new();
    public IReadOnlyDictionary<int, PacketStat> Stats => _stats;
    public void ClearStats() => _stats.Clear();

    public DynoController() => _rx.PacketReceived += OnPacket;

    public bool Connect(ITransport transport)
    {
        Disconnect();
        _transport = transport;
        _transport.DataReceived += OnData;
        if (!_transport.Open())
        {
            _transport.DataReceived -= OnData;
            _transport = null;
            return false;
        }
        TxPackets = 0;
        RxPackets = 0;
        CommsLossEvents = 0;
        _commsActive = false;
        _commsEverUp = false;
        if (CommsLogEnabled)
        {
            _commsLog.Start();
            _commsLog.Log($"CONNECTED — {TransportName}");
        }
        _txTimer = new System.Threading.Timer(TxTick, null, 0, 50);
        return true;
    }

    /// <summary>Enable/disable comms-event file logging mid-session.</summary>
    public void SetCommsLogging(bool enabled)
    {
        CommsLogEnabled = enabled;
        if (enabled)
        {
            if (IsConnected && !_commsLog.IsOpen)
            {
                _commsLog.Start();
                _commsLog.Log($"COMMS LOGGING ENABLED — {TransportName}");
            }
        }
        else if (_commsLog.IsOpen)
        {
            _commsLog.Log("COMMS LOGGING DISABLED");
            _commsLog.Stop();
        }
    }

    // Detect comms dropouts (no valid RX within the timeout, after data had been
    // flowing). Each fresh->stale transition increments the loss tally and logs it.
    private void TrackComms(DateTime nowUtc)
    {
        bool fresh = State.LastRxUtc != DateTime.MinValue && (nowUtc - State.LastRxUtc) < CommsTimeout;
        if (fresh)
        {
            if (!_commsActive)
            {
                _commsActive = true;
                if (_commsEverUp) _commsLog.Log("COMMS RESTORED");
                else { _commsEverUp = true; _commsLog.Log("COMMS UP — first data received"); }
            }
        }
        else if (_commsActive && _commsEverUp)
        {
            _commsActive = false;
            CommsLossEvents++;
            _commsLog.Log($"COMMS LOST (#{CommsLossEvents})");
        }
    }

    public void Disconnect()
    {
        _txTimer?.Dispose();
        _txTimer = null;
        if (_transport != null)
        {
            _transport.DataReceived -= OnData;
            _transport.Close();
            _transport.Dispose();
            _transport = null;
        }
        if (_commsLog.IsOpen)
        {
            _commsLog.Log("DISCONNECTED");
            _commsLog.Stop();
        }
        while (_responses.TryDequeue(out _)) { }
        if (_rawFile != null) { try { _rawFile.Dispose(); } catch { } _rawFile = null; }
        RawCaptureEnabled = false;
        State.ResetForDisconnect();
    }

    private void OnData(byte[] data)
    {
        if (RawCaptureEnabled) CaptureRaw(data);
        _rx.Feed(data, data.Length);
    }

    private void CaptureRaw(byte[] data)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff}  RX[{data.Length,4}]  {Convert.ToHexString(data)}";
        _rawLog.Enqueue(line);
        while (_rawLog.Count > 5000) _rawLog.TryDequeue(out _);
        try { _rawFile?.WriteLine(line); } catch { }
    }

    /// <summary>Enable/disable raw inbound-byte capture (rolling buffer + logs\dyno-raw-*.log).</summary>
    public void SetRawCapture(bool enabled)
    {
        RawCaptureEnabled = enabled;
        if (enabled && _rawFile == null)
        {
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                _rawFile = new StreamWriter(Path.Combine(dir, $"dyno-raw-{DateTime.Now:yyyyMMdd-HHmmss}.log"), false) { AutoFlush = true };
            }
            catch { }
        }
        else if (!enabled && _rawFile != null)
        {
            try { _rawFile.Dispose(); } catch { }
            _rawFile = null;
        }
    }

    public string[] RawSnapshot() => _rawLog.ToArray();

    private void OnPacket(CanPacket p)
    {
        RxPackets++;
        LastRxHex = p.ToHex();

        int key = (p.Id << 16) | (p.Cmd << 8) | p.Payload[0];
        var st = _stats.GetOrAdd(key, _ => new PacketStat());
        st.Count++;
        st.Last = p.Value16;
        if (p.Value16 < st.Min) st.Min = p.Value16;
        if (p.Value16 > st.Max) st.Max = p.Value16;
        st.LastHex = LastRxHex;

        PacketParser.Process(p, State, Command, _responses.Enqueue);
        AppendLog("RX", LastRxHex);
    }

    private void Send(CanPacket? p)
    {
        if (p == null || _transport == null) return;
        byte[] frame = PacketFramer.Wrap(p);
        _transport.Write(frame, frame.Length);
        TxPackets++;
        LastTxHex = p.ToHex();
        AppendLog("TX", LastTxHex);
    }

    /// <summary>
    /// Safety governor: the commanded speed (TargetSpeed, sent by the knob) is
    /// leashed to within ±10 km/h of the actual dyno speed, and may not be
    /// commanded to 0 while the dyno is doing ≥5 km/h. The operator's request
    /// (RequestedSpeed) is fed in gradually as the dyno catches up. Gated to road
    /// speed control (kph/mph); engine/rpm passes through unchanged.
    /// </summary>
    private void Govern()
    {
        var c = Command;
        if (c.SpeedControl == SpeedControl.Engine || c.SpeedUnits == SpeedUnit.Rpm)
        {
            c.TargetSpeed = c.RequestedSpeed;
            return;
        }

        double reqKph = c.ToKph(c.RequestedSpeed);
        double actKph = State.SpeedKph;
        double floor = actKph < 5.0 ? 0.0 : 5.0;   // no stop command unless nearly stopped
        floor = Math.Max(floor, actKph - 10.0);     // don't command >10 km/h below actual
        double ceil = actKph + 10.0;                // don't command >10 km/h above actual
        double cmdKph = Math.Clamp(reqKph, floor, ceil);
        c.TargetSpeed = c.FromKph(cmdKph);
    }

    private void TxTick(object? _)
    {
        try
        {
            // Flush any handshake/identity replies the parser queued, then run the
            // comms-loss fail-safe (zeroes stale speed, updates device-online flags).
            while (_responses.TryDequeue(out var resp)) Send(resp);
            var nowUtc = DateTime.UtcNow;
            State.UpdateWatchdog(nowUtc);
            TrackComms(nowUtc);

            // Mirror PC-controlled shootout state into the command side so the
            // knob sends the PC's ramp rate during a shootout.
            Command.ShootoutActive = State.ShootoutActive;
            Command.PcRampRate = State.PcRampRate;

            Govern();
            Send(PacketFactory.Knob(Command));
            Send(PacketFactory.DigitalInputs(Command));

            if (Command.Mode == DynoMode.SetBed)
                Send(PacketFactory.RequestedBedPosition(Command));

            if (++_tick5Hz >= 4)
            {
                _tick5Hz = 0;
                if (Command.Is4Wd) Send(PacketFactory.Balance(Command));
                Send(PacketFactory.Ping());
                Send(PacketFactory.HfrMode(Command));
                Send(PacketFactory.DynoModePacket(Command));
            }
        }
        catch { /* keep the loop alive */ }
    }

    private void AppendLog(string dir, string hex)
    {
        if (!LogEnabled) return;
        _log.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {dir}  {hex}");
        while (_log.Count > 20000) _log.TryDequeue(out _);
    }

    public string[] LogSnapshot() => _log.ToArray();
    public void ClearLog() { while (_log.TryDequeue(out _)) { } }
}
