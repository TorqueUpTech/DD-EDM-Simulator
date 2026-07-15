namespace DynoCommandPC.Protocol;

public enum DynoMode
{
    NoLoad,
    Speed,
    Load,
    RampUp,
    RampHold,
    RampDown,
    Brake,
    Handsfree,
    SetBed
}

public enum SpeedUnit { Kph, Mph, Rpm }

public enum SpeedControl { Road, Engine }

/// <summary>Per-fan output state. Matches FAN_CONTROL_TYPE in the original.</summary>
public enum FanMode { PC, Off, On, Auto }

/// <summary>Drivetrain/bed type, as reported by the dyno in a config push (CMD 0x2F P0 0x05). Mirrors TRANSMISSIONS.</summary>
public enum TransmissionType
{
    Unknown,
    Standard2Wd,
    Locked4Wd,
    CenterDiff4Wd,
    CenterViscous4Wd,
    ComputerDogClutch4Wd,
    TractionStability4Wd,
    FrontViscous4Wd
}
