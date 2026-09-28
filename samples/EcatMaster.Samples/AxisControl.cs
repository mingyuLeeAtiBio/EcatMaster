using EcatMaster.Core.EtherCat;

namespace EcatMaster.Samples;

/// <summary>
/// <see cref="Axis"/> 위에 얹은 "명령 보내고 결과 기다리기" 도우미.
/// Axis.Submit 은 실시간 스레드로 넘기는 비동기 우편함이라(축당 최신 1개) 결과는 상태를 폴링해서 알아야 한다.
/// 거부(인터록, 서보 오프 등)는 예외가 아니라 <see cref="Axis.LastError"/> 에 남으므로 여기서 예외로 바꿔 준다.
/// 다른 솔루션에 옮길 때 이 파일을 그대로 가져가면 된다.
/// </summary>
public static class AxisControl
{
    /// <summary>조건이 참이 될 때까지 폴링 (2 ms 간격). 시간 초과면 false.</summary>
    public static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs, CancellationToken ct = default)
    {
        long t0 = Environment.TickCount64;
        while (Environment.TickCount64 - t0 < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(2, ct);
        }
        return condition();
    }

    /// <summary>서보 온 (Operation Enabled 까지 대기). 인터록으로 차단되어 있으면 LastError 를 예외로.</summary>
    public static async Task EnableAsync(Axis axis, int timeoutMs = 3000, CancellationToken ct = default)
    {
        if (axis.State == Cia402State.OperationEnabled) return;
        ThrowIfBlocked(axis);
        axis.ClearLastError();
        axis.Submit(new AxisCommand(AxisCommandKind.Enable));
        bool ok = await WaitAsync(() => axis.State == Cia402State.OperationEnabled || axis.LastError is not null, timeoutMs, ct);
        if (axis.LastError is not null) throw new InvalidOperationException($"[{axis.Name}] 서보 온 거부: {axis.LastError}");
        if (!ok) throw new TimeoutException($"[{axis.Name}] 서보 온 대기 초과 (state={axis.State}, fault=0x{axis.Tx.ErrorCode:X4})");
    }

    public static void Disable(Axis axis) => axis.Submit(new AxisCommand(AxisCommandKind.Disable));

    /// <summary>알람 리셋 후 Switch On Disabled/Ready 로 돌아오길 기다린다.</summary>
    public static async Task FaultResetAsync(Axis axis, int timeoutMs = 2000, CancellationToken ct = default)
    {
        axis.Submit(new AxisCommand(AxisCommandKind.FaultReset));
        await WaitAsync(() => axis.State is not (Cia402State.Fault or Cia402State.FaultReactionActive), timeoutMs, ct);
    }

    /// <summary>
    /// 절대/상대 이동을 걸고 완료까지 기다린다. 값은 축의 사용자 단위(기본 µm; unit 으로 step 지정 가능).
    /// 거부되면 InvalidOperationException, 완료 대기 초과면 TimeoutException, 이동 중 알람이면 InvalidOperationException.
    /// </summary>
    public static async Task MoveAsync(Axis axis, double position, double velocity, bool relative = false,
                                       double? acceleration = null, double? deceleration = null, UserUnit? unit = null,
                                       int timeoutMs = 60_000, CancellationToken ct = default)
    {
        ThrowIfBlocked(axis);
        if (axis.State != Cia402State.OperationEnabled) throw new InvalidOperationException($"[{axis.Name}] 서보 온이 아님 ({axis.State})");
        axis.ClearLastError();
        axis.Submit(axis.MakeMove(relative, position, velocity, acceleration, deceleration, unit));

        // 1) 받아들여져 움직이기 시작했는지 (거부면 LastError). 목표가 현재 위치와 같으면 시작 직후 끝나 IsMotionActive 를 못 볼 수 있으므로
        //    300 ms 안에 아무 신호도 없으면 "즉시 완료" 로 보고 넘어간다 (아래 2·3 단계가 도달 여부를 확인).
        await WaitAsync(() => axis.IsMotionActive || axis.LastError is not null, 300, ct);
        if (axis.LastError is not null) throw new InvalidOperationException($"[{axis.Name}] 이동 거부: {axis.LastError}");

        // 2) 완료: 동작 단계가 Idle 로 돌아오면 끝. Halted 면 누군가(인터록 등) 세운 것
        if (!await WaitAsync(() => !axis.IsMotionActive, timeoutMs, ct))
            throw new TimeoutException($"[{axis.Name}] 이동 완료 대기 초과");
        var s = axis.GetStatus();
        if (s.Fault) throw new InvalidOperationException($"[{axis.Name}] 이동 중 알람 0x{s.ErrorCode:X4}");
        if (s.Activity == "Halted") throw new InvalidOperationException($"[{axis.Name}] 이동이 정지됨" + (s.MotionBlocked ? $" (인터록: {s.BlockReason})" : ""));
    }

    /// <summary>지정 시간만큼 조그하고 Halt 로 세운다 (velocity 부호 = 방향, 사용자 단위/s).</summary>
    public static async Task JogForAsync(Axis axis, double velocity, int durationMs, CancellationToken ct = default)
    {
        ThrowIfBlocked(axis);
        axis.ClearLastError();
        axis.Submit(axis.MakeJog(velocity, null, null));
        try
        {
            await Task.Delay(durationMs, ct);
        }
        finally
        {
            axis.Submit(new AxisCommand(AxisCommandKind.Halt));   // 취소돼도 반드시 정지
        }
        if (axis.LastError is not null) throw new InvalidOperationException($"[{axis.Name}] 조그 거부: {axis.LastError}");
        await WaitAsync(() => axis.GetStatus().Velocity == 0, 5000, ct);
    }

    /// <summary>감속 정지(Halt). 서보는 유지.</summary>
    public static void Halt(Axis axis) => axis.Submit(new AxisCommand(AxisCommandKind.Halt));

    /// <summary>급정지 후 서보 오프(Quick Stop).</summary>
    public static void QuickStop(Axis axis) => axis.Submit(new AxisCommand(AxisCommandKind.QuickStop));

    /// <summary>원점복귀 (6098h 방식은 드라이브 설정) 완료까지 대기.</summary>
    public static async Task HomeAsync(Axis axis, int timeoutMs = 120_000, CancellationToken ct = default)
    {
        ThrowIfBlocked(axis);
        axis.ClearLastError();
        axis.Submit(new AxisCommand(AxisCommandKind.Home));
        if (!await WaitAsync(() => axis.IsMotionActive || axis.LastError is not null, 1000, ct)) throw new TimeoutException($"[{axis.Name}] 원점복귀가 시작되지 않음");
        if (axis.LastError is not null) throw new InvalidOperationException($"[{axis.Name}] 원점복귀 거부: {axis.LastError}");
        if (!await WaitAsync(() => !axis.IsMotionActive, timeoutMs, ct)) throw new TimeoutException($"[{axis.Name}] 원점복귀 대기 초과");
        if (axis.LastError is not null) throw new InvalidOperationException($"[{axis.Name}] 원점복귀 실패: {axis.LastError}");
    }

    /// <summary>한 줄 상태 요약.</summary>
    public static string Describe(Axis axis)
    {
        var s = axis.GetStatus();
        return $"[{s.Name}] {s.State} · {s.Activity} · pos {s.PositionUser:0.#} {s.Unit} ({s.Position} step) · vel {s.VelocityUser:0.#} {s.Unit}/s"
             + (s.TargetReached ? " · 도달" : "")
             + (s.MotionBlocked ? $" · 인터록 차단: {s.BlockReason}" : "")
             + (s.Fault ? $" · 알람 0x{s.ErrorCode:X4}" : "")
             + (s.LastError is null ? "" : $" · 마지막 오류: {s.LastError}");
    }

    private static void ThrowIfBlocked(Axis axis)
    {
        if (axis.MotionBlocked) throw new InvalidOperationException($"[{axis.Name}] 인터록 차단: {axis.BlockReason}");
    }

}
