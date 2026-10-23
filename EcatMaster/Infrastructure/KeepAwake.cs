using System.Runtime.InteropServices;

namespace EcatMaster.Infrastructure;

/// <summary>
/// 장시간 운전 중 Windows 가 절전(대기) 모드로 들어가 EtherCAT 사이클이 멈추지 않도록 막는다.
/// 화면 꺼짐은 허용하고 시스템 절전만 막는다. 호출한 스레드(UI 스레드) 기준으로 유지된다.
/// </summary>
public static class KeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private static bool _active;

    public static void Set(bool active)
    {
        if (active == _active) return;
        _active = active;
        SetThreadExecutionState(active ? EsContinuous | EsSystemRequired : EsContinuous);
    }
}
