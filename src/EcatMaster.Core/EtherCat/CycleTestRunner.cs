using System.Collections.Concurrent;
using System.Diagnostics;

namespace EcatMaster.Core.EtherCat;

/// <summary>왕복 테스트 요청. 위치/속도 단위는 Unit(없으면 축 설정 단위).</summary>
public sealed record CycleTestRequest(
    double PointA,
    double PointB,
    double Velocity,
    /// <summary>왕복 횟수. 0 = 중지할 때까지 무한.</summary>
    int Cycles,
    /// <summary>각 지점 도달 후 정지 시간 [ms]</summary>
    int DwellMs = 200,
    double? Acceleration = null,
    double? Deceleration = null,
    string? Unit = null,
    /// <summary>도달 판정 허용 오차 [step]</summary>
    int PositionToleranceSteps = 5,
    /// <summary>한 번의 이동 최대 대기 [ms]</summary>
    int MoveTimeoutMs = 60_000);

public sealed record CycleTestStatus(
    int AxisId,
    string AxisName,
    bool Running,
    int Cycles,
    int CompletedCycles,
    string Phase,
    string CurrentTarget,
    int PointASteps,
    int PointBSteps,
    double ElapsedSeconds,
    double LastMoveSeconds,
    double MinMoveSeconds,
    double MaxMoveSeconds,
    int MaxPositionErrorSteps,
    string? Error,
    DateTime? StartedUtc,
    DateTime? FinishedUtc);

/// <summary>
/// 축별 A↔B 왕복 시퀀스 실행기. 실시간 스레드와 무관한 백그라운드 Task 가 이동 명령을 넣고 완료를 폴링한다.
/// 서보 오프·알람·인터록·위치 오차·타임아웃이면 즉시 중단하고 사유를 남긴다.
/// </summary>
public sealed class CycleTestRunner
{
    private readonly ConcurrentDictionary<int, Run> _runs = new();

    public CycleTestStatus Start(Axis axis, CycleTestRequest req)
    {
        if (req.Velocity <= 0) throw new ArgumentException("velocity must be > 0");
        if (req.Cycles < 0) throw new ArgumentException("cycles must be >= 0 (0 = infinite)");
        if (req.DwellMs < 0) throw new ArgumentException("dwellMs must be >= 0");

        UserUnit? unit = null;
        if (req.Unit is not null)
        {
            if (!DriveMechanics.TryParseUnit(req.Unit, out var u)) throw new ArgumentException($"unit '{req.Unit}' must be step or um");
            unit = u;
        }

        int a = axis.Mechanics.UserToSteps(req.PointA, unit);
        int b = axis.Mechanics.UserToSteps(req.PointB, unit);
        if (a == b) throw new ArgumentException("point A and B are the same position");

        var run = new Run(axis, req, unit, a, b);
        if (!_runs.TryAdd(axis.Id, run))
        {
            var existing = _runs[axis.Id];
            if (existing.IsRunning) throw new InvalidOperationException($"axis {axis.Id} already has a running cycle test");
            _runs[axis.Id] = run;
        }
        run.Start();
        return run.GetStatus();
    }

    /// <summary>중지 요청. 진행 중 이동은 Halt 로 감속 정지시킨다.</summary>
    public bool Stop(int axisId)
    {
        if (!_runs.TryGetValue(axisId, out var run) || !run.IsRunning) return false;
        run.Stop();
        return true;
    }

    public CycleTestStatus? GetStatus(int axisId) => _runs.TryGetValue(axisId, out var run) ? run.GetStatus() : null;

    public IReadOnlyList<CycleTestStatus> GetAll() => _runs.Values.Select(r => r.GetStatus()).OrderBy(s => s.AxisId).ToList();

    public bool IsRunning(int axisId) => _runs.TryGetValue(axisId, out var run) && run.IsRunning;

    // ------------------------------------------------------------------

    private sealed class Run
    {
        private readonly Axis _axis;
        private readonly CycleTestRequest _req;
        private readonly UserUnit? _unit;
        private readonly int _a, _b;
        private readonly CancellationTokenSource _cts = new();
        private readonly Stopwatch _elapsed = new();
        private readonly object _lock = new();

        private Task? _task;
        private int _completed;
        private string _phase = "Idle";
        private string _target = "";
        private double _lastMove, _minMove = double.MaxValue, _maxMove;
        private int _maxPosError;
        private string? _error;
        private DateTime? _started, _finished;

        public Run(Axis axis, CycleTestRequest req, UserUnit? unit, int a, int b)
        {
            _axis = axis; _req = req; _unit = unit; _a = a; _b = b;
        }

        public bool IsRunning => _task is { IsCompleted: false };

        public void Start()
        {
            _started = DateTime.UtcNow;
            _elapsed.Start();
            _task = Task.Run(LoopAsync);
        }

        public void Stop() => _cts.Cancel();

        private async Task LoopAsync()
        {
            var ct = _cts.Token;
            try
            {
                SetPhase("MoveToA", "A");
                await MoveAndWaitAsync(_a, ct);

                for (int i = 0; _req.Cycles == 0 || i < _req.Cycles; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    SetPhase("MoveToB", "B");
                    await MoveAndWaitAsync(_b, ct);
                    SetPhase("DwellB", "B");
                    await Task.Delay(_req.DwellMs, ct);

                    SetPhase("MoveToA", "A");
                    await MoveAndWaitAsync(_a, ct);
                    lock (_lock) _completed++;
                    SetPhase("DwellA", "A");
                    await Task.Delay(_req.DwellMs, ct);
                }
                SetPhase("Completed", "");
            }
            catch (OperationCanceledException)
            {
                _axis.Submit(new AxisCommand(AxisCommandKind.Halt));
                SetPhase("Stopped", "");
            }
            catch (Exception ex)
            {
                lock (_lock) _error = ex.Message;
                SetPhase("Failed", "");
            }
            finally
            {
                _elapsed.Stop();
                _finished = DateTime.UtcNow;
            }
        }

        private void SetPhase(string phase, string target)
        {
            lock (_lock) { _phase = phase; _target = target; }
        }

        private async Task MoveAndWaitAsync(int targetSteps, CancellationToken ct)
        {
            CheckAxisReady();

            var m = _axis.Mechanics;
            var cmd = new AxisCommand(
                AxisCommandKind.MoveAbsolute,
                Position: targetSteps,
                Velocity: m.UserToStepsUnsigned(_req.Velocity, _unit),
                Acceleration: _req.Acceleration is > 0 ? m.UserToStepsUnsigned(_req.Acceleration.Value, _unit) : 0,
                Deceleration: _req.Deceleration is > 0 ? m.UserToStepsUnsigned(_req.Deceleration.Value, _unit) : 0);

            string? errBefore = _axis.LastError;
            int startPos = _axis.Tx.PositionActual;
            bool realMove = Math.Abs(targetSteps - startPos) > _req.PositionToleranceSteps;   // 이미 목표 위치면 통계에서 제외
            var sw = Stopwatch.StartNew();
            _axis.Submit(cmd);

            // 1) 수락 대기: 실시간 스레드가 명령을 적용해 Moving 상태가 되거나, 이미 목표 위치라 곧바로 끝나는 경우
            bool sawMotion = false;
            while (sw.ElapsedMilliseconds < 500)
            {
                await Task.Delay(5, ct);
                if (_axis.LastError is { } e && e != errBefore) throw new InvalidOperationException($"drive rejected move: {e}");
                if (_axis.IsMotionActive) { sawMotion = true; break; }
            }

            // 2) 완료 대기
            while (true)
            {
                await Task.Delay(10, ct);
                CheckAxisReady();
                if (_axis.LastError is { } e && e != errBefore) throw new InvalidOperationException($"move failed: {e}");
                if (sw.ElapsedMilliseconds > _req.MoveTimeoutMs) throw new TimeoutException($"move to {targetSteps} step timed out after {_req.MoveTimeoutMs} ms");
                if (_axis.IsMotionActive) { sawMotion = true; continue; }
                if (!sawMotion && sw.ElapsedMilliseconds < 300) continue;   // 아직 적용 전일 수 있음
                break;
            }

            // 3) 도달 판정
            var st = _axis.GetStatus();
            int err = Math.Abs(st.Position - targetSteps);
            if (!st.TargetReached || err > _req.PositionToleranceSteps)
                throw new InvalidOperationException($"target not reached: pos {st.Position}, target {targetSteps}, error {err} step (tolerance {_req.PositionToleranceSteps})");

            double secs = sw.Elapsed.TotalSeconds;
            lock (_lock)
            {
                _maxPosError = Math.Max(_maxPosError, err);
                if (realMove)
                {
                    _lastMove = secs;
                    _minMove = Math.Min(_minMove, secs);
                    _maxMove = Math.Max(_maxMove, secs);
                }
            }
        }

        private void CheckAxisReady()
        {
            if (!_axis.Online) throw new InvalidOperationException("axis offline (EtherCAT)");
            if (_axis.State is Cia402State.Fault or Cia402State.FaultReactionActive) throw new InvalidOperationException($"drive fault (error 0x{_axis.Tx.ErrorCode:X4})");
            if (_axis.State != Cia402State.OperationEnabled) throw new InvalidOperationException($"servo not enabled ({_axis.State})");
            if (_axis.MotionBlocked) throw new InvalidOperationException($"interlock: {_axis.BlockReason}");
        }

        public CycleTestStatus GetStatus()
        {
            lock (_lock)
            {
                return new CycleTestStatus(_axis.Id, _axis.Name, IsRunning, _req.Cycles, _completed, _phase, _target, _a, _b,
                    _elapsed.Elapsed.TotalSeconds, _lastMove, _minMove == double.MaxValue ? 0 : _minMove, _maxMove, _maxPosError,
                    _error, _started, _finished);
            }
        }
    }
}
