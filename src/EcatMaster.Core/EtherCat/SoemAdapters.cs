using System.Runtime.InteropServices;
using EcatMaster.Core.Native;

namespace EcatMaster.Core.EtherCat;

public sealed record NetworkAdapter(string Name, string Description)
{
    public override string ToString() => string.IsNullOrEmpty(Description) ? Name : $"{Description}  [{Name}]";
}

/// <summary>NIC 하나를 EtherCAT 으로 탐색한 결과. SlaveCount 0 = 슬레이브 없음, Error = 열기/탐색 실패.</summary>
public sealed record AdapterProbe(NetworkAdapter Adapter, int SlaveCount, string[] SlaveNames, string? Error, TimeSpan Elapsed)
{
    public bool HasSlaves => SlaveCount > 0;

    /// <summary>"Intel I350 #2 — 슬레이브 20개 (NCR-HD Series, AZD-KRED ×4, …)"</summary>
    public string Summary
    {
        get
        {
            if (Error is not null) return $"{Adapter.Description} — 실패: {Error}";
            if (SlaveCount == 0) return $"{Adapter.Description} — 슬레이브 없음";
            var groups = SlaveNames.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).Take(4).ToList();
            if (SlaveNames.Distinct().Count() > 4) groups.Add("…");
            return $"{Adapter.Description} — 슬레이브 {SlaveCount}개 ({string.Join(", ", groups)})";
        }
    }
}

/// <summary>SOEM 이 보는 NIC 목록. Windows 에서는 Npcap 이 설치되어 있어야 하며 이름이 \Device\NPF_{GUID} 형식이다.</summary>
public static class SoemAdapters
{
    /// <summary>SOEM 은 전역 컨텍스트 하나라 탐색은 한 번에 하나씩, 그리고 마스터가 연결되어 있지 않을 때만.</summary>
    private static readonly object ProbeLock = new();

    /// <summary>네이티브 라이브러리를 못 찾으면 <see cref="DllNotFoundException"/> 을 던진다.</summary>
    public static IReadOnlyList<NetworkAdapter> List()
    {
        SoemNative.EnsureNativeSearchPath();
        int n = SoemNative.shim_adapter_count();
        var list = new List<NetworkAdapter>(n);
        for (int i = 0; i < n; i++)
        {
            list.Add(new NetworkAdapter(
                Marshal.PtrToStringAnsi(SoemNative.shim_adapter_name(i)) ?? "",
                Marshal.PtrToStringAnsi(SoemNative.shim_adapter_desc(i)) ?? ""));
        }
        return list;
    }

    /// <summary>탐색에서 건너뛸 어댑터 (루프백·가상 미니포트 등).</summary>
    public static bool IsProbeCandidate(NetworkAdapter a)
    {
        var d = a.Description ?? "";
        return !(d.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                 || d.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
                 || d.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// NIC 하나에 EtherCAT 슬레이브가 있는지 본다: ec_init → ec_config_init(브로드캐스트 탐색) → 슬레이브 이름 수집 → ec_close.
    /// 슬레이브가 없는 포트는 수십~수백 ms 안에 0 으로 돌아온다. 마스터가 연결된 상태에서 부르면 안 된다.
    /// </summary>
    public static AdapterProbe Probe(NetworkAdapter adapter)
    {
        lock (ProbeLock)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                SoemNative.EnsureNativeSearchPath();
                if (SoemNative.shim_init(adapter.Name) == 0)
                    return new AdapterProbe(adapter, 0, [], SoemNative.LastError(), sw.Elapsed);
                try
                {
                    int n = SoemNative.shim_config_init();
                    if (n <= 0) return new AdapterProbe(adapter, 0, [], null, sw.Elapsed);
                    var names = new string[n];
                    for (int s = 1; s <= n; s++) names[s - 1] = SoemNative.SlaveName(s);
                    return new AdapterProbe(adapter, n, names, null, sw.Elapsed);
                }
                finally
                {
                    try { SoemNative.shim_close(); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                return new AdapterProbe(adapter, 0, [], ex.Message, sw.Elapsed);
            }
        }
    }

    /// <summary>후보 NIC 를 차례로 탐색한다. progress 는 NIC 하나가 끝날 때마다 호출된다 (호출 스레드 = 이 메서드를 부른 스레드).</summary>
    public static IReadOnlyList<AdapterProbe> ProbeAll(IEnumerable<NetworkAdapter> adapters, Action<AdapterProbe>? progress = null, CancellationToken ct = default)
    {
        var results = new List<AdapterProbe>();
        foreach (var a in adapters)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsProbeCandidate(a)) continue;
            var r = Probe(a);
            results.Add(r);
            progress?.Invoke(r);
        }
        return results;
    }

    /// <summary>soemshim 네이티브 라이브러리가 로드 가능한지 확인.</summary>
    /// <summary>Npcap 설치 상태 설명 (Windows 전용 진단).</summary>
    public static string NpcapStatus => SoemNative.DescribeNpcap();

    public static bool IsNativeAvailable(out string? error)
    {
        try
        {
            SoemNative.EnsureNativeSearchPath();
            SoemNative.shim_adapter_count();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            error = ex.Message;
            return false;
        }
    }
}
