using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EcatMaster.Core.EtherCat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EcatMaster.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EcatMaster.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private readonly UserSettings _settings;
    private IEtherCatMaster? _master;

    /// <param name="dialogs">대화상자/보조 창 서비스. WPF 는 <c>Views.WpfDialogService</c>, 테스트는 <see cref="NullDialogService"/>.</param>
    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;

        EnableAllInterlocksCommand = new RelayCommand(EnableAllInterlocks, () => IsConnected && Interlocks.Any(r => !r.Enabled));
        AddInterlockCommand = new RelayCommand(() => EditInterlock(null), () => IsConnected);
        EditInterlockCommand = new RelayCommand(() => EditInterlock(SelectedInterlock?.Name), () => IsConnected && SelectedInterlock is not null);
        DeleteInterlockCommand = new RelayCommand(DeleteSelectedInterlock, () => IsConnected && SelectedInterlock is not null);
        EditInterlockRowCommand = new RelayCommand<InterlockRowViewModel>(row => { if (row is null) return; SelectedInterlock = row; EditInterlock(row.Name); }, _ => IsConnected);
        OpenIoGroupWindowCommand = new RelayCommand<IoGroupViewModel>(g => { if (g is not null) _dialogs.ShowIoGroup(g); });
        SaveAxisAliasCommand = new RelayCommand(SaveAxisAlias, () => IsConnected && SelectedAxis is not null);
        OpenDeviceConfigCommand = new RelayCommand(OpenDeviceConfig);
        CtStartCommand = new RelayCommand(CtStart, () => IsConnected && SelectedAxis is not null && !CtRunning && !EtRunning);
        CtStopCommand = new RelayCommand(CtStop, () => IsConnected && SelectedAxis is not null && CtRunning);
        CtSetAFromCurrentCommand = new RelayCommand(() => { if (SelectedAxis is { } a) CtPointA = a.Axis.GetStatus().PositionUser; }, () => SelectedAxis is not null);
        CtSetBFromCurrentCommand = new RelayCommand(() => { if (SelectedAxis is { } a) CtPointB = a.Axis.GetStatus().PositionUser; }, () => SelectedAxis is not null);
        EtStartCommand = new RelayCommand(EtStart, () => IsConnected && SelectedAxis is not null && !EtRunning && !CtRunning);
        EtStopCommand = new RelayCommand(EtStop, () => IsConnected && SelectedAxis is not null && EtRunning);
        EtOpenFolderCommand = new RelayCommand(EtOpenFolder);
        AutoConnectCommand = new AsyncRelayCommand(async () =>
        {
            if (Environment.GetEnvironmentVariable("ECATMASTER_NO_AUTOCONNECT") == "1") return;   // 자동화 테스트용
            try { await AutoConnectAsync(); }
            catch (Exception ex) { AppendLog(LogLevel.Debug, $"자동 연결 오류: {ex.Message}"); }
        });
        // 해제로 IO 그룹 목록이 바뀌면 떠 있는 모달리스 IO 창을 정리
        IoGroups.CollectionChanged += (_, _) => _dialogs.CloseIoGroupsExcept(IoGroups);

        RefreshAdaptersCommand = new RelayCommand(RefreshAdapters, () => !IsConnected);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !IsBusy);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected && !IsBusy);

        EnableAllCommand = new RelayCommand(() => _master?.EnableAll(), () => IsConnected);
        DisableAllCommand = new RelayCommand(() => _master?.DisableAll(), () => IsConnected);
        FaultResetAllCommand = new RelayCommand(() => _master?.FaultResetAll(), () => IsConnected);
        StopAllCommand = new RelayCommand(() => _master?.StopAll(false), () => IsConnected);
        QuickStopAllCommand = new RelayCommand(() => _master?.StopAll(true), () => IsConnected);

        EnableCommand = AxisCmd(_ => new AxisCommand(AxisCommandKind.Enable));
        DisableCommand = AxisCmd(_ => new AxisCommand(AxisCommandKind.Disable));
        FaultResetCommand = AxisCmd(_ => new AxisCommand(AxisCommandKind.FaultReset));
        MoveAbsoluteCommand = AxisCmd(a => a.MakeMove(false, TargetPosition, Velocity, Acceleration, Deceleration));
        MoveRelativeCommand = AxisCmd(a => a.MakeMove(true, TargetPosition, Velocity, Acceleration, Deceleration));
        JogPlusCommand = AxisCmd(a => a.MakeJog(Math.Abs(JogVelocity), Acceleration, Deceleration));
        JogMinusCommand = AxisCmd(a => a.MakeJog(-Math.Abs(JogVelocity), Acceleration, Deceleration));
        StopCommand = AxisCmd(_ => new AxisCommand(AxisCommandKind.Halt));
        QuickStopCommand = AxisCmd(_ => new AxisCommand(AxisCommandKind.QuickStop));
        HomeCommand = AxisCmd(_ => new AxisCommand(AxisCommandKind.Home));
        ClearLogCommand = new RelayCommand(Log.Clear);
        ResetStatsCommand = new RelayCommand(() => _master?.ResetStatistics(), () => IsConnected);
        SdoReadCommand = new AsyncRelayCommand(SdoReadAsync, () => IsConnected && !UseSimulation);
        SdoWriteCommand = new AsyncRelayCommand(SdoWriteAsync, () => IsConnected && !UseSimulation);

        ReadDriveSettingsCommand = new AsyncRelayCommand(ReadDriveSettingsAsync, () => IsConnected && SelectedAxis is not null);
        WriteDriveSettingsCommand = new AsyncRelayCommand(() => WriteDriveSettingsAsync(false, false), () => IsConnected && SelectedAxis is not null);
        RunConfigurationCommand = new AsyncRelayCommand(() => WriteDriveSettingsAsync(true, false), () => IsConnected && SelectedAxis is not null);
        SaveNvmCommand = new AsyncRelayCommand(() => WriteDriveSettingsAsync(false, true), () => IsConnected && SelectedAxis is not null);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Refresh();

        // 저장된 입력값 복원
        _settings = UserSettings.Load();
        _autoConnect = _settings.AutoConnect;
        _cycleTimeUs = _settings.CycleTimeUs;
        _axisCount = _settings.AxisCount;
        _remapPdo = _settings.RemapPdo;
        _cpuCore = _settings.CpuCore;
        _dcSyncOffsetUs = _settings.DcSyncOffsetUs > 0 ? _settings.DcSyncOffsetUs : 500;
        _selectedUnit = UnitModes.Contains(_settings.Unit) ? _settings.Unit : "um";
        _leadUm = _settings.LeadUm > 0 ? _settings.LeadUm : 10_000;
        _targetPosition = _settings.TargetPosition;
        _velocity = _settings.Velocity;
        _acceleration = _settings.Acceleration;
        _deceleration = _settings.Deceleration;
        _jogVelocity = _settings.JogVelocity;
        _ctPointA = _settings.CyclePointA;
        _ctPointB = _settings.CyclePointB;
        _ctVelocity = _settings.CycleVelocity;
        _ctCycles = _settings.CycleCount;
        _ctDwellMs = _settings.CycleDwellMs;
        _etMode = EtModes[Math.Clamp(_settings.EnduranceMode, 0, EtModes.Length - 1)];
        _etVelocity = _settings.EnduranceVelocity;
        _etReverse = _settings.EnduranceReverse;
        _etDuration = _settings.EnduranceDuration;
        _etDurationUnit = EtDurationUnits.Contains(_settings.EnduranceDurationUnit) ? _settings.EnduranceDurationUnit : EtDurationUnits[0];
        _etInfinite = _settings.EnduranceInfinite;
        _etSampleSeconds = _settings.EnduranceSampleSeconds;
        _etSpeedTolerance = _settings.EnduranceSpeedTolerance;
        _etAutoRecover = _settings.EnduranceAutoRecover;
        _etMaxRecoveries = _settings.EnduranceMaxRecoveries;

        NativeAvailable = SoemAdapters.IsNativeAvailable(out var err);
        if (!NativeAvailable)
        {
            UseSimulation = true;
            bool dllPresent = System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "soemshim.dll"));
            AppendLog(LogLevel.Warning, dllPresent
                ? $"soemshim.dll 은 있지만 의존 DLL(wpcap.dll) 을 못 찾았습니다. {SoemAdapters.NpcapStatus}. 시뮬레이션만 가능합니다."
                : $"soemshim.dll 이 없습니다. native/soemshim/build-win.ps1 로 빌드하세요. 시뮬레이션만 가능합니다. ({err})");
        }
        else
        {
            AppendLog(LogLevel.Information, SoemAdapters.NpcapStatus);
            UseSimulation = _settings.UseSimulation;
            RefreshAdapters();
            if (!string.IsNullOrEmpty(_settings.AdapterName))
            {
                var saved = Adapters.FirstOrDefault(a => a.Name == _settings.AdapterName);
                if (saved is not null) SelectedAdapter = saved;
            }
        }
    }

    private void SaveSettings()
    {
        _settings.UseSimulation = UseSimulation;
        _settings.AdapterName = SelectedAdapter?.Name;
        _settings.AutoConnect = AutoConnect;
        _settings.CycleTimeUs = CycleTimeUs;
        _settings.AxisCount = AxisCount;
        _settings.RemapPdo = RemapPdo;
        _settings.CpuCore = CpuCore;
        _settings.DcSyncOffsetUs = DcSyncOffsetUs;
        _settings.Unit = SelectedUnit;
        _settings.LeadUm = LeadUm;
        _settings.TargetPosition = TargetPosition;
        _settings.Velocity = Velocity;
        _settings.Acceleration = Acceleration;
        _settings.Deceleration = Deceleration;
        _settings.JogVelocity = JogVelocity;
        _settings.CyclePointA = CtPointA;
        _settings.CyclePointB = CtPointB;
        _settings.CycleVelocity = CtVelocity;
        _settings.CycleCount = CtCycles;
        _settings.CycleDwellMs = CtDwellMs;
        _settings.EnduranceMode = Array.IndexOf(EtModes, EtMode);
        _settings.EnduranceVelocity = EtVelocity;
        _settings.EnduranceReverse = EtReverse;
        _settings.EnduranceDuration = EtDuration;
        _settings.EnduranceDurationUnit = EtDurationUnit;
        _settings.EnduranceInfinite = EtInfinite;
        _settings.EnduranceSampleSeconds = EtSampleSeconds;
        _settings.EnduranceSpeedTolerance = EtSpeedTolerance;
        _settings.EnduranceAutoRecover = EtAutoRecover;
        _settings.EnduranceMaxRecoveries = EtMaxRecoveries;
        _settings.Save();
    }

    // ------------------------------------------------------------ connection settings

    public bool NativeAvailable { get; }

    public ObservableCollection<NetworkAdapter> Adapters { get; } = new();

    private NetworkAdapter? _selectedAdapter;
    public NetworkAdapter? SelectedAdapter { get => _selectedAdapter; set => SetProperty(ref _selectedAdapter, value); }

    private bool _useSimulation;
    public bool UseSimulation
    {
        get => _useSimulation;
        set { if (SetProperty(ref _useSimulation, value)) OnPropertyChanged(nameof(UseSoem)); }
    }
    public bool UseSoem { get => !_useSimulation; set => UseSimulation = !value; }

    private int _cycleTimeUs = 2000;
    public int CycleTimeUs { get => _cycleTimeUs; set => SetProperty(ref _cycleTimeUs, value); }

    private int _axisCount = 12;
    public int AxisCount { get => _axisCount; set => SetProperty(ref _axisCount, value); }

    private bool _remapPdo = true;
    public bool RemapPdo { get => _remapPdo; set => SetProperty(ref _remapPdo, value); }

    private int _cpuCore = -1;
    public int CpuCore { get => _cpuCore; set => SetProperty(ref _cpuCore, value); }

    private int _dcSyncOffsetUs = 500;
    /// <summary>DC 동기 위상 오프셋 [µs]. VPH-HD 같은 DC 필수 드라이브에서 동기 오류가 나면 키운다 (사이클의 1/4 정도).</summary>
    public int DcSyncOffsetUs { get => _dcSyncOffsetUs; set => SetProperty(ref _dcSyncOffsetUs, value); }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(CanEditSettings));
            OnPropertyChanged(nameof(CanControlAxis));
            RefreshCommandStates();
        }
    }

    /// <summary>조그 버튼처럼 Command 없이 IsEnabled 로 묶는 컨트롤용.</summary>
    public bool CanControlAxis => IsConnected && SelectedAxis is not null;

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(CanEditSettings)); RefreshCommandStates(); } } }

    public bool CanEditSettings => !IsConnected && !IsBusy;

    private string _connectionText = "연결 안 됨";
    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }

    // ------------------------------------------------------------ units

    public string[] UnitModes { get; } = ["um", "step"];

    private string _selectedUnit = "um";
    /// <summary>입력·표시 단위. 연결 중에 바꾸면 모든 축에 즉시 반영된다.</summary>
    public string SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (!SetProperty(ref _selectedUnit, value)) return;
            var unit = value == "um" ? UserUnit.Um : UserUnit.Step;
            if (_master is not null) foreach (var a in _master.Axes) a.Mechanics.Unit = unit;
            OnPropertyChanged(nameof(PosUnitLabel));
            OnPropertyChanged(nameof(VelUnitLabel));
            OnPropertyChanged(nameof(AccUnitLabel));
            LoadDriveSettingsFields();
            NotifyRpm();
        }
    }

    private double _leadUm = 10_000;
    /// <summary>모든 축 공통 리드 [µm/rev]. 연결 시 마스터 옵션으로 전달. 축별로 다르면 드라이브 설정 패널에서 개별 수정.</summary>
    public double LeadUm { get => _leadUm; set => SetProperty(ref _leadUm, value); }

    public string PosUnitLabel => SelectedUnit == "um" ? "µm" : "step";
    public string VelUnitLabel => SelectedUnit == "um" ? "µm/s" : "Hz";
    public string AccUnitLabel => SelectedUnit == "um" ? "µm/s²" : "step/s²";

    // ------------------------------------------------------------ status

    private MasterStatus? _masterStatus;
    public MasterStatus? Master { get => _masterStatus; private set => SetProperty(ref _masterStatus, value); }

    public ObservableCollection<SlaveInfo> Slaves { get; } = new();
    public ObservableCollection<AxisRowViewModel> Axes { get; } = new();
    public ObservableCollection<IoModuleViewModel> IoModules { get; } = new();
    /// <summary>IO 탭 단위. 구성의 Group 또는 자동 DI/DO 쌍 묶기 (<see cref="IoGroupViewModel.Build"/>).</summary>
    public ObservableCollection<IoGroupViewModel> IoGroups { get; } = new();
    public ObservableCollection<InterlockRowViewModel> Interlocks { get; } = new();

    /// <summary>
    /// 체크박스 요청을 받아 디스패처 큐에서 처리: 해제는 확인 창(OK/취소)을 거친 뒤 마스터·devices.json·로그에 반영.
    /// 바인딩 setter 밖에서 모달 창을 열기 위해 BeginInvoke 로 미룬다.
    /// </summary>
    private void OnInterlockToggleRequested(InterlockRowViewModel row, bool enabled)
    {
        AppendLog(LogLevel.Debug, $"인터록 '{row.Name}' {(enabled ? "사용" : "해제")} 요청");
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            try { ApplyInterlockEnabled(row.Name, enabled, confirm: !enabled); }
            catch (Exception ex) { AppendLog(LogLevel.Error, $"인터록 '{row.Name}' 변경 처리 오류: {ex.Message}"); }
        }));
    }

    private void ApplyInterlockEnabled(string name, bool enabled, bool confirm)
    {
        if (_master is null) return;
        var st = _master.GetInterlocks().FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (st is null) { AppendLog(LogLevel.Warning, $"인터록 '{name}' 을 찾을 수 없음"); return; }
        if (st.Enabled == enabled) return;

        if (confirm)
        {
            bool ok = Confirm(
                $"인터록 '{name}' 을(를) 해제합니다.\n\n해제 중에는 이 조건 [{st.ConditionText}] 에서 축이 정지·차단되지 않습니다.\n시운전·점검 목적으로만 사용하고, 끝나면 다시 켜 주세요.",
                "인터록 해제 확인");
            if (!ok)
            {
                AppendLog(LogLevel.Information, $"인터록 '{name}' 해제 취소");
                return;
            }
        }

        if (!_master.SetInterlockEnabled(name, enabled)) return;

        _devices ??= DeviceConfigFile.LoadOrCreateDefault(out _);
        var rule = _devices.Interlocks.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (rule is not null) { rule.Enabled = enabled; _devices.Save(); }

        AppendLog(enabled ? LogLevel.Information : LogLevel.Warning,
            enabled ? $"인터록 '{name}' 사용 재개 (devices.json 저장)" : $"인터록 '{name}' 해제 (devices.json 저장) — 재연결 후에도 해제 상태 유지");
    }

    private void EnableAllInterlocks()
    {
        if (_master is null) return;
        foreach (var row in Interlocks.Where(r => !r.Enabled).ToList()) ApplyInterlockEnabled(row.Name, true, confirm: false);
    }

    /// <summary>OK/취소 확인 (기본 취소). 다이얼로그 서비스가 활성 창(모달리스 IO 창 등) 위에 띄운다.</summary>
    private bool Confirm(string text, string title) => _dialogs.Confirm(text, title);

    // ---------------- 인터록 규칙 추가/편집/삭제 (런타임 + devices.json) ----------------

    private InterlockRowViewModel? _selectedInterlock;
    public InterlockRowViewModel? SelectedInterlock { get => _selectedInterlock; set => SetProperty(ref _selectedInterlock, value); }

    /// <summary>name=null 이면 추가. 편집기(모달) → 검증된 규칙 → 마스터 교체(원자적) → devices.json 저장.</summary>
    public void EditInterlock(string? name)
    {
        if (_master is null) return;
        var existing = name is null ? null : _master.InterlockRules.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (name is not null && existing is null) { AppendLog(LogLevel.Warning, $"인터록 '{name}' 을 찾을 수 없음"); return; }

        var rule = _dialogs.EditInterlock(new InterlockEditorViewModel(_master, existing));
        if (rule is null) return;

        // 안전 수준을 낮추는 변경은 확인
        if (existing is not null)
        {
            var lowered = new List<string>();
            if (existing.BlockMotion && !rule.BlockMotion) lowered.Add("동작 차단 해제");
            if (existing.Enabled && !rule.Enabled) lowered.Add("규칙 해제(사용 끔)");
            if (existing.EffectiveAction != InterlockAction.None && rule.EffectiveAction == InterlockAction.None) lowered.Add($"정지 동작 {existing.EffectiveAction} → None");
            if (lowered.Count > 0 && !Confirm($"인터록 '{existing.Name}' 의 안전 수준을 낮춥니다:\n - {string.Join("\n - ", lowered)}\n\n계속하시겠습니까?", "인터록 안전 수준 하향 확인"))
            {
                AppendLog(LogLevel.Information, $"인터록 '{existing.Name}' 편집 취소");
                return;
            }
        }

        try
        {
            var statuses = existing is null ? _master.AddInterlock(rule) : _master.UpdateInterlock(existing.Name, rule);
            SaveInterlockRules();
            var st = statuses.FirstOrDefault(s => string.Equals(s.Name, rule.Name, StringComparison.OrdinalIgnoreCase));
            string cond = st?.ConditionText ?? InterlockText.Preview(rule);
            if (st?.Error is not null)
                AppendLog(LogLevel.Error, $"인터록 '{rule.Name}' {(existing is null ? "추가" : "편집")}됨 — 그러나 비활성: {st.Error} (devices.json 저장)");
            else
                AppendLog(LogLevel.Warning, $"인터록 '{rule.Name}' {(existing is null ? "추가" : "편집")}: [{cond}] → {rule.EffectiveAction}, {InterlockText.ModeName(rule.Mode)}{(rule.BlockMotion ? ", 차단" : "")} (devices.json 저장, 즉시 적용)");
            Refresh();
            SelectedInterlock = Interlocks.FirstOrDefault(r => string.Equals(r.Name, rule.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, $"인터록 '{rule.Name}' 저장 실패: {ex.Message}");
        }
    }

    private void DeleteSelectedInterlock()
    {
        if (_master is null || SelectedInterlock is null) return;
        var row = SelectedInterlock;
        if (!Confirm($"인터록 '{row.Name}' 을(를) 삭제합니다.\n조건: {row.ConditionText}\n\n삭제하면 이 조건에서 축이 정지·차단되지 않습니다. 계속하시겠습니까?", "인터록 삭제 확인"))
        {
            AppendLog(LogLevel.Information, $"인터록 '{row.Name}' 삭제 취소");
            return;
        }
        try
        {
            _master.RemoveInterlock(row.Name);
            SaveInterlockRules();
            AppendLog(LogLevel.Warning, $"인터록 '{row.Name}' 삭제됨 [{row.ConditionText}] (devices.json 저장)");
            Refresh();
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, $"인터록 '{row.Name}' 삭제 실패: {ex.Message}");
        }
    }

    /// <summary>마스터의 현재 규칙 목록을 devices.json 모델에 복사해 저장. (마스터는 Options.Interlocks 를 제자리에서 갱신하지만, 안전하게 마스터 것을 진실로 삼는다.)</summary>
    private void SaveInterlockRules()
    {
        if (_master is null) return;
        _devices ??= DeviceConfigFile.LoadOrCreateDefault(out _);
        _devices.Interlocks = _master.InterlockRules.ToList();
        _devices.Save();
    }

    private string _interlockSummary = "";
    /// <summary>하단 상태바: 활성 인터록 요약.</summary>
    public string InterlockSummary { get => _interlockSummary; private set => SetProperty(ref _interlockSummary, value); }

    private bool _anyInterlockActive;
    public bool AnyInterlockActive { get => _anyInterlockActive; private set => SetProperty(ref _anyInterlockActive, value); }

    public string DeviceConfigPath => DeviceConfigFile.PathOnDisk;

    // ------------------------------------------------------------ 별칭 (alias)

    private DeviceConfigFile? _devices;   // 연결 시 로드. 별칭 변경을 여기에 기록해 저장.

    private string _selectedAxisAlias = "";
    /// <summary>선택 축 별칭 편집란. "별칭 저장" 으로 반영.</summary>
    public string SelectedAxisAlias { get => _selectedAxisAlias; set => SetProperty(ref _selectedAxisAlias, value); }

    private void SaveAxisAlias()
    {
        var axis = SelectedAxis?.Axis;
        if (axis is null || _master is null) return;
        var name = SelectedAxisAlias.Trim();
        if (name.Length == 0) { AppendLog(LogLevel.Warning, "별칭이 비어 있습니다"); return; }
        var dup = _master.GetAxis(name);
        if (dup is not null && dup.Id != axis.Id) { AppendLog(LogLevel.Warning, $"별칭 '{name}' 은 축 {dup.Id} 가 사용 중"); return; }
        if (int.TryParse(name, out _)) AppendLog(LogLevel.Warning, $"별칭 '{name}' 이 숫자라 축 번호와 혼동될 수 있습니다");

        axis.Name = name;
        _devices ??= DeviceConfigFile.LoadOrCreateDefault(out _);
        _devices.SetAxisAlias(axis.Id, name, axis.Mechanics.LeadUm);
        _devices.Save();
        SelectedAxis?.Update();
        OnPropertyChanged(nameof(SelectedAxis));
        AppendLog(LogLevel.Information, $"축 {axis.Id} 별칭 → '{name}' (devices.json 저장)");
    }

    /// <summary>IO 비트 별칭 변경 (비트 오른쪽 클릭 컨텍스트 메뉴). 런타임 반영 + devices.json 저장.</summary>
    private void RenameIoBit(IoModuleViewModel module, IoBitViewModel bit)
    {
        var kind = bit.IsOutput ? "출력" : "입력";
        var name = _dialogs.Prompt("비트 별칭 변경", $"{module.Name} {kind} {bit.Index} 의 새 별칭", bit.Name);
        if (name is null) return;
        ApplyIoBitAlias(module, bit, name);
        _devices?.Save();
        AppendLog(LogLevel.Information, $"{module.Name} {kind} {bit.Index} 별칭 → '{bit.Name}' (devices.json 저장, 인터록 규칙에 쓰였다면 규칙의 Input/Output 도 맞춰 주세요)");
    }

    /// <summary>별칭 하나를 마스터(런타임)와 devices.json 메모리 사본에 반영. 저장은 호출자가.</summary>
    private void ApplyIoBitAlias(IoModuleViewModel module, IoBitViewModel bit, string name)
    {
        if (bit.IsOutput) module.Module.SetOutputName(bit.Index, name); else module.Module.SetInputName(bit.Index, name);
        module.Update();
        _devices ??= DeviceConfigFile.LoadOrCreateDefault(out _);
        _devices.SetIoBitAlias(module.Module.Name, module.Module.Slave, !bit.IsOutput, bit.Index, bit.Name);
    }

    /// <summary>IO 그룹(탭/창)의 "별칭 편집…": 모든 비트를 표에서 한 번에 고친다.</summary>
    private void EditIoAliases(IoGroupViewModel group)
    {
        var changes = _dialogs.EditIoAliases(group);
        if (changes is null || changes.Count == 0) return;
        foreach (var (module, bit, name) in changes) ApplyIoBitAlias(module, bit, name);
        _devices?.Save();
        AppendLog(LogLevel.Information, $"IO '{group.Name}' 별칭 {changes.Count}개 변경: "
            + string.Join(", ", changes.Select(c => $"{c.Module.Name}.{(c.Bit.IsOutput ? "OUT" : "IN")}{c.Bit.Index}='{c.NewName}'"))
            + " (devices.json 저장)");
    }

    /// <summary>구성(devices.json IoModules[].Group)에서 모듈의 표시 그룹을 찾는다. 이름 우선, 없으면 슬레이브 번호.</summary>
    private string? ConfiguredIoGroup(IoModuleViewModel m)
    {
        var list = _devices?.IoModules;
        if (list is null) return null;
        var o = list.FirstOrDefault(x => string.Equals(x.Name, m.Module.Name, StringComparison.OrdinalIgnoreCase))
             ?? list.FirstOrDefault(x => x.Slave > 0 && x.Slave == m.Module.Slave);
        return o?.Group;
    }

    private void BuildIoGroups()
    {
        IoGroups.Clear();
        foreach (var g in IoGroupViewModel.Build(IoModules, ConfiguredIoGroup, EditIoAliases)) IoGroups.Add(g);
        if (IoGroups.Count > 0)
            AppendLog(LogLevel.Debug, "IO 탭 그룹: " + string.Join(" | ", IoGroups.Select(g => g.IsPair ? $"{g.Name} (쌍)" : g.Name)));
    }

    private void OpenDeviceConfig()
    {
        try
        {
            DeviceConfigFile.LoadOrCreateDefault(out _);   // 없으면 기본 파일 생성
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DeviceConfigFile.PathOnDisk) { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog(LogLevel.Error, $"장비 구성 파일 열기 실패: {ex.Message}"); }
    }

    private AxisRowViewModel? _selectedAxis;
    public AxisRowViewModel? SelectedAxis
    {
        get => _selectedAxis;
        set
        {
            if (!SetProperty(ref _selectedAxis, value)) return;
            if (value is not null) SdoSlave = value.Axis.Slave;
            SelectedAxisAlias = value?.Axis.Name ?? "";
            OnPropertyChanged(nameof(CanControlAxis));
            LoadDriveSettingsFields();
            NotifyRpm();
            RefreshCommandStates();
        }
    }

    public ObservableCollection<string> Log { get; } = new();

    // ------------------------------------------------------------ motion inputs (현재 단위 기준)

    private double _targetPosition = 1000;
    public double TargetPosition { get => _targetPosition; set => SetProperty(ref _targetPosition, value); }

    private double _velocity = 1000;
    public double Velocity { get => _velocity; set { if (SetProperty(ref _velocity, value)) NotifyRpm(); } }

    private double _acceleration = 30_000;
    public double Acceleration { get => _acceleration; set => SetProperty(ref _acceleration, value); }

    private double _deceleration = 30_000;
    public double Deceleration { get => _deceleration; set => SetProperty(ref _deceleration, value); }

    private double _jogVelocity = 500;
    public double JogVelocity { get => _jogVelocity; set { if (SetProperty(ref _jogVelocity, value)) NotifyRpm(); } }

    // ------------------------------------------------------------ 왕복 테스트 (A ↔ B, N회)

    private readonly CycleTestRunner _cycleRunner = new();

    // ------------------------------------------------------------ RPM 환산 (선택 축의 분해능·기어비·리드 기준, 출력축)

    /// <summary>현재 단위의 속도 입력값 → "≈ 400 rpm". 축이 선택되지 않으면 빈 문자열.</summary>
    private string RpmHint(double velocity) =>
        SelectedAxis?.Axis.Mechanics is { } m ? $"≈ {m.UserVelocityToRpm(velocity):0.#} rpm" : "";

    /// <summary>속도 0 은 드라이브 기본값(6081h)을 쓰므로 그 값을 환산해 보여 준다.</summary>
    public string VelocityRpmText => Velocity == 0 && SelectedAxis?.Axis is { } a
        ? $"≈ {a.Mechanics.StepsPerSecToRpm(a.Defaults.Velocity):0.#} rpm (기본)"
        : RpmHint(Velocity);
    public string JogVelocityRpmText => RpmHint(Math.Abs(JogVelocity));
    public string CtVelocityRpmText => RpmHint(CtVelocity);

    private void NotifyRpm()
    {
        OnPropertyChanged(nameof(VelocityRpmText));
        OnPropertyChanged(nameof(JogVelocityRpmText));
        OnPropertyChanged(nameof(CtVelocityRpmText));
        OnPropertyChanged(nameof(EtVelocityRpmText));
    }

    private double _ctPointA;
    public double CtPointA { get => _ctPointA; set => SetProperty(ref _ctPointA, value); }

    private double _ctPointB = 10_000;
    public double CtPointB { get => _ctPointB; set => SetProperty(ref _ctPointB, value); }

    private double _ctVelocity = 5_000;
    public double CtVelocity { get => _ctVelocity; set { if (SetProperty(ref _ctVelocity, value)) NotifyRpm(); } }

    private int _ctCycles = 10;
    /// <summary>0 = 중지할 때까지 무한</summary>
    public int CtCycles { get => _ctCycles; set => SetProperty(ref _ctCycles, value); }

    private int _ctDwellMs = 200;
    public int CtDwellMs { get => _ctDwellMs; set => SetProperty(ref _ctDwellMs, value); }

    private string _ctStatusText = "";
    public string CtStatusText { get => _ctStatusText; private set => SetProperty(ref _ctStatusText, value); }

    private bool _ctRunning;
    public bool CtRunning
    {
        get => _ctRunning;
        private set
        {
            // 시작/중지 버튼의 CanExecute 는 입력 이벤트가 있어야 재평가되므로, 백그라운드 완료 시 강제로 갱신
            if (SetProperty(ref _ctRunning, value)) RefreshCommandStates();
        }
    }

    private void CtStart()
    {
        var axis = SelectedAxis?.Axis;
        if (axis is null) return;
        try
        {
            var st = _cycleRunner.Start(axis, new CycleTestRequest(CtPointA, CtPointB, CtVelocity, CtCycles, CtDwellMs, Acceleration, Deceleration));
            AppendLog(LogLevel.Information, $"[{axis.Name}] 왕복 테스트 시작: A={CtPointA} B={CtPointB} {PosUnitLabel}, {CtVelocity} {VelUnitLabel}, {(CtCycles == 0 ? "무한" : CtCycles + "회")}, 정지 {CtDwellMs} ms (step: {st.PointASteps} ↔ {st.PointBSteps})");
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Warning, $"[{axis.Name}] 왕복 테스트 시작 실패: {ex.Message}");
        }
    }

    private void CtStop()
    {
        var axis = SelectedAxis?.Axis;
        if (axis is null) return;
        if (_cycleRunner.Stop(axis.Id)) AppendLog(LogLevel.Information, $"[{axis.Name}] 왕복 테스트 중지 요청");
    }

    // ------------------------------------------------------------ 연속 운전 (시간 기반 내구 시험)

    private readonly EnduranceTestRunner _enduranceRunner = new();

    public string[] EtModes { get; } = ["연속 회전", "왕복 (A↔B)"];
    private string _etMode = "연속 회전";
    public string EtMode { get => _etMode; set { if (SetProperty(ref _etMode, value)) OnPropertyChanged(nameof(EtIsRotate)); } }
    public bool EtIsRotate => EtMode == EtModes[0];

    private double _etVelocity = 66_667;
    /// <summary>현재 단위 기준 속도 (크기). 회전 방향은 <see cref="EtReverse"/>.</summary>
    public double EtVelocity { get => _etVelocity; set { if (SetProperty(ref _etVelocity, value)) NotifyRpm(); } }
    public string EtVelocityRpmText => RpmHint(Math.Abs(EtVelocity));

    private bool _etReverse;
    public bool EtReverse { get => _etReverse; set => SetProperty(ref _etReverse, value); }

    public string[] EtDurationUnits { get; } = ["분", "시간"];
    private double _etDuration = 60;
    public double EtDuration { get => _etDuration; set => SetProperty(ref _etDuration, value); }
    private string _etDurationUnit = "분";
    public string EtDurationUnit { get => _etDurationUnit; set => SetProperty(ref _etDurationUnit, value); }

    private bool _etInfinite;
    /// <summary>운전 시간 무한: 중지 버튼을 누르거나 오류로 끝날 때까지 운전.</summary>
    public bool EtInfinite { get => _etInfinite; set { if (SetProperty(ref _etInfinite, value)) OnPropertyChanged(nameof(EtCanEditDuration)); } }

    /// <summary>시간 입력칸·단위 활성 여부 (운전 중이 아니고 무한이 아닐 때).</summary>
    public bool EtCanEditDuration => !EtRunning && !EtInfinite;

    private double _etSampleSeconds = 1;
    public double EtSampleSeconds { get => _etSampleSeconds; set => SetProperty(ref _etSampleSeconds, value); }

    private double _etSpeedTolerance = 10;
    public double EtSpeedTolerance { get => _etSpeedTolerance; set => SetProperty(ref _etSpeedTolerance, value); }

    private bool _etAutoRecover;
    public bool EtAutoRecover { get => _etAutoRecover; set => SetProperty(ref _etAutoRecover, value); }

    private int _etMaxRecoveries = 3;
    public int EtMaxRecoveries { get => _etMaxRecoveries; set => SetProperty(ref _etMaxRecoveries, value); }

    private string _etStatusText = "";
    public string EtStatusText { get => _etStatusText; private set => SetProperty(ref _etStatusText, value); }

    private double _etProgress;
    /// <summary>0~100 [%]</summary>
    public double EtProgress { get => _etProgress; private set => SetProperty(ref _etProgress, value); }

    private bool _etRunning;
    public bool EtRunning
    {
        get => _etRunning;
        private set
        {
            if (!SetProperty(ref _etRunning, value)) return;
            KeepAwake.Set(value);   // 장시간 시험 중 PC 절전 방지
            OnPropertyChanged(nameof(EtCanEditDuration));
            RefreshCommandStates();
        }
    }

    /// <summary>최근 사건 (최신이 위). "시각 [등급] 종류 · 메시지"</summary>
    public ObservableCollection<string> EtEvents { get; } = new();

    private string? _etLastFolder;
    private DateTime _etLastEventUtc;
    private string? _etLastReportedPhase;

    private void EtStart()
    {
        var axis = SelectedAxis?.Axis;
        if (axis is null || _master is null) return;
        try
        {
            double minutes = EtInfinite ? 0 : EtDurationUnit == "시간" ? EtDuration * 60 : EtDuration;   // 0 = 무한
            if (!EtInfinite && minutes <= 0) { AppendLog(LogLevel.Warning, $"[{axis.Name}] 운전 시간은 0 보다 커야 합니다 (무한은 '무한' 체크)"); return; }
            bool rotate = EtIsRotate;
            var req = new EnduranceTestRequest(
                rotate ? EnduranceMode.Rotate : EnduranceMode.Reciprocate,
                Velocity: rotate && EtReverse ? -Math.Abs(EtVelocity) : Math.Abs(EtVelocity),
                DurationMinutes: minutes,
                PointA: CtPointA, PointB: CtPointB, DwellMs: CtDwellMs,
                Acceleration: Acceleration, Deceleration: Deceleration,
                SampleIntervalSeconds: EtSampleSeconds,
                SpeedTolerancePercent: EtSpeedTolerance,
                AutoRecover: EtAutoRecover, MaxRecoveries: EtMaxRecoveries);
            var st = _enduranceRunner.Start(_master, axis, req);
            _etLastFolder = System.IO.Path.GetDirectoryName(st.EventsFile);
            _etLastEventUtc = DateTime.MinValue;
            EtEvents.Clear();
            AppendLog(LogLevel.Information, $"[{axis.Name}] 연속 운전 시작: {EtMode}, {(EtInfinite ? "무한" : $"{EtDuration} {EtDurationUnit}")}, 기록 {st.EventsFile}");
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Warning, $"[{axis.Name}] 연속 운전 시작 실패: {ex.Message}");
        }
    }

    private void EtStop()
    {
        var axis = SelectedAxis?.Axis;
        if (axis is null) return;
        if (_enduranceRunner.Stop(axis.Id)) AppendLog(LogLevel.Information, $"[{axis.Name}] 연속 운전 중지 요청");
    }

    private void EtOpenFolder()
    {
        string dir = _etLastFolder ?? EnduranceTestRunner.DefaultOutputDirectory;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog(LogLevel.Warning, $"기록 폴더 열기 실패: {ex.Message}"); }
    }

    /// <summary>72시간 같은 장시간도 표시되도록 총 시간 기준 hh:mm:ss.</summary>
    private static string Hms(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private int _etEventsAxisId = -1;

    private void RefreshEndurance()
    {
        var axis = SelectedAxis?.Axis;
        if ((axis?.Id ?? -1) != _etEventsAxisId)
        {
            // 선택 축이 바뀌면 사건 목록을 그 축 기준으로 다시 채운다
            _etEventsAxisId = axis?.Id ?? -1;
            _etLastEventUtc = DateTime.MinValue;
            EtEvents.Clear();
        }
        var st = axis is null ? null : _enduranceRunner.GetStatus(axis.Id);
        // 다른 축에서 돌고 있어도 절전 방지는 유지
        if (st is null) { EtRunning = false; KeepAwake.Set(_enduranceRunner.AnyRunning); EtStatusText = ""; EtProgress = 0; _etLastReportedPhase = null; return; }

        EtRunning = st.Running;
        KeepAwake.Set(_enduranceRunner.AnyRunning);
        EtProgress = st.DurationSeconds > 0 ? Math.Min(100, st.ElapsedSeconds * 100 / st.DurationSeconds) : 0;
        string phase = st.Phase switch
        {
            "Rotating" => "회전 중", "MoveToA" => "A로 이동", "MoveToB" => "B로 이동", "DwellA" => "A 정지", "DwellB" => "B 정지",
            "Recovering" => "자동 복구 중", "Stopping" => "정지 중", "Completed" => "완료", "Stopped" => "중지됨", "Failed" => "중단(오류)", _ => st.Phase,
        };
        var elapsed = TimeSpan.FromSeconds(st.ElapsedSeconds);
        var remain = TimeSpan.FromSeconds(Math.Max(0, st.DurationSeconds - st.ElapsedSeconds));
        string motion = st.Mode == nameof(EnduranceMode.Rotate)
            ? $"RPM {st.LastRpm:0.#} (지령 {st.TargetRpm:0.#}, 정속 최소/평균/최대 {st.MinRpm:0.#}/{st.AvgRpm:0.#}/{st.MaxRpm:0.#})"
            : $"왕복 {st.CompletedCycles}회 · 최대 위치오차 {st.MaxPositionErrorSteps} step";
        bool infinite = st.DurationSeconds <= 0;
        EtStatusText = (infinite ? $"{phase} · 경과 {Hms(elapsed)} / 무한 (중지할 때까지)\n" : $"{phase} · 경과 {Hms(elapsed)} / 남음 {Hms(remain)} ({EtProgress:0.#}%)\n")
                       + $"{motion} · 누적 {st.Revolutions:0.#} rev\n"
                       + $"오류 {st.Errors} · 경고 {st.Warnings} · 복구 {st.Recoveries} · WKC 오류 {st.WkcErrors} · 사이클 누락 {st.MissedCycles} · 최대 지터 {st.MaxJitterUs:0} µs"
                       + (st.StopReason is null ? "" : $"\n종료 사유: {st.StopReason}");

        // 새 사건만 목록 위에 추가하고, 경고·오류는 메인 로그에도 남긴다
        var fresh = st.RecentEvents.TakeWhile(e => e.TimeUtc > _etLastEventUtc).Reverse().ToList();
        foreach (var e in fresh)
        {
            string tag = e.Severity switch { EnduranceSeverity.Error => "오류", EnduranceSeverity.Warning => "경고", _ => "정보" };
            EtEvents.Insert(0, $"{e.TimeUtc.ToLocalTime():HH:mm:ss} [{tag}] {e.Category} · {e.Message}");
            if (e.Severity != EnduranceSeverity.Info)
                AppendLog(e.Severity == EnduranceSeverity.Error ? LogLevel.Error : LogLevel.Warning, $"[{st.AxisName}] 연속 운전 {tag}: {e.Message}");
        }
        if (fresh.Count > 0) _etLastEventUtc = fresh[^1].TimeUtc;
        while (EtEvents.Count > 200) EtEvents.RemoveAt(EtEvents.Count - 1);

        if (!st.Running && st.Phase is "Completed" or "Stopped" or "Failed" && _etLastReportedPhase != st.Phase + st.FinishedUtc)
        {
            _etLastReportedPhase = st.Phase + st.FinishedUtc;
            AppendLog(st.Phase == "Failed" ? LogLevel.Warning : LogLevel.Information,
                $"[{st.AxisName}] 연속 운전 {phase}: 경과 {Hms(elapsed)}, 오류 {st.Errors} / 경고 {st.Warnings}. 요약: {st.SummaryFile}");
        }
    }

    private string? _ctLastReportedPhase;

    private void RefreshCycleTest()
    {
        var axis = SelectedAxis?.Axis;
        var st = axis is null ? null : _cycleRunner.GetStatus(axis.Id);
        if (st is null) { CtRunning = false; CtStatusText = ""; _ctLastReportedPhase = null; return; }

        CtRunning = st.Running;
        string cycles = st.Cycles == 0 ? $"{st.CompletedCycles}회 (무한)" : $"{st.CompletedCycles} / {st.Cycles}회";
        string phase = st.Phase switch
        {
            "MoveToA" => "A로 이동", "MoveToB" => "B로 이동", "DwellA" => "A 정지", "DwellB" => "B 정지",
            "Completed" => "완료", "Stopped" => "중지됨", "Failed" => "실패", _ => st.Phase,
        };
        CtStatusText = $"{phase} · {cycles} · 경과 {st.ElapsedSeconds:F1}s · 이동 {st.LastMoveSeconds:F2}s (최소 {st.MinMoveSeconds:F2} / 최대 {st.MaxMoveSeconds:F2}) · 최대 위치오차 {st.MaxPositionErrorSteps} step"
                       + (st.Error is null ? "" : $"\n오류: {st.Error}");

        // 종료 시 한 번만 로그
        if (!st.Running && st.Phase is "Completed" or "Stopped" or "Failed" && _ctLastReportedPhase != st.Phase + st.FinishedUtc)
        {
            _ctLastReportedPhase = st.Phase + st.FinishedUtc;
            AppendLog(st.Phase == "Failed" ? LogLevel.Warning : LogLevel.Information,
                $"[{st.AxisName}] 왕복 테스트 {phase}: {cycles}, 경과 {st.ElapsedSeconds:F1}s, 이동 최소/최대 {st.MinMoveSeconds:F2}/{st.MaxMoveSeconds:F2}s, 최대 위치오차 {st.MaxPositionErrorSteps} step" + (st.Error is null ? "" : $" — {st.Error}"));
        }
    }

    // ------------------------------------------------------------ drive settings panel

    private uint _dsGearA = 1;
    public uint DsGearA { get => _dsGearA; set { if (SetProperty(ref _dsGearA, value)) OnDsPreviewChanged(); } }

    private uint _dsGearB = 1;
    public uint DsGearB { get => _dsGearB; set { if (SetProperty(ref _dsGearB, value)) OnDsPreviewChanged(); } }

    private short _dsGearRatioRaw;
    public short DsGearRatioRaw { get => _dsGearRatioRaw; set { if (SetProperty(ref _dsGearRatioRaw, value)) OnDsPreviewChanged(); } }

    private double _dsLeadUm = 10_000;
    public double DsLeadUm { get => _dsLeadUm; set { if (SetProperty(ref _dsLeadUm, value)) OnDsPreviewChanged(); } }

    private double _dsPulsesPerRev = DriveMechanics.AzdBaseStepsPerRev;
    /// <summary>분해능 기준값: AZ 는 10,000 고정, VPH-HD 는 엔코더 분해능(편집 가능).</summary>
    public double DsPulsesPerRev { get => _dsPulsesPerRev; set { if (SetProperty(ref _dsPulsesPerRev, value)) OnDsPreviewChanged(); } }

    private string _dsProfile = "";
    public string DsProfile { get => _dsProfile; private set => SetProperty(ref _dsProfile, value); }

    private string _dsGearALabel = "전자기어 A";
    public string DsGearALabel { get => _dsGearALabel; private set => SetProperty(ref _dsGearALabel, value); }

    private string _dsGearBLabel = "전자기어 B";
    public string DsGearBLabel { get => _dsGearBLabel; private set => SetProperty(ref _dsGearBLabel, value); }

    private string _dsFormula = "";
    public string DsFormula { get => _dsFormula; private set => SetProperty(ref _dsFormula, value); }

    private bool _dsShowGearRatio = true;
    public bool DsShowGearRatio { get => _dsShowGearRatio; private set => SetProperty(ref _dsShowGearRatio, value); }

    private bool _dsShowPulses;
    public bool DsShowPulses { get => _dsShowPulses; private set => SetProperty(ref _dsShowPulses, value); }

    private bool _dsMaintenanceEnabled = true;
    public bool DsMaintenanceEnabled { get => _dsMaintenanceEnabled; private set => SetProperty(ref _dsMaintenanceEnabled, value); }

    private string _dsPosUnit = "step", _dsVelUnit = "Hz", _dsAccUnit = "step/s²";
    public string DsPosUnit { get => _dsPosUnit; private set => SetProperty(ref _dsPosUnit, value); }
    public string DsVelUnit { get => _dsVelUnit; private set => SetProperty(ref _dsVelUnit, value); }
    public string DsAccUnit { get => _dsAccUnit; private set => SetProperty(ref _dsAccUnit, value); }

    private uint _dsVelocity;
    public uint DsVelocity { get => _dsVelocity; set { if (SetProperty(ref _dsVelocity, value)) OnPropertyChanged(nameof(DsVelocityRpmText)); } }

    private uint _dsAcceleration;
    public uint DsAcceleration { get => _dsAcceleration; set => SetProperty(ref _dsAcceleration, value); }

    private uint _dsDeceleration;
    public uint DsDeceleration { get => _dsDeceleration; set => SetProperty(ref _dsDeceleration, value); }

    private string _dsSource = "";
    public string DsSource { get => _dsSource; private set => SetProperty(ref _dsSource, value); }

    private string _dsMessage = "";
    public string DsMessage { get => _dsMessage; private set => SetProperty(ref _dsMessage, value); }

    /// <summary>입력 중인 값으로 계산한 분해능과 step/µm (드라이브에 쓰기 전 미리보기).</summary>
    public string DsResolutionText
    {
        get
        {
            double stepsPerRev = Math.Max(1, DsPulsesPerRev) * (double)DsGearB / Math.Max(1u, DsGearA);
            double gear = DsShowGearRatio && DsGearRatioRaw > 0 ? DsGearRatioRaw / 100.0 : 1.0;
            double stepsPerUm = DsLeadUm > 0 ? stepsPerRev * gear / DsLeadUm : double.NaN;
            string u = DsPosUnit;
            return $"분해능 {stepsPerRev:0.##} {u}/rev" + (DsShowGearRatio ? $" · 기어비 {gear:0.##}" : "") +
                   $" · {stepsPerUm:0.####} {u}/µm  (1 µm = {1 / stepsPerUm:0.###} {u})";
        }
    }

    /// <summary>드라이브 설정 패널의 기본 속도(6081h, 원단위)를 입력 중인 분해능·기어비로 환산한 출력축 회전수.</summary>
    public string DsVelocityRpmText
    {
        get
        {
            double stepsPerRev = Math.Max(1, DsPulsesPerRev) * (double)DsGearB / Math.Max(1u, DsGearA);
            double gear = DsShowGearRatio && DsGearRatioRaw > 0 ? DsGearRatioRaw / 100.0 : 1.0;
            return $"기본 속도 {DsVelocity} {DsVelUnit} = {DsVelocity * 60.0 / (stepsPerRev * gear):0.#} rpm (출력축)";
        }
    }

    private void OnDsPreviewChanged()
    {
        OnPropertyChanged(nameof(DsResolutionText));
        OnPropertyChanged(nameof(DsVelocityRpmText));
    }

    private void LoadDriveSettingsFields()
    {
        var axis = SelectedAxis?.Axis;
        if (axis is null) { DsSource = ""; DsProfile = ""; return; }
        var s = axis.GetDriveSettings();
        DsProfile = s.Profile;
        DsGearALabel = s.GearALabel;
        DsGearBLabel = s.GearBLabel;
        DsFormula = s.ResolutionFormula;
        DsShowGearRatio = s.SupportsGearRatio;
        DsShowPulses = s.NeedsPulsesPerRev;
        DsMaintenanceEnabled = s.SupportsMaintenanceCommands;
        DsPosUnit = s.PositionUnit;
        DsVelUnit = s.VelocityUnit;
        DsAccUnit = s.AccelerationUnit;
        DsPulsesPerRev = s.BaseStepsPerRev;
        DsGearA = s.ElectronicGearA;
        DsGearB = s.ElectronicGearB;
        DsGearRatioRaw = s.GearRatioRaw;
        DsLeadUm = s.LeadUm;
        DsVelocity = s.ProfileVelocity;
        DsAcceleration = s.ProfileAcceleration;
        DsDeceleration = s.ProfileDeceleration;
        DsSource = $"프로파일 {s.Profile} · " + (s.ReadFromDrive ? "전자기어: 드라이브에서 읽음" : "전자기어: 기본값 (드라이브 읽기 실패)")
                   + " · 리드" + (s.NeedsPulsesPerRev ? "·엔코더 분해능" : "") + ": 소프트웨어 설정";
    }

    private async Task ReadDriveSettingsAsync()
    {
        if (_master is null || SelectedAxis is null) return;
        int id = SelectedAxis.Axis.Id;
        var r = await Task.Run(() => _master.ReadDriveSettings(id));
        LoadDriveSettingsFields();
        DsMessage = string.Join("\n", r.Messages);
        AppendLog(r.Success ? LogLevel.Information : LogLevel.Warning, $"[Axis{id}] 드라이브 설정 읽기: {string.Join(" | ", r.Messages)}");
    }

    private async Task WriteDriveSettingsAsync(bool runConfiguration, bool saveToNvm)
    {
        if (_master is null || SelectedAxis is null) return;
        int id = SelectedAxis.Axis.Id;
        var cur = SelectedAxis.Axis.GetDriveSettings();

        // 바뀐 항목만 보낸다
        var req = new DriveSettingsWrite(
            ElectronicGearA: DsGearA != cur.ElectronicGearA ? DsGearA : null,
            ElectronicGearB: DsGearB != cur.ElectronicGearB ? DsGearB : null,
            GearRatioRaw: cur.SupportsGearRatio && DsGearRatioRaw != cur.GearRatioRaw ? DsGearRatioRaw : null,
            LeadUm: Math.Abs(DsLeadUm - cur.LeadUm) > 1e-9 ? DsLeadUm : null,
            PulsesPerRev: cur.NeedsPulsesPerRev && Math.Abs(DsPulsesPerRev - cur.BaseStepsPerRev) > 1e-9 ? DsPulsesPerRev : null,
            ProfileVelocity: DsVelocity != cur.ProfileVelocity ? DsVelocity : null,
            ProfileAcceleration: DsAcceleration != cur.ProfileAcceleration ? DsAcceleration : null,
            ProfileDeceleration: DsDeceleration != cur.ProfileDeceleration ? DsDeceleration : null,
            RunConfiguration: runConfiguration,
            SaveToNvm: saveToNvm);

        // VPH 엔코더 분해능은 devices.json 에도 저장해 다음 연결 때 쓰이게 한다
        if (req.PulsesPerRev is > 0)
        {
            _devices ??= DeviceConfigFile.LoadOrCreateDefault(out _);
            _devices.SetAxisPulsesPerRev(SelectedAxis.Axis.Id, req.PulsesPerRev.Value);
            _devices.Save();
        }

        var r = await Task.Run(() => _master.WriteDriveSettings(id, req));
        LoadDriveSettingsFields();
        DsMessage = r.Messages.Count == 0 ? "변경된 항목이 없습니다" : string.Join("\n", r.Messages);
        AppendLog(r.Success ? LogLevel.Information : LogLevel.Warning, $"[Axis{id}] 드라이브 설정 쓰기: {string.Join(" | ", r.Messages)}");
    }

    // ------------------------------------------------------------ SDO (디버그)

    private int _sdoSlave = 1;
    public int SdoSlave { get => _sdoSlave; set => SetProperty(ref _sdoSlave, value); }

    private string _sdoIndexHex = "6061";
    public string SdoIndexHex { get => _sdoIndexHex; set => SetProperty(ref _sdoIndexHex, value); }

    private int _sdoSubIndex;
    public int SdoSubIndex { get => _sdoSubIndex; set => SetProperty(ref _sdoSubIndex, value); }

    public int[] SdoSizes { get; } = [1, 2, 4];

    private int _sdoSize = 1;
    public int SdoSize { get => _sdoSize; set => SetProperty(ref _sdoSize, value); }

    private string _sdoValueText = "0";
    /// <summary>10진수 또는 0x 접두 16진수.</summary>
    public string SdoValueText { get => _sdoValueText; set => SetProperty(ref _sdoValueText, value); }

    private string _sdoResult = "";
    public string SdoResultText { get => _sdoResult; private set => SetProperty(ref _sdoResult, value); }

    // ------------------------------------------------------------ commands

    // CommunityToolkit.Mvvm 커맨드는 CanExecute 를 자동 재평가하지 않으므로(WPF CommandManager 미사용)
    // 상태가 바뀔 때와 100 ms 갱신 주기에 RefreshCommandStates() 로 전부 NotifyCanExecuteChanged 한다.
    private readonly IDialogService _dialogs;
    private IRelayCommand[]? _allCommands;

    private void RefreshCommandStates()
    {
        _allCommands ??= GetType().GetProperties()
            .Where(p => typeof(IRelayCommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.GetValue(this)).OfType<IRelayCommand>().ToArray();
        foreach (var c in _allCommands) c.NotifyCanExecuteChanged();
    }

    public IRelayCommand RefreshAdaptersCommand { get; }
    public IRelayCommand ConnectCommand { get; }
    public IRelayCommand DisconnectCommand { get; }
    public IRelayCommand EnableAllCommand { get; }
    public IRelayCommand DisableAllCommand { get; }
    public IRelayCommand FaultResetAllCommand { get; }
    public IRelayCommand StopAllCommand { get; }
    public IRelayCommand QuickStopAllCommand { get; }
    public IRelayCommand EnableCommand { get; }
    public IRelayCommand DisableCommand { get; }
    public IRelayCommand FaultResetCommand { get; }
    public IRelayCommand MoveAbsoluteCommand { get; }
    public IRelayCommand MoveRelativeCommand { get; }
    public IRelayCommand JogPlusCommand { get; }
    public IRelayCommand JogMinusCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand QuickStopCommand { get; }
    public IRelayCommand HomeCommand { get; }
    public IRelayCommand ClearLogCommand { get; }
    public IRelayCommand ResetStatsCommand { get; }
    public IRelayCommand SdoReadCommand { get; }
    public IRelayCommand SdoWriteCommand { get; }
    public IRelayCommand ReadDriveSettingsCommand { get; }
    public IRelayCommand WriteDriveSettingsCommand { get; }
    public IRelayCommand RunConfigurationCommand { get; }
    public IRelayCommand SaveNvmCommand { get; }
    public IRelayCommand EnableAllInterlocksCommand { get; }
    public IRelayCommand AddInterlockCommand { get; }
    public IRelayCommand EditInterlockCommand { get; }
    public IRelayCommand DeleteInterlockCommand { get; }
    /// <summary>인터록 행 더블클릭 (DataGridBehaviors.DoubleClickCommand, 파라미터 = 행).</summary>
    public IRelayCommand EditInterlockRowCommand { get; }
    /// <summary>IO 그룹을 모달리스 창으로 (파라미터 = IoGroupViewModel).</summary>
    public IRelayCommand OpenIoGroupWindowCommand { get; }
    public IRelayCommand SaveAxisAliasCommand { get; }
    public IRelayCommand OpenDeviceConfigCommand { get; }
    public IRelayCommand CtStartCommand { get; }
    public IRelayCommand CtStopCommand { get; }
    public IRelayCommand CtSetAFromCurrentCommand { get; }
    public IRelayCommand CtSetBFromCurrentCommand { get; }
    public IRelayCommand EtStartCommand { get; }
    public IRelayCommand EtStopCommand { get; }
    public IRelayCommand EtOpenFolderCommand { get; }
    /// <summary>창 Loaded 에서 실행 (WindowBehaviors.LoadedCommand): 시작 시 자동 연결.</summary>
    public IRelayCommand AutoConnectCommand { get; }

    private RelayCommand AxisCmd(Func<Axis, AxisCommand> factory) => new(() =>
    {
        var axis = SelectedAxis?.Axis;
        if (axis is null) return;
        var cmd = factory(axis);
        axis.Submit(cmd);
        string unit = axis.Mechanics.UnitName;
        AppendLog(LogLevel.Information, $"[{axis.Name}] {cmd.Kind}" + cmd.Kind switch
        {
            AxisCommandKind.MoveAbsolute or AxisCommandKind.MoveRelative =>
                $" {TargetPosition} {unit} → {cmd.Position} step, vel {(cmd.Velocity > 0 ? cmd.Velocity : axis.Defaults.Velocity)} Hz, acc {(cmd.Acceleration > 0 ? cmd.Acceleration : axis.Defaults.Acceleration)} step/s²",
            AxisCommandKind.Jog => $" {cmd.TargetVelocity} Hz",
            _ => "",
        });
    }, () => IsConnected && SelectedAxis is not null);

    // ------------------------------------------------------------ actions

    // ---------------- 시작 시 자동 연결: NIC 를 탐색해 EtherCAT 슬레이브가 있는 포트에 연결 ----------------

    private bool _autoConnect = true;
    /// <summary>프로그램 시작 시 NIC 를 탐색해 슬레이브가 있는 포트에 자동 연결 (여러 개면 선택 창).</summary>
    public bool AutoConnect { get => _autoConnect; set { if (SetProperty(ref _autoConnect, value)) SaveSettings(); } }

    private bool _isProbing;
    public bool IsProbing { get => _isProbing; private set => SetProperty(ref _isProbing, value); }

    /// <summary>
    /// 창이 뜬 뒤 호출. 네이티브(SOEM)가 있고 자동 연결이 켜져 있으면 후보 NIC 를 차례로 탐색해
    /// 슬레이브가 있는 NIC 가 1개면 바로 연결, 여러 개면 <see cref="NicPickerWindow"/> 로 고르게 하고, 없으면 그대로 둔다.
    /// </summary>
    public async Task AutoConnectAsync()
    {
        if (!NativeAvailable) { AppendLog(LogLevel.Information, "자동 연결 건너뜀: SOEM 네이티브 없음 (시뮬레이션만 가능)"); return; }
        if (!AutoConnect) { AppendLog(LogLevel.Information, "자동 연결 꺼짐 (상단 '시작 시 자동 연결' 체크로 켤 수 있음)"); return; }
        if (IsConnected || IsBusy) return;

        var candidates = Adapters.Where(SoemAdapters.IsProbeCandidate).ToList();
        if (candidates.Count == 0) { AppendLog(LogLevel.Warning, "자동 연결: 탐색할 NIC 이 없음"); return; }

        IsBusy = true; IsProbing = true;
        ConnectionText = "EtherCAT 슬레이브 탐색 중…";
        AppendLog(LogLevel.Information, $"자동 연결: NIC {candidates.Count}개에서 EtherCAT 슬레이브 탐색 시작");
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        IReadOnlyList<AdapterProbe> results;
        try
        {
            results = await Task.Run(() => SoemAdapters.ProbeAll(candidates, r =>
                dispatcher.BeginInvoke(() =>
                {
                    ConnectionText = $"탐색 중… {r.Adapter.Description}";
                    AppendLog(r.HasSlaves ? LogLevel.Information : LogLevel.Debug, $"  탐색 {r.Adapter.Description}: {(r.Error is not null ? "실패 — " + r.Error : r.HasSlaves ? $"슬레이브 {r.SlaveCount}개" : "슬레이브 없음")} ({r.Elapsed.TotalMilliseconds:0} ms)");
                })));
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, $"자동 연결 탐색 실패: {ex.Message}");
            IsBusy = false; IsProbing = false; ConnectionText = "연결 안 됨";
            return;
        }
        IsBusy = false; IsProbing = false;

        var hits = results.Where(r => r.HasSlaves).ToList();
        if (hits.Count == 0)
        {
            ConnectionText = "연결 안 됨";
            AppendLog(LogLevel.Warning, "자동 연결: EtherCAT 슬레이브가 있는 NIC 을 찾지 못했습니다. 케이블·드라이브 전원을 확인하거나 수동으로 연결하세요.");
            return;
        }

        AdapterProbe chosen;
        if (hits.Count == 1)
        {
            chosen = hits[0];
            AppendLog(LogLevel.Information, $"자동 연결: {chosen.Summary}");
        }
        else
        {
            AppendLog(LogLevel.Information, $"자동 연결: 슬레이브가 있는 NIC {hits.Count}개 → 선택 창");
            var picked = _dialogs.PickNic(hits, _settings.AdapterName);
            if (picked is null)
            {
                ConnectionText = "연결 안 됨";
                AppendLog(LogLevel.Information, "자동 연결 취소 (NIC 선택 안 함)");
                return;
            }
            chosen = picked;
        }

        SelectedAdapter = Adapters.FirstOrDefault(a => a.Name == chosen.Adapter.Name) ?? chosen.Adapter;
        UseSimulation = false;
        await ConnectAsync();
    }

    private void RefreshAdapters()
    {
        Adapters.Clear();
        try
        {
            foreach (var a in SoemAdapters.List()) Adapters.Add(a);
            SelectedAdapter = Adapters.FirstOrDefault();
            AppendLog(LogLevel.Information, $"NIC {Adapters.Count}개 발견");
            if (Adapters.Count == 0)
                AppendLog(LogLevel.Warning, "NIC 이 없습니다. Npcap 이 설치되어 있는지 확인하세요.");
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, $"NIC 조회 실패: {ex.Message}");
        }
    }

    private async Task ConnectAsync()
    {
        if (!UseSimulation && SelectedAdapter is null)
        {
            AppendLog(LogLevel.Error, "NIC 을 선택하세요.");
            return;
        }

        IsBusy = true;
        ConnectionText = "연결 중…";
        SaveSettings();
        try
        {
            var masterOptions = new MasterOptions
            {
                Mode = UseSimulation ? "Simulated" : "Soem",
                Interface = SelectedAdapter?.Name ?? "",
                CycleTimeUs = CycleTimeUs,
                AxisCount = AxisCount,
                RemapPdo = RemapPdo,
                WriteOptionCodes = false,                       // 드라이브에 설정된 옵션 코드 유지
                CpuCore = CpuCore,
                DcSyncOffsetNs = Math.Max(0, DcSyncOffsetUs) * 1000,
                RealtimePriority = 80,
                Defaults = new MotionDefaults { UseDriveValues = true },  // 속도·가감속은 드라이브 값
                Units = new UnitOptions { Mode = SelectedUnit, DefaultLeadUm = LeadUm },
            };

            // IO 모듈 이름 · 인터록 · 상태 출력은 devices.json 에서
            var devices = DeviceConfigFile.LoadOrCreateDefault(out var cfgError);
            if (cfgError is not null) AppendLog(LogLevel.Warning, cfgError);
            devices.ApplyTo(masterOptions);
            _devices = devices;
            AppendLog(LogLevel.Information, $"장비 구성: 축 별칭 {devices.Axes.Count}개, 인터록 {devices.Interlocks.Count}개, 상태 출력 {devices.StatusOutputs.Count}개 ({DeviceConfigFile.PathOnDisk})");

            var options = Options.Create(masterOptions);

            IEtherCatMaster master = UseSimulation
                ? new SimulatedMaster(options, new UiLogger<SimulatedMaster>(AppendLog))
                : new SoemMaster(options, new UiLogger<SoemMaster>(AppendLog));

            // 예외를 던지지 않는 시작: 실패 시 마스터가 소켓을 정리하고 사유를 돌려준다 (VS 디버거 중단 없음)
            var startError = await master.TryStartAsync(CancellationToken.None);
            if (startError is not null)
            {
                (master as IDisposable)?.Dispose();
                AppendLog(LogLevel.Error, $"연결 실패: {startError}");
                ConnectionText = "연결 실패";
                return;
            }

            _master = master;
            Axes.Clear();
            foreach (var a in master.Axes) Axes.Add(new AxisRowViewModel(a));
            SelectedAxis = Axes.FirstOrDefault();
            IoModules.Clear();
            foreach (var io in master.IoModules) IoModules.Add(new IoModuleViewModel(io, master, RenameIoBit));
            BuildIoGroups();
            Interlocks.Clear();
            foreach (var il in master.GetInterlocks()) Interlocks.Add(new InterlockRowViewModel(il, OnInterlockToggleRequested));

            IsConnected = true;
            ConnectionText = UseSimulation ? "시뮬레이션 연결됨" : $"SOEM 연결됨: {SelectedAdapter!.Description}";
            _timer.Start();
            Refresh();
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, $"연결 실패: {ex.Message}");
            ConnectionText = "연결 실패";
            try { SoemDisposeQuiet(); } catch { /* ignore */ }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            _timer.Stop();
            if (_master is not null)
            {
                foreach (var a in _master.Axes) _cycleRunner.Stop(a.Id);
                await _enduranceRunner.StopAllAsync(TimeSpan.FromSeconds(5));   // 감속 정지 + 요약 기록까지
                _master.StopAll(false);
                await Task.Delay(50);
                await _master.StopAsync(CancellationToken.None);
                (_master as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, $"해제 중 오류: {ex.Message}");
        }
        finally
        {
            _master = null;
            Axes.Clear();
            Slaves.Clear();
            IoGroups.Clear();
            IoModules.Clear();
            Interlocks.Clear();
            InterlockSummary = "";
            AnyInterlockActive = false;
            Master = null;
            IsConnected = false;
            ConnectionText = "연결 안 됨";
            IsBusy = false;
        }
    }

    private void SoemDisposeQuiet()
    {
        if (_master is IDisposable d) d.Dispose();
        _master = null;
    }

    public async Task ShutdownAsync()
    {
        SaveSettings();
        if (IsConnected) await DisconnectAsync();
    }

    private void Refresh()
    {
        RefreshCommandStates();   // 100 ms 마다 CanExecute 재평가 (선택 인터록·왕복 테스트 상태 등)
        if (_master is null) return;

        foreach (var row in Axes) row.Update();
        SelectedAxis?.Update();
        NotifyRpm();   // 드라이브 설정 쓰기·리드 변경 뒤에도 환산값이 따라오도록
        foreach (var io in IoModules) io.Update();
        foreach (var g in IoGroups) g.Update();
        RefreshCycleTest();
        RefreshEndurance();

        var st = _master.GetStatus();
        Master = st;

        // 인터록 상태 갱신 (행 객체는 유지, 바뀐 값만 알림)
        var ils = _master.GetInterlocks();
        if (ils.Count != Interlocks.Count)
        {
            Interlocks.Clear();
            foreach (var il in ils) Interlocks.Add(new InterlockRowViewModel(il, OnInterlockToggleRequested));
        }
        else
        {
            for (int i = 0; i < ils.Count; i++) Interlocks[i].Update(ils[i]);
        }
        // 상태바: 메시지 + 참조 위치 (예: "비상정지 눌림 [DI1.IN0 @13]")
        var active = ils.Where(i => i.Active).Select(i => $"{i.Message ?? i.Name} [{i.ConditionText}]").ToList();
        int disabled = ils.Count(i => !i.Enabled);
        AnyInterlockActive = active.Count > 0;
        InterlockSummary = (active.Count == 0 ? "" : $"인터록 활성: {string.Join(", ", active)}")
                           + (disabled == 0 ? "" : $"{(active.Count == 0 ? "" : "  ·  ")}⚠ 인터록 {disabled}개 해제 중");

        if (Slaves.Count != st.Slaves.Count || !Slaves.SequenceEqual(st.Slaves))
        {
            Slaves.Clear();
            foreach (var s in st.Slaves) Slaves.Add(s);
        }
    }

    private async Task SdoReadAsync()
    {
        if (_master is null || !TryParseSdoAddress(out ushort idx)) return;
        var (slave, sub, size) = (SdoSlave, (byte)SdoSubIndex, SdoSize);
        var r = await Task.Run(() => _master.SdoRead(slave, idx, sub, size));
        SdoResultText = r.Success
            ? $"{idx:X4}h:{sub:X2} = {r.Value} ({r.ValueHex}, {size}B)" + (size >= 2 ? $"  signed={SignedView(r.Value, size)}" : "")
            : $"오류: {r.Error}";
        AppendLog(r.Success ? LogLevel.Information : LogLevel.Warning, $"SDO read slave {slave} {SdoResultText}");
    }

    private async Task SdoWriteAsync()
    {
        if (_master is null || !TryParseSdoAddress(out ushort idx)) return;
        if (!TryParseValue(SdoValueText, out uint value))
        {
            SdoResultText = "값은 10진수 또는 0x 접두 16진수";
            return;
        }
        var (slave, sub, size) = (SdoSlave, (byte)SdoSubIndex, SdoSize);
        var r = await Task.Run(() => _master.SdoWrite(slave, idx, sub, size, value));
        SdoResultText = r.Success ? $"{idx:X4}h:{sub:X2} ← {value} ({r.ValueHex}) 기록 완료" : $"오류: {r.Error}";
        AppendLog(r.Success ? LogLevel.Information : LogLevel.Warning, $"SDO write slave {slave} {SdoResultText}");
    }

    private bool TryParseSdoAddress(out ushort idx)
    {
        var s = SdoIndexHex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.EndsWith('h') || s.EndsWith('H')) s = s[..^1];
        if (ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out idx)) return true;
        SdoResultText = "인덱스는 16진수 4자리 (예: 6061)";
        return false;
    }

    private static bool TryParseValue(string s, out uint v)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out v);
        if (long.TryParse(s, out long l)) { v = unchecked((uint)l); return true; }
        v = 0;
        return false;
    }

    private static long SignedView(uint v, int size) => size switch
    {
        1 => (sbyte)v,
        2 => (short)v,
        _ => (int)v,
    };

    /// <summary>코드비하인드 등 외부에서 진단 로그를 남길 때.</summary>
    public void LogDebug(string message) => AppendLog(LogLevel.Debug, message);

    private static readonly object LogFileLock = new();
    private static readonly string LogDir =
        AppPaths.LogDir;

    /// <summary>로그 파일 경로 (일자별). 실기 문제를 보고할 때 이 파일을 보내면 된다.</summary>
    public static string LogFilePath => System.IO.Path.Combine(LogDir, $"gui-{DateTime.Now:yyyyMMdd}.log");

    private void AppendLog(LogLevel level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{Short(level)}] {message}";

        // 화면 로그와 별도로 파일에도 남긴다 (연결 실패 원인 분석용)
        try
        {
            lock (LogFileLock)
            {
                System.IO.Directory.CreateDirectory(LogDir);
                System.IO.File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch { /* 파일 로그 실패는 무시 */ }

        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) Add(line);
        else app.Dispatcher.BeginInvoke(() => Add(line));

        void Add(string l)
        {
            Log.Add(l);
            while (Log.Count > 500) Log.RemoveAt(0);
        }
        static string Short(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
            LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "   ",
        };
    }
}
