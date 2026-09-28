using EcatMaster.Core.EtherCat;

namespace EcatMaster.Api.Endpoints;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/system").WithTags("System");

        g.MapGet("/status", (IEtherCatMaster m) => m.GetStatus())
            .WithSummary("마스터/슬레이브 상태, WKC, 지터");

        g.MapPost("/enable", (IEtherCatMaster m) => { m.EnableAll(); return Results.Accepted(); })
            .WithSummary("모든 축 서보 온");

        g.MapPost("/disable", (IEtherCatMaster m) => { m.DisableAll(); return Results.Accepted(); })
            .WithSummary("모든 축 서보 오프");

        g.MapPost("/stop", (StopRequest? req, IEtherCatMaster m) =>
            {
                m.StopAll(req?.Quick ?? false);
                return Results.Accepted();
            })
            .WithSummary("모든 축 정지 (Quick=false 감속정지, true 급정지+서보오프)");

        g.MapPost("/fault-reset", (IEtherCatMaster m) => { m.FaultResetAll(); return Results.Accepted(); })
            .WithSummary("모든 축 알람 리셋");

        g.MapPost("/reset-stats", (IEtherCatMaster m) => { m.ResetStatistics(); return Results.Accepted(); })
            .WithSummary("지터·WKC 오류 누적 통계 초기화");

        // ---- SDO (디버그/설정 확인) ----
        var sdo = app.MapGroup("/api/sdo").WithTags("SDO");

        sdo.MapGet("/{slave:int}/{index}/{sub:int}", (int slave, string index, int sub, int? size, IEtherCatMaster m) =>
            {
                if (!TryParseHex(index, out ushort idx)) return Results.Problem("index 는 16진수 (예: 6061)", statusCode: 400);
                var r = m.SdoRead(slave, idx, (byte)sub, size ?? 4);
                return r.Success ? Results.Ok(r) : Results.Problem(r.Error, statusCode: 502);
            })
            .WithSummary("SDO 읽기. index 는 16진수 문자열, size = 1|2|4 (기본 4)");

        sdo.MapPost("/{slave:int}/{index}/{sub:int}", (int slave, string index, int sub, SdoWriteRequest req, IEtherCatMaster m) =>
            {
                if (!TryParseHex(index, out ushort idx)) return Results.Problem("index 는 16진수 (예: 605A)", statusCode: 400);
                var r = m.SdoWrite(slave, idx, (byte)sub, req.Size, req.Value);
                return r.Success ? Results.Ok(r) : Results.Problem(r.Error, statusCode: 502);
            })
            .WithSummary("SDO 쓰기. body: { size: 1|2|4, value: 정수 }");

        g.MapGet("/health", (IEtherCatMaster m) =>
            {
                var s = m.GetStatus();
                return s.Running && s.AllOperational ? Results.Ok("ok") : Results.Problem("EtherCAT not operational", statusCode: 503);
            })
            .WithSummary("헬스체크 (503 = EtherCAT 비정상)");

        return app;
    }

    private static bool TryParseHex(string s, out ushort value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.EndsWith('h') || s.EndsWith('H')) s = s[..^1];
        return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}
