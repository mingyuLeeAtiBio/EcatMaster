using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using EcatMaster.Core.EtherCat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EcatMaster.Infrastructure;

namespace EcatMaster.ViewModels;

/// <summary>
/// IO 탭 하나 = 논리 그룹 하나. 보통 스테이션의 입력 모듈과 출력 모듈 한 쌍이지만, 혼합형 모듈이나 짝 없는 모듈은 단독.
/// 구성(devices.json IoModules[].Group)이 있으면 그대로, 없으면 이름의 숫자 접미가 같은 DI/DO 를 자동으로 묶는다.
/// </summary>
public sealed class IoGroupViewModel : ObservableObject
{
    private bool _allOnline;

    public string Name { get; }
    public ObservableCollection<IoModuleViewModel> Modules { get; } = new();
    public bool IsPair => Modules.Count > 1;
    public bool AllOnline { get => _allOnline; private set => SetProperty(ref _allOnline, value); }
    public string Subtitle => string.Join("  ·  ", Modules.Select(m => m.Title));
    public ICommand EditAliasesCommand { get; }

    public IoGroupViewModel(string name, IEnumerable<IoModuleViewModel> modules, Action<IoGroupViewModel> editAliases)
    {
        Name = name;
        // 입력 모듈을 왼쪽에, 출력 모듈을 오른쪽에
        foreach (var m in modules.OrderBy(m => m.HasInputs ? 0 : 1).ThenBy(m => m.Module.Slave)) Modules.Add(m);
        EditAliasesCommand = new RelayCommand(() => editAliases(this));
        Update();
    }

    public void Update() => AllOnline = Modules.All(m => m.Online);

    private static readonly Regex TrailingNumber = new(@"^(?<prefix>.*?)(?<num>\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// 모듈 목록을 그룹으로 나눈다.
    /// 1) 구성에 Group 이 있으면 그 이름으로 묶는다.
    /// 2) 나머지는 이름 끝 숫자가 같은 "입력만 있는 모듈 1개 + 출력만 있는 모듈 1개" 쌍을 자동으로 묶는다 (DI1+DO1 → "DI1/DO1").
    /// 3) 그 외는 단독 그룹.
    /// </summary>
    public static List<IoGroupViewModel> Build(IEnumerable<IoModuleViewModel> modules, Func<IoModuleViewModel, string?> configuredGroup,
                                               Action<IoGroupViewModel> editAliases)
    {
        var all = modules.ToList();
        var groups = new List<IoGroupViewModel>();
        var used = new HashSet<IoModuleViewModel>();

        // 1) 명시 그룹 (구성 순서 유지)
        foreach (var m in all)
        {
            if (used.Contains(m)) continue;
            var g = configuredGroup(m)?.Trim();
            if (string.IsNullOrEmpty(g)) continue;
            var members = all.Where(x => !used.Contains(x) && string.Equals(configuredGroup(x)?.Trim(), g, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var x in members) used.Add(x);
            groups.Add(new IoGroupViewModel(g, members, editAliases));
        }

        // 2) 자동 쌍: 같은 숫자 접미, 입력전용 1 + 출력전용 1
        var bySuffix = all.Where(m => !used.Contains(m))
                          .Select(m => (m, match: TrailingNumber.Match(m.Name)))
                          .Where(t => t.match.Success)
                          .GroupBy(t => t.match.Groups["num"].Value);
        foreach (var grp in bySuffix)
        {
            var inputsOnly = grp.Where(t => t.m.HasInputs && !t.m.HasOutputs).ToList();
            var outputsOnly = grp.Where(t => t.m.HasOutputs && !t.m.HasInputs).ToList();
            if (inputsOnly.Count != 1 || outputsOnly.Count != 1) continue;
            var di = inputsOnly[0].m; var dO = outputsOnly[0].m;
            used.Add(di); used.Add(dO);
            groups.Add(new IoGroupViewModel($"{di.Name}/{dO.Name}", [di, dO], editAliases));
        }

        // 3) 단독
        foreach (var m in all.Where(m => !used.Contains(m)))
            groups.Add(new IoGroupViewModel(m.Name, [m], editAliases));

        // 표시 순서: 가장 앞 모듈의 슬레이브 번호
        return groups.OrderBy(g => g.Modules.Min(m => m.Module.Slave)).ToList();
    }
}

/// <summary>별칭 편집 창의 한 행 (비트 하나).</summary>
public sealed class IoAliasRow : ObservableObject
{
    private string _newName;

    public IoModuleViewModel Module { get; }
    public IoBitViewModel Bit { get; }
    public string ModuleName => Module.Name;
    public string Direction => Bit.IsOutput ? "출력" : "입력";
    public int Index => Bit.Index;
    public string CurrentName => Bit.Name;
    public string DefaultName => (Bit.IsOutput ? "OUT" : "IN") + Bit.Index;
    public string NewName { get => _newName; set { if (SetProperty(ref _newName, value)) OnPropertyChanged(nameof(Changed)); } }
    public bool Changed => !string.Equals(NewName.Trim(), CurrentName, StringComparison.Ordinal);

    public IoAliasRow(IoModuleViewModel module, IoBitViewModel bit)
    {
        Module = module;
        Bit = bit;
        _newName = bit.Name;
    }
}
