namespace EcatMaster.Core.EtherCat;

public enum AxisCommandKind
{
    Enable,
    Disable,
    FaultReset,
    MoveAbsolute,
    MoveRelative,
    Jog,
    Halt,
    QuickStop,
    Home,
}

/// <summary>
/// API 스레드에서 실시간 스레드로 넘기는 명령. 불변 레코드라 락 없이
/// Interlocked.Exchange 로 교환한다 (축당 최신 명령 1개만 유지).
/// </summary>
public sealed record AxisCommand(
    AxisCommandKind Kind,
    int Position = 0,
    uint Velocity = 0,
    uint Acceleration = 0,
    uint Deceleration = 0,
    int TargetVelocity = 0);
