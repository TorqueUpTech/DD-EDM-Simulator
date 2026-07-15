namespace DynoCommandPC.Protocol;

/// <summary>
/// Builds the outgoing packets the handheld continuously transmits. Logic and
/// magic numbers mirror the original PacketFactory.
/// </summary>
public static class PacketFactory
{
    // Digital-inputs bitfield (Payload[2..3], little-endian ushort).
    private const ushort DI_GraphClear = 0x0001;
    private const ushort DI_Brake      = 0x0002;
    private const ushort DI_Snapshot   = 0x0004;
    private const ushort DI_CommandRpm = 0x0008;
    private const ushort DI_Fan1Low    = 0x0010;
    private const ushort DI_Fan1High   = 0x0020;
    private const ushort DI_Fan2Low    = 0x0040;
    private const ushort DI_Fan2High   = 0x0080;
    private const ushort DI_RampUp     = 0x0100;
    private const ushort DI_RampDown   = 0x0200;
    private const ushort DI_Load       = 0x0400;
    private const ushort DI_Speed      = 0x0800;
    private const ushort DI_SwapScreens= 0x1000;

    public static CanPacket Ping() => new()
    {
        Id = 0x41, Cmd = 0xFB
    };

    public static CanPacket Knob(CommandState s)
    {
        ushort value = 0;
        byte active = 0;

        switch (s.Mode)
        {
            case DynoMode.SetBed:
                value = 300; active = 1; break;
            case DynoMode.Speed:
                if (s.SpeedUnits == SpeedUnit.Rpm || s.SpeedControl == SpeedControl.Engine)
                    value = (ushort)s.TargetSpeed;
                else
                    value = (ushort)(s.TargetSpeed * 10.0);
                active = 1;
                break;
            case DynoMode.Load:
                value = (ushort)(s.TargetLoad * 10.0); active = 1; break;
            case DynoMode.RampUp:
            case DynoMode.RampHold:
            case DynoMode.RampDown:
                value = (ushort)s.EffectiveRampRate; break;
        }

        var p = new CanPacket { Id = 0x4F, Cmd = 0x0F };
        p.Payload[0] = 0x19;
        p.Payload[1] = active;
        p.Value16 = value;
        return p;
    }

    public static CanPacket DigitalInputs(CommandState s)
    {
        ushort bits = 0;

        if (s.GraphClearRequests > 0)  { bits |= DI_GraphClear;  s.GraphClearRequests--; }
        if (s.SnapshotRequests > 0)    { bits |= DI_Snapshot;    s.SnapshotRequests--; }
        if (s.SwapScreenRequests > 0)  { bits |= DI_SwapScreens; s.SwapScreenRequests--; }
        // Brake release: pulse the Speed command bit to clear the PC's latched brake warning.
        if (s.BrakeClearRequests > 0)  { bits |= DI_Speed;       s.BrakeClearRequests--; }

        switch (s.Mode)
        {
            case DynoMode.RampUp:   bits |= DI_Load | DI_RampUp; break;
            case DynoMode.RampHold: bits |= DI_Load; break;
            case DynoMode.RampDown: bits |= DI_Load | DI_RampDown; break;
            case DynoMode.Speed:    bits |= DI_Speed; break;
            case DynoMode.Load:     bits |= DI_Load; break;
            case DynoMode.Brake:    bits |= DI_Brake; break;
            case DynoMode.SetBed:   bits |= DI_Speed; break;
        }

        if (s.SpeedControl == SpeedControl.Engine) bits |= DI_CommandRpm;

        bits |= FanBits(s.Fan1, DI_Fan1Low, DI_Fan1High);
        bits |= FanBits(s.Fan2, DI_Fan2Low, DI_Fan2High);

        var p = new CanPacket { Id = 0x4F, Cmd = 0x0F };
        p.Payload[0] = 0x18;
        p.Value16 = bits;
        return p;
    }

    private static ushort FanBits(FanMode mode, ushort low, ushort high) => mode switch
    {
        FanMode.Off  => low,
        FanMode.On   => (ushort)(low | high),
        FanMode.Auto => high,
        _            => 0 // PC: leave to PC
    };

    public static CanPacket HfrMode(CommandState s)
    {
        var p = new CanPacket { Id = 0x4F, Cmd = 0x05 };
        p.Payload[0] = 0xE0;
        p.Value16 = s.Mode == DynoMode.Handsfree ? (ushort)0xA500 : (ushort)0x5050;
        return p;
    }

    public static CanPacket DynoModePacket(CommandState s)
    {
        var p = new CanPacket { Id = 0x4F, Cmd = 0x6F };
        p.Payload[0] = 0x50;
        p.Value16 = s.Mode == DynoMode.Load ? (ushort)0x35 : (ushort)0x53;
        return p;
    }

    public static CanPacket Balance(CommandState s)
    {
        ushort value = 0;
        if (s.BalanceActive)
            value = unchecked((ushort)(short)Math.Round(s.Balance * -10.0));

        var p = new CanPacket { Id = 0x4F, Cmd = 0x0F };
        p.Payload[0] = 0x17;
        p.Value16 = value;
        return p;
    }

    public static CanPacket? RequestedBedPosition(CommandState s)
    {
        if (s.BedRequestedPosition == 0.0) return null;
        var p = new CanPacket { Id = 0x48, Cmd = 0xA0 };
        p.Payload[0] = 0x32;
        p.Value16 = (ushort)(s.BedRequestedPosition * 1000.0);
        return p;
    }
}
