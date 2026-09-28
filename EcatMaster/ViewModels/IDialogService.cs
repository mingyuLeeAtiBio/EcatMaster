using EcatMaster.Core.EtherCat;

namespace EcatMaster.ViewModels;

/// <summary>
/// 뷰모델이 창을 직접 만들지 않도록 대화상자/보조 창을 추상화한다. WPF 구현은 Views/WpfDialogService.
/// 다른 솔루션에 붙일 때는 그쪽 다이얼로그 서비스로 어댑터를 만들어 <see cref="MainViewModel"/> 에 넘긴다.
/// </summary>
public interface IDialogService
{
    /// <summary>한 줄 입력. 취소면 null.</summary>
    string? Prompt(string title, string label, string initial);

    /// <summary>OK/취소 확인 (기본 취소). OK 면 true.</summary>
    bool Confirm(string text, string title);

    /// <summary>인터록 규칙 편집기(모달). 저장하면 규칙, 취소면 null.</summary>
    InterlockRule? EditInterlock(InterlockEditorViewModel editor);

    /// <summary>IO 그룹 별칭 편집기(모달). 저장하면 바뀐 항목들(비어 있을 수 있음), 취소면 null.</summary>
    IReadOnlyList<IoAliasChange>? EditIoAliases(IoGroupViewModel group);

    /// <summary>슬레이브가 있는 NIC 가 여러 개일 때 선택. 취소면 null.</summary>
    AdapterProbe? PickNic(IReadOnlyList<AdapterProbe> candidates, string? preferredAdapterName);

    /// <summary>IO 그룹을 모달리스 창으로. 이미 열려 있으면 앞으로 가져온다.</summary>
    void ShowIoGroup(IoGroupViewModel group);

    /// <summary>keep 에 없는 그룹의 모달리스 창을 닫는다 (해제 시).</summary>
    void CloseIoGroupsExcept(IEnumerable<IoGroupViewModel> keep);
}

/// <summary>별칭 편집기 결과 한 건.</summary>
public sealed record IoAliasChange(IoModuleViewModel Module, IoBitViewModel Bit, string NewName);

/// <summary>대화상자가 없는 환경(단위 테스트 등)용: 모두 취소/무시.</summary>
public sealed class NullDialogService : IDialogService
{
    public string? Prompt(string title, string label, string initial) => null;
    public bool Confirm(string text, string title) => false;
    public InterlockRule? EditInterlock(InterlockEditorViewModel editor) => null;
    public IReadOnlyList<IoAliasChange>? EditIoAliases(IoGroupViewModel group) => null;
    public AdapterProbe? PickNic(IReadOnlyList<AdapterProbe> candidates, string? preferredAdapterName) => null;
    public void ShowIoGroup(IoGroupViewModel group) { }
    public void CloseIoGroupsExcept(IEnumerable<IoGroupViewModel> keep) { }
}
