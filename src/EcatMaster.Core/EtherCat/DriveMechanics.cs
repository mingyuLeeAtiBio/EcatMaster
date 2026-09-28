namespace EcatMaster.Core.EtherCat;

public enum UserUnit
{
    /// <summary>드라이브 원단위: 위치 step, 속도 step/s, 가감속 step/s²</summary>
    Step,
    /// <summary>사용자 단위: 위치 µm, 속도 µm/s, 가감속 µm/s²</summary>
    Um,
}

/// <summary>
/// 한 축의 기구 상수. 전자기어(6091h)는 연결 시 드라이브에서 읽고, 리드(µm/rev)와 (VPH 의) 엔코더 분해능은 설정에서 온다.
///
/// 공통 규칙:  분해능[step/rev] = BaseStepsPerRev × B / A
///   · Oriental AZ : Base = 10,000 (고정), A/B = 전자기어 A/B
///   · Nikki VPH-HD: Base = 엔코더 분해능(PulsesPerRev), A = 전자기어 분자(P162), B = 분모(P163)
///   기어비        = 47F1h / 100 (AZ 전용, 0 이면 1.0)
///   step/µm       = 분해능 × 기어비 / 리드[µm]
/// </summary>
public sealed class DriveMechanics
{
    public const int AzdBaseStepsPerRev = 10_000;
    /// <summary>VPH-HD 엔코더 분해능 기본 가정 (20 bit). 반드시 모터 사양으로 확인.</summary>
    public const int VphDefaultPulsesPerRev = 1_048_576;

    private uint _gearA = 1;
    private uint _gearB = 1;
    private short _gearRatioRaw;
    private double _leadUm = 10_000;
    private double _baseStepsPerRev = AzdBaseStepsPerRev;
    private UserUnit _unit = UserUnit.Step;

    /// <summary>6091h:01. 1~99,999,999</summary>
    public uint ElectronicGearA { get => _gearA; set => _gearA = Math.Max(1u, value); }

    /// <summary>6091h:02. 1~99,999,999</summary>
    public uint ElectronicGearB { get => _gearB; set => _gearB = Math.Max(1u, value); }

    /// <summary>47F1h (AZ 전용). 0 = 기어비 설정 비활성(1.0), 1~32,767 = ×0.01</summary>
    public short GearRatioRaw { get => _gearRatioRaw; set => _gearRatioRaw = Math.Max((short)0, value); }

    /// <summary>출력축 1회전당 이동량 [µm]. 볼스크류 리드 등. 드라이브에 없는 값.</summary>
    public double LeadUm { get => _leadUm; set => _leadUm = value > 0 ? value : _leadUm; }

    /// <summary>전자기어 1:1 일 때 1회전당 step (AZ 10,000 / VPH 엔코더 분해능).</summary>
    public double BaseStepsPerRev { get => _baseStepsPerRev; set => _baseStepsPerRev = value > 0 ? value : _baseStepsPerRev; }

    public UserUnit Unit { get => _unit; set => _unit = value; }

    /// <summary>전자기어를 드라이브에서 실제로 읽어 왔는지 (false = 설정/기본값).</summary>
    public bool ReadFromDrive { get; set; }

    public double StepsPerRev => BaseStepsPerRev * (double)ElectronicGearB / ElectronicGearA;
    public double GearRatio => GearRatioRaw <= 0 ? 1.0 : GearRatioRaw / 100.0;
    public double StepsPerUm => StepsPerRev * GearRatio / LeadUm;

    public string UnitName => Unit == UserUnit.Um ? "um" : "step";

    // ---- 변환 ----

    public double StepsToUser(double steps, UserUnit? unit = null) =>
        (unit ?? Unit) == UserUnit.Um ? steps / StepsPerUm : steps;

    public double UserToStepsExact(double value, UserUnit? unit = null) =>
        (unit ?? Unit) == UserUnit.Um ? value * StepsPerUm : value;

    public int UserToSteps(double value, UserUnit? unit = null) =>
        (int)Math.Round(Math.Clamp(UserToStepsExact(value, unit), int.MinValue, int.MaxValue));

    public uint UserToStepsUnsigned(double value, UserUnit? unit = null) =>
        (uint)Math.Round(Math.Clamp(UserToStepsExact(value, unit), 0, uint.MaxValue));

    public static bool TryParseUnit(string? text, out UserUnit unit)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "um": case "µm": case "micron": unit = UserUnit.Um; return true;
            case "step": case "steps": case "pulse": unit = UserUnit.Step; return true;
            default: unit = UserUnit.Step; return false;
        }
    }
}

/// <summary>드라이브 설정 스냅샷 (GUI 표시용).</summary>
public sealed record DriveSettings(
    int AxisId,
    int Slave,
    string Profile,
    bool ReadFromDrive,
    uint ElectronicGearA,
    uint ElectronicGearB,
    string GearALabel,
    string GearBLabel,
    string ResolutionFormula,
    double BaseStepsPerRev,
    bool NeedsPulsesPerRev,
    double StepsPerRev,
    short GearRatioRaw,
    double GearRatio,
    bool SupportsGearRatio,
    bool SupportsMaintenanceCommands,
    double LeadUm,
    double StepsPerUm,
    string Unit,
    string PositionUnit,
    string VelocityUnit,
    string AccelerationUnit,
    uint ProfileVelocity,
    uint ProfileAcceleration,
    uint ProfileDeceleration);

/// <summary>
/// 드라이브 설정 변경 요청. null 인 항목은 건드리지 않는다.
/// 전자기어는 갱신 시점이 즉시가 아니므로(AZ: C, VPH: 재기동) 쓴 뒤 Configuration/NV 저장/재기동이 필요할 수 있다.
/// 속도/가감속은 우리 기본값을 바꾸고 SDO 로도 써서 저장 시 드라이브에 남게 한다.
/// </summary>
public sealed record DriveSettingsWrite(
    uint? ElectronicGearA = null,
    uint? ElectronicGearB = null,
    short? GearRatioRaw = null,
    double? LeadUm = null,
    /// <summary>VPH: 엔코더 분해능 [pulse/rev] (소프트웨어 설정)</summary>
    double? PulsesPerRev = null,
    string? Unit = null,
    uint? ProfileVelocity = null,
    uint? ProfileAcceleration = null,
    uint? ProfileDeceleration = null,
    bool RunConfiguration = false,
    bool SaveToNvm = false);

public sealed record DriveSettingsResult(bool Success, DriveSettings Settings, IReadOnlyList<string> Messages);
