using Microsoft.Extensions.Hosting;

namespace EcatMaster.Core.EtherCat;

public sealed record SlaveInfo(int Position, string Name, uint VendorId, uint ProductCode, string State, ushort AlStatus, string AlStatusText, int OutputBytes, int InputBytes);

public sealed record MasterStatus(
    string Mode,
    bool Running,
    bool AllOperational,
    string Interface,
    int SlaveCount,
    int AxisCount,
    int IoModuleCount,
    int ActiveInterlocks,
    int CycleTimeUs,
    long CycleCount,
    /// <summary>실시간 루프가 최근에 사이클을 수행했는지 (마지막 사이클 후 5주기 또는 200 ms 이내).</summary>
    bool CycleAlive,
    /// <summary>최근 1초 구간의 실측 사이클 수/초. 2 ms 주기면 500 근처여야 정상.</summary>
    double CyclesPerSecond,
    /// <summary>실측 평균 주기 [us] (= 1e6 / CyclesPerSecond).</summary>
    double ActualCycleTimeUs,
    int ExpectedWkc,
    int LastWkc,
    long MissedCycles,
    long WkcErrors,
    double MaxJitterUs,
    string? LastError,
    IReadOnlyList<SlaveInfo> Slaves);

/// <summary>
/// EtherCAT 마스터 추상화. 실제(SOEM)와 시뮬레이션 두 구현이 있고,
/// API 레이어는 이 인터페이스만 본다. IHostedService 라 앱 시작/종료와 수명이 같다.
/// </summary>
public interface IEtherCatMaster : IHostedService
{
    /// <summary>StartAsync 와 같지만 예외 대신 실패 사유 문자열을 반환한다 (GUI 용). 성공이면 null.</summary>
    Task<string?> TryStartAsync(CancellationToken cancellationToken);

    IReadOnlyList<Axis> Axes { get; }
    Axis? GetAxis(int id);

    /// <summary>번호("3") 또는 별칭("X")으로 축 찾기 (대소문자 무시).</summary>
    Axis? GetAxis(string idOrAlias);
    MasterStatus GetStatus();

    /// <summary>디지털 IO 모듈 (EL1889/EL2889 등). 축과 같은 사이클로 갱신된다.</summary>
    IReadOnlyList<IoModule> IoModules { get; }
    IoModule? GetIoModule(string nameOrId);

    /// <summary>인터록 규칙별 현재 상태.</summary>
    IReadOnlyList<InterlockStatus> GetInterlocks();

    /// <summary>인터록 규칙 사용/해제 (런타임). 이름으로 찾는다. 없으면 false.</summary>
    bool SetInterlockEnabled(string name, bool enabled);

    /// <summary>현재 규칙 목록 (설정 객체, 순서 = 표시 순서).</summary>
    IReadOnlyList<InterlockRule> InterlockRules { get; }

    /// <summary>규칙 검증만 (편집기). 문제 없으면 null. replacingName = 편집 중인 기존 규칙 이름.</summary>
    string? ValidateInterlock(InterlockRule rule, string? replacingName = null);

    /// <summary>규칙 목록 전체 교체 (런타임, 원자적). 같은 이름은 통계 이월. 반환 = 교체 후 상태.</summary>
    IReadOnlyList<InterlockStatus> ReplaceInterlocks(IReadOnlyList<InterlockRule> rules);
    IReadOnlyList<InterlockStatus> AddInterlock(InterlockRule rule);
    IReadOnlyList<InterlockStatus> UpdateInterlock(string name, InterlockRule rule);
    IReadOnlyList<InterlockStatus> RemoveInterlock(string name);

    /// <summary>입력 강제(IoModule.ForceInput) 허용 여부. 시뮬레이션에서만 true.</summary>
    bool SupportsInputForcing { get; }

    /// <summary>모든 축 Halt(감속 정지). quick=true 면 Quick Stop 후 서보 오프.</summary>
    void StopAll(bool quick);
    void EnableAll();
    void DisableAll();
    void FaultResetAll();

    /// <summary>지터·WKC 오류 등 누적 통계 초기화.</summary>
    void ResetStatistics();

    /// <summary>
    /// SDO 읽기 (디버그/설정 확인용). size 는 1, 2, 4 byte. 실시간 스레드와 병행 실행되므로 자주 호출하지 말 것.
    /// </summary>
    SdoResult SdoRead(int slave, ushort index, byte subIndex, int size);

    /// <summary>SDO 쓰기. 값은 size 바이트로 잘려 little-endian 으로 전송된다.</summary>
    SdoResult SdoWrite(int slave, ushort index, byte subIndex, int size, uint value);

    /// <summary>현재 보관 중인 드라이브 설정 스냅샷 (I/O 없음).</summary>
    DriveSettings? GetDriveSettings(int axisId);

    /// <summary>드라이브에서 전자기어(6091h), 기어비(47F1h), 속도/가감속(6081h/6083h/6084h)을 다시 읽는다.</summary>
    DriveSettingsResult ReadDriveSettings(int axisId);

    /// <summary>드라이브 설정 변경. null 인 항목은 유지. 필요 시 Configuration(40C6h) / NV 저장(40C9h) 실행.</summary>
    DriveSettingsResult WriteDriveSettings(int axisId, DriveSettingsWrite request);
}

public sealed record SdoResult(bool Success, uint Value, int Size, string? Error)
{
    public string ValueHex => Size switch { 1 => $"0x{Value:X2}", 2 => $"0x{Value:X4}", _ => $"0x{Value:X8}" };
    public static SdoResult Fail(string error) => new(false, 0, 0, error);
}
