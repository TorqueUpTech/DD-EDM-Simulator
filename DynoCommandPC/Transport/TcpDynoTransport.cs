using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DynoCommandPC.Transport;

/// <summary>
/// Connects to the dyno over TCP/IP via the "Dyno Dynamics" <b>DynoServer</b> relay,
/// as an alternative to the direct Bluetooth EDM link. Handshake (reverse-engineered
/// from Server/DynoServer — Discovery.cs / Registration.cs / ServerConnection.cs):
///   1. UDP discovery — broadcast a 52-byte "Dyno Dynamics" cmd=1 packet to ports
///      52123/59748/63123; the server replies (54 bytes, cmd=2) with an allocated TCP port.
///   2. TCP connect to that port; register: request the producer list (cmd 4 → 5), then
///      attach to the dyno producer as a <b>Controller</b> consumer (cmd 6 → 7).
///   3. The socket then becomes a transparent byte relay of the dyno's stream (same bytes
///      we'd see over Bluetooth — the server does not reframe).
/// Wire messages are length-prefixed: [u16 len][u32 cmd][u16 ver][payload], network order.
/// </summary>
public sealed class TcpDynoTransport : ITransport
{
    private static readonly int[] DiscoveryPorts = { 52123, 59748, 63123 };
    private const string SyncString = "Dyno Dynamics";
    private const int DiscoveryTimeoutMs = 2000;

    private readonly string _serverAddress; // "" / null => UDP broadcast discovery
    private readonly bool _listenerOnly;    // true => attach read-only (mode 1), no TX
    private Socket? _sock;
    private Thread? _reader;
    private volatile bool _running;

    public event Action<byte[]>? DataReceived;
    public bool IsOpen { get; private set; }
    public string Description { get; private set; } = "TCP DynoServer";
    public string? LastError { get; private set; }

    public TcpDynoTransport(string? serverAddress, bool listenerOnly = false)
    {
        _serverAddress = serverAddress ?? "";
        _listenerOnly = listenerOnly;
    }

    public bool Open()
    {
        try
        {
            LastError = null;
            if (!Discover(out IPAddress serverIp, out int tcpPort))
            {
                LastError ??= "no DynoServer discovery reply (is the server running on this network?)";
                return false;
            }

            _sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true, ReceiveTimeout = 8000, SendTimeout = 8000
            };
            _sock.Connect(new IPEndPoint(serverIp, tcpPort));

            if (!Register())
            {
                LastError ??= "DynoServer registration failed (no dyno producer connected?)";
                Close();
                return false;
            }

            _sock.ReceiveTimeout = 0; // relay is long-lived
            Description = $"TCP {serverIp}:{tcpPort}{(_listenerOnly ? " (listener)" : "")}";
            _running = true;
            IsOpen = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "tcp-dyno" };
            _reader.Start();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Close();
            return false;
        }
    }

    // ---- UDP discovery ----
    private bool Discover(out IPAddress serverIp, out int tcpPort)
    {
        serverIp = IPAddress.None;
        tcpPort = 0;

        using var udp = new UdpClient { EnableBroadcast = true };
        udp.Client.ReceiveTimeout = DiscoveryTimeoutMs;

        var req = new byte[52];
        Encoding.ASCII.GetBytes(SyncString).CopyTo(req, 0);      // [0..12] sync
        WriteNetI32(req, 14, 1);                                 // [14..17] command = 1
        WriteNetU16(req, 18, 0);                                 // [18..19] version = 0
        Encoding.ASCII.GetBytes("DynoCommandPC").CopyTo(req, 20);// [20..51] client identifier

        IPAddress target = IPAddress.Broadcast;
        if (!string.IsNullOrWhiteSpace(_serverAddress) && IPAddress.TryParse(_serverAddress, out var parsed))
            target = parsed;

        foreach (int port in DiscoveryPorts)
            try { udp.Send(req, req.Length, new IPEndPoint(target, port)); } catch { }

        var deadline = DateTime.UtcNow.AddMilliseconds(DiscoveryTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] reply = udp.Receive(ref remote);
                if (reply.Length == 54 &&
                    Encoding.ASCII.GetString(reply, 0, 13) == SyncString &&
                    ReadNetI32(reply, 14) == 2)
                {
                    tcpPort = ReadNetU16(reply, 52);
                    serverIp = remote.Address;
                    return true;
                }
            }
            catch (SocketException) { break; } // timeout
        }
        return false;
    }

    // ---- TCP registration ----
    private bool Register()
    {
        // Request the producer list (cmd 4).
        Send(BuildHeader(8, 4));
        byte[]? list = ReadMessage();
        if (list == null || ReadNetI32(list, 2) != 5) return false;

        ushort dynoHandle = PickDynoHandle(list);
        if (dynoHandle == 0) return false;

        // Register as a Controller consumer of that producer (cmd 6).
        var reg = new byte[13];
        WriteNetU16(reg, 0, 13);   // length
        WriteNetI32(reg, 2, 6);    // command = REG_REQUEST_CONSUMER
        WriteNetU16(reg, 6, 0);    // version
        WriteNetU16(reg, 8, 1);    // number of producers
        WriteNetU16(reg, 10, dynoHandle);
        reg[12] = _listenerOnly ? (byte)1 : (byte)2; // 1 = Listener (read-only), 2 = Controller (read/write)
        Send(reg);

        byte[]? reply = ReadMessage();
        if (reply == null || ReadNetI32(reply, 2) != 7) return false;
        // Per-producer status byte at [12]: 0 = OK, 1 = fail.
        return reply.Length >= 13 && reply[12] == 0;
    }

    // Parse the producer-list reply (cmd 5): pick the first dyno producer's handle.
    // Block per producer: [u16 handle][u16 idLen][idLen id][32 identifier][1 hasController].
    private static ushort PickDynoHandle(byte[] msg)
    {
        try
        {
            int n = ReadNetU16(msg, 8);
            int off = 10;
            ushort firstHandle = 0;
            for (int i = 0; i < n && off + 4 <= msg.Length; i++)
            {
                ushort handle = ReadNetU16(msg, off); off += 2;
                int idLen = ReadNetU16(msg, off); off += 2;
                if (off + idLen + 33 > msg.Length) break;
                if (firstHandle == 0) firstHandle = handle;
                // clientID type lives at id[9] (network i16); IsDyno = 0..8.
                if (idLen >= 11)
                {
                    short idType = (short)ReadNetU16(msg, off + 9);
                    if (idType is >= 0 and <= 8) return handle;
                }
                off += idLen + 32 + 1;
            }
            return firstHandle; // no explicit dyno match — fall back to the first producer
        }
        catch { return 0; }
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        while (_running)
        {
            int n;
            try { n = _sock!.Receive(buf); }
            catch { break; }
            if (n <= 0) break;
            var d = new byte[n];
            Array.Copy(buf, d, n);
            DataReceived?.Invoke(d);
        }
        IsOpen = false;
    }

    public void Write(byte[] data, int length)
    {
        var s = _sock;
        if (s == null || _listenerOnly) return; // listener = passive, never transmit
        try
        {
            int off = 0;
            while (off < length)
            {
                int sent = s.Send(data, off, length - off, SocketFlags.None);
                if (sent <= 0) break;
                off += sent;
            }
        }
        catch { }
    }

    public void Close()
    {
        _running = false;
        var s = _sock;
        _sock = null;
        if (s != null) { try { s.Close(); } catch { } }
        try { _reader?.Join(300); } catch { }
        IsOpen = false;
    }

    public void Dispose() => Close();

    // ---- helpers ----
    private void Send(byte[] msg) => _sock!.Send(msg);

    // Read one length-prefixed message: [u16 len][...]. Returns the full buffer incl. the length.
    private byte[]? ReadMessage()
    {
        try
        {
            var head = new byte[2];
            if (!ReceiveExact(head, 2)) return null;
            int len = ReadNetU16(head, 0);
            if (len < 2 || len > 65535) return null;
            var msg = new byte[len];
            msg[0] = head[0]; msg[1] = head[1];
            return ReceiveExact(msg, len - 2, 2) ? msg : null;
        }
        catch { return null; }
    }

    private bool ReceiveExact(byte[] buf, int count, int offset = 0)
    {
        int got = 0;
        while (got < count)
        {
            int n = _sock!.Receive(buf, offset + got, count - got, SocketFlags.None);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    private static byte[] BuildHeader(ushort len, uint cmd)
    {
        var b = new byte[len];
        WriteNetU16(b, 0, len);
        WriteNetI32(b, 2, (int)cmd);
        WriteNetU16(b, 6, 0);
        return b;
    }

    private static void WriteNetI32(byte[] b, int off, int val)
        => BitConverter.GetBytes((uint)IPAddress.HostToNetworkOrder(val)).CopyTo(b, off);
    private static void WriteNetU16(byte[] b, int off, int val)
        => BitConverter.GetBytes((ushort)IPAddress.HostToNetworkOrder((short)val)).CopyTo(b, off);
    private static int ReadNetI32(byte[] b, int off) => IPAddress.NetworkToHostOrder(BitConverter.ToInt32(b, off));
    private static ushort ReadNetU16(byte[] b, int off) => (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(b, off));
}
