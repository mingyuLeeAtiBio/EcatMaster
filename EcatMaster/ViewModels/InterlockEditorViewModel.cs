using System.Collections.ObjectModel;
using EcatMaster.Core.EtherCat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EcatMaster.Infrastructure;

namespace EcatMaster.ViewModels;

/// <summary>콤보 항목 (값 + 표시 문구).</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>대상 축 체크리스트 항목.</summary>
public sealed class AxisCheckItem : ObservableObject
{
    private bool _isChecked;
    public int Id { get; }
    public string Label { get; }
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
    public AxisCheckItem(int id, string label, bool isChecked) { Id = id; Label = label; _isChecked = isChecked; }
}

/// <summary>
/// 인터록 규칙 편집기(추가/편집) 뷰모델. 연결된 마스터의 IO 모듈·축 목록으로 선택지를 만들고,
/// 값이 바뀔 때마다 <see cref="IEtherCatMaster.ValidateInterlock"/> 로 검증해 저장 가능 여부를 정한다.
/// </summary>
public sealed class InterlockEditorViewModel : ObservableObject
{
    private readonly IEtherCatMaster _master;
    private readonly string? _originalName;
    private bool _actionTouched;

    public bool IsNew => _originalName is null;
    public string Title => IsNew ? "인터록 추가" : $"인터록 편집 · {_originalName}";

    public InterlockEditorViewModel(IEtherCatMaster master, InterlockRule? existing)
    {
        _master = master;
        _originalName = existing?.Name;
        var r = existing?.Clone() ?? new InterlockRule { Name = "", Source = InterlockSource.Input };

        Sources = Enum.GetValues<InterlockSource>().Select(s => new Choice<InterlockSource>(s, InterlockText.SourceName(s))).ToList();
        Modes = Enum.GetValues<InterlockMode>().Select(m => new Choice<InterlockMode>(m, InterlockText.ModeName(m))).ToList();
        Actions = Enum.GetValues<InterlockAction>().Select(a => new Choice<InterlockAction>(a, InterlockText.ActionName(a))).ToList();
        Units = [new Choice<string>("um", "um (µm)"), new Choice<string>("step", "step (드라이브 지령 단위)")];

        InputModules = master.IoModules.Where(m => m.InputBits > 0).Select(m => new Choice<IoModule>(m, $"{m.Name} (slave {m.Slave}, IN {m.InputBits})")).ToList();
        OutputModules = [new Choice<IoModule?>(null, "(없음)")];
        OutputModules.AddRange(master.IoModules.Where(m => m.OutputBits > 0).Select(m => new Choice<IoModule?>(m, $"{m.Name} (slave {m.Slave}, OUT {m.OutputBits})")));
        SourceAxes = master.Axes.Select(a => new Choice<Axis>(a, $"{a.Name}({a.Id}) · slave {a.Slave} · {a.Mechanics.UnitName}")).ToList();
        foreach (var a in master.Axes)
            TargetAxes.Add(new AxisCheckItem(a.Id, $"{a.Name}({a.Id})", r.Axes.Contains(a.Id)));
        foreach (var t in TargetAxes) t.PropertyChanged += (_, _) => Revalidate();

        // ---- 기존 값 채우기 ----
        _name = r.Name;
        _selectedSource = Sources.First(s => s.Value == r.Source);
        _selectedInputModule = InputModules.FirstOrDefault(m => string.Equals(m.Value.Name, r.Module, StringComparison.OrdinalIgnoreCase)) ?? InputModules.FirstOrDefault();
        RebuildInputBits();
        if (_selectedInputModule is not null && _selectedInputModule.Value.TryResolveInput(r.Input ?? r.Bit.ToString(), out int ib))
            _selectedInputBit = InputBits.FirstOrDefault(b => b.Value == ib);
        _selectedInputBit ??= InputBits.FirstOrDefault();
        _activeLow = r.ActiveLow;

        _selectedSourceAxis = SourceAxes.FirstOrDefault(a => string.Equals(a.Value.Name, r.SourceAxis, StringComparison.OrdinalIgnoreCase) || a.Value.Id.ToString() == r.SourceAxis)
                              ?? SourceAxes.FirstOrDefault();
        _min = r.Min; _max = r.Max;
        _selectedUnit = Units.FirstOrDefault(u => string.Equals(u.Value, r.Unit, StringComparison.OrdinalIgnoreCase))
                        ?? Units.First(u => u.Value == (_selectedSourceAxis?.Value.Mechanics.UnitName ?? "um"));
        _hysteresis = r.Hysteresis;
        _velocityThreshold = r.VelocityThreshold;
        _selectedMode = Modes.First(m => m.Value == r.Mode);
        _selectedAction = Actions.First(a => a.Value == r.EffectiveAction);
        _actionTouched = existing is not null;
        _allAxes = r.Axes.Length == 0;
        _blockMotion = r.BlockMotion;
        _selectedOutputModule = OutputModules.FirstOrDefault(m => m.Value is not null && string.Equals(m.Value.Name, r.OutputModule, StringComparison.OrdinalIgnoreCase)) ?? OutputModules[0];
        RebuildOutputBits();
        if (_selectedOutputModule.Value is not null && _selectedOutputModule.Value.TryResolveOutput(r.Output ?? r.OutputBit?.ToString() ?? "", out int ob))
            _selectedOutputBit = OutputBits.FirstOrDefault(b => b.Value == ob);
        _selectedOutputBit ??= OutputBits.FirstOrDefault();
        _message = r.Message ?? "";
        _enabled = r.Enabled;

        Revalidate();
    }

    // ---- 선택지 ----
    public List<Choice<InterlockSource>> Sources { get; }
    public List<Choice<InterlockMode>> Modes { get; }
    public List<Choice<InterlockAction>> Actions { get; }
    public List<Choice<string>> Units { get; }
    public List<Choice<IoModule>> InputModules { get; }
    public List<Choice<IoModule?>> OutputModules { get; }
    public List<Choice<Axis>> SourceAxes { get; }
    public ObservableCollection<Choice<int>> InputBits { get; } = new();
    public ObservableCollection<Choice<int>> OutputBits { get; } = new();
    public ObservableCollection<AxisCheckItem> TargetAxes { get; } = new();

    // ---- 값 ----
    private string _name;
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) Revalidate(); } }

    private Choice<InterlockSource> _selectedSource;
    public Choice<InterlockSource> SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            // 축 조건으로 바꾸면 기본 동작을 Halt 로 (사용자가 동작을 직접 고르기 전까지)
            if (!_actionTouched) _selectedAction = Actions.First(a => a.Value == (value.Value == InterlockSource.Input ? InterlockAction.QuickStop : InterlockAction.Halt));
            OnPropertyChanged(nameof(SelectedAction));
            OnPropertyChanged(nameof(IsInputSource)); OnPropertyChanged(nameof(IsAxisSource));
            OnPropertyChanged(nameof(IsRangeSource)); OnPropertyChanged(nameof(IsMovingSource));
            Revalidate();
        }
    }
    public bool IsInputSource => _selectedSource.Value == InterlockSource.Input;
    public bool IsAxisSource => !IsInputSource;
    public bool IsRangeSource => _selectedSource.Value is InterlockSource.AxisInRange or InterlockSource.AxisOutOfRange;
    public bool IsMovingSource => _selectedSource.Value == InterlockSource.AxisMoving;

    private Choice<IoModule>? _selectedInputModule;
    public Choice<IoModule>? SelectedInputModule
    {
        get => _selectedInputModule;
        set { if (SetProperty(ref _selectedInputModule, value)) { RebuildInputBits(); SelectedInputBit = InputBits.FirstOrDefault(); Revalidate(); } }
    }

    private Choice<int>? _selectedInputBit;
    public Choice<int>? SelectedInputBit { get => _selectedInputBit; set { if (SetProperty(ref _selectedInputBit, value)) Revalidate(); } }

    private bool _activeLow;
    public bool ActiveLow { get => _activeLow; set { if (SetProperty(ref _activeLow, value)) Revalidate(); } }

    private Choice<Axis>? _selectedSourceAxis;
    public Choice<Axis>? SelectedSourceAxis
    {
        get => _selectedSourceAxis;
        set
        {
            if (!SetProperty(ref _selectedSourceAxis, value)) return;
            if (value is not null) SelectedUnit = Units.First(u => u.Value == value.Value.Mechanics.UnitName);   // 축 단위를 기본으로
            Revalidate();
        }
    }

    private double? _min;
    public double? Min { get => _min; set { if (SetProperty(ref _min, value)) Revalidate(); } }
    private double? _max;
    public double? Max { get => _max; set { if (SetProperty(ref _max, value)) Revalidate(); } }

    private Choice<string> _selectedUnit;
    public Choice<string> SelectedUnit { get => _selectedUnit; set { if (SetProperty(ref _selectedUnit, value)) Revalidate(); } }

    private double _hysteresis;
    public double Hysteresis { get => _hysteresis; set { if (SetProperty(ref _hysteresis, value)) Revalidate(); } }

    private double? _velocityThreshold;
    public double? VelocityThreshold { get => _velocityThreshold; set { if (SetProperty(ref _velocityThreshold, value)) Revalidate(); } }

    private Choice<InterlockMode> _selectedMode;
    public Choice<InterlockMode> SelectedMode { get => _selectedMode; set { if (SetProperty(ref _selectedMode, value)) Revalidate(); } }

    private Choice<InterlockAction> _selectedAction;
    public Choice<InterlockAction> SelectedAction
    {
        get => _selectedAction;
        set { if (SetProperty(ref _selectedAction, value)) { _actionTouched = true; Revalidate(); } }
    }

    private bool _allAxes;
    public bool AllAxes { get => _allAxes; set { if (SetProperty(ref _allAxes, value)) Revalidate(); } }

    private bool _blockMotion;
    public bool BlockMotion { get => _blockMotion; set { if (SetProperty(ref _blockMotion, value)) Revalidate(); } }

    private Choice<IoModule?> _selectedOutputModule;
    public Choice<IoModule?> SelectedOutputModule
    {
        get => _selectedOutputModule;
        set { if (SetProperty(ref _selectedOutputModule, value)) { RebuildOutputBits(); SelectedOutputBit = OutputBits.FirstOrDefault(); OnPropertyChanged(nameof(HasOutput)); Revalidate(); } }
    }
    public bool HasOutput => _selectedOutputModule.Value is not null;

    private Choice<int>? _selectedOutputBit;
    public Choice<int>? SelectedOutputBit { get => _selectedOutputBit; set { if (SetProperty(ref _selectedOutputBit, value)) Revalidate(); } }

    private string _message;
    public string Message { get => _message; set { if (SetProperty(ref _message, value)) Revalidate(); } }

    private bool _enabled;
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

    // ---- 검증/미리보기 ----
    private string? _validation;
    public string? ValidationMessage { get => _validation; private set { if (SetProperty(ref _validation, value)) OnPropertyChanged(nameof(CanSave)); } }
    public bool CanSave => _validation is null;

    private string _warning = "";
    public string Warning { get => _warning; private set => SetProperty(ref _warning, value); }

    private string _preview = "";
    public string Preview { get => _preview; private set => SetProperty(ref _preview, value); }

    private void RebuildInputBits()
    {
        InputBits.Clear();
        var m = _selectedInputModule?.Value;
        if (m is null) return;
        for (int i = 0; i < m.InputBits; i++) InputBits.Add(new Choice<int>(i, $"IN{i} · {m.InputNames[i]}"));
    }

    private void RebuildOutputBits()
    {
        OutputBits.Clear();
        var m = _selectedOutputModule.Value;
        if (m is null) return;
        for (int i = 0; i < m.OutputBits; i++) OutputBits.Add(new Choice<int>(i, $"OUT{i} · {m.OutputNames[i]}"));
    }

    /// <summary>화면 값으로 규칙 객체를 만든다. 별칭이 있는 비트/축은 별칭으로 저장 (이름 바뀌면 규칙도 같이 고쳐야 함은 기존 관례).</summary>
    public InterlockRule BuildRule()
    {
        var r = new InterlockRule
        {
            Name = Name.Trim(),
            Source = SelectedSource.Value,
            Mode = SelectedMode.Value,
            Action = SelectedAction.Value,
            Axes = AllAxes ? [] : TargetAxes.Where(t => t.IsChecked).Select(t => t.Id).OrderBy(i => i).ToArray(),
            BlockMotion = BlockMotion,
            Message = string.IsNullOrWhiteSpace(Message) ? null : Message.Trim(),
            Enabled = Enabled,
        };
        if (r.Source == InterlockSource.Input)
        {
            var m = SelectedInputModule?.Value;
            r.Module = m?.Name ?? "";
            int bit = SelectedInputBit?.Value ?? 0;
            r.Bit = bit;
            string? alias = m is not null && bit < m.InputBits ? m.InputNames[bit] : null;
            r.Input = alias is not null && alias != $"IN{bit}" ? alias : null;
            r.ActiveLow = ActiveLow;
        }
        else
        {
            var a = SelectedSourceAxis?.Value;
            r.SourceAxis = a is null ? null : (string.IsNullOrWhiteSpace(a.Name) || a.Name == $"Axis{a.Id}" ? a.Id.ToString() : a.Name);
            r.Unit = SelectedUnit.Value;
            if (IsRangeSource) { r.Min = Min; r.Max = Max; r.Hysteresis = Hysteresis; }
            if (IsMovingSource && VelocityThreshold is > 0) r.VelocityThreshold = VelocityThreshold;
        }
        var om = SelectedOutputModule.Value;
        if (om is not null)
        {
            r.OutputModule = om.Name;
            int ob = SelectedOutputBit?.Value ?? 0;
            string? alias = ob < om.OutputBits ? om.OutputNames[ob] : null;
            if (alias is not null && alias != $"OUT{ob}") r.Output = alias; else r.OutputBit = ob;
        }
        return r;
    }

    private void Revalidate()
    {
        var rule = BuildRule();
        string? err = null;
        if (rule.Source == InterlockSource.Input && InputModules.Count == 0) err = "입력이 있는 IO 모듈이 없음";
        else if (rule.IsAxisSource && SourceAxes.Count == 0) err = "축이 없음";
        else if (!AllAxes && rule.Axes.Length == 0) err = "대상 축을 하나 이상 선택하거나 '전체 축'을 켜세요";
        err ??= _master.ValidateInterlock(rule, _originalName);
        ValidationMessage = err;

        var warn = new List<string>();
        if (rule.Mode == InterlockMode.AllowOnlyWhileActive && rule.EffectiveAction is InterlockAction.QuickStop or InterlockAction.Disable)
            warn.Add("허용 조건이 깨질 때마다 대상 축이 서보 오프됩니다 (QuickStop/Disable). 축 간 조건에는 보통 Halt 가 적절합니다.");
        if (rule.Mode == InterlockMode.AllowOnlyWhileActive) warn.Add("허용 조건이 처음부터 충족되지 않으면 연결 직후부터 대상 축이 차단됩니다.");
        if (!rule.BlockMotion && rule.EffectiveAction == InterlockAction.None) warn.Add("동작 None + 차단 없음: 이 규칙은 표시 출력만 바꿉니다.");
        Warning = string.Join("\n", warn);

        string unitName = SelectedUnit.Value;
        string axisLabel = SelectedSourceAxis is null ? "?" : $"{SelectedSourceAxis.Value.Name}({SelectedSourceAxis.Value.Id})";
        string inputAlias = SelectedInputModule is not null && SelectedInputBit is not null ? SelectedInputModule.Value.InputNames[SelectedInputBit.Value] : "?";
        string cond = InterlockText.Condition(rule, SelectedInputModule?.Value.Name, inputAlias, SelectedInputBit?.Value ?? 0, axisLabel, unitName);
        string targets = AllAxes ? "전체 축" : string.Join(", ", TargetAxes.Where(t => t.IsChecked).Select(t => t.Label));
        Preview = rule.Mode == InterlockMode.AllowOnlyWhileActive
            ? $"[{cond}] 일 때만 {targets} 동작 허용. 조건이 깨지면 {rule.EffectiveAction}{(rule.BlockMotion ? " + 차단" : "")}"
            : $"[{cond}] 이면 {targets} {rule.EffectiveAction}{(rule.BlockMotion ? " + 차단" : "")}";
    }
}
