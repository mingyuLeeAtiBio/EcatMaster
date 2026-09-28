// ============================================================================
// EcatMaster 샘플: GUI 없이 Core 만으로 특정 모터(축)에 명령 내리기
//
//   dotnet run                              시뮬레이션 (하드웨어 불필요)
//   dotnet run -- --soem I350               설명에 "I350" 이 들어간 NIC 로 실기 연결 (Npcap 필요)
//   dotnet run -- --axis Z                  대상 축: 번호("3") 또는 별칭("Z"). 기본 "X"
//   dotnet run -- --soem I350 --axis 2 --no-move   연결·상태 확인만 (모터를 움직이지 않음)
//
// 흐름: MasterOptions → 마스터 생성 → TryStartAsync → GetAxis → Submit(명령) → 상태 폴링(AxisControl 도우미)
// ============================================================================
using EcatMaster.Core.EtherCat;
using EcatMaster.Samples;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ---------------------------------------------------------------- 인자
string? nicFilter = null;      // null = 시뮬레이션
string axisKey = "X";
bool noMove = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--soem": nicFilter = i + 1 < args.Length ? args[++i] : ""; break;
        case "--axis": axisKey = args[++i]; break;
        case "--no-move": noMove = true; break;
    }
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; }).SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("Sample");

// ---------------------------------------------------------------- 1) 마스터 설정
// 축 별칭·리드(µm/rev)는 여기서 코드로 준다. GUI 는 같은 내용을 %LOCALAPPDATA%\EcatMaster\devices.json 에서 읽는다.
var opt = new MasterOptions
{
    Mode = nicFilter is null ? "Simulated" : "Soem",
    CycleTimeUs = 2000,
    AxisCount = 3,
    Axes =
    {
        new AxisOptions { Id = 1, Name = "X", LeadUm = 10_000 },
        new AxisOptions { Id = 2, Name = "Y", LeadUm = 10_000 },
        new AxisOptions { Id = 3, Name = "Z", LeadUm = 5_000 },
        // NCR-HD(VPH-HD) 축이면: new AxisOptions { Id = 1, Name = "X", Profile = "VPH-HD", PulsesPerRev = 1_048_576 }
    },
};
opt.Interlocks.Clear();      // 샘플은 인터록 없이. 실제 장비에서는 devices.json/appsettings 의 규칙을 그대로 넣을 것
opt.StatusOutputs.Clear();

if (nicFilter is not null)
{
    // NIC 이름(\Device\NPF_{GUID}) 은 Npcap 목록에서 고른다. 설명(Description)에 필터 문자열이 들어간 첫 어댑터.
    if (!SoemAdapters.IsNativeAvailable(out var nerr)) { log.LogError("SOEM 네이티브 없음: {Err}", nerr); return 2; }
    var nic = SoemAdapters.List().FirstOrDefault(a => a.Description.Contains(nicFilter, StringComparison.OrdinalIgnoreCase));
    if (nic is null) { log.LogError("NIC '{F}' 없음. 목록: {List}", nicFilter, string.Join(" | ", SoemAdapters.List().Select(a => a.Description))); return 2; }
    opt.Interface = nic.Name;
    log.LogInformation("NIC: {Nic}", nic);

    // (선택) 연결 전에 슬레이브가 있는지 먼저 확인 — GUI 자동 연결이 쓰는 것과 같은 탐색
    var probe = SoemAdapters.Probe(nic);
    log.LogInformation("탐색: {Summary}", probe.Summary);
    if (!probe.HasSlaves) return 2;
}

// ---------------------------------------------------------------- 2) 마스터 생성·시작
IEtherCatMaster master = nicFilter is null
    ? new SimulatedMaster(Options.Create(opt), loggerFactory.CreateLogger<SimulatedMaster>())
    : new SoemMaster(Options.Create(opt), loggerFactory.CreateLogger<SoemMaster>());

string? err = await master.TryStartAsync(CancellationToken.None);   // 예외 대신 실패 사유 문자열
if (err is not null) { log.LogError("연결 실패: {Err}", err); return 1; }

var st = master.GetStatus();
log.LogInformation("연결됨: {Mode}, 슬레이브 {Slaves}, 축 {Axes}, IO 모듈 {Io}, WKC 기대 {Wkc}", st.Mode, st.SlaveCount, st.AxisCount, st.IoModuleCount, st.ExpectedWkc);
foreach (var a in master.Axes) log.LogInformation("  {Axis}", AxisControl.Describe(a));

try
{
    // ---------------------------------------------------------------- 3) 특정 축 잡기 (번호 또는 별칭)
    Axis axis = master.GetAxis(axisKey) ?? throw new InvalidOperationException($"축 '{axisKey}' 없음");
    log.LogInformation("대상 축: {Name}({Id}) slave {Slave}, 프로파일 {Profile}, 단위 {Unit}, {Spu:0.###} step/{Unit}",
        axis.Name, axis.Id, axis.Slave, axis.Profile.Name, axis.Mechanics.UnitName, axis.Mechanics.StepsPerUm, axis.Mechanics.UnitName);

    if (axis.State is Cia402State.Fault or Cia402State.FaultReactionActive)
    {
        log.LogWarning("알람 상태 → 알람 리셋");
        await AxisControl.FaultResetAsync(axis);
    }

    // ---------------------------------------------------------------- 4) 서보 온
    await AxisControl.EnableAsync(axis);
    log.LogInformation("서보 온: {S}", AxisControl.Describe(axis));

    if (noMove)
    {
        log.LogInformation("--no-move: 이동 없이 종료");
    }
    else
    {
        // ---------------------------------------------------------------- 5) 절대 이동 (사용자 단위 µm, 속도 µm/s)
        double start = axis.GetStatus().PositionUser;
        await AxisControl.MoveAsync(axis, position: start + 2_000, velocity: 5_000);
        log.LogInformation("절대 이동 완료: {S}", AxisControl.Describe(axis));

        // ---------------------------------------------------------------- 6) 상대 이동 (-1000 µm)
        await AxisControl.MoveAsync(axis, position: -1_000, velocity: 5_000, relative: true);
        log.LogInformation("상대 이동 완료: {S}", AxisControl.Describe(axis));

        // ---------------------------------------------------------------- 7) step 단위로 직접 (전자기어 기준 지령 단위)
        await AxisControl.MoveAsync(axis, position: axis.Tx.PositionActual + 5_000, velocity: 20_000, unit: UserUnit.Step);
        log.LogInformation("step 이동 완료: {S}", AxisControl.Describe(axis));

        // ---------------------------------------------------------------- 8) 조그 0.5 초 후 Halt (조그 종료는 항상 Halt 로)
        await AxisControl.JogForAsync(axis, velocity: -3_000, durationMs: 500);
        log.LogInformation("조그 종료(Halt): {S}", AxisControl.Describe(axis));

        // ---------------------------------------------------------------- 9) 이동 중 정지: Halt(감속 정지, 서보 유지)
        axis.Submit(axis.MakeMove(false, start + 50_000, 2_000, null, null));
        await AxisControl.WaitAsync(() => axis.IsMotionActive, 1000);
        await Task.Delay(300);
        AxisControl.Halt(axis);
        await AxisControl.WaitAsync(() => axis.GetStatus().Velocity == 0, 3000);
        log.LogInformation("Halt 후: {S}  (State 는 OperationEnabled 유지, Activity=Halted)", AxisControl.Describe(axis));

        // ---------------------------------------------------------------- 10) 원위치 복귀
        await AxisControl.MoveAsync(axis, position: start, velocity: 10_000);
        log.LogInformation("원위치: {S}", AxisControl.Describe(axis));

        // ---------------------------------------------------------------- 11) 여러 축 동시 이동: 각 축에 Submit 후 함께 대기
        var others = master.Axes.Where(a => a.Id != axis.Id).Take(2).ToList();
        foreach (var a in others) await AxisControl.EnableAsync(a);
        var tasks = others.Select(a => AxisControl.MoveAsync(a, 1_000, 5_000, relative: true)).ToList();
        await Task.WhenAll(tasks);
        foreach (var a in others) log.LogInformation("동시 이동 완료: {S}", AxisControl.Describe(a));

        // ---------------------------------------------------------------- 12) A↔B 왕복 테스트 (Core 의 CycleTestRunner)
        var runner = new CycleTestRunner();
        runner.Start(axis, new CycleTestRequest(PointA: start, PointB: start + 1_000, Velocity: 10_000, Cycles: 3, DwellMs: 100));
        await AxisControl.WaitAsync(() => runner.GetStatus(axis.Id) is { Running: false }, 30_000);
        var cs = runner.GetStatus(axis.Id)!;
        log.LogInformation("왕복 테스트: {Phase}, 완료 {Done}회, 최대 위치 오차 {Err} step", cs.Phase, cs.CompletedCycles, cs.MaxPositionErrorSteps);
    }

    // ---------------------------------------------------------------- 13) IO / SDO (있을 때만)
    foreach (var io in master.IoModules)
        log.LogInformation("IO 모듈 {Name} (slave {Slave}): IN {In}bit OUT {Out}bit, 온라인 {On}", io.Name, io.Slave, io.InputBits, io.OutputBits, io.Online);
    var firstOut = master.IoModules.FirstOrDefault(m => m.OutputBits > 0);
    if (firstOut is not null && !noMove)
    {
        firstOut.SetOutput(0, true); await Task.Delay(200);            // OUT0 켜기
        log.LogInformation("{M}.OUT0 = {V}", firstOut.Name, firstOut.GetOutput(0));
        firstOut.SetOutput(0, false);
    }
    if (nicFilter is not null)
    {
        var r = master.SdoRead(axis.Slave, 0x6064, 0, 4);               // Position actual value (SDO)
        log.LogInformation("SDO 6064h(위치) = {V} ({Ok})", r.Value, r.Success ? "OK" : r.Error);
    }
}
catch (Exception ex)
{
    log.LogError("오류: {Msg}", ex.Message);
}
finally
{
    // ---------------------------------------------------------------- 14) 정리: 정지·서보 오프·해제
    master.StopAll(quick: false);
    await Task.Delay(200);
    master.DisableAll();
    await master.StopAsync(CancellationToken.None);
    log.LogInformation("해제 완료");
}
return 0;
