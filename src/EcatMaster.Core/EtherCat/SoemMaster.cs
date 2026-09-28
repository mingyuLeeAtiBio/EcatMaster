using System.Runtime;
using System.Runtime.InteropServices;
using EcatMaster.Core.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EcatMaster.Core.EtherCat;

/// <summary>
/// SOEM 기반 실제 EtherCAT 마스터.
/// 시작: init → config_init → (PO2SO 훅에서 프로파일별 PDO 재매핑) → config_map → DC 설정 → SafeOp → OP.
/// 실행: 실시간 스레드가 CycleTimeUs 마다 send/receive → IO 입력 → 인터록 → 축 Tick → 출력. DC 사용 시 기준 클록에 위상 동기.
///       감시 스레드가 슬레이브 복구, 가감속 SDO 동기(6083h/6084h 가 PDO 불가인 드라이브), 이벤트 로그를 맡는다.
/// </summary>
public sealed unsafe class SoemMaster : MasterBase, IDisposable
{
    private readonly ILogger<SoemMaster> _log;
    private readonly SoemNative.Po2SoCallback _po2so;   // GC 방지용으로 필드에 보관

    private void* _ioMap;
    private Thread? _cyclicThread;
    private Thread? _supervisorThread;
    private volatile bool _running;
    private volatile bool _inOp;

    private int _slaveCount;
    private int _expectedWkc;
    private volatile int _lastWkc;
    private long _cycleCount;
    private long _missedCycles;
    private long _wkcErrors;
    private long _maxJitterNs;
    private int _consecutiveWkcErrors;
    private volatile string? _lastError;

    // DC
    private bool _dcEnabled;
    private long _dcIntegral;
    private long _dcLastToffNs;

    /// <summary>축의 IOmap 위치와 PDO 레이아웃 캐시 (config_map 이후 고정).</summary>
    private struct AxisMapRef
    {
        public byte* In; public byte* Out; public uint[] RxEntries; public uint[] TxEntries;
    }
    private AxisMapRef[] _axisRefs = [];

    /// <summary>IO 모듈의 IOmap 위치 캐시.</summary>
    private struct IoMapRef
    {
        public byte* In; public int InStart; public int InBits;
        public byte* Out; public int OutStart; public int OutBits;
    }
    private IoMapRef[] _ioRefs = [];

    /// <summary>PO2SO 훅에서 슬레이브 → 프로파일 조회용 (BuildAxes 이전이므로 벤더로만 판단).</summary>
    private readonly Dictionary<int, IDriveProfile> _slaveProfiles = new();

    public SoemMaster(IOptions<MasterOptions> options, ILogger<SoemMaster> log) : base(options)
    {
        _log = log;
        _po2so = OnPreOpToSafeOp;
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>호스팅용: 실패하면 예외. GUI 는 디버거 중단을 피하려면 <see cref="TryStartAsync"/> 를 쓴다.</summary>
    public override Task StartAsync(CancellationToken cancellationToken) =>
        TryStartAsync(cancellationToken).ContinueWith(t =>
        {
            if (t.Result is not null) throw new InvalidOperationException(t.Result);
        }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);   // unsafe 클래스라 await 불가

    /// <summary>
    /// 초기화·OP 전환·스레드 시작. 실패하면 소켓을 닫고 사유 문자열을 반환한다 (예외를 Task 경계 밖으로 던지지 않으므로
    /// Visual Studio 의 "사용자가 처리하지 않은 예외" 중단이 나지 않는다).
    /// </summary>
    public override Task<string?> TryStartAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                Initialize();
                _running = true;

                _cyclicThread = new Thread(CyclicLoop) { Name = "ecat-cyclic", IsBackground = true, Priority = ThreadPriority.Highest };
                _supervisorThread = new Thread(SupervisorLoop) { Name = "ecat-supervisor", IsBackground = true };
                _cyclicThread.Start();
                _supervisorThread.Start();
                return (string?)null;
            }
            catch (Exception ex)
            {
                _log.LogError("EtherCAT 시작 실패: {Msg}", ex.Message);
                CleanupAfterFailedStart();
                return ex.Message;
            }
        }, cancellationToken);
    }

    /// <summary>시작 실패 시 슬레이브를 INIT 으로 내리고 소켓을 닫아, 재시도(재연결)가 가능하게 한다.</summary>
    private void CleanupAfterFailedStart()
    {
        try
        {
            if (_slaveCount > 0) SoemNative.shim_request_state(0, SoemNative.EC_STATE_INIT);
        }
        catch { /* ignore */ }
        try { SoemNative.shim_close(); } catch { /* ignore */ }
        _slaveCount = 0;
        _inOp = false;
        _dcEnabled = false;
        AxisList.Clear();
        IoModuleList.Clear();
        ClearInterlocks();
        _axisRefs = [];
        _ioRefs = [];
        Dispose();
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _running = false;
        _cyclicThread?.Join(TimeSpan.FromSeconds(2));
        _supervisorThread?.Join(TimeSpan.FromSeconds(2));
        ResetCycleTracking();

        if (_slaveCount > 0)
        {
            if (_dcEnabled)
                foreach (var a in AxisList) if (a.Profile.RequiresDcSync) SoemNative.shim_dcsync0(a.Slave, 0, 0, 0);
            SoemNative.shim_request_state(0, SoemNative.EC_STATE_INIT);
        }
        SoemNative.shim_close();
        _log.LogInformation("EtherCAT master stopped");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_ioMap != null)
        {
            NativeMemory.Free(_ioMap);
            _ioMap = null;
        }
    }

    // ------------------------------------------------------------------ init

    private void Initialize()
    {
        var o = Options;
        SoemNative.EnsureNativeSearchPath();
        _log.LogInformation("Starting SOEM on {If}, cycle {Cycle} us, rx timeout {Rx} us", o.Interface, o.CycleTimeUs, o.ReceiveTimeoutUs);
        if (OperatingSystem.IsWindows()) _log.LogInformation("{Npcap}", SoemNative.DescribeNpcap());

        if (SoemNative.shim_init(o.Interface) == 0)
        {
            string hint = OperatingSystem.IsWindows()
                ? "Npcap 설치 여부, 어댑터 이름(\\Device\\NPF_{GUID}), Npcap 을 '관리자만 접근' 옵션으로 설치했다면 관리자 권한 실행 여부를 확인하세요."
                : "CAP_NET_RAW 권한 또는 root 가 필요합니다. systemd 유닛의 AmbientCapabilities 를 확인하세요.";
            throw new InvalidOperationException($"ec_init({o.Interface}) failed: {SoemNative.LastError()} — {hint}");
        }

        _slaveCount = SoemNative.shim_config_init();
        if (_slaveCount <= 0)
            throw new InvalidOperationException($"No EtherCAT slaves found on {o.Interface}: {SoemNative.LastError()}");

        _log.LogInformation("Found {N} slaves", _slaveCount);
        _slaveProfiles.Clear();
        for (int s = 1; s <= _slaveCount; s++)
        {
            uint v = SoemNative.shim_slave_vendor_id(s), p = SoemNative.shim_slave_product_code(s);
            var prof = DriveProfileRegistry.Find(v, p);
            if (prof is not null) _slaveProfiles[s] = prof;
            _log.LogInformation("  [{S}] {Name} vendor=0x{V:X} product=0x{P:X}{Prof}", s, SoemNative.SlaveName(s), v, p,
                prof is null ? "" : $" → 드라이브 프로파일 {prof.Name}");
        }

        // DC 는 SAFE_OP 요청(config_map 내부) 이전에 준비해야 한다. VPH-HD 처럼 DC SYNC0 만 지원하는 드라이브는
        // SYNC0 이 켜져 있지 않으면 PRE_OP→SAFE_OP 전이를 거부한다 (AL 0x0011).
        ConfigureDistributedClocks();

        if (o.RemapPdo || _dcEnabled) SoemNative.shim_set_po2so_callback(_po2so);

        _ioMap = NativeMemory.AllocZeroed((nuint)o.IoMapSize);
        int used = SoemNative.shim_config_map(_ioMap, o.IoMapSize);
        _log.LogInformation("IOmap uses {Used} bytes", used);
        if (used > o.IoMapSize)
            throw new InvalidOperationException($"IOmap overflow: need {used} > {o.IoMapSize}. Master:IoMapSize 를 늘리세요.");

        BuildAxes();

        SoemNative.shim_state_check(0, SoemNative.EC_STATE_SAFE_OP, SoemNative.EC_TIMEOUTSTATE * 4);
        _expectedWkc = SoemNative.shim_expected_wkc();

        GoOperational();
    }

    /// <summary>
    /// DC 강제 옵션이 켜져 있거나 DC SYNC0 이 필수인 프로파일의 슬레이브가 있으면 ec_configdc 로 DC 를 준비한다 (PRE_OP 에서 실행).
    /// 슬레이브별 SYNC0 활성화는 PO2SO 훅(<see cref="OnPreOpToSafeOp"/>)에서 SAFE_OP 요청 직전에 한다.
    /// </summary>
    private void ConfigureDistributedClocks()
    {
        bool needDc = Options.UseDistributedClocks || _slaveProfiles.Values.Any(p => p.RequiresDcSync);
        if (!needDc) return;

        int found = SoemNative.shim_dc_config();
        _dcEnabled = found != 0;
        if (!_dcEnabled)
        {
            _log.LogWarning("DC 가 필요하지만 DC 지원 슬레이브를 찾지 못했습니다 (ec_configdc = 0)");
            return;
        }
        _log.LogInformation("DC 준비 완료: 주기 {Cycle} us, SYNC0 시프트 {Shift} ns, 마스터 위상 오프셋 {Off} us{Win}",
            Options.CycleTimeUs, Options.DcSync0ShiftNs, Options.DcSyncOffsetNs / 1000.0,
            OperatingSystem.IsWindows() ? " — Windows 는 지터가 커서 SYNC0 전 도착 여유가 부족할 수 있음. 동기 오류 시 DcSyncOffsetNs 를 키우거나 사이클을 늘릴 것" : "");
    }

    /// <summary>PO2SO 훅 안에서 호출: DC 필수 프로파일 슬레이브에 SYNC0 을 켠다.</summary>
    private void EnableSync0(int slave, IDriveProfile profile)
    {
        if (!profile.RequiresDcSync) return;
        if (!_dcEnabled)
        {
            _log.LogWarning("Slave {S}: {Prof} 는 DC SYNC0 이 필수지만 DC 가 준비되지 않아 SAFE_OP 전이가 거부될 수 있음", slave, profile.Name);
            return;
        }
        if (SoemNative.shim_slave_hasdc(slave) == 0)
        {
            _log.LogWarning("Slave {S}: 프로파일은 DC 필수지만 슬레이브가 DC 를 지원하지 않는다고 보고", slave);
            return;
        }
        uint cycleNs = (uint)Options.CycleTimeUs * 1000u;
        SoemNative.shim_dcsync0(slave, 1, cycleNs, Options.DcSync0ShiftNs);
        _log.LogInformation("Slave {S}: DC SYNC0 활성 (주기 {Cycle} ns, 시프트 {Shift} ns) — SAFE_OP 요청 전", slave, cycleNs, Options.DcSync0ShiftNs);
    }

    /// <summary>
    /// 슬레이브를 분류한다: 드라이브 프로파일이 있으면 축, 디지털 비트 PDO 가 있으면 IO 모듈, PDO 가 없으면 무시.
    /// Axes[].Profile 로 강제 지정, IoModules[].Kind=Ignore 로 제외 가능. 축 번호는 등록 순서 1..N.
    /// </summary>
    private void BuildAxes()
    {
        AxisList.Clear();
        IoModuleList.Clear();
        int axisId = 0, ioId = 0;
        var axisRefs = new List<AxisMapRef>();

        for (int s = 1; s <= _slaveCount; s++)
        {
            uint vendor = SoemNative.shim_slave_vendor_id(s), product = SoemNative.shim_slave_product_code(s);
            string name = SoemNative.SlaveName(s);
            int ob = SoemNative.shim_slave_output_bytes(s);
            int ib = SoemNative.shim_slave_input_bytes(s);
            int obits = SoemNative.shim_slave_output_bits(s);
            int ibits = SoemNative.shim_slave_input_bits(s);
            var ovr = Options.IoModules.FirstOrDefault(o => o.Slave == s);

            if (ovr is not null && string.Equals(ovr.Kind, "Ignore", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("Slave {S} ({Name}): 설정에 의해 무시", s, name);
                continue;
            }

            var profile = ovr is null ? ResolveProfile(axisId + 1, vendor, product) : null;
            if (profile is not null)
            {
                if (axisId >= Options.AxisCount)
                {
                    _log.LogWarning("Slave {S} ({Name}): AxisCount={Max} 초과로 축 등록 생략", s, name, Options.AxisCount);
                    continue;
                }
                int needOut = PdoLayout.SizeBytes(profile.RxEntries), needIn = PdoLayout.SizeBytes(profile.TxEntries);
                if (ob < needOut || ib < needIn)
                {
                    _log.LogWarning("Slave {S} ({Name}) PDO size mismatch (out={O}, in={I}, expected out>={EO} in>={EI}); not registered as axis",
                        s, name, ob, ib, needOut, needIn);
                    AzdDriveConfigurator.DumpPdoMapping(s, _log);
                    continue;
                }
                if (ob != needOut || ib != needIn)
                    _log.LogWarning("Slave {S}: PDO larger than expected (out={O}/{EO}, in={I}/{EI}). 앞부분만 사용", s, ob, needOut, ib, needIn);

                axisId++;
                var axis = RegisterAxis(axisId, s, profile);
                axisRefs.Add(new AxisMapRef
                {
                    In = SoemNative.shim_slave_inputs(s), Out = SoemNative.shim_slave_outputs(s),
                    RxEntries = profile.RxEntries, TxEntries = profile.TxEntries,
                });
                continue;
            }

            if (ibits > 0 || obits > 0)
            {
                ioId++;
                string ioName = !string.IsNullOrWhiteSpace(ovr?.Name) ? ovr!.Name! : $"IO{ioId}";
                int inBits = Math.Min(ibits, 64), outBits = Math.Min(obits, 64);
                var module = new IoModule(ioId, ioName, s, inBits, outBits,
                    IoModuleOptions.MergeNames(inBits, ovr?.InputNames, ovr?.InputAliases, "IN"),
                    IoModuleOptions.MergeNames(outBits, ovr?.OutputNames, ovr?.OutputAliases, "OUT"));
                IoModuleList.Add(module);
                _log.LogInformation("Slave {S} ({Name}, vendor 0x{V:X}): IO 모듈 '{Io}' 입력 {I} bit, 출력 {O} bit{Trunc}",
                    s, name, vendor, ioName, module.InputBits, module.OutputBits, ibits > 64 || obits > 64 ? " (64 bit 초과분 무시)" : "");
                continue;
            }

            _log.LogInformation("Slave {S} ({Name}, vendor 0x{V:X}): PDO 없음 → 커플러/패시브로 무시", s, name, vendor);
        }

        _axisRefs = axisRefs.ToArray();
        _ioRefs = new IoMapRef[IoModuleList.Count];
        for (int i = 0; i < IoModuleList.Count; i++)
        {
            int s = IoModuleList[i].Slave;
            _ioRefs[i] = new IoMapRef
            {
                In = SoemNative.shim_slave_inputs(s), InStart = SoemNative.shim_slave_input_startbit(s), InBits = IoModuleList[i].InputBits,
                Out = SoemNative.shim_slave_outputs(s), OutStart = SoemNative.shim_slave_output_startbit(s), OutBits = IoModuleList[i].OutputBits,
            };
        }

        _log.LogInformation("{N} axes, {M} IO modules registered", AxisList.Count, IoModuleList.Count);
        BuildInterlocks(_log);
    }

    private Axis RegisterAxis(int axisId, int s, IDriveProfile profile)
    {
        var axis = new Axis(axisId, AxisName(axisId), s, Options.Defaults, Options.Units, profile);
        ApplyAxisOptions(axis);

        // 드라이브에 이미 설정된 값(전자기어, 속도/가감속)을 읽어 그대로 쓴다. RT 스레드 시작 전이므로 PDO 초기값도 갱신.
        var msgs = new List<string>();
        profile.ReadMechanics(axis, msgs);
        axis.Rx.ProfileVelocity = axis.Defaults.Velocity;
        axis.Rx.ProfileAcceleration = axis.Defaults.Acceleration;
        axis.Rx.ProfileDeceleration = axis.Defaults.Deceleration;
        axis.MarkProfileSynced(axis.Defaults.Acceleration, axis.Defaults.Deceleration);
        msgs.Add(DescribeMechanics(axis));
        foreach (var msg in msgs) _log.LogInformation("Axis {Id} (slave {S}, {Prof}): {Msg}", axis.Id, s, profile.Name, msg);

        AxisList.Add(axis);
        return axis;
    }

    private static string DescribeMechanics(Axis axis)
    {
        var m = axis.Mechanics;
        var p = axis.Profile;
        return $"분해능 {m.StepsPerRev:0.##} {p.PositionUnit}/rev (base {m.BaseStepsPerRev:0.##}, A={m.ElectronicGearA}, B={m.ElectronicGearB}), " +
               (p.SupportsGearRatioObject ? $"기어비 {m.GearRatio:0.##}, " : "") +
               $"리드 {m.LeadUm} um → {m.StepsPerUm:0.####} {p.PositionUnit}/um; 드라이브 속도 {axis.Defaults.Velocity} {p.VelocityUnit}, " +
               $"가감속 {axis.Defaults.Acceleration}/{axis.Defaults.Deceleration} {p.AccelerationUnit}" +
               (p.AccelerationViaPdo ? "" : " (가감속은 SDO 전송)") + (p.SupportsProfileVelocity ? "" : " (조그 = PP 상대이동)");
    }

    private void GoOperational()
    {
        for (int i = 0; i < 3; i++)
        {
            WriteOutputs();
            SoemNative.shim_send_processdata();
            SoemNative.shim_receive_processdata(SoemNative.EC_TIMEOUTRET);
        }

        SoemNative.shim_request_state(0, SoemNative.EC_STATE_OPERATIONAL);

        // 프로세스 데이터를 주기적으로 계속 보내면서 OP 를 기다린다 (DC 슬레이브는 SYNC0 위상이 맞을 때까지 거부할 수 있음).
        // 최대 10 초. 도중에 ERROR 비트가 선 슬레이브는 AL 코드를 로그로 남기고 한 번 ACK 후 OP 를 다시 요청한다.
        long period = Options.CycleTimeUs * 1000L;
        long next = SoemNative.shim_now_ns() + period;
        int lastWkc = 0;
        var acked = new HashSet<int>();
        int attempts = (int)Math.Max(2000, 10_000_000L / Options.CycleTimeUs);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            SoemNative.shim_sleep_until_ns(next);
            next += period;
            SoemNative.shim_send_processdata();
            lastWkc = SoemNative.shim_receive_processdata(SoemNative.EC_TIMEOUTRET);

            if (attempt % 25 != 0) continue;
            if (SoemNative.shim_state_check(0, SoemNative.EC_STATE_OPERATIONAL, 1_000) == SoemNative.EC_STATE_OPERATIONAL)
            {
                _inOp = true;
                _log.LogInformation("All slaves OPERATIONAL, expected WKC={Wkc}{Dc}", _expectedWkc, _dcEnabled ? " (DC sync)" : "");
                return;
            }

            if (attempt % 250 == 0)
            {
                SoemNative.shim_read_state();
                for (int s = 1; s <= _slaveCount; s++)
                {
                    ushort st = SoemNative.shim_slave_state(s);
                    if ((st & SoemNative.EC_STATE_ERROR) == 0 || acked.Contains(s)) continue;
                    ushort al = SoemNative.shim_slave_al_status(s);
                    _log.LogWarning("Slave {S} ({Name}) state=0x{St:X2} AL=0x{Al:X4} {Txt} → ACK 후 OP 재요청",
                        s, SoemNative.SlaveName(s), st, al, SoemNative.AlStatusText(al));
                    SoemNative.shim_request_state(s, (ushort)((st & 0x0F) | SoemNative.EC_STATE_ACK));
                    SoemNative.shim_state_check(s, (ushort)(st & 0x0F), 50_000);
                    SoemNative.shim_request_state(s, SoemNative.EC_STATE_OPERATIONAL);
                    acked.Add(s);
                }
            }
        }

        // 실패: 슬레이브별 원인을 로그와 예외 메시지에 모두 담는다
        SoemNative.shim_read_state();
        var reasons = new List<string>();
        for (int s = 1; s <= _slaveCount; s++)
        {
            ushort st = SoemNative.shim_slave_state(s);
            if (st == SoemNative.EC_STATE_OPERATIONAL) continue;
            ushort al = SoemNative.shim_slave_al_status(s);
            string line = $"slave {s} ({SoemNative.SlaveName(s)}) state={StateName(st)} AL=0x{al:X4} {SoemNative.AlStatusText(al)}";
            _log.LogError("{Line}", line);
            reasons.Add(line);
        }
        _log.LogError("OP 전환 실패: 마지막 WKC {Wkc}/{Exp}, DC {Dc}. 흔한 원인: AL 0x001D/0x001E = PDO 매핑 불일치(재매핑 거부), " +
                      "0x001B = SM 워치독, 0x0032~0x0036 = DC 동기 오류(SYNC0 미수신·사이클 부적합), 0x0011 = 상태 전이 거부",
            lastWkc, _expectedWkc, _dcEnabled);
        throw new InvalidOperationException("Not all slaves reached OPERATIONAL: " + string.Join("; ", reasons));
    }

    private int OnPreOpToSafeOp(ushort slave)
    {
        if (!_slaveProfiles.TryGetValue(slave, out var profile))
        {
            _log.LogInformation("Slave {S}: 드라이브 프로파일 없음 (vendor 0x{V:X}), PDO 재매핑 생략", slave, SoemNative.shim_slave_vendor_id(slave));
            return 1;
        }

        bool ok = true;
        if (Options.RemapPdo)
        {
            _log.LogInformation("Slave {S}: {Prof} PDO 재매핑 (1600h {Rx}개 / 1A00h {Tx}개 엔트리)", slave, profile.Name, profile.RxEntries.Length, profile.TxEntries.Length);
            ok = profile.Configure(slave, Options.WriteOptionCodes, _log);
            if (!ok) _log.LogError("Slave {S}: PDO 재매핑에 오류가 있어 드라이브 기본 매핑이 남아 있을 수 있음", slave);
        }

        // SAFE_OP 요청 직전에 SYNC0 활성 (DC 필수 드라이브)
        EnableSync0(slave, profile);
        return ok ? 1 : 0;
    }

    // ------------------------------------------------------------------ cyclic

    private void CyclicLoop()
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        if (SoemNative.shim_set_realtime(Options.RealtimePriority) == 0)
            _log.LogWarning("SCHED_FIFO 설정 실패 (CAP_SYS_NICE 필요). 지터가 커질 수 있음");
        if (Options.CpuCore >= 0 && SoemNative.shim_pin_cpu(Options.CpuCore) == 0)
            _log.LogWarning("CPU {Core} 고정 실패", Options.CpuCore);

        long period = Options.CycleTimeUs * 1000L;
        long next = SoemNative.shim_now_ns() + period;
        int rxTimeout = Math.Clamp(Options.ReceiveTimeoutUs, 100, Math.Max(100, Options.CycleTimeUs - 100));
        int warmup = Options.JitterWarmupCycles;

        while (_running)
        {
            SoemNative.shim_sleep_until_ns(next);
            long now = SoemNative.shim_now_ns();
            long jitter = now - next;
            bool statsArmed = _cycleCount >= warmup;
            if (statsArmed && jitter > _maxJitterNs) _maxJitterNs = jitter;

            next += period + (_dcEnabled ? _dcLastToffNs : 0);
            if (now > next)
            {
                if (statsArmed) _missedCycles++;
                next = now + period;
            }

            // 1) 지난 사이클에 계산한 출력 송신, 입력 수신
            SoemNative.shim_send_processdata();
            int wkc = SoemNative.shim_receive_processdata(rxTimeout);
            _lastWkc = wkc;
            _cycleCount++;

            // DC: 마스터 사이클 위상을 기준 클록에 맞춘다 (SOEM ec_sync 방식)
            if (_dcEnabled && wkc > 0) DcSync(SoemNative.shim_dc_time(), period);

            bool healthy = wkc >= _expectedWkc;
            if (healthy) _consecutiveWkcErrors = 0;
            else
            {
                if (statsArmed) _wkcErrors++;
                if (_consecutiveWkcErrors <= Options.OfflineAfterMissedCycles) _consecutiveWkcErrors++;
            }
            bool online = _consecutiveWkcErrors < Options.OfflineAfterMissedCycles;

            // 2) IO 입력 스냅샷 → 축 Tx → 인터록/상태출력 → 축 Tick → 출력
            for (int i = 0; i < _ioRefs.Length; i++)
            {
                var m = IoModuleList[i];
                m.Online = online;
                m.UpdateInputs(IoBits.Read(_ioRefs[i].In, _ioRefs[i].InStart, _ioRefs[i].InBits));
            }
            for (int i = 0; i < _axisRefs.Length; i++)
            {
                var axis = AxisList[i];
                axis.Online = online;
                PdoLayout.ReadTx(_axisRefs[i].In, _axisRefs[i].TxEntries, ref axis.Tx);
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            RtMarkCycle(nowTicks);
            RtEvaluate(nowTicks, healthy && _inOp);

            foreach (var axis in AxisList) axis.Tick();
            WriteOutputs();
        }
    }

    /// <summary>
    /// SOEM red_test 의 ec_sync: DC 기준 시각이 (사이클 경계 + offset) 에 오도록 다음 대기 시간을 보정한다.
    /// </summary>
    private void DcSync(long dcTimeNs, long cycleNs)
    {
        long delta = (dcTimeNs - Options.DcSyncOffsetNs) % cycleNs;
        if (delta > cycleNs / 2) delta -= cycleNs;
        if (delta > 0) _dcIntegral++;
        if (delta < 0) _dcIntegral--;
        _dcLastToffNs = -(delta / 100) - (_dcIntegral / 20);
    }

    private void WriteOutputs()
    {
        for (int i = 0; i < _axisRefs.Length; i++)
            PdoLayout.WriteRx(_axisRefs[i].Out, _axisRefs[i].RxEntries, in AxisList[i].Rx);
        for (int i = 0; i < _ioRefs.Length; i++)
        {
            if (_ioRefs[i].OutBits > 0)
                IoBits.Write(_ioRefs[i].Out, _ioRefs[i].OutStart, _ioRefs[i].OutBits, IoModuleList[i].Outputs);
        }
    }

    /// <summary>10 ms 주기: 슬레이브 복구, 가감속 SDO 동기, 이벤트 로그.</summary>
    private void SupervisorLoop()
    {
        int lastNotOp = 0;
        while (_running)
        {
            Thread.Sleep(10);
            DrainEvents(_log);
            if (!_inOp) continue;

            SyncProfiles();

            int notOp = SoemNative.shim_check_and_recover(_lastWkc, _expectedWkc);
            if (notOp != lastNotOp)
            {
                if (notOp > 0)
                {
                    _lastError = $"{notOp} slave(s) not OPERATIONAL";
                    _log.LogWarning("{N} slave(s) not OPERATIONAL (wkc {Wkc}/{Exp})", notOp, _lastWkc, _expectedWkc);
                }
                else
                {
                    _lastError = null;
                    _log.LogInformation("All slaves back to OPERATIONAL");
                }
                lastNotOp = notOp;
            }
        }
    }

    /// <summary>6083h/6084h 가 PDO 에 없는 드라이브(VPH-HD): 축이 요청하면 SDO 로 쓰고 동기 완료를 표시한다.</summary>
    private void SyncProfiles()
    {
        foreach (var axis in AxisList)
        {
            if (!axis.ProfileSyncPending) continue;
            uint acc = axis.Rx.ProfileAcceleration, dec = axis.Rx.ProfileDeceleration;
            bool ok = SoemNative.SdoWrite(axis.Slave, 0x6083, 0, acc) && SoemNative.SdoWrite(axis.Slave, 0x6084, 0, dec);
            if (ok)
                axis.MarkProfileSynced(acc, dec);
            else
                _log.LogWarning("Axis {Id}: 6083h/6084h SDO 쓰기 실패: {Err}", axis.Id, SoemNative.LastError());
        }
    }

    // ------------------------------------------------------------------ statistics / SDO

    public override void ResetStatistics()
    {
        Interlocked.Exchange(ref _maxJitterNs, 0);
        Interlocked.Exchange(ref _missedCycles, 0);
        Interlocked.Exchange(ref _wkcErrors, 0);
    }

    public override SdoResult SdoRead(int slave, ushort index, byte subIndex, int size)
    {
        if (!ValidSdo(slave, size, out var err)) return SdoResult.Fail(err!);
        uint value = 0;
        int sz = size;
        bool ok = SoemNative.shim_sdo_read(slave, index, subIndex, 0, &sz, &value, SoemNative.EC_TIMEOUTRXM) > 0;
        return ok
            ? new SdoResult(true, value, size, null)
            : SdoResult.Fail($"SDO read {index:X4}h:{subIndex:X2} failed: {SoemNative.LastError()}");
    }

    public override SdoResult SdoWrite(int slave, ushort index, byte subIndex, int size, uint value)
    {
        if (!ValidSdo(slave, size, out var err)) return SdoResult.Fail(err!);
        uint v = value;
        bool ok = SoemNative.shim_sdo_write(slave, index, subIndex, 0, size, &v, SoemNative.EC_TIMEOUTRXM) > 0;
        return ok
            ? new SdoResult(true, value, size, null)
            : SdoResult.Fail($"SDO write {index:X4}h:{subIndex:X2} failed: {SoemNative.LastError()}");
    }

    private bool ValidSdo(int slave, int size, out string? error)
    {
        error = null;
        if (!_running || _slaveCount == 0) error = "master not running";
        else if (slave < 1 || slave > _slaveCount) error = $"slave {slave} out of range (1..{_slaveCount})";
        else if (size is not (1 or 2 or 4)) error = "size must be 1, 2 or 4";
        return error is null;
    }

    // ------------------------------------------------------------------ drive settings

    public override DriveSettingsResult ReadDriveSettings(int axisId)
    {
        var axis = RequireAxis(axisId);
        var msgs = new List<string>();
        if (!_running) return new DriveSettingsResult(false, axis.GetDriveSettings(), ["master not running"]);
        bool ok = axis.Profile.ReadMechanics(axis, msgs);
        msgs.Add(DescribeMechanics(axis));
        return new DriveSettingsResult(ok, axis.GetDriveSettings(), msgs);
    }

    public override DriveSettingsResult WriteDriveSettings(int axisId, DriveSettingsWrite req)
    {
        var axis = RequireAxis(axisId);
        var msgs = new List<string>();
        if (!_running) return new DriveSettingsResult(false, axis.GetDriveSettings(), ["master not running"]);

        int s = axis.Slave;
        var m = axis.Mechanics;
        var p = axis.Profile;
        bool ok = true;
        bool gearChanged = false;

        if (req.ElectronicGearA is > 0)
        {
            if (WriteChecked(SoemNative.SdoWrite(s, 0x6091, 1, req.ElectronicGearA.Value), $"6091h:01 {p.GearALabel}", req.ElectronicGearA.Value, msgs)) m.ElectronicGearA = req.ElectronicGearA.Value; else ok = false;
            gearChanged = true;
        }
        if (req.ElectronicGearB is > 0)
        {
            if (WriteChecked(SoemNative.SdoWrite(s, 0x6091, 2, req.ElectronicGearB.Value), $"6091h:02 {p.GearBLabel}", req.ElectronicGearB.Value, msgs)) m.ElectronicGearB = req.ElectronicGearB.Value; else ok = false;
            gearChanged = true;
        }
        if (req.GearRatioRaw is not null)
        {
            if (!p.SupportsGearRatioObject) msgs.Add($"{p.Name} 은 47F1h 기어비 오브젝트가 없어 무시");
            else if (WriteChecked(SoemNative.SdoWrite(s, 0x47F1, 0, req.GearRatioRaw.Value), "47F1h 기어비(×0.01)", req.GearRatioRaw.Value, msgs)) { m.GearRatioRaw = req.GearRatioRaw.Value; gearChanged = true; }
            else ok = false;
        }
        if (req.ProfileVelocity is > 0) ok &= WriteChecked(SoemNative.SdoWrite(s, 0x6081, 0, req.ProfileVelocity.Value), $"6081h 속도 [{p.VelocityUnit}]", req.ProfileVelocity.Value, msgs);
        if (req.ProfileAcceleration is > 0) ok &= WriteChecked(SoemNative.SdoWrite(s, 0x6083, 0, req.ProfileAcceleration.Value), $"6083h 가속 [{p.AccelerationUnit}]", req.ProfileAcceleration.Value, msgs);
        if (req.ProfileDeceleration is > 0) ok &= WriteChecked(SoemNative.SdoWrite(s, 0x6084, 0, req.ProfileDeceleration.Value), $"6084h 감속 [{p.AccelerationUnit}]", req.ProfileDeceleration.Value, msgs);

        ApplyLocalSettings(axis, req, msgs);

        if (gearChanged)
            msgs.Add(p.SupportsMaintenanceCommands
                ? "전자기어/기어비는 갱신 시점 C: Configuration(40C6h) 실행 또는 NV 저장(40C9h) 후 제어전원 재투입 시 반영됩니다."
                : "전자기어비는 드라이브 재기동(제어전원 재투입) 후 반영됩니다. 파라미터 저장은 드라이브 설정 도구로 하세요.");

        if (req.RunConfiguration)
        {
            if (!p.SupportsMaintenanceCommands) { msgs.Add($"{p.Name} 은 Configuration(40C6h) 명령이 없습니다"); ok = false; }
            else
            {
                if (axis.State == Cia402State.OperationEnabled) msgs.Add("주의: 서보 온 상태입니다. Configuration 은 모터 정지·서보 오프 상태에서 실행하세요.");
                ok &= Maintenance(s, 0x40C6, "Configuration", msgs);
            }
        }
        if (req.SaveToNvm)
        {
            if (!p.SupportsMaintenanceCommands) { msgs.Add($"{p.Name} 은 NV 저장(40C9h) 명령이 없습니다"); ok = false; }
            else
            {
                ok &= Maintenance(s, 0x40C9, "Write batch NV memory", msgs);
                msgs.Add("NV 기록 중·완료 후 5초간 제어전원을 끄지 마세요 (EEPROM 오류 41h 방지).");
            }
        }

        return new DriveSettingsResult(ok, axis.GetDriveSettings(), msgs);
    }

    private static bool WriteChecked(bool result, string what, object value, List<string> msgs)
    {
        msgs.Add(result ? $"{what} ← {value}" : $"{what} 쓰기 실패: {SoemNative.LastError()}");
        return result;
    }

    /// <summary>유지보수 명령(40Cxh): "0 → 1" 방식으로 실행 (연속 실행 방지, 매뉴얼 권장).</summary>
    private static bool Maintenance(int slave, ushort index, string name, List<string> msgs)
    {
        bool ok = SoemNative.SdoWrite(slave, index, 0, (byte)0) && SoemNative.SdoWrite(slave, index, 0, (byte)1);
        msgs.Add(ok ? $"{name} ({index:X4}h) 실행" : $"{name} ({index:X4}h) 실패: {SoemNative.LastError()}");
        return ok;
    }

    // ------------------------------------------------------------------ status

    public override MasterStatus GetStatus()
    {
        var slaves = new List<SlaveInfo>(_slaveCount);
        if (_slaveCount > 0)
        {
            for (int s = 1; s <= _slaveCount; s++)
            {
                ushort st = SoemNative.shim_slave_state(s);
                ushort al = SoemNative.shim_slave_al_status(s);
                slaves.Add(new SlaveInfo(s, SoemNative.SlaveName(s), SoemNative.shim_slave_vendor_id(s),
                    SoemNative.shim_slave_product_code(s), StateName(st), al, SoemNative.AlStatusText(al),
                    SoemNative.shim_slave_output_bytes(s), SoemNative.shim_slave_input_bytes(s)));
            }
        }

        return new MasterStatus(
            Mode: _dcEnabled ? "Soem+DC" : "Soem",
            Running: _running,
            AllOperational: _inOp && _lastError is null,
            Interface: Options.Interface,
            SlaveCount: _slaveCount,
            AxisCount: AxisList.Count,
            IoModuleCount: IoModuleList.Count,
            ActiveInterlocks: ActiveInterlockCount,
            CycleTimeUs: Options.CycleTimeUs,
            CycleCount: Interlocked.Read(ref _cycleCount),
            CycleAlive: CycleAlive,
            CyclesPerSecond: CyclesPerSecond,
            ActualCycleTimeUs: ActualCycleTimeUs,
            ExpectedWkc: _expectedWkc,
            LastWkc: _lastWkc,
            MissedCycles: Interlocked.Read(ref _missedCycles),
            WkcErrors: Interlocked.Read(ref _wkcErrors),
            MaxJitterUs: Interlocked.Read(ref _maxJitterNs) / 1000.0,
            LastError: _lastError,
            Slaves: slaves);
    }

    private static string StateName(ushort st) => (st & 0x0F) switch
    {
        SoemNative.EC_STATE_INIT => "INIT",
        SoemNative.EC_STATE_PRE_OP => "PRE_OP",
        SoemNative.EC_STATE_BOOT => "BOOT",
        SoemNative.EC_STATE_SAFE_OP => "SAFE_OP",
        SoemNative.EC_STATE_OPERATIONAL => "OP",
        _ => "NONE",
    } + ((st & SoemNative.EC_STATE_ERROR) != 0 ? "+ERR" : "");
}
