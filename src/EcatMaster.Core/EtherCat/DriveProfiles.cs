using EcatMaster.Core.Native;
using Microsoft.Extensions.Logging;

namespace EcatMaster.Core.EtherCat;

/// <summary>
/// 드라이브 벤더별 차이를 한곳에 모은 프로파일. 마스터·축 로직은 CiA402 공통부만 알고, 다음은 프로파일에 묻는다.
/// PDO 구성, DC 동기 필요 여부, PV 모드 지원, 가감속 전달 경로(PDO/SDO), 기구 해석(분해능 규칙), 유지보수 명령.
/// </summary>
public interface IDriveProfile
{
    string Name { get; }
    uint VendorId { get; }
    bool Matches(uint vendorId, uint productCode);

    /// <summary>1600h 에 쓸 RxPDO 엔트리 (index&lt;&lt;16 | sub&lt;&lt;8 | bits). <see cref="AzdRxPdo"/> 필드 중 일부일 수 있다.</summary>
    uint[] RxEntries { get; }
    /// <summary>1A00h 에 쓸 TxPDO 엔트리.</summary>
    uint[] TxEntries { get; }

    /// <summary>DC SYNC0 없이는 OP 로 가지 않는 드라이브 (VPH-HD).</summary>
    bool RequiresDcSync { get; }
    /// <summary>PV(Profile Velocity) 모드 지원. 없으면 조그를 PP 상대 이동으로 구현.</summary>
    bool SupportsProfileVelocity { get; }
    /// <summary>6083h/6084h 가 PDO 매핑 가능한가. 아니면 이동 전 SDO 로 써야 한다.</summary>
    bool AccelerationViaPdo { get; }
    /// <summary>
    /// PP 모드 컨트롤워드 Bit5(Change set immediately) 를 세워도 되는가.
    /// VPH-HD 는 Bit5=1 이면 커맨드 경고 603Fh=FFF3h 를 내고 set-point 를 거부하며,
    /// 진행 중인 위치결정 위에 새 set-point 를 걸 때는 Target reached=1 이거나 Halt 로 먼저 중단해야 한다.
    /// </summary>
    bool SupportsChangeSetImmediately { get; }

    // ---- 드라이브 원단위 라벨 ----
    string PositionUnit { get; }
    string VelocityUnit { get; }
    string AccelerationUnit { get; }

    // ---- 기구 해석 ----
    /// <summary>6091h:01 / :02 의 의미 (표시용).</summary>
    string GearALabel { get; }
    string GearBLabel { get; }
    /// <summary>분해능 계산식 설명 (표시용).</summary>
    string ResolutionFormula { get; }
    /// <summary>47F1h 기어비 오브젝트(Oriental 고유) 지원.</summary>
    bool SupportsGearRatioObject { get; }
    /// <summary>40C6h Configuration / 40C9h NV 저장 (Oriental 고유) 지원.</summary>
    bool SupportsMaintenanceCommands { get; }
    /// <summary>축 설정(PulsesPerRev)이 분해능 계산에 필요한가.</summary>
    bool NeedsPulsesPerRev { get; }

    /// <summary>PreOp→SafeOp 훅: PDO 재매핑 등.</summary>
    bool Configure(int slave, bool writeOptionCodes, ILogger log);

    /// <summary>SafeOp 이후 SDO 로 기구·기본값을 읽어 축에 반영.</summary>
    bool ReadMechanics(Axis axis, List<string> messages);
}

/// <summary>등록된 프로파일 목록. 벤더/제품 코드로 찾는다.</summary>
public static class DriveProfileRegistry
{
    public static readonly IDriveProfile Azd = new AzdProfile();
    public static readonly IDriveProfile Vph = new VphProfile();

    public static IReadOnlyList<IDriveProfile> All { get; } = [Azd, Vph];

    public static IDriveProfile? Find(uint vendorId, uint productCode) =>
        All.FirstOrDefault(p => p.Matches(vendorId, productCode));

    public static IDriveProfile? FindByName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>PDO 엔트리 목록에 따라 IOmap 바이트와 <see cref="AzdRxPdo"/>/<see cref="AzdTxPdo"/> 사이를 옮긴다 (할당 없음).</summary>
public static unsafe class PdoLayout
{
    public static int SizeBytes(uint[] entries)
    {
        int bits = 0;
        foreach (var e in entries) bits += (int)(e & 0xFF);
        return (bits + 7) / 8;
    }

    /// <summary>축의 논리 Rx 값을 드라이브 PDO 레이아웃으로 기록. 목록에 없는 필드는 보내지 않는다.</summary>
    public static void WriteRx(byte* dst, uint[] entries, in AzdRxPdo rx)
    {
        int off = 0;
        foreach (var e in entries)
        {
            int index = (int)(e >> 16), bytes = (int)(e & 0xFF) / 8;
            switch (index)
            {
                case 0x6040: *(ushort*)(dst + off) = rx.ControlWord; break;
                case 0x6060: *(sbyte*)(dst + off) = rx.ModesOfOperation; break;
                case 0x607A: *(int*)(dst + off) = rx.TargetPosition; break;
                case 0x6081: *(uint*)(dst + off) = rx.ProfileVelocity; break;
                case 0x6083: *(uint*)(dst + off) = rx.ProfileAcceleration; break;
                case 0x6084: *(uint*)(dst + off) = rx.ProfileDeceleration; break;
                case 0x60FF: *(int*)(dst + off) = rx.TargetVelocity; break;
                default: for (int i = 0; i < bytes; i++) dst[off + i] = 0; break;   // 모르는 항목은 0
            }
            off += bytes;
        }
    }

    /// <summary>드라이브 PDO 레이아웃에서 논리 Tx 값을 읽는다. 목록에 없는 필드는 그대로 둔다.</summary>
    public static void ReadTx(byte* src, uint[] entries, ref AzdTxPdo tx)
    {
        int off = 0;
        foreach (var e in entries)
        {
            int index = (int)(e >> 16), bytes = (int)(e & 0xFF) / 8;
            switch (index)
            {
                case 0x6041: tx.StatusWord = *(ushort*)(src + off); break;
                case 0x6061: tx.ModesOfOperationDisplay = *(sbyte*)(src + off); break;
                case 0x6064: tx.PositionActual = *(int*)(src + off); break;
                case 0x606C: tx.VelocityActual = *(int*)(src + off); break;
                case 0x603F: tx.ErrorCode = *(ushort*)(src + off); break;
                case 0x60FD: tx.DigitalInputs = *(uint*)(src + off); break;
            }
            off += bytes;
        }
    }
}

/// <summary>1600h/1A00h 재매핑 공통 절차 (CoE 표준: 할당 해제 → 엔트리 수 0 → 엔트리 기록 → 엔트리 수 → 할당).</summary>
public static class PdoMappingWriter
{
    public const ushort SmRxAssign = 0x1C12;
    public const ushort SmTxAssign = 0x1C13;

    public static bool WriteBoth(int slave, uint[] rx, uint[] tx, ILogger log)
    {
        bool ok = Write(slave, SmRxAssign, AzdPdoMap.RxPdoIndex, rx, log);
        ok &= Write(slave, SmTxAssign, AzdPdoMap.TxPdoIndex, tx, log);
        return ok;
    }

    public static bool Write(int slave, ushort smAssign, ushort pdoIndex, uint[] entries, ILogger log)
    {
        bool ok = true;
        ok &= Check(SoemNative.SdoWrite(slave, smAssign, 0, (byte)0), slave, $"{smAssign:X4}h:00=0", log);
        ok &= Check(SoemNative.SdoWrite(slave, pdoIndex, 0, (byte)0), slave, $"{pdoIndex:X4}h:00=0", log);
        for (int i = 0; i < entries.Length; i++)
            ok &= Check(SoemNative.SdoWrite(slave, pdoIndex, (byte)(i + 1), entries[i]), slave, $"{pdoIndex:X4}h:{i + 1:X2}={entries[i]:X8}", log);
        ok &= Check(SoemNative.SdoWrite(slave, pdoIndex, 0, (byte)entries.Length), slave, $"{pdoIndex:X4}h:00={entries.Length}", log);
        ok &= Check(SoemNative.SdoWrite(slave, smAssign, 1, pdoIndex), slave, $"{smAssign:X4}h:01={pdoIndex:X4}", log);
        ok &= Check(SoemNative.SdoWrite(slave, smAssign, 0, (byte)1), slave, $"{smAssign:X4}h:00=1", log);
        return ok;
    }

    private static bool Check(bool result, int slave, string what, ILogger log)
    {
        if (!result) log.LogError("Slave {Slave}: SDO write {What} failed: {Err}", slave, what, SoemNative.LastError());
        return result;
    }
}

// =====================================================================================================
// Oriental Motor AZ 시리즈 (AZD-CED/KED/AED)
// =====================================================================================================

public sealed class AzdProfile : IDriveProfile
{
    public string Name => "AZD";
    public uint VendorId => 0x2BE;
    public bool Matches(uint vendorId, uint productCode) => vendorId == VendorId;

    public uint[] RxEntries => AzdPdoMap.RxEntries;
    public uint[] TxEntries => AzdPdoMap.TxEntries;

    public bool RequiresDcSync => false;
    public bool SupportsProfileVelocity => true;
    public bool AccelerationViaPdo => true;
    public bool SupportsChangeSetImmediately => true;

    public string PositionUnit => "step";
    public string VelocityUnit => "Hz";
    public string AccelerationUnit => "step/s²";

    public string GearALabel => "전자기어 A (6091h:01)";
    public string GearBLabel => "전자기어 B (6091h:02)";
    public string ResolutionFormula => "분해능 = 10,000 × B / A [step/rev]";
    public bool SupportsGearRatioObject => true;
    public bool SupportsMaintenanceCommands => true;
    public bool NeedsPulsesPerRev => false;

    public bool Configure(int slave, bool writeOptionCodes, ILogger log) =>
        AzdDriveConfigurator.Configure(slave, writeOptionCodes, log);

    public bool ReadMechanics(Axis axis, List<string> messages)
    {
        int s = axis.Slave;
        var m = axis.Mechanics;
        bool ok = true;
        m.BaseStepsPerRev = DriveMechanics.AzdBaseStepsPerRev;

        if (SoemNative.SdoRead(s, 0x6091, 1, out uint gearA) && SoemNative.SdoRead(s, 0x6091, 2, out uint gearB))
        {
            m.ElectronicGearA = gearA;
            m.ElectronicGearB = gearB;
            m.ReadFromDrive = true;
        }
        else
        {
            ok = false;
            messages.Add($"6091h 전자기어 읽기 실패: {SoemNative.LastError()} — 기본 10,000 step/rev 로 간주");
        }

        if (SoemNative.SdoRead(s, 0x47F1, 0, out short gearRatio)) m.GearRatioRaw = gearRatio;
        else messages.Add($"47F1h 기어비 읽기 실패: {SoemNative.LastError()} — 1.0 으로 간주");

        ok &= ReadProfileDefaults(axis, messages);
        return ok;
    }

    internal static bool ReadProfileDefaults(Axis axis, List<string> messages)
    {
        if (!axis.Defaults.UseDriveValues) return true;
        int s = axis.Slave;
        bool ok = true;
        if (SoemNative.SdoRead(s, 0x6081, 0, out uint vel) && vel > 0) axis.Defaults.Velocity = vel; else ok = false;
        if (SoemNative.SdoRead(s, 0x6083, 0, out uint acc) && acc > 0) axis.Defaults.Acceleration = acc; else ok = false;
        if (SoemNative.SdoRead(s, 0x6084, 0, out uint dec) && dec > 0) axis.Defaults.Deceleration = dec; else ok = false;
        if (!ok) messages.Add("6081h/6083h/6084h 일부 읽기 실패 — 설정 파일 기본값 사용");
        return ok;
    }
}

// =====================================================================================================
// Nikki Denso VPH-HD (NCR-HD) — 매뉴얼 K_TJ-42620 Ver.1.5
//   · pv/tq 미지원 → 조그는 PP 상대 이동 · DC SYNC0 필수 · 6083h/6084h 는 PDO 매핑 불가(SDO, 정지 시 반영)
//   · 지령단위/rev = 엔코더 분해능 × 6091h:02(분모) / 6091h:01(분자)   (P162/P163 초기값)
//   · P 파라미터 = 3000h + 번호 (P162 = 30A2h)
// =====================================================================================================

public sealed class VphProfile : IDriveProfile
{
    public string Name => "VPH-HD";
    public uint VendorId => 0x816;
    public bool Matches(uint vendorId, uint productCode) => vendorId == VendorId;

    /// <summary>1600h: 6040 / 6060 / 607A / 6081 / 60FF (15 byte). 6083h·6084h 는 매핑 불가.</summary>
    public uint[] RxEntries { get; } =
    {
        0x6040_0010, // Controlword
        0x6060_0008, // Modes of operation
        0x607A_0020, // Target position
        0x6081_0020, // Profile velocity
        0x60FF_0020, // Target velocity (csv 용, 현재 미사용)
    };

    /// <summary>1A00h: AZD 와 동일 (모두 매핑 가능).</summary>
    public uint[] TxEntries => AzdPdoMap.TxEntries;

    public bool RequiresDcSync => true;
    public bool SupportsProfileVelocity => false;
    public bool AccelerationViaPdo => false;
    public bool SupportsChangeSetImmediately => false;   // 매뉴얼 표 3-115: Bit5 는 '0' (1 이면 FFF3h 경고)

    public string PositionUnit => "cmd";
    public string VelocityUnit => "cmd/s";
    public string AccelerationUnit => "cmd/s²";

    public string GearALabel => "전자기어 분자 (6091h:01, P162)";
    public string GearBLabel => "전자기어 분모 (6091h:02, P163)";
    public string ResolutionFormula => "지령단위/rev = 엔코더 분해능 × 분모 / 분자";
    public bool SupportsGearRatioObject => false;
    public bool SupportsMaintenanceCommands => false;
    public bool NeedsPulsesPerRev => true;

    public bool Configure(int slave, bool writeOptionCodes, ILogger log)
    {
        // 1600h/1A00h 는 자유 매핑(최대 10 엔트리, 총 40 byte 이내). 할당 1C12h/1C13h 도 RW.
        bool ok = PdoMappingWriter.WriteBoth(slave, RxEntries, TxEntries, log);
        if (SoemNative.SdoRead(slave, 0x605A, 0, out short qs))
            log.LogInformation("Slave {S}: VPH-HD 605Ah quick stop option = {Qs} (0 프리런 / 1 감속 후 토크프리), 605Dh halt = 1 고정", slave, qs);
        return ok;
    }

    public bool ReadMechanics(Axis axis, List<string> messages)
    {
        int s = axis.Slave;
        var m = axis.Mechanics;
        bool ok = true;

        if (SoemNative.SdoRead(s, 0x6091, 1, out uint num) && SoemNative.SdoRead(s, 0x6091, 2, out uint den))
        {
            m.ElectronicGearA = num;   // 분자
            m.ElectronicGearB = den;   // 분모
            m.ReadFromDrive = true;
        }
        else
        {
            ok = false;
            messages.Add($"6091h 전자기어비 읽기 실패: {SoemNative.LastError()} — 1/1 로 간주");
        }

        if (m.BaseStepsPerRev <= 0)
        {
            m.BaseStepsPerRev = DriveMechanics.VphDefaultPulsesPerRev;
            messages.Add($"엔코더 분해능(PulsesPerRev)이 설정되지 않아 {DriveMechanics.VphDefaultPulsesPerRev:N0} pulse/rev 로 가정 — 모터 사양으로 확인 필요");
        }

        ok &= AzdProfile.ReadProfileDefaults(axis, messages);
        return ok;
    }
}
