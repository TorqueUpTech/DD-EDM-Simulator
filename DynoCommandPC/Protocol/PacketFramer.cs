namespace DynoCommandPC.Protocol;

/// <summary>
/// Stream (de)framing for the serial / Bluetooth-SPP link.
/// Wire frame: [0xA0 SYNC][LEN][stuffed 10-byte packet...]
/// Every 0xA0 inside the packet body is followed by an inserted 0x00 so the
/// 0xA0 sync byte is unambiguous. LEN is the total wrapped length (12..22).
/// Mirrors the original PacketWriter.wrappPacket / PacketTester.testPacket.
/// </summary>
public static class PacketFramer
{
    public const byte Sync = 0xA0;

    /// <summary>Computes the CRC, then wraps the 10-byte packet for the wire.</summary>
    public static byte[] Wrap(CanPacket pkt)
    {
        ushort crc = Crc16.Calculate(pkt.ToBytes(), 8);
        pkt.Crcl = (byte)(crc & 0xFF);
        pkt.Crch = (byte)(crc >> 8);

        byte[] packet = pkt.ToBytes(); // 10 bytes incl. CRC
        var buf = new byte[2 + packet.Length * 2 + 1];
        int num = 2;
        foreach (byte b in packet)
        {
            buf[num++] = b;
            if (b == Sync) buf[num++] = 0;
        }
        buf[0] = Sync;
        buf[1] = (byte)num;

        var outp = new byte[num];
        Array.Copy(buf, outp, num);
        return outp;
    }
}

/// <summary>Byte-at-a-time push parser. Raises PacketReceived for each valid frame.</summary>
public sealed class FrameParser
{
    private enum State { WaitSyncL, WaitSyncH, GetData }

    private State _state = State.WaitSyncL;
    private readonly byte[] _data = new byte[22];
    private int _idx;

    public long Errors { get; private set; }
    public event Action<CanPacket>? PacketReceived;

    public void Feed(byte[] buffer, int length)
    {
        for (int i = 0; i < length; i++) Feed(buffer[i]);
    }

    public void Feed(byte rx)
    {
        bool error = false;

        switch (_state)
        {
            case State.WaitSyncL:
                if (rx == PacketFramer.Sync)
                {
                    _data[0] = PacketFramer.Sync;
                    _state = State.WaitSyncH;
                }
                else Errors++;
                break;

            case State.WaitSyncH:
                if (rx > 11 && rx < 23)
                {
                    _data[1] = rx;
                    _idx = 2;
                    _state = State.GetData;
                }
                else { Errors++; error = true; }
                break;

            case State.GetData:
                _data[_idx++] = rx;
                if (_idx == _data[1])
                {
                    error = !TryComplete();
                    _state = State.WaitSyncL;
                    _idx = 0;
                }
                break;
        }

        if (error) _state = State.WaitSyncL;
    }

    private bool TryComplete()
    {
        int len = _data[1];
        var packet = new byte[10];
        int rxIdx = 0;

        for (int loop = 2; loop < len; loop++)
        {
            if (_data[loop] == PacketFramer.Sync)
            {
                if (_data[loop + 1] != 0 || loop == len - 1) { Errors++; return false; }
                if (rxIdx >= 10) { Errors++; return false; }
                packet[rxIdx++] = PacketFramer.Sync;
                loop++; // skip the inserted 0x00
            }
            else
            {
                if (rxIdx >= 10) { Errors++; return false; }
                packet[rxIdx++] = _data[loop];
            }
        }

        ushort packetCrc = (ushort)((packet[9] << 8) | packet[8]);
        if (packetCrc != Crc16.Calculate(packet, 8)) { Errors++; return false; }

        PacketReceived?.Invoke(CanPacket.FromBytes(packet));
        return true;
    }
}
