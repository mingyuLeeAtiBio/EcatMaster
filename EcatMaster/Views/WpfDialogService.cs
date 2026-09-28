using System.Windows;
using EcatMaster.Core.EtherCat;
using EcatMaster.ViewModels;

namespace EcatMaster.Views;

/// <summary><see cref="IDialogService"/> 의 WPF 구현. 대화상자 소유자는 "지금 활성인 창"(모달리스 IO 창 위에서 열어도 그 위에 뜨게).</summary>
public sealed class WpfDialogService : IDialogService
{
    private readonly Dictionary<IoGroupViewModel, IoGroupWindow> _ioWindows = new();

    private static Window? ActiveOwner()
        => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public string? Prompt(string title, string label, string initial) => PromptDialog.Show(ActiveOwner(), title, label, initial);

    public bool Confirm(string text, string title)
    {
        var owner = ActiveOwner();
        var r = owner is null
            ? MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(owner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        return r == MessageBoxResult.OK;
    }

    public InterlockRule? EditInterlock(InterlockEditorViewModel editor)
    {
        var dlg = new InterlockEditorWindow(editor, ActiveOwner());
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public IReadOnlyList<IoAliasChange>? EditIoAliases(IoGroupViewModel group)
    {
        var dlg = new IoAliasEditorWindow(group, ActiveOwner());
        if (dlg.ShowDialog() != true) return null;
        return dlg.Changes.Select(c => new IoAliasChange(c.Module, c.Bit, c.NewName)).ToList();
    }

    public AdapterProbe? PickNic(IReadOnlyList<AdapterProbe> candidates, string? preferredAdapterName)
    {
        var dlg = new NicPickerWindow(candidates, Application.Current?.MainWindow, preferredAdapterName);
        return dlg.ShowDialog() == true ? dlg.Selected : null;
    }

    public void ShowIoGroup(IoGroupViewModel group)
    {
        if (_ioWindows.TryGetValue(group, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var owner = Application.Current?.MainWindow;
        if (owner is null) return;
        var win = new IoGroupWindow(group, owner);
        win.Closed += (_, _) => _ioWindows.Remove(group);
        _ioWindows[group] = win;
        win.Show();   // 모달리스
    }

    public void CloseIoGroupsExcept(IEnumerable<IoGroupViewModel> keep)
    {
        var set = keep.ToHashSet();
        foreach (var (group, win) in _ioWindows.ToArray())
            if (!set.Contains(group)) win.Close();
    }
}
