using System.Runtime.InteropServices;

namespace DynoCommandPC.Transport;

/// <summary>
/// Direct classic-Bluetooth RFCOMM/SPP connection to the EDM by MAC address —
/// no pairing/bonding and no virtual COM port required. This mirrors what the
/// original handheld did (connect straight to the address) and works with a
/// connectable-but-not-discoverable, non-bonding EDM. Uses raw Winsock AF_BTH
/// with an SDP service lookup (serviceClassId = SPP, port = 0).
/// </summary>
public sealed class BluetoothRfcommTransport : ITransport
{
    private const int AF_BTH = 32;
    private const int SOCK_STREAM = 1;
    private const int BTHPROTO_RFCOMM = 3;
    private static readonly Guid SerialPortServiceClass = new("00001101-0000-1000-8000-00805F9B34FB");
    private static readonly IntPtr INVALID_SOCKET = new(-1);

    // ws2bth.h declares SOCKADDR_BTH byte-packed (sizeof == 30): addressFamily@0,
    // btAddr@2, serviceClassId@10, port@26. Pack=1 is essential — default padding
    // misplaces btAddr and the OS rejects the address (WSAEADDRNOTAVAIL/10049).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SOCKADDR_BTH
    {
        public ushort addressFamily;
        public ulong btAddr;
        public Guid serviceClassId;
        public uint port;
    }

    [DllImport("ws2_32.dll")] private static extern int WSAStartup(ushort version, byte[] data);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern IntPtr socket(int af, int type, int protocol);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int connect(IntPtr s, ref SOCKADDR_BTH name, int namelen);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int send(IntPtr s, byte[] buf, int len, int flags);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int recv(IntPtr s, byte[] buf, int len, int flags);
    [DllImport("ws2_32.dll")] private static extern int closesocket(IntPtr s);

    private readonly ulong _addr;
    private readonly string _mac;
    private IntPtr _sock = INVALID_SOCKET;
    private Thread? _reader;
    private volatile bool _running;

    public event Action<byte[]>? DataReceived;
    public bool IsOpen { get; private set; }
    public string Description => $"BT-direct {_mac}";

    /// <summary>Set when Open() fails, e.g. "connect() WSA 10060 (timed out)".</summary>
    public string? LastError { get; private set; }

    public BluetoothRfcommTransport(string mac)
    {
        _mac = mac;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        _addr = Convert.ToUInt64(hex, 16);
    }

    public bool Open()
    {
        try
        {
            LastError = null;
            WSAStartup(0x0202, new byte[408]); // refcounted; safe even if already up
            _sock = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
            if (_sock == INVALID_SOCKET)
            {
                LastError = $"socket() WSA {Marshal.GetLastWin32Error()}";
                return false;
            }

            var sa = new SOCKADDR_BTH
            {
                addressFamily = AF_BTH,
                btAddr = _addr,
                serviceClassId = SerialPortServiceClass, // SDP-resolve the RFCOMM channel
                port = 0
            };
            if (connect(_sock, ref sa, Marshal.SizeOf<SOCKADDR_BTH>()) != 0)
            {
                int err = Marshal.GetLastWin32Error();
                LastError = $"connect() WSA {err}{WsaHint(err)}";
                Close();
                return false;
            }

            _running = true;
            IsOpen = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "BT-rfcomm" };
            _reader.Start();
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    private void ReadLoop()
    {
        var buf = new byte[512];
        while (_running)
        {
            int n = recv(_sock, buf, buf.Length, 0);
            if (n <= 0) break;
            var d = new byte[n];
            Array.Copy(buf, d, n);
            DataReceived?.Invoke(d);
        }
        IsOpen = false;
    }

    public void Write(byte[] data, int length)
    {
        if (_sock == INVALID_SOCKET) return;
        try
        {
            int off = 0;
            while (off < length)
            {
                byte[] chunk = off == 0 ? data : data[off..];
                int n = send(_sock, chunk, length - off, 0);
                if (n <= 0) break;
                off += n;
            }
        }
        catch { }
    }

    public void Close()
    {
        _running = false;
        var s = _sock;
        _sock = INVALID_SOCKET;
        if (s != INVALID_SOCKET) { try { closesocket(s); } catch { } }
        try { _reader?.Join(300); } catch { }
        IsOpen = false;
    }

    private static string WsaHint(int err) => err switch
    {
        10060 => " (timed out — EDM off/out of range)",
        10061 => " (refused — busy or auth required)",
        10064 => " (host down)",
        10050 or 10051 => " (BT network down/unreachable)",
        10108 => " (SPP service not found via SDP)",
        10013 => " (access denied — may need pairing/authentication)",
        _ => ""
    };

    public void Dispose() => Close();
}
