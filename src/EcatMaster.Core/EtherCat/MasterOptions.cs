namespace EcatMaster.Core.EtherCat;

/// <summary>appsettings.json 의 "Master" 섹션.</summary>
public sealed class MasterOptions
{
    public const string Section = "Master";

    /// <summary>"Soem" = 실제 EtherCAT, "Simulated" = 하드웨어 없이 가상 드라이브.</summary>
    public string Mode { get; set; } = "Simulated";

    /// <summary>EtherCAT 전용 NIC 이름 (Linux: enp2s0 / Windows: \Device\NPF_{GUID}).</summary>
    public string Interface { get; set; } = "eth0";

    /// <summary>사이클 주기 [us]. 12축 PP 모드면 2000 이 안전한 기본값.</summary>
    public int CycleTimeUs { get; set; } = 2000;

    /// <summary>실시간 스레드 SCHED_FIFO 우선순위 (1~99). Linux 는 CAP_SYS_NICE 필요.</summary>
    public int RealtimePriority { get; set; } = 80;

    /// <summary>실시간 스레드를 고정할 CPU 코어. -1 이면 고정 안 함. isolcpus 와 맞출 것.</summary>
    public int CpuCore { get; set; } = -1;

    /// <summary>제어할 최대 축 수. 슬레이브 순서(1..N)가 곧 축 번호.</summary>
    public int AxisCount { get; set; } = 12;

    /// <summary>
    /// 축별 설정: 별칭(Name)과 리드. 축 번호(Id)는 등록 순서 1..N.
    /// 예) [{ "Id": 1, "Name": "X", "LeadUm": 10000 }, { "Id": 2, "Name": "Y" }]
    /// 별칭은 API 경로(/api/axes/X)와 GUI 에서 번호 대신 쓸 수 있다. 숫자만으로 된 별칭은 피할 것.
    /// </summary>
    public List<AxisOptions> Axes { get; set; } = new();

    /// <summary>(구형) 축 이름 배열. Axes 에 항목이 없을 때만 사용.</summary>
    public string[] AxisNames { get; set; } = [];

    /// <summary>축 별칭 조회: Axes → AxisNames → "Axis{n}".</summary>
    public string AxisNameFor(int axisId)
    {
        var ax = Axes.FirstOrDefault(a => a.Id == axisId);
        if (ax is not null && !string.IsNullOrWhiteSpace(ax.Name)) return ax.Name!.Trim();
        if (axisId - 1 < AxisNames.Length && !string.IsNullOrWhiteSpace(AxisNames[axisId - 1])) return AxisNames[axisId - 1].Trim();
        return $"Axis{axisId}";
    }

    /// <summary>PDO 재매핑을 적용할 벤더 ID. Oriental Motor = 0x2BE.</summary>
    public uint VendorId { get; set; } = 0x2BE;

    /// <summary>PreOp 에서 1600h/1A00h 를 <see cref="AzdPdoMap"/> 대로 다시 쓴다 (AZ 기본 매핑은 3개 엔트리뿐).</summary>
    public bool RemapPdo { get; set; } = true;

    /// <summary>
    /// 연결 시 605Ah(Quick stop option) / 605Dh(Halt option) 를 우리 값(2 / 1)으로 덮어쓴다.
    /// 드라이브 셋업이 끝난 장비에서는 false 로 두고 드라이브 값을 그대로 쓴다.
    /// </summary>
    public bool WriteOptionCodes { get; set; } = false;

    /// <summary>
    /// DC(분산 클록) 강제 사용. false 여도 DC SYNC0 이 필수인 프로파일(VPH-HD)이 하나라도 있으면 자동으로 켠다.
    /// </summary>
    public bool UseDistributedClocks { get; set; } = false;

    /// <summary>SYNC0 시프트 [ns]. 슬레이브의 SYNC0 이벤트를 사이클 시작 기준으로 이만큼 늦춘다.</summary>
    public int DcSync0ShiftNs { get; set; } = 0;

    /// <summary>
    /// 마스터 프레임 송신 시각을 DC 기준 시각의 (사이클 경계 + 이 값)에 맞춘다 [ns]. SOEM 예제 기본 50 µs.
    /// Windows 처럼 지터가 큰 환경에서는 사이클의 1/4 정도(예: 2 ms → 500 µs)로 키워 SYNC0 전 도착 여유를 확보한다.
    /// </summary>
    public int DcSyncOffsetNs { get; set; } = 50_000;

    /// <summary>IOmap 크기 [byte]. 12축 × 40byte 면 4096 으로 충분.</summary>
    public int IoMapSize { get; set; } = 4096;

    /// <summary>OP 전환 후 WKC 불일치가 이 사이클 수 이상 이어지면 축을 오프라인으로 표시.</summary>
    public int OfflineAfterMissedCycles { get; set; } = 10;

    /// <summary>프로세스 데이터 수신 대기 [us]. 사이클보다 작아야 한다. Windows(Npcap)에서 WKC 오류가 잦으면 늘려 본다.</summary>
    public int ReceiveTimeoutUs { get; set; } = 2000;

    /// <summary>시작 직후 이 사이클 수 동안은 지터 통계에서 제외 (JIT, 캐시 워밍업).</summary>
    public int JitterWarmupCycles { get; set; } = 500;

    public MotionDefaults Defaults { get; set; } = new();

    public UnitOptions Units { get; set; } = new();

    /// <summary>IO 모듈 이름/비트 이름 지정 또는 제외 (선택). 없으면 벤더·PDO 로 자동 분류하고 IO1, IO2… 로 이름 붙임.</summary>
    public List<IoModuleOptions> IoModules { get; set; } = new();

    /// <summary>인터록 규칙: 입력 비트 → 축 정지/동작 차단.</summary>
    public List<InterlockRule> Interlocks { get; set; } = new();

    /// <summary>마스터 상태 → 출력 비트 (램프, 부저, PLC 신호).</summary>
    public List<StatusOutputRule> StatusOutputs { get; set; } = new();

    /// <summary>시뮬레이션 모드에서 만들 가상 IO 모듈. 기본: DI1(16 입력), DO1(16 출력).</summary>
    public List<SimulatedIoModuleOptions> SimulatedIoModules { get; set; } = new()
    {
        new SimulatedIoModuleOptions { Name = "DI1", InputBits = 16, OutputBits = 0 },
        new SimulatedIoModuleOptions { Name = "DO1", InputBits = 0, OutputBits = 16 },
    };
}

/// <summary>
/// 명령에 값이 없을 때 쓰는 프로파일 기본값. 단위는 드라이브 원단위(Hz, step/s²).
/// <see cref="UseDriveValues"/> 가 true 면 연결 시 드라이브의 6081h/6083h/6084h 를 읽어 이 값을 덮어쓴다.
/// </summary>
public sealed class MotionDefaults
{
    /// <summary>연결 시 드라이브에 저장된 속도·가감속을 읽어 기본값으로 사용 (권장).</summary>
    public bool UseDriveValues { get; set; } = true;

    /// <summary>6081h Profile velocity [Hz]. AZ 초기값 10,000</summary>
    public uint Velocity { get; set; } = 10_000;

    /// <summary>6083h Profile acceleration [step/s²]. AZ 초기값 300,000</summary>
    public uint Acceleration { get; set; } = 300_000;

    /// <summary>6084h Profile deceleration [step/s²]. AZ 초기값 300,000</summary>
    public uint Deceleration { get; set; } = 300_000;

    /// <summary>set-point ack / homing 완료를 기다리는 최대 사이클 수.</summary>
    public int AckTimeoutCycles { get; set; } = 500;

    public MotionDefaults Clone() => (MotionDefaults)MemberwiseClone();
}

/// <summary>축 하나의 사용자 설정.</summary>
public sealed class AxisOptions
{
    /// <summary>축 번호 (등록 순서, 1-based)</summary>
    public int Id { get; set; }

    /// <summary>별칭. 예: "X", "Lift", "Conveyor1"</summary>
    public string? Name { get; set; }

    /// <summary>이 축의 리드 [µm/rev]. null/0 이면 Units.DefaultLeadUm 또는 Units.AxisLeadUm.</summary>
    public double? LeadUm { get; set; }

    /// <summary>
    /// 엔코더 분해능 [pulse/rev]. VPH-HD 처럼 지령단위가 엔코더 펄스 기반인 드라이브에 필요 (예: 20 bit = 1048576).
    /// AZ 는 10,000 고정이라 무시.
    /// </summary>
    public double? PulsesPerRev { get; set; }

    /// <summary>프로파일 강제 지정 ("AZD" | "VPH-HD"). 비우면 벤더 ID 로 자동.</summary>
    public string? Profile { get; set; }
}

/// <summary>사용자 단위 설정. 전자기어·기어비는 드라이브에서 읽고, 리드만 여기서 준다.</summary>
public sealed class UnitOptions
{
    /// <summary>"step" 또는 "um". API 와 GUI 의 위치/속도/가감속 입력·표시 단위.</summary>
    public string Mode { get; set; } = "um";

    /// <summary>출력축 1회전당 이동량 [µm] 기본값 (예: 볼스크류 리드 10 mm = 10000).</summary>
    public double DefaultLeadUm { get; set; } = 10_000;

    /// <summary>축별 리드 [µm] (인덱스 0 = 1축). 0 이나 항목 없음이면 DefaultLeadUm.</summary>
    public double[] AxisLeadUm { get; set; } = [];

    public double LeadFor(int axisId) =>
        axisId - 1 < AxisLeadUm.Length && AxisLeadUm[axisId - 1] > 0 ? AxisLeadUm[axisId - 1] : DefaultLeadUm;

    public UserUnit UnitMode => DriveMechanics.TryParseUnit(Mode, out var u) ? u : UserUnit.Step;
}
