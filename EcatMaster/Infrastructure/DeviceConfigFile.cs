using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EcatMaster.Core.EtherCat;

namespace EcatMaster.Infrastructure;

/// <summary>
/// GUI 용 장비 구성 파일: %LOCALAPPDATA%\EcatMaster\devices.json
/// 축 별칭·리드, IO 모듈 별칭과 비트 별칭, 인터록 규칙, 상태 출력, 시뮬레이션 IO 모듈을 정의한다.
/// 없으면 예시가 담긴 기본 파일을 만든다. (API 서버는 같은 내용을 appsettings.json 의 Master 섹션에서 읽는다.)
/// </summary>
public sealed class DeviceConfigFile
{
    /// <summary>축 별칭과 리드. 예: { "Id": 1, "Name": "X", "LeadUm": 10000 }</summary>
    public List<AxisOptions> Axes { get; set; } = new();
    public List<IoModuleOptions> IoModules { get; set; } = new();
    public List<InterlockRule> Interlocks { get; set; } = new();
    public List<StatusOutputRule> StatusOutputs { get; set; } = new();
    public List<SimulatedIoModuleOptions> SimulatedIoModules { get; set; } = new();

    public static readonly string PathOnDisk =
        AppPaths.DevicesFile;    // %LOCALAPPDATA%/EcatMaster/devices.json (구 DirectControl 폴더에서 자동 이전)

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static DeviceConfigFile LoadOrCreateDefault(out string? error)
    {
        error = null;
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<DeviceConfigFile>(File.ReadAllText(PathOnDisk), JsonOpts) ?? CreateDefault();
        }
        catch (Exception ex)
        {
            error = $"{PathOnDisk} 읽기 실패: {ex.Message} (기본 구성 사용)";
            return CreateDefault();
        }

        var def = CreateDefault();
        def.Save();
        return def;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
            File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 저장 실패는 무시 */ }
    }

    /// <summary>축 별칭(과 선택적으로 리드)을 기록한다. 항목이 없으면 만든다.</summary>
    public void SetAxisAlias(int axisId, string name, double? leadUm = null)
    {
        var ax = Axes.FirstOrDefault(a => a.Id == axisId);
        if (ax is null) { ax = new AxisOptions { Id = axisId }; Axes.Add(ax); Axes.Sort((a, b) => a.Id.CompareTo(b.Id)); }
        ax.Name = name;
        if (leadUm is > 0) ax.LeadUm = leadUm;
    }

    /// <summary>VPH 등 엔코더 분해능이 필요한 축의 PulsesPerRev 를 기록한다.</summary>
    public void SetAxisPulsesPerRev(int axisId, double pulsesPerRev)
    {
        var ax = Axes.FirstOrDefault(a => a.Id == axisId);
        if (ax is null) { ax = new AxisOptions { Id = axisId }; Axes.Add(ax); Axes.Sort((a, b) => a.Id.CompareTo(b.Id)); }
        ax.PulsesPerRev = pulsesPerRev;
    }

    /// <summary>IO 비트 별칭을 기록한다. 모듈 항목이 없으면 이름만으로 만든다 (Slave 는 실기 연결 후 채워짐).</summary>
    public void SetIoBitAlias(string moduleName, int slave, bool input, int bit, string name)
    {
        var io = IoModules.FirstOrDefault(m => string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase))
                 ?? IoModules.FirstOrDefault(m => slave > 0 && slave < 1000 && m.Slave == slave);
        if (io is null)
        {
            io = new IoModuleOptions { Name = moduleName, Slave = slave < 1000 ? slave : 0 };
            IoModules.Add(io);
        }
        var dict = input ? io.InputAliases : io.OutputAliases;
        // 기본 이름(IN4/OUT4)으로 되돌리면 항목을 지워 파일을 깨끗하게 유지
        if (string.Equals(name, (input ? "IN" : "OUT") + bit, StringComparison.Ordinal)) dict.Remove(bit.ToString());
        else dict[bit.ToString()] = name;
    }

    /// <summary>예시 구성: X/Y/Z 축, EStop/Door(NC 접점) 인터록, 에어리어 센서(X·Y만), 상태 램프.</summary>
    public static DeviceConfigFile CreateDefault() => new()
    {
        Axes =
        {
            new AxisOptions { Id = 1, Name = "X", LeadUm = 10_000 },
            new AxisOptions { Id = 2, Name = "Y", LeadUm = 10_000 },
            new AxisOptions { Id = 3, Name = "Z", LeadUm = 5_000 },
        },
        IoModules =
        {
            new IoModuleOptions { Slave = 13, Name = "DI1", InputAliases = new() { ["0"] = "EStop", ["1"] = "Door", ["2"] = "AreaSensor", ["3"] = "StartBtn" } },
            new IoModuleOptions { Slave = 14, Name = "DO1", OutputAliases = new() { ["0"] = "RunLamp", ["1"] = "AlarmLamp", ["2"] = "InterlockLamp", ["3"] = "Heartbeat", ["4"] = "Clamp1", ["5"] = "Clamp2" } },
        },
        Interlocks =
        {
            new InterlockRule { Name = "EStop", Module = "DI1", Input = "EStop", ActiveLow = true, Action = InterlockAction.QuickStop, BlockMotion = true, OutputModule = "DO1", Output = "InterlockLamp", Message = "비상정지 눌림" },
            new InterlockRule { Name = "Door", Module = "DI1", Input = "Door", ActiveLow = true, Action = InterlockAction.Halt, BlockMotion = true, Message = "도어 열림" },
            new InterlockRule { Name = "AreaSensor", Module = "DI1", Input = "AreaSensor", ActiveLow = false, Action = InterlockAction.Halt, Axes = [1, 2], BlockMotion = true, Message = "에어리어 센서 감지" },
            // 축 조건 예시: X 이동 중이면 Z 정지·차단 / Z 가 안전 범위 안일 때만 X 이동 허용
            new InterlockRule { Name = "ZvsXMoving", Source = InterlockSource.AxisMoving, SourceAxis = "X", Mode = InterlockMode.BlockWhileActive, Action = InterlockAction.Halt, Axes = [3], BlockMotion = true, Message = "X 이동 중 — Z 대기" },
            new InterlockRule { Name = "XNeedsZSafe", Source = InterlockSource.AxisInRange, SourceAxis = "Z", Min = 1000, Max = 2000, Unit = "um", Hysteresis = 20, Mode = InterlockMode.AllowOnlyWhileActive, Action = InterlockAction.Halt, Axes = [1], BlockMotion = true, Message = "Z 안전 범위 밖 — X 금지" },
        },
        StatusOutputs =
        {
            new StatusOutputRule { Module = "DO1", Output = "RunLamp", Source = StatusOutputSource.AnyMoving },
            new StatusOutputRule { Module = "DO1", Output = "AlarmLamp", Source = StatusOutputSource.AnyFault },
            new StatusOutputRule { Module = "DO1", Output = "Heartbeat", Source = StatusOutputSource.Heartbeat },
        },
        SimulatedIoModules =
        {
            new SimulatedIoModuleOptions { Name = "DI1", InputBits = 16, OutputBits = 0 },
            new SimulatedIoModuleOptions { Name = "DO1", InputBits = 0, OutputBits = 16 },
        },
    };

    public void ApplyTo(MasterOptions options)
    {
        options.Axes = Axes;
        options.IoModules = IoModules;
        options.Interlocks = Interlocks;
        options.StatusOutputs = StatusOutputs;
        if (SimulatedIoModules.Count > 0) options.SimulatedIoModules = SimulatedIoModules;
    }
}
