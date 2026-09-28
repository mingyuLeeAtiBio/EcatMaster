using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EcatMaster.Infrastructure.Behaviors;

/// <summary>
/// 조그 버튼: 누르는 동안 StartCommand, 떼거나(버튼 밖에서 떼어도) 캡처를 잃으면 StopCommand.
/// <code>&lt;Button bh:Jog.StartCommand="{Binding JogPlusCommand}" bh:Jog.StopCommand="{Binding StopCommand}"/&gt;</code>
/// </summary>
public static class Jog
{
    public static readonly DependencyProperty StartCommandProperty = DependencyProperty.RegisterAttached(
        "StartCommand", typeof(ICommand), typeof(Jog), new PropertyMetadata(null, OnStartCommandChanged));
    public static readonly DependencyProperty StopCommandProperty = DependencyProperty.RegisterAttached(
        "StopCommand", typeof(ICommand), typeof(Jog), new PropertyMetadata(null));
    private static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(bool), typeof(Jog), new PropertyMetadata(false));

    public static ICommand? GetStartCommand(DependencyObject d) => (ICommand?)d.GetValue(StartCommandProperty);
    public static void SetStartCommand(DependencyObject d, ICommand? v) => d.SetValue(StartCommandProperty, v);
    public static ICommand? GetStopCommand(DependencyObject d) => (ICommand?)d.GetValue(StopCommandProperty);
    public static void SetStopCommand(DependencyObject d, ICommand? v) => d.SetValue(StopCommandProperty, v);

    private static void OnStartCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase b) return;
        b.PreviewMouseLeftButtonDown -= Down; b.PreviewMouseLeftButtonUp -= Up; b.LostMouseCapture -= Lost;
        if (e.NewValue is null) return;
        b.PreviewMouseLeftButtonDown += Down; b.PreviewMouseLeftButtonUp += Up; b.LostMouseCapture += Lost;
    }

    private static void Down(object sender, MouseButtonEventArgs e)
    {
        var b = (ButtonBase)sender;
        if ((bool)b.GetValue(ActiveProperty)) return;
        var cmd = GetStartCommand(b);
        if (cmd is null || !cmd.CanExecute(null)) return;
        b.SetValue(ActiveProperty, true);
        b.CaptureMouse();        // 버튼 밖에서 떼어도 MouseUp/LostMouseCapture 를 받기 위해
        cmd.Execute(null);
        e.Handled = true;        // Click 으로 중복 실행되지 않게
    }

    private static void Up(object sender, MouseButtonEventArgs e) { End((ButtonBase)sender); e.Handled = true; }
    private static void Lost(object sender, MouseEventArgs e) => End((ButtonBase)sender);   // Alt+Tab, 창 비활성화 등에도 반드시 정지

    private static void End(ButtonBase b)
    {
        if (!(bool)b.GetValue(ActiveProperty)) return;
        b.SetValue(ActiveProperty, false);
        if (b.IsMouseCaptured) b.ReleaseMouseCapture();
        var stop = GetStopCommand(b);
        if (stop is not null && stop.CanExecute(null)) stop.Execute(null);
    }
}

/// <summary>DataGrid 보조: 템플릿 열의 체크박스를 한 번 클릭으로 토글, 행 더블클릭 → 커맨드(행 항목이 파라미터).</summary>
public static class DataGridBehaviors
{
    public static readonly DependencyProperty SingleClickCheckBoxProperty = DependencyProperty.RegisterAttached(
        "SingleClickCheckBox", typeof(bool), typeof(DataGridBehaviors), new PropertyMetadata(false, OnSingleClickChanged));
    public static readonly DependencyProperty DoubleClickCommandProperty = DependencyProperty.RegisterAttached(
        "DoubleClickCommand", typeof(ICommand), typeof(DataGridBehaviors), new PropertyMetadata(null, OnDoubleClickChanged));

    public static bool GetSingleClickCheckBox(DependencyObject d) => (bool)d.GetValue(SingleClickCheckBoxProperty);
    public static void SetSingleClickCheckBox(DependencyObject d, bool v) => d.SetValue(SingleClickCheckBoxProperty, v);
    public static ICommand? GetDoubleClickCommand(DependencyObject d) => (ICommand?)d.GetValue(DoubleClickCommandProperty);
    public static void SetDoubleClickCommand(DependencyObject d, ICommand? v) => d.SetValue(DoubleClickCommandProperty, v);

    private static void OnSingleClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid g) return;
        g.PreviewMouseLeftButtonDown -= GridDown;
        if ((bool)e.NewValue) g.PreviewMouseLeftButtonDown += GridDown;
    }

    /// <summary>DataGrid 가 첫 클릭을 셀 선택에 써 버려 체크박스가 두 번 클릭돼야 하는 문제 → 첫 클릭에서 바로 토글.</summary>
    private static void GridDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        var cell = FindParent<DataGridCell>(src);
        if (cell is not { Column: DataGridTemplateColumn }) return;
        var cb = FindChild<CheckBox>(cell);
        if (cb is null || !cb.IsEnabled) return;
        cb.IsChecked = !(cb.IsChecked ?? false);   // 바인딩 setter 로 흐른다
        e.Handled = true;
    }

    private static void OnDoubleClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid g) return;
        g.MouseDoubleClick -= GridDoubleClick;
        if (e.NewValue is not null) g.MouseDoubleClick += GridDoubleClick;
    }

    private static void GridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var g = (DataGrid)sender;
        if (e.OriginalSource is not DependencyObject src) return;
        var row = FindParent<DataGridRow>(src);
        var cell = FindParent<DataGridCell>(src);
        if (row is null || cell is { Column: DataGridTemplateColumn }) return;   // 체크박스 열은 제외
        var cmd = GetDoubleClickCommand(g);
        if (cmd is null || !cmd.CanExecute(row.Item)) return;
        g.SelectedItem = row.Item;
        cmd.Execute(row.Item);
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var d = child; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            var found = FindChild<T>(c);
            if (found is not null) return found;
        }
        return null;
    }
}

/// <summary>ListBox 보조: 항목이 추가되면 맨 아래로 스크롤 (로그).</summary>
public static class ListBoxBehaviors
{
    public static readonly DependencyProperty AutoScrollToEndProperty = DependencyProperty.RegisterAttached(
        "AutoScrollToEnd", typeof(bool), typeof(ListBoxBehaviors), new PropertyMetadata(false, OnChanged));

    public static bool GetAutoScrollToEnd(DependencyObject d) => (bool)d.GetValue(AutoScrollToEndProperty);
    public static void SetAutoScrollToEnd(DependencyObject d, bool v) => d.SetValue(AutoScrollToEndProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox lb) return;
        ((INotifyCollectionChanged)lb.Items).CollectionChanged -= (s, a) => { };
        if (!(bool)e.NewValue) return;
        ((INotifyCollectionChanged)lb.Items).CollectionChanged += (_, a) =>
        {
            if (a.Action != NotifyCollectionChangedAction.Add) return;
            // CollectionChanged 안에서 바로 ScrollIntoView 를 부르면 ItemCollection 이 아직 동기화 전이라 예외 → 디스패처 큐로 미룬다
            lb.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                int n = lb.Items.Count;
                if (n > 0) lb.ScrollIntoView(lb.Items[n - 1]);
            });
        };
    }
}

/// <summary>Window 보조: Loaded 에서 커맨드 실행 (자동 연결 등).</summary>
public static class WindowBehaviors
{
    public static readonly DependencyProperty LoadedCommandProperty = DependencyProperty.RegisterAttached(
        "LoadedCommand", typeof(ICommand), typeof(WindowBehaviors), new PropertyMetadata(null, OnChanged));

    public static ICommand? GetLoadedCommand(DependencyObject d) => (ICommand?)d.GetValue(LoadedCommandProperty);
    public static void SetLoadedCommand(DependencyObject d, ICommand? v) => d.SetValue(LoadedCommandProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnLoaded;
        if (e.NewValue is not null) fe.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var cmd = GetLoadedCommand((DependencyObject)sender);
        if (cmd is not null && cmd.CanExecute(null)) cmd.Execute(null);
    }
}
