using System.ComponentModel;
using System.Windows;
using EcatMaster.ViewModels;

namespace EcatMaster.Views;

/// <summary>
/// 코드 비하인드는 최소만: 뷰모델 생성(다이얼로그 서비스 주입)과 종료 시 마스터 정리.
/// 조그 누름/뗌, 체크박스 한 번 클릭, 행 더블클릭, 로그 자동 스크롤, Loaded 자동 연결은 Infrastructure/Behaviors 의 attached property 로 XAML 에서 연결한다.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(new WpfDialogService());
        DataContext = _vm;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;      // 마스터를 정리(INIT 전환, 소켓 닫기)한 뒤에 닫는다
        _closing = true;
        try { await _vm.ShutdownAsync(); }
        finally { Application.Current.Shutdown(); }
    }
}
