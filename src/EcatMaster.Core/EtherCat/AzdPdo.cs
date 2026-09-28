using System.Runtime.InteropServices;

namespace EcatMaster.Core.EtherCat;

/// <summary>
/// Oriental Motor AZD-CED 에 우리가 직접 매핑하는 RxPDO(마스터 → 드라이브) 레이아웃.
/// 드라이브 기본 매핑에 의존하지 않고 PreOp 단계에서 1600h 를 이 순서대로 다시 쓴다
/// (<see cref="AzdDriveConfigurator"/>). Pack=1 이므로 바이트 오프셋이 그대로 PDO 순서.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AzdRxPdo
{
    public ushort ControlWord;         // 6040h
    public sbyte ModesOfOperation;     // 6060h
    public int TargetPosition;         // 607Ah  [step]
    public uint ProfileVelocity;       // 6081h  [Hz]   (단위는 드라이브 파라미터에 따름)
    public uint ProfileAcceleration;   // 6083h  [kHz/s 또는 ms]
    public uint ProfileDeceleration;   // 6084h
    public int TargetVelocity;         // 60FFh  [Hz]  (PV 모드)

    public const int Size = 23;
}

/// <summary>TxPDO(드라이브 → 마스터) 레이아웃. 1A00h 로 매핑.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AzdTxPdo
{
    public ushort StatusWord;              // 6041h
    public sbyte ModesOfOperationDisplay;  // 6061h
    public int PositionActual;             // 6064h
    public int VelocityActual;             // 606Ch
    public ushort ErrorCode;               // 603Fh
    public uint DigitalInputs;             // 60FDh

    public const int Size = 17;
}

/// <summary>PDO 매핑 엔트리 (index &lt;&lt; 16 | subindex &lt;&lt; 8 | bit length).</summary>
public static class AzdPdoMap
{
    public const ushort RxPdoIndex = 0x1600;
    public const ushort TxPdoIndex = 0x1A00;

    public static readonly uint[] RxEntries =
    {
        0x6040_0010, // Controlword            U16
        0x6060_0008, // Modes of operation     I8
        0x607A_0020, // Target position        I32
        0x6081_0020, // Profile velocity       U32
        0x6083_0020, // Profile acceleration   U32
        0x6084_0020, // Profile deceleration   U32
        0x60FF_0020, // Target velocity        I32
    };

    public static readonly uint[] TxEntries =
    {
        0x6041_0010, // Statusword                     U16
        0x6061_0008, // Modes of operation display     I8
        0x6064_0020, // Position actual value          I32
        0x606C_0020, // Velocity actual value          I32
        0x603F_0010, // Error code                     U16
        0x60FD_0020, // Digital inputs                 U32
    };
}
