using System.Windows;
using EcatMaster.ViewModels;

namespace EcatMaster.Views;

/// <summary>인터록 규칙 추가/편집 대화상자 (모달). 저장하면 <see cref="Result"/> 에 규칙이 들어 있다.</summary>
public partial class InterlockEditorWindow : Window
{
    public InterlockEditorViewModel Vm { get; }
    public Core.EtherCat.InterlockRule? Result { get; private set; }

    public InterlockEditorWindow(InterlockEditorViewModel vm, Window? owner)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
        Owner = owner;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanSave) return;
        Result = Vm.BuildRule();
        DialogResult = true;
    }
}
