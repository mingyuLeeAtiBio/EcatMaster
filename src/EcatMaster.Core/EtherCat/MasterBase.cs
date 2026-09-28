using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EcatMaster.Core.EtherCat;

/// <summary>SOEM/시뮬레이션 마스터의 공통부: 축·IO 모듈 목록, 인터록·상태출력 평가, 전체 명령.</summary>
public abstract class MasterBase : IEtherCatMaster
{
    protected readonly MasterOptions Options;
    protected readonly List<Axis> AxisList = new();
    protected readonly List<IoModule> IoModuleList = new();
    /// <summary>인터록 배열. RT 스레드는 매 사이클 Volatile.Read 로 한 번 읽어 쓰고, 교체는 새 배열을 만들어 Volatile.Write (락 없음, 할당 없음).</summary>
    private Interlock[] _interlocks = [];
    private readonly object _interlockEdit = new();
    private ILogger? _interlockLog;
    private readonly List<(StatusOutputRule Rule, IoModule Module)> _statusOutputs = new();

    /// <summary>실시간 스레드가 넣고 감시 스레드/GUI 가 꺼내 로그로 남기는 이벤트 (인터록 트리거 등).</summary>
    protected readonly ConcurrentQueue<string> Events = new();

    private int _activeInterlocks;
    private bool _heartbeat;
    private long _heartbeatToggleTicks;

    // ---- 사이클 생존/실측 주기 ----
    private long _lastCycleTicks;
    private long _windowStartTicks;
    private long _windowCycles;
    private long _cyclesPerSecondBits;   // double 을 Interlocked 로 다루기 위한 비트 표현

    /// <summary>실시간 스레드가 매 사이클 호출. 최근 1초 구간의 사이클 수를 계산한다.</summary>
    protected void RtMarkCycle(long nowUtcTicks)
    {
        Volatile.Write(ref _lastCycleTicks, nowUtcTicks);
        if (_windowStartTicks == 0) _windowStartTicks = nowUtcTicks;
        _windowCycles++;
        long elapsed = nowUtcTicks - _windowStartTicks;
        if (elapsed >= TimeSpan.TicksPerSecond)
        {
            double cps = _windowCycles * (double)TimeSpan.TicksPerSecond / elapsed;
            Interlocked.Exchange(ref _cyclesPerSecondBits, BitConverter.DoubleToInt64Bits(cps));
            _windowStartTicks = nowUtcTicks;
            _windowCycles = 0;
        }
    }

    /// <summary>마지막 사이클이 5주기(최소 200 ms) 안이면 살아 있다고 본다.</summary>
    protected bool CycleAlive
    {
        get
        {
            long last = Volatile.Read(ref _lastCycleTicks);
            if (last == 0) return false;
            long limit = Math.Max(TimeSpan.TicksPerMillisecond * 200, Options.CycleTimeUs * 10L * 5);
            return DateTime.UtcNow.Ticks - last < limit;
        }
    }

    protected double CyclesPerSecond => CycleAlive ? BitConverter.Int64BitsToDouble(Interlocked.Read(ref _cyclesPerSecondBits)) : 0.0;
    protected double ActualCycleTimeUs => CyclesPerSecond > 0 ? 1_000_000.0 / CyclesPerSecond : 0.0;

    /// <summary>연결 해제 시 생존 표시를 즉시 끈다.</summary>
    protected void ResetCycleTracking()
    {
        Volatile.Write(ref _lastCycleTicks, 0);
        _windowStartTicks = 0;
        _windowCycles = 0;
        Interlocked.Exchange(ref _cyclesPerSecondBits, 0);
    }

    protected MasterBase(IOptions<MasterOptions> options)
    {
        Options = options.Value;
    }

    public IReadOnlyList<Axis> Axes => AxisList;
    public IReadOnlyList<IoModule> IoModules => IoModuleList;

    public Axis? GetAxis(int id) => AxisList.FirstOrDefault(a => a.Id == id);

    public Axis? GetAxis(string idOrAlias)
    {
        if (string.IsNullOrWhiteSpace(idOrAlias)) return null;
        var key = idOrAlias.Trim();
        var byName = AxisList.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        return int.TryParse(key, out int id) ? GetAxis(id) : null;
    }

    public IoModule? GetIoModule(string nameOrId)
    {
        var byName = IoModuleList.FirstOrDefault(m => string.Equals(m.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        return int.TryParse(nameOrId, out int id) ? IoModuleList.FirstOrDefault(m => m.Id == id) : null;
    }

    public IReadOnlyList<InterlockStatus> GetInterlocks() => Volatile.Read(ref _interlocks).Select(i => i.GetStatus()).ToList();

    public IReadOnlyList<InterlockRule> InterlockRules => Options.Interlocks;

    public bool SetInterlockEnabled(string name, bool enabled)
    {
        var il = Volatile.Read(ref _interlocks).FirstOrDefault(i => string.Equals(i.Rule.Name, name, StringComparison.OrdinalIgnoreCase));
        if (il is null) return false;
        il.Enabled = enabled;
        il.Rule.Enabled = enabled;   // 설정 객체에도 반영 (GUI 가 devices.json 에 저장할 때 사용)
        Events.Enqueue(enabled ? $"인터록 '{name}' 사용 재개" : $"인터록 '{name}' 해제됨 — 해당 조건에서 축이 정지·차단되지 않습니다");
        return true;
    }

    /// <summary>규칙 하나를 검증만 한다 (편집기 실시간 검증). 문제 없으면 null. replacingName = 편집 중인 기존 규칙 이름(중복 검사에서 제외).</summary>
    public string? ValidateInterlock(InterlockRule rule, string? replacingName = null)
    {
        if (string.IsNullOrWhiteSpace(rule.Name)) return "이름이 비어 있음";
        bool dup = Options.Interlocks.Any(r => !ReferenceEquals(r, rule)
                                             && string.Equals(r.Name, rule.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                                             && !string.Equals(r.Name, replacingName, StringComparison.OrdinalIgnoreCase));
        if (dup) return $"이름 '{rule.Name}' 이 이미 있음";
        return Bind(rule).Error;
    }

    /// <summary>
    /// 규칙 목록 전체를 교체한다 (GUI 스레드). 같은 이름의 규칙은 트리거 통계를 이월하고, 빠진 규칙의 표시 출력은 끈다.
    /// Options.Interlocks 는 같은 List 인스턴스를 유지한 채 내용만 바꾸므로 devices.json 모델(같은 리스트)도 함께 갱신된다.
    /// </summary>
    public IReadOnlyList<InterlockStatus> ReplaceInterlocks(IReadOnlyList<InterlockRule> rules)
    {
        lock (_interlockEdit)
        {
            var copy = rules.ToList();                 // 호출자가 Options.Interlocks 자체를 넘겨도 안전
            var old = Volatile.Read(ref _interlocks);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fresh = new Interlock[copy.Count];
            for (int i = 0; i < copy.Count; i++)
            {
                var rule = copy[i];
                var il = Bind(rule, seen);
                var prev = old.FirstOrDefault(o => string.Equals(o.Rule.Name, rule.Name, StringComparison.OrdinalIgnoreCase));
                if (prev is not null)
                {
                    il.CopyCountersFrom(prev);
                    // 규칙 객체가 그대로면(다른 규칙만 편집) 에지 상태도 이월 → 재발동 없음. 바뀐 규칙은 다음 사이클에 다시 평가·발동.
                    if (ReferenceEquals(prev.Rule, rule) && prev.Error is null && il.Error is null) il.CopyEdgeStateFrom(prev);
                }
                fresh[i] = il;
            }
            foreach (var o in old)
                if (!fresh.Any(n => string.Equals(n.Rule.Name, o.Rule.Name, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(n.OutputModule, o.OutputModule) && n.OutputBitIndex == o.OutputBitIndex))
                    o.ReleaseOutput();

            Options.Interlocks.Clear();
            Options.Interlocks.AddRange(copy);
            Volatile.Write(ref _interlocks, fresh);

            var log = _interlockLog;
            if (log is not null) foreach (var il in fresh) LogInterlock(log, il);
            Events.Enqueue($"인터록 규칙 교체: {fresh.Length}개 (오류 {fresh.Count(i => i.Error is not null)}개)");
            return fresh.Select(i => i.GetStatus()).ToList();
        }
    }

    public IReadOnlyList<InterlockStatus> AddInterlock(InterlockRule rule)
    {
        var list = Options.Interlocks.ToList();
        list.Add(rule);
        return ReplaceInterlocks(list);
    }

    public IReadOnlyList<InterlockStatus> UpdateInterlock(string name, InterlockRule rule)
    {
        var list = Options.Interlocks.ToList();
        int idx = list.FindIndex(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) list.Add(rule); else list[idx] = rule;    // 자리(순서) 유지
        return ReplaceInterlocks(list);
    }

    public IReadOnlyList<InterlockStatus> RemoveInterlock(string name)
    {
        var list = Options.Interlocks.Where(r => !string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return ReplaceInterlocks(list);
    }

    protected void ClearInterlocks() => Volatile.Write(ref _interlocks, []);

    public virtual bool SupportsInputForcing => false;

    protected int ActiveInterlockCount => Volatile.Read(ref _activeInterlocks);

    public abstract MasterStatus GetStatus();
    public abstract Task StartAsync(CancellationToken cancellationToken);

    /// <summary>기본 구현: StartAsync 를 감싸 예외를 문자열로 바꾼다. SoemMaster 는 소켓 정리를 위해 재정의.</summary>
    public virtual async Task<string?> TryStartAsync(CancellationToken cancellationToken)
    {
        try { await StartAsync(cancellationToken); return null; }
        catch (Exception ex) { return ex.Message; }
    }
    public abstract Task StopAsync(CancellationToken cancellationToken);
    public abstract void ResetStatistics();
    public abstract SdoResult SdoRead(int slave, ushort index, byte subIndex, int size);
    public abstract SdoResult SdoWrite(int slave, ushort index, byte subIndex, int size, uint value);
    public abstract DriveSettingsResult ReadDriveSettings(int axisId);
    public abstract DriveSettingsResult WriteDriveSettings(int axisId, DriveSettingsWrite request);

    public DriveSettings? GetDriveSettings(int axisId) => GetAxis(axisId)?.GetDriveSettings();

    // ------------------------------------------------------------------ 전체 명령

    public void StopAll(bool quick)
    {
        var cmd = new AxisCommand(quick ? AxisCommandKind.QuickStop : AxisCommandKind.Halt);
        foreach (var a in AxisList) a.Submit(cmd);
    }

    public void EnableAll()
    {
        var cmd = new AxisCommand(AxisCommandKind.Enable);
        foreach (var a in AxisList) a.Submit(cmd);
    }

    public void DisableAll()
    {
        var cmd = new AxisCommand(AxisCommandKind.Disable);
        foreach (var a in AxisList) a.Submit(cmd);
    }

    public void FaultResetAll()
    {
        var cmd = new AxisCommand(AxisCommandKind.FaultReset);
        foreach (var a in AxisList) a.Submit(cmd);
    }

    // ------------------------------------------------------------------ 구성

    protected string AxisName(int index) => Options.AxisNameFor(index);

    /// <summary>축 생성 직후: 축별 리드·엔코더 분해능(Axes[]) 적용.</summary>
    protected void ApplyAxisOptions(Axis axis)
    {
        var ax = Options.Axes.FirstOrDefault(a => a.Id == axis.Id);
        if (ax?.LeadUm is > 0) axis.Mechanics.LeadUm = ax.LeadUm.Value;
        if (axis.Profile.NeedsPulsesPerRev)
            axis.Mechanics.BaseStepsPerRev = ax?.PulsesPerRev is > 0 ? ax.PulsesPerRev.Value : 0;   // 0 → 프로파일이 기본값·경고 처리
        else
            axis.Mechanics.BaseStepsPerRev = DriveMechanics.AzdBaseStepsPerRev;
    }

    /// <summary>다음 축 번호에 쓸 프로파일: Axes[].Profile 강제 지정 → 벤더/제품 코드 자동.</summary>
    protected IDriveProfile? ResolveProfile(int axisId, uint vendorId, uint productCode)
    {
        var forced = DriveProfileRegistry.FindByName(Options.Axes.FirstOrDefault(a => a.Id == axisId)?.Profile);
        return forced ?? DriveProfileRegistry.Find(vendorId, productCode);
    }

    /// <summary>축 별칭 중복·숫자 별칭을 경고한다 (API 에서 번호와 혼동).</summary>
    protected void ValidateAliases(ILogger log)
    {
        foreach (var g in AxisList.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            log.LogWarning("축 별칭 '{Name}' 중복: 축 {Ids}. 별칭으로 조회하면 첫 번째만 선택됩니다", g.Key, string.Join(",", g.Select(a => a.Id)));
        foreach (var a in AxisList.Where(a => int.TryParse(a.Name, out _)))
            log.LogWarning("축 {Id} 별칭 '{Name}' 이 숫자입니다. 번호와 혼동되므로 다른 별칭을 권장", a.Id, a.Name);
        foreach (var g in IoModuleList.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            log.LogWarning("IO 모듈 별칭 '{Name}' 중복: 모듈 {Ids}", g.Key, string.Join(",", g.Select(m => m.Id)));
    }

    /// <summary>축·IO 모듈이 모두 만들어진 뒤 호출. 규칙을 모듈/축에 바인딩하고 문제를 로그로 남긴다. 비트는 번호 또는 별칭.</summary>
    protected void BuildInterlocks(ILogger log)
    {
        _interlockLog = log;
        _statusOutputs.Clear();
        ValidateAliases(log);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var arr = Options.Interlocks.Select(rule => Bind(rule, seen)).ToArray();
        Volatile.Write(ref _interlocks, arr);
        foreach (var il in arr) LogInterlock(log, il);

        foreach (var rule in Options.StatusOutputs)
        {
            var module = GetIoModule(rule.Module);
            if (module is null || !module.TryResolveOutput(rule.Output ?? rule.Bit.ToString(), out int bit))
            {
                log.LogError("상태 출력 {Src} → {Module}.{Bit}: 모듈 없음 또는 비트/별칭 없음", rule.Source, rule.Module, rule.Output ?? rule.Bit.ToString());
                continue;
            }
            rule.Bit = bit;   // 이후 RT 평가는 번호로
            _statusOutputs.Add((rule, module));
            log.LogInformation("상태 출력 {Src} → {Module}.{Bit}({Alias}){Inv}", rule.Source, rule.Module, bit, module.OutputNames[bit], rule.Invert ? " (반전)" : "");
        }
    }

    /// <summary>
    /// 규칙 하나를 현재 축·IO 모듈에 바인딩한다. 문제가 있으면 Error 를 채우고 모듈/소스 축 참조를 비워 평가되지 않게 한다 (목록에는 남아 GUI 에 표시).
    /// seen = 이름 중복 검사용 (null 이면 검사 안 함).
    /// </summary>
    private Interlock Bind(InterlockRule rule, HashSet<string>? seen = null)
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(rule.Name)) error = "이름이 비어 있음";
        else if (seen is not null && !seen.Add(rule.Name.Trim())) error = $"이름 '{rule.Name}' 중복";

        // ---- 조건 소스 ----
        IoModule? module = null;
        int inputBit = -1;
        Axis? sourceAxis = null;
        UserUnit unit = UserUnit.Step;
        if (rule.Source == InterlockSource.Input)
        {
            module = GetIoModule(rule.Module);
            if (module is null)
                error ??= $"입력 모듈 '{rule.Module}' 없음";
            else if (!module.TryResolveInput(rule.Input ?? rule.Bit.ToString(), out inputBit))
                error ??= $"입력 '{rule.Input ?? rule.Bit.ToString()}' 을 {module.Name} 에서 찾을 수 없음 (0..{module.InputBits - 1} 또는 별칭)";
        }
        else
        {
            sourceAxis = GetAxis(rule.SourceAxis ?? "");
            if (sourceAxis is null)
                error ??= $"소스 축 '{rule.SourceAxis}' 없음";
            else
            {
                unit = sourceAxis.Mechanics.Unit;
                if (!string.IsNullOrWhiteSpace(rule.Unit) && !DriveMechanics.TryParseUnit(rule.Unit, out unit))
                    error ??= $"단위 '{rule.Unit}' 을 모름 (um | step)";
            }
            if (rule.IsRangeSource)
            {
                if (rule.Min is null || rule.Max is null) error ??= "범위 Min/Max 가 필요함";
                else if (!(rule.Min < rule.Max)) error ??= $"Min({rule.Min}) 은 Max({rule.Max}) 보다 작아야 함";
                else if (rule.Hysteresis < 0) error ??= "히스테리시스는 0 이상";
                else if (2 * rule.Hysteresis >= rule.Max - rule.Min) error ??= $"히스테리시스 {rule.Hysteresis} 가 범위 폭 {rule.Max - rule.Min} 에 비해 너무 큼 (2h < 폭)";
            }
            else if (rule.VelocityThreshold is < 0) error ??= "속도 문턱은 0 이상";
        }

        // ---- 표시 출력 ----
        IoModule? outModule = null;
        int outputBit = -1;
        if (!string.IsNullOrEmpty(rule.OutputModule))
        {
            outModule = GetIoModule(rule.OutputModule);
            if (outModule is null) error ??= $"출력 모듈 '{rule.OutputModule}' 없음";
            else if (!outModule.TryResolveOutput(rule.Output ?? rule.OutputBit?.ToString() ?? "", out outputBit))
                error ??= $"출력 '{rule.Output ?? rule.OutputBit?.ToString()}' 을 {outModule.Name} 에서 찾을 수 없음";
        }

        // ---- 대상 축 ----
        var targets = rule.Axes.Length == 0
            ? AxisList.ToArray()
            : AxisList.Where(a => rule.Axes.Contains(a.Id)).ToArray();
        if (rule.Axes.Length > 0 && targets.Length != rule.Axes.Length)
            error ??= $"대상 축 일부 없음 ({string.Join(",", rule.Axes)})";
        if (sourceAxis is not null && targets.Any(t => ReferenceEquals(t, sourceAxis)))
            error ??= rule.Axes.Length == 0
                ? $"소스 축 {sourceAxis.Name}({sourceAxis.Id}) 이 대상(전체 축)에 포함됨 — 대상 축을 명시하세요 (자기 참조)"
                : $"소스 축 {sourceAxis.Name}({sourceAxis.Id}) 이 대상 축에 포함됨 (자기 참조)";

        bool ok = error is null;
        return new Interlock(rule, ok ? module : null, inputBit, ok ? outModule : null, outputBit, targets, error, ok ? sourceAxis : null, unit);
    }

    private static void LogInterlock(ILogger log, Interlock il)
    {
        var rule = il.Rule;
        if (il.Error is not null)
        {
            log.LogError("인터록 '{Name}' 비활성: {Err}", rule.Name, il.Error);
            return;
        }
        string lamp = il.OutputModule is not null && il.OutputBitIndex >= 0
            ? $", 표시 → {il.OutputModule.Name}.OUT{il.OutputBitIndex}({il.OutputModule.OutputNames[il.OutputBitIndex]}) @slave {il.OutputModule.Slave}" : "";
        string allowNote = rule.Mode == InterlockMode.AllowOnlyWhileActive ? " (허용 조건 미충족이면 차단 — 시작 시 차단 상태일 수 있음)" : "";
        log.LogInformation("인터록 '{Name}'{Dis}: [{Cond}] → {Action} [{Targets}]{Block}{Lamp}{Allow}", rule.Name,
            rule.Enabled ? "" : " (해제 상태)", il.SourceText, rule.EffectiveAction,
            rule.Axes.Length == 0 ? $"전체 {il.Targets.Length}축" : string.Join(",", il.Targets.Select(t => t.Name)),
            rule.BlockMotion ? " + 동작 차단" : "", lamp, allowNote);
    }

    // ------------------------------------------------------------------ 실시간 사이클 공통부

    /// <summary>
    /// 실시간 스레드에서 입력 갱신 직후, 축 Tick 직전에 호출. 할당 없음(이벤트 문자열 제외).
    /// 1) 인터록 평가·트리거, 2) 축별 동작 차단 플래그, 3) 상태 출력 비트.
    /// 주의: 축 Tick 전이므로 IsMotionActive/State 는 직전 사이클 값, 위치(Tx)는 이번 사이클 값.
    /// </summary>
    protected void RtEvaluate(long nowUtcTicks, bool busOk)
    {
        var interlocks = Volatile.Read(ref _interlocks);
        int active = 0;
        for (int i = 0; i < interlocks.Length; i++)
        {
            var il = interlocks[i];
            if (il.Evaluate(nowUtcTicks))
                Events.Enqueue($"인터록 '{il.Rule.Name}' 트리거 [{il.SourceText}] → {il.Rule.EffectiveAction}" + (il.Rule.Message is null ? "" : $": {il.Rule.Message}"));
            if (il.Active) active++;
        }
        Volatile.Write(ref _activeInterlocks, active);

        for (int a = 0; a < AxisList.Count; a++)
        {
            var axis = AxisList[a];
            bool blocked = false;
            string? reason = null;
            for (int i = 0; i < interlocks.Length && !blocked; i++)
            {
                var il = interlocks[i];
                if (!il.Active || !il.Rule.BlockMotion) continue;
                for (int t = 0; t < il.Targets.Length; t++)
                {
                    if (ReferenceEquals(il.Targets[t], axis)) { blocked = true; reason = il.BlockReason; break; }
                }
            }
            if (blocked != axis.MotionBlocked) axis.SetMotionBlocked(blocked, reason);
        }

        if (_statusOutputs.Count == 0) return;

        bool anyMoving = false, anyFault = false, allServo = AxisList.Count > 0, anyServo = false;
        for (int a = 0; a < AxisList.Count; a++)
        {
            var axis = AxisList[a];
            bool enabled = axis.State == Cia402State.OperationEnabled;
            anyMoving |= axis.IsMotionActive;
            anyFault |= axis.State is Cia402State.Fault or Cia402State.FaultReactionActive;
            allServo &= enabled;
            anyServo |= enabled;
        }
        if (nowUtcTicks - _heartbeatToggleTicks >= TimeSpan.TicksPerMillisecond * 500)
        {
            _heartbeat = !_heartbeat;
            _heartbeatToggleTicks = nowUtcTicks;
        }

        for (int i = 0; i < _statusOutputs.Count; i++)
        {
            var (rule, module) = _statusOutputs[i];
            bool v = rule.Source switch
            {
                StatusOutputSource.AnyMoving => anyMoving,
                StatusOutputSource.AnyFault => anyFault,
                StatusOutputSource.AllServoOn => allServo,
                StatusOutputSource.AnyServoOn => anyServo,
                StatusOutputSource.AnyInterlock => active > 0,
                StatusOutputSource.BusOk => busOk,
                StatusOutputSource.Heartbeat => _heartbeat,
                _ => false,
            };
            if (rule.Invert) v = !v;
            if (module.GetOutput(rule.Bit) != v) module.SetOutput(rule.Bit, v);
        }
    }

    /// <summary>실시간 스레드가 쌓은 이벤트를 로그로 내보낸다 (감시 스레드 등 비실시간 문맥에서 호출).</summary>
    protected void DrainEvents(ILogger log)
    {
        while (Events.TryDequeue(out var e)) log.LogWarning("{Event}", e);
    }

    /// <summary>드라이브 I/O 없이 적용 가능한 항목(리드, 단위, 우리 기본값)을 반영한다. 파생 클래스가 SDO 처리 후 호출.</summary>
    protected static void ApplyLocalSettings(Axis axis, DriveSettingsWrite req, List<string> messages)
    {
        if (req.LeadUm is > 0) { axis.Mechanics.LeadUm = req.LeadUm.Value; messages.Add($"리드 = {req.LeadUm.Value} um/rev (소프트웨어 설정)"); }
        if (req.PulsesPerRev is > 0)
        {
            if (axis.Profile.NeedsPulsesPerRev) { axis.Mechanics.BaseStepsPerRev = req.PulsesPerRev.Value; messages.Add($"엔코더 분해능 = {req.PulsesPerRev.Value:N0} pulse/rev (소프트웨어 설정)"); }
            else messages.Add($"{axis.Profile.Name} 은 분해능 기준이 고정({DriveMechanics.AzdBaseStepsPerRev:N0})이라 PulsesPerRev 를 무시");
        }
        if (req.Unit is not null)
        {
            if (DriveMechanics.TryParseUnit(req.Unit, out var u)) { axis.Mechanics.Unit = u; messages.Add($"단위 = {axis.Mechanics.UnitName}"); }
            else messages.Add($"알 수 없는 단위 '{req.Unit}' (step | um)");
        }
        if (req.ProfileVelocity is > 0) axis.Defaults.Velocity = req.ProfileVelocity.Value;
        if (req.ProfileAcceleration is > 0) axis.Defaults.Acceleration = req.ProfileAcceleration.Value;
        if (req.ProfileDeceleration is > 0) axis.Defaults.Deceleration = req.ProfileDeceleration.Value;
    }

    protected Axis RequireAxis(int axisId) =>
        GetAxis(axisId) ?? throw new KeyNotFoundException($"axis {axisId} not found");
}
