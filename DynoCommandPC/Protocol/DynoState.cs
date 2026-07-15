namespace DynoCommandPC.Protocol;

/// <summary>Decoded live state received from the dyno (via the EDM).</summary>
public sealed class DynoState
{
    public ushort RawSpeedMaster;
    public ushort RawSpeedSlave;

    /// <summary>Roller speed in revs/sec (base unit), = raw * 0.002 / GearRatio.</summary>
    public double SpeedRevsPerSec;
    /// <summary>Derived road speed in km/h from roller circumference.</summary>
    public double SpeedKph;

    public string BrakeState = "—";
    public string HandsfreeState = "—";
    public string ShootoutMode = "—";

    /// <summary>True during an active (standard/accredited) shootout — PC owns the ramp rate.</summary>
    public bool ShootoutActive;
    /// <summary>Ramp rate dictated by the PC (rampratekph), decoded from 0F A0 67.</summary>
    public double PcRampRate;

    // ---- Bed position read-back (Set-Bed) ----
    /// <summary>Actual bed position reported by the bed controller, metres (0 = none yet).</summary>
    public double BedCurrentPosition;
    /// <summary>Target bed position the PC has set, metres.</summary>
    public double BedTargetPosition;

    // ---- Config pushed by the dyno (CMD 0x2F) ----
    public bool ConfigReceived;
    public bool In4wdMode;
    public bool MasterIsController = true;
    public bool IsA4wdBed;
    public TransmissionType Transmission = TransmissionType.Unknown;
    /// <summary>Roller diameter the dyno reports (metres). 0 until received.</summary>
    public double DynoRollerDiameter;
    public double DynoSpeedMaxKph, DynoSpeedMaxMph, DynoSpeedMaxRpm;
    public int FeatureLevel;
    public bool DemoMode;
    /// <summary>Speed unit the dyno last asked us to display (CMD 0x6F 0x60).</summary>
    public string DynoRequestedUnit = "—";
    /// <summary>EDM Bluetooth address learned via the 0xD1 handshake (if any).</summary>
    public string LearnedBtAddress = "";

    // Scratch for the two-part roller-diameter config message.
    internal ushort TempRollerDiameterHi;

    // ---- Device-online watchdog ----
    public bool PitControllerOnline;
    public bool PcOnline;
    public bool BedCtrlOnline;
    public DateTime PitSeenUtc = DateTime.MinValue;
    public DateTime PcSeenUtc = DateTime.MinValue;
    public DateTime BedSeenUtc = DateTime.MinValue;

    public DateTime LastRxUtc = DateTime.MinValue;

    /// <summary>How long a subsystem may go silent before it's declared offline.</summary>
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Comms-loss fail-safe. Called from the TX loop. When the pit controller
    /// stops transmitting, zero the roller speed (so a frozen reading can't be
    /// mistaken for a live one), and clear the online flags for any silent
    /// subsystem. Mirrors the original COM_Comms device-timer / zeroSpeed logic.
    /// </summary>
    public void UpdateWatchdog(DateTime nowUtc)
    {
        PitControllerOnline = PitSeenUtc != DateTime.MinValue && nowUtc - PitSeenUtc < DeviceTimeout;
        PcOnline            = PcSeenUtc  != DateTime.MinValue && nowUtc - PcSeenUtc  < DeviceTimeout;
        BedCtrlOnline       = BedSeenUtc != DateTime.MinValue && nowUtc - BedSeenUtc < DeviceTimeout;

        if (!PitControllerOnline)
        {
            RawSpeedMaster = 0;
            RawSpeedSlave = 0;
            SpeedRevsPerSec = 0;
            SpeedKph = 0;
        }
        if (!PcOnline)
            ShootoutActive = false;
    }

    /// <summary>Reset everything to a safe idle (on disconnect).</summary>
    public void ResetForDisconnect()
    {
        RawSpeedMaster = RawSpeedSlave = 0;
        SpeedRevsPerSec = SpeedKph = 0;
        ShootoutActive = false;
        PitControllerOnline = PcOnline = BedCtrlOnline = false;
        PitSeenUtc = PcSeenUtc = BedSeenUtc = LastRxUtc = DateTime.MinValue;
        BrakeState = HandsfreeState = "—";
    }
}
