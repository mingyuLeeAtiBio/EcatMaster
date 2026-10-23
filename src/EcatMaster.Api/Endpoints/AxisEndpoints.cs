using EcatMaster.Core.EtherCat;

namespace EcatMaster.Api.Endpoints;

/// <summary>
/// 축 엔드포인트. 경로의 {axis} 는 축 번호("1") 또는 별칭("X") 모두 허용한다.
/// </summary>
public static class AxisEndpoints
{
    public static IEndpointRouteBuilder MapAxisEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/axes").WithTags("Axes");

        g.MapGet("/", (IEtherCatMaster m) => m.Axes.Select(a => a.GetStatus()))
            .WithSummary("모든 축 상태 (step 값과 사용자 단위 값 모두 포함)");

        g.MapGet("/{axis}", (string axis, IEtherCatMaster m) =>
                m.GetAxis(axis) is { } a ? Results.Ok(a.GetStatus()) : AxisNotFound(axis))
            .WithSummary("축 상태. {axis} = 번호 또는 별칭");

        g.MapPut("/{axis}/alias", (string axis, AliasRequest req, IEtherCatMaster m) =>
            {
                var a = m.GetAxis(axis);
                if (a is null) return AxisNotFound(axis);
                if (string.IsNullOrWhiteSpace(req.Name)) return Results.Problem("name 이 비어 있습니다", statusCode: 400);
                var name = req.Name.Trim();
                var dup = m.GetAxis(name);
                if (dup is not null && dup.Id != a.Id) return Results.Problem($"별칭 '{name}' 은 축 {dup.Id} 가 이미 사용 중", statusCode: 409);
                a.Name = name;
                return Results.Ok(a.GetStatus());
            })
            .WithSummary("축 별칭 변경 (런타임만. 영구 반영은 설정 Master:Axes[].Name)");

        g.MapPost("/{axis}/enable", (string axis, IEtherCatMaster m) =>
                Submit(m, axis, _ => new AxisCommand(AxisCommandKind.Enable)))
            .WithSummary("서보 온 (Switch on disabled → Operation enabled)");

        g.MapPost("/{axis}/disable", (string axis, IEtherCatMaster m) =>
                Submit(m, axis, _ => new AxisCommand(AxisCommandKind.Disable)))
            .WithSummary("서보 오프");

        g.MapPost("/{axis}/fault-reset", (string axis, IEtherCatMaster m) =>
                Submit(m, axis, _ => new AxisCommand(AxisCommandKind.FaultReset)))
            .WithSummary("알람 리셋 (Controlword bit7 펄스)");

        g.MapPost("/{axis}/move-absolute", (string axis, MoveRequest req, IEtherCatMaster m) => SubmitMove(m, axis, req, relative: false))
            .WithSummary("절대 위치 이동 (PP 모드). 단위: Unit 또는 서버 설정(기본 um)");

        g.MapPost("/{axis}/move-relative", (string axis, MoveRequest req, IEtherCatMaster m) => SubmitMove(m, axis, req, relative: true))
            .WithSummary("상대 위치 이동 (PP 모드)");

        g.MapPost("/{axis}/jog", (string axis, JogRequest req, IEtherCatMaster m) =>
            {
                if (!TryUnit(req.Unit, out var unit, out var bad)) return bad!;
                return Submit(m, axis, a => a.MakeJog(req.Velocity, req.Acceleration, req.Deceleration, unit),
                    (a, c) => $"target velocity {c.TargetVelocity} Hz ({req.Velocity} {UnitName(a, unit)}/s)");
            })
            .WithSummary("조그 (PV 모드). 정지는 /stop");

        g.MapPost("/{axis}/stop", (string axis, IEtherCatMaster m) =>
                Submit(m, axis, _ => new AxisCommand(AxisCommandKind.Halt)))
            .WithSummary("감속 정지 (Halt bit). 서보는 유지");

        g.MapPost("/{axis}/quick-stop", (string axis, IEtherCatMaster m) =>
                Submit(m, axis, _ => new AxisCommand(AxisCommandKind.QuickStop)))
            .WithSummary("급정지 후 서보 오프 (Quick stop)");

        g.MapPost("/{axis}/home", (string axis, IEtherCatMaster m) =>
                Submit(m, axis, _ => new AxisCommand(AxisCommandKind.Home)))
            .WithSummary("원점 복귀 (Homing 모드, 방식은 드라이브 6098h 설정)");

        // ---- 왕복 테스트 (A ↔ B, N회) ----

        g.MapPost("/{axis}/cycle-test", (string axis, CycleTestRequest req, IEtherCatMaster m, CycleTestRunner runner) =>
            {
                var a = m.GetAxis(axis);
                if (a is null) return AxisNotFound(axis);
                if (a.MotionBlocked) return Results.Problem($"axis {a.Id} blocked by interlock: {a.BlockReason}", statusCode: 409);
                try { return Results.Accepted($"/api/axes/{a.Id}/cycle-test", runner.Start(a, req)); }
                catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
                catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
            })
            .WithSummary("A↔B 왕복 테스트 시작. body: { pointA, pointB, velocity, cycles(0=무한), dwellMs, unit? }");

        g.MapGet("/{axis}/cycle-test", (string axis, IEtherCatMaster m, CycleTestRunner runner) =>
            {
                var a = m.GetAxis(axis);
                if (a is null) return AxisNotFound(axis);
                var s = runner.GetStatus(a.Id);
                return s is null ? Results.Problem("no cycle test for this axis", statusCode: 404) : Results.Ok(s);
            })
            .WithSummary("왕복 테스트 진행 상태 (완료 횟수, 이동 시간 min/max, 최대 위치 오차, 중단 사유)");

        g.MapDelete("/{axis}/cycle-test", (string axis, IEtherCatMaster m, CycleTestRunner runner) =>
            {
                var a = m.GetAxis(axis);
                if (a is null) return AxisNotFound(axis);
                return runner.Stop(a.Id) ? Results.Accepted() : Results.Problem("no running cycle test", statusCode: 409);
            })
            .WithSummary("왕복 테스트 중지 (진행 중 이동은 Halt)");

        app.MapGet("/api/cycle-tests", (CycleTestRunner runner) => runner.GetAll())
            .WithTags("Axes")
            .WithSummary("모든 축의 왕복 테스트 상태");

        // ---- 연속 운전 (시간 기반 내구 시험) ----

        g.MapPost("/{axis}/endurance-test", (string axis, EnduranceTestRequest req, IEtherCatMaster m, EnduranceTestRunner runner, CycleTestRunner cycles) =>
            {
                var a = m.GetAxis(axis);
                if (a is null) return AxisNotFound(axis);
                if (a.MotionBlocked) return Results.Problem($"axis {a.Id} blocked by interlock: {a.BlockReason}", statusCode: 409);
                if (cycles.IsRunning(a.Id)) return Results.Problem($"axis {a.Id} has a running cycle test", statusCode: 409);
                try { return Results.Accepted($"/api/axes/{a.Id}/endurance-test", runner.Start(m, a, req)); }
                catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
                catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
                catch (IOException ex) { return Results.Problem($"cannot create log files: {ex.Message}", statusCode: 500); }
            })
            .WithSummary("연속 운전 시작. body: { mode: Rotate|Reciprocate, velocity(부호=회전 방향), durationMinutes, pointA?, pointB?, dwellMs?, " +
                         "sampleIntervalSeconds?, speedTolerancePercent?, autoRecover?, maxRecoveries?, unit? }. 사건·샘플·요약을 CSV/TXT 로 기록");

        g.MapGet("/{axis}/endurance-test", (string axis, IEtherCatMaster m, EnduranceTestRunner runner) =>
            {
                var a = m.GetAxis(axis);
                if (a is null) return AxisNotFound(axis);
                var s = runner.GetStatus(a.Id);
                return s is null ? Results.Problem("no endurance test for this axis", statusCode: 404) : Results.Ok(s);
            })
            .WithSummary("연속 운전 진행 상태 (경과, RPM min/avg/max, 오류·경고 수, 최근 사건, 기록 파일 경로)");

        g.MapDelete("/{axis}/endurance-test", (string axis, IEtherCatMaster m, EnduranceTestRunner runner) =>
            {
                var a = m.GetAxis(axis);
                if (a is null) return AxisNotFound(axis);
                return runner.Stop(a.Id) ? Results.Accepted() : Results.Problem("no running endurance test", statusCode: 409);
            })
            .WithSummary("연속 운전 중지 (감속 정지 후 요약 기록)");

        app.MapGet("/api/endurance-tests", (EnduranceTestRunner runner) => runner.GetAll())
            .WithTags("Axes")
            .WithSummary("모든 축의 연속 운전 상태");

        // ---- 드라이브 설정 (전자기어 · 기어비 · 리드 · 속도/가감속 기본값) ----

        g.MapGet("/{axis}/drive-settings", (string axis, IEtherCatMaster m) =>
                m.GetAxis(axis) is { } a ? Results.Ok(a.GetDriveSettings()) : AxisNotFound(axis))
            .WithSummary("보관 중인 드라이브 설정 (연결 시 드라이브에서 읽은 값 + 리드/단위)");

        g.MapPost("/{axis}/drive-settings/read", (string axis, IEtherCatMaster m) =>
            {
                var a = m.GetAxis(axis);
                if (a is null) return AxisNotFound(axis);
                var r = m.ReadDriveSettings(a.Id);
                return Results.Json(r, statusCode: r.Success ? 200 : 502);
            })
            .WithSummary("드라이브에서 6091h/47F1h/6081h/6083h/6084h 다시 읽기");

        g.MapPut("/{axis}/drive-settings", (string axis, DriveSettingsWrite req, IEtherCatMaster m) =>
            {
                var a = m.GetAxis(axis);
                if (a is null) return AxisNotFound(axis);
                var r = m.WriteDriveSettings(a.Id, req);
                return Results.Json(r, statusCode: r.Success ? 200 : 502);
            })
            .WithSummary("드라이브 설정 변경. null 항목 유지. runConfiguration=40C6h, saveToNvm=40C9h");

        return app;
    }

    private static IResult SubmitMove(IEtherCatMaster m, string axis, MoveRequest req, bool relative)
    {
        if (!TryUnit(req.Unit, out var unit, out var bad)) return bad!;
        return Submit(m, axis, a => a.MakeMove(relative, req.Position, req.Velocity, req.Acceleration, req.Deceleration, unit),
            (a, c) => $"target {c.Position} step ({req.Position} {UnitName(a, unit)}), vel {(c.Velocity > 0 ? c.Velocity : a.Defaults.Velocity)} Hz");
    }

    private static IResult Submit(IEtherCatMaster m, string axisKey, Func<Axis, AxisCommand> factory, Func<Axis, AxisCommand, string>? describe = null)
    {
        var axis = m.GetAxis(axisKey);
        if (axis is null) return AxisNotFound(axisKey);
        var cmd = factory(axis);
        if (axis.MotionBlocked && cmd.Kind is AxisCommandKind.Enable or AxisCommandKind.MoveAbsolute or AxisCommandKind.MoveRelative
                                            or AxisCommandKind.Jog or AxisCommandKind.Home)
        {
            return Results.Problem($"axis {axis.Id} ({axis.Name}) blocked by interlock: {axis.BlockReason}", statusCode: StatusCodes.Status409Conflict);
        }
        axis.Submit(cmd);
        var msg = describe is null ? "queued for next EtherCAT cycle" : describe(axis, cmd);
        return Results.Accepted($"/api/axes/{axis.Id}", new CommandAccepted(axis.Id, cmd.Kind.ToString(), msg));
    }

    private static bool TryUnit(string? text, out UserUnit? unit, out IResult? error)
    {
        unit = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (DriveMechanics.TryParseUnit(text, out var u)) { unit = u; return true; }
        error = Results.Problem($"unit '{text}' 은 step 또는 um 이어야 합니다", statusCode: 400);
        return false;
    }

    private static string UnitName(Axis a, UserUnit? unit) => (unit ?? a.Mechanics.Unit) == UserUnit.Um ? "um" : "step";

    private static IResult AxisNotFound(string axis) =>
        Results.Problem($"axis '{axis}' not found (번호 또는 별칭)", statusCode: StatusCodes.Status404NotFound);
}
