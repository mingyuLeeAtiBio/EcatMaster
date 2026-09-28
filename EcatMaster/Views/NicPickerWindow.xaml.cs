using System.Windows;
using System.Windows.Input;
using EcatMaster.Core.EtherCat;

namespace EcatMaster.Views;

/// <summary>여러 NIC 에서 EtherCAT 슬레이브가 발견됐을 때 하나를 고르는 대화상자 (모달).</summary>
public partial class NicPickerWindow : Window
{
    public AdapterProbe? Selected { get; private set; }

    public NicPickerWindow(IReadOnlyList<AdapterProbe> candidates, Window? owner, string? preferredAdapterName)
    {
        InitializeComponent();
        Owner = owner;
        List.ItemsSource = candidates;
        List.SelectedItem = candidates.FirstOrDefault(c => c.Adapter.Name == preferredAdapterName) ?? candidates.FirstOrDefault();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (List.SelectedItem is not null) Accept(); }

    private void Accept()
    {
        if (List.SelectedItem is not AdapterProbe p) return;
        Selected = p;
        DialogResult = true;
    }
}
