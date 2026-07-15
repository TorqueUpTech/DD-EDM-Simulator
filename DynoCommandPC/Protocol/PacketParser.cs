namespace DynoCommandPC.Protocol;

/// <summary>
/// Decodes inbound packets into DynoState, and produces the handshake/identity
/// replies the dyno expects (config echo, config query, FW version/CRC, BT
/// address exchange) via the <paramref name="respond"/> callback. Mirrors the
/// matchers in the original PacketParser.
/// </summary>
public static class PacketParser
{
    // Reported firmware version (handheld identity). Mirrors SYS_Version.
    public const byte FwMajor = 1, FwMinor = 0, FwSub = 0;

    private static string _tempBtAddress = "";

    public static void Process(CanPacket p, DynoState st, CommandState cmd, Action<CanPacket>? respond = null)
    {
        DateTime now = DateTime.UtcNow;
        st.LastRxUtc = now;

        int src = p.Id & 0xF0;
        int lo  = p.Id & 0x0F;

        // ---- Raw roller speeds (pit controller alive). ----
        if (src == 0x20 && p.Cmd == 0x0F && p.Payload[0] == 0x0A)
        { st.RawSpeedMaster = p.Value16; st.PitSeenUtc = now; }
        else if (src == 0x70 && p.Cmd == 0x0F && p.Payload[0] == 0x5B)
        { st.RawSpeedSlave = p.Value16; st.PitSeenUtc = now; }

        // Combined controller speed (avg when both present, >=160 raw threshold).
        ushort num;
        if (st.RawSpeedMaster >= 160)
            num = st.RawSpeedSlave >= 160
                ? (ushort)((st.RawSpeedMaster + st.RawSpeedSlave) / 2)
                : st.RawSpeedMaster;
        else
            num = st.RawSpeedSlave >= 160 ? st.RawSpeedSlave : (ushort)0;

        st.SpeedRevsPerSec = num * 0.002 / cmd.GearRatio;
        st.SpeedKph = st.SpeedRevsPerSec * cmd.RollerCircumference * 3.6;

        // ---- Pit brake state. ----
        if (p.Cmd == 0x0F &&
            ((src == 0x20 && p.Payload[0] == 0x1A) ||
             (src == 0x70 && p.Payload[0] == 0x56)))
        {
            st.BrakeState = (p.Value16 & 0x200) == 0x200 ? "Active" : "Requested";
            st.PitSeenUtc = now;
        }

        // ---- Hands-free state machine. ----
        if (p.Cmd == 0x25)
        {
            switch (p.Payload[0])
            {
                case 0xA0: st.HandsfreeState = "Accelerate to start speed"; st.PitSeenUtc = now; break;
                case 0xA1: st.HandsfreeState = "Open throttle";             st.PitSeenUtc = now; break;
                case 0xA2: st.HandsfreeState = "Ramping up";                st.PitSeenUtc = now; break;
                case 0xA3: st.HandsfreeState = "Ramping down";              st.PitSeenUtc = now; break;
                case 0xA4: st.HandsfreeState = "Close throttle";            st.PitSeenUtc = now; break;
                case 0xA5: st.HandsfreeState = "Slow to start speed";       st.PitSeenUtc = now; break;
            }

            // PC shootout mode (from the PC node, id == 0x0F).
            if (p.Id == 0x0F)
            {
                switch (p.Payload[0])
                {
                    case 0x10: st.ShootoutMode = "Normal";              st.ShootoutActive = false; st.PcSeenUtc = now; break;
                    case 0x11: st.ShootoutMode = "Standard shootout";   st.ShootoutActive = true;  st.PcSeenUtc = now; break;
                    case 0x12: st.ShootoutMode = "Accredited shootout"; st.ShootoutActive = true;  st.PcSeenUtc = now; break;
                }
            }
        }

        // ---- PC-dictated ramp rate (used during a shootout). 0F A0 67. ----
        if (p.Id == 0x0F && p.Cmd == 0xA0 && p.Payload[0] == 0x67)
        { st.PcRampRate = p.Value16 * 0.002 * 10.0 / cmd.GearRatio; st.PcSeenUtc = now; }

        // ---- EDM-commanded speed-unit change. 0F 6F 60, P2==1. ----
        if (p.Id == 0x0F && p.Cmd == 0x6F && p.Payload[0] == 0x60 && p.Payload[2] == 1)
        {
            switch (p.Payload[3])
            {
                case 1: cmd.SpeedUnits = SpeedUnit.Kph; st.DynoRequestedUnit = "km/h"; break;
                case 2: cmd.SpeedUnits = SpeedUnit.Mph; st.DynoRequestedUnit = "mph";  break;
                case 3: cmd.SpeedUnits = SpeedUnit.Rpm; st.DynoRequestedUnit = "rpm";  break;
            }
        }

        // ---- Bed position read-back. ----
        if (p.Cmd == 0xA0 && (lo == 0x04 || lo == 0x0F) && src == 0x80 && p.Payload[0] == 0x32)
        { st.BedCurrentPosition = p.Value16 / 1000.0; st.BedSeenUtc = now; }
        else if (p.Cmd == 0xA0 && (lo == 0x04 || lo == 0x0F) && src == 0x00 && p.Payload[0] == 0x31)
            st.BedTargetPosition = p.Value16 / 1000.0;

        // ---- Config push from the dyno (CMD 0x2F). ----
        if ((lo == 0x0F || lo == 0x04) && p.Cmd == 0x2F)
            HandleConfigPush(p, st, cmd, respond);

        // ---- Config query from the dyno (CMD 0x3F) — we answer. ----
        if ((lo == 0x0F || lo == 0x04) && p.Cmd == 0x3F)
            HandleConfigQuery(p, cmd, respond);

        // ---- Firmware version / CRC request (CMD 0xE5). ----
        if (lo == 0x04 && p.Cmd == 0xE5 && p.Payload[0] == 0x20 && respond != null)
            respond(FwVersion(p.Id));
        if (lo == 0x04 && p.Cmd == 0xE5 && p.Payload[0] == 0x30 && respond != null)
            respond(FwCrc());

        // ---- Bluetooth address exchange handshake (CMD 0xD1). ----
        if (lo == 0x04 && p.Cmd == 0xD1 && (p.Payload[0] & 0x20) == 0)
            HandleBtAddress(p, st, cmd, respond);
    }

    private static void HandleConfigPush(CanPacket p, DynoState st, CommandState cmd, Action<CanPacket>? respond)
    {
        st.ConfigReceived = true;
        byte sel = p.Payload[0];

        switch (sel)
        {
            case 0x05: // bed-mode bits + transmission type
            {
                ushort bits = p.Value16;
                st.In4wdMode        = (bits & 0x01) == 0x01;
                st.MasterIsController = (bits & 0x02) == 0x02;
                st.IsA4wdBed        = (bits & 0x04) == 0x04;
                st.Transmission = (TransmissionType)System.Math.Min(p.Payload[3], (byte)7);
                cmd.Is4Wd = st.IsA4wdBed; // let the dyno drive the 4WD flag
                break;
            }
            case 0xC9: st.DynoSpeedMaxKph = p.Value16 / 10.0; cmd.SpeedMaxKph = st.DynoSpeedMaxKph; break;
            case 0xCA: st.DynoSpeedMaxMph = p.Value16 / 10.0; cmd.SpeedMaxMph = st.DynoSpeedMaxMph; break;
            case 0xCB: st.DynoSpeedMaxRpm = p.Value16;        cmd.SpeedMaxRpm = st.DynoSpeedMaxRpm; break;
            case 0xCC: st.FeatureLevel = p.Value16; cmd.FeatureLevel = st.FeatureLevel; break;
            case 0xCD: st.DemoMode = p.Payload[2] == 1; cmd.DemoMode = st.DemoMode; break;
            case 0xCF: // roller diameter, two-part
                if ((p.Payload[1] & 0x7F) == 1)
                {
                    double d = (p.Value16 * 65536.0 + st.TempRollerDiameterHi) / 100000.0;
                    st.DynoRollerDiameter = d;
                    if (d > 0) cmd.RollerDiameter = d; // dyno drives the scaling
                }
                else st.TempRollerDiameterHi = p.Value16;
                break;
        }

        // Echo an ACK back when the dyno asked for one (P1 bit 0x80).
        if ((p.Payload[1] & 0x80) == 0x80 && respond != null)
        {
            var ack = new CanPacket { Id = (byte)(0x40 + (p.Id >> 4)), Cmd = 0x2F, Tsl = p.Tsl, Tsh = p.Tsh };
            ack.Payload[0] = p.Payload[0];
            ack.Payload[1] = (byte)(p.Payload[1] & 0x7F);
            ack.Payload[2] = p.Payload[2];
            ack.Payload[3] = p.Payload[3];
            respond(ack);
        }
    }

    private static void HandleConfigQuery(CanPacket p, CommandState cmd, Action<CanPacket>? respond)
    {
        if (respond == null) return;

        ushort val;
        switch (p.Payload[0])
        {
            case 0xC8: val = 3; break;                              // PDA model (generic)
            case 0xC9: val = (ushort)(cmd.SpeedMaxKph * 10.0); break;
            case 0xCA: val = (ushort)(cmd.SpeedMaxMph * 10.0); break;
            case 0xCB: val = (ushort)cmd.SpeedMaxRpm; break;
            case 0xCC: val = (ushort)cmd.FeatureLevel; break;
            case 0xCD: val = (ushort)(cmd.DemoMode ? 1 : 0); break;
            case 0xCF:                                              // roller diameter (two-part)
            {
                uint scaled = (uint)(cmd.RollerDiameter * 100000.0);
                ushort hi = (ushort)(scaled / 65536u);
                val = (p.Payload[1] & 0x7F) == 1 ? (ushort)(scaled - hi * 65536u) : hi;
                break;
            }
            default: return;
        }

        var r = new CanPacket { Id = (byte)(0x40 + (p.Id >> 4)), Cmd = 0x3F, Tsl = p.Tsl, Tsh = p.Tsh };
        r.Payload[0] = p.Payload[0];
        r.Payload[1] = (byte)(p.Payload[1] & 0x7F);
        r.Value16 = val;
        respond(r);
    }

    private static void HandleBtAddress(CanPacket p, DynoState st, CommandState cmd, Action<CanPacket>? respond)
    {
        if (respond == null) return;

        if ((p.Payload[0] & 0x10) == 0)
        {
            // Dyno is asking for our EDM address — answer in two halves.
            string addr = cmd.EdmAddress ?? "";
            if (addr.Length < 12) return;
            var r = new CanPacket { Id = (byte)(0x40 + (p.Id >> 4)), Cmd = 0xD1 };
            if ((p.Payload[0] & 0x0F) == 0)
            {
                r.Payload[0] = 0x20;
                r.Payload[1] = Hex(addr, 6); r.Payload[2] = Hex(addr, 8); r.Payload[3] = Hex(addr, 10);
            }
            else
            {
                r.Payload[0] = 0x21;
                r.Payload[1] = Hex(addr, 0); r.Payload[2] = Hex(addr, 2); r.Payload[3] = Hex(addr, 4);
            }
            respond(r);
            return;
        }

        // Dyno is sending us an address to learn — echo each half back.
        string half = p.Payload[1].ToString("X2") + p.Payload[2].ToString("X2") + p.Payload[3].ToString("X2");
        var ack = new CanPacket { Id = (byte)(0x40 + (p.Id >> 4)), Cmd = 0xD1 };
        ack.Payload[1] = p.Payload[1]; ack.Payload[2] = p.Payload[2]; ack.Payload[3] = p.Payload[3];
        if ((p.Payload[0] & 0x0F) == 0)
        {
            ack.Payload[0] = 0x30;
            _tempBtAddress = half;
        }
        else
        {
            ack.Payload[0] = 0x31;
            st.LearnedBtAddress = half + _tempBtAddress;
        }
        respond(ack);
    }

    private static CanPacket FwVersion(byte reqId)
    {
        var r = new CanPacket { Id = (byte)(0x40 + (reqId >> 4)), Cmd = 0xE5 };
        r.Payload[0] = 0x25;
        r.Payload[1] = FwMajor;
        r.Payload[2] = (byte)(((FwMinor & 0x0F) << 4) + ((FwSub & 0xF0) >> 4));
        r.Payload[3] = (byte)((FwSub & 0x0F) << 4);
        return r;
    }

    private static CanPacket FwCrc()
    {
        var r = new CanPacket { Id = 0x40, Cmd = 0xE5 };
        r.Payload[0] = 0x35;
        r.Payload[1] = 0xFF;
        r.Value16 = 0;
        return r;
    }

    private static byte Hex(string s, int start) => Convert.ToByte(s.Substring(start, 2), 16);
}
