using System.Text;
using EcatMaster.Core.Serial;

namespace EcatMaster.Api.Endpoints;

public static class SerialEndpoints
{
    public static IEndpointRouteBuilder MapSerialEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/serial").WithTags("Serial");

        g.MapGet("/devices", () => SerialService.AvailableDevices())
            .WithSummary("OS 가 인식한 시리얼 장치 목록");

        g.MapGet("/", (SerialService s) => s.GetAll())
            .WithSummary("설정된 포트 상태");

        g.MapGet("/{name}", (string name, SerialService s) =>
                s.GetStatus(name) is { } st ? Results.Ok(st) : NotFound(name))
            .WithSummary("포트 상태");

        g.MapPost("/{name}/open", (string name, SerialOpenRequest? req, SerialService s) =>
            {
                if (!s.Exists(name)) return NotFound(name);
                s.Open(name, req?.BaudRate);
                return Results.Ok(s.GetStatus(name));
            })
            .WithSummary("포트 열기");

        g.MapPost("/{name}/close", (string name, SerialService s) =>
            {
                if (!s.Exists(name)) return NotFound(name);
                s.Close(name);
                return Results.Ok(s.GetStatus(name));
            })
            .WithSummary("포트 닫기");

        g.MapPost("/{name}/write", (string name, SerialWriteRequest req, SerialService s) =>
            {
                if (!s.Exists(name)) return NotFound(name);
                if (!string.IsNullOrEmpty(req.Hex))
                {
                    s.WriteBytes(name, ParseHex(req.Hex));
                }
                else if (req.Text is not null)
                {
                    if (req.AppendNewLine) s.WriteLine(name, req.Text); else s.Write(name, req.Text);
                }
                else
                {
                    return Results.Problem("Text 또는 Hex 중 하나는 필요합니다", statusCode: 400);
                }
                return Results.Ok(s.GetStatus(name));
            })
            .WithSummary("송신 (Text 또는 Hex)");

        g.MapGet("/{name}/read", (string name, int? max, SerialService s) =>
                s.Exists(name) ? Results.Ok(s.ReadLines(name, max ?? 100)) : NotFound(name))
            .WithSummary("수신 버퍼에서 라인 꺼내기 (꺼낸 라인은 버퍼에서 제거)");

        return app;
    }

    private static IResult NotFound(string name) =>
        Results.Problem($"serial port '{name}' not configured", statusCode: StatusCodes.Status404NotFound);

    private static byte[] ParseHex(string hex)
    {
        var sb = new StringBuilder(hex.Length);
        foreach (char c in hex) if (Uri.IsHexDigit(c)) sb.Append(c);
        if (sb.Length % 2 != 0) throw new ArgumentException("hex string must have even number of digits");
        return Convert.FromHexString(sb.ToString());
    }
}
