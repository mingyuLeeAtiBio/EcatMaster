using EcatMaster.Core.EtherCat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EcatMaster.Infrastructure;

namespace EcatMaster.ViewModels;

/// <summary>DataGrid 한 행. 100ms 마다 <see cref="Update"/> 로 갱신된다. 위치/속도는 현재 사용자 단위로 표시.</summary>
public sealed class AxisRowViewModel : ObservableObject
{
    private AxisStatus _s;

    public Axis Axis { get; }

    public AxisRowViewModel(Axis axis)
    {
        Axis = axis;
        _s = axis.GetStatus();
    }

    public int Id => _s.Id;
    public string Name => _s.Name;
    public string Profile => _s.Profile;
    public bool Online => _s.Online;
    public string State => _s.State;
    public bool Enabled => _s.Enabled;
    public bool Fault => _s.Fault;
    public string Mode => _s.Mode;
    public string Activity => _s.Activity;
    public int Position => _s.Position;
    public int Velocity => _s.Velocity;
    public int TargetPosition => _s.TargetPosition;
    public bool TargetReached => _s.TargetReached;
    public string StatusWordHex => $"0x{_s.StatusWord:X4}";
    public string ControlWordHex => $"0x{_s.ControlWord:X4}";
    public string ErrorCodeHex => _s.ErrorCode == 0 ? "" : $"0x{_s.ErrorCode:X4}";
    public string DigitalInputsHex => $"0x{_s.DigitalInputs:X8}";
    public string? LastError => _s.LastError;
    public bool MotionBlocked => _s.MotionBlocked;
    public string? BlockReason => _s.BlockReason;

    public string Unit => _s.Unit;
    private bool IsUm => _s.Unit == "um";
    public string PositionText => IsUm ? _s.PositionUser.ToString("F1") : _s.Position.ToString();
    public string VelocityText => IsUm ? _s.VelocityUser.ToString("F1") : _s.Velocity.ToString();
    public string RpmText => _s.VelocityRpm.ToString("F1");
    public string TargetText => IsUm ? _s.TargetPositionUser.ToString("F1") : _s.TargetPosition.ToString();

    /// <summary>선택 축 상세 패널용: 상태 워드 비트 해석.</summary>
    public string StatusBits
    {
        get
        {
            ushort sw = _s.StatusWord;
            var parts = new List<string>();
            void B(int bit, string name) { if ((sw & (1 << bit)) != 0) parts.Add(name); }
            B(0, "ReadyToSwitchOn"); B(1, "SwitchedOn"); B(2, "OperationEnabled"); B(3, "Fault");
            B(4, "VoltageEnabled"); B(5, "QuickStop(1=inactive)"); B(6, "SwitchOnDisabled"); B(7, "Warning");
            B(9, "Remote"); B(10, "TargetReached"); B(11, "InternalLimit");
            B(12, _s.Mode switch { "ProfilePosition" => "SetPointAck", "Homing" => "HomingAttained", "ProfileVelocity" => "SpeedZero", _ => "Bit12" });
            B(13, _s.Mode switch { "ProfilePosition" => "FollowingError", "Homing" => "HomingError", _ => "Bit13" });
            return string.Join(", ", parts);
        }
    }

    public void Update()
    {
        var n = Axis.GetStatus();
        if (n == _s) return;   // record 값 비교: 변화 없으면 알림 생략
        _s = n;
        OnPropertyChanged(string.Empty);   // 모든 바인딩 갱신
    }
}
