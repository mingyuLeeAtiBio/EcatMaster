using EcatMaster.Core.EtherCat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EcatMaster.Infrastructure;

namespace EcatMaster.ViewModels;

/// <summary>인터록 탭 한 행. "사용" 체크박스로 런타임 해제/재개 (확인 창 + 로그 + devices.json 저장은 MainViewModel 이 처리).</summary>
public sealed class InterlockRowViewModel : ObservableObject
{
    private readonly Action<InterlockRowViewModel, bool> _toggleRequested;   // (row, enabled) — MainViewModel 이 비동기로 처리
    private InterlockStatus _s;
    private bool _suppress;

    public InterlockRowViewModel(InterlockStatus status, Action<InterlockRowViewModel, bool> toggleRequested)
    {
        _s = status;
        _toggleRequested = toggleRequested;
    }

    public InterlockStatus Status => _s;
    public string Name => _s.Name;
    public string Module => _s.Module;
    public int Bit => _s.Bit;
    public string InputName => _s.InputName;
    public bool ActiveLow => _s.ActiveLow;
    public string Action => _s.Action;
    public int[] Axes => _s.Axes;
    public bool BlockMotion => _s.BlockMotion;
    public bool Active => _s.Active;
    public bool InputState => _s.InputState;
    public long TriggerCount => _s.TriggerCount;
    public DateTime? LastTriggeredUtc => _s.LastTriggeredUtc;
    public string? Message => _s.Message;
    public string? Error => _s.Error;
    public string MessageOrError => _s.Error ?? _s.Message ?? "";
    public bool IsAxisSource => _s.Source != nameof(InterlockSource.Input);

    // ---- 참조 위치·조건을 분명히 ----

    /// <summary>소스 위치: "DI1 (slave 13)" / "축 Z(3) (slave 7)"</summary>
    public string SourceText => _s.SourceLabel ?? _s.Module;

    /// <summary>조건: "DI1.IN0 (EStop) = 0" / "축 X(1) 이동 중" / "축 Z(3) 위치 1000~2000 um 안 (±20)"</summary>
    public string ConditionText => _s.ConditionText ?? "";

    /// <summary>현재값: 입력 1/0 · "이동 중"/"정지" · "1234.5 um"</summary>
    public string CurrentValueText => _s.Source switch
    {
        nameof(InterlockSource.AxisMoving) => _s.SourceValue is null ? "-" : (_s.Condition ? "이동 중" : "정지"),
        nameof(InterlockSource.AxisInRange) or nameof(InterlockSource.AxisOutOfRange) =>
            _s.SourceValue is null ? "-" : $"{_s.SourceValue.Value:0.##} {_s.Unit}" + (_s.Condition ? (_s.Source == nameof(InterlockSource.AxisInRange) ? " (안)" : " (밖)") : ""),
        _ => _s.Slave > 0 ? (_s.InputState ? "1" : "0") : "-",
    };

    public string ModeText => _s.Mode == nameof(InterlockMode.AllowOnlyWhileActive) ? "활성 시만 허용" : "활성 시 차단";

    /// <summary>툴팁: 조건 + 소스 + 모드 + 안전측 설명</summary>
    public string ConditionTooltip
    {
        get
        {
            string mode = _s.Mode == nameof(InterlockMode.AllowOnlyWhileActive)
                ? "조건이 참인 동안만 대상 축 동작 허용 (거짓이면 정지·차단)"
                : "조건이 참인 동안 대상 축 정지·차단";
            string safe = _s.Source == nameof(InterlockSource.Input)
                ? (_s.ActiveLow ? "입력 0 이면 조건 참 (NC 접점). 모듈 오프라인 = 차단" : "입력 1 이면 조건 참 (NO 접점). 모듈 오프라인 = 차단")
                : "소스 축 오프라인 = 차단";
            return $"{ConditionText}\n소스: {SourceText}\n방식: {mode}\n{safe}";
        }
    }

    /// <summary>표시 출력(램프): "DO1.OUT2 · InterlockLamp (slave 14)" 또는 빈 문자열</summary>
    public string OutputText => _s.OutputModule is null ? ""
        : _s.OutputSlave > 0 && _s.OutputBit >= 0 ? $"{_s.OutputModule}.OUT{_s.OutputBit} · {_s.OutputName} (slave {_s.OutputSlave})"
        : $"{_s.OutputModule}.{_s.OutputName ?? _s.OutputBit.ToString()} (미해석)";

    /// <summary>대상 축: "전체 (12축)" 또는 "X(1), Y(2)"</summary>
    public string AxesText => _s.AllAxes ? $"전체 ({_s.Axes.Length}축)"
        : _s.AxisNames is { Length: > 0 } ? string.Join(", ", _s.AxisNames.Select((n, i) => $"{n}({_s.Axes[i]})")) : string.Join(", ", _s.Axes);

    /// <summary>
    /// 체크박스 바인딩. 사용자가 바꾸면 요청만 넘기고 값은 즉시 원래대로 되돌린다.
    /// 실제 반영(확인 창 → 마스터 → devices.json)은 MainViewModel 이 디스패처에서 처리하고, 100 ms 주기 갱신이 마스터 상태를 다시 읽어 체크박스에 반영한다.
    /// (바인딩 setter 안에서 모달 창을 열면 DataGrid/자동화 문맥에서 재진입 문제가 생겨 분리했다.)
    /// </summary>
    public bool Enabled
    {
        get => _s.Enabled;
        set
        {
            if (_suppress) return;
            if (value == _s.Enabled) return;
            _toggleRequested(this, value);
            // 체크박스는 마스터 상태가 바뀔 때(100 ms 갱신) 따라오므로 지금은 원래 값으로 되돌린다
            OnPropertyChanged(nameof(Enabled));
        }
    }

    public void Update(InterlockStatus s)
    {
        if (s == _s) return;
        _s = s;
        _suppress = true;
        OnPropertyChanged(string.Empty);
        _suppress = false;
    }
}
