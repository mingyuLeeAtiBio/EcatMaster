namespace EcatMaster.Core.EtherCat;

public enum Cia402State
{
    NotReadyToSwitchOn,
    SwitchOnDisabled,
    ReadyToSwitchOn,
    SwitchedOn,
    OperationEnabled,
    QuickStopActive,
    FaultReactionActive,
    Fault,
    Unknown,
}

public enum OperationMode : sbyte
{
    None = 0,
    ProfilePosition = 1,
    ProfileVelocity = 3,
    Homing = 6,
    CyclicSyncPosition = 8,
}

/// <summary>CiA402 (IEC 61800-7-201) 제어/상태 워드 정의.</summary>
public static class Cia402
{
    // Controlword (6040h) bits
    public const ushort CwSwitchOn = 1 << 0;
    public const ushort CwEnableVoltage = 1 << 1;
    public const ushort CwQuickStop = 1 << 2;      // 0 = quick stop 요구
    public const ushort CwEnableOperation = 1 << 3;
    public const ushort CwBit4 = 1 << 4;           // PP: new set-point / HM: start homing
    public const ushort CwBit5 = 1 << 5;           // PP: change set immediately
    public const ushort CwBit6 = 1 << 6;           // PP: relative
    public const ushort CwFaultReset = 1 << 7;
    public const ushort CwHalt = 1 << 8;

    public const ushort CmdDisableVoltage = 0x0000;
    public const ushort CmdQuickStop = 0x0002;
    public const ushort CmdShutdown = 0x0006;
    public const ushort CmdSwitchOn = 0x0007;
    public const ushort CmdEnableOperation = 0x000F;

    // Statusword (6041h) bits
    public const ushort SwWarning = 1 << 7;
    public const ushort SwRemote = 1 << 9;
    public const ushort SwTargetReached = 1 << 10;
    public const ushort SwInternalLimitActive = 1 << 11;
    public const ushort SwBit12 = 1 << 12;         // PP: set-point ack / HM: homing attained / PV: speed==0
    public const ushort SwBit13 = 1 << 13;         // PP: following error / HM: homing error

    public static Cia402State Decode(ushort sw)
    {
        if ((sw & 0x4F) == 0x00) return Cia402State.NotReadyToSwitchOn;
        if ((sw & 0x4F) == 0x40) return Cia402State.SwitchOnDisabled;
        if ((sw & 0x6F) == 0x21) return Cia402State.ReadyToSwitchOn;
        if ((sw & 0x6F) == 0x23) return Cia402State.SwitchedOn;
        if ((sw & 0x6F) == 0x27) return Cia402State.OperationEnabled;
        if ((sw & 0x6F) == 0x07) return Cia402State.QuickStopActive;
        if ((sw & 0x4F) == 0x0F) return Cia402State.FaultReactionActive;
        if ((sw & 0x4F) == 0x08) return Cia402State.Fault;
        return Cia402State.Unknown;
    }
}
