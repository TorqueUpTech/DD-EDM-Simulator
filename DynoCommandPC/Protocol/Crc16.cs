namespace DynoCommandPC.Protocol;

/// <summary>
/// CRC-16/MODBUS (reflected, polynomial 0xA001, init 0xFFFF, no final XOR).
/// Table is generated at startup; table[1] == 0xC0C1, matching the original
/// firmware's hard-coded COM_CRCGenerator table. Computed over the first 8
/// bytes of a CanPacket (ID..TSH).
/// </summary>
public static class Crc16
{
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort c = (ushort)i;
            for (int j = 0; j < 8; j++)
                c = (ushort)((c & 1) != 0 ? (c >> 1) ^ 0xA001 : c >> 1);
            table[i] = c;
        }
        return table;
    }

    public static ushort Calculate(byte[] buffer, int length, ushort accumulator = 0xFFFF)
    {
        for (int i = 0; i < length; i++)
            accumulator = (ushort)((accumulator >> 8) ^ Table[(accumulator ^ buffer[i]) & 0xFF]);
        return accumulator;
    }
}
