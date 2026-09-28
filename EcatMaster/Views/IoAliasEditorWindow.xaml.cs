using System.Windows;
using System.Windows.Controls;
using EcatMaster.ViewModels;

namespace EcatMaster.Views;

/// <summary>IO 그룹(모듈 1~2개)의 모든 비트 별칭을 표에서 한 번에 고치는 대화상자. 결과는 <see cref="Changes"/> 로 돌려준다.</summary>
public partial class IoAliasEditorWindow : Window
{
    private readonly List<IoAliasRow> _rows;

    /// <summary>저장을 누른 경우 바뀐 행들 (비트, 새 이름). 취소면 비어 있음.</summary>
    public List<(IoModuleViewModel Module, IoBitViewModel Bit, string NewName)> Changes { get; } = new();

    public IoAliasEditorWindow(IoGroupViewModel group, Window? owner)
    {
        InitializeComponent();
        Owner = owner;
        Title = $"IO 별칭 편집 · {group.Name}";
        HeaderText.Text = group.Subtitle;
        _rows = group.Modules
            .SelectMany(m => m.Inputs.Select(b => new IoAliasRow(m, b)).Concat(m.Outputs.Select(b => new IoAliasRow(m, b))))
            .ToList();
        Grid.ItemsSource = _rows;
        foreach (var r in _rows) r.PropertyChanged += (_, _) => UpdateChangedText();
        UpdateChangedText();
    }

    private void UpdateChangedText()
    {
        int n = _rows.Count(r => r.Changed);
        ChangedText.Text = n == 0 ? "" : $"변경 {n}개";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var r in _rows)
        {
            if (!r.Changed) continue;
            var name = r.NewName.Trim();
            if (name.Length == 0) name = r.DefaultName;     // 비우면 기본 이름으로
            if (name == r.CurrentName) continue;
            Changes.Add((r.Module, r.Bit, name));
        }
        DialogResult = true;
    }
}
