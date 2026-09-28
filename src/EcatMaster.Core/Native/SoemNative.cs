using System.Runtime.InteropServices;

namespace EcatMaster.Core.Native;

/// <summary>
/// native/soemshim (SOEM 1.4.x 위에 얹은 얇은 C 래퍼)에 대한 P/Invoke 선언.
/// libsoemshim.so 가 실행 파일 옆 또는 LD_LIBRARY_PATH 에 있어야 한다.
/// </summary>
internal static unsafe class SoemNative
{
    private const string Lib = "soemshim";

    // SOEM 상태 상수 (ethercat.h)
    public const ushort EC_STATE_NONE = 0x00;
    public const ushort EC_STATE_INIT = 0x01;
    public const ushort EC_STATE_PRE_OP = 0x02;
    public const ushort EC_STATE_BOOT = 0x03;
    public const ushort EC_STATE_SAFE_OP = 0x04;
    public const ushort EC_STATE_OPERATIONAL = 0x08;
    public const ushort EC_STATE_ACK = 0x10;
    public const ushort EC_STATE_ERROR = 0x10;

    public const int EC_TIMEOUTRET = 2_000;        // us
    public const int EC_TIMEOUTRXM = 700_000;      // us (SDO)
    public const int EC_TIMEOUTSTATE = 2_000_000;  // us

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int Po2SoCallback(ushort slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_adapter_count();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr shim_adapter_name(int i);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr shim_adapter_desc(int i);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_init([MarshalAs(UnmanagedType.LPStr)] string ifname);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void shim_close();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_config_init();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void shim_set_po2so_callback(Po2SoCallback? cb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_config_map(void* iomap, int iomapSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_slave_count();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr shim_slave_name(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint shim_slave_vendor_id(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint shim_slave_product_code(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ushort shim_slave_state(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ushort shim_slave_al_status(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr shim_al_status_text(ushort code);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* shim_slave_outputs(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_slave_output_bytes(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* shim_slave_inputs(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_slave_input_bytes(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_slave_output_bits(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_slave_input_bits(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_slave_output_startbit(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_slave_input_startbit(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_request_state(int slave, ushort state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ushort shim_state_check(int slave, ushort state, int timeoutUs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void shim_read_state();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_send_processdata();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_receive_processdata(int timeoutUs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_expected_wkc();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_sdo_write(int slave, ushort index, byte subindex, int completeAccess, int size, void* data, int timeoutUs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_sdo_read(int slave, ushort index, byte subindex, int completeAccess, int* size, void* data, int timeoutUs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_dc_config();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long shim_dc_time();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_slave_hasdc(int slave);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void shim_dcsync0(int slave, int active, uint cycleNs, int shiftNs);

    /// <summary>simple_test.c 의 ecatcheck 한 회. 반환값: OP 가 아닌 슬레이브 수.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_check_and_recover(int wkc, int expectedWkc);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_set_realtime(int priority);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int shim_pin_cpu(int cpu);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long shim_now_ns();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void shim_sleep_until_ns(long t);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr shim_last_error();

    // ---- DLL 검색 경로 ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string? lpPathName);

    private static bool _searchPathReady;

    /// <summary>
    /// Windows: Npcap 이 "WinPcap API-compatible Mode" 없이 설치되면 wpcap.dll 이 System32\Npcap 에만 있어
    /// soemshim.dll 로드가 실패한다. 그 폴더를 DLL 검색 경로에 넣어 두 설치 방식 모두 지원한다.
    /// 첫 P/Invoke 전에 한 번 호출.
    /// </summary>
    public static void EnsureNativeSearchPath()
    {
        if (_searchPathReady) return;
        _searchPathReady = true;
        if (!OperatingSystem.IsWindows()) return;

        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string npcapDir = Path.Combine(sys32, "Npcap");
        if (!File.Exists(Path.Combine(sys32, "wpcap.dll")) && File.Exists(Path.Combine(npcapDir, "wpcap.dll")))
        {
            SetDllDirectoryW(npcapDir);
        }
    }

    /// <summary>Windows 에서 Npcap 설치 여부 진단용.</summary>
    public static string DescribeNpcap()
    {
        if (!OperatingSystem.IsWindows()) return "n/a (Linux raw socket)";
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        bool compat = File.Exists(Path.Combine(sys32, "wpcap.dll"));
        bool npcap = File.Exists(Path.Combine(sys32, "Npcap", "wpcap.dll"));
        if (compat) return "Npcap/WinPcap 호환 모드 설치됨 (System32\\wpcap.dll)";
        if (npcap) return "Npcap 설치됨 (System32\\Npcap\\wpcap.dll, 호환 모드 아님 → 검색 경로 자동 추가)";
        return "Npcap 미설치 (https://npcap.com 에서 설치)";
    }

    // ---- 편의 래퍼 ----

    public static string SlaveName(int slave) => Marshal.PtrToStringAnsi(shim_slave_name(slave)) ?? string.Empty;
    public static string AlStatusText(ushort code) => Marshal.PtrToStringAnsi(shim_al_status_text(code)) ?? string.Empty;
    public static string LastError() => Marshal.PtrToStringAnsi(shim_last_error()) ?? string.Empty;

    public static bool SdoWrite<T>(int slave, ushort index, byte sub, T value) where T : unmanaged
    {
        T v = value;
        return shim_sdo_write(slave, index, sub, 0, sizeof(T), &v, EC_TIMEOUTRXM) > 0;
    }

    public static bool SdoRead<T>(int slave, ushort index, byte sub, out T value) where T : unmanaged
    {
        T v = default;
        int size = sizeof(T);
        bool ok = shim_sdo_read(slave, index, sub, 0, &size, &v, EC_TIMEOUTRXM) > 0;
        value = v;
        return ok;
    }
}
