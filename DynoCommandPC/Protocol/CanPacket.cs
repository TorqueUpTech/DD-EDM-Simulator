namespace DynoCommandPC.Protocol;

/// <summary>
/// The fixed 10-byte dyno CAN packet. Layout is byte-identical to the original
/// handheld firmware's CanPacket (ID, CMD, Payload[4], TSL, TSH, CRCL, CRCH).
/// 16-bit values live little-endian in Payload[2..3].
/// </summary>
public sealed class CanPacket
{
    public byte Id;
    public byte Cmd;
    public readonly byte[] Payload = new byte[4];
    public byte Tsl;
    public byte Tsh;
    public byte Crcl;
    public byte Crch;

    /// <summary>Little-endian 16-bit value carried in Payload[2..3].</summary>
    public ushort Value16
    {
        get => (ushort)(Payload[2] | (Payload[3] << 8));
        set { Payload[2] = (byte)(value & 0xFF); Payload[3] = (byte)(value >> 8); }
    }

    public byte[] ToBytes() => new[]
        { Id, Cmd, Payload[0], Payload[1], Payload[2], Payload[3], Tsl, Tsh, Crcl, Crch };

    public byte[] ToBytesNoCrc() => new[]
        { Id, Cmd, Payload[0], Payload[1], Payload[2], Payload[3], Tsl, Tsh };

    public static CanPacket FromBytes(byte[] b)
    {
        var p = new CanPacket { Id = b[0], Cmd = b[1], Tsl = b[6], Tsh = b[7] };
        p.Payload[0] = b[2];
        p.Payload[1] = b[3];
        p.Payload[2] = b[4];
        p.Payload[3] = b[5];
        if (b.Length > 8) { p.Crcl = b[8]; p.Crch = b[9]; }
        return p;
    }

    public string ToHex()
    {
        var b = ToBytes();
        return string.Join(" ", Array.ConvertAll(b, x => x.ToString("X2")));
    }
}
