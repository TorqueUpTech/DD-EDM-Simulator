using DynoCommandPC.Protocol;

namespace DynoCommandPC.Settings;

/// <summary>
/// Persisted operator settings — the PC-app equivalent of the original
/// PdaSettings.xml. Serialized to settings.json beside the executable and
/// created with defaults on first run.
/// </summary>
public sealed class AppSettings
{
    // --- Connection (Bluetooth-direct only) ---
    public string EdmBluetoothAddress { get; set; } = "0012F304962C";
    /// <summary>DynoServer IP for the network (TCP) fork. Blank = UDP broadcast discovery.</summary>
    public string DynoServerAddress { get; set; } = "";
    /// <summary>Network fork: attach as a read-only Listener (no TX) instead of Controller.</summary>
    public bool NetListenerOnly { get; set; }
    public bool AutoConnectAtStartup { get; set; }
    public bool LogCommsToFile { get; set; }

    // --- Dyno type ---
    public bool Is4Wd { get; set; }

    // --- Diagnostics panel visibility (main screen) ---
    public bool ShowPacketLog { get; set; } = true;
    public bool ShowPacketsSeen { get; set; } = true;
    /// <summary>Show the raw inbound-byte hex panel (pre-framing) + capture to logs\dyno-raw-*.log.</summary>
    public bool ShowRawRx { get; set; }

    // --- Feedback ---
    public bool AudibleFeedback { get; set; }

    // --- Speed ---
    public SpeedUnit SpeedUnits { get; set; } = SpeedUnit.Kph;
    public SpeedControl SpeedControl { get; set; } = SpeedControl.Road;
    public double RollerDiameter { get; set; } = 0.2186; // metres
    public double GearRatio { get; set; } = 1.0;
    public double SpeedMaxKph { get; set; } = 200;
    public double SpeedMaxMph { get; set; } = 124;
    public double SpeedMaxRpm { get; set; } = 5000;
    public double SpeedIncSmall { get; set; } = 1;
    public double SpeedIncLarge { get; set; } = 5;
    /// <summary>Operator-defined run start speed (in the active speed unit).</summary>
    public double StartSpeed { get; set; } = 80;

    public double MaxForCurrentUnit() => SpeedUnits switch
    {
        SpeedUnit.Rpm => SpeedMaxRpm,
        SpeedUnit.Mph => SpeedMaxMph,
        _ => SpeedMaxKph
    };

    public string UnitLabel() => SpeedUnits switch
    {
        SpeedUnit.Rpm => "rpm",
        SpeedUnit.Mph => "mph",
        _ => "km/h"
    };
}
