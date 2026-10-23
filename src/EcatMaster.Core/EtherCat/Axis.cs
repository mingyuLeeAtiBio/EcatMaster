namespace EcatMaster.Core.EtherCat;

public sealed record AxisStatus(
    int Id,
    string Name,
    int Slave,
    string Profile,
    bool Online,
    string State,
    bool Enabled,
    bool Fault,
    string Mode,
    string Activity,
    int Position,
    int Velocity,
    int TargetPosition,
    bool TargetReached,
    ushort StatusWord,
    ushort ControlWord,
    ushort ErrorCode,
    uint DigitalInputs,
    string? LastError,
    bool MotionBlocked,
    string? BlockReason,
    // ---- 사용자 단위 ----
    string Unit,
    double StepsPerUnit,
    double PositionUser,
    double VelocityUser,
    double TargetPositionUser,
    /// <summary>실속도를 출력축 회전수로 환산 [r/min] (분해능·기어비 반영).</summary>
    double VelocityRpm);

/// <summary>
/// 한 축의 CiA402 상태기계 + PP/PV/Homing 시퀀스.
/// <see cref="Tick"/> 은 실시간 스레드에서 매 사이클 호출되고, 그 외 메서드는 API 스레드에서 호출된다.
/// 두 스레드 사이의 공유는 <see cref="Submit"/> 의 Interlocked 교환과 상태 필드 읽기(모니터링용, 약간의 찢어진 읽기 허용)만이다.
/// 내부 단위는 항상 드라이브 원단위(step, step/s, step/s²)이며, 사용자 단위 변환은 <see cref="Mechanics"/> 로 경계에서만 한다.
/// 벤더 차이(PV 지원, 가감속 전달 경로)는 <see cref="Profile"/> 로 처리한다.
/// </summary>
public sealed class Axis
{
    private enum Phase
    {
        Idle,
        PpArm,        // 모드 전환·가감속 SDO 동기 확인 + 이전 ack 해제 대기
        PpStart,      // bit4 세우고 ack(bit12) 대기
        PpMoving,     // bit4 내리고 target reached 대기
        Jogging,      // PV 모드 속도 유지
        HomingArm,    // 모드 전환 확인
        HomingStart,  // bit4 세우고 homing attained 대기
    }

    /// <summary>PV 미지원 드라이브의 조그: 이 거리만큼 상대 이동을 걸고 떼면 Halt.</summary>
    private const int JogPpDistance = 200_000_000;

    private AxisCommand? _pending;
    private bool _enableWanted;
    private bool _quickStop;
    private bool _halt;
    private bool _relative;
    private bool _jogPp;
    private int _faultResetTicks;
    private Phase _phase;
    private int _phaseTicks;

    // 가감속을 SDO 로 보내야 하는 프로파일용 동기 상태
    private volatile bool _profileSyncPending;
    private uint _syncedAcc, _syncedDec;

    /// <summary>마스터 → 드라이브. Tick 이 채우고 마스터가 IOmap 으로 복사.</summary>
    public AzdRxPdo Rx;

    /// <summary>드라이브 → 마스터. 마스터가 IOmap 에서 복사한 뒤 Tick 호출.</summary>
    public AzdTxPdo Tx;

    public int Id { get; }

    /// <summary>축 별칭 (GUI 표시). 런타임 변경 가능.</summary>
    public string Name { get; set; }
    public int Slave { get; }
    public IDriveProfile Profile { get; }
    public bool Online { get; set; }
    public Cia402State State { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>마지막 오류 지우기 (명령을 새로 보내기 전, 이전 거부 사유가 결과 판정에 섞이지 않게). API 스레드용.</summary>
    public void ClearLastError() => LastError = null;

    private volatile bool _motionBlocked;
    private string? _blockReason;

    /// <summary>인터록 활성으로 새 동작(서보 온·이동·조그·원점복귀)이 차단된 상태. 마스터가 매 사이클 갱신.</summary>
    public bool MotionBlocked => _motionBlocked;
    public string? BlockReason => _blockReason;

    public void SetMotionBlocked(bool blocked, string? reason)
    {
        _blockReason = blocked ? reason : null;
        _motionBlocked = blocked;
    }

    /// <summary>이동·조그·원점복귀 시퀀스가 진행 중인지 (상태 출력용).</summary>
    public bool IsMotionActive => _phase != Phase.Idle && State == Cia402State.OperationEnabled;

    /// <summary>이 축의 프로파일 기본값 (연결 시 드라이브 값으로 덮어쓰일 수 있음). API 스레드에서 수정 가능.</summary>
    public MotionDefaults Defaults { get; }

    /// <summary>기구 상수와 단위 변환.</summary>
    public DriveMechanics Mechanics { get; }

    public Axis(int id, string name, int slave, MotionDefaults defaultsTemplate, UnitOptions units, IDriveProfile profile)
    {
        Id = id;
        Name = name;
        Slave = slave;
        Profile = profile;
        Defaults = defaultsTemplate.Clone();
        Mechanics = new DriveMechanics { LeadUm = units.LeadFor(id), Unit = units.UnitMode };

        Rx.ModesOfOperation = (sbyte)OperationMode.ProfilePosition;
        Rx.ProfileVelocity = Defaults.Velocity;
        Rx.ProfileAcceleration = Defaults.Acceleration;
        Rx.ProfileDeceleration = Defaults.Deceleration;
        Rx.ControlWord = Cia402.CmdShutdown;
        _syncedAcc = Defaults.Acceleration;
        _syncedDec = Defaults.Deceleration;
    }

    // ---------------- 가감속 SDO 동기 (AccelerationViaPdo == false 프로파일) ----------------

    /// <summary>true 면 마스터 감시 스레드가 Rx 의 6083h/6084h 를 SDO 로 써 준 뒤 <see cref="MarkProfileSynced"/> 를 호출해야 한다.</summary>
    public bool ProfileSyncPending => _profileSyncPending;

    public void MarkProfileSynced(uint acc, uint dec)
    {
        _syncedAcc = acc;
        _syncedDec = dec;
        _profileSyncPending = false;
    }

    private void RequestProfileSyncIfNeeded()
    {
        if (Profile.AccelerationViaPdo) return;
        if (Rx.ProfileAcceleration != _syncedAcc || Rx.ProfileDeceleration != _syncedDec) _profileSyncPending = true;
    }

    // ---------------- API 스레드 ----------------

    public void Submit(AxisCommand cmd) => Interlocked.Exchange(ref _pending, cmd);

    /// <summary>사용자 단위 값을 step 으로 변환해 이동 명령을 만든다. 생략한 프로파일 값은 <see cref="Defaults"/>.</summary>
    public AxisCommand MakeMove(bool relative, double position, double? velocity, double? acceleration, double? deceleration, UserUnit? unit = null)
    {
        var m = Mechanics;
        return new AxisCommand(
            relative ? AxisCommandKind.MoveRelative : AxisCommandKind.MoveAbsolute,
            Position: m.UserToSteps(position, unit),
            Velocity: velocity is > 0 ? m.UserToStepsUnsigned(velocity.Value, unit) : 0,
            Acceleration: acceleration is > 0 ? m.UserToStepsUnsigned(acceleration.Value, unit) : 0,
            Deceleration: deceleration is > 0 ? m.UserToStepsUnsigned(deceleration.Value, unit) : 0);
    }

    /// <summary>사용자 단위 속도로 조그 명령. 부호가 방향.</summary>
    public AxisCommand MakeJog(double velocity, double? acceleration, double? deceleration, UserUnit? unit = null)
    {
        var m = Mechanics;
        return new AxisCommand(
            AxisCommandKind.Jog,
            Acceleration: acceleration is > 0 ? m.UserToStepsUnsigned(acceleration.Value, unit) : 0,
            Deceleration: deceleration is > 0 ? m.UserToStepsUnsigned(deceleration.Value, unit) : 0,
            TargetVelocity: m.UserToSteps(velocity, unit));
    }

    public AxisStatus GetStatus()
    {
        var tx = Tx;
        var rx = Rx;
        var m = Mechanics;
        return new AxisStatus(
            Id, Name, Slave, Profile.Name, Online,
            State.ToString(),
            Enabled: State == Cia402State.OperationEnabled,
            Fault: State is Cia402State.Fault or Cia402State.FaultReactionActive,
            Mode: ((OperationMode)tx.ModesOfOperationDisplay).ToString(),
            Activity: _phase switch
            {
                Phase.Idle => _halt ? "Halted" : "Idle",
                Phase.PpArm or Phase.PpStart or Phase.PpMoving => _jogPp ? "Jogging" : "Moving",
                Phase.Jogging => "Jogging",
                Phase.HomingArm or Phase.HomingStart => "Homing",
                _ => _phase.ToString(),
            },
            Position: tx.PositionActual,
            Velocity: tx.VelocityActual,
            TargetPosition: rx.TargetPosition,
            TargetReached: (tx.StatusWord & Cia402.SwTargetReached) != 0,
            StatusWord: tx.StatusWord,
            ControlWord: rx.ControlWord,
            ErrorCode: tx.ErrorCode,
            DigitalInputs: tx.DigitalInputs,
            LastError,
            MotionBlocked: _motionBlocked,
            BlockReason: _blockReason,
            Unit: m.UnitName,
            StepsPerUnit: m.Unit == UserUnit.Um ? m.StepsPerUm : 1.0,
            PositionUser: m.StepsToUser(tx.PositionActual),
            VelocityUser: m.StepsToUser(tx.VelocityActual),
            TargetPositionUser: m.StepsToUser(rx.TargetPosition),
            VelocityRpm: m.StepsPerSecToRpm(tx.VelocityActual));
    }

    public DriveSettings GetDriveSettings()
    {
        var m = Mechanics;
        var d = Defaults;
        var p = Profile;
        return new DriveSettings(Id, Slave, p.Name, m.ReadFromDrive, m.ElectronicGearA, m.ElectronicGearB,
            p.GearALabel, p.GearBLabel, p.ResolutionFormula, m.BaseStepsPerRev, p.NeedsPulsesPerRev, m.StepsPerRev,
            m.GearRatioRaw, m.GearRatio, p.SupportsGearRatioObject, p.SupportsMaintenanceCommands,
            m.LeadUm, m.StepsPerUm, m.UnitName, p.PositionUnit, p.VelocityUnit, p.AccelerationUnit,
            d.Velocity, d.Acceleration, d.Deceleration);
    }

    // ---------------- 실시간 스레드 ----------------

    /// <summary>매 사이클 호출. Tx 는 이미 최신, 결과는 Rx 에 남는다. 할당 없음.</summary>
    public void Tick()
    {
        State = Cia402.Decode(Tx.StatusWord);

        var cmd = Interlocked.Exchange(ref _pending, null);
        if (cmd is not null) Apply(cmd);

        ushort cw;

        if (State is Cia402State.Fault or Cia402State.FaultReactionActive)
        {
            _phase = Phase.Idle;
            cw = Cia402.CmdDisableVoltage;
            if (_faultResetTicks > 0)
            {
                cw |= Cia402.CwFaultReset;   // 상승 에지가 필요하므로 몇 사이클 유지 후 내림
                _faultResetTicks--;
            }
        }
        else if (_quickStop)
        {
            if (State is Cia402State.OperationEnabled or Cia402State.QuickStopActive)
            {
                cw = Cia402.CmdQuickStop;
            }
            else
            {
                _quickStop = false;          // 정지 완료 → 이후엔 비활성 상태 유지
                cw = Cia402.CmdShutdown;
            }
        }
        else if (!_enableWanted)
        {
            _phase = Phase.Idle;
            cw = Cia402.CmdShutdown;          // Ready to switch on = 모터 무여자
        }
        else
        {
            cw = State switch
            {
                Cia402State.SwitchOnDisabled => Cia402.CmdShutdown,
                Cia402State.ReadyToSwitchOn => Cia402.CmdSwitchOn,
                Cia402State.SwitchedOn => Cia402.CmdEnableOperation,
                Cia402State.OperationEnabled => Cia402.CmdEnableOperation,
                Cia402State.QuickStopActive => Cia402.CmdEnableOperation,
                _ => Cia402.CmdShutdown,
            };

            if (State == Cia402State.OperationEnabled)
                cw = RunMotion(cw);
            else
                _phase = _phase == Phase.Jogging ? Phase.Idle : _phase; // 서보 오프 중엔 조그 취소
        }

        if (_halt) cw |= Cia402.CwHalt;

        Rx.ControlWord = cw;
    }

    private ushort RunMotion(ushort cw)
    {
        ushort sw = Tx.StatusWord;
        var modeDisplay = (OperationMode)Tx.ModesOfOperationDisplay;

        switch (_phase)
        {
            case Phase.PpArm:
                // 가감속을 SDO 로 보내는 드라이브는 감시 스레드가 써 줄 때까지 대기
                if (_profileSyncPending)
                {
                    if (++_phaseTicks > Defaults.AckTimeoutCycles) Fail("profile sync timeout (6083h/6084h SDO)");
                    break;
                }
                // Change-set-immediately 를 못 쓰는 드라이브(VPH-HD)는 진행 중인 위치결정 위에 새 set-point 를 걸면
                // FFF3h 경고로 거부한다 → Halt 를 유지해 정지(Target reached)시킨 뒤에 건다.
                if (!Profile.SupportsChangeSetImmediately && modeDisplay == OperationMode.ProfilePosition
                    && (sw & Cia402.SwTargetReached) == 0)
                {
                    cw |= Cia402.CwHalt;
                    if (++_phaseTicks > Defaults.AckTimeoutCycles) Fail("previous motion did not stop before new set-point");
                    break;
                }
                // 드라이브가 PP 모드를 반영했고, 이전 set-point ack 가 내려간 뒤에만 새 set-point.
                if (modeDisplay == OperationMode.ProfilePosition && (sw & Cia402.SwBit12) == 0)
                {
                    _phase = Phase.PpStart;
                    _phaseTicks = 0;
                }
                else if (++_phaseTicks > Defaults.AckTimeoutCycles)
                {
                    Fail("PP arm timeout (mode not accepted)");
                }
                break;

            case Phase.PpStart:
                cw |= Cia402.CwBit4;
                if (Profile.SupportsChangeSetImmediately) cw |= Cia402.CwBit5;   // VPH-HD 는 Bit5=1 → FFF3h 경고
                if (_relative) cw |= Cia402.CwBit6;
                if ((sw & Cia402.SwBit12) != 0)
                {
                    _phase = Phase.PpMoving;   // ack 받음 → 다음 사이클부터 bit4 내림
                    _phaseTicks = 0;
                }
                else if (++_phaseTicks > Defaults.AckTimeoutCycles)
                {
                    Fail("set-point not acknowledged");
                }
                break;

            case Phase.PpMoving:
                if ((sw & Cia402.SwBit13) != 0)
                {
                    Fail("following error");
                }
                else if ((sw & Cia402.SwBit12) == 0 && (sw & Cia402.SwTargetReached) != 0)
                {
                    _phase = Phase.Idle;
                    _jogPp = false;
                }
                break;

            case Phase.Jogging:
                // PV 모드: TargetVelocity 유지. Halt 명령이 오면 _halt 로 감속.
                break;

            case Phase.HomingArm:
                if (_profileSyncPending)
                {
                    if (++_phaseTicks > Defaults.AckTimeoutCycles) Fail("profile sync timeout (6083h/6084h SDO)");
                    break;
                }
                if (modeDisplay == OperationMode.Homing && (sw & Cia402.SwBit12) == 0)
                {
                    _phase = Phase.HomingStart;
                    _phaseTicks = 0;
                }
                else if (++_phaseTicks > Defaults.AckTimeoutCycles)
                {
                    Fail("homing mode not accepted");
                }
                break;

            case Phase.HomingStart:
                cw |= Cia402.CwBit4;
                if ((sw & Cia402.SwBit13) != 0)
                {
                    Fail("homing error");
                }
                else if ((sw & Cia402.SwBit12) != 0 && (sw & Cia402.SwTargetReached) != 0)
                {
                    _phase = Phase.Idle;       // homing attained → bit4 내림
                }
                break;
        }

        return cw;
    }

    private void Apply(AxisCommand cmd)
    {
        // 인터록 활성 중에는 새 동작을 시작하는 명령을 거부한다. 정지·서보오프·알람리셋은 항상 허용.
        if (_motionBlocked && cmd.Kind is AxisCommandKind.Enable or AxisCommandKind.MoveAbsolute or AxisCommandKind.MoveRelative
                                          or AxisCommandKind.Jog or AxisCommandKind.Home)
        {
            LastError = $"interlock: {_blockReason}";
            return;
        }

        switch (cmd.Kind)
        {
            case AxisCommandKind.Enable:
                _enableWanted = true;
                _quickStop = false;
                _halt = false;
                LastError = null;
                break;

            case AxisCommandKind.Disable:
                _enableWanted = false;
                _phase = Phase.Idle;
                _jogPp = false;
                break;

            case AxisCommandKind.FaultReset:
                _faultResetTicks = 5;
                _enableWanted = false;
                LastError = null;
                break;

            case AxisCommandKind.MoveAbsolute:
            case AxisCommandKind.MoveRelative:
                if (!RequireEnabled()) break;
                Rx.TargetPosition = cmd.Position;
                Rx.ProfileVelocity = cmd.Velocity > 0 ? cmd.Velocity : Defaults.Velocity;
                Rx.ProfileAcceleration = cmd.Acceleration > 0 ? cmd.Acceleration : Defaults.Acceleration;
                Rx.ProfileDeceleration = cmd.Deceleration > 0 ? cmd.Deceleration : Defaults.Deceleration;
                Rx.ModesOfOperation = (sbyte)OperationMode.ProfilePosition;
                RequestProfileSyncIfNeeded();
                _relative = cmd.Kind == AxisCommandKind.MoveRelative;
                _jogPp = false;
                _halt = false;
                _phase = Phase.PpArm;
                _phaseTicks = 0;
                LastError = null;
                break;

            case AxisCommandKind.Jog:
                if (!RequireEnabled()) break;
                Rx.ProfileAcceleration = cmd.Acceleration > 0 ? cmd.Acceleration : Defaults.Acceleration;
                Rx.ProfileDeceleration = cmd.Deceleration > 0 ? cmd.Deceleration : Defaults.Deceleration;
                RequestProfileSyncIfNeeded();
                _halt = false;
                if (Profile.SupportsProfileVelocity)
                {
                    Rx.TargetVelocity = cmd.TargetVelocity;
                    Rx.ModesOfOperation = (sbyte)OperationMode.ProfileVelocity;
                    _phase = Phase.Jogging;
                }
                else
                {
                    // PV 미지원(VPH-HD): 조그 방향으로 큰 상대 이동을 걸고, 떼면 Halt 로 감속 정지
                    // 양쪽 모두 long 으로 계산 (int 로 하면 음의 방향에서 오버플로 → 거리 0 → 조그 - 가 동작하지 않음)
                    long room = cmd.TargetVelocity >= 0
                        ? (long)int.MaxValue - 1000 - Tx.PositionActual
                        : (long)Tx.PositionActual - ((long)int.MinValue + 1000);
                    int dist = (int)Math.Min(JogPpDistance, Math.Max(0, room));
                    Rx.TargetPosition = cmd.TargetVelocity >= 0 ? dist : -dist;
                    Rx.ProfileVelocity = (uint)Math.Abs((long)cmd.TargetVelocity);
                    Rx.ModesOfOperation = (sbyte)OperationMode.ProfilePosition;
                    _relative = true;
                    _jogPp = true;
                    _phase = Phase.PpArm;
                    _phaseTicks = 0;
                }
                LastError = null;
                break;

            case AxisCommandKind.Halt:
                _halt = true;
                Rx.TargetVelocity = 0;
                _phase = Phase.Idle;
                _jogPp = false;
                break;

            case AxisCommandKind.QuickStop:
                _quickStop = true;
                _enableWanted = false;
                _phase = Phase.Idle;
                _jogPp = false;
                break;

            case AxisCommandKind.Home:
                if (!RequireEnabled()) break;
                Rx.ModesOfOperation = (sbyte)OperationMode.Homing;
                RequestProfileSyncIfNeeded();
                _halt = false;
                _jogPp = false;
                _phase = Phase.HomingArm;
                _phaseTicks = 0;
                LastError = null;
                break;
        }
    }

    private bool RequireEnabled()
    {
        if (_enableWanted && State == Cia402State.OperationEnabled) return true;
        LastError = "axis not enabled";
        return false;
    }

    private void Fail(string message)
    {
        LastError = message;
        _phase = Phase.Idle;
        _jogPp = false;
    }
}
