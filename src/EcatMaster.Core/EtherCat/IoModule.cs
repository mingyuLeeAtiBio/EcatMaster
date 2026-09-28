namespace EcatMaster.Core.EtherCat;

/// <summary>설정으로 IO 모듈을 덧붙이거나 이름을 붙일 때 (appsettings "Master:IoModules"). 없으면 자동 분류·자동 이름.</summary>
public sealed class IoModuleOptions
{
    /// <summary>슬레이브 위치 (1-based)</summary>
    public int Slave { get; set; }

    /// <summary>API/GUI 에서 부르는 이름 (예: "DI1", "DoorIO"). 비우면 "IO{n}"</summary>
    public string? Name { get; set; }

    /// <summary>"Digital" (기본) | "Ignore" (이 슬레이브를 IO 로 다루지 않음, 예: 아날로그 터미널)</summary>
    public string Kind { get; set; } = "Digital";

    /// <summary>
    /// GUI 표시 그룹. 같은 그룹의 모듈은 IO 탭 하나에 나란히 표시된다 (예: 스테이션의 DI 와 DO 를 한 쌍으로).
    /// 비우면 이름의 숫자 접미가 같은 입력전용/출력전용 모듈(DI1+DO1)을 자동으로 묶는다. 제어 동작에는 영향 없음.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>(위치 배열) 입력 비트 이름. 0번부터 순서대로.</summary>
    public string[] InputNames { get; set; } = [];
    public string[] OutputNames { get; set; } = [];

    /// <summary>(희소 사전) 비트 번호 → 별칭. 예: { "0": "EStop", "5": "Clamp1" }. 배열보다 우선.</summary>
    public Dictionary<string, string> InputAliases { get; set; } = new();
    public Dictionary<string, string> OutputAliases { get; set; } = new();

    /// <summary>배열 + 사전을 합쳐 bits 길이의 이름 배열을 만든다. 빈 자리는 prefix+번호.</summary>
    public static string[] MergeNames(int bits, string[]? array, Dictionary<string, string>? aliases, string prefix)
    {
        var names = new string[bits];
        for (int i = 0; i < bits; i++)
        {
            string? n = null;
            if (aliases is not null && aliases.TryGetValue(i.ToString(), out var a) && !string.IsNullOrWhiteSpace(a)) n = a.Trim();
            else if (array is not null && i < array.Length && !string.IsNullOrWhiteSpace(array[i])) n = array[i].Trim();
            names[i] = n ?? $"{prefix}{i}";
        }
        return names;
    }
}

/// <summary>시뮬레이션 모드에서 만들 가상 IO 모듈.</summary>
public sealed class SimulatedIoModuleOptions
{
    public string Name { get; set; } = "DI1";
    public int InputBits { get; set; } = 16;
    public int OutputBits { get; set; }
}

public sealed record IoModuleStatus(
    int Id,
    string Name,
    int Slave,
    bool Online,
    int InputBits,
    int OutputBits,
    ulong Inputs,
    ulong Outputs,
    bool[] InputStates,
    bool[] OutputStates,
    string[] InputNames,
    string[] OutputNames,
    bool[] ForcedInputs);

/// <summary>
/// 디지털 IO 터미널 하나 (EL1889 16DI, EL2889 16DO, EL1008 8DI …). 최대 64 비트.
/// 입력은 실시간 스레드가 매 사이클 스냅샷을 쓰고, 출력은 API/GUI/상태출력 규칙이 원자적으로 비트를 바꾸면 실시간 스레드가 PDO 로 내보낸다.
/// </summary>
public sealed class IoModule
{
    private long _inputs;          // RT 가 쓰고 API 가 읽음
    private long _outputs;         // API/RT 가 Interlocked 로 쓰고 RT 가 PDO 로 내보냄
    private long _forceMask;       // 시뮬레이션/시험용: 강제 입력 비트 (마스터가 허용할 때만)
    private long _forceValue;

    public int Id { get; }

    /// <summary>모듈 별칭 (API 경로에 사용). 런타임 변경 가능.</summary>
    public string Name { get; set; }
    public int Slave { get; }
    public int InputBits { get; }
    public int OutputBits { get; }

    /// <summary>비트 별칭. <see cref="SetInputName"/>/<see cref="SetOutputName"/> 로 런타임 변경.</summary>
    public string[] InputNames { get; }
    public string[] OutputNames { get; }
    public bool Online { get; set; }

    public IoModule(int id, string name, int slave, int inputBits, int outputBits, string[]? inputNames = null, string[]? outputNames = null)
    {
        if (inputBits is < 0 or > 64 || outputBits is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(inputBits), "IoModule supports up to 64 bits per direction");
        Id = id;
        Name = name;
        Slave = slave;
        InputBits = inputBits;
        OutputBits = outputBits;
        InputNames = IoModuleOptions.MergeNames(inputBits, inputNames, null, "IN");
        OutputNames = IoModuleOptions.MergeNames(outputBits, outputNames, null, "OUT");
    }

    public void SetInputName(int bit, string name)
    {
        if (bit < 0 || bit >= InputBits) throw new ArgumentOutOfRangeException(nameof(bit));
        InputNames[bit] = string.IsNullOrWhiteSpace(name) ? $"IN{bit}" : name.Trim();
    }

    public void SetOutputName(int bit, string name)
    {
        if (bit < 0 || bit >= OutputBits) throw new ArgumentOutOfRangeException(nameof(bit));
        OutputNames[bit] = string.IsNullOrWhiteSpace(name) ? $"OUT{bit}" : name.Trim();
    }

    /// <summary>"3" 같은 번호 또는 별칭으로 입력 비트를 찾는다 (대소문자 무시).</summary>
    public bool TryResolveInput(string bitOrAlias, out int bit) => TryResolve(bitOrAlias, InputNames, InputBits, out bit);

    public bool TryResolveOutput(string bitOrAlias, out int bit) => TryResolve(bitOrAlias, OutputNames, OutputBits, out bit);

    private static bool TryResolve(string key, string[] names, int count, out int bit)
    {
        key = key.Trim();
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(names[i], key, StringComparison.OrdinalIgnoreCase)) { bit = i; return true; }
        }
        if (int.TryParse(key, out bit) && bit >= 0 && bit < count) return true;
        bit = -1;
        return false;
    }

    // ---- 읽기 (모든 스레드) ----

    public ulong Inputs => (ulong)Volatile.Read(ref _inputs);
    public ulong Outputs => (ulong)Volatile.Read(ref _outputs);

    public bool GetInput(int bit) => bit >= 0 && bit < InputBits && ((Inputs >> bit) & 1) != 0;
    public bool GetOutput(int bit) => bit >= 0 && bit < OutputBits && ((Outputs >> bit) & 1) != 0;

    // ---- 출력 쓰기 (API/GUI/RT, 원자적) ----

    public void SetOutput(int bit, bool on)
    {
        if (bit < 0 || bit >= OutputBits) throw new ArgumentOutOfRangeException(nameof(bit));
        long mask = 1L << bit;
        long cur, next;
        do
        {
            cur = Volatile.Read(ref _outputs);
            next = on ? cur | mask : cur & ~mask;
        } while (Interlocked.CompareExchange(ref _outputs, next, cur) != cur);
    }

    /// <summary>mask 에 해당하는 비트만 value 로 바꾼다.</summary>
    public void SetOutputs(ulong value, ulong mask = ulong.MaxValue)
    {
        ulong valid = OutputBits >= 64 ? ulong.MaxValue : (1UL << OutputBits) - 1;
        mask &= valid;
        long cur, next;
        do
        {
            cur = Volatile.Read(ref _outputs);
            next = (long)(((ulong)cur & ~mask) | (value & mask));
        } while (Interlocked.CompareExchange(ref _outputs, next, cur) != cur);
    }

    // ---- 강제 입력 (시뮬레이션/시험) ----

    public void ForceInput(int bit, bool? value)
    {
        if (bit < 0 || bit >= InputBits) throw new ArgumentOutOfRangeException(nameof(bit));
        long mask = 1L << bit;
        if (value is null)
        {
            long cur;
            do { cur = Volatile.Read(ref _forceMask); } while (Interlocked.CompareExchange(ref _forceMask, cur & ~mask, cur) != cur);
        }
        else
        {
            long cur;
            do { cur = Volatile.Read(ref _forceValue); } while (Interlocked.CompareExchange(ref _forceValue, value.Value ? cur | mask : cur & ~mask, cur) != cur);
            do { cur = Volatile.Read(ref _forceMask); } while (Interlocked.CompareExchange(ref _forceMask, cur | mask, cur) != cur);
        }
    }

    public void ClearForcedInputs()
    {
        Volatile.Write(ref _forceMask, 0);
        Volatile.Write(ref _forceValue, 0);
    }

    // ---- 실시간 스레드 ----

    /// <summary>실시간 스레드가 매 사이클 호출. raw 는 PDO 에서 읽은 값, 강제 비트가 있으면 덮어쓴다.</summary>
    public void UpdateInputs(ulong raw)
    {
        long fm = Volatile.Read(ref _forceMask);
        if (fm != 0)
        {
            long fv = Volatile.Read(ref _forceValue);
            raw = ((ulong)(long)raw & ~(ulong)fm) | ((ulong)fv & (ulong)fm);
        }
        Volatile.Write(ref _inputs, (long)raw);
    }

    public IoModuleStatus GetStatus()
    {
        ulong i = Inputs, o = Outputs, fm = (ulong)Volatile.Read(ref _forceMask);
        return new IoModuleStatus(Id, Name, Slave, Online, InputBits, OutputBits, i, o,
            Enumerable.Range(0, InputBits).Select(b => ((i >> b) & 1) != 0).ToArray(),
            Enumerable.Range(0, OutputBits).Select(b => ((o >> b) & 1) != 0).ToArray(),
            InputNames, OutputNames,
            Enumerable.Range(0, InputBits).Select(b => ((fm >> b) & 1) != 0).ToArray());
    }
}

/// <summary>IOmap 의 비트 오프셋을 고려한 읽기/쓰기 (SOEM Istartbit/Ostartbit).</summary>
public static unsafe class IoBits
{
    public static ulong Read(byte* p, int startBit, int nbits)
    {
        if (nbits <= 0 || p == null) return 0;
        ulong v = 0;
        int totalBits = startBit + nbits;
        int nbytes = (totalBits + 7) / 8;
        for (int i = 0; i < nbytes && i < 9; i++) v |= (ulong)p[i] << (8 * i);
        v >>= startBit;
        return nbits >= 64 ? v : v & ((1UL << nbits) - 1);
    }

    public static void Write(byte* p, int startBit, int nbits, ulong value)
    {
        if (nbits <= 0 || p == null) return;
        int totalBits = startBit + nbits;
        int nbytes = (totalBits + 7) / 8;
        ulong mask = (nbits >= 64 ? ulong.MaxValue : (1UL << nbits) - 1) << startBit;
        ulong cur = 0;
        for (int i = 0; i < nbytes && i < 8; i++) cur |= (ulong)p[i] << (8 * i);
        cur = (cur & ~mask) | ((value << startBit) & mask);
        for (int i = 0; i < nbytes && i < 8; i++) p[i] = (byte)(cur >> (8 * i));
    }
}
