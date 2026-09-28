using EcatMaster.Core.EtherCat;
using EcatMaster.Core.Serial;

namespace EcatMaster.Api;

/// <summary>
/// 시리얼로 들어온 텍스트 라인을 모션 명령으로 해석하는 브리지 (확장 지점).
/// 기본 프로토콜(예시):
///   "MOVE 1 10000"   → 1축 절대 이동
///   "STOP"           → 전체 감속 정지
///   "QSTOP"          → 전체 급정지
///   "ENABLE" / "DISABLE"
/// 실제 상대 장비(PLC, HMI 등)의 프로토콜에 맞게 <see cref="Handle"/> 만 바꾸면 된다.
/// </summary>
public sealed class SerialCommandBridge : IHostedService
{
    private readonly SerialService _serial;
    private readonly IEtherCatMaster _master;
    private readonly ILogger<SerialCommandBridge> _log;

    public SerialCommandBridge(SerialService serial, IEtherCatMaster master, ILogger<SerialCommandBridge> log)
    {
        _serial = serial;
        _master = master;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serial.LineReceived += Handle;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _serial.LineReceived -= Handle;
        return Task.CompletedTask;
    }

    private void Handle(string port, string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        try
        {
            switch (parts[0].ToUpperInvariant())
            {
                case "STOP": _master.StopAll(quick: false); break;
                case "QSTOP": _master.StopAll(quick: true); break;
                case "ENABLE": _master.EnableAll(); break;
                case "DISABLE": _master.DisableAll(); break;
                case "MOVE" when parts.Length >= 3:
                    _master.GetAxis(int.Parse(parts[1]))?.Submit(new AxisCommand(AxisCommandKind.MoveAbsolute, int.Parse(parts[2])));
                    break;
                default:
                    _log.LogDebug("Serial[{Port}] unhandled line: {Line}", port, line);
                    return;
            }
            _log.LogInformation("Serial[{Port}] command: {Line}", port, line);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Serial[{Port}] bad command: {Line}", port, line);
        }
    }
}
