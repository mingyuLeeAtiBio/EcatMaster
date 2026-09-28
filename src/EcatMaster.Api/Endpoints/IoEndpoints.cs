using EcatMaster.Core.EtherCat;

namespace EcatMaster.Api.Endpoints;

/// <summary>
/// IO 엔드포인트. {name} 은 모듈 별칭 또는 ID, {bit} 는 비트 번호("3") 또는 비트 별칭("RunLamp").
/// </summary>
public static class IoEndpoints
{
    public static IEndpointRouteBuilder MapIoEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/io").WithTags("IO");

        g.MapGet("/", (IEtherCatMaster m) => m.IoModules.Select(x => x.GetStatus()))
            .WithSummary("모든 IO 모듈 상태 (입력/출력 비트, 별칭, 강제 여부)");

        g.MapGet("/{name}", (string name, IEtherCatMaster m) =>
                m.GetIoModule(name) is { } io ? Results.Ok(io.GetStatus()) : NotFound(name))
            .WithSummary("IO 모듈 상태 (별칭 또는 ID)");

        g.MapGet("/{name}/inputs/{bit}", (string name, string bit, IEtherCatMaster m) =>
            {
                var io = m.GetIoModule(name);
                if (io is null) return NotFound(name);
                if (!io.TryResolveInput(bit, out int b)) return BitNotFound(io, bit, input: true);
                return Results.Ok(new { module = io.Name, bit = b, name = io.InputNames[b], value = io.GetInput(b) });
            })
            .WithSummary("입력 비트 하나 읽기 (번호 또는 별칭)");

        g.MapGet("/{name}/outputs/{bit}", (string name, string bit, IEtherCatMaster m) =>
            {
                var io = m.GetIoModule(name);
                if (io is null) return NotFound(name);
                if (!io.TryResolveOutput(bit, out int b)) return BitNotFound(io, bit, input: false);
                return Results.Ok(new { module = io.Name, bit = b, name = io.OutputNames[b], value = io.GetOutput(b) });
            })
            .WithSummary("출력 비트 하나 읽기 (번호 또는 별칭)");

        g.MapPut("/{name}/outputs/{bit}", (string name, string bit, OutputBitRequest req, IEtherCatMaster m) =>
            {
                var io = m.GetIoModule(name);
                if (io is null) return NotFound(name);
                if (!io.TryResolveOutput(bit, out int b)) return BitNotFound(io, bit, input: false);
                io.SetOutput(b, req.Value);
                return Results.Ok(io.GetStatus());
            })
            .WithSummary("출력 비트 하나 설정. {bit} = 번호 또는 별칭, body: { value: true|false }");

        g.MapPut("/{name}/outputs", (string name, OutputWordRequest req, IEtherCatMaster m) =>
            {
                var io = m.GetIoModule(name);
                if (io is null) return NotFound(name);
                io.SetOutputs(req.Value, req.Mask ?? ulong.MaxValue);
                return Results.Ok(io.GetStatus());
            })
            .WithSummary("출력 워드 설정. body: { value, mask? } (mask 의 1 비트만 변경)");

        g.MapPut("/{name}/inputs/{bit}", (string name, string bit, ForceInputRequest req, IEtherCatMaster m) =>
            {
                if (!m.SupportsInputForcing) return Results.Problem("input forcing is only available in simulated mode", statusCode: 409);
                var io = m.GetIoModule(name);
                if (io is null) return NotFound(name);
                if (!io.TryResolveInput(bit, out int b)) return BitNotFound(io, bit, input: true);
                io.ForceInput(b, req.Value);
                return Results.Ok(io.GetStatus());
            })
            .WithSummary("(시뮬레이션) 입력 비트 강제. body: { value: true|false|null } null = 해제");

        g.MapDelete("/{name}/inputs/forced", (string name, IEtherCatMaster m) =>
            {
                var io = m.GetIoModule(name);
                if (io is null) return NotFound(name);
                io.ClearForcedInputs();
                return Results.Ok(io.GetStatus());
            })
            .WithSummary("강제 입력 모두 해제");

        // ---- 별칭 (런타임 변경. 영구 반영은 설정 Master:IoModules) ----

        g.MapPut("/{name}/alias", (string name, AliasRequest req, IEtherCatMaster m) =>
            {
                var io = m.GetIoModule(name);
                if (io is null) return NotFound(name);
                if (string.IsNullOrWhiteSpace(req.Name)) return Results.Problem("name 이 비어 있습니다", statusCode: 400);
                var dup = m.GetIoModule(req.Name.Trim());
                if (dup is not null && dup.Id != io.Id) return Results.Problem($"별칭 '{req.Name}' 은 모듈 {dup.Id} 가 사용 중", statusCode: 409);
                io.Name = req.Name.Trim();
                return Results.Ok(io.GetStatus());
            })
            .WithSummary("IO 모듈 별칭 변경 (런타임만)");

        g.MapPut("/{name}/inputs/{bit}/alias", (string name, string bit, AliasRequest req, IEtherCatMaster m) =>
            {
                var io = m.GetIoModule(name);
                if (io is null) return NotFound(name);
                if (!io.TryResolveInput(bit, out int b)) return BitNotFound(io, bit, input: true);
                io.SetInputName(b, req.Name);
                return Results.Ok(io.GetStatus());
            })
            .WithSummary("입력 비트 별칭 변경 (런타임만)");

        g.MapPut("/{name}/outputs/{bit}/alias", (string name, string bit, AliasRequest req, IEtherCatMaster m) =>
            {
                var io = m.GetIoModule(name);
                if (io is null) return NotFound(name);
                if (!io.TryResolveOutput(bit, out int b)) return BitNotFound(io, bit, input: false);
                io.SetOutputName(b, req.Name);
                return Results.Ok(io.GetStatus());
            })
            .WithSummary("출력 비트 별칭 변경 (런타임만)");

        app.MapGet("/api/interlocks", (IEtherCatMaster m) => m.GetInterlocks())
            .WithTags("IO")
            .WithSummary("인터록 규칙별 상태 (활성 여부, 트리거 횟수, 마지막 트리거 시각)");

        return app;
    }

    private static IResult NotFound(string name) =>
        Results.Problem($"IO module '{name}' not found", statusCode: StatusCodes.Status404NotFound);

    private static IResult BitNotFound(IoModule io, string bit, bool input) =>
        Results.Problem($"{(input ? "input" : "output")} '{bit}' not found in {io.Name} (0..{(input ? io.InputBits : io.OutputBits) - 1} 또는 별칭)",
            statusCode: StatusCodes.Status404NotFound);
}
