using System.Windows;
using EcatMaster.ViewModels;

namespace EcatMaster.Views;

/// <summary>IO 그룹(보통 DI/DO 한 쌍)을 별도 창(모달리스)으로 보여 준다. 여러 그룹을 동시에 띄워 놓고 볼 수 있다.</summary>
public partial class IoGroupWindow : Window
{
    public IoGroupViewModel Group { get; }

    public IoGroupWindow(IoGroupViewModel group, Window owner)
    {
        InitializeComponent();
        Group = group;
        Owner = owner;
        DataContext = group;
        Title = $"IO · {group.Name}";
    }
}
