using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace EcatMaster.Core.EtherCat;

[JsonConverter(typeof(JsonStringEnumConverter<EnduranceMode>))]
public enum EnduranceMode
{
    /// <summary>한 방향 연속 회전 (PV 모드, 미지원 드라이브는 큰 상대 이동). 속도 부호가 방향.</summary>
    Rotate,
    /// <summary>A↔B 왕복을 지정 시간 동안 반복.</summary>
    Reciprocate,
}

/// <summary>시간 기반 연속 운전(내구 시험) 요청. 위치/속도 단위는 Unit(없으면 축 설정 단위).</summary>
public sealed record EnduranceTestRequest(
    EnduranceMode Mode,
    double Velocity,
    /// <summary>운전 시간 [분]. 60 = 1시간.</summary>
    double DurationMinutes,
    double PointA = 0,
    double PointB = 0,
    /// <summary>왕복: 각 지점 도달 후 정지 시간 [ms]</summary>
    int DwellMs = 200,
    double? Acceleration = null,
    double? Deceleration = null,
    string? Unit = null,
    /// <summary>샘플 CSV 기록 주기 [s]</summary>
    double SampleIntervalSeconds = 1.0,
    /// <summary>회전: 실속도가 지령에서 이 비율 이상 벗어나 1초 넘게 지속되면 경고 [%]. 0 = 검사 안 함</summary>
    double SpeedTolerancePercent = 10,
    /// <summary>알람·서보 꺼짐 등 오류 발생 시 알람 리셋 → 서보 온 → 운전 재개. false 면 즉시 중단.</summary>
    bool AutoRecover = false,
    int MaxRecoveries = 3,
    /// <summary>기록 폴더. null 이면 LocalApplicationData/EcatMaster/endurance</summary>
    string? OutputDirectory = null,
    int PositionToleranceSteps = 5,
    int MoveTimeoutMs = 60_000,
    /// <summary>시작 시 마스터의 WKC 오류·사이클 누락·최대 지터 통계를 0 으로 (시험 구간만 평가)</summary>
    bool ResetBusStatistics = true);

[JsonConverter(typeof(JsonStringEnumConverter<EnduranceSeverity>))]
public enum EnduranceSeverity { Info, Warning, Error }

/// <summary>시험 중 발생한 사건 1건. 위치/속도는 그 순간의 드라이브 원단위 값.</summary>
public sealed record EnduranceEvent(
    DateTime TimeUtc,
    double ElapsedSeconds,
    EnduranceSeverity Severity,
    string Category,
    string Message,
    int Position,
    int Velocity,
    ushort StatusWord,
    ushort ErrorCode);

public sealed record EnduranceTestStatus(
    int AxisId,
    string AxisName,
    bool Running,
    string Mode,
    string Phase,
    double ElapsedSeconds,
    double DurationSeconds,
    double TargetRpm,
    double LastRpm,
    double MinRpm,
    double MaxRpm,
    double AvgRpm,
    double Revolutions,
    long CompletedCycles,
    int MaxPositionErrorSteps,
    int Errors,
    int Warnings,
    int Recoveries,
    long WkcErrors,
    long MissedCycles,
    double MaxJitterUs,
    string? StopReason,
    string? EventsFile,
    string? SamplesFile,
    string? SummaryFile,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    IReadOnlyList<EnduranceEvent> RecentEvents);

/// <summary>
/// 축별 시간 기반 연속 운전 실행기 (안정성 평가용).
/// 운전 Task 와 감시 Task 가 따로 돈다. 감시 Task 는 50 ms 마다 축·버스 상태를 보고 변화(에지)를 사건으로 기록하고,
/// 주기마다 샘플을 CSV 로 남긴다. 오류급 사건은 운전 Task 에 전달되어 중단 또는 자동 복구된다.
/// 기록: {폴더}/{시각}_{축}_events.csv · _samples.csv · _summary.txt
/// </summary>
public sealed class EnduranceTestRunner
{
    private readonly ConcurrentDictionary<int, Run> _runs = new();

    public static string DefaultOutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EcatMaster", "endurance");

    public EnduranceTestStatus Start(IEtherCatMaster master, Axis axis, EnduranceTestRequest req)
    {
        if (req.DurationMinutes <= 0) throw new ArgumentException("durationMinutes must be > 0");
        if (req.Velocity == 0) throw new ArgumentException("velocity must not be 0");
        if (req.Mode == EnduranceMode.Reciprocate && req.Velocity < 0) throw new ArgumentException("velocity must be > 0 for reciprocate");
        if (req.SampleIntervalSeconds < 0.05) throw new ArgumentException("sampleIntervalSeconds must be >= 0.05");
        if (req.DwellMs < 0) throw new ArgumentException("dwellMs must be >= 0");
        if (req.MaxRecoveries < 0) throw new ArgumentException("maxRecoveries must be >= 0");

        UserUnit? unit = null;
        if (req.Unit is not null)
        {
            if (!DriveMechanics.TryParseUnit(req.Unit, out var u)) throw new ArgumentException($"unit '{req.Unit}' must be step or um");
            unit = u;
        }

        int a = 0, b = 0;
        if (req.Mode == EnduranceMode.Reciprocate)
        {
            a = axis.Mechanics.UserToSteps(req.PointA, unit);
            b = axis.Mechanics.UserToSteps(req.PointB, unit);
            if (a == b) throw new ArgumentException("point A and B are the same position");
        }

        if (_runs.TryGetValue(axis.Id, out var existing) && existing.IsRunning)
            throw new InvalidOperationException($"axis {axis.Id} already has a running endurance test");
        if (NotReadyReason(axis) is { } notReady) throw new InvalidOperationException(notReady);

        var run = new Run(master, axis, req, unit, a, b);
        run.Start();   // 파일 열기 실패 등은 여기서 예외
        _runs[axis.Id] = run;
        return run.GetStatus();
    }

    /// <summary>중지 요청. 진행 중 동작은 Halt 로 감속 정지시킨다.</summary>
    public bool Stop(int axisId)
    {
        if (!_runs.TryGetValue(axisId, out var run) || !run.IsRunning) return false;
        run.Stop();
        return true;
    }

    public EnduranceTestStatus? GetStatus(int axisId) => _runs.TryGetValue(axisId, out var run) ? run.GetStatus() : null;

    public IReadOnlyList<EnduranceTestStatus> GetAll() => _runs.Values.Select(r => r.GetStatus()).OrderBy(s => s.AxisId).ToList();

    public bool IsRunning(int axisId) => _runs.TryGetValue(axisId, out var run) && run.IsRunning;

    public bool AnyRunning => _runs.Values.Any(r => r.IsRunning);

    /// <summary>모든 시험 중지 후 요약 기록까지 대기 (마스터 해제 전에 호출). 시간 초과면 그냥 반환.</summary>
    public async Task StopAllAsync(TimeSpan timeout)
    {
        var running = _runs.Values.Where(r => r.IsRunning).ToList();
        foreach (var r in running) r.Stop();
        await Task.WhenAny(Task.WhenAll(running.Select(r => r.Completion)), Task.Delay(timeout));
    }

    /// <summary>운전을 시작·재개할 수 없는 사유. 준비됐으면 null.</summary>
    private static string? NotReadyReason(Axis axis)
    {
        // 인터록이 걸려 있으면 서보 온도 거부되므로 원인인 인터록을 먼저 알린다
        if (!axis.Online) return "축 오프라인 (EtherCAT)";
        if (axis.MotionBlocked) return $"인터록: {axis.BlockReason}";
        if (axis.State is Cia402State.Fault or Cia402State.FaultReactionActive) return $"드라이브 알람 (603Fh=0x{axis.Tx.ErrorCode:X4})";
        if (axis.State != Cia402State.OperationEnabled) return $"서보 온 상태가 아님 ({axis.State})";
        return null;
    }

    // ------------------------------------------------------------------

    private sealed class Run
    {
        private const int RecentMax = 200;
        private const int MonitorPeriodMs = 50;

        private readonly IEtherCatMaster _master;
        private readonly Axis _axis;
        private readonly EnduranceTestRequest _req;
        private readonly UserUnit? _unit;
        private readonly int _a, _b;
        private readonly TimeSpan _duration;
        private readonly CancellationTokenSource _cts = new();
        private readonly Stopwatch _elapsed = new();
        private readonly object _lock = new();
        private readonly LinkedList<EnduranceEvent> _recent = new();
        private readonly Dictionary<string, int> _categoryCounts = new();

        private StreamWriter? _events, _samples;
        private string? _eventsPath, _samplesPath, _summaryPath;
        private Task? _task;
        private string _phase = "Idle";
        private string? _stopReason;
        private DateTime? _started, _finished;
        private int _errors, _warnings, _recoveries, _maxPosError;
        private long _cycles;

        // 운전 Task 로 전달되는 오류 (감시 Task 가 세팅)
        private string? _pendingError;

        // 통계
        private readonly double _targetRpm;
        private readonly int _targetStepsPerSec;
        private double _lastRpm, _minRpm = double.MaxValue, _maxRpm, _rpmSum;
        private long _rpmCount;
        private double _revolutions;
        private long _wkcErrors, _missedCycles;
        private double _maxJitterUs;

        public Run(IEtherCatMaster master, Axis axis, EnduranceTestRequest req, UserUnit? unit, int a, int b)
        {
            _master = master; _axis = axis; _req = req; _unit = unit; _a = a; _b = b;
            _duration = TimeSpan.FromMinutes(req.DurationMinutes);
            _targetStepsPerSec = axis.Mechanics.UserToSteps(req.Velocity, unit);
            _targetRpm = axis.Mechanics.StepsPerSecToRpm(_targetStepsPerSec);
        }

        public bool IsRunning => _task is { IsCompleted: false };
        public Task Completion => _task ?? Task.CompletedTask;

        public void Start()
        {
            string dir = string.IsNullOrWhiteSpace(_req.OutputDirectory) ? DefaultOutputDirectory : _req.OutputDirectory!;
            Directory.CreateDirectory(dir);
            string safeName = string.Concat(_axis.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string prefix = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_axis{_axis.Id}_{safeName}");
            _eventsPath = prefix + "_events.csv";
            _samplesPath = prefix + "_samples.csv";
            _summaryPath = prefix + "_summary.txt";

            // Excel 에서 한글이 깨지지 않도록 UTF-8 BOM
            _events = new StreamWriter(_eventsPath, false, new UTF8Encoding(true)) { AutoFlush = true };
            _samples = new StreamWriter(_samplesPath, false, new UTF8Encoding(true));
            _events.WriteLine("time,elapsed_s,severity,category,message,position_step,velocity_step_s,rpm,statusword,error_code");
            _samples.WriteLine("time,elapsed_s,phase,state,activity,position_step,velocity_step_s,rpm,target_rpm,statusword,error_code," +
                               "bus_ok,wkc,expected_wkc,wkc_errors,missed_cycles,max_jitter_us,cycles_per_s");

            _started = DateTime.UtcNow;
            _elapsed.Start();
            _task = Task.Run(MainAsync);
        }

        public void Stop() => _cts.Cancel();

        // ============================================================ 운전 + 감시

        private async Task MainAsync()
        {
            var ct = _cts.Token;
            using var monitorCts = new CancellationTokenSource();
            if (_req.ResetBusStatistics) _master.ResetStatistics();
            var monitor = Task.Run(() => MonitorAsync(monitorCts.Token));
            try
            {
                string mode = _req.Mode == EnduranceMode.Rotate
                    ? $"연속 회전 {_req.Velocity} {UnitName}/s (≈ {_targetRpm:0.#} rpm)"
                    : $"왕복 A={_req.PointA} B={_req.PointB} {UnitName}, {_req.Velocity} {UnitName}/s (≈ {_targetRpm:0.#} rpm), 정지 {_req.DwellMs} ms";
                Record(EnduranceSeverity.Info, "Start",
                    $"시작: {mode}, 시간 {FormatDuration(_duration)}, 버스 통계 {(_req.ResetBusStatistics ? "초기화" : "유지")}, 자동복구 {(_req.AutoRecover ? $"최대 {_req.MaxRecoveries}회" : "안 함")}");

                string? notReady = NotReadyReason();
                if (notReady is not null) throw new InvalidOperationException(notReady);

                while (true)
                {
                    try
                    {
                        if (_req.Mode == EnduranceMode.Rotate) await RotateAsync(ct);
                        else await ReciprocateAsync(ct);
                        break;   // 시간 만료
                    }
                    catch (EnduranceFault f)
                    {
                        if (!_req.AutoRecover || _recoveries >= _req.MaxRecoveries)
                            throw new InvalidOperationException(f.Message + (_req.AutoRecover ? $" (자동 복구 {_recoveries}회 소진)" : ""));
                        await RecoverAsync(f.Message, ct);
                    }
                }

                await HaltAndWaitAsync();
                SetPhase("Completed");
                SetStopReason(null);
                Record(EnduranceSeverity.Info, "Completed", $"정상 완료: {FormatDuration(_elapsed.Elapsed)}");
            }
            catch (OperationCanceledException)
            {
                await HaltAndWaitAsync();
                SetPhase("Stopped");
                SetStopReason("사용자 중지");
                Record(EnduranceSeverity.Info, "Stopped", $"사용자 중지: 경과 {FormatDuration(_elapsed.Elapsed)}");
            }
            catch (Exception ex)
            {
                _axis.Submit(new AxisCommand(AxisCommandKind.Halt));
                SetPhase("Failed");
                SetStopReason(ex.Message);
                Record(EnduranceSeverity.Error, "Aborted", $"중단: {ex.Message} (경과 {FormatDuration(_elapsed.Elapsed)})");
            }
            finally
            {
                _elapsed.Stop();
                _finished = DateTime.UtcNow;
                monitorCts.Cancel();
                try { await monitor; } catch { /* 감시 종료 */ }
                lock (_lock) _lastRpm = _axis.GetStatus().VelocityRpm;   // 정지 후 값으로 갱신
                WriteSummary();
                lock (_lock)
                {
                    _events?.Dispose(); _events = null;
                    _samples?.Dispose(); _samples = null;
                }
            }
        }

        private bool TimeUp => _elapsed.Elapsed >= _duration;

        private async Task RotateAsync(CancellationToken ct)
        {
            SetPhase("Rotating");
            _axis.ClearLastError();
            _axis.Submit(_axis.MakeJog(_req.Velocity, _req.Acceleration, _req.Deceleration, _unit));
            while (!TimeUp)
            {
                await Task.Delay(MonitorPeriodMs, ct);
                ThrowIfFault();
            }
        }

        private async Task ReciprocateAsync(CancellationToken ct)
        {
            SetPhase("MoveToA");
            await MoveAndWaitAsync(_a, ct);
            while (!TimeUp)
            {
                SetPhase("MoveToB");
                await MoveAndWaitAsync(_b, ct);
                SetPhase("DwellB");
                await DwellAsync(ct);
                SetPhase("MoveToA");
                await MoveAndWaitAsync(_a, ct);
                Interlocked.Increment(ref _cycles);
                SetPhase("DwellA");
                await DwellAsync(ct);
            }
        }

        private async Task DwellAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < _req.DwellMs)
            {
                await Task.Delay(Math.Min(MonitorPeriodMs, Math.Max(1, _req.DwellMs - (int)sw.ElapsedMilliseconds)), ct);
                ThrowIfFault();
            }
        }

        private async Task MoveAndWaitAsync(int targetSteps, CancellationToken ct)
        {
            ThrowIfFault();
            var m = _axis.Mechanics;
            _axis.ClearLastError();
            _axis.Submit(new AxisCommand(
                AxisCommandKind.MoveAbsolute,
                Position: targetSteps,
                Velocity: m.UserToStepsUnsigned(_req.Velocity, _unit),
                Acceleration: _req.Acceleration is > 0 ? m.UserToStepsUnsigned(_req.Acceleration.Value, _unit) : 0,
                Deceleration: _req.Deceleration is > 0 ? m.UserToStepsUnsigned(_req.Deceleration.Value, _unit) : 0));

            var sw = Stopwatch.StartNew();
            bool sawMotion = false;
            while (true)
            {
                await Task.Delay(10, ct);
                ThrowIfFault();
                if (_axis.LastError is { } e) throw new EnduranceFault($"이동 실패: {e}");
                if (sw.ElapsedMilliseconds > _req.MoveTimeoutMs) throw new EnduranceFault($"이동 시간 초과 ({_req.MoveTimeoutMs} ms, 목표 {targetSteps} step)");
                if (_axis.IsMotionActive) { sawMotion = true; continue; }
                if (!sawMotion && sw.ElapsedMilliseconds < 300) continue;   // 아직 적용 전일 수 있음
                break;
            }

            var st = _axis.GetStatus();
            int err = Math.Abs(st.Position - targetSteps);
            lock (_lock) _maxPosError = Math.Max(_maxPosError, err);
            if (!st.TargetReached || err > _req.PositionToleranceSteps)
                throw new EnduranceFault($"목표 미도달: 위치 {st.Position}, 목표 {targetSteps}, 오차 {err} step (허용 {_req.PositionToleranceSteps})");
        }

        private async Task RecoverAsync(string reason, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref _recoveries);
            SetPhase("Recovering");
            Record(EnduranceSeverity.Warning, "Recovery", $"자동 복구 {n}/{_req.MaxRecoveries} 시작 (사유: {reason})");

            _axis.Submit(new AxisCommand(AxisCommandKind.Halt));
            await Task.Delay(300, ct);
            if (_axis.State is Cia402State.Fault or Cia402State.FaultReactionActive)
            {
                _axis.Submit(new AxisCommand(AxisCommandKind.FaultReset));
                await WaitAsync(() => _axis.State is not (Cia402State.Fault or Cia402State.FaultReactionActive), 3000, ct);
            }
            _axis.Submit(new AxisCommand(AxisCommandKind.Enable));
            bool ok = await WaitAsync(() => _axis.State == Cia402State.OperationEnabled, 5000, ct);
            await Task.Delay(200, ct);
            Volatile.Write(ref _pendingError, null);

            string? notReady = NotReadyReason();
            if (!ok || notReady is not null) throw new InvalidOperationException($"자동 복구 실패: {notReady ?? _axis.State.ToString()}");
            Record(EnduranceSeverity.Info, "Recovery", $"자동 복구 {n} 완료, 운전 재개");
        }

        private static async Task<bool> WaitAsync(Func<bool> cond, int timeoutMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                await Task.Delay(20, ct);
            }
            return cond();
        }

        private async Task HaltAndWaitAsync()
        {
            _axis.Submit(new AxisCommand(AxisCommandKind.Halt));
            SetPhase("Stopping");
            // 감속 정지 대기 (최대 10 s). 취소 토큰은 이미 취소됐을 수 있으므로 쓰지 않는다.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10_000 && (_axis.IsMotionActive || Math.Abs(_axis.Tx.VelocityActual) > Math.Max(10, Math.Abs(_targetStepsPerSec) / 100)))
                await Task.Delay(20);
        }

        private void ThrowIfFault()
        {
            if (Volatile.Read(ref _pendingError) is { } e) throw new EnduranceFault(e);
        }

        private string? NotReadyReason() => EnduranceTestRunner.NotReadyReason(_axis);

        // ------------------------------------------------------------ 감시

        private async Task MonitorAsync(CancellationToken ct)
        {
            var st0 = _master.GetStatus();
            long wkcBase = st0.WkcErrors, missBase = st0.MissedCycles;
            long wkcSeen = wkcBase, missSeen = missBase;
            bool wasOnline = _axis.Online, wasFault = false, wasWarn = false, wasBlocked = false, wasEnabled = true;
            bool wasAlive = true, wasAllOp = st0.AllOperational, speedBad = false;
            string? lastAxisError = null;
            var alSeen = st0.Slaves.ToDictionary(s => s.Position, s => s.AlStatus);
            var speedBadSince = Stopwatch.StartNew();
            var sampleTimer = Stopwatch.StartNew();
            var rateTimer = Stopwatch.StartNew();
            double rotateStartSec = -1;
            int lastPos = _axis.Tx.PositionActual;

            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(MonitorPeriodMs, ct); } catch (OperationCanceledException) { break; }

                var ax = _axis.GetStatus();
                var ms = _master.GetStatus();
                string phase; lock (_lock) phase = _phase;
                bool active = phase is not ("Stopping" or "Recovering" or "Completed" or "Stopped" or "Failed");

                // 회전량 (위치 차분, int 넘김 고려)
                int dp = unchecked(ax.Position - lastPos);
                lastPos = ax.Position;
                double rpm = ax.VelocityRpm;
                lock (_lock)
                {
                    _revolutions += Math.Abs(dp) / _axis.Mechanics.StepsPerOutputRev;
                    _lastRpm = rpm;
                    _maxJitterUs = Math.Max(_maxJitterUs, ms.MaxJitterUs);
                }

                // ---- 축 ----
                bool fault = ax.Fault;
                if (fault && !wasFault) Fault("DriveAlarm", $"드라이브 알람 603Fh=0x{ax.ErrorCode:X4} ({ax.State})", ax);
                else if (!fault && wasFault) Record(EnduranceSeverity.Info, "DriveAlarm", "알람 해제", ax);
                wasFault = fault;

                bool warn = (ax.StatusWord & Cia402.SwWarning) != 0;
                if (warn && !wasWarn) Record(EnduranceSeverity.Warning, "DriveWarning", $"드라이브 경고 (6041h bit7) 603Fh=0x{ax.ErrorCode:X4}", ax);
                else if (!warn && wasWarn) Record(EnduranceSeverity.Info, "DriveWarning", "경고 해제", ax);
                wasWarn = warn;

                if (!ax.Online && wasOnline) Fault("AxisOffline", "축 오프라인 (EtherCAT 통신 끊김)", ax);
                else if (ax.Online && !wasOnline) Record(EnduranceSeverity.Info, "AxisOffline", "축 온라인 복귀", ax);
                wasOnline = ax.Online;

                if (!ax.Enabled && wasEnabled && !fault && active) Fault("ServoOff", $"서보 꺼짐 ({ax.State})", ax);
                wasEnabled = ax.Enabled;

                if (ax.MotionBlocked && !wasBlocked) Fault("Interlock", $"인터록 차단: {ax.BlockReason}", ax);
                else if (!ax.MotionBlocked && wasBlocked) Record(EnduranceSeverity.Info, "Interlock", "인터록 해제", ax);
                wasBlocked = ax.MotionBlocked;

                if (ax.LastError is { } le && le != lastAxisError) Record(EnduranceSeverity.Warning, "AxisError", $"축 오류: {le}", ax);
                lastAxisError = ax.LastError;

                if ((ax.StatusWord & Cia402.SwBit13) != 0 && ax.Mode == nameof(OperationMode.ProfilePosition) && active)
                    Record(EnduranceSeverity.Warning, "FollowingError", "추종 오차 (6041h bit13)", ax, throttleKey: "FollowingError");

                // ---- 회전 속도 ----
                if (_req.Mode == EnduranceMode.Rotate && phase == "Rotating" && ax.Enabled)
                {
                    if (rotateStartSec < 0) rotateStartSec = _elapsed.Elapsed.TotalSeconds;
                    bool settled = _elapsed.Elapsed.TotalSeconds - rotateStartSec > RampSeconds() + 1.0;
                    if (settled)
                    {
                        lock (_lock)
                        {
                            // 방향과 무관하게 크기로 통계 (역회전은 rpm 이 음수)
                            double mag = Math.Abs(rpm);
                            _minRpm = Math.Min(_minRpm, mag);
                            _maxRpm = Math.Max(_maxRpm, mag);
                            _rpmSum += mag; _rpmCount++;
                        }
                        if (_req.SpeedTolerancePercent > 0 && _targetStepsPerSec != 0)
                        {
                            double dev = Math.Abs(ax.Velocity - _targetStepsPerSec) * 100.0 / Math.Abs(_targetStepsPerSec);
                            bool bad = dev > _req.SpeedTolerancePercent;
                            if (!bad) speedBadSince.Restart();
                            if (bad && !speedBad && speedBadSince.ElapsedMilliseconds > 1000)
                            {
                                speedBad = true;
                                Record(EnduranceSeverity.Warning, "SpeedDeviation", $"속도 편차 {dev:0.#}% (실 {rpm:0.#} / 지령 {_targetRpm:0.#} rpm)", ax);
                            }
                            else if (!bad && speedBad)
                            {
                                speedBad = false;
                                Record(EnduranceSeverity.Info, "SpeedDeviation", $"속도 정상 복귀 ({rpm:0.#} rpm)", ax);
                            }
                        }
                        if (ax.Activity is "Idle" or "Halted" && ax.Enabled && Volatile.Read(ref _pendingError) is null)
                            Fault("MotionStopped", $"회전이 멈춤 (activity={ax.Activity})", ax);
                    }
                }
                else rotateStartSec = -1;

                // ---- 버스 ----
                if (!ms.CycleAlive && wasAlive) Fault("CycleStopped", "실시간 사이클 정지", ax);
                else if (ms.CycleAlive && !wasAlive) Record(EnduranceSeverity.Info, "CycleStopped", "실시간 사이클 재개", ax);
                wasAlive = ms.CycleAlive;

                if (!ms.AllOperational && wasAllOp)
                {
                    string bad = string.Join(", ", ms.Slaves.Where(s => s.State != "OP").Select(s => $"slave {s.Position} {s.State} AL=0x{s.AlStatus:X4} {s.AlStatusText}"));
                    Record(EnduranceSeverity.Error, "BusNotOp", $"슬레이브 OP 이탈: {bad}", ax);
                }
                else if (ms.AllOperational && !wasAllOp) Record(EnduranceSeverity.Info, "BusNotOp", "모든 슬레이브 OP 복귀", ax);
                wasAllOp = ms.AllOperational;

                foreach (var s in ms.Slaves)
                {
                    alSeen.TryGetValue(s.Position, out var prev);
                    if (s.AlStatus != prev && s.AlStatus != 0)
                        Record(EnduranceSeverity.Warning, "AlStatus", $"slave {s.Position} ({s.Name}) AL=0x{s.AlStatus:X4} {s.AlStatusText}", ax);
                    alSeen[s.Position] = s.AlStatus;
                }

                // WKC 오류·사이클 누락은 발생 순간마다 찍으면 수만 줄이 되므로 1초 단위로 묶어서 기록
                if (rateTimer.ElapsedMilliseconds >= 1000)
                {
                    rateTimer.Restart();
                    if (ms.WkcErrors > wkcSeen)
                        Record(EnduranceSeverity.Warning, "WkcError", $"WKC 오류 +{ms.WkcErrors - wkcSeen} (마지막 {ms.LastWkc}/{ms.ExpectedWkc})", ax);
                    if (ms.MissedCycles > missSeen)
                        Record(EnduranceSeverity.Warning, "MissedCycle", $"사이클 누락 +{ms.MissedCycles - missSeen} (최대 지터 {ms.MaxJitterUs:0} µs)", ax);
                    // 통계 리셋(누적값 감소) 대비
                    if (ms.WkcErrors < wkcSeen) wkcBase -= wkcSeen - ms.WkcErrors;
                    if (ms.MissedCycles < missSeen) missBase -= missSeen - ms.MissedCycles;
                    wkcSeen = ms.WkcErrors;
                    missSeen = ms.MissedCycles;
                    lock (_lock)
                    {
                        _wkcErrors = wkcSeen - wkcBase;
                        _missedCycles = missSeen - missBase;
                    }
                }

                // ---- 샘플 ----
                if (sampleTimer.Elapsed.TotalSeconds >= _req.SampleIntervalSeconds)
                {
                    sampleTimer.Restart();
                    WriteSample(phase, ax, ms);
                }
            }
        }

        /// <summary>가감속으로 목표 속도에 닿는 데 걸리는 시간 [s] (속도 편차 검사 유예).</summary>
        private double RampSeconds()
        {
            uint acc = _req.Acceleration is > 0 ? _axis.Mechanics.UserToStepsUnsigned(_req.Acceleration.Value, _unit) : _axis.Defaults.Acceleration;
            return acc == 0 ? 2.0 : Math.Min(30.0, Math.Abs(_targetStepsPerSec) / (double)acc);
        }

        /// <summary>오류급 사건: 기록하고 운전 Task 에 전달 (중단 또는 자동 복구).</summary>
        private void Fault(string category, string message, AxisStatus ax)
        {
            Record(EnduranceSeverity.Error, category, message, ax);
            Interlocked.CompareExchange(ref _pendingError, message, null);
        }

        // ------------------------------------------------------------ 기록

        private readonly Dictionary<string, DateTime> _throttle = new();

        private void Record(EnduranceSeverity sev, string category, string message, AxisStatus? ax = null, string? throttleKey = null)
        {
            ax ??= _axis.GetStatus();
            var now = DateTime.UtcNow;
            var ev = new EnduranceEvent(now, _elapsed.Elapsed.TotalSeconds, sev, category, message, ax.Position, ax.Velocity, ax.StatusWord, ax.ErrorCode);
            lock (_lock)
            {
                if (throttleKey is not null)
                {
                    // 같은 종류는 10초에 한 번만
                    if (_throttle.TryGetValue(throttleKey, out var last) && (now - last).TotalSeconds < 10) return;
                    _throttle[throttleKey] = now;
                }
                if (sev == EnduranceSeverity.Error) _errors++;
                else if (sev == EnduranceSeverity.Warning) _warnings++;
                if (sev != EnduranceSeverity.Info) _categoryCounts[category] = _categoryCounts.GetValueOrDefault(category) + 1;

                _recent.AddFirst(ev);
                while (_recent.Count > RecentMax) _recent.RemoveLast();

                try
                {
                    _events?.WriteLine(string.Join(",",
                        now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        F(ev.ElapsedSeconds, "0.000"), sev, category, Csv(message), ev.Position, ev.Velocity,
                        F(ax.VelocityRpm, "0.0"), $"0x{ev.StatusWord:X4}", $"0x{ev.ErrorCode:X4}"));
                }
                catch { /* 디스크 오류로 시험을 멈추지 않는다 */ }
            }
        }

        private void WriteSample(string phase, AxisStatus ax, MasterStatus ms)
        {
            lock (_lock)
            {
                try
                {
                    _samples?.WriteLine(string.Join(",",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        F(_elapsed.Elapsed.TotalSeconds, "0.000"), phase, ax.State, ax.Activity, ax.Position, ax.Velocity,
                        F(ax.VelocityRpm, "0.0"), F(_targetRpm, "0.0"), $"0x{ax.StatusWord:X4}", $"0x{ax.ErrorCode:X4}",
                        ms.AllOperational ? 1 : 0, ms.LastWkc, ms.ExpectedWkc, ms.WkcErrors, ms.MissedCycles,
                        F(ms.MaxJitterUs, "0.0"), F(ms.CyclesPerSecond, "0.0")));
                    _samples?.Flush();
                }
                catch { /* 무시 */ }
            }
        }

        private void WriteSummary()
        {
            if (_summaryPath is null) return;
            try
            {
                var s = GetStatus();
                var sb = new StringBuilder();
                sb.AppendLine("EcatMaster 연속 운전(내구 시험) 결과");
                sb.AppendLine("====================================");
                sb.AppendLine($"축            : {s.AxisName} (#{s.AxisId}, slave {_axis.Slave}, {_axis.Profile.Name})");
                sb.AppendLine($"방식          : {(_req.Mode == EnduranceMode.Rotate ? "연속 회전" : $"왕복 A={_req.PointA} B={_req.PointB} {UnitName}, 정지 {_req.DwellMs} ms")}");
                sb.AppendLine($"지령 속도     : {_req.Velocity} {UnitName}/s ({_targetStepsPerSec} step/s, ≈ {_targetRpm:0.##} rpm)");
                sb.AppendLine($"설정 시간     : {FormatDuration(_duration)}");
                sb.AppendLine($"시작 / 종료   : {s.StartedUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss} / {s.FinishedUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"실제 운전     : {FormatDuration(TimeSpan.FromSeconds(s.ElapsedSeconds))}");
                sb.AppendLine($"결과          : {s.Phase}{(s.StopReason is null ? "" : " — " + s.StopReason)}");
                sb.AppendLine();
                if (_req.Mode == EnduranceMode.Rotate)
                    sb.AppendLine($"|RPM| (정속 구간): 최소 {s.MinRpm:0.##} / 평균 {s.AvgRpm:0.##} / 최대 {s.MaxRpm:0.##}");
                else
                    sb.AppendLine($"왕복 횟수     : {s.CompletedCycles}회, 최대 위치 오차 {s.MaxPositionErrorSteps} step");
                sb.AppendLine($"누적 회전     : {s.Revolutions:0.##} rev");
                sb.AppendLine($"오류 / 경고   : {s.Errors} / {s.Warnings}, 자동 복구 {s.Recoveries}회");
                sb.AppendLine($"버스          : WKC 오류 {s.WkcErrors}, 사이클 누락 {s.MissedCycles}, 최대 지터 {s.MaxJitterUs:0.#} µs");
                sb.AppendLine();
                sb.AppendLine("종류별 발생 횟수 (경고·오류):");
                lock (_lock)
                {
                    if (_categoryCounts.Count == 0) sb.AppendLine("  (없음)");
                    foreach (var kv in _categoryCounts.OrderByDescending(k => k.Value)) sb.AppendLine($"  {kv.Key,-16} {kv.Value}");
                }
                sb.AppendLine();
                sb.AppendLine($"사건 기록 : {Path.GetFileName(_eventsPath)}");
                sb.AppendLine($"샘플 기록 : {Path.GetFileName(_samplesPath)} ({_req.SampleIntervalSeconds:0.###} s 주기)");
                File.WriteAllText(_summaryPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch { /* 무시 */ }
        }

        private string UnitName => (_unit ?? _axis.Mechanics.Unit) == UserUnit.Um ? "um" : "step";

        private void SetPhase(string phase) { lock (_lock) _phase = phase; }
        private void SetStopReason(string? r) { lock (_lock) _stopReason = r; }

        private static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);
        private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        public static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes}분 {t.Seconds}초" : t.TotalMinutes >= 1 ? $"{t.Minutes}분 {t.Seconds}초" : $"{t.TotalSeconds:0.#}초";

        public EnduranceTestStatus GetStatus()
        {
            lock (_lock)
            {
                return new EnduranceTestStatus(_axis.Id, _axis.Name, IsRunning, _req.Mode.ToString(), _phase,
                    _elapsed.Elapsed.TotalSeconds, _duration.TotalSeconds, _targetRpm, _lastRpm,
                    _rpmCount == 0 ? 0 : _minRpm, _maxRpm, _rpmCount == 0 ? 0 : _rpmSum / _rpmCount,
                    _revolutions, Interlocked.Read(ref _cycles), _maxPosError, _errors, _warnings, _recoveries,
                    _wkcErrors, _missedCycles, _maxJitterUs, _stopReason, _eventsPath, _samplesPath, _summaryPath,
                    _started, _finished, _recent.ToList());
            }
        }
    }

    /// <summary>운전 중 오류 (자동 복구 대상).</summary>
    private sealed class EnduranceFault(string message) : Exception(message);
}
