using System.Collections.ObjectModel;
using System.Windows.Input;
using EcatMaster.Core.EtherCat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EcatMaster.Infrastructure;

namespace EcatMaster.ViewModels;

/// <summary>IO 비트 하나 (입력 LED 또는 출력 토글).</summary>
public sealed class IoBitViewModel : ObservableObject
{
    private bool _isOn;
    private bool _isForced;
    private string _name;

    public int Index { get; }
    public bool IsOutput { get; }
    public ICommand ToggleCommand { get; }
    public ICommand RenameCommand { get; }

    public IoBitViewModel(int index, string name, bool isOutput, Action<IoBitViewModel> toggle, Func<bool> canToggle, Action<IoBitViewModel> rename)
    {
        Index = index;
        _name = name;
        IsOutput = isOutput;
        ToggleCommand = new RelayCommand(() => toggle(this), canToggle);
        RenameCommand = new RelayCommand(() => rename(this));
    }

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(Label)); }
    }
    public bool IsOn { get => _isOn; set => SetProperty(ref _isOn, value); }
    public bool IsForced { get => _isForced; set => SetProperty(ref _isForced, value); }
    public string Label => $"{Index}\n{Name}";
}

/// <summary>IO 모듈 한 개의 표시/조작 모델. 100 ms 마다 <see cref="Update"/>.</summary>
public sealed class IoModuleViewModel : ObservableObject
{
    private readonly IEtherCatMaster _master;
    private readonly Action<IoModuleViewModel, IoBitViewModel> _rename;
    private bool _online;

    public IoModule Module { get; }
    public string Name => Module.Name;
    public string Title => $"{Module.Name}  (slave {Module.Slave}, IN {Module.InputBits} / OUT {Module.OutputBits})";
    public ObservableCollection<IoBitViewModel> Inputs { get; } = new();
    public ObservableCollection<IoBitViewModel> Outputs { get; } = new();
    public bool HasInputs => Module.InputBits > 0;
    public bool HasOutputs => Module.OutputBits > 0;
    public bool CanForceInputs => _master.SupportsInputForcing;
    public bool Online { get => _online; private set => SetProperty(ref _online, value); }

    public IoModuleViewModel(IoModule module, IEtherCatMaster master, Action<IoModuleViewModel, IoBitViewModel> rename)
    {
        Module = module;
        _master = master;
        _rename = rename;
        for (int i = 0; i < module.InputBits; i++)
            Inputs.Add(new IoBitViewModel(i, module.InputNames[i], false, ToggleForcedInput, () => _master.SupportsInputForcing, b => _rename(this, b)));
        for (int i = 0; i < module.OutputBits; i++)
            Outputs.Add(new IoBitViewModel(i, module.OutputNames[i], true, ToggleOutput, () => true, b => _rename(this, b)));
        Update();
    }

    private void ToggleOutput(IoBitViewModel bit) => Module.SetOutput(bit.Index, !Module.GetOutput(bit.Index));

    /// <summary>시뮬레이션: 클릭할 때마다 강제값을 반전. 강제가 없던 비트는 현재 값의 반대로 강제.</summary>
    private void ToggleForcedInput(IoBitViewModel bit) => Module.ForceInput(bit.Index, !Module.GetInput(bit.Index));

    public void Update()
    {
        var s = Module.GetStatus();
        Online = s.Online;
        for (int i = 0; i < Inputs.Count; i++)
        {
            Inputs[i].IsOn = s.InputStates[i];
            Inputs[i].IsForced = s.ForcedInputs[i];
            if (Inputs[i].Name != s.InputNames[i]) Inputs[i].Name = s.InputNames[i];
        }
        for (int i = 0; i < Outputs.Count; i++)
        {
            Outputs[i].IsOn = s.OutputStates[i];
            if (Outputs[i].Name != s.OutputNames[i]) Outputs[i].Name = s.OutputNames[i];
        }
    }

    public ICommand ClearForcedCommand => new RelayCommand(Module.ClearForcedInputs, () => _master.SupportsInputForcing);
}
