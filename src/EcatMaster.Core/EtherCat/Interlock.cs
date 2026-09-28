using System.Text.Json.Serialization;

namespace EcatMaster.Core.EtherCat;

public enum InterlockAction
{
    /// <summary>정지 명령 없음. BlockMotion 만으로 새 동작을 막을 때.</summary>
    None,
    /// <summary>대상 축 감속 정지(Halt). 서보 유지.</summary>
    Halt,
    /// <summary>대상 축 급정지 후 서보 오프(Quick stop).</summary>
    QuickStop,
    /// <summary>대상 축 서보 오프(Disable).</summary>
    Disable,
}

/// <summary>인터록이 감시하는 조건의 종류.</summary>
public enum InterlockSource
{
    /// <summary>디지털 입력 비트 (Module/Bit/Input, ActiveLow)</summary>
    Input,
    /// <summary>소스 축이 이동 중 (IsMotionActive, 선택적으로 속도 문턱)</summary>
    AxisMoving,
    /// <summary>소스 축 위치가 [Min, Max] 안</summary>
    AxisInRange,
    /// <summary>소스 축 위치가 [Min, Max] 밖</summary>
    AxisOutOfRange,
}

/// <summary>조건이 참일 때 무엇을 하는가.</summary>
public enum InterlockMode
{
    /// <summary>조건이 참인 동안 차단 (기본). 예: "X 이동 중이면 Z 정지·차단"</summary>
    BlockWhileActive,
    /// <summary>조건이 참인 동안만 허용, 거짓이면 차단. 예: "Z 가 안전 범위 안일 때만 X 이동"</summary>
    AllowOnlyWhileActive,
}

/// <summary>
/// 인터록 규칙 (설정 "Master:Interlocks", GUI 는 devices.json).
/// 규칙 하나 = 조건 하나. 조건(<see cref="Source"/>)이 활성이 되는 순간(상승 에지) <see cref="EffectiveAction"/> 을 대상 축에 적용하고,
/// 활성 상태가 유지되는 동안 BlockMotion 이면 대상 축의 서보 온·이동·조그·원점복귀 명령을 거부한다.
/// <see cref="Mode"/>=AllowOnlyWhileActive 면 조건이 거짓일 때가 "활성(차단)" 이다.
/// </summary>
public sealed class InterlockRule
{
    public string Name { get; set; } = "";

    /// <summary>조건 종류. 구 파일에는 없으므로 기본 Input.</summary>
    public InterlockSource Source { get; set; } = InterlockSource.Input;

    // ---- Source = Input ----

    /// <summary>입력 IO 모듈 이름 (IoModule.Name)</summary>
    public string Module { get; set; } = "";

    /// <summary>입력 비트 번호. <see cref="Input"/> 별칭이 있으면 그것을 우선 해석한다.</summary>
    public int Bit { get; set; }

    /// <summary>입력 비트 별칭 (예: "EStop"). 지정하면 Bit 대신 사용.</summary>
    public string? Input { get; set; }

    /// <summary>true = 입력 0 일 때 활성 (NC 접점 비상정지·도어 스위치의 일반적인 배선)</summary>
    public bool ActiveLow { get; set; }

    // ---- Source = Axis* ----

    /// <summary>소스 축: 번호("3") 또는 별칭("Z").</summary>
    public string? SourceAxis { get; set; }

    /// <summary>위치 범위 [Min, Max] (AxisInRange/AxisOutOfRange). 단위는 <see cref="Unit"/>.</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }

    /// <summary>"um" | "step". 비우면 소스 축의 현재 사용자 단위.</summary>
    public string? Unit { get; set; }

    /// <summary>범위 경계 히스테리시스 (같은 단위, ≥0). 차단 진입은 정확한 경계에서, 해제는 경계에서 이 값만큼 벗어나야 한다.</summary>
    public double Hysteresis { get; set; }

    /// <summary>AxisMoving 보조: |실속도| ≥ 이 값(단위/s) 이면 이동 중으로 본다. Halt 직후 감속 중인 실기 드라이브를 잡기 위함. null/0 = IsMotionActive 만.</summary>
    public double? VelocityThreshold { get; set; }

    /// <summary>조건이 참일 때 차단(기본) / 조건이 참일 때만 허용.</summary>
    public InterlockMode Mode { get; set; } = InterlockMode.BlockWhileActive;

    // ---- 공통 ----

    /// <summary>표시 출력 비트 별칭 (예: "InterlockLamp"). 지정하면 OutputBit 대신 사용.</summary>
    public string? Output { get; set; }

    /// <summary>활성 상승 에지에 대상 축에 내릴 동작. null 이면 Source 별 기본값 (Input=QuickStop, 축 조건=Halt).</summary>
    public InterlockAction? Action { get; set; }

    [JsonIgnore]
    public InterlockAction EffectiveAction => Action ?? (Source == InterlockSource.Input ? InterlockAction.QuickStop : InterlockAction.Halt);

    /// <summary>대상 축 ID. 비어 있으면 모든 축.</summary>
    public int[] Axes { get; set; } = [];

    /// <summary>활성 중 대상 축의 새 동작 명령 차단.</summary>
    public bool BlockMotion { get; set; } = true;

    /// <summary>활성 상태를 표시할 출력 (선택). 예: 경고등.</summary>
    public string? OutputModule { get; set; }
    public int? OutputBit { get; set; }

    /// <summary>API/GUI 에 보여 줄 메시지 (예: "도어 열림")</summary>
    public string? Message { get; set; }

    /// <summary>false 면 규칙을 평가하지 않는다 (시운전·점검용 일시 해제). 런타임에 GUI 로 바꿀 수 있다.</summary>
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public bool IsAxisSource => Source != InterlockSource.Input;
    [JsonIgnore]
    public bool IsRangeSource => Source is InterlockSource.AxisInRange or InterlockSource.AxisOutOfRange;

    /// <summary>얕은 복사 (편집기용).</summary>
    public InterlockRule Clone() => (InterlockRule)MemberwiseClone();
}

public enum StatusOutputSource
{
    /// <summary>어느 축이든 이동/조그/원점복귀 중</summary>
    AnyMoving,
    /// <summary>어느 축이든 알람</summary>
    AnyFault,
    /// <summary>모든 축 서보 온</summary>
    AllServoOn,
    /// <summary>어느 축이든 서보 온</summary>
    AnyServoOn,
    /// <summary>인터록 하나 이상 활성</summary>
    AnyInterlock,
    /// <summary>모든 슬레이브 OP (통신 정상)</summary>
    BusOk,
    /// <summary>1 Hz 토글 (마스터 생존 표시)</summary>
    Heartbeat,
}

/// <summary>마스터 상태를 DO 비트로 내보내는 규칙 (설정 "Master:StatusOutputs"). 램프·부저·PLC 신호용.</summary>
public sealed class StatusOutputRule
{
    public string Module { get; set; } = "";
    public int Bit { get; set; }

    /// <summary>출력 비트 별칭 (예: "RunLamp"). 지정하면 Bit 대신 사용.</summary>
    public string? Output { get; set; }
    public StatusOutputSource Source { get; set; }
    public bool Invert { get; set; }
}

public sealed record InterlockStatus(
    string Name,
    bool Enabled,
    string Module,
    int Bit,
    string InputName,
    bool ActiveLow,
    string Action,
    int[] Axes,
    bool BlockMotion,
    bool Active,
    bool InputState,
    long TriggerCount,
    DateTime? LastTriggeredUtc,
    string? Message,
    string? Error,
    /// <summary>입력 모듈의 EtherCAT 슬레이브 위치 (미해석 0). 시뮬레이션은 1000 이상.</summary>
    int Slave = 0,
    /// <summary>대상 축 이름 (Axes 와 같은 순서).</summary>
    string[]? AxisNames = null,
    /// <summary>규칙의 Axes 가 비어 "전체 축" 인지.</summary>
    bool AllAxes = false,
    /// <summary>표시 출력(램프) 모듈/슬레이브/비트/이름. 없으면 null / 0 / -1.</summary>
    string? OutputModule = null,
    int OutputSlave = 0,
    int OutputBit = -1,
    string? OutputName = null,
    // ---- 축 조건 확장 ----
    string Source = "Input",
    string Mode = "BlockWhileActive",
    int SourceAxisId = 0,
    string? SourceAxisName = null,
    int SourceAxisSlave = 0,
    double? Min = null,
    double? Max = null,
    string? Unit = null,
    double Hysteresis = 0,
    double? VelocityThreshold = null,
    /// <summary>모드 적용 전 원시 조건 (입력 활성 / 이동 중 / 범위 안·밖).</summary>
    bool Condition = false,
    /// <summary>현재값: 위치(사용자 단위) 또는 이동 중 1/0. 입력 조건은 null.</summary>
    double? SourceValue = null,
    /// <summary>사람이 읽는 조건: "DI1.IN0 (EStop) = 0", "축 X(1) 이동 중", "축 Z(3) 위치 1000~2000 um 안 (±20)"</summary>
    string? ConditionText = null,
    /// <summary>소스 위치: "DI1 (slave 13)", "축 Z(3) (slave 7)"</summary>
    string? SourceLabel = null);

/// <summary>규칙 + 바인딩된 모듈/축. <see cref="Evaluate"/> 는 실시간 스레드에서 매 사이클 호출된다 (할당 없음).</summary>
public sealed class Interlock
{
    private bool _prev;
    private long _triggerCount;
    private long _lastTriggeredTicks;

    public InterlockRule Rule { get; }
    public IoModule? Module { get; }
    public IoModule? OutputModule { get; }
    public Axis[] Targets { get; }
    public string? Error { get; }

    /// <summary>축 조건의 소스 축 (바인딩 실패면 null).</summary>
    public Axis? SourceAxis { get; }
    /// <summary>축 조건의 값 단위.</summary>
    public UserUnit Unit { get; }
    private readonly double _min, _max, _hys, _velThr;

    public bool Active { get; private set; }
    public bool InputState { get; private set; }
    /// <summary>모드 적용 전 원시 조건.</summary>
    public bool Condition { get; private set; }
    /// <summary>현재값 (위치 또는 1/0). 입력 조건은 NaN.</summary>
    public double SourceValue { get; private set; } = double.NaN;

    private volatile bool _enabled;

    /// <summary>런타임 사용 여부. false 로 바꾸면 다음 사이클부터 비활성(Active=false)이 되고 차단·표시 출력이 풀린다.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>해석된 입력 비트 (별칭 → 번호).</summary>
    public int InputBit { get; }

    /// <summary>해석된 출력 비트, 없으면 -1.</summary>
    public int OutputBitIndex { get; }

    public Interlock(InterlockRule rule, IoModule? module, int inputBit, IoModule? outputModule, int outputBit, Axis[] targets, string? error,
                     Axis? sourceAxis = null, UserUnit unit = UserUnit.Step)
    {
        Rule = rule;
        Module = module;
        InputBit = inputBit;
        OutputModule = outputModule;
        OutputBitIndex = outputBit;
        Targets = targets;
        Error = error;
        SourceAxis = sourceAxis;
        Unit = unit;
        _min = rule.Min ?? double.NegativeInfinity;
        _max = rule.Max ?? double.PositiveInfinity;
        _hys = Math.Max(0, rule.Hysteresis);
        _velThr = rule.VelocityThreshold is > 0 ? rule.VelocityThreshold.Value : 0;
        _enabled = rule.Enabled;
    }

    public string BlockReason => Rule.Message ?? Rule.Name;

    /// <summary>바인딩이 되어 평가 가능한가.</summary>
    public bool IsBound => Rule.Source == InterlockSource.Input
        ? Module is not null && InputBit >= 0 && InputBit < Module.InputBits
        : SourceAxis is not null;

    /// <summary>매 사이클. 활성 상승 에지에서 Action 을 대상 축에 적용. 반환값: 이번 사이클에 트리거되었는지.</summary>
    public bool Evaluate(long nowUtcTicks)
    {
        if (!IsBound)
        {
            Active = false;
            return false;
        }

        // 1) 원시 조건 + 소스 오프라인 여부
        bool condition;
        bool offline;
        switch (Rule.Source)
        {
            case InterlockSource.AxisMoving:
            {
                var ax = SourceAxis!;
                offline = !ax.Online;
                condition = ax.IsMotionActive
                            || (_velThr > 0 && ax.State == Cia402State.OperationEnabled
                                && Math.Abs(ax.Mechanics.StepsToUser(ax.Tx.VelocityActual, Unit)) >= _velThr);
                SourceValue = condition ? 1 : 0;
                break;
            }
            case InterlockSource.AxisInRange:
            case InterlockSource.AxisOutOfRange:
            {
                var ax = SourceAxis!;
                offline = !ax.Online;
                double pos = ax.Mechanics.StepsToUser(ax.Tx.PositionActual, Unit);   // 매 사이클 변환: 리드/기어 변경 자동 반영
                SourceValue = pos;
                // 차단 영역은 h 만큼 넓게, 허용 영역은 h 만큼 좁게 — 단, 직전에 차단 상태였을 때만(해제 시 여유)
                bool insideIsBlocking = (Rule.Source == InterlockSource.AxisInRange) ^ (Rule.Mode == InterlockMode.AllowOnlyWhileActive);
                double h = _prev ? _hys : 0;
                bool inside = insideIsBlocking
                    ? pos >= _min - h && pos <= _max + h
                    : pos >= _min + h && pos <= _max - h;
                condition = Rule.Source == InterlockSource.AxisInRange ? inside : !inside;
                break;
            }
            default:
            {
                bool input = Module!.GetInput(InputBit);
                InputState = input;
                offline = !Module.Online;
                condition = input ^ Rule.ActiveLow;
                break;
            }
        }
        Condition = condition;

        if (!_enabled)
        {
            // 비활성: 조건은 계속 보여 주되 동작·차단·표시 출력은 하지 않는다. 다시 켤 때 이미 활성 조건이면 상승 에지로 처리된다.
            Active = false;
            _prev = false;
            ReleaseOutput();
            return false;
        }

        // 2) 모드 적용. 소스(모듈/축)가 오프라인이면 안전 측(활성)으로 본다
        bool active = offline || (Rule.Mode == InterlockMode.AllowOnlyWhileActive ? !condition : condition);
        Active = active;

        bool triggered = active && !_prev;
        _prev = active;

        if (triggered)
        {
            _triggerCount++;
            _lastTriggeredTicks = nowUtcTicks;
            var cmd = Rule.EffectiveAction switch
            {
                InterlockAction.Halt => new AxisCommand(AxisCommandKind.Halt),
                InterlockAction.QuickStop => new AxisCommand(AxisCommandKind.QuickStop),
                InterlockAction.Disable => new AxisCommand(AxisCommandKind.Disable),
                _ => null,
            };
            if (cmd is not null)
                foreach (var a in Targets) a.Submit(cmd);
        }

        if (OutputModule is not null && OutputBitIndex >= 0 && OutputBitIndex < OutputModule.OutputBits)
            OutputModule.SetOutput(OutputBitIndex, active);

        return triggered;
    }

    /// <summary>표시 출력이 켜져 있으면 끈다 (해제·규칙 제거 시). RT/GUI 어느 스레드에서든 안전 (SetOutput 은 CAS).</summary>
    public void ReleaseOutput()
    {
        if (OutputModule is not null && OutputBitIndex >= 0 && OutputBitIndex < OutputModule.OutputBits && OutputModule.GetOutput(OutputBitIndex))
            OutputModule.SetOutput(OutputBitIndex, false);
    }

    /// <summary>규칙 교체 시 같은 이름의 이전 인스턴스에서 통계를 이월.</summary>
    public void CopyCountersFrom(Interlock other)
    {
        _triggerCount = Interlocked.Read(ref other._triggerCount);
        _lastTriggeredTicks = Interlocked.Read(ref other._lastTriggeredTicks);
    }

    /// <summary>
    /// 규칙 내용이 바뀌지 않은(같은 InterlockRule 인스턴스) 항목은 에지 상태도 이월해, 다른 규칙을 편집했을 때
    /// 이미 활성인 규칙이 상승 에지로 다시 발동(정지 명령 재전송)하지 않게 한다. 편집된 규칙은 이월하지 않아 한 번 재발동한다(의도).
    /// </summary>
    public void CopyEdgeStateFrom(Interlock other)
    {
        _prev = other._prev;
        Active = other.Active;
        Condition = other.Condition;
        InputState = other.InputState;
        SourceValue = other.SourceValue;
    }

    public string UnitName => Unit == UserUnit.Um ? "um" : "step";

    public InterlockStatus GetStatus() => new(
        Rule.Name, _enabled, Rule.Module, InputBit,
        Module is not null && InputBit >= 0 && InputBit < Module.InputBits ? Module.InputNames[InputBit] : (Rule.Input ?? Rule.Bit.ToString()),
        Rule.ActiveLow, Rule.EffectiveAction.ToString(),
        Targets.Select(a => a.Id).ToArray(), Rule.BlockMotion, Active, InputState,
        Interlocked.Read(ref _triggerCount),
        _lastTriggeredTicks == 0 ? null : new DateTime(Interlocked.Read(ref _lastTriggeredTicks), DateTimeKind.Utc),
        Rule.Message, Error,
        Slave: Module?.Slave ?? 0,
        AxisNames: Targets.Select(a => a.Name).ToArray(),
        AllAxes: Rule.Axes.Length == 0,
        OutputModule: OutputModule?.Name ?? Rule.OutputModule,
        OutputSlave: OutputModule?.Slave ?? 0,
        OutputBit: OutputBitIndex,
        OutputName: OutputModule is not null && OutputBitIndex >= 0 && OutputBitIndex < OutputModule.OutputBits ? OutputModule.OutputNames[OutputBitIndex] : Rule.Output,
        Source: Rule.Source.ToString(),
        Mode: Rule.Mode.ToString(),
        SourceAxisId: SourceAxis?.Id ?? 0,
        SourceAxisName: SourceAxis?.Name ?? Rule.SourceAxis,
        SourceAxisSlave: SourceAxis?.Slave ?? 0,
        Min: Rule.Min, Max: Rule.Max,
        Unit: Rule.IsAxisSource ? UnitName : null,
        Hysteresis: Rule.Hysteresis,
        VelocityThreshold: Rule.VelocityThreshold,
        Condition: Condition,
        SourceValue: Rule.IsAxisSource && !double.IsNaN(SourceValue) ? SourceValue : null,
        ConditionText: ConditionText,
        SourceLabel: SourceLabel);

    /// <summary>소스 위치: "DI1 (slave 13)" / "축 Z(3) (slave 7)" / 미해석.</summary>
    public string SourceLabel => Rule.Source == InterlockSource.Input
        ? (Module is null ? $"{Rule.Module} (미해석)" : $"{Module.Name} (slave {Module.Slave})")
        : (SourceAxis is null ? $"축 '{Rule.SourceAxis}' (미해석)" : $"축 {SourceAxis.Name}({SourceAxis.Id}) (slave {SourceAxis.Slave})");

    /// <summary>사람이 읽는 조건. 모드·안전측은 포함하지 않는다.</summary>
    public string ConditionText => InterlockText.Condition(Rule,
        Module is not null && InputBit >= 0 && InputBit < Module.InputBits ? Module.Name : null,
        Module is not null && InputBit >= 0 && InputBit < Module.InputBits ? Module.InputNames[InputBit] : null,
        InputBit, SourceAxis is null ? null : $"{SourceAxis.Name}({SourceAxis.Id})", UnitName);

    /// <summary>로그용: 조건 + 슬레이브 + 모드. 예: "DI1.IN0 (EStop) = 0 @slave 13", "축 Z(3) 위치 1000~2000 um 안 (±20) @slave 7 → 이때만 허용".</summary>
    public string SourceText
    {
        get
        {
            int slave = Rule.Source == InterlockSource.Input ? Module?.Slave ?? 0 : SourceAxis?.Slave ?? 0;
            string s = ConditionText + (slave > 0 ? $" @slave {slave}" : " (미해석)");
            if (Rule.Mode == InterlockMode.AllowOnlyWhileActive) s += " → 이때만 허용";
            return s;
        }
    }
}

/// <summary>인터록 문구 생성 (Core 로그·상태·GUI 가 같은 표현을 쓰도록 한곳에).</summary>
public static class InterlockText
{
    public static string SourceName(InterlockSource s) => s switch
    {
        InterlockSource.Input => "디지털 입력",
        InterlockSource.AxisMoving => "축 이동 중",
        InterlockSource.AxisInRange => "축 위치 범위 안",
        InterlockSource.AxisOutOfRange => "축 위치 범위 밖",
        _ => s.ToString(),
    };

    public static string ModeName(InterlockMode m) => m == InterlockMode.AllowOnlyWhileActive ? "활성 시만 허용" : "활성 시 차단";

    public static string ActionName(InterlockAction a) => a switch
    {
        InterlockAction.None => "None (차단만)",
        InterlockAction.Halt => "Halt (감속 정지)",
        InterlockAction.QuickStop => "QuickStop (급정지·서보 오프)",
        InterlockAction.Disable => "Disable (서보 오프)",
        _ => a.ToString(),
    };

    /// <summary>
    /// 조건 문구. 바인딩 정보(모듈 이름·비트 별칭·축 라벨)가 없으면 규칙의 원문으로 만든다 (편집기 미리보기용).
    /// </summary>
    public static string Condition(InterlockRule r, string? moduleName, string? inputAlias, int inputBit, string? axisLabel, string unitName)
    {
        switch (r.Source)
        {
            case InterlockSource.AxisMoving:
            {
                string s = $"축 {axisLabel ?? $"'{r.SourceAxis}'"} 이동 중";
                if (r.VelocityThreshold is > 0) s += $" (|v| ≥ {r.VelocityThreshold.Value:0.###} {unitName}/s)";
                return s;
            }
            case InterlockSource.AxisInRange:
            case InterlockSource.AxisOutOfRange:
            {
                string range = $"{Fmt(r.Min)}~{Fmt(r.Max)} {unitName}";
                string s = $"축 {axisLabel ?? $"'{r.SourceAxis}'"} 위치 {range} {(r.Source == InterlockSource.AxisInRange ? "안" : "밖")}";
                if (r.Hysteresis > 0) s += $" (±{r.Hysteresis:0.###})";
                return s;
            }
            default:
            {
                string mod = moduleName ?? r.Module;
                string bit = inputAlias is not null ? $"IN{inputBit} ({inputAlias})" : (r.Input ?? $"IN{r.Bit}");
                return $"{mod}.{bit} = {(r.ActiveLow ? 0 : 1)}";
            }
        }
    }

    /// <summary>바인딩 없이 규칙만으로 만든 조건 문구 (편집기 미리보기).</summary>
    public static string Preview(InterlockRule r, string unitName = "um") => Condition(r, null, null, r.Bit, null, r.Unit ?? unitName);

    private static string Fmt(double? v) => v is null ? "?" : v.Value.ToString("0.###");
}
