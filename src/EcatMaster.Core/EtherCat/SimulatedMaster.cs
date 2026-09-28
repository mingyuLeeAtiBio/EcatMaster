using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EcatMaster.Core.EtherCat;

/// <summary>
/// 하드웨어 없이 API/시퀀스를 개발·시험하기 위한 가상 마스터.
/// 축당 <see cref="SimDrive"/> 가 CiA402 상태기계와 PP/PV/Homing 을 단순 모델로 흉내 낸다.
/// Windows 에서도 동작하므로 기존 WPF 앱과 함께 개발할 때 쓴다.
/// </summary>
public sealed class SimulatedMaster : MasterBase
{
    private readonly ILogger<SimulatedMaster> _log;
    private readonly List<SimDrive> _drives = new();
    private Thread? _thread;
    private volatile bool _running;
    private long _cycleCount;
    private long _maxJitterNs;

    public SimulatedMaster(IOptions<MasterOptions> options, ILogger<SimulatedMaster> log) : base(options)
    {
        _log = log;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        for (int i = 1; i <= Options.AxisCount; i++)
        {
            // Axes[].Profile 로 "VPH-HD" 를 지정하면 PV 없는 드라이브(조그 = PP 상대이동, 가감속 SDO 동기)를 흉내 낸다
            var profile = DriveProfileRegistry.FindByName(Options.Axes.FirstOrDefault(a => a.Id == i)?.Profile) ?? DriveProfileRegistry.Azd;
            var axis = new Axis(i, AxisName(i), i, Options.Defaults, Options.Units, profile);
            ApplyAxisOptions(axis);
            if (axis.Profile.NeedsPulsesPerRev && axis.Mechanics.BaseStepsPerRev <= 0) axis.Mechanics.BaseStepsPerRev = DriveMechanics.VphDefaultPulsesPerRev;
            axis.Mechanics.ReadFromDrive = true;   // 가상 드라이브의 "저장값" (A=1, B=1)
            AxisList.Add(axis);
            _drives.Add(new SimDrive());
        }

        int ioId = 0;
        foreach (var io in Options.SimulatedIoModules)
        {
            ioId++;
            string ioName = string.IsNullOrWhiteSpace(io.Name) ? $"IO{ioId}" : io.Name;
            // 이름이 같은 IoModules 항목이 있으면 비트 이름을 가져온다 (실기 구성과 같은 라벨로 시험)
            var named = Options.IoModules.FirstOrDefault(o => string.Equals(o.Name, ioName, StringComparison.OrdinalIgnoreCase));
            int inBits = Math.Clamp(io.InputBits, 0, 64), outBits = Math.Clamp(io.OutputBits, 0, 64);
            var m = new IoModule(ioId, ioName, 1000 + ioId, inBits, outBits,
                IoModuleOptions.MergeNames(inBits, named?.InputNames, named?.InputAliases, "IN"),
                IoModuleOptions.MergeNames(outBits, named?.OutputNames, named?.OutputAliases, "OUT")) { Online = true };
            IoModuleList.Add(m);
        }
        BuildInterlocks(_log);

        _running = true;
        _thread = new Thread(Loop) { Name = "ecat-sim", IsBackground = true };
        _thread.Start();
        _log.LogWarning("EtherCAT master running in SIMULATED mode with {N} virtual axes, {M} virtual IO modules", AxisList.Count, IoModuleList.Count);
        return Task.CompletedTask;
    }

    /// <summary>시뮬레이션에서는 입력을 강제로 바꿔 인터록을 시험할 수 있다.</summary>
    public override bool SupportsInputForcing => true;

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        ResetCycleTracking();
        return Task.CompletedTask;
    }

    private void Loop()
    {
        double dt = Options.CycleTimeUs / 1_000_000.0;
        long periodTicks = (long)(Options.CycleTimeUs * (Stopwatch.Frequency / 1_000_000.0));
        long next = Stopwatch.GetTimestamp() + periodTicks;

        while (_running)
        {
            // Windows 타이머 해상도(~1ms)를 고려한 단순 대기
            long now;
            while ((now = Stopwatch.GetTimestamp()) < next)
            {
                long remainUs = (next - now) * 1_000_000 / Stopwatch.Frequency;
                if (remainUs > 2000) Thread.Sleep(1); else Thread.SpinWait(50);
            }
            long jitterNs = (now - next) * 1_000_000_000 / Stopwatch.Frequency;
            if (_cycleCount >= Options.JitterWarmupCycles && jitterNs > _maxJitterNs) _maxJitterNs = jitterNs;
            next += periodTicks;
            if (now > next) next = now + periodTicks;

            // 가상 IO: 실제 입력은 없으므로 0, 강제 비트만 반영
            for (int i = 0; i < IoModuleList.Count; i++) IoModuleList[i].UpdateInputs(0);

            for (int i = 0; i < AxisList.Count; i++)
            {
                var axis = AxisList[i];
                axis.Online = true;
                if (axis.ProfileSyncPending) axis.MarkProfileSynced(axis.Rx.ProfileAcceleration, axis.Rx.ProfileDeceleration);   // 가상 SDO 즉시 완료
                _drives[i].Step(in axis.Rx, ref axis.Tx, dt);
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            RtMarkCycle(nowTicks);
            RtEvaluate(nowTicks, true);

            for (int i = 0; i < AxisList.Count; i++) AxisList[i].Tick();

            _cycleCount++;
            if (_cycleCount % 50 == 0) DrainEvents(_log);
        }
    }

    public override void ResetStatistics() => Interlocked.Exchange(ref _maxJitterNs, 0);

    public override SdoResult SdoRead(int slave, ushort index, byte subIndex, int size) =>
        SdoResult.Fail("SDO is not available in simulated mode");

    public override SdoResult SdoWrite(int slave, ushort index, byte subIndex, int size, uint value) =>
        SdoResult.Fail("SDO is not available in simulated mode");

    public override DriveSettingsResult ReadDriveSettings(int axisId)
    {
        var axis = RequireAxis(axisId);
        return new DriveSettingsResult(true, axis.GetDriveSettings(), ["(시뮬레이션) 가상 드라이브 값 유지"]);
    }

    public override DriveSettingsResult WriteDriveSettings(int axisId, DriveSettingsWrite req)
    {
        var axis = RequireAxis(axisId);
        var m = axis.Mechanics;
        var msgs = new List<string> { "(시뮬레이션) 드라이브 쓰기 없이 메모리에만 반영" };
        if (req.ElectronicGearA is > 0) { m.ElectronicGearA = req.ElectronicGearA.Value; msgs.Add($"6091h:01 {axis.Profile.GearALabel} ← {m.ElectronicGearA}"); }
        if (req.ElectronicGearB is > 0) { m.ElectronicGearB = req.ElectronicGearB.Value; msgs.Add($"6091h:02 {axis.Profile.GearBLabel} ← {m.ElectronicGearB}"); }
        if (req.GearRatioRaw is not null && axis.Profile.SupportsGearRatioObject) { m.GearRatioRaw = req.GearRatioRaw.Value; msgs.Add($"47F1h 기어비 ← {m.GearRatioRaw} (×0.01)"); }
        ApplyLocalSettings(axis, req, msgs);
        if (req.RunConfiguration) msgs.Add("Configuration (40C6h) — 시뮬레이션에서는 즉시 반영");
        if (req.SaveToNvm) msgs.Add("Write batch NV memory (40C9h) — 시뮬레이션에서는 무시");
        return new DriveSettingsResult(true, axis.GetDriveSettings(), msgs);
    }

    public override MasterStatus GetStatus()
    {
        var slaves = _drives.Select((d, i) => new SlaveInfo(i + 1, "AZD-CED (simulated)", Options.VendorId, 0,
            "OP", 0, "No error", AzdRxPdo.Size, AzdTxPdo.Size)).ToList();

        return new MasterStatus("Simulated", _running, _running, "(none)", _drives.Count, AxisList.Count,
            IoModuleList.Count, ActiveInterlockCount,
            Options.CycleTimeUs, Interlocked.Read(ref _cycleCount), CycleAlive, CyclesPerSecond, ActualCycleTimeUs, 0, 0, 0, 0,
            Interlocked.Read(ref _maxJitterNs) / 1000.0, null, slaves);
    }

    /// <summary>가상 CiA402 드라이브. 가감속은 무시하고 프로파일 속도로 등속 이동한다.</summary>
    private sealed class SimDrive
    {
        private Cia402State _state = Cia402State.SwitchOnDisabled;
        private double _pos;
        private double _vel;
        private int _target;
        private bool _moving;
        private bool _prevBit4;
        private bool _prevFaultReset;
        private bool _setpointAck;
        private bool _homingAttained;

        public void Step(in AzdRxPdo rx, ref AzdTxPdo tx, double dt)
        {
            ushort cw = rx.ControlWord;
            bool bit4 = (cw & Cia402.CwBit4) != 0;
            bool halt = (cw & Cia402.CwHalt) != 0;
            bool faultReset = (cw & Cia402.CwFaultReset) != 0;
            int cmd = cw & 0x0F;

            // ---- 상태 전이 ----
            switch (_state)
            {
                case Cia402State.Fault:
                    if (faultReset && !_prevFaultReset) _state = Cia402State.SwitchOnDisabled;
                    break;
                case Cia402State.SwitchOnDisabled:
                    if (cmd == Cia402.CmdShutdown) _state = Cia402State.ReadyToSwitchOn;
                    break;
                case Cia402State.ReadyToSwitchOn:
                    if (cmd == Cia402.CmdSwitchOn) _state = Cia402State.SwitchedOn;
                    else if (cmd == Cia402.CmdEnableOperation) _state = Cia402State.OperationEnabled;
                    else if ((cw & Cia402.CwEnableVoltage) == 0) _state = Cia402State.SwitchOnDisabled;
                    break;
                case Cia402State.SwitchedOn:
                    if (cmd == Cia402.CmdEnableOperation) _state = Cia402State.OperationEnabled;
                    else if (cmd == Cia402.CmdShutdown) _state = Cia402State.ReadyToSwitchOn;
                    else if ((cw & Cia402.CwEnableVoltage) == 0) _state = Cia402State.SwitchOnDisabled;
                    break;
                case Cia402State.OperationEnabled:
                    if ((cw & Cia402.CwEnableVoltage) == 0) { _state = Cia402State.SwitchOnDisabled; StopNow(); }
                    else if ((cw & Cia402.CwQuickStop) == 0) _state = Cia402State.QuickStopActive;
                    else if (cmd == Cia402.CmdSwitchOn) { _state = Cia402State.SwitchedOn; StopNow(); }
                    else if (cmd == Cia402.CmdShutdown) { _state = Cia402State.ReadyToSwitchOn; StopNow(); }
                    break;
                case Cia402State.QuickStopActive:
                    // 605Ah = 2: 정지 후 Switch on disabled
                    StopNow();
                    _state = Cia402State.SwitchOnDisabled;
                    break;
            }
            _prevFaultReset = faultReset;

            // ---- 모션 ----
            var mode = (OperationMode)rx.ModesOfOperation;
            if (_state == Cia402State.OperationEnabled)
            {
                switch (mode)
                {
                    case OperationMode.ProfilePosition:
                        if (bit4 && !_prevBit4)
                        {
                            _target = (cw & Cia402.CwBit6) != 0 ? (int)Math.Round(_pos) + rx.TargetPosition : rx.TargetPosition;
                            _moving = true;
                            _setpointAck = true;
                        }
                        if (!bit4) _setpointAck = false;
                        // Halt = 진행 중 위치결정 중단(605Dh=1: 감속 정지 후 유지). 실기(AZD·VPH-HD)와 같이 정지하면 Target reached 가 선다.
                        if (halt && _moving) { _moving = false; _vel = 0; }
                        if (_moving)
                        {
                            double v = Math.Max(1, rx.ProfileVelocity);
                            double step = v * dt;
                            double diff = _target - _pos;
                            if (Math.Abs(diff) <= step) { _pos = _target; _vel = 0; _moving = false; }
                            else { _pos += Math.Sign(diff) * step; _vel = Math.Sign(diff) * v; }
                        }
                        else _vel = 0;
                        break;

                    case OperationMode.ProfileVelocity:
                        _vel = halt ? 0 : rx.TargetVelocity;
                        _pos += _vel * dt;
                        _moving = _vel != 0;
                        break;

                    case OperationMode.Homing:
                        if (bit4 && !_prevBit4) { _moving = true; _homingAttained = false; }
                        if (_moving && !halt)
                        {
                            double v = Math.Max(1, rx.ProfileVelocity);
                            double step = v * dt;
                            if (Math.Abs(_pos) <= step) { _pos = 0; _vel = 0; _moving = false; _homingAttained = true; }
                            else { _pos -= Math.Sign(_pos) * step; _vel = -Math.Sign(_pos) * v; }
                        }
                        else _vel = 0;
                        if (!bit4) _homingAttained = false;
                        break;
                }
            }
            else
            {
                _setpointAck = false;
                _homingAttained = false;
            }
            _prevBit4 = bit4;

            // ---- 상태 워드 ----
            ushort sw = _state switch
            {
                Cia402State.NotReadyToSwitchOn => 0x0000,
                Cia402State.SwitchOnDisabled => 0x0040,
                Cia402State.ReadyToSwitchOn => 0x0021,
                Cia402State.SwitchedOn => 0x0023,
                Cia402State.OperationEnabled => 0x0027,
                Cia402State.QuickStopActive => 0x0007,
                Cia402State.FaultReactionActive => 0x000F,
                Cia402State.Fault => 0x0008,
                _ => 0,
            };
            sw |= Cia402.SwRemote;
            if (!_moving) sw |= Cia402.SwTargetReached;
            switch (mode)
            {
                case OperationMode.ProfilePosition: if (_setpointAck) sw |= Cia402.SwBit12; break;
                case OperationMode.ProfileVelocity: if (_vel == 0) sw |= Cia402.SwBit12; break;
                case OperationMode.Homing: if (_homingAttained) sw |= Cia402.SwBit12; break;
            }

            tx.StatusWord = sw;
            tx.ModesOfOperationDisplay = rx.ModesOfOperation;
            tx.PositionActual = (int)Math.Round(_pos);
            tx.VelocityActual = (int)Math.Round(_vel);
            tx.ErrorCode = 0;
            tx.DigitalInputs = 0;
        }

        private void StopNow()
        {
            _moving = false;
            _vel = 0;
        }
    }
}
